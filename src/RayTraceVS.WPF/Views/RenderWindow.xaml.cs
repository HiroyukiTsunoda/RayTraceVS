using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RayTraceVS.WPF.Services;
using RayTraceVS.WPF.Models;
using RayTraceVS.Interop;

namespace RayTraceVS.WPF.Views
{
    /// <summary>
    /// シーンパラメーターを保持するレコード（非同期レンダリング用）
    /// </summary>
    internal record SceneParams(
        SphereData[] Spheres,
        PlaneData[] Planes,
        BoxData[] Boxes,
        CameraData Camera,
        LightData[] Lights,
        MeshInstanceData[] MeshInstances,
        MeshCacheData[] MeshCaches,
        int SamplesPerPixel,
        int MaxBounces,
        int TraceRecursionDepth,
        float Exposure,
        int ToneMapOperator,
        float DenoiserStabilization,
        float ShadowStrength,
        float ShadowAbsorptionScale,
        bool EnableDenoiser,
        float Gamma,
        int PhotonDebugMode,
        float PhotonDebugScale,
        // P1 optimization settings
        float LightAttenuationConstant,
        float LightAttenuationLinear,
        float LightAttenuationQuadratic,
        int MaxShadowLights,
        float NRDBypassDistance,
        float NRDBypassBlendRange);

    public partial class RenderWindow : Window
    {
        private RenderService? renderService;
        private NodeGraph? nodeGraph;
        private SceneEvaluator? sceneEvaluator;
        private WriteableBitmap? renderBitmap;
        private byte[]? cachedSkyBuffer;
        
        private bool isRendering = false;
        private int photonDebugMode = 0;
        private float photonDebugScale = 1.0f;
        private readonly float[] photonDebugScaleOptions = new[] { 1.0f, 4.0f, 16.0f };
        
        // 非同期レンダリング用フィールド
        private bool _isRenderingInProgress = false;
        private SceneParams? _pendingSceneParams = null;
        private readonly object _renderLock = new object();
        
        // レンダリング時間計測用
        private readonly Stopwatch _renderStopwatch = new Stopwatch();
        private bool _isFirstRender = true;  // 最初のレンダリングフラグ
        
        /// <summary>
        /// レンダリング完了時に発行されるイベント
        /// 引数はレンダリングにかかった時間（ミリ秒）
        /// </summary>
        public event Action<double>? RenderCompleted;
        
        // レンダリング解像度（コンストラクタで設定）
        private readonly int RenderWidth;
        private readonly int RenderHeight;
        
        // テンポラルデノイズのための最低描画回数
        // 1回目: 履歴なし、2回目: テンポラル蓄積開始、3回目以降: 安定化
        private const int MinRenderPassesForTemporal = 1; // DEBUG: reduced from 5 to isolate crash

        public RenderWindow() : this(1920, 1080)
        {
        }
        
        public RenderWindow(int width, int height)
        {
            RenderWidth = width;
            RenderHeight = height;
            
            InitializeComponent();
            
            // 解像度に応じてウィンドウサイズを設定
            RenderImage.Width = width;
            RenderImage.Height = height;
            Title = $"レンダリング結果 - RayTraceVS ({width}x{height})";
            ResolutionText.Text = $"解像度: {width}x{height}";
        }

        public void SetNodeGraph(NodeGraph graph)
        {
            // 以前のノードグラフのイベント購読を解除
            if (nodeGraph != null)
            {
                nodeGraph.SceneChanged -= OnSceneChanged;
            }
            
            nodeGraph = graph;
            
            // 新しいノードグラフのシーン変更を監視
            if (nodeGraph != null)
            {
                nodeGraph.SceneChanged += OnSceneChanged;
            }
        }
        
        private void OnSceneChanged(object? sender, EventArgs e)
        {
            if (!isRendering || renderService == null || nodeGraph == null || sceneEvaluator == null)
                return;

            // UIスレッドでシーン評価（パラメーター取得）を1回だけ実行
            var evaluated = sceneEvaluator.EvaluateScene(nodeGraph);
            var sceneParams = new SceneParams(
                evaluated.Item1, evaluated.Item2, evaluated.Item3,
                evaluated.Item4, evaluated.Item5,
                evaluated.Item6, evaluated.Item7,  // MeshInstances, MeshCaches
                evaluated.SamplesPerPixel, evaluated.MaxBounces, evaluated.TraceRecursionDepth,
                evaluated.Exposure, evaluated.ToneMapOperator,
                evaluated.DenoiserStabilization, evaluated.ShadowStrength, evaluated.ShadowAbsorptionScale,
                evaluated.EnableDenoiser, evaluated.Gamma,
                photonDebugMode, photonDebugScale,
                evaluated.LightAttenuationConstant, evaluated.LightAttenuationLinear, evaluated.LightAttenuationQuadratic,
                evaluated.MaxShadowLights, evaluated.NRDBypassDistance, evaluated.NRDBypassBlendRange);

            lock (_renderLock)
            {
                if (_isRenderingInProgress)
                {
                    // レンダリング中 → キューに保存（上書き）
                    _pendingSceneParams = sceneParams;
                    return;
                }
                
                _isRenderingInProgress = true;
            }

            // 非同期でレンダリング開始
            _ = RenderWithParamsAsync(sceneParams);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // ウィンドウハンドル取得
                var windowHandle = new WindowInteropHelper(this).Handle;
                
                // DPIスケーリングを取得して、物理ピクセルで正確なサイズになるようWPFサイズを計算
                var source = PresentationSource.FromVisual(this);
                double dpiScaleX = 1.0;
                double dpiScaleY = 1.0;
                if (source?.CompositionTarget != null)
                {
                    dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
                    dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
                }
                
                // WPF単位でのサイズ（物理ピクセル / DPIスケール）
                double wpfWidth = RenderWidth / dpiScaleX;
                double wpfHeight = RenderHeight / dpiScaleY;
                
                // レンダリングサービス初期化
                renderService = new RenderService();
                sceneEvaluator = new SceneEvaluator();
                
                if (!renderService.Initialize(windowHandle, RenderWidth, RenderHeight))
                {
                    MessageBox.Show("DirectXレンダリングエンジンの初期化に失敗しました。\n\n" +
                                  "必要な環境：\n" +
                                  "- DirectX 12対応GPU\n" +
                                  "- Windows 10 2004以降\n" +
                                  "- 最新のグラフィックスドライバ",
                                  "初期化エラー", 
                                  MessageBoxButton.OK, 
                                  MessageBoxImage.Error);
                    Close();
                    return;
                }
                
                // ダミーレンダリング：シェーダーコンパイルなどの初期化を完了させる
                PerformWarmupRender();
                
                // WritableBitmapを作成
                renderBitmap = new WriteableBitmap(
                    RenderWidth, 
                    RenderHeight, 
                    96, 96, 
                    PixelFormats.Bgra32, 
                    null);
                RenderImage.Source = renderBitmap;
                
                // DPI補正したサイズで初期表示（物理ピクセルで正確なサイズ）
                RenderImage.Width = wpfWidth;
                RenderImage.Height = wpfHeight;
                RenderImage.Stretch = Stretch.None;
                
                UpdateInfo();
                
                // レイアウト更新後にリサイズ可能モードに切り替え
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // 初期サイズ設定後、ユーザーがリサイズできるようにする
                    SizeToContent = SizeToContent.Manual;
                    
                    // リサイズ時はスケーリングを有効にする
                    RenderImage.Width = double.NaN;
                    RenderImage.Height = double.NaN;
                    RenderImage.Stretch = Stretch.Uniform;
                }), DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            StopRendering();
            
            // ノードグラフのイベント購読を解除
            if (nodeGraph != null)
            {
                nodeGraph.SceneChanged -= OnSceneChanged;
            }
            
            renderService?.Dispose();
            renderService = null;
        }

        // MainWindowのツールバーから呼び出されるメソッド
        public void StartRenderingFromToolbar()
        {
            StartRendering();
        }
        
        public void StopRenderingFromToolbar()
        {
            StopRendering();
        }
        
        public WriteableBitmap? GetRenderBitmap()
        {
            return renderBitmap;
        }

        public WriteableBitmap? GetRenderBitmapCopy()
        {
            if (renderBitmap == null)
                return null;

            if (!Dispatcher.CheckAccess())
            {
                return Dispatcher.Invoke(GetRenderBitmapCopy);
            }

            try
            {
                renderBitmap.Lock();
                try
                {
                    var copy = new WriteableBitmap(
                        renderBitmap.PixelWidth,
                        renderBitmap.PixelHeight,
                        renderBitmap.DpiX,
                        renderBitmap.DpiY,
                        renderBitmap.Format,
                        renderBitmap.Palette);

                    copy.WritePixels(
                        new Int32Rect(0, 0, renderBitmap.PixelWidth, renderBitmap.PixelHeight),
                        renderBitmap.BackBuffer,
                        renderBitmap.BackBufferStride * renderBitmap.PixelHeight,
                        renderBitmap.BackBufferStride);

                    copy.Freeze();
                    return copy;
                }
                finally
                {
                    renderBitmap.Unlock();
                }
            }
            catch
            {
                return null;
            }
        }

        private void StartRendering()
        {
            if (isRendering || renderService == null || nodeGraph == null || sceneEvaluator == null)
                return;

            isRendering = true;
            StatusText.Text = "状態: レンダリング中";
            UpdateInfo();

            // 初回レンダリング：シーン評価してパラメーター取得
            var evaluated = sceneEvaluator.EvaluateScene(nodeGraph);
            var sceneParams = new SceneParams(
                evaluated.Item1, evaluated.Item2, evaluated.Item3,
                evaluated.Item4, evaluated.Item5,
                evaluated.Item6, evaluated.Item7,  // MeshInstances, MeshCaches
                evaluated.SamplesPerPixel, evaluated.MaxBounces, evaluated.TraceRecursionDepth,
                evaluated.Exposure, evaluated.ToneMapOperator,
                evaluated.DenoiserStabilization, evaluated.ShadowStrength, evaluated.ShadowAbsorptionScale,
                evaluated.EnableDenoiser, evaluated.Gamma,
                photonDebugMode, photonDebugScale,
                evaluated.LightAttenuationConstant, evaluated.LightAttenuationLinear, evaluated.LightAttenuationQuadratic,
                evaluated.MaxShadowLights, evaluated.NRDBypassDistance, evaluated.NRDBypassBlendRange);

            lock (_renderLock)
            {
                _isRenderingInProgress = true;
            }

            // 非同期でレンダリング開始
            _ = RenderWithParamsAsync(sceneParams);
        }

        private void StopRendering()
        {
            if (!isRendering)
                return;

            isRendering = false;
            StatusText.Text = "状態: 停止中";
        }

        /// <summary>
        /// 指定されたパラメーターで非同期にレンダリングを実行する
        /// キューに保留中のパラメーターがあれば、完了後に再度レンダリングを実行する
        /// </summary>
        private async Task RenderWithParamsAsync(SceneParams sceneParams)
        {
            while (true)
            {
                byte[]? finalPixelData = null;
                double renderTimeMs = 0;
                
                try
                {
                    // バックグラウンドスレッドで複数パスレンダリング
                    finalPixelData = await Task.Run(() =>
                    {
                        for (int i = 0; i < MinRenderPassesForTemporal; i++)
                        {
                            // レンダリング停止チェック
                            if (!isRendering || renderService == null)
                                return null;

                            // 同じパラメーターでシーン更新＆レンダリング
                            renderService.UpdateScene(
                                sceneParams.Spheres, sceneParams.Planes, sceneParams.Boxes,
                                sceneParams.Camera, sceneParams.Lights,
                                sceneParams.MeshInstances, sceneParams.MeshCaches,
                                sceneParams.SamplesPerPixel, sceneParams.MaxBounces, sceneParams.TraceRecursionDepth,
                                sceneParams.Exposure, sceneParams.ToneMapOperator,
                                sceneParams.DenoiserStabilization, sceneParams.ShadowStrength, sceneParams.ShadowAbsorptionScale,
                                sceneParams.EnableDenoiser, sceneParams.Gamma,
                                sceneParams.PhotonDebugMode, sceneParams.PhotonDebugScale,
                                sceneParams.LightAttenuationConstant, sceneParams.LightAttenuationLinear, sceneParams.LightAttenuationQuadratic,
                                sceneParams.MaxShadowLights, sceneParams.NRDBypassDistance, sceneParams.NRDBypassBlendRange);
                            
                            // 空シーンはGPUを使わずスカイ色で即時更新
                            bool emptyScene = (sceneParams.Spheres.Length == 0 &&
                                               sceneParams.Planes.Length == 0 &&
                                               sceneParams.Boxes.Length == 0 &&
                                               sceneParams.MeshInstances.Length == 0);
                            if (emptyScene)
                            {
                                return GetCachedSkyBuffer();
                            }
                            
                            // レンダリング処理の時間のみを計測
                            _renderStopwatch.Restart();
                            renderService.Render();
                            _renderStopwatch.Stop();
                            renderTimeMs += _renderStopwatch.Elapsed.TotalMilliseconds;
                        }
                        
                        // 最後にピクセルデータを取得
                        return renderService?.GetPixelData();
                    });

                    // UIスレッドで画面更新
                    if (finalPixelData != null && isRendering)
                    {
                        UpdateDisplay(finalPixelData);
                        
                        // 最初のフレームは初期化コストが含まれるためスキップ
                        if (_isFirstRender)
                        {
                            _isFirstRender = false;
                        }
                        else
                        {
                            // レンダリング完了イベントを発行
                            RenderCompleted?.Invoke(renderTimeMs);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Render error: {ex.Message}");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        MessageBox.Show($"レンダリングエラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                        StopRendering();
                    });
                    
                    lock (_renderLock)
                    {
                        _isRenderingInProgress = false;
                        _pendingSceneParams = null;
                    }
                    return;
                }

                // キューを確認
                lock (_renderLock)
                {
                    if (_pendingSceneParams != null)
                    {
                        // キューから取り出して次のレンダリングへ
                        sceneParams = _pendingSceneParams;
                        _pendingSceneParams = null;
                        // ループ継続
                    }
                    else
                    {
                        // キューが空 → 終了
                        _isRenderingInProgress = false;
                        break;
                    }
                }
            }
        }

        private byte[] GetCachedSkyBuffer()
        {
            if (cachedSkyBuffer != null)
            {
                return cachedSkyBuffer;
            }

            int dataSize = RenderWidth * RenderHeight * 4;
            cachedSkyBuffer = new byte[dataSize];

            // RGBA (same as compute clear): 0.5, 0.7, 1.0, 1.0
            byte r = (byte)(0.5f * 255);
            byte g = (byte)(0.7f * 255);
            byte b = (byte)(1.0f * 255);
            byte a = 255;

            for (int i = 0; i < dataSize; i += 4)
            {
                cachedSkyBuffer[i + 0] = r;
                cachedSkyBuffer[i + 1] = g;
                cachedSkyBuffer[i + 2] = b;
                cachedSkyBuffer[i + 3] = a;
            }

            return cachedSkyBuffer;
        }

        /// <summary>
        /// ピクセルデータを画面に転送する
        /// </summary>
        private void UpdateDisplay(byte[] pixelData)
        {
            if (renderBitmap == null)
                return;

            renderBitmap.Lock();
            try
            {
                unsafe
                {
                    byte* pBackBuffer = (byte*)renderBitmap.BackBuffer;
                    int stride = renderBitmap.BackBufferStride;
                    
                    // RGBA to BGRA conversion using uint32 swap for better performance
                    fixed (byte* pSrc = pixelData)
                    {
                        for (int y = 0; y < RenderHeight; y++)
                        {
                            uint* srcRow = (uint*)(pSrc + y * RenderWidth * 4);
                            uint* dstRow = (uint*)(pBackBuffer + y * stride);
                            
                            for (int x = 0; x < RenderWidth; x++)
                            {
                                uint rgba = srcRow[x];
                                // RGBA -> BGRA: swap R and B
                                uint r = (rgba >> 0) & 0xFF;
                                uint g = (rgba >> 8) & 0xFF;
                                uint b = (rgba >> 16) & 0xFF;
                                uint a = (rgba >> 24) & 0xFF;
                                dstRow[x] = (a << 24) | (r << 16) | (g << 8) | b;
                            }
                        }
                    }
                }
                
                renderBitmap.AddDirtyRect(new Int32Rect(0, 0, RenderWidth, RenderHeight));
            }
            finally
            {
                renderBitmap.Unlock();
            }
        }

        /// <summary>
        /// ウォームアップ用のダミーレンダリングを実行
        /// シェーダーコンパイルやパイプライン初期化を事前に完了させる
        /// </summary>
        private void PerformWarmupRender()
        {
            if (renderService == null)
                return;
            
            try
            {
                // 空のシーンでダミーレンダリング（1つの球体を配置してシェーダーを強制的にコンパイル）
                var dummySphere = new SphereData
                {
                    Position = new Vector3(0, 0, 0),
                    Radius = 1.0f,
                    Color = new Vector4(0, 0, 0, 1),  // 真っ黒
                    Metallic = 0,
                    Roughness = 1,
                    Transmission = 0,
                    IOR = 1.0f,
                    Specular = 0,
                    Emission = new Vector3(0, 0, 0),
                    Absorption = new Vector3(0, 0, 0)
                };
                
                var dummyCamera = new CameraData
                {
                    Position = new Vector3(0, 0, -10),
                    LookAt = new Vector3(0, 0, 0),
                    Up = new Vector3(0, 1, 0),
                    FieldOfView = 60.0f,
                    AspectRatio = (float)RenderWidth / RenderHeight,
                    Near = 0.1f,
                    Far = 1000.0f,
                    ApertureSize = 0,
                    FocusDistance = 10.0f
                };
                
                var dummyLight = new LightData
                {
                    Position = new Vector3(0, 10, 0),
                    Color = new Vector4(0, 0, 0, 1),  // 真っ暗
                    Intensity = 0,
                    Type = LightType.Point,
                    Radius = 0,
                    SoftShadowSamples = 1
                };
                
                // ダミーシーンでレンダリング実行（シェーダーコンパイルを発生させる）
                renderService.UpdateScene(
                    new[] { dummySphere },
                    Array.Empty<PlaneData>(),
                    Array.Empty<BoxData>(),
                    dummyCamera,
                    new[] { dummyLight },
                    Array.Empty<MeshInstanceData>(),
                    Array.Empty<MeshCacheData>(),
                    1, 1, 1,  // samplesPerPixel, maxBounces, traceRecursionDepth
                    1.0f, 0, 1.0f, 1.0f, 1.0f, false, 1.0f, 0, 1.0f);  // 最小設定
                
                renderService.Render();
                
                Debug.WriteLine("Warmup render completed - shaders compiled");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Warmup render failed: {ex.Message}");
            }
        }

        private void UpdateInfo()
        {
            if (nodeGraph != null)
            {
                var objects = nodeGraph.GetAllNodes();
                ObjectCountText.Text = $"オブジェクト: {System.Linq.Enumerable.Count(objects)}";
            }
            PhotonDebugText.Text = photonDebugMode == 0
                ? "Photon Debug: Off"
                : $"Photon Debug: Mode {photonDebugMode} (x{photonDebugScale:0.##})";
            InfoOverlay.Visibility = photonDebugMode == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // F1: Cycle photon debug mode (used for various shader debug visualizations)
            // 0 = off
            // 1-2 = existing photon debug modes
            // 3-4 = material debug (see HLSL)
            // 5 = Composite: show raw fallback blend factor (rawT)
            // 6 = Composite: show ViewZ visualization
            // 7 = Composite: show PreDenoiseColor full-screen
            // 8 = Composite: show far-field selection mask (rawT>0.5)
            // 9 = RayGen: refraction ray direction (first refraction)
            // 10 = RayGen: refraction diagnostics (overflow / hit)
            // 11 = Composite: show ViewZ-in-range mask (zStart..zEnd)
            // 12 = Composite: show ViewZ linear scale (debug)
            if (e.Key == System.Windows.Input.Key.F1)
            {
                photonDebugMode = (photonDebugMode + 1) % 13; // 0..12
                UpdateInfo();
                RequestRenderRefresh();
                e.Handled = true;
                return;
            }

            if (e.Key == System.Windows.Input.Key.F2)
            {
                photonDebugMode = 0;
                UpdateInfo();
                RequestRenderRefresh();
                e.Handled = true;
                return;
            }

            if (e.Key == System.Windows.Input.Key.F3)
            {
                int index = Array.IndexOf(photonDebugScaleOptions, photonDebugScale);
                if (index < 0)
                {
                    photonDebugScale = photonDebugScaleOptions[0];
                }
                else
                {
                    photonDebugScale = photonDebugScaleOptions[(index + 1) % photonDebugScaleOptions.Length];
                }
                UpdateInfo();
                RequestRenderRefresh();
                e.Handled = true;
            }
        }

        private void RequestRenderRefresh()
        {
            if (!isRendering || renderService == null || nodeGraph == null || sceneEvaluator == null)
                return;

            var evaluated = sceneEvaluator.EvaluateScene(nodeGraph);
            var sceneParams = new SceneParams(
                evaluated.Item1, evaluated.Item2, evaluated.Item3,
                evaluated.Item4, evaluated.Item5,
                evaluated.Item6, evaluated.Item7,
                evaluated.SamplesPerPixel, evaluated.MaxBounces, evaluated.TraceRecursionDepth,
                evaluated.Exposure, evaluated.ToneMapOperator,
                evaluated.DenoiserStabilization, evaluated.ShadowStrength, evaluated.ShadowAbsorptionScale,
                evaluated.EnableDenoiser, evaluated.Gamma,
                photonDebugMode, photonDebugScale,
                evaluated.LightAttenuationConstant, evaluated.LightAttenuationLinear, evaluated.LightAttenuationQuadratic,
                evaluated.MaxShadowLights, evaluated.NRDBypassDistance, evaluated.NRDBypassBlendRange);

            lock (_renderLock)
            {
                if (_isRenderingInProgress)
                {
                    _pendingSceneParams = sceneParams;
                    return;
                }

                _isRenderingInProgress = true;
            }

            _ = RenderWithParamsAsync(sceneParams);
        }

    }
}

using System;
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
        bool EnableDenoiser,
        float Gamma,
        int PhotonDebugMode,
        float PhotonDebugScale);

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
        
        // 解像度を1920x1080に固定
        private const int RenderWidth = 1920;
        private const int RenderHeight = 1080;
        
        // テンポラルデノイズのための最低描画回数
        // 1回目: 履歴なし、2回目: テンポラル蓄積開始、3回目以降: 安定化
        private const int MinRenderPassesForTemporal = 1; // DEBUG: reduced from 5 to isolate crash

        public RenderWindow()
        {
            InitializeComponent();
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
                evaluated.DenoiserStabilization, evaluated.ShadowStrength,
                evaluated.EnableDenoiser, evaluated.Gamma,
                photonDebugMode, photonDebugScale);

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
                
                // DPIスケーリングを取得して、物理ピクセル1920x1080になるようWPFサイズを計算
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
                
                // WritableBitmapを作成
                renderBitmap = new WriteableBitmap(
                    RenderWidth, 
                    RenderHeight, 
                    96, 96, 
                    PixelFormats.Bgra32, 
                    null);
                RenderImage.Source = renderBitmap;
                
                // DPI補正したサイズで初期表示（物理ピクセル1920x1080）
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
                evaluated.DenoiserStabilization, evaluated.ShadowStrength,
                evaluated.EnableDenoiser, evaluated.Gamma,
                photonDebugMode, photonDebugScale);

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
                                sceneParams.DenoiserStabilization, sceneParams.ShadowStrength,
                                sceneParams.EnableDenoiser, sceneParams.Gamma,
                                sceneParams.PhotonDebugMode, sceneParams.PhotonDebugScale);
                            
                            // 空シーンはGPUを使わずスカイ色で即時更新
                            bool emptyScene = (sceneParams.Spheres.Length == 0 &&
                                               sceneParams.Planes.Length == 0 &&
                                               sceneParams.Boxes.Length == 0 &&
                                               sceneParams.MeshInstances.Length == 0);
                            if (emptyScene)
                            {
                                return GetCachedSkyBuffer();
                            }
                            
                            renderService.Render();
                        }
                        
                        // 最後にピクセルデータを取得
                        return renderService?.GetPixelData();
                    });

                    // UIスレッドで画面更新
                    if (finalPixelData != null && isRendering)
                    {
                        UpdateDisplay(finalPixelData);
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
            if (e.Key == System.Windows.Input.Key.F2)
            {
                photonDebugMode = (photonDebugMode + 1) % 5;
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
                evaluated.DenoiserStabilization, evaluated.ShadowStrength,
                evaluated.EnableDenoiser, evaluated.Gamma,
                photonDebugMode, photonDebugScale);

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

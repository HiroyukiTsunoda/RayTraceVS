using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RayTraceVS.WPF.Services;
using RayTraceVS.WPF.Models;

namespace RayTraceVS.WPF.Views
{
    public partial class RenderWindow : Window
    {
        private RenderService? renderService;
        private NodeGraph? nodeGraph;
        private SceneEvaluator? sceneEvaluator;
        private WriteableBitmap? renderBitmap;
        
        private bool isRendering = false;
        private bool needsRedraw = false;
        
        // 解像度を1920x1080に固定
        private const int RenderWidth = 1920;
        private const int RenderHeight = 1080;
        
        // テンポラルデノイズのための最低描画回数
        // 1回目: 履歴なし、2回目: テンポラル蓄積開始、3回目以降: 安定化
        private const int MinRenderPassesForTemporal = 5;

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
            // シーンが変更されたら再描画が必要
            if (isRendering)
            {
                needsRedraw = true;
                // UIスレッドでテンポラルデノイズのために複数回描画
                Dispatcher.BeginInvoke(new Action(() => RenderMultiplePasses()), DispatcherPriority.Render);
            }
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
            if (isRendering || renderService == null)
                return;

            isRendering = true;
            StatusText.Text = "状態: レンダリング中";

            // テンポラルデノイズのために最低回数描画
            RenderMultiplePasses();
            UpdateInfo();
        }
        
        /// <summary>
        /// テンポラルデノイズのために最低回数描画する
        /// 最終フレームのみ画面に表示（中間フレームは内部処理のみ）
        /// </summary>
        private void RenderMultiplePasses()
        {
            for (int i = 0; i < MinRenderPassesForTemporal; i++)
            {
                bool isLastPass = (i == MinRenderPassesForTemporal - 1);
                RenderOnce(updateDisplay: isLastPass);
            }
        }

        private void StopRendering()
        {
            if (!isRendering)
                return;

            isRendering = false;
            StatusText.Text = "状態: 停止中";
        }

        /// <summary>
        /// 1フレーム描画する
        /// </summary>
        /// <param name="updateDisplay">trueの場合のみ画面に表示（falseは内部処理のみ）</param>
        private void RenderOnce(bool updateDisplay = true)
        {
            if (!isRendering || renderService == null || nodeGraph == null || sceneEvaluator == null || renderBitmap == null)
                return;

            needsRedraw = false;

            try
            {
                // ノードグラフからシーンデータを評価
                var (spheres, planes, boxes, camera, lights, samplesPerPixel, maxBounces, exposure, toneMapOperator, denoiserStabilization, shadowStrength, enableDenoiser) = sceneEvaluator.EvaluateScene(nodeGraph);
                
                // シーン更新
                renderService.UpdateScene(spheres, planes, boxes, camera, lights, samplesPerPixel, maxBounces, exposure, toneMapOperator, denoiserStabilization, shadowStrength, enableDenoiser);
                
                // レンダリング（GPU側で描画実行）
                renderService.Render();
                
                // 最終フレームのみ画面に転送
                if (!updateDisplay)
                    return;
                
                // ピクセルデータを取得して画面に表示
                var pixelData = renderService.GetPixelData();
                
                if (pixelData != null)
                {
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
                else
                {
                    System.Diagnostics.Debug.WriteLine("Pixel data is null!");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Render error: {ex.Message}");
                MessageBox.Show($"レンダリングエラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                StopRendering();
            }
        }

        private void UpdateInfo()
        {
            if (nodeGraph != null)
            {
                var objects = nodeGraph.GetAllNodes();
                ObjectCountText.Text = $"オブジェクト: {System.Linq.Enumerable.Count(objects)}";
            }
        }

    }
}

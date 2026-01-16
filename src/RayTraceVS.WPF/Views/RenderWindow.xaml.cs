using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
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
        
        private const int RenderWidth = 1280;
        private const int RenderHeight = 720;
        
        private SettingsService settingsService;

        public RenderWindow()
        {
            InitializeComponent();
            
            settingsService = new SettingsService();
            
            // ウィンドウの位置とサイズを復元
            RestoreWindowBounds();
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
                // UIスレッドで再描画を実行
                Dispatcher.BeginInvoke(new Action(() => RenderOnce()), DispatcherPriority.Render);
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // ウィンドウハンドル取得
                var windowHandle = new WindowInteropHelper(this).Handle;
                
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
                
                UpdateInfo();
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
            
            // ウィンドウの位置とサイズを保存
            SaveWindowBounds();
            
            renderService?.Dispose();
            renderService = null;
        }
        
        private void RestoreWindowBounds()
        {
            var bounds = settingsService.RenderWindowBounds;
            if (bounds != null && bounds.Width > 0 && bounds.Height > 0)
            {
                // 位置を復元（マルチモニター対応で負の座標も許可）
                this.Left = bounds.Left;
                this.Top = bounds.Top;
                this.Width = bounds.Width;
                this.Height = bounds.Height;
                
                if (bounds.IsMaximized)
                {
                    this.WindowState = WindowState.Maximized;
                }
            }
        }
        
        private void SaveWindowBounds()
        {
            // 最大化状態の場合はRestoreBoundsから位置とサイズを取得
            var bounds = new WindowBounds();
            
            if (this.WindowState == WindowState.Maximized)
            {
                bounds.Left = this.RestoreBounds.Left;
                bounds.Top = this.RestoreBounds.Top;
                bounds.Width = this.RestoreBounds.Width;
                bounds.Height = this.RestoreBounds.Height;
                bounds.IsMaximized = true;
            }
            else
            {
                bounds.Left = this.Left;
                bounds.Top = this.Top;
                bounds.Width = this.Width;
                bounds.Height = this.Height;
                bounds.IsMaximized = false;
            }
            
            settingsService.RenderWindowBounds = bounds;
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            StartRendering();
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            StopRendering();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            SaveImage();
        }

        private void Resolution_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // 解像度変更処理（実装予定）
        }

        private void StartRendering()
        {
            if (isRendering || renderService == null)
                return;

            isRendering = true;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StatusText.Text = "状態: レンダリング中";

            // 一回だけレンダリング
            RenderOnce();
            UpdateInfo();
        }

        private void StopRendering()
        {
            if (!isRendering)
                return;

            isRendering = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            StatusText.Text = "状態: 停止中";
        }

        private void RenderOnce()
        {
            if (!isRendering || renderService == null || nodeGraph == null || sceneEvaluator == null || renderBitmap == null)
                return;

            needsRedraw = false;

            try
            {
                // ノードグラフからシーンデータを評価
                var (spheres, planes, cylinders, camera, lights) = sceneEvaluator.EvaluateScene(nodeGraph);
                
                // シーン更新
                renderService.UpdateScene(spheres, planes, cylinders, camera, lights);
                
                // レンダリング
                renderService.Render();
                
                // ピクセルデータを取得
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

        private void SaveImage()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "PNG画像|*.png|JPEG画像|*.jpg|ビットマップ|*.bmp",
                    DefaultExt = "png",
                    FileName = $"render_{DateTime.Now:yyyyMMdd_HHmmss}"
                };

                if (dialog.ShowDialog() == true)
                {
                    // レンダリング結果を保存（実装予定）
                    MessageBox.Show("画像保存機能は実装予定です。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

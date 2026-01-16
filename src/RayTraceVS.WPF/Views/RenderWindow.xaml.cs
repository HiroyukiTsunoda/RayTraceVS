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
        
        private DispatcherTimer renderTimer;
        private DispatcherTimer fpsTimer;
        
        private int frameCount = 0;
        private int fps = 0;
        private bool isRendering = false;
        
        private const int RenderWidth = 1280;
        private const int RenderHeight = 720;
        
        private SettingsService settingsService;

        public RenderWindow()
        {
            InitializeComponent();
            
            settingsService = new SettingsService();
            
            // ウィンドウの位置とサイズを復元
            RestoreWindowBounds();
            
            renderTimer = new DispatcherTimer();
            renderTimer.Interval = TimeSpan.FromMilliseconds(16); // ~60 FPS
            renderTimer.Tick += RenderTimer_Tick;

            fpsTimer = new DispatcherTimer();
            fpsTimer.Interval = TimeSpan.FromSeconds(1);
            fpsTimer.Tick += FpsTimer_Tick;
        }

        public void SetNodeGraph(NodeGraph graph)
        {
            nodeGraph = graph;
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

            renderTimer.Start();
            fpsTimer.Start();
        }

        private void StopRendering()
        {
            if (!isRendering)
                return;

            isRendering = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;

            renderTimer.Stop();
            fpsTimer.Stop();
        }

        private void RenderTimer_Tick(object? sender, EventArgs e)
        {
            if (!isRendering || renderService == null || nodeGraph == null || sceneEvaluator == null || renderBitmap == null)
                return;

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
                            
                            // RGBAからBGRAに変換
                            for (int y = 0; y < RenderHeight; y++)
                            {
                                for (int x = 0; x < RenderWidth; x++)
                                {
                                    int srcIndex = (y * RenderWidth + x) * 4;
                                    int dstIndex = y * stride + x * 4;
                                    
                                    // RGBAからBGRAに変換
                                    pBackBuffer[dstIndex + 0] = pixelData[srcIndex + 2]; // B
                                    pBackBuffer[dstIndex + 1] = pixelData[srcIndex + 1]; // G
                                    pBackBuffer[dstIndex + 2] = pixelData[srcIndex + 0]; // R
                                    pBackBuffer[dstIndex + 3] = pixelData[srcIndex + 3]; // A
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
                
                frameCount++;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Render error: {ex.Message}");
                MessageBox.Show($"レンダリングエラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                StopRendering();
            }
        }

        private void FpsTimer_Tick(object? sender, EventArgs e)
        {
            fps = frameCount;
            frameCount = 0;
            
            UpdateInfo();
        }

        private void UpdateInfo()
        {
            FpsText.Text = $"FPS: {fps}";
            
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

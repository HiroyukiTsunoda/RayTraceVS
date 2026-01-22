using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RayTraceVS.WPF.ViewModels;
using RayTraceVS.WPF.Views;
using RayTraceVS.WPF.Services;

namespace RayTraceVS.WPF
{
    public partial class MainWindow : Window
    {
        private MainViewModel? viewModel;
        private RenderWindow? renderWindow;
        private string? currentFilePath;
        private SettingsService settingsService;
        private bool hasUnsavedChanges;
        private bool isRendering = false;

        public MainWindow()
        {
            InitializeComponent();
            viewModel = new MainViewModel();
            DataContext = viewModel;
            
            settingsService = new SettingsService();
            
            // ノードと接続の変更を監視
            viewModel.Nodes.CollectionChanged += OnSceneChanged;
            viewModel.Connections.CollectionChanged += OnSceneChanged;
            
            // ノードのプロパティ変更（パラメーター変更）を監視
            viewModel.NodeGraph.SceneChanged += OnNodeGraphSceneChanged;
            
            // ウィンドウの位置とサイズを復元
            RestoreWindowBounds();
            
            // 起動時に前回開いていたファイルを読み込む
            LoadLastScene();
            
            // シーンが読み込まれたら自動的にレンダリングを開始
            this.Loaded += MainWindow_Loaded;
            
            // ウィンドウが閉じる際に設定を保存
            this.Closing += MainWindow_Closing;
        }
        
        private void OnSceneChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (!hasUnsavedChanges)
            {
                hasUnsavedChanges = true;
                UpdateTitle();
            }
        }
        
        private void OnNodeGraphSceneChanged(object? sender, EventArgs e)
        {
            if (!hasUnsavedChanges)
            {
                hasUnsavedChanges = true;
                UpdateTitle();
            }
        }
        
        private void UpdateTitle()
        {
            string baseName = "RayTraceVS";
            string fileName = string.IsNullOrEmpty(currentFilePath) 
                ? "新規シーン" 
                : System.IO.Path.GetFileName(currentFilePath);
            
            if (hasUnsavedChanges)
            {
                Title = $"{baseName}* - {fileName}";
            }
            else
            {
                Title = $"{baseName} - {fileName}";
            }
        }
        
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // UIのレンダリングが完全に完了してから接続線を更新
            Dispatcher.BeginInvoke(new Action(() =>
            {
                NodeEditor.RefreshConnectionLines();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // ノードがある場合、終了確認
            if (viewModel != null && viewModel.Nodes.Count > 0)
            {
                var result = MessageBox.Show(
                    "アプリケーションを終了しますか？\n\n未保存の変更は失われる可能性があります。",
                    "終了確認",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            // ウィンドウの位置とサイズを保存
            SaveWindowBounds();

            // 現在のファイルパスを保存
            if (!string.IsNullOrEmpty(currentFilePath))
            {
                settingsService.LastOpenedFilePath = currentFilePath;
            }
        }
        
        private void RestoreWindowBounds()
        {
            var bounds = settingsService.MainWindowBounds;
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
            var bounds = new Services.WindowBounds();
            
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
            
            settingsService.MainWindowBounds = bounds;
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Deleteキーの処理（優先）
            if (e.Key == System.Windows.Input.Key.Delete)
            {
                NodeEditor.DeleteSelectedNodes();
                e.Handled = true;
                return;
            }
            
            // F5: レンダリング開始
            if (e.Key == System.Windows.Input.Key.F5)
            {
                if (e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Shift)
                {
                    // Shift+F5: レンダリング停止
                    StopRendering();
                }
                else
                {
                    // F5: レンダリング開始
                    StartRendering();
                }
                e.Handled = true;
                return;
            }
            
            // Ctrl+Shift+Z: Redo（Ctrl+Zより先に判定）
            if (e.Key == System.Windows.Input.Key.Z && 
                e.KeyboardDevice.Modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
            {
                if (viewModel != null && viewModel.CommandManager.CanRedo)
                {
                    viewModel.CommandManager.Redo();
                    // 接続線とノード値の更新
                    NodeEditor.RefreshConnectionLines();
                    NodeEditor.RefreshNodeTextBoxValues();
                }
                e.Handled = true;
                return;
            }
            
            // キーボードショートカット
            if (e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case System.Windows.Input.Key.Z:
                        // Ctrl+Z: Undo
                        if (viewModel != null && viewModel.CommandManager.CanUndo)
                        {
                            viewModel.CommandManager.Undo();
                            // 接続線とノード値の更新
                            NodeEditor.RefreshConnectionLines();
                            NodeEditor.RefreshNodeTextBoxValues();
                        }
                        e.Handled = true;
                        break;
                    case System.Windows.Input.Key.N:
                        NewScene_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                        break;
                    case System.Windows.Input.Key.O:
                        OpenScene_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                        break;
                    case System.Windows.Input.Key.S:
                        if (e.KeyboardDevice.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
                        {
                            SaveSceneAs_Click(this, new RoutedEventArgs());
                        }
                        else
                        {
                            SaveScene_Click(this, new RoutedEventArgs());
                        }
                        e.Handled = true;
                        break;
                }
            }
            else if (e.KeyboardDevice.Modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
            {
                if (e.Key == System.Windows.Input.Key.S)
                {
                    SaveSceneAs_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
                else if (e.Key == System.Windows.Input.Key.P)
                {
                    // Ctrl+Shift+P: スクリーンショット保存
                    SaveScreenshot();
                    e.Handled = true;
                }
            }
        }
        
        private void LoadLastScene()
        {
            try
            {
                // 前回開いていたファイルパスを取得
                var lastFilePath = settingsService.LastOpenedFilePath;
                
                // ファイルパスが存在し、ファイルが実際に存在する場合は読み込む
                if (!string.IsNullOrEmpty(lastFilePath) && System.IO.File.Exists(lastFilePath))
                {
                    LoadSceneFromFile(lastFilePath);
                }
                // 前回のファイルがない場合は、サンプルシーンを読み込む
                else
                {
                    var sampleScenePath = @"c:\git\RayTraceVS\sample_scene.rtvs";
                    if (System.IO.File.Exists(sampleScenePath))
                    {
                        LoadSceneFromFile(sampleScenePath);
                    }
                }
            }
            catch (System.Exception ex)
            {
                // 起動時のエラーはメッセージボックスで表示
                MessageBox.Show($"シーンの読み込みに失敗しました：{ex.Message}", 
                              "警告", 
                              MessageBoxButton.OK, 
                              MessageBoxImage.Warning);
            }
        }

        private void LoadSceneFromFile(string filePath)
        {
            if (viewModel != null)
            {
                var sceneService = new SceneFileService();
                var (nodes, connections, viewportState) = sceneService.LoadScene(filePath);
                
                viewModel.Nodes.Clear();
                viewModel.Connections.Clear();
                
                // ビューポートの状態を先に設定（ノード追加前に設定することで初期描画のズレを防ぐ）
                NodeEditor.SetViewportState(viewportState);
                
                foreach (var node in nodes)
                    viewModel.AddNode(node);
                
                foreach (var connection in connections)
                    viewModel.AddConnection(connection);
                
                currentFilePath = filePath;
                hasUnsavedChanges = false;
                UpdateTitle();
                
                // Undo/Redo履歴をクリア
                viewModel.CommandManager.Clear();
                
                // パネルの開閉状態を復元（シーンファイルから）
                if (viewportState != null)
                {
                    SetPanelVisibility(viewportState.IsLeftPanelVisible, viewportState.IsRightPanelVisible);
                    
                    // Expanderの開閉状態を復元
                    ComponentPalette.SetExpanderStates(viewportState.ExpanderStates);
                }
                
                // UIのレンダリング完了後に接続線を更新
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    NodeEditor.RefreshConnectionLines();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
                
                // キャッシュにないFBXノードが除外された場合は警告を表示
                if (sceneService.RemovedNodeInfos.Count > 0)
                {
                    var message = "以下のノードはキャッシュにメッシュデータがないため除外されました：\n\n" +
                                  string.Join("\n", sceneService.RemovedNodeInfos) +
                                  "\n\nResource/Modelフォルダに対応するFBXファイルを配置して再起動してください。";
                    
                    MessageBox.Show(message, "警告：ノードが除外されました",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void NewScene_Click(object sender, RoutedEventArgs e)
        {
            // 新規シーン作成
            if (viewModel != null)
            {
                // ノードがある場合のみ確認ダイアログを表示
                if (viewModel.Nodes.Count > 0)
                {
                    var result = MessageBox.Show(
                        "現在のシーンを破棄して新規作成しますか？\n\n未保存の変更は失われます。", 
                        "新規シーン作成", 
                        MessageBoxButton.YesNo, 
                        MessageBoxImage.Warning);
                    
                    if (result != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }
                
                viewModel.Nodes.Clear();
                viewModel.Connections.Clear();
                currentFilePath = null;
                hasUnsavedChanges = false;
                UpdateTitle();
                
                // Undo/Redo履歴をクリア
                viewModel.CommandManager.Clear();
                
                // 新規作成時は設定をクリア
                settingsService.LastOpenedFilePath = null;
            }
        }

        private void OpenScene_Click(object sender, RoutedEventArgs e)
        {
            // シーン読み込み
            var dialog = new OpenFileDialog
            {
                Filter = "RayTraceVSシーン|*.rtvs|すべてのファイル|*.*",
                DefaultExt = "rtvs",
                Title = "シーンを開く"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    LoadSceneFromFile(dialog.FileName);
                    
                    // 設定を更新
                    settingsService.LastOpenedFilePath = currentFilePath;
                    
                    var fileName = System.IO.Path.GetFileName(dialog.FileName);
                    var nodeCount = viewModel?.Nodes.Count ?? 0;
                    var connectionCount = viewModel?.Connections.Count ?? 0;
                    
                    MessageBox.Show($"シーンを読み込みました。\n\nファイル: {fileName}\nノード数: {nodeCount}\n接続数: {connectionCount}", 
                                  "読み込み完了", 
                                  MessageBoxButton.OK, 
                                  MessageBoxImage.Information);
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show($"シーンの読み込みに失敗しました：\n\n{ex.Message}", 
                                  "エラー", 
                                  MessageBoxButton.OK, 
                                  MessageBoxImage.Error);
                }
            }
        }

        private void SaveScene_Click(object sender, RoutedEventArgs e)
        {
            // シーン保存
            if (string.IsNullOrEmpty(currentFilePath))
            {
                SaveSceneAs_Click(sender, e);
                return;
            }

            SaveSceneToFile(currentFilePath);
        }

        private void SaveSceneAs_Click(object sender, RoutedEventArgs e)
        {
            // 名前を付けて保存
            var dialog = new SaveFileDialog
            {
                Filter = "RayTraceVSシーン|*.rtvs|すべてのファイル|*.*",
                DefaultExt = "rtvs",
                FileName = string.IsNullOrEmpty(currentFilePath) 
                    ? "scene" 
                    : System.IO.Path.GetFileNameWithoutExtension(currentFilePath),
                Title = "名前を付けて保存"
            };

            if (dialog.ShowDialog() == true)
            {
                currentFilePath = dialog.FileName;
                SaveSceneToFile(dialog.FileName);
                
                // 設定を更新
                settingsService.LastOpenedFilePath = currentFilePath;
            }
        }

        private void SaveSceneToFile(string filePath)
        {
            try
            {
                if (viewModel != null)
                {
                    var sceneService = new SceneFileService();
                    var viewportState = NodeEditor.GetViewportState();
                    
                    // パネルの開閉状態も保存（シーンファイルに）
                    viewportState.IsLeftPanelVisible = LeftPanelBorder.Visibility == Visibility.Visible;
                    viewportState.IsRightPanelVisible = RightPanelBorder.Visibility == Visibility.Visible;
                    
                    // Expanderの開閉状態も保存
                    viewportState.ExpanderStates = ComponentPalette.GetExpanderStates();
                    
                    sceneService.SaveScene(filePath, viewModel.Nodes, viewModel.Connections, viewportState);
                    
                    // 保存成功：未保存フラグをリセットしてタイトル更新
                    hasUnsavedChanges = false;
                    UpdateTitle();
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"シーンの保存に失敗しました：\n\n{ex.Message}", 
                              "エラー", 
                              MessageBoxButton.OK, 
                              MessageBoxImage.Error);
            }
        }
        
        private void SetPanelVisibility(bool isLeftVisible, bool isRightVisible)
        {
            // 左パネルの表示/非表示
            if (isLeftVisible)
            {
                LeftPanelColumn.Width = new GridLength(250);
                LeftPanelBorder.Visibility = Visibility.Visible;
                LeftSplitter.Visibility = Visibility.Visible;
            }
            else
            {
                LeftPanelColumn.Width = new GridLength(0);
                LeftPanelBorder.Visibility = Visibility.Collapsed;
                LeftSplitter.Visibility = Visibility.Collapsed;
            }
            ToggleLeftPanelMenuItem.IsChecked = isLeftVisible;
            
            // 右パネルの表示/非表示
            if (isRightVisible)
            {
                RightPanelColumn.Width = new GridLength(300);
                RightPanelBorder.Visibility = Visibility.Visible;
                RightSplitter.Visibility = Visibility.Visible;
            }
            else
            {
                RightPanelColumn.Width = new GridLength(0);
                RightPanelBorder.Visibility = Visibility.Collapsed;
                RightSplitter.Visibility = Visibility.Collapsed;
            }
            ToggleRightPanelMenuItem.IsChecked = isRightVisible;
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void StartRendering_Click(object sender, RoutedEventArgs e)
        {
            StartRendering();
        }

        private void StopRendering_Click(object sender, RoutedEventArgs e)
        {
            // レンダリングウィンドウを閉じる
            if (renderWindow != null && renderWindow.IsLoaded)
            {
                renderWindow.Close();
                renderWindow = null;
            }
            UpdateRenderingState(false);
        }
        
        // ツールバーボタンのイベントハンドラ
        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            StartRendering();
        }
        
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopRendering();
        }
        
        private void ScreenshotButton_Click(object sender, RoutedEventArgs e)
        {
            SaveScreenshot();
        }
        
        private void StartRendering()
        {
            if (isRendering) return;
            
            // レンダリングウィンドウを開く
            if (renderWindow == null || !renderWindow.IsLoaded)
            {
                renderWindow = new RenderWindow();
                
                if (viewModel != null)
                {
                    renderWindow.SetNodeGraph(viewModel.NodeGraph);
                }
                
                renderWindow.Closed += (s, args) => 
                {
                    UpdateRenderingState(false);
                    renderWindow = null;
                };
                
                renderWindow.Show();
                
                // レンダリング開始を通知
                renderWindow.StartRenderingFromToolbar();
            }
            else
            {
                renderWindow.Activate();
                renderWindow.StartRenderingFromToolbar();
            }
            
            UpdateRenderingState(true);
        }
        
        private void StopRendering()
        {
            if (!isRendering) return;
            
            if (renderWindow != null && renderWindow.IsLoaded)
            {
                renderWindow.StopRenderingFromToolbar();
            }
            
            UpdateRenderingState(false);
        }
        
        private void UpdateRenderingState(bool rendering)
        {
            isRendering = rendering;
            PlayButton.IsEnabled = !rendering;
            StopButton.IsEnabled = rendering;
            ScreenshotButton.IsEnabled = rendering;
            
            StatusText.Text = rendering ? "レンダリング中..." : "準備完了";
        }
        
        private void SaveScreenshot()
        {
            if (renderWindow == null || !renderWindow.IsLoaded)
            {
                MessageBox.Show("レンダリングウィンドウが開いていません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
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
                    var bitmap = renderWindow.GetRenderBitmap();
                    if (bitmap != null)
                    {
                        BitmapEncoder encoder;
                        var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
                        
                        switch (ext)
                        {
                            case ".jpg":
                            case ".jpeg":
                                encoder = new JpegBitmapEncoder { QualityLevel = 95 };
                                break;
                            case ".bmp":
                                encoder = new BmpBitmapEncoder();
                                break;
                            default:
                                encoder = new PngBitmapEncoder();
                                break;
                        }
                        
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        
                        using (var stream = File.Create(dialog.FileName))
                        {
                            encoder.Save(stream);
                        }
                    }
                    else
                    {
                        MessageBox.Show("レンダリング画像を取得できませんでした。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ToggleLeftPanel_Click(object sender, RoutedEventArgs e)
        {
            bool isVisible = ToggleLeftPanelMenuItem.IsChecked;
            if (isVisible)
            {
                LeftPanelColumn.Width = new GridLength(250);
                LeftPanelBorder.Visibility = Visibility.Visible;
                LeftSplitter.Visibility = Visibility.Visible;
            }
            else
            {
                LeftPanelColumn.Width = new GridLength(0);
                LeftPanelBorder.Visibility = Visibility.Collapsed;
                LeftSplitter.Visibility = Visibility.Collapsed;
            }
        }

        private void ToggleRightPanel_Click(object sender, RoutedEventArgs e)
        {
            bool isVisible = ToggleRightPanelMenuItem.IsChecked;
            if (isVisible)
            {
                RightPanelColumn.Width = new GridLength(300);
                RightPanelBorder.Visibility = Visibility.Visible;
                RightSplitter.Visibility = Visibility.Visible;
            }
            else
            {
                RightPanelColumn.Width = new GridLength(0);
                RightPanelBorder.Visibility = Visibility.Collapsed;
                RightSplitter.Visibility = Visibility.Collapsed;
            }
        }
    }
}

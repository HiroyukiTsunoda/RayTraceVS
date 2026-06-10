using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
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
        private SettingsService settingsService;
        private bool isRendering = false;
        private bool isSavingScreenshot = false;

        // レンダリング解像度（デフォルトは1920x1080）
        private int renderWidth = 1920;
        private int renderHeight = 1080;

        public MainWindow()
        {
            InitializeComponent();
            viewModel = new MainViewModel();
            DataContext = viewModel;

            settingsService = new SettingsService();

            // ウィンドウの位置とサイズを復元
            RestoreWindowBounds();

            // 起動時に前回開いていたファイルを読み込む
            LoadLastScene();

            // シーンが読み込まれたら自動的にレンダリングを開始
            this.Loaded += MainWindow_Loaded;

            // ウィンドウが閉じる際に設定を保存
            this.Closing += MainWindow_Closing;

            // グローバルなキー入力を最優先で捕捉
            System.Windows.Input.InputManager.Current.PreProcessInput += OnPreProcessInput;
            this.Closed += MainWindow_Closed;
        }
        
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // UIのレンダリングが完全に完了してから接続線を更新
            Dispatcher.BeginInvoke(new Action(() =>
            {
                NodeEditor.RefreshConnectionLines();
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            // 起動直後にフォーカスを明示的に与える
            Activate();
            Focus();
            System.Windows.Input.Keyboard.Focus(this);
            
            // 解像度表示を初期化
            UpdateResolutionDisplay();
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
            if (!string.IsNullOrEmpty(viewModel?.CurrentFilePath))
            {
                settingsService.LastOpenedFilePath = viewModel.CurrentFilePath;
            }
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            System.Windows.Input.InputManager.Current.PreProcessInput -= OnPreProcessInput;
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

        /// <summary>
        /// IME有効時やSystemキー押下時の実キーを取得
        /// </summary>
        private static System.Windows.Input.Key GetRealKey(System.Windows.Input.KeyEventArgs e)
        {
            var key = e.Key;
            if (key == System.Windows.Input.Key.ImeProcessed)
                key = e.ImeProcessedKey;
            else if (key == System.Windows.Input.Key.System)
                key = e.SystemKey;
            return key;
        }

        /// <summary>
        /// PreviewKeyDown - 最優先でショートカットを処理（トンネリングイベント）
        /// </summary>
        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            HandleGlobalShortcuts(e);
        }

        private void OnPreProcessInput(object sender, System.Windows.Input.PreProcessInputEventArgs e)
        {
            if (!IsActive)
            {
                return;
            }

            if (e.StagingItem.Input is System.Windows.Input.KeyEventArgs keyEvent &&
                keyEvent.RoutedEvent == System.Windows.Input.Keyboard.PreviewKeyDownEvent)
            {
                HandleGlobalShortcuts(keyEvent);
            }
        }

        private void HandleGlobalShortcuts(System.Windows.Input.KeyEventArgs e)
        {
            var key = GetRealKey(e);
            var modifiers = System.Windows.Input.Keyboard.Modifiers;
            bool isCtrl = modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
            bool isShift = modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);
            bool isTextBoxFocused = System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox;

            // Ctrl+S: 保存（常に処理）
            if (isCtrl && !isShift && key == System.Windows.Input.Key.S)
            {
                SaveScene_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            // Ctrl+Shift+S: 名前を付けて保存（常に処理）
            if (isCtrl && isShift && key == System.Windows.Input.Key.S)
            {
                SaveSceneAs_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            // Ctrl+N: 新規シーン（常に処理）
            if (isCtrl && !isShift && key == System.Windows.Input.Key.N)
            {
                NewScene_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            // Ctrl+O: 開く（常に処理）
            if (isCtrl && !isShift && key == System.Windows.Input.Key.O)
            {
                OpenScene_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            // Ctrl+P: スクリーンショット クイック保存（ダイアログなし）
            if (isCtrl && !isShift && key == System.Windows.Input.Key.P)
            {
                SaveScreenshotQuick();
                e.Handled = true;
                return;
            }

            // Ctrl+Shift+P: スクリーンショット保存（ダイアログ表示）
            if (isCtrl && isShift && key == System.Windows.Input.Key.P)
            {
                SaveScreenshot();
                e.Handled = true;
                return;
            }

            // TextBoxにフォーカスがある場合、以下のショートカットはテキスト操作用にスルー
            if (isTextBoxFocused)
            {
                return;
            }

            // Ctrl+Z: Undo
            if (isCtrl && !isShift && key == System.Windows.Input.Key.Z)
            {
                ExecuteUndo();
                e.Handled = true;
                return;
            }

            // Ctrl+Shift+Z: Redo
            if (isCtrl && isShift && key == System.Windows.Input.Key.Z)
            {
                ExecuteRedo();
                e.Handled = true;
                return;
            }

            // Ctrl+C: コピー
            if (isCtrl && !isShift && key == System.Windows.Input.Key.C)
            {
                NodeEditor.CopySelectedNodes();
                e.Handled = true;
                return;
            }

            // Ctrl+V: ペースト
            if (isCtrl && !isShift && key == System.Windows.Input.Key.V)
            {
                NodeEditor.PasteNodes();
                e.Handled = true;
                return;
            }
        }

        private void ExecuteUndo()
        {
            if (viewModel != null && viewModel.CommandManager.CanUndo)
            {
                viewModel.CommandManager.Undo();
                NodeEditor.RefreshConnectionLines();
                NodeEditor.RefreshNodeTextBoxValues();
            }
        }

        private void ExecuteRedo()
        {
            if (viewModel != null && viewModel.CommandManager.CanRedo)
            {
                viewModel.CommandManager.Redo();
                NodeEditor.RefreshConnectionLines();
                NodeEditor.RefreshNodeTextBoxValues();
            }
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            var key = GetRealKey(e);
            bool isTextBoxFocused = System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox;

            // Deleteキーの処理（TextBox以外）
            if (!isTextBoxFocused && key == System.Windows.Input.Key.Delete)
            {
                NodeEditor.DeleteSelectedNodes();
                e.Handled = true;
                return;
            }
            
            // F5: レンダリング開始
            if (key == System.Windows.Input.Key.F5)
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
        }

        // メニュー用 Click ハンドラ
        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            ExecuteUndo();
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            ExecuteRedo();
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            NodeEditor.CopySelectedNodes();
        }

        private void Paste_Click(object sender, RoutedEventArgs e)
        {
            NodeEditor.PasteNodes();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            NodeEditor.DeleteSelectedNodes();
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
                    var sampleScenePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sample_scene.rtvs");
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
                // シーンデータの読み込み・置き換えはViewModelに委譲し、UI反映のみここで行う
                var (viewportState, removedNodeInfos) = viewModel.LoadScene(filePath,
                    applyViewportStateBeforeNodes: state => NodeEditor.SetViewportState(state));

                // パネルの開閉状態を復元（シーンファイルから）
                if (viewportState != null)
                {
                    SetPanelVisibility(viewportState.IsLeftPanelVisible, viewportState.IsRightPanelVisible);

                    // Expanderの開閉状態を復元
                    ComponentPalette.SetExpanderStates(viewportState.ExpanderStates);

                    // レンダリング解像度を復元
                    renderWidth = viewportState.RenderWidth;
                    renderHeight = viewportState.RenderHeight;
                    SetResolutionComboBox(renderWidth, renderHeight);
                }

                // UIのレンダリング完了後に接続線を更新
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    NodeEditor.RefreshConnectionLines();
                }), System.Windows.Threading.DispatcherPriority.Loaded);

                // キャッシュにないFBXノードが除外された場合は警告を表示
                if (removedNodeInfos.Count > 0)
                {
                    var message = "以下のノードはキャッシュにメッシュデータがないため除外されました：\n\n" +
                                  string.Join("\n", removedNodeInfos) +
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

                viewModel.NewScene();

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
                    settingsService.LastOpenedFilePath = viewModel?.CurrentFilePath;
                    
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
            if (string.IsNullOrEmpty(viewModel?.CurrentFilePath))
            {
                SaveSceneAs_Click(sender, e);
                return;
            }

            SaveSceneToFile(viewModel.CurrentFilePath);
        }

        private void SaveSceneAs_Click(object sender, RoutedEventArgs e)
        {
            // 名前を付けて保存
            var dialog = new SaveFileDialog
            {
                Filter = "RayTraceVSシーン|*.rtvs|すべてのファイル|*.*",
                DefaultExt = "rtvs",
                FileName = string.IsNullOrEmpty(viewModel?.CurrentFilePath)
                    ? "scene"
                    : System.IO.Path.GetFileNameWithoutExtension(viewModel.CurrentFilePath),
                Title = "名前を付けて保存"
            };

            if (dialog.ShowDialog() == true)
            {
                SaveSceneToFile(dialog.FileName);

                // 設定を更新
                settingsService.LastOpenedFilePath = viewModel?.CurrentFilePath;
            }
        }

        private void SaveSceneToFile(string filePath)
        {
            try
            {
                if (viewModel != null)
                {
                    // ViewportState（UI状態）の構築はViewの責務
                    var viewportState = NodeEditor.GetViewportState();

                    // パネルの開閉状態も保存（シーンファイルに）
                    viewportState.IsLeftPanelVisible = LeftPanelBorder.Visibility == Visibility.Visible;
                    viewportState.IsRightPanelVisible = RightPanelBorder.Visibility == Visibility.Visible;

                    // Expanderの開閉状態も保存
                    viewportState.ExpanderStates = ComponentPalette.GetExpanderStates();

                    // レンダリング解像度も保存
                    viewportState.RenderWidth = renderWidth;
                    viewportState.RenderHeight = renderHeight;

                    // 保存処理と状態更新（CurrentFilePath/HasUnsavedChanges）はViewModelに委譲
                    viewModel.SaveScene(filePath, viewportState);
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
        
        // 解像度選択のイベントハンドラ
        private void ResolutionComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ResolutionComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem)
            {
                var tag = selectedItem.Tag?.ToString();
                if (!string.IsNullOrEmpty(tag) && tag.Contains("x"))
                {
                    var parts = tag.Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int width) && int.TryParse(parts[1], out int height))
                    {
                        renderWidth = width;
                        renderHeight = height;
                        
                        // 右上の解像度表示を更新
                        UpdateResolutionDisplay();
                        
                        // レンダリング中の場合は既存のウィンドウを閉じて再起動が必要
                        // （ここでは変更をマークするだけ）
                        if (viewModel != null)
                        {
                            viewModel.HasUnsavedChanges = true;
                        }
                    }
                }
            }
        }
        
        // 解像度表示を更新
        private void UpdateResolutionDisplay()
        {
            if (ResolutionDisplayText != null)
            {
                ResolutionDisplayText.Text = $"{renderWidth} x {renderHeight}";
            }
        }
        
        // レンダリング時間の表示を更新
        private void UpdateRenderTimeDisplay(double milliseconds)
        {
            if (RenderTimeText != null)
            {
                RenderTimeText.Text = $"{milliseconds:F1} ms";
            }
        }
        
        // レンダリング時間の表示をクリア
        private void ClearRenderTimeDisplay()
        {
            if (RenderTimeText != null)
            {
                RenderTimeText.Text = "";
            }
        }
        
        // レンダリング完了イベントハンドラ
        private void OnRenderCompleted(double milliseconds)
        {
            // UIスレッドで実行
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateRenderTimeDisplay(milliseconds);
            }));
        }
        
        // 解像度に応じてComboBoxの選択を設定
        private void SetResolutionComboBox(int width, int height)
        {
            var targetTag = $"{width}x{height}";
            foreach (var item in ResolutionComboBox.Items)
            {
                if (item is System.Windows.Controls.ComboBoxItem comboBoxItem && 
                    comboBoxItem.Tag?.ToString() == targetTag)
                {
                    ResolutionComboBox.SelectedItem = comboBoxItem;
                    return;
                }
            }
            // マッチしない場合はデフォルトの1920x1080を選択
            ResolutionComboBox.SelectedIndex = 2;
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
            // Shiftキーを押しながらクリックでダイアログ表示、通常クリックはクイック保存
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                SaveScreenshot();
            }
            else
            {
                SaveScreenshotQuick();
            }
        }
        
        private void StartRendering()
        {
            if (isRendering) return;
            
            // レンダリングウィンドウを開く
            if (renderWindow == null || !renderWindow.IsLoaded)
            {
                renderWindow = new RenderWindow(renderWidth, renderHeight);
                
                if (viewModel != null)
                {
                    renderWindow.SetNodeGraph(viewModel.NodeGraph);
                }
                
                // レンダリング完了イベントを購読
                renderWindow.RenderCompleted += OnRenderCompleted;
                
                renderWindow.Closed += (s, args) => 
                {
                    UpdateRenderingState(false);
                    ClearRenderTimeDisplay();
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
        
        /// <summary>
        /// クイック保存: ダイアログなしで直接保存（Ctrl+P）
        /// </summary>
        private void SaveScreenshotQuick()
        {
            if (renderWindow == null || !renderWindow.IsLoaded)
            {
                MessageBox.Show("レンダリングウィンドウが開いていません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (isSavingScreenshot)
            {
                return;
            }

            isSavingScreenshot = true;
            try
            {
                // 保存先フォルダを決定
                var saveDir = settingsService.LastScreenshotFolder;
                if (string.IsNullOrWhiteSpace(saveDir) || !Directory.Exists(saveDir))
                {
                    saveDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                }
                if (string.IsNullOrWhiteSpace(saveDir) || !Directory.Exists(saveDir))
                {
                    saveDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                }

                // 自動ファイル名生成
                var fileName = $"render_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
                var filePath = Path.Combine(saveDir, fileName);

                var bitmap = renderWindow.GetRenderBitmapCopy();
                if (bitmap != null)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));

                    using (var stream = File.Create(filePath))
                    {
                        encoder.Save(stream);
                    }

                    // ステータスバーかタイトルで通知（MessageBoxは使わない）
                    // 注: ローカル値の設定でWindowTitleバインディングが外れるため、戻すときに再設定する
                    Title = $"RayTraceVS - 保存完了: {fileName}";

                    // 3秒後にタイトルを戻す
                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(3)
                    };
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        SetBinding(TitleProperty, new System.Windows.Data.Binding(nameof(MainViewModel.WindowTitle)));
                    };
                    timer.Start();
                }
                else
                {
                    MessageBox.Show("レンダリング画像を取得できませんでした。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                isSavingScreenshot = false;
            }
        }

        /// <summary>
        /// ダイアログ付き保存（Ctrl+Shift+P）
        /// </summary>
        private void SaveScreenshot()
        {
            if (renderWindow == null || !renderWindow.IsLoaded)
            {
                MessageBox.Show("レンダリングウィンドウが開いていません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (isSavingScreenshot)
            {
                return;
            }

            isSavingScreenshot = true;
            try
            {
                var initialDir = settingsService.LastScreenshotFolder;
                if (string.IsNullOrWhiteSpace(initialDir) || !Directory.Exists(initialDir))
                {
                    initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                }
                if (string.IsNullOrWhiteSpace(initialDir) || !Directory.Exists(initialDir))
                {
                    initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                }

                // シェル拡張による遅延を軽減するオプション
                var dialog = new SaveFileDialog
                {
                    Filter = "PNG画像|*.png|JPEG画像|*.jpg|ビットマップ|*.bmp",
                    DefaultExt = "png",
                    FileName = $"render_{DateTime.Now:yyyyMMdd_HHmmss_fff}",
                    InitialDirectory = initialDir,
                    RestoreDirectory = false,
                    DereferenceLinks = false,
                    AddExtension = true
                };

                if (dialog.ShowDialog() == true)
                {
                    var bitmap = renderWindow.GetRenderBitmapCopy();
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

                        var savedDir = Path.GetDirectoryName(dialog.FileName);
                        if (!string.IsNullOrEmpty(savedDir))
                        {
                            settingsService.LastScreenshotFolder = savedDir;
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
            finally
            {
                isSavingScreenshot = false;
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

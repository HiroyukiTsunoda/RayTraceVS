using System;
using System.Windows;
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

        public MainWindow()
        {
            InitializeComponent();
            viewModel = new MainViewModel();
            DataContext = viewModel;
            
            // 起動時にsample_scene.rtvsを自動的に読み込む
            LoadSampleScene();
            
            // サンプルシーンが読み込まれたら自動的にレンダリングを開始
            this.Loaded += MainWindow_Loaded;
        }
        
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // シーンが読み込まれている場合は自動的にレンダリングウィンドウを開く
            if (viewModel != null && viewModel.Nodes.Count > 0)
            {
                // 少し遅延させてから開く
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StartRendering_Click(this, new RoutedEventArgs());
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
        
        private void LoadSampleScene()
        {
            try
            {
                // ワークスペースルートのsample_scene.rtvsを探す
                var sampleScenePath = @"c:\git\RayTraceVS\sample_scene.rtvs";
                
                if (System.IO.File.Exists(sampleScenePath) && viewModel != null)
                {
                    var sceneService = new SceneFileService();
                    var (nodes, connections) = sceneService.LoadScene(sampleScenePath);
                    
                    viewModel.Nodes.Clear();
                    viewModel.Connections.Clear();
                    
                    foreach (var node in nodes)
                        viewModel.AddNode(node);
                    
                    foreach (var connection in connections)
                        viewModel.AddConnection(connection);
                    
                    currentFilePath = sampleScenePath;
                    Title = $"RayTraceVS - {System.IO.Path.GetFileName(currentFilePath)}";
                }
            }
            catch (System.Exception ex)
            {
                // 起動時のエラーはメッセージボックスで表示
                MessageBox.Show($"サンプルシーンの読み込みに失敗しました：{ex.Message}", 
                              "警告", 
                              MessageBoxButton.OK, 
                              MessageBoxImage.Warning);
            }
        }

        private void NewScene_Click(object sender, RoutedEventArgs e)
        {
            // 新規シーン作成
            if (viewModel != null)
            {
                var result = MessageBox.Show("現在のシーンを破棄して新規作成しますか？", 
                                           "確認", 
                                           MessageBoxButton.YesNo, 
                                           MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    viewModel.Nodes.Clear();
                    viewModel.Connections.Clear();
                    currentFilePath = null;
                    Title = "RayTraceVS - DirectX12 DXR Visual Raytracing";
                }
            }
        }

        private void OpenScene_Click(object sender, RoutedEventArgs e)
        {
            // シーン読み込み
            var dialog = new OpenFileDialog
            {
                Filter = "RayTraceVSシーン|*.rtvs|すべてのファイル|*.*",
                DefaultExt = "rtvs"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var sceneService = new SceneFileService();
                    var (nodes, connections) = sceneService.LoadScene(dialog.FileName);
                    
                    if (viewModel != null)
                    {
                        viewModel.Nodes.Clear();
                        viewModel.Connections.Clear();
                        
                        foreach (var node in nodes)
                            viewModel.AddNode(node);
                        
                        foreach (var connection in connections)
                            viewModel.AddConnection(connection);
                    }
                    
                    currentFilePath = dialog.FileName;
                    Title = $"RayTraceVS - {System.IO.Path.GetFileName(currentFilePath)}";
                    
                    MessageBox.Show("シーンを読み込みました。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show($"シーンの読み込みに失敗しました：{ex.Message}", 
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
                FileName = "scene"
            };

            if (dialog.ShowDialog() == true)
            {
                SaveSceneToFile(dialog.FileName);
                currentFilePath = dialog.FileName;
                Title = $"RayTraceVS - {System.IO.Path.GetFileName(currentFilePath)}";
            }
        }

        private void SaveSceneToFile(string filePath)
        {
            try
            {
                if (viewModel != null)
                {
                    var sceneService = new SceneFileService();
                    sceneService.SaveScene(filePath, viewModel.Nodes, viewModel.Connections);
                    MessageBox.Show("シーンを保存しました。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"シーンの保存に失敗しました：{ex.Message}", 
                              "エラー", 
                              MessageBoxButton.OK, 
                              MessageBoxImage.Error);
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void StartRendering_Click(object sender, RoutedEventArgs e)
        {
            // レンダリングウィンドウを開く
            if (renderWindow == null || !renderWindow.IsLoaded)
            {
                renderWindow = new RenderWindow();
                
                if (viewModel != null)
                {
                    renderWindow.SetNodeGraph(viewModel.NodeGraph);
                }
                
                renderWindow.Show();
            }
            else
            {
                renderWindow.Activate();
            }
        }

        private void StopRendering_Click(object sender, RoutedEventArgs e)
        {
            // レンダリングウィンドウを閉じる
            if (renderWindow != null && renderWindow.IsLoaded)
            {
                renderWindow.Close();
                renderWindow = null;
            }
        }
    }
}

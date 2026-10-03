using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RayTraceVS.WPF.Commands;
using RayTraceVS.WPF.ViewModels;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Nodes;
using RayTraceVS.WPF.Models.Serialization;
using RayTraceVS.WPF.Services;

namespace RayTraceVS.WPF.Views
{
    public partial class ComponentPaletteView : UserControl
    {
        private Random random = new Random();

        /// <summary>
        /// カテゴリの表示順・ヘッダー・ボタン背景ブラシの定義
        /// </summary>
        private static readonly (NodeCategory Category, string Header, string BrushKey)[] CategoryDefinitions =
        {
            (NodeCategory.Object,   "◆ オブジェクト", "NodeObjectBrush"),
            (NodeCategory.Material, "◆ マテリアル",   "NodeMaterialBrush"),
            (NodeCategory.Math,     "◆ 数学",         "NodeMathBrush"),
            (NodeCategory.Camera,   "◆ カメラ",       "NodeCameraBrush"),
            (NodeCategory.Light,    "◆ ライト",       "NodeLightBrush"),
            (NodeCategory.Scene,    "◆ シーン",       "NodeSceneBrush"),
        };

        private readonly Dictionary<NodeCategory, Expander> _categoryExpanders = new();
        private Expander _fbxExpander = null!;
        private StackPanel _fbxButtonsPanel = null!;

        public ComponentPaletteView()
        {
            InitializeComponent();
            BuildPalette();
            Loaded += ComponentPaletteView_Loaded;
        }

        private void ComponentPaletteView_Loaded(object sender, RoutedEventArgs e)
        {
            // FBXリストを初期化
            RefreshFBXList();
        }

        /// <summary>
        /// NodeRegistryの登録情報からパレットUI（カテゴリExpanderとノードボタン）を動的生成する。
        /// 新ノードはNodeRegistryへの登録だけでパレットに表示される。
        /// </summary>
        private void BuildPalette()
        {
            var registrationsByCategory = NodeRegistry.GetRegistrations()
                .Where(r => r.ShowInPalette)
                .GroupBy(r => r.Category)
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.SortOrder).ToList());

            bool isFirst = true;
            foreach (var (category, header, brushKey) in CategoryDefinitions)
            {
                var buttonsPanel = new StackPanel { Margin = new Thickness(10, 5, 10, 5) };
                if (registrationsByCategory.TryGetValue(category, out var registrations))
                {
                    foreach (var registration in registrations)
                    {
                        var button = new Button
                        {
                            Content = registration.DisplayName,
                            Tag = registration,
                            Margin = new Thickness(0, 2, 0, 2),
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            Background = FindResource(brushKey) as Brush
                        };
                        button.Click += AddNodeFromPalette_Click;
                        buttonsPanel.Children.Add(button);
                    }
                }

                var expander = CreateCategoryExpander(header, isFirst);
                expander.Content = buttonsPanel;
                _categoryExpanders[category] = expander;
                CategoriesPanel.Children.Add(expander);

                // FBXオブジェクトカテゴリ（メッシュキャッシュから動的生成）はオブジェクトの直後に配置
                if (category == NodeCategory.Object)
                {
                    _fbxButtonsPanel = new StackPanel { Margin = new Thickness(10, 5, 10, 5) };
                    _fbxExpander = CreateCategoryExpander("◆ FBXオブジェクト", isFirstCategory: false);
                    _fbxExpander.Content = _fbxButtonsPanel;
                    CategoriesPanel.Children.Add(_fbxExpander);
                }

                isFirst = false;
            }
        }

        private Expander CreateCategoryExpander(string header, bool isFirstCategory)
        {
            return new Expander
            {
                Header = header,
                IsExpanded = isFirstCategory,
                Margin = isFirstCategory ? new Thickness(0) : new Thickness(0, 5, 0, 5),
                Foreground = FindResource("TextBrush") as Brush,
                Background = FindResource("PanelBrush") as Brush
            };
        }

        /// <summary>
        /// パレットボタン共通のクリックハンドラ（Tagに登録メタデータを保持）
        /// </summary>
        private void AddNodeFromPalette_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is NodeRegistration registration)
            {
                AddNodeWithCommand(registration.Factory());
            }
        }

        /// <summary>
        /// FBXオブジェクトリストを更新
        /// </summary>
        public void RefreshFBXList()
        {
            _fbxButtonsPanel.Children.Clear();

            var meshCacheService = App.MeshCacheService;
            if (meshCacheService == null) return;

            foreach (var meshName in meshCacheService.AvailableMeshes)
            {
                var button = new Button
                {
                    Content = meshName,
                    Tag = meshName,
                    Margin = new Thickness(0, 2, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Background = FindResource("NodeObjectBrush") as Brush
                };
                button.Click += AddFBXMesh_Click;
                _fbxButtonsPanel.Children.Add(button);
            }

            // メッシュがない場合はメッセージを表示
            if (meshCacheService.AvailableMeshes.Count == 0)
            {
                var textBlock = new TextBlock
                {
                    Text = "FBXファイルがありません\nResource/Modelに配置してください",
                    Foreground = FindResource("TextSecondaryBrush") as Brush,
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap
                };
                _fbxButtonsPanel.Children.Add(textBlock);
            }
        }

        private void AddFBXMesh_Click(object sender, RoutedEventArgs e)
        {
            var meshName = (sender as Button)?.Tag as string;
            if (string.IsNullOrEmpty(meshName)) return;

            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new FBXMeshNode(meshName);
                ((Node)node).Position = GetViewportCenterPosition();
                viewModel.AddNode(node);
            }
        }

        private MainViewModel? GetViewModel()
        {
            return Window.GetWindow(this)?.DataContext as MainViewModel;
        }

        /// <summary>
        /// NodeEditorViewを取得
        /// </summary>
        private NodeEditorView? GetNodeEditor()
        {
            var mainWindow = Window.GetWindow(this) as MainWindow;
            return mainWindow?.FindName("NodeEditor") as NodeEditorView;
        }

        /// <summary>
        /// Expanderの開閉状態を取得
        /// </summary>
        public ExpanderStates GetExpanderStates()
        {
            return new ExpanderStates
            {
                IsObjectExpanded = _categoryExpanders[NodeCategory.Object].IsExpanded,
                IsFBXObjectExpanded = _fbxExpander.IsExpanded,
                IsMaterialExpanded = _categoryExpanders[NodeCategory.Material].IsExpanded,
                IsMathExpanded = _categoryExpanders[NodeCategory.Math].IsExpanded,
                IsCameraExpanded = _categoryExpanders[NodeCategory.Camera].IsExpanded,
                IsLightExpanded = _categoryExpanders[NodeCategory.Light].IsExpanded,
                IsSceneExpanded = _categoryExpanders[NodeCategory.Scene].IsExpanded
            };
        }

        /// <summary>
        /// Expanderの開閉状態を設定
        /// </summary>
        public void SetExpanderStates(ExpanderStates? states)
        {
            if (states == null) return;

            _categoryExpanders[NodeCategory.Object].IsExpanded = states.IsObjectExpanded;
            _fbxExpander.IsExpanded = states.IsFBXObjectExpanded;
            _categoryExpanders[NodeCategory.Material].IsExpanded = states.IsMaterialExpanded;
            _categoryExpanders[NodeCategory.Math].IsExpanded = states.IsMathExpanded;
            _categoryExpanders[NodeCategory.Camera].IsExpanded = states.IsCameraExpanded;
            _categoryExpanders[NodeCategory.Light].IsExpanded = states.IsLightExpanded;
            _categoryExpanders[NodeCategory.Scene].IsExpanded = states.IsSceneExpanded;
        }

        private Point GetViewportCenterPosition()
        {
            // ノードエディタのビューポート中央にランダムなオフセットを加えて配置
            var nodeEditor = GetNodeEditor();
            if (nodeEditor != null)
            {
                var center = nodeEditor.GetViewportCenterInCanvas();
                return new Point(
                    center.X + random.Next(-50, 50),
                    center.Y + random.Next(-50, 50)
                );
            }

            // フォールバック: デフォルト位置
            return new Point(
                400 + random.Next(-100, 100),
                300 + random.Next(-100, 100)
            );
        }

        /// <summary>
        /// ノードを追加し、コマンド履歴に登録
        /// </summary>
        private void AddNodeWithCommand(Node node)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                node.Position = GetViewportCenterPosition();
                viewModel.CommandManager.Execute(new AddNodeCommand(viewModel, node));
            }
        }
    }
}

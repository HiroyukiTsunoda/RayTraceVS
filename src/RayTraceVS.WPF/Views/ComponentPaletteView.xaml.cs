using System;
using System.Windows;
using System.Windows.Controls;
using RayTraceVS.WPF.Commands;
using RayTraceVS.WPF.ViewModels;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Nodes;
using RayTraceVS.WPF.Services;

namespace RayTraceVS.WPF.Views
{
    public partial class ComponentPaletteView : UserControl
    {
        private Random random = new Random();

        public ComponentPaletteView()
        {
            InitializeComponent();
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
                IsObjectExpanded = ObjectExpander.IsExpanded,
                IsMaterialExpanded = MaterialExpander.IsExpanded,
                IsMathExpanded = MathExpander.IsExpanded,
                IsCameraExpanded = CameraExpander.IsExpanded,
                IsLightExpanded = LightExpander.IsExpanded,
                IsSceneExpanded = SceneExpander.IsExpanded
            };
        }
        
        /// <summary>
        /// Expanderの開閉状態を設定
        /// </summary>
        public void SetExpanderStates(ExpanderStates? states)
        {
            if (states == null) return;
            
            ObjectExpander.IsExpanded = states.IsObjectExpanded;
            MaterialExpander.IsExpanded = states.IsMaterialExpanded;
            MathExpander.IsExpanded = states.IsMathExpanded;
            CameraExpander.IsExpanded = states.IsCameraExpanded;
            LightExpander.IsExpanded = states.IsLightExpanded;
            SceneExpander.IsExpanded = states.IsSceneExpanded;
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

        private void AddSphere_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new SphereNode());
        }

        private void AddPlane_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new PlaneNode());
        }

        private void AddBox_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new BoxNode());
        }

        // マテリアルノード追加ハンドラ
        private void AddColor_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new ColorNode());
        }

        private void AddEmission_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new EmissionMaterialNode());
        }

        private void AddMaterialBSDF_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new MaterialBSDFNode());
        }

        private void AddUniversalPBR_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new UniversalPBRNode());
        }

        private void AddFloat_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new FloatNode());
        }

        private void AddVector3_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new Vector3Node());
        }

        private void AddVector4_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new Vector4Node());
        }

        private void AddMath_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new AddNode());
        }

        private void AddSub_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new SubNode());
        }

        private void AddMultiply_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new MulNode());
        }

        private void AddDiv_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new DivNode());
        }

        private void AddTransform_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new TransformNode());
        }

        private void AddCombineTransform_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new CombineTransformNode());
        }

        private void AddCamera_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new CameraNode());
        }

        private void AddAmbientLight_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new AmbientLightNode());
        }

        private void AddDirectionalLight_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new DirectionalLightNode());
        }

        private void AddLight_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new PointLightNode());
        }

        private void AddScene_Click(object sender, RoutedEventArgs e)
        {
            AddNodeWithCommand(new SceneNode());
        }
    }
}

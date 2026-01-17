using System;
using System.Windows;
using System.Windows.Controls;
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

        private Point GetRandomPosition()
        {
            // ノードエディタの中央付近にランダムに配置
            return new Point(
                400 + random.Next(-100, 100),
                300 + random.Next(-100, 100)
            );
        }

        private void AddSphere_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new SphereNode();
                ((Node)node).Position = GetRandomPosition();
                viewModel.AddNode(node);
            }
        }

        private void AddPlane_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new PlaneNode();
                ((Node)node).Position = GetRandomPosition();
                viewModel.AddNode(node);
            }
        }

        private void AddBox_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new BoxNode();
                ((Node)node).Position = GetRandomPosition();
                viewModel.AddNode(node);
            }
        }

        // マテリアルノード追加ハンドラ
        private void AddColor_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new ColorNode { Position = GetRandomPosition() };
                viewModel.AddNode(node);
            }
        }

        private void AddDiffuse_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new DiffuseMaterialNode { Position = GetRandomPosition() };
                viewModel.AddNode(node);
            }
        }

        private void AddMetal_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new MetalMaterialNode { Position = GetRandomPosition() };
                viewModel.AddNode(node);
            }
        }

        private void AddGlass_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new GlassMaterialNode { Position = GetRandomPosition() };
                viewModel.AddNode(node);
            }
        }

        private void AddEmission_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new EmissionMaterialNode { Position = GetRandomPosition() };
                viewModel.AddNode(node);
            }
        }

        private void AddMaterialBSDF_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new MaterialBSDFNode { Position = GetRandomPosition() };
                viewModel.AddNode(node);
            }
        }

        private void AddFloat_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new FloatNode { Position = GetRandomPosition() };
                viewModel.AddNode(node);
            }
        }

        private void AddVector3_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new Vector3Node { Position = GetRandomPosition() };
                viewModel.AddNode(node);
            }
        }

        private void AddVector4_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new Vector4Node { Position = GetRandomPosition() };
                viewModel.AddNode(node);
            }
        }

        private void AddMath_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new AddNode { Position = GetRandomPosition() };
                viewModel.AddNode(node);
            }
        }

        private void AddSub_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new SubNode { Position = GetRandomPosition() };
                viewModel.AddNode(node);
            }
        }

        private void AddMultiply_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new MulNode { Position = GetRandomPosition() };
                viewModel.AddNode(node);
            }
        }

        private void AddDiv_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new DivNode { Position = GetRandomPosition() };
                viewModel.AddNode(node);
            }
        }

        private void AddTransform_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new TransformNode { Position = GetRandomPosition() };
                viewModel.AddNode(node);
            }
        }

        private void AddCombineTransform_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new CombineTransformNode { Position = GetRandomPosition() };
                viewModel.AddNode(node);
            }
        }

        private void AddCamera_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new CameraNode();
                ((Node)node).Position = GetRandomPosition();
                viewModel.AddNode(node);
            }
        }

        private void AddAmbientLight_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new AmbientLightNode();
                ((Node)node).Position = GetRandomPosition();
                viewModel.AddNode(node);
            }
        }

        private void AddDirectionalLight_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new DirectionalLightNode();
                ((Node)node).Position = GetRandomPosition();
                viewModel.AddNode(node);
            }
        }

        private void AddLight_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new PointLightNode();
                ((Node)node).Position = GetRandomPosition();
                viewModel.AddNode(node);
            }
        }

        private void AddScene_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new SceneNode { Position = GetRandomPosition() };
                viewModel.AddNode(node);
            }
        }
    }
}

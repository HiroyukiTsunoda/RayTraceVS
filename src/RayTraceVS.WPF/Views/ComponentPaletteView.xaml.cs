using System;
using System.Windows;
using System.Windows.Controls;
using RayTraceVS.WPF.ViewModels;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Nodes;

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

        private void AddCylinder_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new CylinderNode();
                ((Node)node).Position = GetRandomPosition();
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

        private void AddMath_Click(object sender, RoutedEventArgs e)
        {
            // 加算ノードを追加（加算ノードクラスが実装されたら有効化）
            MessageBox.Show("加算ノードは実装予定です", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void AddMultiply_Click(object sender, RoutedEventArgs e)
        {
            // 乗算ノードを追加（乗算ノードクラスが実装されたら有効化）
            MessageBox.Show("乗算ノードは実装予定です", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
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

        private void AddLight_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel != null)
            {
                var node = new LightNode();
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

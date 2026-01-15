using System.Windows;
using System.Windows.Controls;
using RayTraceVS.WPF.ViewModels;
using RayTraceVS.WPF.Models;
using System.Diagnostics;

namespace RayTraceVS.WPF.Views
{
    public partial class PropertyPanelView : UserControl
    {
        public PropertyPanelView()
        {
            InitializeComponent();
            
            // DataContextの変更を監視
            DataContextChanged += (s, e) =>
            {
                Debug.WriteLine($"PropertyPanelView DataContext changed: {e.NewValue?.GetType().Name}");
            };
        }

        private MainViewModel? GetViewModel()
        {
            return DataContext as MainViewModel;
        }

        private void DeleteNode_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel?.SelectedNode != null)
            {
                var result = MessageBox.Show(
                    $"ノード '{viewModel.SelectedNode.Title}' を削除しますか？",
                    "確認",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    var nodeToDelete = viewModel.SelectedNode;
                    viewModel.SelectedNode = null;
                    viewModel.RemoveNode(nodeToDelete);
                }
            }
        }
    }
}

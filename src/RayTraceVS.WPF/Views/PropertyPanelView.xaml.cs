using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using RayTraceVS.WPF.ViewModels;
using RayTraceVS.WPF.Models;

namespace RayTraceVS.WPF.Views
{
    public partial class PropertyPanelView : UserControl
    {
        public PropertyPanelView()
        {
            InitializeComponent();
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

        /// <summary>
        /// TextBoxでEnterキーを押した時にバインディングを即座に更新
        /// </summary>
        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox textBox)
            {
                var binding = BindingOperations.GetBindingExpression(textBox, TextBox.TextProperty);
                binding?.UpdateSource();
                e.Handled = true;
                
                // フォーカスを移動して入力完了を視覚的に示す
                Keyboard.ClearFocus();
            }
        }

        /// <summary>
        /// TextBoxからフォーカスが外れる前にバインディングを更新
        /// </summary>
        private void TextBox_PreviewLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                var binding = BindingOperations.GetBindingExpression(textBox, TextBox.TextProperty);
                binding?.UpdateSource();
            }
        }
    }
}

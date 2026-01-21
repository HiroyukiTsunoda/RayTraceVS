using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
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
        /// TextBoxでEnterキーまたはTabキーを押した時にバインディングを即座に更新
        /// </summary>
        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                if (e.Key == Key.Enter)
                {
                    // バインディングを更新
                    var binding = BindingOperations.GetBindingExpression(textBox, TextBox.TextProperty);
                    binding?.UpdateSource();
                    
                    // 次のTextBoxにフォーカスを移動
                    MoveToNextTextBox(textBox, forward: true);
                    e.Handled = true;
                }
                else if (e.Key == Key.Tab)
                {
                    // バインディングを更新
                    var binding = BindingOperations.GetBindingExpression(textBox, TextBox.TextProperty);
                    binding?.UpdateSource();
                    
                    // Shift+Tabで前へ、Tabで次へ
                    bool forward = !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                    MoveToNextTextBox(textBox, forward);
                    e.Handled = true;
                }
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

        /// <summary>
        /// プロパティパネル内の次（または前）のTextBoxにフォーカスを移動
        /// 一番下で次を押すと一番上に戻る（循環移動）
        /// </summary>
        private void MoveToNextTextBox(TextBox currentTextBox, bool forward)
        {
            // プロパティパネル内のすべてのTextBoxを取得
            var allTextBoxes = FindVisualChildren<TextBox>(this)
                .Where(tb => tb.IsVisible && tb.IsEnabled)
                .ToList();

            if (allTextBoxes.Count == 0)
            {
                return;
            }

            // 現在のTextBoxのインデックスを取得
            int currentIndex = allTextBoxes.IndexOf(currentTextBox);
            if (currentIndex < 0)
            {
                // 見つからない場合は最初のTextBoxにフォーカス
                if (allTextBoxes.Count > 0)
                {
                    allTextBoxes[0].Focus();
                    allTextBoxes[0].SelectAll();
                }
                return;
            }

            // 次（または前）のインデックスを計算（循環）
            int nextIndex;
            if (forward)
            {
                nextIndex = (currentIndex + 1) % allTextBoxes.Count;
            }
            else
            {
                nextIndex = (currentIndex - 1 + allTextBoxes.Count) % allTextBoxes.Count;
            }

            // 次のTextBoxにフォーカスを設定
            var nextTextBox = allTextBoxes[nextIndex];
            nextTextBox.Focus();
            nextTextBox.SelectAll();
        }

        /// <summary>
        /// ビジュアルツリーから指定された型の子要素をすべて取得
        /// </summary>
        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    yield return typedChild;
                }

                foreach (var descendant in FindVisualChildren<T>(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}

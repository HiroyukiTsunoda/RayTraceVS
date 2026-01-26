using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RayTraceVS.WPF.Commands;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.ViewModels;

namespace RayTraceVS.WPF.Views.Handlers
{
    /// <summary>
    /// ノードエディタのTextBox入力処理を担当するハンドラ
    /// Float/Vector3/Vector4/Color の共通入力処理を提供
    /// </summary>
    public class TextBoxInputHandler
    {
        private readonly EditorInputState _state;
        
        /// <summary>
        /// フォーカスをクリアしてキャンバスにフォーカスを移すコールバック
        /// </summary>
        public Action? ClearFocusToCanvas { get; set; }
        
        /// <summary>
        /// ViewModelを取得するコールバック
        /// </summary>
        public Func<MainViewModel?>? GetViewModel { get; set; }

        public TextBoxInputHandler(EditorInputState state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        /// <summary>
        /// 浮動小数点数の入力が有効かどうかチェック
        /// </summary>
        public bool IsValidFloatInput(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            if (text == "-" || text == "." || text == "-." || text == "-.") return true;
            
            return float.TryParse(text, out _);
        }
        
        /// <summary>
        /// 浮動小数点数の PreviewTextInput を処理
        /// </summary>
        /// <param name="textBox">対象のTextBox</param>
        /// <param name="e">イベント引数</param>
        public void HandleFloatPreviewTextInput(TextBox textBox, TextCompositionEventArgs e)
        {
            string input = e.Text;
            string currentText = textBox.Text;
            int selectionStart = textBox.SelectionStart;
            int selectionLength = textBox.SelectionLength;
            string newText = currentText.Substring(0, selectionStart) + input + 
                            currentText.Substring(selectionStart + selectionLength);
            
            e.Handled = !IsValidFloatInput(newText);
        }
        
        /// <summary>
        /// TextBoxの値が有効な浮動小数点数かどうか確認し、float値を返す
        /// </summary>
        public bool TryParseFloat(string text, out float value)
        {
            // 空または無効な場合
            if (string.IsNullOrWhiteSpace(text) || text == "-" || text == ".")
            {
                value = 0f;
                return false;
            }
            
            return float.TryParse(text, out value);
        }
        
        /// <summary>
        /// ノード内の次のTextBoxにフォーカスを移動
        /// </summary>
        /// <param name="currentTextBox">現在のTextBox</param>
        /// <param name="forward">true: 次へ, false: 前へ</param>
        public void MoveToNextTextBoxInNode(TextBox currentTextBox, bool forward = true)
        {
            // 親のノードコンテナを探す
            var nodeContainer = FindParentNodeContainer(currentTextBox);
            if (nodeContainer == null)
            {
                ClearFocusToCanvas?.Invoke();
                return;
            }
            
            // ノードコンテナ内のすべての有効なTextBoxを取得
            var textBoxes = FindVisualChildren<TextBox>(nodeContainer)
                .Where(tb => tb.IsVisible && tb.IsEnabled)
                .ToList();
            
            if (textBoxes.Count <= 1)
            {
                // 1つ以下なら移動先がないのでフォーカス解除
                ClearFocusToCanvas?.Invoke();
                return;
            }
            
            // 現在のTextBoxのインデックスを取得
            int currentIndex = textBoxes.IndexOf(currentTextBox);
            if (currentIndex < 0)
            {
                ClearFocusToCanvas?.Invoke();
                return;
            }
            
            // 次（または前）のインデックスを計算（ループ）
            int nextIndex;
            if (forward)
            {
                nextIndex = (currentIndex + 1) % textBoxes.Count;
            }
            else
            {
                nextIndex = (currentIndex - 1 + textBoxes.Count) % textBoxes.Count;
            }
            
            // 次のTextBoxにフォーカス
            var nextTextBox = textBoxes[nextIndex];
            nextTextBox.Focus();
            nextTextBox.SelectAll();
        }
        
        /// <summary>
        /// 親のノードコンテナ（Border）を探す
        /// </summary>
        private FrameworkElement? FindParentNodeContainer(DependencyObject child)
        {
            DependencyObject parent = VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                // ノードのコンテナはBorderでDataContextがNode
                if (parent is Border border && border.DataContext is Node)
                {
                    return border;
                }
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
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
        
        /// <summary>
        /// プロパティ変更のUndoコマンドを登録
        /// </summary>
        public void RegisterPropertyChangeCommand<T>(object target, string propertyName, T oldValue, T newValue, string description)
        {
            var viewModel = GetViewModel?.Invoke();
            if (viewModel == null) return;
            
            if (EqualityComparer<T>.Default.Equals(oldValue, newValue))
                return;
                
            var command = new ChangePropertyCommand<T>(target, propertyName, oldValue, newValue, description);
            viewModel.CommandManager.RegisterExecuted(command);
        }
    }
}

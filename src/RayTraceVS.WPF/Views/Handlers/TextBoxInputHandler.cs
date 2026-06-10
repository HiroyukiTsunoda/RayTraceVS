using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RayTraceVS.WPF.Commands;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Nodes;
using RayTraceVS.WPF.ViewModels;

namespace RayTraceVS.WPF.Views.Handlers
{
    /// <summary>
    /// ノードエディタのTextBox入力処理を担当するハンドラ
    /// Float/Vector3/Vector4/Color の共通入力処理を提供
    /// </summary>
    public class TextBoxInputHandler
    {
        // パターン: オプションのマイナス、数字、オプションの小数点、オプションの数字
        private static readonly Regex FloatInputRegex = new Regex(@"^-?(\d*\.?\d*)$", RegexOptions.Compiled);

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

        // ==================================================================
        // 入力バリデーション（Float共通）
        // ==================================================================

        /// <summary>
        /// 文字列が有効なfloat入力かどうかをチェック。
        /// 入力途中も許可するパターン（マイナス記号のみ、小数点で終わるなど）。
        /// </summary>
        public bool IsValidFloatInput(string text)
        {
            if (string.IsNullOrEmpty(text))
                return true;

            return FloatInputRegex.IsMatch(text);
        }

        /// <summary>
        /// 浮動小数点数の PreviewTextInput を処理（全型共通）
        /// </summary>
        public void HandlePreviewTextInput(TextBox textBox, TextCompositionEventArgs e)
        {
            string input = e.Text;
            string currentText = textBox.Text;
            int selectionStart = textBox.SelectionStart;
            int selectionLength = textBox.SelectionLength;
            string newText = currentText.Substring(0, selectionStart) + input +
                            currentText.Substring(selectionStart + selectionLength);

            e.Handled = !IsValidFloatInput(newText);
        }

        // ==================================================================
        // ソケットベースのTextBox処理（Vector3 / Vector4 / Color 共通）
        // ==================================================================

        /// <summary>
        /// ソケットベースのTextBoxがロードされたとき、初期値を設定
        /// </summary>
        public void HandleSocketTextBox_Loaded(TextBox textBox)
        {
            if (textBox?.Tag is NodeSocket socket && socket.ParentNode is ISocketValueNode svNode)
            {
                float value = svNode.GetSocketValue(socket.Name);
                textBox.Text = value.ToString("G");
            }
        }

        /// <summary>
        /// ソケットベースのTextBoxのKeyDown処理（Enter / Tab / Escape）
        /// </summary>
        /// <param name="textBox">対象のTextBox</param>
        /// <param name="e">キーイベント引数</param>
        /// <param name="supportsUndo">Undo/Redo登録をサポートするか（ColorNodeではfalse）</param>
        public void HandleSocketTextBox_KeyDown(TextBox textBox, KeyEventArgs e, bool supportsUndo)
        {
            if (e.Key == Key.Enter)
            {
                ApplySocketTextBoxValue(textBox, supportsUndo);
                ClearFocusToCanvas?.Invoke();
                e.Handled = true;
            }
            else if (e.Key == Key.Tab)
            {
                ApplySocketTextBoxValue(textBox, supportsUndo);
                MoveToNextTextBoxInNode(textBox, !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (textBox.Tag is NodeSocket socket && socket.ParentNode is ISocketValueNode svNode)
                {
                    if (supportsUndo)
                    {
                        // 変更を破棄（Undo用の値も削除）
                        _state.TextBoxOriginalValues.Remove(textBox);
                    }

                    // 元の値に戻す
                    textBox.Text = svNode.GetSocketValue(socket.Name).ToString("G");
                    ClearFocusToCanvas?.Invoke();
                }
                e.Handled = true;
            }
        }

        /// <summary>
        /// ソケットベースのTextBoxのLostFocus処理
        /// </summary>
        public void HandleSocketTextBox_LostFocus(TextBox textBox, bool supportsUndo)
        {
            ApplySocketTextBoxValue(textBox, supportsUndo);
        }

        /// <summary>
        /// ソケットベースのTextBoxのGotFocus処理（全選択 + Undo用の値記録）
        /// </summary>
        /// <param name="textBox">対象のTextBox</param>
        /// <param name="supportsUndo">Undo/Redo登録をサポートするか</param>
        public void HandleSocketTextBox_GotFocus(TextBox textBox, bool supportsUndo)
        {
            if (supportsUndo)
            {
                // 変更前の値を記録（Undo用）
                if (textBox.Tag is NodeSocket socket && socket.ParentNode is ISocketValueNode svNode)
                {
                    _state.TextBoxOriginalValues[textBox] = svNode.GetSocketValue(socket.Name);
                }
            }

            textBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                textBox.SelectAll();
            }), DispatcherPriority.Input);
        }

        /// <summary>
        /// ソケットベースのTextBoxの値を適用する共通メソッド。
        /// Vector3Node / Vector4Node / ColorNode で共有。
        /// </summary>
        /// <param name="textBox">対象のTextBox</param>
        /// <param name="supportsUndo">Undo/Redo登録をサポートするか</param>
        public void ApplySocketTextBoxValue(TextBox textBox, bool supportsUndo)
        {
            if (textBox.Tag is NodeSocket socket && socket.ParentNode is ISocketValueNode svNode)
            {
                var node = (Node)socket.ParentNode;

                // 空または無効な場合は現在の値を維持
                if (string.IsNullOrWhiteSpace(textBox.Text) || textBox.Text == "-" || textBox.Text == ".")
                {
                    textBox.Text = svNode.GetSocketValue(socket.Name).ToString("G");
                    if (supportsUndo)
                    {
                        _state.TextBoxOriginalValues.Remove(textBox);
                    }
                    return;
                }

                if (float.TryParse(textBox.Text, out float newValue))
                {
                    if (supportsUndo)
                    {
                        // 変更前の値を取得
                        float oldValue = svNode.GetSocketValue(socket.Name);
                        if (_state.TextBoxOriginalValues.TryGetValue(textBox, out float originalValue))
                        {
                            oldValue = originalValue;
                            _state.TextBoxOriginalValues.Remove(textBox);
                        }

                        // 値が変更された場合のみコマンドを発行
                        if (oldValue != newValue)
                        {
                            var viewModel = GetViewModel?.Invoke();
                            if (viewModel != null)
                            {
                                // 値を設定してからコマンドを登録
                                svNode.SetSocketValue(socket.Name, newValue);

                                // ノード型名を取得してUndoの説明文を生成
                                string typeName = node switch
                                {
                                    Vector3Node => "Vector3",
                                    Vector4Node => "Vector4",
                                    _ => node.GetType().Name.Replace("Node", "")
                                };
                                string propertyName = socket.Name;

                                viewModel.CommandManager.RegisterExecuted(
                                    new ChangePropertyCommand<float>(node, propertyName, oldValue, newValue,
                                        $"{typeName}.{propertyName} を変更"));
                            }
                            else
                            {
                                svNode.SetSocketValue(socket.Name, newValue);
                            }
                        }
                    }
                    else
                    {
                        // Undoなし: 値を直接設定
                        svNode.SetSocketValue(socket.Name, newValue);
                    }

                    textBox.Text = newValue.ToString("G");
                }
                else
                {
                    // パース失敗時は現在の値に戻す
                    textBox.Text = svNode.GetSocketValue(socket.Name).ToString("G");
                    if (supportsUndo)
                    {
                        _state.TextBoxOriginalValues.Remove(textBox);
                    }
                }
            }
        }

        // ==================================================================
        // FloatNode 用のTextBox処理
        // ==================================================================

        /// <summary>
        /// FloatNode TextBoxのKeyDown処理（Enter / Tab で確定、Escape で破棄）
        /// </summary>
        public void HandleFloatTextBox_KeyDown(TextBox textBox, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                // 変更前の値を取得
                float? oldValue = null;
                if (_state.TextBoxOriginalValues.TryGetValue(textBox, out float originalValue))
                {
                    oldValue = originalValue;
                    _state.TextBoxOriginalValues.Remove(textBox);
                }

                // バインディングを強制更新
                var bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty);
                bindingExpression?.UpdateSource();

                // FloatNodeの場合、Undo/Redoコマンドを発行
                if (oldValue.HasValue && textBox.DataContext is FloatNode floatNode)
                {
                    float newValue = floatNode.Value;
                    if (oldValue.Value != newValue)
                    {
                        var viewModel = GetViewModel?.Invoke();
                        viewModel?.CommandManager.RegisterExecuted(
                            new ChangePropertyCommand<float>(floatNode, "Value", oldValue.Value, newValue,
                                "Float値を変更"));
                    }
                }

                // フォーカスを外す
                ClearFocusToCanvas?.Invoke();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                // 変更を破棄（Undo用の値も削除）
                _state.TextBoxOriginalValues.Remove(textBox);

                // バインディングをリセット（元の値に戻す）
                var bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty);
                bindingExpression?.UpdateTarget();

                // フォーカスを外す
                ClearFocusToCanvas?.Invoke();
                e.Handled = true;
            }
        }

        /// <summary>
        /// FloatNode TextBoxのLostFocus処理（値の確定）
        /// </summary>
        public void ApplyFloatTextBoxValue(TextBox textBox)
        {
            // 空の場合は0に設定
            if (string.IsNullOrWhiteSpace(textBox.Text) || textBox.Text == "-" || textBox.Text == ".")
            {
                textBox.Text = "0";
            }

            // 変更前の値を取得
            float? oldValue = null;
            if (_state.TextBoxOriginalValues.TryGetValue(textBox, out float originalValue))
            {
                oldValue = originalValue;
                _state.TextBoxOriginalValues.Remove(textBox);
            }

            // バインディングを強制更新
            var bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty);
            bindingExpression?.UpdateSource();

            // FloatNodeの場合、Undo/Redoコマンドを発行
            if (oldValue.HasValue && textBox.DataContext is FloatNode floatNode)
            {
                float newValue = floatNode.Value;
                if (oldValue.Value != newValue)
                {
                    var viewModel = GetViewModel?.Invoke();
                    viewModel?.CommandManager.RegisterExecuted(
                        new ChangePropertyCommand<float>(floatNode, "Value", oldValue.Value, newValue,
                            "Float値を変更"));
                }
            }
        }

        /// <summary>
        /// FloatNode TextBoxのGotFocus処理（全選択 + Undo用の値記録）
        /// </summary>
        public void HandleFloatTextBox_GotFocus(TextBox textBox)
        {
            // 変更前の値を記録（Undo用）
            if (textBox.DataContext is FloatNode floatNode)
            {
                _state.TextBoxOriginalValues[textBox] = floatNode.Value;
            }

            textBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                textBox.SelectAll();
            }), DispatcherPriority.Input);
        }

        // ==================================================================
        // ユーティリティ
        // ==================================================================

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
        public static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
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

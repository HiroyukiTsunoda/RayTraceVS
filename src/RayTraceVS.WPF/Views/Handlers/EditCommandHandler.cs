using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using RayTraceVS.WPF.Commands;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.ViewModels;

namespace RayTraceVS.WPF.Views.Handlers
{
    /// <summary>
    /// ノードエディタの編集コマンド（Delete/Copy/Paste）を担当するハンドラ
    /// </summary>
    public class EditCommandHandler
    {
        private readonly EditorInputState _state;
        
        /// <summary>
        /// ViewModelを取得するコールバック
        /// </summary>
        public Func<MainViewModel?>? GetViewModel { get; set; }
        
        /// <summary>
        /// 選択をクリアするコールバック
        /// </summary>
        public Action<MainViewModel>? ClearSelections { get; set; }
        
        /// <summary>
        /// コピー処理を実行するコールバック（実際の処理は呼び出し元に委譲）
        /// </summary>
        public Action? PerformCopy { get; set; }
        
        /// <summary>
        /// ペースト処理を実行するコールバック（実際の処理は呼び出し元に委譲）
        /// </summary>
        public Action? PerformPaste { get; set; }

        public EditCommandHandler(EditorInputState state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        /// <summary>
        /// Deleteキーの処理
        /// </summary>
        /// <param name="e">キーイベント引数</param>
        /// <returns>処理が行われた場合はtrue</returns>
        public bool HandleDeleteKey(KeyEventArgs e)
        {
            if (e.Key == Key.Delete && _state.SelectedNodes.Count > 0)
            {
                DeleteSelectedNodes();
                e.Handled = true;
                return true;
            }
            return false;
        }
        
        /// <summary>
        /// キーボードショートカットの処理（Ctrl+C/V など）
        /// </summary>
        /// <param name="e">キーイベント引数</param>
        /// <returns>処理が行われた場合はtrue</returns>
        public bool HandleKeyboardShortcuts(KeyEventArgs e)
        {
            // Ctrl+C: コピー
            if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                PerformCopy?.Invoke();
                e.Handled = true;
                return true;
            }
            
            // Ctrl+V: ペースト
            if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                PerformPaste?.Invoke();
                e.Handled = true;
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// 選択されたノードを削除
        /// </summary>
        public void DeleteSelectedNodes()
        {
            var viewModel = GetViewModel?.Invoke();
            if (viewModel == null) return;
            
            if (_state.SelectedNodes.Count == 0) return;
            
            var nodesToDelete = _state.SelectedNodes.ToList();
            ClearSelections?.Invoke(viewModel);
            
            if (nodesToDelete.Count == 1)
            {
                // 単一ノード削除
                viewModel.CommandManager.Execute(new RemoveNodeCommand(viewModel, nodesToDelete[0]));
            }
            else if (nodesToDelete.Count > 1)
            {
                // 複数ノード削除 - CompositeCommandでまとめる
                var composite = new CompositeCommand($"{nodesToDelete.Count}個のノードを削除");
                foreach (var node in nodesToDelete)
                {
                    composite.Add(new RemoveNodeCommand(viewModel, node));
                }
                viewModel.CommandManager.Execute(composite);
            }
        }
        
        /// <summary>
        /// 選択されているノードがあるかどうか
        /// </summary>
        public bool HasSelection => _state.SelectedNodes.Count > 0;
        
        /// <summary>
        /// 選択されているノードの数
        /// </summary>
        public int SelectionCount => _state.SelectedNodes.Count;
    }
}

using System;
using System.Collections.Generic;

namespace RayTraceVS.WPF.Commands
{
    /// <summary>
    /// コマンドの実行とアンドゥ/リドゥを管理するマネージャ
    /// </summary>
    public class CommandManager
    {
        private readonly Stack<IEditorCommand> _undoStack = new();
        private readonly Stack<IEditorCommand> _redoStack = new();
        private readonly int _maxUndoCount;

        /// <summary>
        /// アンドゥ可能かどうか
        /// </summary>
        public bool CanUndo => _undoStack.Count > 0;

        /// <summary>
        /// リドゥ可能かどうか
        /// </summary>
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>
        /// 次にアンドゥするコマンドの説明
        /// </summary>
        public string? UndoDescription => CanUndo ? _undoStack.Peek().Description : null;

        /// <summary>
        /// 次にリドゥするコマンドの説明
        /// </summary>
        public string? RedoDescription => CanRedo ? _redoStack.Peek().Description : null;

        /// <summary>
        /// アンドゥスタックの状態が変わったときに発火
        /// </summary>
        public event EventHandler? StateChanged;

        public CommandManager(int maxUndoCount = 100)
        {
            _maxUndoCount = maxUndoCount;
        }

        /// <summary>
        /// コマンドを実行し、アンドゥスタックに追加
        /// </summary>
        public void Execute(IEditorCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            command.Execute();

            if (command.CanUndo)
            {
                _undoStack.Push(command);

                // スタックサイズを制限
                while (_undoStack.Count > _maxUndoCount)
                {
                    // 古いコマンドを削除（効率的な方法ではないが、通常は発生しない）
                    var tempStack = new Stack<IEditorCommand>();
                    while (_undoStack.Count > 1)
                    {
                        tempStack.Push(_undoStack.Pop());
                    }
                    _undoStack.Pop(); // 最も古いものを削除
                    while (tempStack.Count > 0)
                    {
                        _undoStack.Push(tempStack.Pop());
                    }
                }

                // リドゥスタックをクリア（新しい操作があったので）
                _redoStack.Clear();

                NotifyStateChanged();
            }
        }

        /// <summary>
        /// 最後のコマンドを元に戻す
        /// </summary>
        public void Undo()
        {
            if (!CanUndo) return;

            var command = _undoStack.Pop();
            command.Undo();
            _redoStack.Push(command);

            NotifyStateChanged();
        }

        /// <summary>
        /// 最後にアンドゥしたコマンドをやり直す
        /// </summary>
        public void Redo()
        {
            if (!CanRedo) return;

            var command = _redoStack.Pop();
            command.Execute();
            _undoStack.Push(command);

            NotifyStateChanged();
        }

        /// <summary>
        /// すべての履歴をクリア
        /// </summary>
        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            NotifyStateChanged();
        }

        private void NotifyStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace RayTraceVS.WPF.Commands
{
    /// <summary>
    /// 複数のコマンドを1つのUndo/Redo操作にまとめる複合コマンド
    /// </summary>
    public class CompositeCommand : IEditorCommand
    {
        private readonly List<IEditorCommand> _commands = new();
        private readonly string _description;

        /// <summary>
        /// コマンドの説明
        /// </summary>
        public string Description => _description;

        /// <summary>
        /// すべての子コマンドがアンドゥ可能な場合のみアンドゥ可能
        /// </summary>
        public bool CanUndo => _commands.Count > 0 && _commands.All(c => c.CanUndo);

        /// <summary>
        /// 含まれるコマンドの数
        /// </summary>
        public int Count => _commands.Count;

        /// <summary>
        /// 複合コマンドを作成
        /// </summary>
        /// <param name="description">コマンドの説明</param>
        public CompositeCommand(string description)
        {
            _description = description;
        }

        /// <summary>
        /// 子コマンドを追加
        /// </summary>
        public void Add(IEditorCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            _commands.Add(command);
        }

        /// <summary>
        /// すべての子コマンドを順番に実行
        /// </summary>
        public void Execute()
        {
            int completed = 0;
            try
            {
                for (; completed < _commands.Count; completed++)
                    _commands[completed].Execute();
            }
            catch (Exception original)
            {
                var failures = new List<Exception> { original };
                for (int i = completed - 1; i >= 0; i--)
                {
                    try { _commands[i].Undo(); }
                    catch (Exception rollback) { failures.Add(rollback); }
                }
                if (failures.Count > 1) throw new AggregateException("複合操作の復元に失敗しました", failures);
                throw;
            }
        }

        /// <summary>
        /// すべての子コマンドを逆順にアンドゥ
        /// </summary>
        public void Undo()
        {
            int current = _commands.Count - 1;
            try
            {
                for (; current >= 0; current--) _commands[current].Undo();
            }
            catch (Exception original)
            {
                var failures = new List<Exception> { original };
                for (int i = current + 1; i < _commands.Count; i++)
                {
                    try { _commands[i].Execute(); }
                    catch (Exception rollback) { failures.Add(rollback); }
                }
                if (failures.Count > 1) throw new AggregateException("複合操作の復元に失敗しました", failures);
                throw;
            }
        }
    }
}

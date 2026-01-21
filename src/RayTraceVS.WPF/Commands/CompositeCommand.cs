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
            _commands.Add(command);
        }

        /// <summary>
        /// すべての子コマンドを順番に実行
        /// </summary>
        public void Execute()
        {
            foreach (var command in _commands)
            {
                command.Execute();
            }
        }

        /// <summary>
        /// すべての子コマンドを逆順にアンドゥ
        /// </summary>
        public void Undo()
        {
            // 逆順にアンドゥ
            for (int i = _commands.Count - 1; i >= 0; i--)
            {
                _commands[i].Undo();
            }
        }
    }
}

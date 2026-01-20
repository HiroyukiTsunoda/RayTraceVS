using System.Windows;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.ViewModels;

namespace RayTraceVS.WPF.Commands
{
    /// <summary>
    /// ノード追加コマンド
    /// </summary>
    public class AddNodeCommand : IEditorCommand
    {
        private readonly MainViewModel _viewModel;
        private readonly Node _node;

        public string Description => $"ノード「{_node.Title}」を追加";
        public bool CanUndo => true;

        public AddNodeCommand(MainViewModel viewModel, Node node)
        {
            _viewModel = viewModel;
            _node = node;
        }

        public void Execute()
        {
            _viewModel.AddNode(_node);
        }

        public void Undo()
        {
            _viewModel.RemoveNode(_node);
        }
    }

    /// <summary>
    /// ノード削除コマンド
    /// </summary>
    public class RemoveNodeCommand : IEditorCommand
    {
        private readonly MainViewModel _viewModel;
        private readonly Node _node;
        private readonly Point _position;

        public string Description => $"ノード「{_node.Title}」を削除";
        public bool CanUndo => true;

        public RemoveNodeCommand(MainViewModel viewModel, Node node)
        {
            _viewModel = viewModel;
            _node = node;
            _position = node.Position;
        }

        public void Execute()
        {
            _viewModel.RemoveNode(_node);
        }

        public void Undo()
        {
            _node.Position = _position;
            _viewModel.AddNode(_node);
        }
    }

    /// <summary>
    /// ノード移動コマンド
    /// </summary>
    public class MoveNodeCommand : IEditorCommand
    {
        private readonly Node _node;
        private readonly Point _oldPosition;
        private readonly Point _newPosition;

        public string Description => $"ノード「{_node.Title}」を移動";
        public bool CanUndo => true;

        public MoveNodeCommand(Node node, Point oldPosition, Point newPosition)
        {
            _node = node;
            _oldPosition = oldPosition;
            _newPosition = newPosition;
        }

        public void Execute()
        {
            _node.Position = _newPosition;
        }

        public void Undo()
        {
            _node.Position = _oldPosition;
        }
    }

    /// <summary>
    /// 複数ノード移動コマンド
    /// </summary>
    public class MoveNodesCommand : IEditorCommand
    {
        private readonly (Node Node, Point OldPosition, Point NewPosition)[] _moves;

        public string Description => $"{_moves.Length}個のノードを移動";
        public bool CanUndo => true;

        public MoveNodesCommand((Node Node, Point OldPosition, Point NewPosition)[] moves)
        {
            _moves = moves;
        }

        public void Execute()
        {
            foreach (var (node, _, newPosition) in _moves)
            {
                node.Position = newPosition;
            }
        }

        public void Undo()
        {
            foreach (var (node, oldPosition, _) in _moves)
            {
                node.Position = oldPosition;
            }
        }
    }
}

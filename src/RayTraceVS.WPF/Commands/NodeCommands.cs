using System.Collections.Generic;
using System.Linq;
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

    /// <summary>ノードを1つ削除する場合も、複数削除と同じ復元手順を使う。</summary>
    public class RemoveNodeCommand : RemoveNodesCommand
    {
        public RemoveNodeCommand(MainViewModel viewModel, Node node) : base(viewModel, new[] { node }) { }
    }

    /// <summary>全ノードを復元してから接続を復元し、共有接続の二重登録を防ぐ。</summary>
    public class RemoveNodesCommand : IEditorCommand
    {
        private readonly MainViewModel _viewModel;
        private readonly Node[] _nodes;
        private readonly Point[] _positions;
        private readonly NodeConnection[] _connections;
        private readonly SceneSocketSnapshot _sceneSockets;

        public string Description => _nodes.Length == 1
            ? $"ノード「{_nodes[0].Title}」を削除" : $"{_nodes.Length}個のノードを削除";
        public bool CanUndo => _nodes.Length > 0;

        public RemoveNodesCommand(MainViewModel viewModel, IEnumerable<Node> nodes)
        {
            _viewModel = viewModel;
            _nodes = nodes.Where(n => ReferenceEquals(viewModel.NodeGraph.GetNodeById(n.Id), n)).Distinct().ToArray();
            _positions = _nodes.Select(n => n.Position).ToArray();
            var selected = new HashSet<Node>(_nodes);
            _connections = viewModel.Connections.Where(c =>
                (c.InputSocket?.ParentNode is Node input && selected.Contains(input)) ||
                (c.OutputSocket?.ParentNode is Node output && selected.Contains(output))).ToArray();
            _sceneSockets = SceneSocketSnapshot.Capture(_connections.Select(c => c.InputSocket).ToArray());
        }

        public void Execute()
        {
            foreach (var node in _nodes) _viewModel.RemoveNode(node);
        }

        public void Undo()
        {
            _sceneSockets.Restore();
            for (int i = 0; i < _nodes.Length; i++)
            {
                _nodes[i].Position = _positions[i];
                _viewModel.AddNode(_nodes[i]);
            }
            foreach (var connection in _connections) _viewModel.AddConnection(connection);
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

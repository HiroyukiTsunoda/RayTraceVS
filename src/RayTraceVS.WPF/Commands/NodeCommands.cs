using System.Collections.Generic;
using System.Linq;
using System.Windows;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Nodes;
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
    /// ノード削除コマンド（接続情報とSceneNodeソケット情報を保持）
    /// </summary>
    public class RemoveNodeCommand : IEditorCommand
    {
        private readonly MainViewModel _viewModel;
        private readonly Node _node;
        private readonly Point _position;
        private readonly List<NodeConnection> _connections;
        private readonly List<SceneNodeSocketInfo> _sceneNodeSocketsToRestore;

        public string Description => $"ノード「{_node.Title}」を削除";
        public bool CanUndo => true;

        /// <summary>
        /// SceneNodeのソケット情報を保存するための構造体
        /// </summary>
        private struct SceneNodeSocketInfo
        {
            public SceneNode SceneNode;
            public NodeSocket Socket;
            public int Index;
        }

        public RemoveNodeCommand(MainViewModel viewModel, Node node)
        {
            _viewModel = viewModel;
            _node = node;
            _position = node.Position;

            // 削除前に接続情報を保存
            _connections = viewModel.Connections
                .Where(c => c.InputSocket?.ParentNode?.Id == node.Id ||
                           c.OutputSocket?.ParentNode?.Id == node.Id)
                .ToList();

            // SceneNodeのソケット情報を保存（接続先がSceneNodeの場合）
            _sceneNodeSocketsToRestore = new List<SceneNodeSocketInfo>();
            foreach (var connection in _connections)
            {
                // 入力側がSceneNodeの場合、そのソケット情報を保存
                if (connection.InputSocket?.ParentNode is SceneNode sceneNode)
                {
                    var socket = connection.InputSocket;
                    var index = sceneNode.InputSockets.IndexOf(socket);
                    if (index >= 0)
                    {
                        _sceneNodeSocketsToRestore.Add(new SceneNodeSocketInfo
                        {
                            SceneNode = sceneNode,
                            Socket = socket,
                            Index = index
                        });
                    }
                }
            }
        }

        public void Execute()
        {
            _viewModel.RemoveNode(_node);
        }

        public void Undo()
        {
            // 1. ノードを復元
            _node.Position = _position;
            _viewModel.AddNode(_node);

            // 2. SceneNodeのソケットを復元（削除されていた場合）
            foreach (var info in _sceneNodeSocketsToRestore)
            {
                // ソケットが既に存在しない場合のみ復元
                if (!info.SceneNode.InputSockets.Contains(info.Socket))
                {
                    // 元のインデックス位置に挿入（範囲外の場合は末尾に追加）
                    if (info.Index >= 0 && info.Index <= info.SceneNode.InputSockets.Count)
                    {
                        info.SceneNode.InputSockets.Insert(info.Index, info.Socket);
                    }
                    else
                    {
                        info.SceneNode.InputSockets.Add(info.Socket);
                    }
                }
            }

            // 3. 接続を復元
            foreach (var connection in _connections)
            {
                // 接続の両端のソケットがまだ存在するか確認
                if (connection.OutputSocket != null && connection.InputSocket != null)
                {
                    // 新しい接続を作成して追加（元の接続オブジェクトは再利用しない）
                    var newConnection = new NodeConnection(connection.OutputSocket, connection.InputSocket);
                    _viewModel.AddConnection(newConnection);
                }
            }
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

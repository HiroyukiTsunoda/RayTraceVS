using System.Collections.Generic;
using System.Linq;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Nodes;
using RayTraceVS.WPF.ViewModels;

namespace RayTraceVS.WPF.Commands
{
    /// <summary>Sceneの動的ソケットへの参照と並び順を保存する。</summary>
    internal sealed class SceneSocketSnapshot
    {
        private readonly Dictionary<SceneNode, NodeSocket[]> _sockets;

        private SceneSocketSnapshot(Dictionary<SceneNode, NodeSocket[]> sockets) => _sockets = sockets;

        public static SceneSocketSnapshot Capture(params NodeSocket?[] sockets)
        {
            return new SceneSocketSnapshot(sockets.Select(s => s?.ParentNode).OfType<SceneNode>()
                .Distinct().ToDictionary(n => n, n => n.InputSockets.ToArray()));
        }

        public void Restore()
        {
            foreach (var (scene, sockets) in _sockets)
            {
                foreach (var socket in scene.InputSockets.Except(sockets).ToArray())
                    scene.InputSockets.Remove(socket);
                for (int i = 0; i < sockets.Length; i++)
                {
                    int current = scene.InputSockets.IndexOf(sockets[i]);
                    if (current < 0) scene.InputSockets.Insert(i, sockets[i]);
                    else if (current != i) scene.InputSockets.Move(current, i);
                }
                scene.RenumberSceneSockets();
            }
        }
    }

    /// <summary>接続と自動追加されたSceneソケットを一緒にUndoする。</summary>
    public class AddConnectionCommand : IEditorCommand
    {
        private readonly MainViewModel _viewModel;
        private readonly NodeConnection _connection;
        private SceneSocketSnapshot? _before;
        private SceneSocketSnapshot? _after;

        public string Description => "接続を追加";
        public bool CanUndo => true;

        public AddConnectionCommand(MainViewModel viewModel, NodeConnection connection)
        {
            _viewModel = viewModel;
            _connection = connection;
        }

        public void Execute()
        {
            _before ??= SceneSocketSnapshot.Capture(_connection.InputSocket);
            _after?.Restore();
            _viewModel.AddConnection(_connection);
            _after ??= SceneSocketSnapshot.Capture(_connection.InputSocket);
        }

        public void Undo()
        {
            _viewModel.RemoveConnection(_connection, cleanupSceneNodeSockets: false);
            _before?.Restore();
        }
    }

    /// <summary>接続IDと動的ソケットを維持したまま削除をUndo/Redoする。</summary>
    public class RemoveConnectionCommand : IEditorCommand
    {
        private readonly MainViewModel _viewModel;
        private readonly NodeConnection _connection;
        private readonly bool _cleanupSceneNodeSockets;
        private SceneSocketSnapshot? _before;

        public string Description => "接続を削除";
        public bool CanUndo => true;

        public RemoveConnectionCommand(MainViewModel viewModel, NodeConnection connection, bool cleanupSceneNodeSockets = true)
        {
            _viewModel = viewModel;
            _connection = connection;
            _cleanupSceneNodeSockets = cleanupSceneNodeSockets;
        }

        public void Execute()
        {
            _before ??= SceneSocketSnapshot.Capture(_connection.InputSocket);
            _viewModel.RemoveConnection(_connection, _cleanupSceneNodeSockets);
        }

        public void Undo()
        {
            _before?.Restore();
            _viewModel.AddConnection(_connection);
        }
    }

    /// <summary>入力ソケットを保持し、同じ接続インスタンスで置換をUndo/Redoする。</summary>
    public class ReplaceConnectionCommand : IEditorCommand
    {
        private readonly MainViewModel _viewModel;
        private readonly NodeConnection _oldConnection;
        private readonly NodeConnection _newConnection;
        private SceneSocketSnapshot? _before;
        private SceneSocketSnapshot? _after;

        public string Description => "接続を置換";
        public bool CanUndo => true;

        public ReplaceConnectionCommand(MainViewModel viewModel, NodeConnection oldConnection, NodeConnection newConnection)
        {
            _viewModel = viewModel;
            _oldConnection = oldConnection;
            _newConnection = newConnection;
        }

        public void Execute()
        {
            _before ??= SceneSocketSnapshot.Capture(_oldConnection.InputSocket, _newConnection.InputSocket);
            _viewModel.RemoveConnection(_oldConnection, cleanupSceneNodeSockets: false);
            try
            {
                _after?.Restore();
                _viewModel.AddConnection(_newConnection);
                _after ??= SceneSocketSnapshot.Capture(_oldConnection.InputSocket, _newConnection.InputSocket);
            }
            catch
            {
                _before.Restore();
                _viewModel.AddConnection(_oldConnection);
                throw;
            }
        }

        public void Undo()
        {
            _viewModel.RemoveConnection(_newConnection, cleanupSceneNodeSockets: false);
            _before?.Restore();
            _viewModel.AddConnection(_oldConnection);
        }
    }
}

using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Nodes;
using RayTraceVS.WPF.ViewModels;

namespace RayTraceVS.WPF.Commands
{
    /// <summary>
    /// 接続追加コマンド（SceneNodeのソケット情報を保持）
    /// </summary>
    public class AddConnectionCommand : IEditorCommand
    {
        private readonly MainViewModel _viewModel;
        private readonly NodeConnection _connection;
        private NodeSocket? _addedSocket;  // SceneNodeに追加されたソケット（Undo時に削除するため）

        public string Description => "接続を追加";
        public bool CanUndo => true;

        public AddConnectionCommand(MainViewModel viewModel, NodeConnection connection)
        {
            _viewModel = viewModel;
            _connection = connection;
        }

        public void Execute()
        {
            // 接続先がSceneNodeの場合、接続前のソケット数を記録
            int socketCountBefore = 0;
            SceneNode? sceneNode = null;
            if (_connection.InputSocket?.ParentNode is SceneNode sn)
            {
                sceneNode = sn;
                socketCountBefore = sceneNode.InputSockets.Count;
            }

            _viewModel.AddConnection(_connection);

            // 接続後にソケットが追加されたか確認
            if (sceneNode != null && sceneNode.InputSockets.Count > socketCountBefore)
            {
                // 最後に追加されたソケットを記録
                _addedSocket = sceneNode.InputSockets[sceneNode.InputSockets.Count - 1];
            }
        }

        public void Undo()
        {
            _viewModel.RemoveConnection(_connection);

            // SceneNodeに追加されたソケットも削除
            if (_addedSocket != null && _addedSocket.ParentNode is SceneNode sceneNode)
            {
                sceneNode.InputSockets.Remove(_addedSocket);
            }
        }
    }

    /// <summary>
    /// 接続削除コマンド（SceneNodeのソケット情報を保持）
    /// </summary>
    public class RemoveConnectionCommand : IEditorCommand
    {
        private readonly MainViewModel _viewModel;
        private readonly NodeSocket? _outputSocket;
        private readonly NodeSocket? _inputSocket;
        private NodeSocket? _removedSocket;  // SceneNodeから削除されたソケット（Undo時に復元するため）
        private int _removedSocketIndex = -1;
        private NodeConnection? _currentConnection;  // 現在のコレクションに存在する接続（Undo/Redo用）

        public string Description => "接続を削除";
        public bool CanUndo => true;

        public RemoveConnectionCommand(MainViewModel viewModel, NodeConnection connection)
        {
            _viewModel = viewModel;
            _currentConnection = connection;
            _outputSocket = connection.OutputSocket;
            _inputSocket = connection.InputSocket;

            // 入力ソケットがSceneNodeの場合、削除前の情報を保存
            if (_inputSocket?.ParentNode is SceneNode sceneNode)
            {
                _removedSocketIndex = sceneNode.InputSockets.IndexOf(_inputSocket);
            }
        }

        /// <summary>
        /// 既に削除済みの接続を登録する場合のコンストラクタ（RegisterExecuted用）
        /// </summary>
        public RemoveConnectionCommand(MainViewModel viewModel, NodeConnection connection, NodeSocket? removedSocket, int removedSocketIndex)
        {
            _viewModel = viewModel;
            _currentConnection = connection;
            _outputSocket = connection.OutputSocket;
            _inputSocket = connection.InputSocket;
            _removedSocket = removedSocket;
            _removedSocketIndex = removedSocketIndex;
        }

        public void Execute()
        {
            if (_currentConnection == null) return;

            // 削除前にSceneNodeのソケット数を記録
            int socketCountBefore = 0;
            SceneNode? sceneNode = null;
            if (_inputSocket?.ParentNode is SceneNode sn)
            {
                sceneNode = sn;
                socketCountBefore = sceneNode.InputSockets.Count;
            }

            _viewModel.RemoveConnection(_currentConnection);

            // 削除後にソケットが減ったか確認
            if (sceneNode != null && sceneNode.InputSockets.Count < socketCountBefore)
            {
                // ソケットが削除された場合、そのソケットを記録
                if (!sceneNode.InputSockets.Contains(_inputSocket))
                {
                    _removedSocket = _inputSocket;
                }
            }
        }

        public void Undo()
        {
            // SceneNodeのソケットを復元（削除されていた場合）
            if (_removedSocket != null && _removedSocket.ParentNode is SceneNode sceneNode)
            {
                if (!sceneNode.InputSockets.Contains(_removedSocket))
                {
                    if (_removedSocketIndex >= 0 && _removedSocketIndex <= sceneNode.InputSockets.Count)
                    {
                        sceneNode.InputSockets.Insert(_removedSocketIndex, _removedSocket);
                    }
                    else
                    {
                        sceneNode.InputSockets.Add(_removedSocket);
                    }
                }
            }

            // 接続を再作成し、次のRedo用に追跡
            if (_outputSocket != null && _inputSocket != null)
            {
                _currentConnection = new NodeConnection(_outputSocket, _inputSocket);
                _viewModel.AddConnection(_currentConnection);
            }
        }
    }

    /// <summary>
    /// 接続置換コマンド（既存接続を削除して新規接続を作成する操作を1つにまとめる）
    /// </summary>
    public class ReplaceConnectionCommand : IEditorCommand
    {
        private readonly MainViewModel _viewModel;
        private readonly NodeConnection _oldConnection;
        private readonly NodeConnection _newConnection;
        private readonly NodeSocket? _oldOutputSocket;
        private readonly NodeSocket? _oldInputSocket;

        public string Description => "接続を置換";
        public bool CanUndo => true;

        public ReplaceConnectionCommand(MainViewModel viewModel, NodeConnection oldConnection, NodeConnection newConnection)
        {
            _viewModel = viewModel;
            _oldConnection = oldConnection;
            _newConnection = newConnection;
            _oldOutputSocket = oldConnection.OutputSocket;
            _oldInputSocket = oldConnection.InputSocket;
        }

        public void Execute()
        {
            // 古い接続を削除（SceneNodeのソケット削除は発生しないはず、新しい接続で置き換えるため）
            _viewModel.RemoveConnection(_oldConnection);
            // 新しい接続を追加
            _viewModel.AddConnection(_newConnection);
        }

        public void Undo()
        {
            // 新しい接続を削除
            _viewModel.RemoveConnection(_newConnection);
            
            // 古い接続を復元
            if (_oldOutputSocket != null && _oldInputSocket != null)
            {
                var restoredConnection = new NodeConnection(_oldOutputSocket, _oldInputSocket);
                _viewModel.AddConnection(restoredConnection);
            }
        }
    }
}

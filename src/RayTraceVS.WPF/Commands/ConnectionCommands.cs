using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.ViewModels;

namespace RayTraceVS.WPF.Commands
{
    /// <summary>
    /// 接続追加コマンド
    /// </summary>
    public class AddConnectionCommand : IEditorCommand
    {
        private readonly MainViewModel _viewModel;
        private readonly NodeConnection _connection;

        public string Description => "接続を追加";
        public bool CanUndo => true;

        public AddConnectionCommand(MainViewModel viewModel, NodeConnection connection)
        {
            _viewModel = viewModel;
            _connection = connection;
        }

        public void Execute()
        {
            _viewModel.AddConnection(_connection);
        }

        public void Undo()
        {
            _viewModel.RemoveConnection(_connection);
        }
    }

    /// <summary>
    /// 接続削除コマンド
    /// </summary>
    public class RemoveConnectionCommand : IEditorCommand
    {
        private readonly MainViewModel _viewModel;
        private readonly NodeConnection _connection;
        private readonly NodeSocket? _outputSocket;
        private readonly NodeSocket? _inputSocket;

        public string Description => "接続を削除";
        public bool CanUndo => true;

        public RemoveConnectionCommand(MainViewModel viewModel, NodeConnection connection)
        {
            _viewModel = viewModel;
            _connection = connection;
            _outputSocket = connection.OutputSocket;
            _inputSocket = connection.InputSocket;
        }

        public void Execute()
        {
            _viewModel.RemoveConnection(_connection);
        }

        public void Undo()
        {
            // 接続を再作成
            if (_outputSocket != null && _inputSocket != null)
            {
                var newConnection = new NodeConnection(_outputSocket, _inputSocket);
                _viewModel.AddConnection(newConnection);
            }
        }
    }
}

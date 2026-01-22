using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using RayTraceVS.WPF.Commands;
using RayTraceVS.WPF.Models;
using System.Diagnostics;
using System.Linq;

namespace RayTraceVS.WPF.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        [ObservableProperty]
        private NodeGraph nodeGraph;

        [ObservableProperty]
        private Node? selectedNode;

        [ObservableProperty]
        private ObservableCollection<Node> nodes;

        [ObservableProperty]
        private ObservableCollection<NodeConnection> connections;

        /// <summary>
        /// Undo/Redo操作を管理するコマンドマネージャ
        /// </summary>
        public CommandManager CommandManager { get; } = new();

        /// <summary>
        /// ノード作成順序を管理するカウンター（描画順序の基準）
        /// </summary>
        private int _nodeCreationCounter = 0;

        public MainViewModel()
        {
            nodeGraph = new NodeGraph();
            nodes = new ObservableCollection<Node>();
            connections = new ObservableCollection<NodeConnection>();
        }

        public void AddNode(Node node)
        {
            // ノードの作成順序を設定（描画順序に使用）
            node.CreationIndex = _nodeCreationCounter++;
            nodes.Add(node);
            nodeGraph.AddNode(node);
        }

        public void RemoveNode(Node node)
        {
            // ノードに接続されている接続を削除
            var connectionsToRemove = connections
                .Where(c => c.InputSocket?.ParentNode?.Id == node.Id || 
                           c.OutputSocket?.ParentNode?.Id == node.Id)
                .ToList();

            foreach (var connection in connectionsToRemove)
            {
                connection.Dispose(); // イベント購読解除
                connections.Remove(connection);
            }

            nodes.Remove(node);
            nodeGraph.RemoveNode(node);
        }

        public void AddConnection(NodeConnection connection)
        {
            connections.Add(connection);
            nodeGraph.AddConnection(connection);
            
            // 入力ソケットの接続状態を更新
            if (connection.InputSocket != null)
            {
                connection.InputSocket.IsConnected = true;
            }
        }

        public void RemoveConnection(NodeConnection connection)
        {
            // 入力ソケットの接続状態を更新（削除前に取得）
            var inputSocket = connection.InputSocket;
            
            connections.Remove(connection);
            nodeGraph.RemoveConnection(connection);
            
            // 入力ソケットの接続状態を更新
            if (inputSocket != null)
            {
                // 同じソケットへの他の接続がないか確認
                inputSocket.IsConnected = connections.Any(c => c.InputSocket == inputSocket);
            }
            
            // シーンノードの場合、未使用のソケットを削除（最後の1つは残す）
            if (inputSocket?.ParentNode is Models.Nodes.SceneNode sceneNode)
            {
                CleanupSceneNodeSockets(sceneNode);
            }
        }
        
        private void CleanupSceneNodeSockets(Models.Nodes.SceneNode sceneNode)
        {
            // オブジェクトソケットをクリーンアップ（最低1つは残す）
            var objectSockets = sceneNode.InputSockets
                .Where(s => s.SocketType == Models.SocketType.Object)
                .ToList();
            
            if (objectSockets.Count > 1)
            {
                var emptyObjectSockets = objectSockets
                    .Where(s => !connections.Any(c => c.InputSocket == s))
                    .Skip(1) // 最初の空ソケットは残す
                    .ToList();
                
                foreach (var socket in emptyObjectSockets)
                {
                    sceneNode.RemoveSocket(socket.Name);
                }
            }
            
            // ライトソケットをクリーンアップ（最低1つは残す）
            var lightSockets = sceneNode.InputSockets
                .Where(s => s.SocketType == Models.SocketType.Light)
                .ToList();
            
            if (lightSockets.Count > 1)
            {
                var emptyLightSockets = lightSockets
                    .Where(s => !connections.Any(c => c.InputSocket == s))
                    .Skip(1) // 最初の空ソケットは残す
                    .ToList();
                
                foreach (var socket in emptyLightSockets)
                {
                    sceneNode.RemoveSocket(socket.Name);
                }
            }
        }
    }
}

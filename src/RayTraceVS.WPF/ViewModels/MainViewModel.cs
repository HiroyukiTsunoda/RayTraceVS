using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using RayTraceVS.WPF.Commands;
using RayTraceVS.WPF.Models;

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
        /// 選択ノードに関連する接続線
        /// </summary>
        public ObservableCollection<NodeConnection> SelectedNodeConnections { get; } = new();

        /// <summary>
        /// 非選択ノードの接続線
        /// </summary>
        public ObservableCollection<NodeConnection> UnselectedNodeConnections { get; } = new();

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
            
            // コレクション変更時にビューを更新
            connections.CollectionChanged += OnConnectionsCollectionChanged;
        }
        
        /// <summary>
        /// 接続線コレクションが変更されたとき
        /// </summary>
        private void OnConnectionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // 新しく追加された接続のIsSelected変更を監視
            if (e.NewItems != null)
            {
                foreach (NodeConnection conn in e.NewItems)
                {
                    conn.PropertyChanged += OnConnectionPropertyChanged;
                    // 適切なコレクションに追加
                    if (conn.IsSelected)
                        SelectedNodeConnections.Add(conn);
                    else
                        UnselectedNodeConnections.Add(conn);
                }
            }
            
            // 削除された接続の監視を解除
            if (e.OldItems != null)
            {
                foreach (NodeConnection conn in e.OldItems)
                {
                    conn.PropertyChanged -= OnConnectionPropertyChanged;
                    // 両方のコレクションから削除
                    SelectedNodeConnections.Remove(conn);
                    UnselectedNodeConnections.Remove(conn);
                }
            }
        }
        
        /// <summary>
        /// 接続のプロパティが変更されたとき（IsSelectedの変更を検知）
        /// </summary>
        private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(NodeConnection.IsSelected) && sender is NodeConnection conn)
            {
                // 選択状態に応じてコレクション間を移動
                if (conn.IsSelected)
                {
                    if (UnselectedNodeConnections.Remove(conn))
                    {
                        SelectedNodeConnections.Add(conn);
                    }
                }
                else
                {
                    if (SelectedNodeConnections.Remove(conn))
                    {
                        UnselectedNodeConnections.Add(conn);
                    }
                }
            }
        }
        
        /// <summary>
        /// 接続線のフィルタリングビューを更新（互換性のため維持）
        /// </summary>
        public void RefreshConnectionViews()
        {
            // ObservableCollectionを使用しているため、手動更新は不要
            // ただし、状態の不整合を修正するためにフルリビルドを行う
            RebuildConnectionCollections();
        }
        
        /// <summary>
        /// 選択/非選択の接続線コレクションを再構築
        /// </summary>
        private void RebuildConnectionCollections()
        {
            SelectedNodeConnections.Clear();
            UnselectedNodeConnections.Clear();
            
            foreach (var conn in connections)
            {
                if (conn.IsSelected)
                    SelectedNodeConnections.Add(conn);
                else
                    UnselectedNodeConnections.Add(conn);
            }
        }

        public void AddNode(Node node)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node), "追加するノードがnullです");
            }

            try
            {
                // ノードの作成順序を設定（描画順序に使用）
                node.CreationIndex = _nodeCreationCounter++;
                nodes.Add(node);
                nodeGraph.AddNode(node);
            }
            catch (NodeGraphException)
            {
                // NodeGraph側で既にログ出力済み、そのまま再スロー
                throw;
            }
            catch (Exception ex) when (ex is not ArgumentNullException)
            {
                Debug.WriteLine($"MainViewModel.AddNode: エラー - {ex.Message}");
                throw new NodeGraphException($"ノード '{node.Title}' の追加に失敗しました", ex);
            }
        }

        public void RemoveNode(Node node)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node), "削除するノードがnullです");
            }

            try
            {
                // ノードに接続されている接続を削除
                var connectionsToRemove = connections
                    .Where(c => c.InputSocket?.ParentNode?.Id == node.Id || 
                               c.OutputSocket?.ParentNode?.Id == node.Id)
                    .ToList();

                foreach (var connection in connectionsToRemove)
                {
                    try
                    {
                        connection.Dispose(); // イベント購読解除
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"MainViewModel.RemoveNode: 接続 {connection.Id} のDisposeに失敗 - {ex.Message}");
                    }
                    connections.Remove(connection);
                }

                nodes.Remove(node);
                nodeGraph.RemoveNode(node);
            }
            catch (NodeGraphException)
            {
                // NodeGraph側で既にログ出力済み、そのまま再スロー
                throw;
            }
            catch (Exception ex) when (ex is not ArgumentNullException)
            {
                Debug.WriteLine($"MainViewModel.RemoveNode: エラー - {ex.Message}");
                throw new NodeGraphException($"ノード '{node.Title}' の削除に失敗しました", ex);
            }
        }

        public void AddConnection(NodeConnection connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection), "追加する接続がnullです");
            }

            try
            {
                connections.Add(connection);
                nodeGraph.AddConnection(connection);
                
                // 入力ソケットの接続状態を更新
                if (connection.InputSocket != null)
                {
                    connection.InputSocket.IsConnected = true;
                }
            }
            catch (NodeGraphException)
            {
                // NodeGraph側で既にログ出力済み、そのまま再スロー
                throw;
            }
            catch (Exception ex) when (ex is not ArgumentNullException)
            {
                Debug.WriteLine($"MainViewModel.AddConnection: エラー - {ex.Message}");
                throw new NodeGraphException("接続の追加に失敗しました", ex);
            }
        }

        public void RemoveConnection(NodeConnection connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection), "削除する接続がnullです");
            }

            try
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
            catch (NodeGraphException)
            {
                // NodeGraph側で既にログ出力済み、そのまま再スロー
                throw;
            }
            catch (Exception ex) when (ex is not ArgumentNullException)
            {
                Debug.WriteLine($"MainViewModel.RemoveConnection: エラー - {ex.Message}");
                throw new NodeGraphException("接続の削除に失敗しました", ex);
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

            // 連番を詰める
            sceneNode.RenumberSceneSockets();
        }
    }
}

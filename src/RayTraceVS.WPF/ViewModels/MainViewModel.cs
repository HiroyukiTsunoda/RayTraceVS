using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using RayTraceVS.WPF.Commands;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Services;

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
        /// 選択されたノード（高ZIndexで描画）
        /// </summary>
        public ObservableCollection<Node> SelectedNodes { get; } = new();

        /// <summary>
        /// 非選択ノード（通常ZIndexで描画）
        /// </summary>
        public ObservableCollection<Node> UnselectedNodes { get; } = new();

        /// <summary>
        /// Undo/Redo操作を管理するコマンドマネージャ
        /// </summary>
        public CommandManager CommandManager { get; } = new();

        /// <summary>
        /// ノード作成順序を管理するカウンター（描画順序の基準）
        /// </summary>
        private int _nodeCreationCounter = 0;

        /// <summary>
        /// ノードID→関連接続リストのインデックス（パフォーマンス最適化用）
        /// ノード移動時に関連する接続のみを更新するために使用
        /// </summary>
        private readonly Dictionary<Guid, List<NodeConnection>> _nodeToConnections = new();

        public MainViewModel()
        {
            nodeGraph = new NodeGraph();
            nodes = new ObservableCollection<Node>();
            connections = new ObservableCollection<NodeConnection>();

            // コレクション変更時にビューを更新
            connections.CollectionChanged += OnConnectionsCollectionChanged;
            nodes.CollectionChanged += OnNodesCollectionChanged;

            // シーンの変更を監視して未保存フラグを更新
            nodes.CollectionChanged += (s, e) => MarkSceneDirty();
            connections.CollectionChanged += (s, e) => MarkSceneDirty();
            nodeGraph.SceneChanged += (s, e) => MarkSceneDirty();
        }

        #region シーンファイル状態管理

        /// <summary>
        /// 現在開いているシーンファイルのパス（新規シーンはnull）
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WindowTitle))]
        private string? currentFilePath;

        /// <summary>
        /// 未保存の変更があるかどうか
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WindowTitle))]
        private bool hasUnsavedChanges;

        /// <summary>
        /// ウィンドウタイトル（ファイル名＋未保存マーク）
        /// </summary>
        public string WindowTitle
        {
            get
            {
                string baseName = "RayTraceVS";
                string fileName = string.IsNullOrEmpty(CurrentFilePath)
                    ? "新規シーン"
                    : System.IO.Path.GetFileName(CurrentFilePath);
                return HasUnsavedChanges ? $"{baseName}* - {fileName}" : $"{baseName} - {fileName}";
            }
        }

        private void MarkSceneDirty()
        {
            if (!HasUnsavedChanges)
            {
                HasUnsavedChanges = true;
            }
        }

        /// <summary>
        /// シーンファイルを読み込み、現在のグラフを置き換える。
        /// ViewportStateの画面反映はView側の責務（ノード追加前に適用する必要があるためコールバックで渡す）。
        /// </summary>
        /// <param name="filePath">読み込むシーンファイル</param>
        /// <param name="applyViewportStateBeforeNodes">ノード追加前にViewportStateをUIへ適用する処理（初期描画のズレ防止）</param>
        /// <returns>読み込んだViewportStateと、キャッシュ欠落で除外されたノードの情報</returns>
        public (ViewportState? ViewportState, List<string> RemovedNodeInfos) LoadScene(
            string filePath, Action<ViewportState?> applyViewportStateBeforeNodes)
        {
            var sceneService = new SceneFileService();
            var (loadedNodes, loadedConnections, viewportState) = sceneService.LoadScene(filePath);

            Nodes.Clear();
            Connections.Clear();

            // ビューポートの状態を先に設定（ノード追加前に設定することで初期描画のズレを防ぐ）
            applyViewportStateBeforeNodes(viewportState);

            foreach (var node in loadedNodes)
                AddNode(node);
            foreach (var connection in loadedConnections)
                AddConnection(connection);

            CurrentFilePath = filePath;
            HasUnsavedChanges = false;

            // Undo/Redo履歴をクリア
            CommandManager.Clear();

            return (viewportState, sceneService.RemovedNodeInfos);
        }

        /// <summary>
        /// シーンをファイルに保存する。ViewportState（パン/ズーム/パネル開閉等）はView側で構築して渡す。
        /// </summary>
        public void SaveScene(string filePath, ViewportState viewportState)
        {
            var sceneService = new SceneFileService();
            sceneService.SaveScene(filePath, Nodes, Connections, viewportState);

            CurrentFilePath = filePath;
            HasUnsavedChanges = false;
        }

        /// <summary>
        /// シーンを新規作成する（全ノード/接続のクリアと状態リセット）
        /// </summary>
        public void NewScene()
        {
            Nodes.Clear();
            Connections.Clear();
            CurrentFilePath = null;
            HasUnsavedChanges = false;

            // Undo/Redo履歴をクリア
            CommandManager.Clear();
        }

        #endregion
        
        /// <summary>
        /// ノードコレクションが変更されたとき
        /// </summary>
        private void OnNodesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Reset（Clear）操作の場合
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                // 全てのノードの監視を解除（できれば）し、コレクションをクリア
                foreach (var node in SelectedNodes)
                    node.PropertyChanged -= OnNodePropertyChanged;
                foreach (var node in UnselectedNodes)
                    node.PropertyChanged -= OnNodePropertyChanged;
                    
                SelectedNodes.Clear();
                UnselectedNodes.Clear();
                return;
            }
            
            // 新しく追加されたノードのIsSelected変更を監視
            if (e.NewItems != null)
            {
                foreach (Node node in e.NewItems)
                {
                    node.PropertyChanged += OnNodePropertyChanged;
                    // 適切なコレクションに追加
                    if (node.IsSelected)
                        SelectedNodes.Add(node);
                    else
                        UnselectedNodes.Add(node);
                }
            }
            
            // 削除されたノードの監視を解除
            if (e.OldItems != null)
            {
                foreach (Node node in e.OldItems)
                {
                    node.PropertyChanged -= OnNodePropertyChanged;
                    // 両方のコレクションから削除
                    SelectedNodes.Remove(node);
                    UnselectedNodes.Remove(node);
                }
            }
        }
        
        /// <summary>
        /// ノードのプロパティが変更されたとき（IsSelectedの変更を検知）
        /// </summary>
        private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Node.IsSelected) && sender is Node node)
            {
                // 選択状態に応じてコレクション間を移動
                if (node.IsSelected)
                {
                    if (UnselectedNodes.Remove(node))
                    {
                        SelectedNodes.Add(node);
                    }
                }
                else
                {
                    if (SelectedNodes.Remove(node))
                    {
                        UnselectedNodes.Add(node);
                    }
                }
            }
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
                
                // ノード→接続インデックスを更新
                AddToNodeConnectionIndex(connection);
                
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
                
                // ノード→接続インデックスから削除（削除前に実行）
                RemoveFromNodeConnectionIndex(connection);
                
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
        
        /// <summary>
        /// 指定したノードに関連する接続リストを取得（O(1)アクセス）
        /// </summary>
        /// <param name="nodeId">ノードID</param>
        /// <returns>関連する接続のリスト（存在しない場合は空のリスト）</returns>
        public IReadOnlyList<NodeConnection> GetConnectionsForNode(Guid nodeId)
        {
            if (_nodeToConnections.TryGetValue(nodeId, out var connections))
            {
                return connections;
            }
            return Array.Empty<NodeConnection>();
        }

        /// <summary>
        /// 接続をノード→接続インデックスに追加
        /// </summary>
        private void AddToNodeConnectionIndex(NodeConnection connection)
        {
            // 出力側ノードのインデックスに追加
            var outputNodeId = connection.OutputSocket?.ParentNode?.Id;
            if (outputNodeId.HasValue)
            {
                if (!_nodeToConnections.TryGetValue(outputNodeId.Value, out var outputList))
                {
                    outputList = new List<NodeConnection>();
                    _nodeToConnections[outputNodeId.Value] = outputList;
                }
                if (!outputList.Contains(connection))
                {
                    outputList.Add(connection);
                }
            }

            // 入力側ノードのインデックスに追加
            var inputNodeId = connection.InputSocket?.ParentNode?.Id;
            if (inputNodeId.HasValue)
            {
                if (!_nodeToConnections.TryGetValue(inputNodeId.Value, out var inputList))
                {
                    inputList = new List<NodeConnection>();
                    _nodeToConnections[inputNodeId.Value] = inputList;
                }
                if (!inputList.Contains(connection))
                {
                    inputList.Add(connection);
                }
            }
        }

        /// <summary>
        /// 接続をノード→接続インデックスから削除
        /// </summary>
        private void RemoveFromNodeConnectionIndex(NodeConnection connection)
        {
            // 出力側ノードのインデックスから削除
            var outputNodeId = connection.OutputSocket?.ParentNode?.Id;
            if (outputNodeId.HasValue && _nodeToConnections.TryGetValue(outputNodeId.Value, out var outputList))
            {
                outputList.Remove(connection);
                // 空になったら削除
                if (outputList.Count == 0)
                {
                    _nodeToConnections.Remove(outputNodeId.Value);
                }
            }

            // 入力側ノードのインデックスから削除
            var inputNodeId = connection.InputSocket?.ParentNode?.Id;
            if (inputNodeId.HasValue && _nodeToConnections.TryGetValue(inputNodeId.Value, out var inputList))
            {
                inputList.Remove(connection);
                // 空になったら削除
                if (inputList.Count == 0)
                {
                    _nodeToConnections.Remove(inputNodeId.Value);
                }
            }
        }

        /// <summary>
        /// ノード→接続インデックスをクリア（Undo/Redo対応用）
        /// </summary>
        public void ClearNodeConnectionIndex()
        {
            _nodeToConnections.Clear();
        }

        /// <summary>
        /// ノード→接続インデックスを再構築（Undo/Redo対応用）
        /// </summary>
        public void RebuildNodeConnectionIndex()
        {
            _nodeToConnections.Clear();
            foreach (var connection in connections)
            {
                AddToNodeConnectionIndex(connection);
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

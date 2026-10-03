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
        // グラフでノードと接続を一元管理する。UIは同じコレクションを読み取り専用で参照する。
        public NodeGraph NodeGraph { get; } = new();

        [ObservableProperty]
        private Node? selectedNode;

        public ReadOnlyObservableCollection<Node> Nodes => NodeGraph.Nodes;
        public ReadOnlyObservableCollection<NodeConnection> Connections => NodeGraph.Connections;
        public IReadOnlyList<string> MissingMeshInfos { get; private set; } = Array.Empty<string>();

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
            ((INotifyCollectionChanged)Connections).CollectionChanged += OnConnectionsCollectionChanged;
            ((INotifyCollectionChanged)Nodes).CollectionChanged += OnNodesCollectionChanged;

            // シーンの変更を監視して未保存フラグを更新
            NodeGraph.SceneChanged += (s, e) => MarkSceneDirty();
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
            ArgumentNullException.ThrowIfNull(applyViewportStateBeforeNodes);
            var sceneService = new SceneFileService(name => Node.MeshCacheProvider?.GetMesh(name) != null);
            var (loadedNodes, loadedConnections, viewportState) = sceneService.LoadScene(filePath);
            // 読み込んだグラフを検証してから、現在の作業を置き換える。
            var validatedGraph = new NodeGraph();
            try
            {
                foreach (var node in loadedNodes) validatedGraph.AddNode(node);
                foreach (var connection in loadedConnections) validatedGraph.AddConnection(connection);
            }
            finally
            {
                validatedGraph.Clear();
            }

            // ビューポートの状態を先に設定（ノード追加前に設定することで初期描画のズレを防ぐ）
            applyViewportStateBeforeNodes(viewportState);
            NodeGraph.Clear();
            _nodeCreationCounter = 0;

            foreach (var node in loadedNodes)
                AddNode(node);
            foreach (var connection in loadedConnections)
                NodeGraph.AddConnection(connection);
            EnsureSceneNodeSocketCounts();
            MissingMeshInfos = sceneService.MissingMeshInfos.ToArray();

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
            NodeGraph.Clear();
            SelectedNode = null;
            _nodeCreationCounter = 0;
            MissingMeshInfos = Array.Empty<string>();
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
                SelectedNode = null;
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
                    if (ReferenceEquals(SelectedNode, node)) SelectedNode = null;
                }
            }
        }
        
        /// <summary>
        /// ノードのプロパティが変更されたとき（IsSelectedの変更を検知）
        /// </summary>
        private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // ノードの配置は保存対象だが描画内容には影響しないため、VMで未保存状態だけを更新する。
            if (e.PropertyName == nameof(Node.Position)) MarkSceneDirty();
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
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var connection in SelectedNodeConnections.Concat(UnselectedNodeConnections))
                    connection.PropertyChanged -= OnConnectionPropertyChanged;
                SelectedNodeConnections.Clear();
                UnselectedNodeConnections.Clear();
                _nodeToConnections.Clear();
                return;
            }
            // 新しく追加された接続のIsSelected変更を監視
            if (e.NewItems != null)
            {
                foreach (NodeConnection conn in e.NewItems)
                {
                    conn.PropertyChanged += OnConnectionPropertyChanged;
                    AddToNodeConnectionIndex(conn);
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
                    RemoveFromNodeConnectionIndex(conn);
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
            
            foreach (var conn in Connections)
            {
                if (conn.IsSelected)
                    SelectedNodeConnections.Add(conn);
                else
                    UnselectedNodeConnections.Add(conn);
            }
        }

        public void AddNode(Node node)
        {
            ArgumentNullException.ThrowIfNull(node);
            if (!Nodes.Contains(node)) node.CreationIndex = _nodeCreationCounter++;
            NodeGraph.AddNode(node);
        }

        public void RemoveNode(Node node)
        {
            NodeGraph.RemoveNode(node);
        }

        public void AddConnection(NodeConnection connection)
        {
            NodeGraph.AddConnection(connection);
            if (connection.InputSocket?.ParentNode is Models.Nodes.SceneNode scene)
            {
                if (!scene.InputSockets.Any(s => s.SocketType == SocketType.Object && !s.IsConnected))
                    scene.AddObjectSocket();
                if (!scene.InputSockets.Any(s => s.SocketType == SocketType.Light && !s.IsConnected))
                    scene.AddLightSocket();
            }
        }

        public void RemoveConnection(NodeConnection connection, bool cleanupSceneNodeSockets = true)
        {
            NodeGraph.RemoveConnection(connection);
            if (cleanupSceneNodeSockets && connection.InputSocket?.ParentNode is Models.Nodes.SceneNode sceneNode)
                CleanupSceneNodeSockets(sceneNode);
        }

        /// <summary>
        /// 指定したノードに関連する接続リストを取得（O(1)アクセス）
        /// </summary>
        /// <param name="nodeId">ノードID</param>
        /// <returns>関連する接続のリスト（存在しない場合は空のリスト）</returns>
        public IReadOnlyList<NodeConnection> GetConnectionsForNode(Guid nodeId)
        {
            if (_nodeToConnections.TryGetValue(nodeId, out var relatedConnections))
            {
                return relatedConnections;
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
            foreach (var connection in Connections)
            {
                AddToNodeConnectionIndex(connection);
            }
        }

        /// <summary>
        /// 全SceneNodeのソケット数が「接続数+1（空きソケット1つ）」になるように調整する。
        /// ファイル読み込み後やUndo/Redo後のグラフ正規化として呼ばれる。
        /// </summary>
        public void EnsureSceneNodeSocketCounts()
        {
            foreach (var node in Nodes)
            {
                if (node is Models.Nodes.SceneNode sceneNode)
                {
                    // オブジェクトソケットをチェック
                    var objectSockets = sceneNode.InputSockets.Where(s => s.SocketType == Models.SocketType.Object).ToList();
                    var connectedObjectSockets = objectSockets.Count(s => Connections.Any(c => c.InputSocket == s));
                    var emptyObjectSockets = objectSockets.Count - connectedObjectSockets;

                    // 空のソケットが0個なら1個追加
                    if (emptyObjectSockets == 0)
                    {
                        sceneNode.AddObjectSocket();
                    }
                    // 空のソケットが2個以上なら余分を削除
                    else if (emptyObjectSockets > 1)
                    {
                        var emptySockets = objectSockets.Where(s => !Connections.Any(c => c.InputSocket == s)).Skip(1).ToList();
                        foreach (var socket in emptySockets)
                        {
                            sceneNode.RemoveSocket(socket.Name);
                        }
                    }

                    // ライトソケットをチェック
                    var lightSockets = sceneNode.InputSockets.Where(s => s.SocketType == Models.SocketType.Light).ToList();
                    var connectedLightSockets = lightSockets.Count(s => Connections.Any(c => c.InputSocket == s));
                    var emptyLightSockets = lightSockets.Count - connectedLightSockets;

                    // 空のソケットが0個なら1個追加
                    if (emptyLightSockets == 0)
                    {
                        sceneNode.AddLightSocket();
                    }
                    // 空のソケットが2個以上なら余分を削除
                    else if (emptyLightSockets > 1)
                    {
                        var emptySockets = lightSockets.Where(s => !Connections.Any(c => c.InputSocket == s)).Skip(1).ToList();
                        foreach (var socket in emptySockets)
                        {
                            sceneNode.RemoveSocket(socket.Name);
                        }
                    }
                }
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
                    .Where(s => !Connections.Any(c => c.InputSocket == s))
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
                    .Where(s => !Connections.Any(c => c.InputSocket == s))
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

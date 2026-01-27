using RayTraceVS.WPF.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;

namespace RayTraceVS.WPF.Models
{
    /// <summary>
    /// ノードグラフ操作時の例外
    /// </summary>
    public class NodeGraphException : Exception
    {
        public NodeGraphException(string message) : base(message) { }
        public NodeGraphException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class NodeGraph
    {
        private readonly Dictionary<Guid, Node> nodes;
        private readonly Dictionary<Guid, NodeConnection> connections;
        
        // パフォーマンス改善: 評価用オブジェクトの再利用
        private Dictionary<Guid, object?>? _evaluationResults;
        private HashSet<Guid>? _evaluatingNodes;
        
        // DirtyTracker（非再帰的伝播、重複防止）
        private readonly DirtyTracker _dirtyTracker;
        
        // 隣接リスト: ノードID → 下流ノードのSet（GetDownstreamNodesの高速化用）
        private readonly Dictionary<Guid, HashSet<Node>> _outgoingEdges = new();
        
        // トポロジカルソート: 評価順序のキャッシュ
        private List<Node>? _topologicalOrder;
        private bool _topologyDirty = true;

        /// <summary>
        /// シーンが変更されたときに発火するイベント
        /// </summary>
        public event EventHandler? SceneChanged;

        public NodeGraph()
        {
            nodes = new Dictionary<Guid, Node>();
            connections = new Dictionary<Guid, NodeConnection>();
            _dirtyTracker = new DirtyTracker(GetDownstreamNodes);
        }

        /// <summary>
        /// シーン変更を通知する
        /// </summary>
        public void NotifySceneChanged()
        {
            SceneChanged?.Invoke(this, EventArgs.Empty);
        }

        public void AddNode(Node node)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node), "追加するノードがnullです");
            }

            try
            {
                if (!nodes.ContainsKey(node.Id))
                {
                    nodes[node.Id] = node;
                    
                    // ノードのプロパティ変更を監視
                    node.PropertyChanged += OnNodePropertyChanged;
                    
                    // ノードのDirty変更を監視（downstream伝播用）
                    node.DirtyChanged += OnNodeDirtyChanged;
                    
                    // トポロジカル順序を無効化
                    InvalidateTopology();
                    
                    NotifySceneChanged();
                }
                else
                {
                    Debug.WriteLine($"NodeGraph.AddNode: ノード {node.Id} ({node.Title}) は既に存在します");
                }
            }
            catch (Exception ex) when (ex is not ArgumentNullException)
            {
                Debug.WriteLine($"NodeGraph.AddNode: エラー - {ex.Message}");
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
                if (nodes.ContainsKey(node.Id))
                {
                    // ノードのプロパティ変更監視を解除
                    node.PropertyChanged -= OnNodePropertyChanged;
                    
                    // ノードのDirty変更監視を解除
                    node.DirtyChanged -= OnNodeDirtyChanged;
                    
                    // ノードに接続されている接続を削除
                    var connectionsToRemove = connections.Values
                        .Where(c => c.InputSocket?.ParentNode?.Id == node.Id || 
                                   c.OutputSocket?.ParentNode?.Id == node.Id)
                        .ToList();

                    foreach (var connection in connectionsToRemove)
                    {
                        // 隣接リストから削除
                        var sourceNode = connection.OutputSocket?.ParentNode;
                        var targetNode = connection.InputSocket?.ParentNode;
                        if (sourceNode != null && targetNode != null)
                        {
                            if (_outgoingEdges.TryGetValue(sourceNode.Id, out var targets))
                            {
                                targets.Remove(targetNode);
                                if (targets.Count == 0)
                                {
                                    _outgoingEdges.Remove(sourceNode.Id);
                                }
                            }
                        }
                        
                        try
                        {
                            connection.Dispose(); // IDisposable対応
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"NodeGraph.RemoveNode: 接続 {connection.Id} のDisposeに失敗 - {ex.Message}");
                        }
                        connections.Remove(connection.Id);
                    }
                    
                    // このノードが出力元として持っていた隣接リストエントリも削除
                    _outgoingEdges.Remove(node.Id);

                    nodes.Remove(node.Id);
                    
                    // トポロジカル順序を無効化
                    InvalidateTopology();
                    
                    NotifySceneChanged();
                }
                else
                {
                    Debug.WriteLine($"NodeGraph.RemoveNode: ノード {node.Id} ({node.Title}) は存在しません");
                }
            }
            catch (Exception ex) when (ex is not ArgumentNullException)
            {
                Debug.WriteLine($"NodeGraph.RemoveNode: エラー - {ex.Message}");
                throw new NodeGraphException($"ノード '{node.Title}' の削除に失敗しました", ex);
            }
        }

        private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // 位置やIsSelected、IsDirty以外のプロパティが変更されたらシーン変更を通知
            if (e.PropertyName != nameof(Node.Position) && 
                e.PropertyName != nameof(Node.IsSelected) &&
                e.PropertyName != nameof(Node.IsDirty))
            {
                if (sender is Node node)
                {
                    // DirtyTrackerを使用して非再帰的に伝播
                    _dirtyTracker.MarkDirty(node);
                }
                NotifySceneChanged();
            }
        }

        /// <summary>
        /// ノードのDirty状態が変更されたときのハンドラ。
        /// MarkDirty()が直接呼ばれた場合に、downstreamへ伝播させる。
        /// </summary>
        private void OnNodeDirtyChanged(object? sender, EventArgs e)
        {
            if (sender is Node node)
            {
                // downstreamのみ伝播（自身は既にDirty）
                foreach (var downstream in GetDownstreamNodes(node))
                {
                    _dirtyTracker.MarkDirty(downstream);
                }
            }
        }

        /// <summary>
        /// 指定ノードの直接の下流ノードを取得する（O(out-degree)）
        /// </summary>
        public IEnumerable<Node> GetDownstreamNodes(Node node)
        {
            // 隣接リストから直接取得（O(out-degree)）
            if (_outgoingEdges.TryGetValue(node.Id, out var downstream))
            {
                return downstream;
            }
            return Enumerable.Empty<Node>();
        }

        /// <summary>
        /// 指定ノードの上流ノード（このノードが依存しているノード）を取得する
        /// </summary>
        public IEnumerable<Node> GetUpstreamNodes(Node node)
        {
            var upstreamNodes = new HashSet<Node>();
            
            foreach (var inputSocket in node.InputSockets)
            {
                var incomingConnection = connections.Values
                    .FirstOrDefault(c => c.InputSocket?.Id == inputSocket.Id);
                
                if (incomingConnection?.OutputSocket?.ParentNode != null)
                {
                    upstreamNodes.Add(incomingConnection.OutputSocket.ParentNode);
                }
            }
            
            return upstreamNodes;
        }

        public void AddConnection(NodeConnection connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection), "追加する接続がnullです");
            }

            try
            {
                if (!connections.ContainsKey(connection.Id))
                {
                    // 接続の妥当性を検証
                    if (connection.OutputSocket == null || connection.InputSocket == null)
                    {
                        throw new NodeGraphException("接続のソケットが不正です（OutputSocketまたはInputSocketがnull）");
                    }

                    connections[connection.Id] = connection;
                    
                    // 隣接リストを更新（出力側ノード → 入力側ノード）
                    var sourceNode = connection.OutputSocket.ParentNode;
                    var targetNode = connection.InputSocket.ParentNode;
                    if (sourceNode != null && targetNode != null)
                    {
                        if (!_outgoingEdges.TryGetValue(sourceNode.Id, out var targets))
                        {
                            targets = new HashSet<Node>();
                            _outgoingEdges[sourceNode.Id] = targets;
                        }
                        targets.Add(targetNode);
                    }
                    
                    // 接続が追加されたら、入力側のノードをDirtyにする
                    if (connection.InputSocket?.ParentNode != null)
                    {
                        _dirtyTracker.MarkDirty(connection.InputSocket.ParentNode);
                    }
                    
                    // トポロジカル順序を無効化
                    InvalidateTopology();
                    
                    NotifySceneChanged();
                }
                else
                {
                    Debug.WriteLine($"NodeGraph.AddConnection: 接続 {connection.Id} は既に存在します");
                }
            }
            catch (Exception ex) when (ex is not ArgumentNullException && ex is not NodeGraphException)
            {
                Debug.WriteLine($"NodeGraph.AddConnection: エラー - {ex.Message}");
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
                if (connections.ContainsKey(connection.Id))
                {
                    // 接続が削除される前に、入力側のノードをDirtyにする
                    if (connection.InputSocket?.ParentNode != null)
                    {
                        _dirtyTracker.MarkDirty(connection.InputSocket.ParentNode);
                    }
                    
                    // 隣接リストから削除
                    var sourceNode = connection.OutputSocket?.ParentNode;
                    var targetNode = connection.InputSocket?.ParentNode;
                    if (sourceNode != null && targetNode != null)
                    {
                        if (_outgoingEdges.TryGetValue(sourceNode.Id, out var targets))
                        {
                            targets.Remove(targetNode);
                            // 空になったらエントリごと削除
                            if (targets.Count == 0)
                            {
                                _outgoingEdges.Remove(sourceNode.Id);
                            }
                        }
                    }
                    
                    try
                    {
                        connection.Dispose(); // IDisposable対応
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"NodeGraph.RemoveConnection: 接続 {connection.Id} のDisposeに失敗 - {ex.Message}");
                    }
                    
                    connections.Remove(connection.Id);
                    
                    // トポロジカル順序を無効化
                    InvalidateTopology();
                    
                    NotifySceneChanged();
                }
                else
                {
                    Debug.WriteLine($"NodeGraph.RemoveConnection: 接続 {connection.Id} は存在しません");
                }
            }
            catch (Exception ex) when (ex is not ArgumentNullException)
            {
                Debug.WriteLine($"NodeGraph.RemoveConnection: エラー - {ex.Message}");
                throw new NodeGraphException("接続の削除に失敗しました", ex);
            }
        }

        /// <summary>
        /// トポロジカル順序を無効化する
        /// </summary>
        private void InvalidateTopology()
        {
            _topologyDirty = true;
        }

        /// <summary>
        /// トポロジカル順序が有効であることを保証する（必要に応じて再計算）
        /// </summary>
        private void EnsureTopologicalOrder()
        {
            if (!_topologyDirty && _topologicalOrder != null)
            {
                return;
            }
            
            _topologicalOrder = ComputeTopologicalOrder();
            _topologyDirty = false;
        }

        /// <summary>
        /// トポロジカルソートを計算する（Kahnのアルゴリズム）
        /// 循環が検出された場合はnullを返さず、処理可能なノードのみを返す
        /// </summary>
        private List<Node> ComputeTopologicalOrder()
        {
            var result = new List<Node>();
            
            // 入次数（依存元の数）を計算
            var inDegree = new Dictionary<Guid, int>();
            foreach (var node in nodes.Values)
            {
                inDegree[node.Id] = 0;
            }
            
            foreach (var edges in _outgoingEdges.Values)
            {
                foreach (var targetNode in edges)
                {
                    if (inDegree.ContainsKey(targetNode.Id))
                    {
                        inDegree[targetNode.Id]++;
                    }
                }
            }
            
            // 入次数が0のノードをキューに追加
            var queue = new Queue<Node>();
            foreach (var node in nodes.Values)
            {
                if (inDegree[node.Id] == 0)
                {
                    queue.Enqueue(node);
                }
            }
            
            // BFSでトポロジカル順序を生成
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                result.Add(current);
                
                // このノードの下流ノードの入次数を減らす
                if (_outgoingEdges.TryGetValue(current.Id, out var downstream))
                {
                    foreach (var targetNode in downstream)
                    {
                        if (inDegree.ContainsKey(targetNode.Id))
                        {
                            inDegree[targetNode.Id]--;
                            if (inDegree[targetNode.Id] == 0)
                            {
                                queue.Enqueue(targetNode);
                            }
                        }
                    }
                }
            }
            
            // 循環検出: 処理されなかったノードがある場合
            if (result.Count < nodes.Count)
            {
                var cycleNodes = nodes.Values.Where(n => !result.Contains(n)).ToList();
                Debug.WriteLine($"NodeGraph: 循環参照が検出されました。影響ノード数: {cycleNodes.Count}");
                foreach (var cycleNode in cycleNodes)
                {
                    Debug.WriteLine($"  - {cycleNode.Title} ({cycleNode.Id})");
                }
                
                // 循環に含まれるノードも追加（評価時にnullを返す）
                result.AddRange(cycleNodes);
            }
            
            return result;
        }

        /// <summary>
        /// グラフに循環参照があるかどうかを確認する
        /// </summary>
        public bool HasCycle()
        {
            EnsureTopologicalOrder();
            
            // ComputeTopologicalOrderで全ノードが処理されなかった場合、循環がある
            var inDegree = new Dictionary<Guid, int>();
            foreach (var node in nodes.Values)
            {
                inDegree[node.Id] = 0;
            }
            
            foreach (var edges in _outgoingEdges.Values)
            {
                foreach (var targetNode in edges)
                {
                    if (inDegree.ContainsKey(targetNode.Id))
                    {
                        inDegree[targetNode.Id]++;
                    }
                }
            }
            
            var queue = new Queue<Node>();
            foreach (var node in nodes.Values)
            {
                if (inDegree[node.Id] == 0)
                {
                    queue.Enqueue(node);
                }
            }
            
            int processedCount = 0;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                processedCount++;
                
                if (_outgoingEdges.TryGetValue(current.Id, out var downstream))
                {
                    foreach (var targetNode in downstream)
                    {
                        if (inDegree.ContainsKey(targetNode.Id))
                        {
                            inDegree[targetNode.Id]--;
                            if (inDegree[targetNode.Id] == 0)
                            {
                                queue.Enqueue(targetNode);
                            }
                        }
                    }
                }
            }
            
            return processedCount < nodes.Count;
        }

        /// <summary>
        /// すべてのノードをDirtyにする（完全再評価が必要な場合）
        /// </summary>
        public void MarkAllNodesDirty()
        {
            _dirtyTracker.MarkAllDirty(nodes.Values);
        }

        /// <summary>
        /// グラフを評価する（増分評価対応、トポロジカル順序で評価）
        /// Dirtyなノードのみ再評価し、それ以外はキャッシュを使用
        /// </summary>
        public Dictionary<Guid, object?> EvaluateGraph()
        {
            // トポロジカル順序を確保（必要に応じて再計算）
            EnsureTopologicalOrder();
            
            // 評価用オブジェクトを再利用（毎回newしない）
            _evaluationResults ??= new Dictionary<Guid, object?>();
            _evaluationResults.Clear();
            
            _evaluatingNodes ??= new HashSet<Guid>();
            _evaluatingNodes.Clear();

            // トポロジカル順序に従って評価（依存関係が保証される）
            if (_topologicalOrder != null)
            {
                foreach (var node in _topologicalOrder)
                {
                    // ノードがまだグラフに存在するか確認
                    if (!nodes.ContainsKey(node.Id))
                    {
                        continue;
                    }
                    
                    EvaluateNode(node, _evaluationResults, _evaluatingNodes);
                }
            }

            // 評価後にDirtyTrackerをクリア（次回のMarkDirtyが正しく機能するように）
            _dirtyTracker.ClearAfterEvaluation();

            return _evaluationResults;
        }

        /// <summary>
        /// グラフを完全に再評価する（すべてのキャッシュを無効化）
        /// </summary>
        public Dictionary<Guid, object?> EvaluateGraphFull()
        {
            MarkAllNodesDirty();
            return EvaluateGraph();
        }

        private object? EvaluateNode(Node node, Dictionary<Guid, object?> results, HashSet<Guid> evaluating)
        {
            // 既にこの評価サイクルで処理済みならresultsから返す
            if (results.TryGetValue(node.Id, out var sessionResult))
            {
                return sessionResult;
            }

            // 評価中のノードに再突入 → 循環参照
            if (evaluating.Contains(node.Id))
            {
                return null;
            }

            // ノードがDirtyでなければ、キャッシュを使用
            if (!node.IsDirty && node.CachedResult != null)
            {
                results[node.Id] = node.CachedResult;
                return node.CachedResult;
            }

            // 評価開始：評価中としてマーク
            evaluating.Add(node.Id);

            // 入力ソケットの値を収集
            var inputValues = new Dictionary<Guid, object?>();

            foreach (var inputSocket in node.InputSockets)
            {
                // この入力に接続されている出力を探す
                var connection = connections.Values.FirstOrDefault(c => c.InputSocket?.Id == inputSocket.Id);
                
                if (connection?.OutputSocket?.ParentNode != null)
                {
                    // 依存ノードを先に評価
                    var inputValue = EvaluateNode(connection.OutputSocket.ParentNode, results, evaluating);
                    inputValues[inputSocket.Id] = inputValue;
                }
            }

            // ノードを評価
            var result = node.Evaluate(inputValues);
            
            // キャッシュに保存してDirtyフラグをクリア
            node.SetCachedResult(result);
            results[node.Id] = result;

            // 評価完了：評価中マークを解除
            evaluating.Remove(node.Id);

            return result;
        }

        public Node? GetNodeById(Guid id)
        {
            return nodes.TryGetValue(id, out var node) ? node : null;
        }

        public IEnumerable<Node> GetAllNodes()
        {
            return nodes.Values;
        }

        public IEnumerable<NodeConnection> GetAllConnections()
        {
            return connections.Values;
        }
    }
}

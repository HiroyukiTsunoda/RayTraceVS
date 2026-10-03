using RayTraceVS.WPF.Utils;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        private readonly ObservableCollection<Node> _nodes = new();
        private readonly ObservableCollection<NodeConnection> _connections = new();

        public ReadOnlyObservableCollection<Node> Nodes { get; }
        public ReadOnlyObservableCollection<NodeConnection> Connections { get; }
        
        // パフォーマンス改善: 評価用オブジェクトの再利用
        private Dictionary<Guid, object?>? _evaluationResults;
        private HashSet<Guid>? _evaluatingNodes;
        
        // DirtyTracker（非再帰的伝播、重複防止）
        private readonly DirtyTracker _dirtyTracker;
        
        // 隣接リスト: ノードID → 下流ノードのSet（GetDownstreamNodesの高速化用）
        private readonly Dictionary<Guid, HashSet<Node>> _outgoingEdges = new();

        // 入力ソケットID → 接続のインデックス（EvaluateNode/GetUpstreamNodesのO(1)探索用）
        // AddConnectionで入力ソケットへの接続を最大1本に制限する。
        private readonly Dictionary<Guid, NodeConnection> _inputSocketIndex = new();

        // トポロジカルソート: 評価順序のキャッシュ
        private List<Node>? _topologicalOrder;
        private bool _topologyDirty = true;

        // 直近のトポロジカルソートで循環が検出されたか（HasCycle用キャッシュ）
        private bool _hasCycle;

        /// <summary>
        /// シーンが変更されたときに発火するイベント
        /// </summary>
        public event EventHandler? SceneChanged;

        public NodeGraph()
        {
            nodes = new Dictionary<Guid, Node>();
            connections = new Dictionary<Guid, NodeConnection>();
            Nodes = new ReadOnlyObservableCollection<Node>(_nodes);
            Connections = new ReadOnlyObservableCollection<NodeConnection>(_connections);
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
            ArgumentNullException.ThrowIfNull(node);
            if (nodes.TryGetValue(node.Id, out var existing))
            {
                if (ReferenceEquals(existing, node)) return;
                throw new NodeGraphException($"ノードID「{node.Id}」は既に使われています");
            }

            nodes.Add(node.Id, node);
            node.PropertyChanged += OnNodePropertyChanged;
            node.DirtyChanged += OnNodeDirtyChanged;
            node.InvalidateCacheOnly();
            InvalidateTopology();
            _nodes.Add(node);
            NotifySceneChanged();
        }

        public void RemoveNode(Node node)
        {
            ArgumentNullException.ThrowIfNull(node);
            if (!nodes.TryGetValue(node.Id, out var existing) || !ReferenceEquals(existing, node))
                return;

            // 接続削除の処理を共有し、残る下流ノードのキャッシュも無効化する。
            foreach (var connection in connections.Values.Where(c =>
                c.InputSocket?.ParentNode == node || c.OutputSocket?.ParentNode == node).ToList())
            {
                RemoveConnectionCore(connection);
            }

            node.PropertyChanged -= OnNodePropertyChanged;
            node.DirtyChanged -= OnNodeDirtyChanged;
            nodes.Remove(node.Id);
            _outgoingEdges.Remove(node.Id);
            InvalidateTopology();
            _nodes.Remove(node);
            NotifySceneChanged();
        }

        /// <summary>ノード、接続、索引、イベント購読をまとめて破棄する。</summary>
        public void Clear()
        {
            foreach (var connection in connections.Values)
            {
                connection.Deactivate();
                if (connection.InputSocket != null) connection.InputSocket.IsConnected = false;
            }
            foreach (var node in nodes.Values)
            {
                node.PropertyChanged -= OnNodePropertyChanged;
                node.DirtyChanged -= OnNodeDirtyChanged;
            }
            connections.Clear();
            nodes.Clear();
            _inputSocketIndex.Clear();
            _outgoingEdges.Clear();
            _evaluationResults?.Clear();
            _evaluatingNodes?.Clear();
            _dirtyTracker.ClearAfterEvaluation();
            InvalidateTopology();
            _connections.Clear();
            _nodes.Clear();
            NotifySceneChanged();
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
                _inputSocketIndex.TryGetValue(inputSocket.Id, out var incomingConnection);

                if (incomingConnection?.OutputSocket?.ParentNode != null)
                {
                    upstreamNodes.Add(incomingConnection.OutputSocket.ParentNode);
                }
            }
            
            return upstreamNodes;
        }

        /// <summary>接続のルールはUI、読み込み、Undo/Redoのすべてに適用する。</summary>
        public bool CanConnect(NodeSocket output, NodeSocket input)
        {
            return NodeSocket.AreCompatible(output, input) &&
                   output.ParentNode != input.ParentNode &&
                   IsRegisteredSocket(output) && IsRegisteredSocket(input) &&
                   !WouldCreateCycle(output.ParentNode!, input.ParentNode!);
        }

        private bool IsRegisteredSocket(NodeSocket socket)
        {
            var parent = socket.ParentNode;
            return parent != null && nodes.TryGetValue(parent.Id, out var registered) &&
                   ReferenceEquals(parent, registered) &&
                   (socket.IsInput ? parent.InputSockets : parent.OutputSockets).Contains(socket);
        }

        private bool WouldCreateCycle(Node source, Node target)
        {
            var pending = new Stack<Node>();
            var visited = new HashSet<Guid>();
            pending.Push(target);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (node == source) return true;
                if (!visited.Add(node.Id)) continue;
                foreach (var downstream in GetDownstreamNodes(node)) pending.Push(downstream);
            }
            return false;
        }

        public void AddConnection(NodeConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            if (connections.TryGetValue(connection.Id, out var existing))
            {
                if (ReferenceEquals(existing, connection)) return;
                throw new NodeGraphException($"接続ID「{connection.Id}」は既に使われています");
            }

            var output = connection.OutputSocket;
            var input = connection.InputSocket;
            if (output == null || input == null || !CanConnect(output, input))
                throw new NodeGraphException("接続の方向、型、所属ノードまたは循環参照が不正です");
            if (_inputSocketIndex.ContainsKey(input.Id))
                throw new NodeGraphException("入力ソケットには既に接続があります");

            // 購読はグラフに登録されている間だけ有効。Undoで同じ接続を再登録できる。
            connection.Activate();
            connections.Add(connection.Id, connection);
            _inputSocketIndex.Add(input.Id, connection);
            var source = output.ParentNode!;
            var target = input.ParentNode!;
            if (!_outgoingEdges.TryGetValue(source.Id, out var targets))
            {
                targets = new HashSet<Node>();
                _outgoingEdges.Add(source.Id, targets);
            }
            targets.Add(target);
            input.IsConnected = true;
            _dirtyTracker.MarkDirty(target);
            InvalidateTopology();
            _connections.Add(connection);
            NotifySceneChanged();
        }

        public void RemoveConnection(NodeConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            if (RemoveConnectionCore(connection)) NotifySceneChanged();
        }

        private bool RemoveConnectionCore(NodeConnection connection)
        {
            if (!connections.TryGetValue(connection.Id, out var existing) || !ReferenceEquals(existing, connection))
                return false;

            var source = connection.OutputSocket!.ParentNode!;
            var target = connection.InputSocket!.ParentNode!;
            connections.Remove(connection.Id);
            _inputSocketIndex.Remove(connection.InputSocket.Id);
            connection.InputSocket.IsConnected = false;
            connection.Deactivate();

            // 同じノード間に複数の接続がある場合は、最後の接続を削除したときに辺を消す。
            if (!connections.Values.Any(c => c.OutputSocket?.ParentNode == source && c.InputSocket?.ParentNode == target) &&
                _outgoingEdges.TryGetValue(source.Id, out var targets))
            {
                targets.Remove(target);
                if (targets.Count == 0) _outgoingEdges.Remove(source.Id);
            }

            _dirtyTracker.MarkDirty(target);
            InvalidateTopology();
            _connections.Remove(connection);
            return true;
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
            _hasCycle = result.Count < nodes.Count;
            if (_hasCycle)
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
            // ComputeTopologicalOrderが循環検出を兼ねるため、キャッシュ済みの結果を返す
            EnsureTopologicalOrder();
            return _hasCycle;
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
                // この入力に接続されている出力を探す（インデックスでO(1)）
                _inputSocketIndex.TryGetValue(inputSocket.Id, out var connection);

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

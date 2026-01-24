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
                    
                    // ノードに接続されている接続を削除
                    var connectionsToRemove = connections.Values
                        .Where(c => c.InputSocket?.ParentNode?.Id == node.Id || 
                                   c.OutputSocket?.ParentNode?.Id == node.Id)
                        .ToList();

                    foreach (var connection in connectionsToRemove)
                    {
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

                    nodes.Remove(node.Id);
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
        /// 指定ノードの直接の下流ノードを取得する
        /// </summary>
        public IEnumerable<Node> GetDownstreamNodes(Node node)
        {
            // このノードの出力ソケットに接続されている入力ソケットのノードを探す
            var downstreamNodes = new HashSet<Node>();
            
            foreach (var outputSocket in node.OutputSockets)
            {
                var outgoingConnections = connections.Values
                    .Where(c => c.OutputSocket?.Id == outputSocket.Id)
                    .ToList();
                
                foreach (var conn in outgoingConnections)
                {
                    if (conn.InputSocket?.ParentNode != null)
                    {
                        downstreamNodes.Add(conn.InputSocket.ParentNode);
                    }
                }
            }
            
            return downstreamNodes;
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
                    
                    // 接続が追加されたら、入力側のノードをDirtyにする
                    if (connection.InputSocket?.ParentNode != null)
                    {
                        _dirtyTracker.MarkDirty(connection.InputSocket.ParentNode);
                    }
                    
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
                    
                    try
                    {
                        connection.Dispose(); // IDisposable対応
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"NodeGraph.RemoveConnection: 接続 {connection.Id} のDisposeに失敗 - {ex.Message}");
                    }
                    
                    connections.Remove(connection.Id);
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
        /// すべてのノードをDirtyにする（完全再評価が必要な場合）
        /// </summary>
        public void MarkAllNodesDirty()
        {
            _dirtyTracker.MarkAllDirty(nodes.Values);
        }

        /// <summary>
        /// グラフを評価する（増分評価対応）
        /// Dirtyなノードのみ再評価し、それ以外はキャッシュを使用
        /// </summary>
        public Dictionary<Guid, object?> EvaluateGraph()
        {
            // 評価用オブジェクトを再利用（毎回newしない）
            _evaluationResults ??= new Dictionary<Guid, object?>();
            _evaluationResults.Clear();
            
            _evaluatingNodes ??= new HashSet<Guid>();
            _evaluatingNodes.Clear();

            foreach (var node in nodes.Values)
            {
                EvaluateNode(node, _evaluationResults, _evaluatingNodes);
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

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace RayTraceVS.WPF.Models
{
    public class NodeGraph
    {
        private Dictionary<Guid, Node> nodes;
        private Dictionary<Guid, NodeConnection> connections;

        /// <summary>
        /// シーンが変更されたときに発火するイベント
        /// </summary>
        public event EventHandler? SceneChanged;

        public NodeGraph()
        {
            nodes = new Dictionary<Guid, Node>();
            connections = new Dictionary<Guid, NodeConnection>();
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
            if (!nodes.ContainsKey(node.Id))
            {
                nodes[node.Id] = node;
                
                // ノードのプロパティ変更を監視
                node.PropertyChanged += OnNodePropertyChanged;
                
                NotifySceneChanged();
            }
        }

        public void RemoveNode(Node node)
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
                    connections.Remove(connection.Id);
                }

                nodes.Remove(node.Id);
                NotifySceneChanged();
            }
        }

        private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // 位置やIsSelected以外のプロパティが変更されたらシーン変更を通知
            if (e.PropertyName != nameof(Node.Position) && e.PropertyName != nameof(Node.IsSelected))
            {
                NotifySceneChanged();
            }
        }

        public void AddConnection(NodeConnection connection)
        {
            if (!connections.ContainsKey(connection.Id))
            {
                connections[connection.Id] = connection;
                NotifySceneChanged();
            }
        }

        public void RemoveConnection(NodeConnection connection)
        {
            if (connections.ContainsKey(connection.Id))
            {
                connections.Remove(connection.Id);
                NotifySceneChanged();
            }
        }

        public Dictionary<Guid, object?> EvaluateGraph()
        {
            var results = new Dictionary<Guid, object?>();
            var evaluating = new HashSet<Guid>();  // 現在評価中のノード（循環検出用）

            foreach (var node in nodes.Values)
            {
                EvaluateNode(node, results, evaluating);
            }

            return results;
        }

        private object? EvaluateNode(Node node, Dictionary<Guid, object?> results, HashSet<Guid> evaluating)
        {
            // 既に評価済みならキャッシュから返す
            if (results.TryGetValue(node.Id, out var cachedResult))
            {
                return cachedResult;
            }

            // 評価中のノードに再突入 → 循環参照
            if (evaluating.Contains(node.Id))
            {
                return null;
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

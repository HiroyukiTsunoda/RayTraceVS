using System;
using System.Collections.Generic;
using System.Linq;

namespace RayTraceVS.WPF.Models
{
    public class NodeGraph
    {
        private Dictionary<Guid, Node> nodes;
        private Dictionary<Guid, NodeConnection> connections;

        public NodeGraph()
        {
            nodes = new Dictionary<Guid, Node>();
            connections = new Dictionary<Guid, NodeConnection>();
        }

        public void AddNode(Node node)
        {
            if (!nodes.ContainsKey(node.Id))
            {
                nodes[node.Id] = node;
            }
        }

        public void RemoveNode(Node node)
        {
            if (nodes.ContainsKey(node.Id))
            {
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
            }
        }

        public void AddConnection(NodeConnection connection)
        {
            if (!connections.ContainsKey(connection.Id))
            {
                connections[connection.Id] = connection;
            }
        }

        public void RemoveConnection(NodeConnection connection)
        {
            if (connections.ContainsKey(connection.Id))
            {
                connections.Remove(connection.Id);
            }
        }

        public Dictionary<Guid, object?> EvaluateGraph()
        {
            var results = new Dictionary<Guid, object?>();
            var visited = new HashSet<Guid>();

            foreach (var node in nodes.Values)
            {
                EvaluateNode(node, results, visited);
            }

            return results;
        }

        private object? EvaluateNode(Node node, Dictionary<Guid, object?> results, HashSet<Guid> visited)
        {
            // 循環参照チェック
            if (visited.Contains(node.Id))
                return null;

            visited.Add(node.Id);

            // 入力ソケットの値を収集
            var inputValues = new Dictionary<Guid, object?>();

            foreach (var inputSocket in node.InputSockets)
            {
                // この入力に接続されている出力を探す
                var connection = connections.Values.FirstOrDefault(c => c.InputSocket?.Id == inputSocket.Id);
                
                if (connection?.OutputSocket?.ParentNode != null)
                {
                    // 依存ノードを先に評価
                    var inputValue = EvaluateNode(connection.OutputSocket.ParentNode, results, visited);
                    inputValues[inputSocket.Id] = inputValue;
                }
            }

            // ノードを評価
            var result = node.Evaluate(inputValues);
            results[node.Id] = result;

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

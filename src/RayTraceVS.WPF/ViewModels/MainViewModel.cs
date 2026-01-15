using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
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

        partial void OnSelectedNodeChanged(Node? value)
        {
            Debug.WriteLine($"SelectedNode changed to: {value?.Title ?? "null"}");
            
            // すべてのノードの情報を表示
            if (value != null)
            {
                Debug.WriteLine($"All nodes:");
                foreach (var node in Nodes)
                {
                    Debug.WriteLine($"  - {node.Title}: Position=({node.Position.X}, {node.Position.Y})");
                }
            }
        }

        [ObservableProperty]
        private ObservableCollection<Node> nodes;

        [ObservableProperty]
        private ObservableCollection<NodeConnection> connections;

        public MainViewModel()
        {
            nodeGraph = new NodeGraph();
            nodes = new ObservableCollection<Node>();
            connections = new ObservableCollection<NodeConnection>();
        }

        public void AddNode(Node node)
        {
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
                connections.Remove(connection);
            }

            nodes.Remove(node);
            nodeGraph.RemoveNode(node);
        }

        public void AddConnection(NodeConnection connection)
        {
            connections.Add(connection);
            nodeGraph.AddConnection(connection);
        }

        public void RemoveConnection(NodeConnection connection)
        {
            connections.Remove(connection);
            nodeGraph.RemoveConnection(connection);
        }
    }
}

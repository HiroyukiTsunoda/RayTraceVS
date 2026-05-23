using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Nodes;
using RayTraceVS.WPF.Models.Serialization;

namespace RayTraceVS.WPF.Services
{
    public class SceneFileService
    {
        /// <summary>
        /// 読み込み時に除外されたノードの情報
        /// </summary>
        public List<string> RemovedNodeInfos { get; private set; } = new();
        public void SaveScene(string filePath, ObservableCollection<Node> nodes, ObservableCollection<NodeConnection> connections, ViewportState? viewportState = null)
        {
            var sceneData = new SceneFileData
            {
                Version = "1.0",
                Nodes = nodes.Select(n => SerializeNode(n)).ToList(),
                Connections = connections.Select(c => SerializeConnection(c)).ToList(),
                Viewport = viewportState
            };

            var json = JsonConvert.SerializeObject(sceneData, Formatting.Indented);

            File.WriteAllText(filePath, json);
        }

        public (List<Node>, List<NodeConnection>, ViewportState?) LoadScene(string filePath)
        {
            // 除外ノード情報をクリア
            RemovedNodeInfos.Clear();

            var json = File.ReadAllText(filePath);
            var sceneData = JsonConvert.DeserializeObject<SceneFileData>(json);

            if (sceneData == null)
                throw new Exception("Invalid scene file format");

            var nodes = sceneData.Nodes
                .Select(n => DeserializeNode(n))
                .Where(n => n != null)
                .Select(n => n!)
                .ToList();

            // キャッシュにないFBXMeshNodeを除外
            var removedFBXNodes = nodes.OfType<FBXMeshNode>()
                .Where(n => !App.MeshCacheService.HasMesh(n.MeshName))
                .ToList();

            foreach (var removedNode in removedFBXNodes)
            {
                RemovedNodeInfos.Add($"FBXMesh: {removedNode.MeshName} (キャッシュにありません)");
                nodes.Remove(removedNode);
                Debug.WriteLine($"FBXMeshNode '{removedNode.MeshName}' を除外しました（キャッシュにありません）");
            }

            // 古いシーンファイルの互換性のため、接続データからSceneNodeに必要なソケットを準備
            PrepareSceneNodeSockets(nodes, sceneData.Connections);

            var connections = sceneData.Connections
                .Select(c => DeserializeConnection(c, nodes))
                .Where(c => c != null)
                .Select(c => c!)
                .ToList();

            return (nodes, connections, sceneData.Viewport);
        }

        /// <summary>
        /// 接続データを見て、SceneNodeに必要なソケットを準備（古いファイルとの互換性のため）
        /// </summary>
        private void PrepareSceneNodeSockets(List<Node> nodes, List<ConnectionData> connections)
        {
            foreach (var sceneNode in nodes.OfType<SceneNode>())
            {
                // このSceneNodeへの接続を見つける
                var sceneNodeConnections = connections.Where(c => c.InputNodeId == sceneNode.Id).ToList();

                foreach (var conn in sceneNodeConnections)
                {
                    // ソケット名が既に存在するかチェック
                    var existingSocket = sceneNode.InputSockets.FirstOrDefault(s => s.Name == conn.InputSocketName);
                    if (existingSocket == null)
                    {
                        // ソケットが存在しない場合、ソケットタイプを推測して作成
                        if (conn.InputSocketName.StartsWith("オブジェクト"))
                        {
                            sceneNode.AddNamedInputSocket(conn.InputSocketName, SocketType.Object);
                        }
                        else if (conn.InputSocketName.StartsWith("ライト"))
                        {
                            sceneNode.AddNamedInputSocket(conn.InputSocketName, SocketType.Light);
                        }
                    }
                }

                // カウンタを復元
                sceneNode.RestoreSocketCounters();
            }
        }

        /// <summary>
        /// ノードをNodeDataにシリアライズ（公開：クリップボード等から共用可能）
        /// </summary>
        public static NodeData SerializeNode(Node node)
        {
            return new NodeData
            {
                Id = node.Id,
                Type = node.GetType().Name,
                Title = node.Title,
                PositionX = node.Position.X,
                PositionY = node.Position.Y,
                Properties = SerializeNodeProperties(node)
            };
        }

        /// <summary>
        /// NodeDataからノードをデシリアライズ（公開：クリップボード等から共用可能）
        /// </summary>
        public static Node? DeserializeNode(NodeData data)
        {
            Node? node = NodeRegistry.CreateNodeByClassName(data.Type);

            if (node != null)
            {
                node.Id = data.Id;
                node.Position = new System.Windows.Point(data.PositionX, data.PositionY);
                DeserializeNodeProperties(node, data.Properties);
            }

            return node;
        }

        /// <summary>
        /// ノードプロパティのシリアライズ（ISerializableNode経由）
        /// </summary>
        public static Dictionary<string, object?> SerializeNodeProperties(Node node)
        {
            var properties = new Dictionary<string, object?>();
            if (node is ISerializableNode serializable)
            {
                serializable.SerializeProperties(properties);
            }
            return properties;
        }

        /// <summary>
        /// ノードプロパティのデシリアライズ（ISerializableNode経由）
        /// </summary>
        public static void DeserializeNodeProperties(Node node, Dictionary<string, object?>? properties)
        {
            if (node is ISerializableNode serializable)
            {
                serializable.DeserializeProperties(
                    properties ?? new Dictionary<string, object?>());
            }
        }

        private ConnectionData SerializeConnection(NodeConnection connection)
        {
            return new ConnectionData
            {
                OutputNodeId = connection.OutputSocket?.ParentNode?.Id ?? Guid.Empty,
                OutputSocketName = connection.OutputSocket?.Name ?? string.Empty,
                InputNodeId = connection.InputSocket?.ParentNode?.Id ?? Guid.Empty,
                InputSocketName = connection.InputSocket?.Name ?? string.Empty
            };
        }

        private NodeConnection? DeserializeConnection(ConnectionData data, List<Node> nodes)
        {
            var outputNode = nodes.FirstOrDefault(n => n.Id == data.OutputNodeId);
            var inputNode = nodes.FirstOrDefault(n => n.Id == data.InputNodeId);

            if (outputNode == null || inputNode == null)
                return null;

            var outputSocket = outputNode.OutputSockets.FirstOrDefault(s => s.Name == data.OutputSocketName);
            var inputSocket = inputNode.InputSockets.FirstOrDefault(s => s.Name == data.InputSocketName);

            if (outputSocket == null || inputSocket == null)
                return null;

            return new NodeConnection(outputSocket, inputSocket);
        }

        /// <summary>
        /// シーンファイルのデータ構造
        /// </summary>
        public class SceneFileData
        {
            public string Version { get; set; } = "1.0";
            public List<NodeData> Nodes { get; set; } = new();
            public List<ConnectionData> Connections { get; set; } = new();
            public ViewportState? Viewport { get; set; }
        }

        /// <summary>
        /// ノードデータ構造（公開：クリップボード等でも使用）
        /// </summary>
        public class NodeData
        {
            public Guid Id { get; set; }
            public string Type { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public double PositionX { get; set; }
            public double PositionY { get; set; }
            public Dictionary<string, object?>? Properties { get; set; }
        }

        /// <summary>
        /// 接続データ構造
        /// </summary>
        public class ConnectionData
        {
            public Guid OutputNodeId { get; set; }
            public string OutputSocketName { get; set; } = string.Empty;
            public Guid InputNodeId { get; set; }
            public string InputSocketName { get; set; } = string.Empty;
        }
    }

    /// <summary>
    /// ビューポートの状態（パンとズーム）とパネルの開閉状態
    /// </summary>
    public class ViewportState
    {
        public double PanX { get; set; }
        public double PanY { get; set; }
        public double Zoom { get; set; } = 1.0;

        // パネルの開閉状態（シーンごとに保存）
        public bool IsLeftPanelVisible { get; set; } = true;
        public bool IsRightPanelVisible { get; set; } = true;

        // コンポーネントパレットのExpander開閉状態
        public ExpanderStates? ExpanderStates { get; set; }

        // レンダリング解像度（デフォルトは1920x1080）
        public int RenderWidth { get; set; } = 1920;
        public int RenderHeight { get; set; } = 1080;
    }

    /// <summary>
    /// コンポーネントパレットの各カテゴリのExpander開閉状態
    /// </summary>
    public class ExpanderStates
    {
        public bool IsObjectExpanded { get; set; } = true;
        public bool IsFBXObjectExpanded { get; set; } = false;
        public bool IsMaterialExpanded { get; set; } = false;
        public bool IsMathExpanded { get; set; } = false;
        public bool IsCameraExpanded { get; set; } = false;
        public bool IsLightExpanded { get; set; } = false;
        public bool IsSceneExpanded { get; set; } = false;
    }
}

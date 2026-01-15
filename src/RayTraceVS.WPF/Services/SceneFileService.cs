using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Nodes;

namespace RayTraceVS.WPF.Services
{
    public class SceneFileService
    {
        public void SaveScene(string filePath, ObservableCollection<Node> nodes, ObservableCollection<NodeConnection> connections)
        {
            var sceneData = new SceneFileData
            {
                Version = "1.0",
                Nodes = nodes.Select(n => SerializeNode(n)).ToList(),
                Connections = connections.Select(c => SerializeConnection(c)).ToList()
            };

            var json = JsonConvert.SerializeObject(sceneData, Formatting.Indented, new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects
            });

            File.WriteAllText(filePath, json);
        }

        public (List<Node>, List<NodeConnection>) LoadScene(string filePath)
        {
            var json = File.ReadAllText(filePath);
            var sceneData = JsonConvert.DeserializeObject<SceneFileData>(json, new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects
            });

            if (sceneData == null)
                throw new Exception("Invalid scene file format");

            var nodes = sceneData.Nodes.Select(n => DeserializeNode(n)).Where(n => n != null).ToList()!;
            var connections = sceneData.Connections.Select(c => DeserializeConnection(c, nodes)).Where(c => c != null).ToList()!;

            return (nodes, connections);
        }

        private NodeData SerializeNode(Node node)
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

        private Node? DeserializeNode(NodeData data)
        {
            Node? node = data.Type switch
            {
                nameof(SphereNode) => new SphereNode(),
                nameof(PlaneNode) => new PlaneNode(),
                nameof(CylinderNode) => new CylinderNode(),
                nameof(CameraNode) => new CameraNode(),
                nameof(LightNode) => new LightNode(),
                nameof(SceneNode) => new SceneNode(),
                nameof(Vector3Node) => new Vector3Node(),
                _ => null
            };

            if (node != null)
            {
                node.Id = data.Id;
                node.Position = new System.Windows.Point(data.PositionX, data.PositionY);
                DeserializeNodeProperties(node, data.Properties);
            }

            return node;
        }

        private Dictionary<string, object?> SerializeNodeProperties(Node node)
        {
            var properties = new Dictionary<string, object?>();

            // ノードタイプごとにプロパティをシリアライズ
            switch (node)
            {
                case SphereNode sphere:
                    properties["Position"] = sphere.ObjectPosition;
                    properties["Radius"] = sphere.Radius;
                    properties["Color"] = sphere.Color;
                    properties["Reflectivity"] = sphere.Reflectivity;
                    properties["Transparency"] = sphere.Transparency;
                    properties["IOR"] = sphere.IOR;
                    break;

                case PlaneNode plane:
                    properties["Position"] = plane.ObjectPosition;
                    properties["Normal"] = plane.Normal;
                    properties["Color"] = plane.Color;
                    properties["Reflectivity"] = plane.Reflectivity;
                    break;

                case CylinderNode cylinder:
                    properties["Position"] = cylinder.ObjectPosition;
                    properties["Axis"] = cylinder.Axis;
                    properties["Radius"] = cylinder.Radius;
                    properties["Height"] = cylinder.Height;
                    properties["Color"] = cylinder.Color;
                    properties["Reflectivity"] = cylinder.Reflectivity;
                    break;

                case CameraNode camera:
                    properties["Position"] = camera.ObjectPosition;
                    properties["LookAt"] = camera.LookAt;
                    properties["Up"] = camera.Up;
                    properties["FieldOfView"] = camera.FieldOfView;
                    break;

                case LightNode light:
                    properties["Position"] = light.ObjectPosition;
                    properties["Color"] = light.Color;
                    properties["Intensity"] = light.Intensity;
                    break;

                case Vector3Node vector3:
                    properties["X"] = vector3.X;
                    properties["Y"] = vector3.Y;
                    properties["Z"] = vector3.Z;
                    break;
            }

            return properties;
        }

        private void DeserializeNodeProperties(Node node, Dictionary<string, object?>? properties)
        {
            if (properties == null)
                return;

            // JSONからの変換で型が異なる場合があるため、Convertを使用
            switch (node)
            {
                case SphereNode sphere:
                    if (properties.TryGetValue("Position", out var spherePos))
                        sphere.ObjectPosition = ConvertToVector3(spherePos);
                    if (properties.TryGetValue("Radius", out var radius))
                        sphere.Radius = Convert.ToSingle(radius);
                    if (properties.TryGetValue("Color", out var sphereColor))
                        sphere.Color = ConvertToVector4(sphereColor);
                    if (properties.TryGetValue("Reflectivity", out var reflectivity))
                        sphere.Reflectivity = Convert.ToSingle(reflectivity);
                    if (properties.TryGetValue("Transparency", out var transparency))
                        sphere.Transparency = Convert.ToSingle(transparency);
                    if (properties.TryGetValue("IOR", out var ior))
                        sphere.IOR = Convert.ToSingle(ior);
                    break;

                case PlaneNode plane:
                    if (properties.TryGetValue("Position", out var planePos))
                        plane.ObjectPosition = ConvertToVector3(planePos);
                    if (properties.TryGetValue("Normal", out var normal))
                        plane.Normal = ConvertToVector3(normal);
                    if (properties.TryGetValue("Color", out var planeColor))
                        plane.Color = ConvertToVector4(planeColor);
                    if (properties.TryGetValue("Reflectivity", out var planeReflectivity))
                        plane.Reflectivity = Convert.ToSingle(planeReflectivity);
                    break;

                case CylinderNode cylinder:
                    if (properties.TryGetValue("Position", out var cylPos))
                        cylinder.ObjectPosition = ConvertToVector3(cylPos);
                    if (properties.TryGetValue("Axis", out var axis))
                        cylinder.Axis = ConvertToVector3(axis);
                    if (properties.TryGetValue("Radius", out var cylRadius))
                        cylinder.Radius = Convert.ToSingle(cylRadius);
                    if (properties.TryGetValue("Height", out var height))
                        cylinder.Height = Convert.ToSingle(height);
                    if (properties.TryGetValue("Color", out var cylColor))
                        cylinder.Color = ConvertToVector4(cylColor);
                    if (properties.TryGetValue("Reflectivity", out var cylReflectivity))
                        cylinder.Reflectivity = Convert.ToSingle(cylReflectivity);
                    break;

                case CameraNode camera:
                    if (properties.TryGetValue("Position", out var camPos))
                        camera.ObjectPosition = ConvertToVector3(camPos);
                    if (properties.TryGetValue("LookAt", out var lookAt))
                        camera.LookAt = ConvertToVector3(lookAt);
                    if (properties.TryGetValue("Up", out var up))
                        camera.Up = ConvertToVector3(up);
                    if (properties.TryGetValue("FieldOfView", out var fov))
                        camera.FieldOfView = Convert.ToSingle(fov);
                    break;

                case LightNode light:
                    if (properties.TryGetValue("Position", out var lightPos))
                        light.ObjectPosition = ConvertToVector3(lightPos);
                    if (properties.TryGetValue("Color", out var lightColor))
                        light.Color = ConvertToVector4(lightColor);
                    if (properties.TryGetValue("Intensity", out var intensity))
                        light.Intensity = Convert.ToSingle(intensity);
                    break;

                case Vector3Node vector3:
                    if (properties.TryGetValue("X", out var x))
                        vector3.X = Convert.ToSingle(x);
                    if (properties.TryGetValue("Y", out var y))
                        vector3.Y = Convert.ToSingle(y);
                    if (properties.TryGetValue("Z", out var z))
                        vector3.Z = Convert.ToSingle(z);
                    break;
            }
        }
        
        private System.Numerics.Vector3 ConvertToVector3(object? obj)
        {
            if (obj == null)
                return System.Numerics.Vector3.Zero;
                
            if (obj is System.Numerics.Vector3 vec3)
                return vec3;
                
            // Newtonsoft.Jsonからのデシリアライズで、JObjectになっている可能性がある
            if (obj is Newtonsoft.Json.Linq.JObject jobj)
            {
                return new System.Numerics.Vector3(
                    jobj["X"]?.Value<float>() ?? 0,
                    jobj["Y"]?.Value<float>() ?? 0,
                    jobj["Z"]?.Value<float>() ?? 0
                );
            }
            
            return System.Numerics.Vector3.Zero;
        }
        
        private System.Numerics.Vector4 ConvertToVector4(object? obj)
        {
            if (obj == null)
                return System.Numerics.Vector4.One;
                
            if (obj is System.Numerics.Vector4 vec4)
                return vec4;
                
            // Newtonsoft.Jsonからのデシリアライズで、JObjectになっている可能性がある
            if (obj is Newtonsoft.Json.Linq.JObject jobj)
            {
                return new System.Numerics.Vector4(
                    jobj["X"]?.Value<float>() ?? 0,
                    jobj["Y"]?.Value<float>() ?? 0,
                    jobj["Z"]?.Value<float>() ?? 0,
                    jobj["W"]?.Value<float>() ?? 1
                );
            }
            
            return System.Numerics.Vector4.One;
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

        private class SceneFileData
        {
            public string Version { get; set; } = "1.0";
            public List<NodeData> Nodes { get; set; } = new();
            public List<ConnectionData> Connections { get; set; } = new();
        }

        private class NodeData
        {
            public Guid Id { get; set; }
            public string Type { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public double PositionX { get; set; }
            public double PositionY { get; set; }
            public Dictionary<string, object?>? Properties { get; set; }
        }

        private class ConnectionData
        {
            public Guid OutputNodeId { get; set; }
            public string OutputSocketName { get; set; } = string.Empty;
            public Guid InputNodeId { get; set; }
            public string InputSocketName { get; set; } = string.Empty;
        }
    }
}

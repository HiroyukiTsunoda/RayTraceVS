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
            var json = File.ReadAllText(filePath);
            var sceneData = JsonConvert.DeserializeObject<SceneFileData>(json);

            if (sceneData == null)
                throw new Exception("Invalid scene file format");

            var nodes = sceneData.Nodes
                .Select(n => DeserializeNode(n))
                .Where(n => n != null)
                .Select(n => n!)
                .ToList();
            
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
                nameof(BoxNode) => new BoxNode(),
                nameof(CameraNode) => new CameraNode(),
                "LightNode" => new PointLightNode(),  // 旧LightNodeはPointLightNodeとして読み込む
                nameof(PointLightNode) => new PointLightNode(),
                nameof(AmbientLightNode) => new AmbientLightNode(),
                nameof(DirectionalLightNode) => new DirectionalLightNode(),
                nameof(MaterialBSDFNode) => new MaterialBSDFNode(),
                nameof(ColorNode) => new ColorNode(),
                nameof(DiffuseMaterialNode) => new DiffuseMaterialNode(),
                nameof(GlassMaterialNode) => new GlassMaterialNode(),
                nameof(MetalMaterialNode) => new MetalMaterialNode(),
                nameof(EmissionMaterialNode) => new EmissionMaterialNode(),
                nameof(SceneNode) => new SceneNode(),
                nameof(Vector3Node) => new Vector3Node(),
                nameof(Vector4Node) => new Vector4Node(),
                nameof(FloatNode) => new FloatNode(),
                nameof(AddNode) => new AddNode(),
                nameof(SubNode) => new SubNode(),
                nameof(MulNode) => new MulNode(),
                nameof(DivNode) => new DivNode(),
                nameof(TransformNode) => new TransformNode(),
                nameof(CombineTransformNode) => new CombineTransformNode(),
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
                    properties["Transform"] = sphere.ObjectTransform;
                    properties["Radius"] = sphere.Radius;
                    break;

                case PlaneNode plane:
                    properties["Transform"] = plane.ObjectTransform;
                    properties["Normal"] = plane.Normal;
                    break;

                case BoxNode box:
                    properties["Transform"] = box.ObjectTransform;
                    properties["Size"] = box.Size;
                    break;

                case CameraNode camera:
                    properties["CameraPosition"] = camera.CameraPosition;
                    properties["LookAt"] = camera.LookAt;
                    properties["Up"] = camera.Up;
                    properties["FieldOfView"] = camera.FieldOfView;
                    properties["Near"] = camera.Near;
                    properties["Far"] = camera.Far;
                    properties["ApertureSize"] = camera.ApertureSize;
                    properties["FocusDistance"] = camera.FocusDistance;
                    break;

                case PointLightNode pointLight:
                    properties["LightPosition"] = pointLight.LightPosition;
                    properties["Color"] = pointLight.Color;
                    properties["Intensity"] = pointLight.Intensity;
                    properties["Attenuation"] = pointLight.Attenuation;
                    break;

                case AmbientLightNode ambientLight:
                    properties["Color"] = ambientLight.Color;
                    properties["Intensity"] = ambientLight.Intensity;
                    break;

                case DirectionalLightNode directionalLight:
                    properties["Direction"] = directionalLight.Direction;
                    properties["Color"] = directionalLight.Color;
                    properties["Intensity"] = directionalLight.Intensity;
                    break;

                case MaterialBSDFNode material:
                    properties["BaseColor"] = material.BaseColor;
                    properties["Metallic"] = material.Metallic;
                    properties["Roughness"] = material.Roughness;
                    properties["Transmission"] = material.Transmission;
                    properties["IOR"] = material.IOR;
                    properties["Emission"] = material.Emission;
                    break;

                case ColorNode color:
                    properties["R"] = color.R;
                    properties["G"] = color.G;
                    properties["B"] = color.B;
                    properties["A"] = color.A;
                    break;

                case DiffuseMaterialNode diffuse:
                    properties["BaseColor"] = diffuse.BaseColor;
                    properties["Roughness"] = diffuse.Roughness;
                    break;

                case GlassMaterialNode glass:
                    properties["Color"] = glass.Color;
                    properties["Roughness"] = glass.Roughness;
                    properties["IOR"] = glass.IOR;
                    properties["Transparency"] = glass.Transparency;
                    break;

                case MetalMaterialNode metal:
                    properties["BaseColor"] = metal.BaseColor;
                    properties["Roughness"] = metal.Roughness;
                    break;

                case EmissionMaterialNode emission:
                    properties["EmissionColor"] = emission.EmissionColor;
                    properties["Strength"] = emission.Strength;
                    properties["BaseColor"] = emission.BaseColor;
                    break;

                case SceneNode sceneNode:
                    // シーンノードのソケット名を保存
                    var objectSocketNames = sceneNode.InputSockets
                        .Where(s => s.SocketType == SocketType.Object)
                        .Select(s => s.Name)
                        .ToList();
                    var lightSocketNames = sceneNode.InputSockets
                        .Where(s => s.SocketType == SocketType.Light)
                        .Select(s => s.Name)
                        .ToList();
                    properties["ObjectSocketNames"] = objectSocketNames;
                    properties["LightSocketNames"] = lightSocketNames;
                    properties["SamplesPerPixel"] = sceneNode.SamplesPerPixel;
                    properties["MaxBounces"] = sceneNode.MaxBounces;
                    properties["Exposure"] = sceneNode.Exposure;
                    properties["ToneMapOperator"] = sceneNode.ToneMapOperator;
                    properties["DenoiserStabilization"] = sceneNode.DenoiserStabilization;
                    properties["ShadowStrength"] = sceneNode.ShadowStrength;
                    properties["EnableDenoiser"] = sceneNode.EnableDenoiser;
                    break;

                case Vector3Node vector3:
                    properties["X"] = vector3.X;
                    properties["Y"] = vector3.Y;
                    properties["Z"] = vector3.Z;
                    break;

                case Vector4Node vector4:
                    properties["X"] = vector4.X;
                    properties["Y"] = vector4.Y;
                    properties["Z"] = vector4.Z;
                    properties["W"] = vector4.W;
                    break;

                case FloatNode floatNode:
                    properties["Value"] = floatNode.Value;
                    break;

                case TransformNode transformNode:
                    properties["PositionX"] = transformNode.PositionX;
                    properties["PositionY"] = transformNode.PositionY;
                    properties["PositionZ"] = transformNode.PositionZ;
                    properties["RotationX"] = transformNode.RotationX;
                    properties["RotationY"] = transformNode.RotationY;
                    properties["RotationZ"] = transformNode.RotationZ;
                    properties["ScaleX"] = transformNode.ScaleX;
                    properties["ScaleY"] = transformNode.ScaleY;
                    properties["ScaleZ"] = transformNode.ScaleZ;
                    break;

                case CombineTransformNode:
                    // CombineTransformNodeにはプロパティがない（入力ソケットのみ）
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
                    if (properties.TryGetValue("Transform", out var sphereTransform))
                        sphere.ObjectTransform = ConvertToTransform(sphereTransform);
                    // 後方互換性: 旧形式のPositionがあればTransformに変換
                    else if (properties.TryGetValue("Position", out var spherePos))
                    {
                        var transform = Transform.Identity;
                        transform.Position = ConvertToVector3(spherePos);
                        sphere.ObjectTransform = transform;
                    }
                    if (properties.TryGetValue("Radius", out var radius))
                        sphere.Radius = Convert.ToSingle(radius);
                    break;

                case PlaneNode plane:
                    if (properties.TryGetValue("Transform", out var planeTransform))
                        plane.ObjectTransform = ConvertToTransform(planeTransform);
                    // 後方互換性
                    else if (properties.TryGetValue("Position", out var planePos))
                    {
                        var transform = Transform.Identity;
                        transform.Position = ConvertToVector3(planePos);
                        plane.ObjectTransform = transform;
                    }
                    if (properties.TryGetValue("Normal", out var normal))
                        plane.Normal = ConvertToVector3(normal);
                    break;

                case BoxNode box:
                    if (properties.TryGetValue("Transform", out var boxTransform))
                        box.ObjectTransform = ConvertToTransform(boxTransform);
                    else if (properties.TryGetValue("Position", out var boxPos))
                    {
                        var transform = Transform.Identity;
                        transform.Position = ConvertToVector3(boxPos);
                        box.ObjectTransform = transform;
                    }
                    if (properties.TryGetValue("Size", out var size))
                        box.Size = ConvertToVector3(size);
                    break;

                case CameraNode camera:
                    // 新形式
                    if (properties.TryGetValue("CameraPosition", out var camPos))
                        camera.CameraPosition = ConvertToVector3(camPos);
                    // 後方互換性: 旧形式のPosition
                    else if (properties.TryGetValue("Position", out var oldCamPos))
                        camera.CameraPosition = ConvertToVector3(oldCamPos);
                    
                    if (properties.TryGetValue("LookAt", out var lookAt))
                        camera.LookAt = ConvertToVector3(lookAt);
                    if (properties.TryGetValue("Up", out var up))
                        camera.Up = ConvertToVector3(up);
                    if (properties.TryGetValue("FieldOfView", out var fov))
                        camera.FieldOfView = Convert.ToSingle(fov);
                    if (properties.TryGetValue("Near", out var near))
                        camera.Near = Convert.ToSingle(near);
                    if (properties.TryGetValue("Far", out var far))
                        camera.Far = Convert.ToSingle(far);
                    if (properties.TryGetValue("ApertureSize", out var aperture))
                        camera.ApertureSize = Convert.ToSingle(aperture);
                    if (properties.TryGetValue("FocusDistance", out var focusDist))
                        camera.FocusDistance = Convert.ToSingle(focusDist);
                    break;

                case PointLightNode pointLight:
                    // 新形式
                    if (properties.TryGetValue("LightPosition", out var lightPos))
                        pointLight.LightPosition = ConvertToVector3(lightPos);
                    // 後方互換性: 旧形式のPosition
                    else if (properties.TryGetValue("Position", out var oldLightPos))
                        pointLight.LightPosition = ConvertToVector3(oldLightPos);
                    
                    if (properties.TryGetValue("Color", out var pointLightColor))
                        pointLight.Color = ConvertToVector4(pointLightColor);
                    if (properties.TryGetValue("Intensity", out var pointIntensity))
                        pointLight.Intensity = Convert.ToSingle(pointIntensity);
                    if (properties.TryGetValue("Attenuation", out var attenuation))
                        pointLight.Attenuation = Convert.ToSingle(attenuation);
                    break;

                case AmbientLightNode ambientLight:
                    if (properties.TryGetValue("Color", out var ambientColor))
                        ambientLight.Color = ConvertToVector4(ambientColor);
                    if (properties.TryGetValue("Intensity", out var ambientIntensity))
                        ambientLight.Intensity = Convert.ToSingle(ambientIntensity);
                    break;

                case DirectionalLightNode directionalLight:
                    if (properties.TryGetValue("Direction", out var direction))
                        directionalLight.Direction = ConvertToVector3(direction);
                    if (properties.TryGetValue("Color", out var dirColor))
                        directionalLight.Color = ConvertToVector4(dirColor);
                    if (properties.TryGetValue("Intensity", out var dirIntensity))
                        directionalLight.Intensity = Convert.ToSingle(dirIntensity);
                    break;

                case MaterialBSDFNode material:
                    if (properties.TryGetValue("BaseColor", out var baseColor))
                        material.BaseColor = ConvertToVector4(baseColor);
                    if (properties.TryGetValue("Metallic", out var metallic))
                        material.Metallic = Convert.ToSingle(metallic);
                    if (properties.TryGetValue("Roughness", out var roughness))
                        material.Roughness = Convert.ToSingle(roughness);
                    if (properties.TryGetValue("Transmission", out var transmission))
                        material.Transmission = Convert.ToSingle(transmission);
                    if (properties.TryGetValue("IOR", out var ior))
                        material.IOR = Convert.ToSingle(ior);
                    if (properties.TryGetValue("Emission", out var emission))
                        material.Emission = ConvertToVector4(emission);
                    break;

                case ColorNode color:
                    if (properties.TryGetValue("R", out var r))
                        color.R = Convert.ToSingle(r);
                    if (properties.TryGetValue("G", out var g))
                        color.G = Convert.ToSingle(g);
                    if (properties.TryGetValue("B", out var b))
                        color.B = Convert.ToSingle(b);
                    if (properties.TryGetValue("A", out var a))
                        color.A = Convert.ToSingle(a);
                    break;

                case DiffuseMaterialNode diffuse:
                    if (properties.TryGetValue("BaseColor", out var diffuseBaseColor))
                        diffuse.BaseColor = ConvertToVector4(diffuseBaseColor);
                    if (properties.TryGetValue("Roughness", out var diffuseRoughness))
                        diffuse.Roughness = Convert.ToSingle(diffuseRoughness);
                    break;

                case GlassMaterialNode glass:
                    if (properties.TryGetValue("Color", out var glassColor))
                        glass.Color = ConvertToVector4(glassColor);
                    if (properties.TryGetValue("Roughness", out var glassRoughness))
                        glass.Roughness = Convert.ToSingle(glassRoughness);
                    if (properties.TryGetValue("IOR", out var glassIor))
                        glass.IOR = Convert.ToSingle(glassIor);
                    if (properties.TryGetValue("Transparency", out var glassTransparency))
                        glass.Transparency = Convert.ToSingle(glassTransparency);
                    break;

                case MetalMaterialNode metal:
                    if (properties.TryGetValue("BaseColor", out var metalBaseColor))
                        metal.BaseColor = ConvertToVector4(metalBaseColor);
                    if (properties.TryGetValue("Roughness", out var metalRoughness))
                        metal.Roughness = Convert.ToSingle(metalRoughness);
                    break;

                case EmissionMaterialNode emissionMat:
                    if (properties.TryGetValue("EmissionColor", out var emissionColor))
                        emissionMat.EmissionColor = ConvertToVector4(emissionColor);
                    if (properties.TryGetValue("Strength", out var strength))
                        emissionMat.Strength = Convert.ToSingle(strength);
                    if (properties.TryGetValue("BaseColor", out var emissionBaseColor))
                        emissionMat.BaseColor = ConvertToVector4(emissionBaseColor);
                    break;

                case SceneNode sceneNode:
                    // シーンノードのソケットを復元
                    if (properties.TryGetValue("ObjectSocketNames", out var objSocketNamesObj) && objSocketNamesObj is JArray objSocketArray)
                    {
                        var objectSocketNames = objSocketArray.ToObject<List<string>>() ?? new List<string>();
                        // 既存のソケットをクリア（コンストラクタで作成された初期ソケット以外）
                        var existingObjectSockets = sceneNode.InputSockets.Where(s => s.SocketType == SocketType.Object).ToList();
                        foreach (var socket in existingObjectSockets)
                        {
                            sceneNode.InputSockets.Remove(socket);
                        }
                        // 保存されたソケットを再作成
                        foreach (var socketName in objectSocketNames)
                        {
                            sceneNode.AddNamedInputSocket(socketName, SocketType.Object);
                        }
                    }
                    
                    if (properties.TryGetValue("LightSocketNames", out var lightSocketNamesObj) && lightSocketNamesObj is JArray lightSocketArray)
                    {
                        var lightSocketNames = lightSocketArray.ToObject<List<string>>() ?? new List<string>();
                        // 既存のソケットをクリア
                        var existingLightSockets = sceneNode.InputSockets.Where(s => s.SocketType == SocketType.Light).ToList();
                        foreach (var socket in existingLightSockets)
                        {
                            sceneNode.InputSockets.Remove(socket);
                        }
                        // 保存されたソケットを再作成
                        foreach (var socketName in lightSocketNames)
                        {
                            sceneNode.AddNamedInputSocket(socketName, SocketType.Light);
                        }
                    }
                    
                    // ソケット名からカウンタを復元
                    sceneNode.RestoreSocketCounters();
                    
                    // レンダリング設定を復元
                    if (properties.TryGetValue("SamplesPerPixel", out var samplesObj))
                        sceneNode.SamplesPerPixel = Convert.ToInt32(samplesObj);
                    if (properties.TryGetValue("MaxBounces", out var bouncesObj))
                        sceneNode.MaxBounces = Convert.ToInt32(bouncesObj);
                    if (properties.TryGetValue("Exposure", out var exposureObj))
                        sceneNode.Exposure = Convert.ToSingle(exposureObj);
                    if (properties.TryGetValue("ToneMapOperator", out var toneMapObj))
                        sceneNode.ToneMapOperator = Convert.ToInt32(toneMapObj);
                    if (properties.TryGetValue("DenoiserStabilization", out var stabObj))
                        sceneNode.DenoiserStabilization = Convert.ToSingle(stabObj);
                    if (properties.TryGetValue("ShadowStrength", out var shadowObj))
                        sceneNode.ShadowStrength = Convert.ToSingle(shadowObj);
                    if (properties.TryGetValue("EnableDenoiser", out var denoiserObj))
                        sceneNode.EnableDenoiser = Convert.ToBoolean(denoiserObj);
                    break;

                case Vector3Node vector3:
                    if (properties.TryGetValue("X", out var x))
                        vector3.X = Convert.ToSingle(x);
                    if (properties.TryGetValue("Y", out var y))
                        vector3.Y = Convert.ToSingle(y);
                    if (properties.TryGetValue("Z", out var z))
                        vector3.Z = Convert.ToSingle(z);
                    break;

                case Vector4Node vector4:
                    if (properties.TryGetValue("X", out var v4x))
                        vector4.X = Convert.ToSingle(v4x);
                    if (properties.TryGetValue("Y", out var v4y))
                        vector4.Y = Convert.ToSingle(v4y);
                    if (properties.TryGetValue("Z", out var v4z))
                        vector4.Z = Convert.ToSingle(v4z);
                    if (properties.TryGetValue("W", out var v4w))
                        vector4.W = Convert.ToSingle(v4w);
                    break;

                case FloatNode floatNode:
                    if (properties.TryGetValue("Value", out var value))
                        floatNode.Value = Convert.ToSingle(value);
                    break;

                case TransformNode transformNode:
                    if (properties.TryGetValue("PositionX", out var posX))
                        transformNode.PositionX = Convert.ToSingle(posX);
                    if (properties.TryGetValue("PositionY", out var posY))
                        transformNode.PositionY = Convert.ToSingle(posY);
                    if (properties.TryGetValue("PositionZ", out var posZ))
                        transformNode.PositionZ = Convert.ToSingle(posZ);
                    if (properties.TryGetValue("RotationX", out var rotX))
                        transformNode.RotationX = Convert.ToSingle(rotX);
                    if (properties.TryGetValue("RotationY", out var rotY))
                        transformNode.RotationY = Convert.ToSingle(rotY);
                    if (properties.TryGetValue("RotationZ", out var rotZ))
                        transformNode.RotationZ = Convert.ToSingle(rotZ);
                    if (properties.TryGetValue("ScaleX", out var scaleX))
                        transformNode.ScaleX = Convert.ToSingle(scaleX);
                    if (properties.TryGetValue("ScaleY", out var scaleY))
                        transformNode.ScaleY = Convert.ToSingle(scaleY);
                    if (properties.TryGetValue("ScaleZ", out var scaleZ))
                        transformNode.ScaleZ = Convert.ToSingle(scaleZ);
                    break;

                case CombineTransformNode:
                    // CombineTransformNodeにはプロパティがない
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

        private Transform ConvertToTransform(object? obj)
        {
            if (obj == null)
                return Transform.Identity;

            if (obj is Transform transform)
                return transform;

            // JObjectから変換
            if (obj is Newtonsoft.Json.Linq.JObject jobj)
            {
                var position = ConvertToVector3(jobj["Position"]);
                // Rotation（旧形式）またはEulerAngles（新形式）からオイラー角を取得
                var rotationEuler = jobj["Rotation"] != null 
                    ? ConvertToVector3(jobj["Rotation"]) 
                    : ConvertToVector3(jobj["EulerAngles"]);
                var scale = ConvertToVector3(jobj["Scale"]);

                var result = new Transform
                {
                    Position = position,
                    Scale = scale
                };
                // オイラー角からQuaternionに変換
                result.EulerAngles = rotationEuler;
                return result;
            }

            return Transform.Identity;
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
            public ViewportState? Viewport { get; set; }
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
    }
    
    /// <summary>
    /// コンポーネントパレットの各カテゴリのExpander開閉状態
    /// </summary>
    public class ExpanderStates
    {
        public bool IsObjectExpanded { get; set; } = true;
        public bool IsMaterialExpanded { get; set; } = false;
        public bool IsMathExpanded { get; set; } = false;
        public bool IsCameraExpanded { get; set; } = false;
        public bool IsLightExpanded { get; set; } = false;
        public bool IsSceneExpanded { get; set; } = false;
    }
}

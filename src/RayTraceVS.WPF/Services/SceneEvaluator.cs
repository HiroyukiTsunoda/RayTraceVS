using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Diagnostics;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Data;
using RayTraceVS.WPF.Models.Nodes;
using InteropSphereData = RayTraceVS.Interop.SphereData;
using InteropPlaneData = RayTraceVS.Interop.PlaneData;
using InteropBoxData = RayTraceVS.Interop.BoxData;
using InteropCameraData = RayTraceVS.Interop.CameraData;
using InteropLightData = RayTraceVS.Interop.LightData;
using InteropMeshInstanceData = RayTraceVS.Interop.MeshInstanceData;
using InteropMeshCacheData = RayTraceVS.Interop.MeshCacheData;
using InteropVector3 = RayTraceVS.Interop.Vector3;
using InteropVector4 = RayTraceVS.Interop.Vector4;
using InteropLightType = RayTraceVS.Interop.LightType;
// データ型のエイリアス（Models.Data名前空間）
using SphereData = RayTraceVS.WPF.Models.Data.SphereData;
using PlaneData = RayTraceVS.WPF.Models.Data.PlaneData;
using BoxData = RayTraceVS.WPF.Models.Data.BoxData;
using CameraData = RayTraceVS.WPF.Models.Data.CameraData;
using LightData = RayTraceVS.WPF.Models.Data.LightData;
using LightType = RayTraceVS.WPF.Models.Data.LightType;
using SceneData = RayTraceVS.WPF.Models.Data.SceneData;
using MaterialData = RayTraceVS.WPF.Models.Data.MaterialData;
using MeshObjectData = RayTraceVS.WPF.Models.Data.MeshObjectData;

namespace RayTraceVS.WPF.Services
{
    public class SceneEvaluator
    {
        public (InteropSphereData[], InteropPlaneData[], InteropBoxData[], InteropCameraData, InteropLightData[], InteropMeshInstanceData[], InteropMeshCacheData[], int SamplesPerPixel, int MaxBounces, int TraceRecursionDepth, float Exposure, int ToneMapOperator, float DenoiserStabilization, float ShadowStrength, float ShadowAbsorptionScale, bool EnableDenoiser, float Gamma, float LightAttenuationConstant, float LightAttenuationLinear, float LightAttenuationQuadratic, int MaxShadowLights, float NRDBypassDistance, float NRDBypassBlendRange) EvaluateScene(NodeGraph nodeGraph)
        {
            var spheres = new List<InteropSphereData>();
            var planes = new List<InteropPlaneData>();
            var boxes = new List<InteropBoxData>();
            var lights = new List<InteropLightData>();
            var meshInstances = new List<InteropMeshInstanceData>();
            var meshCaches = new Dictionary<string, InteropMeshCacheData>();
            int samplesPerPixel = 1;
            int maxBounces = 6;
            int traceRecursionDepth = 2;
            float exposure = 1.0f;
            int toneMapOperator = 2;
            float denoiserStabilization = 1.0f;
            float shadowStrength = 1.0f;
            float shadowAbsorptionScale = 4.0f;
            bool enableDenoiser = true;
            float gamma = 1.0f;
            // P1 optimization settings
            float lightAttenuationConstant = 1.0f;
            float lightAttenuationLinear = 0.0f;
            float lightAttenuationQuadratic = 0.01f;
            int maxShadowLights = 2;
            float nrdBypassDistance = 8.0f;
            float nrdBypassBlendRange = 2.0f;
            InteropCameraData camera = new InteropCameraData
            {
                Position = new InteropVector3(0, 2, -5),
                LookAt = new InteropVector3(0, 0, 0),
                Up = new InteropVector3(0, 1, 0),
                FieldOfView = 60.0f,
                AspectRatio = 16.0f / 9.0f
            };

            var allNodes = nodeGraph.GetAllNodes();
            var connections = nodeGraph.GetAllConnections();
            
            // SceneNodeが存在するか確認
            var sceneNode = allNodes.OfType<Models.Nodes.SceneNode>().FirstOrDefault();
            
            if (sceneNode != null && connections.Any())
            {
                // SceneNodeが存在する場合：グラフを評価してSceneNodeの出力を使用
                // 増分評価を使用（Dirtyなノードのみ再評価）
                var results = nodeGraph.EvaluateGraph();
                
                // SceneNodeの評価結果を取得
                if (results.TryGetValue(sceneNode.Id, out var sceneResult) && sceneResult is SceneData sceneData)
                {
                    // Debug: log SceneNode object socket connections
                    var sceneObjectSockets = sceneNode.InputSockets.Where(s => s.SocketType == SocketType.Object).ToList();
                    Debug.WriteLine($"[SceneEvaluator] SceneNode object sockets: {sceneObjectSockets.Count}");
                    foreach (var socket in sceneObjectSockets)
                    {
                        var connection = connections.FirstOrDefault(c => c.InputSocket?.Id == socket.Id);
                        if (connection?.OutputSocket?.ParentNode != null)
                        {
                            var node = connection.OutputSocket.ParentNode;
                            Debug.WriteLine($"[SceneEvaluator] {socket.Name} <- {node.Title} ({node.GetType().Name}) [{node.Id}]");
                        }
                        else
                        {
                            Debug.WriteLine($"[SceneEvaluator] {socket.Name} <- (empty)");
                        }
                    }
                    
                    // カメラの設定（デフォルト値でなければ使用）
                    if (sceneData.Camera.FieldOfView > 0)
                    {
                        camera = ConvertCameraData(sceneData.Camera);
                    }
                    
                    // SceneNodeに接続されたオブジェクトのみを追加
                    foreach (var obj in sceneData.Objects)
                    {
                        if (obj is SphereData sd && sd.Radius > 0)
                        {
                            Debug.WriteLine($"[SceneEvaluator] Sphere Pos({sd.Position.X:F3}, {sd.Position.Y:F3}, {sd.Position.Z:F3}) R={sd.Radius:F3} " +
                                            $"Base({sd.Material.BaseColor.X:F3}, {sd.Material.BaseColor.Y:F3}, {sd.Material.BaseColor.Z:F3}, {sd.Material.BaseColor.W:F3}) " +
                                            $"M={sd.Material.Metallic:F3} Rgh={sd.Material.Roughness:F3} T={sd.Material.Transmission:F3} IOR={sd.Material.IOR:F3}");
                            spheres.Add(ConvertSphereData(sd));
                        }
                        else if (obj is PlaneData pd)
                        {
                            planes.Add(ConvertPlaneData(pd));
                        }
                        else if (obj is BoxData bd)
                        {
                            boxes.Add(ConvertBoxData(bd));
                        }
                        else if (obj is MeshObjectData md && !string.IsNullOrEmpty(md.MeshName))
                        {
                            // キャッシュが存在する場合のみインスタンスを追加
                            // キャッシュがない場合はスキップ（AccessViolation防止）
                            if (!meshCaches.ContainsKey(md.MeshName))
                            {
                                var cache = CreateMeshCacheData(md.MeshName);
                                if (cache != null)
                                {
                                    meshCaches[md.MeshName] = cache;
                                }
                                else
                                {
                                    // キャッシュが存在しない場合はこのメッシュインスタンスをスキップ
                                    continue;
                                }
                            }
                            meshInstances.Add(ConvertMeshInstanceData(md));
                        }
                    }
                    
                    // SceneNodeに接続されたライトのみを追加
                    foreach (var light in sceneData.Lights)
                    {
                        lights.Add(ConvertLightData(light));
                    }
                    
                    Debug.WriteLine($"[SceneEvaluator] Objects: spheres={spheres.Count}, planes={planes.Count}, boxes={boxes.Count}, meshInstances={meshInstances.Count}");
                    Debug.WriteLine($"[SceneEvaluator] Lights: {lights.Count}");
                    
                    // レンダリング設定を取得
                    samplesPerPixel = sceneData.SamplesPerPixel > 0 ? sceneData.SamplesPerPixel : 1;
                    maxBounces = sceneData.MaxBounces > 0 ? sceneData.MaxBounces : 6;
                    traceRecursionDepth = sceneData.TraceRecursionDepth > 0 ? sceneData.TraceRecursionDepth : 2;
                    exposure = sceneData.Exposure > 0 ? sceneData.Exposure : 1.0f;
                    toneMapOperator = sceneData.ToneMapOperator;
                    denoiserStabilization = sceneData.DenoiserStabilization > 0 ? sceneData.DenoiserStabilization : 1.0f;
                    shadowStrength = sceneData.ShadowStrength >= 0 ? sceneData.ShadowStrength : 1.0f;
                    shadowAbsorptionScale = sceneData.ShadowAbsorptionScale >= 0 ? sceneData.ShadowAbsorptionScale : 4.0f;
                    enableDenoiser = sceneData.EnableDenoiser;
                    gamma = sceneData.Gamma > 0 ? sceneData.Gamma : 1.0f;
                    // P1 optimization settings
                    lightAttenuationConstant = sceneData.LightAttenuationConstant > 0 ? sceneData.LightAttenuationConstant : 1.0f;
                    lightAttenuationLinear = sceneData.LightAttenuationLinear >= 0 ? sceneData.LightAttenuationLinear : 0.0f;
                    lightAttenuationQuadratic = sceneData.LightAttenuationQuadratic >= 0 ? sceneData.LightAttenuationQuadratic : 0.01f;
                    maxShadowLights = sceneData.MaxShadowLights > 0 ? sceneData.MaxShadowLights : 2;
                    nrdBypassDistance = sceneData.NRDBypassDistance > 0 ? sceneData.NRDBypassDistance : 8.0f;
                    nrdBypassBlendRange = sceneData.NRDBypassBlendRange > 0 ? sceneData.NRDBypassBlendRange : 2.0f;
                }
            }
            else
            {
                // SceneNodeがない場合：すべてのオブジェクトノードから直接取得（フォールバック）
                // 接続がある場合はグラフ評価を利用（入力値が正しく伝播される）
                Dictionary<Guid, object?>? results = null;
                if (connections.Any())
                {
                    results = nodeGraph.EvaluateGraph();
                }
                
                foreach (var node in allNodes)
                {
                    // グラフ評価結果があればそれを使用、なければノードプロパティから取得
                    if (node is Models.Nodes.SphereNode sphereNode)
                    {
                        SphereData sphereData;
                        if (results != null && results.TryGetValue(sphereNode.Id, out var evalResult) && evalResult is SphereData sd)
                        {
                            sphereData = sd;
                        }
                        else
                        {
                            sphereData = new SphereData
                            {
                                Position = sphereNode.ObjectTransform.Position,
                                Radius = sphereNode.Radius,
                                Material = MaterialData.Default
                            };
                        }
                        if (sphereData.Radius > 0)
                        {
                            spheres.Add(ConvertSphereData(sphereData));
                        }
                    }
                    else if (node is Models.Nodes.PlaneNode planeNode)
                    {
                        PlaneData planeData;
                        if (results != null && results.TryGetValue(planeNode.Id, out var evalResult) && evalResult is PlaneData pd)
                        {
                            planeData = pd;
                        }
                        else
                        {
                            planeData = new PlaneData
                            {
                                Position = planeNode.ObjectTransform.Position,
                                Normal = planeNode.Normal,
                                Material = MaterialData.Default
                            };
                        }
                        planes.Add(ConvertPlaneData(planeData));
                    }
                    else if (node is Models.Nodes.BoxNode boxNode)
                    {
                        BoxData boxData;
                        if (results != null && results.TryGetValue(boxNode.Id, out var evalResult) && evalResult is BoxData bd)
                        {
                            boxData = bd;
                        }
                        else
                        {
                            // Default: identity rotation (axis-aligned)
                            boxData = new BoxData
                            {
                                Center = boxNode.ObjectTransform.Position,
                                Size = boxNode.Size * 0.5f,  // half-extents
                                AxisX = Vector3.UnitX,
                                AxisY = Vector3.UnitY,
                                AxisZ = Vector3.UnitZ,
                                Material = MaterialData.Default
                            };
                        }
                        boxes.Add(ConvertBoxData(boxData));
                    }
                    else if (node is Models.Nodes.CameraNode cameraNode)
                    {
                        CameraData cameraData;
                        if (results != null && results.TryGetValue(cameraNode.Id, out var evalResult) && evalResult is CameraData cd)
                        {
                            cameraData = cd;
                        }
                        else
                        {
                            cameraData = new CameraData
                            {
                                Position = cameraNode.CameraPosition,
                                LookAt = cameraNode.LookAt,
                                Up = cameraNode.Up,
                                FieldOfView = cameraNode.FieldOfView,
                                Near = cameraNode.Near,
                                Far = cameraNode.Far,
                                ApertureSize = cameraNode.ApertureSize,
                                FocusDistance = cameraNode.FocusDistance
                            };
                        }
                        camera = ConvertCameraData(cameraData);
                    }
                    else if (node is Models.Nodes.PointLightNode pointLightNode)
                    {
                        var lightData = new LightData
                        {
                            Type = LightType.Point,
                            Position = pointLightNode.LightPosition,
                            Direction = Vector3.Zero,
                            Color = pointLightNode.Color,
                            Intensity = pointLightNode.Intensity,
                            Attenuation = pointLightNode.Attenuation
                        };
                        lights.Add(ConvertLightData(lightData));
                    }
                    else if (node is Models.Nodes.AmbientLightNode ambientLightNode)
                    {
                        var lightData = new LightData
                        {
                            Type = LightType.Ambient,
                            Position = Vector3.Zero,
                            Direction = Vector3.Zero,
                            Color = ambientLightNode.Color,
                            Intensity = ambientLightNode.Intensity,
                            Attenuation = 0.0f
                        };
                        lights.Add(ConvertLightData(lightData));
                    }
                    else if (node is Models.Nodes.DirectionalLightNode directionalLightNode)
                    {
                        var lightData = new LightData
                        {
                            Type = LightType.Directional,
                            Position = Vector3.Zero,
                            Direction = directionalLightNode.Direction,
                            Color = directionalLightNode.Color,
                            Intensity = directionalLightNode.Intensity,
                            Attenuation = 0.0f
                        };
                        lights.Add(ConvertLightData(lightData));
                    }
                }
            }

            return (spheres.ToArray(), planes.ToArray(), boxes.ToArray(), camera, lights.ToArray(), meshInstances.ToArray(), meshCaches.Values.ToArray(), samplesPerPixel, maxBounces, traceRecursionDepth, exposure, toneMapOperator, denoiserStabilization, shadowStrength, shadowAbsorptionScale, enableDenoiser, gamma, lightAttenuationConstant, lightAttenuationLinear, lightAttenuationQuadratic, maxShadowLights, nrdBypassDistance, nrdBypassBlendRange);
        }

        private InteropSphereData ConvertSphereData(SphereData data)
        {
            var material = data.Material;
            return new InteropSphereData
            {
                Position = new InteropVector3(data.Position.X, data.Position.Y, data.Position.Z),
                Radius = data.Radius,
                Color = new InteropVector4(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z, material.BaseColor.W),
                Metallic = material.Metallic,
                Roughness = material.Roughness,
                Transmission = material.Transmission,
                IOR = material.IOR,
                Specular = material.Specular,
                Emission = new InteropVector3(material.Emission.X, material.Emission.Y, material.Emission.Z),
                Absorption = new InteropVector3(material.Absorption.X, material.Absorption.Y, material.Absorption.Z)
            };
        }

        private InteropPlaneData ConvertPlaneData(PlaneData data)
        {
            // MaterialDataから旧形式のパラメータに変換
            var material = data.Material;

            // Guard against default Vector3Node (1,1,1) being used as a normal
            // If plane is at origin and normal is roughly (1,1,1), treat it as floor normal
            var normal = data.Normal;
            if (normal.LengthSquared() > 0.0f)
            {
                normal = Vector3.Normalize(normal);
            }

            if (data.Position.LengthSquared() < 1e-6f)
            {
                // Detect default Vector3Node normal (approx equal components, positive)
                if (MathF.Abs(normal.X - normal.Y) < 0.01f &&
                    MathF.Abs(normal.Y - normal.Z) < 0.01f &&
                    normal.X > 0.0f && normal.Y > 0.0f && normal.Z > 0.0f)
                {
                    normal = Vector3.UnitY;
                }
            }

            return new InteropPlaneData
            {
                Position = new InteropVector3(data.Position.X, data.Position.Y, data.Position.Z),
                Normal = new InteropVector3(normal.X, normal.Y, normal.Z),
                Color = new InteropVector4(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z, material.BaseColor.W),
                Metallic = material.Metallic,
                Roughness = material.Roughness,
                Transmission = material.Transmission,
                IOR = material.IOR,
                Specular = material.Specular,
                Emission = new InteropVector3(material.Emission.X, material.Emission.Y, material.Emission.Z),
                Absorption = new InteropVector3(material.Absorption.X, material.Absorption.Y, material.Absorption.Z)
            };
        }

        private InteropBoxData ConvertBoxData(BoxData data)
        {
            var material = data.Material;
            return new InteropBoxData
            {
                Center = new InteropVector3(data.Center.X, data.Center.Y, data.Center.Z),
                Size = new InteropVector3(data.Size.X, data.Size.Y, data.Size.Z),
                // OBB local axes
                AxisX = new InteropVector3(data.AxisX.X, data.AxisX.Y, data.AxisX.Z),
                AxisY = new InteropVector3(data.AxisY.X, data.AxisY.Y, data.AxisY.Z),
                AxisZ = new InteropVector3(data.AxisZ.X, data.AxisZ.Y, data.AxisZ.Z),
                Color = new InteropVector4(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z, material.BaseColor.W),
                Metallic = material.Metallic,
                Roughness = material.Roughness,
                Transmission = material.Transmission,
                IOR = material.IOR,
                Specular = material.Specular,
                Emission = new InteropVector3(material.Emission.X, material.Emission.Y, material.Emission.Z),
                Absorption = new InteropVector3(material.Absorption.X, material.Absorption.Y, material.Absorption.Z)
            };
        }

        private InteropCameraData ConvertCameraData(CameraData data)
        {
            return new InteropCameraData
            {
                Position = new InteropVector3(data.Position.X, data.Position.Y, data.Position.Z),
                LookAt = new InteropVector3(data.LookAt.X, data.LookAt.Y, data.LookAt.Z),
                Up = new InteropVector3(data.Up.X, data.Up.Y, data.Up.Z),
                FieldOfView = data.FieldOfView,
                AspectRatio = 16.0f / 9.0f,
                Near = data.Near,
                Far = data.Far,
                ApertureSize = data.ApertureSize,
                FocusDistance = data.FocusDistance
            };
        }

        private InteropLightData ConvertLightData(LightData data)
        {
            // LightTypeを正しく変換
            var interopType = data.Type switch
            {
                LightType.Ambient => InteropLightType.Ambient,
                LightType.Directional => InteropLightType.Directional,
                LightType.Point => InteropLightType.Point,
                _ => InteropLightType.Point
            };
            
            // Directionalライトの場合、Positionに方向ベクトルを格納
            var position = data.Type == LightType.Directional 
                ? data.Direction 
                : data.Position;
            
            return new InteropLightData
            {
                Position = new InteropVector3(position.X, position.Y, position.Z),
                Color = new InteropVector4(data.Color.X, data.Color.Y, data.Color.Z, data.Color.W),
                Intensity = data.Intensity,
                Type = interopType,
                Radius = data.Radius,
                SoftShadowSamples = data.SoftShadowSamples
            };
        }

        private InteropMeshInstanceData ConvertMeshInstanceData(MeshObjectData data)
        {
            var material = data.Material;
            var transform = data.Transform;

            // EulerAnglesプロパティを使用してオイラー角（度数法）を取得
            // 注意: Transform.RotationはQuaternion型なので、直接X,Y,Zを使用してはいけない
            var eulerAngles = transform.EulerAngles;
            
            // Scaleが0の場合はデフォルト(1,1,1)を使用（未初期化対策）
            var scale = transform.Scale;
            if (scale.X == 0 && scale.Y == 0 && scale.Z == 0)
            {
                scale = System.Numerics.Vector3.One;
            }

            return new InteropMeshInstanceData
            {
                MeshName = data.MeshName,
                Position = new InteropVector3(transform.Position.X, transform.Position.Y, transform.Position.Z),
                Rotation = new InteropVector3(eulerAngles.X, eulerAngles.Y, eulerAngles.Z),
                Scale = new InteropVector3(scale.X, scale.Y, scale.Z),
                Color = new InteropVector4(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z, material.BaseColor.W),
                Metallic = material.Metallic,
                Roughness = material.Roughness,
                Transmission = material.Transmission,
                IOR = material.IOR,
                Specular = material.Specular,
                Emission = new InteropVector3(material.Emission.X, material.Emission.Y, material.Emission.Z),
                Absorption = new InteropVector3(material.Absorption.X, material.Absorption.Y, material.Absorption.Z)
            };
        }

        private InteropMeshCacheData? CreateMeshCacheData(string meshName)
        {
            var meshCacheService = App.MeshCacheService;
            if (meshCacheService == null) return null;

            var cachedMesh = meshCacheService.GetMesh(meshName);
            if (cachedMesh == null) return null;

            var cacheData = new InteropMeshCacheData
            {
                MeshName = meshName,
                Vertices = cachedMesh.Vertices,
                Indices = cachedMesh.Indices,
                BoundsMin = new InteropVector3(cachedMesh.BoundsMin.X, cachedMesh.BoundsMin.Y, cachedMesh.BoundsMin.Z),
                BoundsMax = new InteropVector3(cachedMesh.BoundsMax.X, cachedMesh.BoundsMax.Y, cachedMesh.BoundsMax.Z)
            };

            return cacheData;
        }
    }
}

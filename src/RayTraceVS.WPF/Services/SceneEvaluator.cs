using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Data;
using RayTraceVS.WPF.Models.Nodes;
using InteropSphereData = RayTraceVS.Interop.SphereData;
using InteropPlaneData = RayTraceVS.Interop.PlaneData;
using InteropBoxData = RayTraceVS.Interop.BoxData;
using InteropCameraData = RayTraceVS.Interop.CameraData;
using InteropLightData = RayTraceVS.Interop.LightData;
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

namespace RayTraceVS.WPF.Services
{
    public class SceneEvaluator
    {
        public (InteropSphereData[], InteropPlaneData[], InteropBoxData[], InteropCameraData, InteropLightData[], int SamplesPerPixel, int MaxBounces, float Exposure, int ToneMapOperator, float DenoiserStabilization, float ShadowStrength, bool EnableDenoiser, float Gamma) EvaluateScene(NodeGraph nodeGraph)
        {
            var spheres = new List<InteropSphereData>();
            var planes = new List<InteropPlaneData>();
            var boxes = new List<InteropBoxData>();
            var lights = new List<InteropLightData>();
            int samplesPerPixel = 1;
            int maxBounces = 6;
            float exposure = 1.0f;
            int toneMapOperator = 2;
            float denoiserStabilization = 1.0f;
            float shadowStrength = 1.0f;
            bool enableDenoiser = true;
            float gamma = 1.0f;
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
                    }
                    
                    // SceneNodeに接続されたライトのみを追加
                    foreach (var light in sceneData.Lights)
                    {
                        lights.Add(ConvertLightData(light));
                    }
                    
                    // レンダリング設定を取得
                    samplesPerPixel = sceneData.SamplesPerPixel > 0 ? sceneData.SamplesPerPixel : 1;
                    maxBounces = sceneData.MaxBounces > 0 ? sceneData.MaxBounces : 6;
                    exposure = sceneData.Exposure > 0 ? sceneData.Exposure : 1.0f;
                    toneMapOperator = sceneData.ToneMapOperator;
                    denoiserStabilization = sceneData.DenoiserStabilization > 0 ? sceneData.DenoiserStabilization : 1.0f;
                    shadowStrength = sceneData.ShadowStrength >= 0 ? sceneData.ShadowStrength : 1.0f;
                    enableDenoiser = sceneData.EnableDenoiser;
                    gamma = sceneData.Gamma > 0 ? sceneData.Gamma : 1.0f;
                }
            }
            else
            {
                // SceneNodeがない場合：すべてのオブジェクトノードから直接取得（フォールバック）
                System.Diagnostics.Debug.WriteLine("[SceneEvaluator] SceneNodeなし：フォールバックモード");
                
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

            return (spheres.ToArray(), planes.ToArray(), boxes.ToArray(), camera, lights.ToArray(), samplesPerPixel, maxBounces, exposure, toneMapOperator, denoiserStabilization, shadowStrength, enableDenoiser, gamma);
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
                Emission = new InteropVector3(material.Emission.X, material.Emission.Y, material.Emission.Z)
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
                Emission = new InteropVector3(material.Emission.X, material.Emission.Y, material.Emission.Z)
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
                Emission = new InteropVector3(material.Emission.X, material.Emission.Y, material.Emission.Z)
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
    }
}

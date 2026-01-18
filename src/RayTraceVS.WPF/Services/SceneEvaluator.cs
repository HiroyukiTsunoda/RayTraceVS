using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Nodes;
using InteropSphereData = RayTraceVS.Interop.SphereData;
using InteropPlaneData = RayTraceVS.Interop.PlaneData;
using InteropBoxData = RayTraceVS.Interop.BoxData;
using InteropCameraData = RayTraceVS.Interop.CameraData;
using InteropLightData = RayTraceVS.Interop.LightData;
using InteropVector3 = RayTraceVS.Interop.Vector3;
using InteropVector4 = RayTraceVS.Interop.Vector4;
using InteropLightType = RayTraceVS.Interop.LightType;

namespace RayTraceVS.WPF.Services
{
    public class SceneEvaluator
    {
        public (InteropSphereData[], InteropPlaneData[], InteropBoxData[], InteropCameraData, InteropLightData[], int SamplesPerPixel, int MaxBounces, float Exposure, int ToneMapOperator, float DenoiserStabilization) EvaluateScene(NodeGraph nodeGraph)
        {
            var spheres = new List<InteropSphereData>();
            var planes = new List<InteropPlaneData>();
            var boxes = new List<InteropBoxData>();
            var lights = new List<InteropLightData>();
            int samplesPerPixel = 1;
            int maxBounces = 4;
            float exposure = 1.0f;
            int toneMapOperator = 2;
            float denoiserStabilization = 1.0f;
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
                // 常に完全再評価を行い、キャッシュの問題を回避
                var results = nodeGraph.EvaluateGraphFull();
                
                // SceneNodeの評価結果を取得
                if (results.TryGetValue(sceneNode.Id, out var sceneResult) && sceneResult is Models.Nodes.SceneData sceneData)
                {
                    // カメラの設定（デフォルト値でなければ使用）
                    if (sceneData.Camera.FieldOfView > 0)
                    {
                        camera = ConvertCameraData(sceneData.Camera);
                    }
                    
                    // SceneNodeに接続されたオブジェクトのみを追加
                    foreach (var obj in sceneData.Objects)
                    {
                        if (obj is Models.Nodes.SphereData sd && sd.Radius > 0)
                        {
                            spheres.Add(ConvertSphereData(sd));
                        }
                        else if (obj is Models.Nodes.PlaneData pd)
                        {
                            planes.Add(ConvertPlaneData(pd));
                        }
                        else if (obj is Models.Nodes.BoxData bd)
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
                    maxBounces = sceneData.MaxBounces > 0 ? sceneData.MaxBounces : 4;
                    exposure = sceneData.Exposure > 0 ? sceneData.Exposure : 1.0f;
                    toneMapOperator = sceneData.ToneMapOperator;
                    denoiserStabilization = sceneData.DenoiserStabilization > 0 ? sceneData.DenoiserStabilization : 1.0f;
                    
                    System.Diagnostics.Debug.WriteLine($"[SceneEvaluator] SceneNode経由: Spheres={spheres.Count}, Planes={planes.Count}, Boxes={boxes.Count}, Lights={lights.Count}, Samples={samplesPerPixel}, Bounces={maxBounces}");
                    
                    // デバッグ：詳細情報を出力
                    foreach (var s in spheres)
                    {
                        System.Diagnostics.Debug.WriteLine($"  Sphere: Pos=({s.Position.X}, {s.Position.Y}, {s.Position.Z}), Radius={s.Radius}");
                    }
                    System.Diagnostics.Debug.WriteLine($"  Camera: Pos=({camera.Position.X}, {camera.Position.Y}, {camera.Position.Z}), LookAt=({camera.LookAt.X}, {camera.LookAt.Y}, {camera.LookAt.Z}), FOV={camera.FieldOfView}");
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
                        Models.Nodes.SphereData sphereData;
                        if (results != null && results.TryGetValue(sphereNode.Id, out var evalResult) && evalResult is Models.Nodes.SphereData sd)
                        {
                            sphereData = sd;
                        }
                        else
                        {
                            sphereData = new Models.Nodes.SphereData
                            {
                                Position = sphereNode.ObjectTransform.Position,
                                Radius = sphereNode.Radius,
                                Material = Models.Nodes.MaterialData.Default
                            };
                        }
                        if (sphereData.Radius > 0)
                        {
                            spheres.Add(ConvertSphereData(sphereData));
                        }
                    }
                    else if (node is Models.Nodes.PlaneNode planeNode)
                    {
                        Models.Nodes.PlaneData planeData;
                        if (results != null && results.TryGetValue(planeNode.Id, out var evalResult) && evalResult is Models.Nodes.PlaneData pd)
                        {
                            planeData = pd;
                        }
                        else
                        {
                            planeData = new Models.Nodes.PlaneData
                            {
                                Position = planeNode.ObjectTransform.Position,
                                Normal = planeNode.Normal,
                                Material = Models.Nodes.MaterialData.Default
                            };
                        }
                        planes.Add(ConvertPlaneData(planeData));
                    }
                    else if (node is Models.Nodes.BoxNode boxNode)
                    {
                        Models.Nodes.BoxData boxData;
                        if (results != null && results.TryGetValue(boxNode.Id, out var evalResult) && evalResult is Models.Nodes.BoxData bd)
                        {
                            boxData = bd;
                        }
                        else
                        {
                            boxData = new Models.Nodes.BoxData
                            {
                                Center = boxNode.ObjectTransform.Position,
                                Size = boxNode.Size * 0.5f,  // half-extents
                                Material = Models.Nodes.MaterialData.Default
                            };
                        }
                        boxes.Add(ConvertBoxData(boxData));
                    }
                    else if (node is Models.Nodes.CameraNode cameraNode)
                    {
                        Models.Nodes.CameraData cameraData;
                        if (results != null && results.TryGetValue(cameraNode.Id, out var evalResult) && evalResult is Models.Nodes.CameraData cd)
                        {
                            cameraData = cd;
                        }
                        else
                        {
                            cameraData = new Models.Nodes.CameraData
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
                        var lightData = new Models.Nodes.LightData
                        {
                            Type = Models.Nodes.LightType.Point,
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
                        var lightData = new Models.Nodes.LightData
                        {
                            Type = Models.Nodes.LightType.Ambient,
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
                        var lightData = new Models.Nodes.LightData
                        {
                            Type = Models.Nodes.LightType.Directional,
                            Position = Vector3.Zero,
                            Direction = directionalLightNode.Direction,
                            Color = directionalLightNode.Color,
                            Intensity = directionalLightNode.Intensity,
                            Attenuation = 0.0f
                        };
                        lights.Add(ConvertLightData(lightData));
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[SceneEvaluator] フォールバック: Spheres={spheres.Count}, Planes={planes.Count}, Boxes={boxes.Count}, Lights={lights.Count}");
            }

            return (spheres.ToArray(), planes.ToArray(), boxes.ToArray(), camera, lights.ToArray(), samplesPerPixel, maxBounces, exposure, toneMapOperator, denoiserStabilization);
        }

        private InteropSphereData ConvertSphereData(Models.Nodes.SphereData data)
        {
            var material = data.Material;
            
            System.Diagnostics.Debug.WriteLine($"[SceneEvaluator] Sphere Material: Color=({material.BaseColor.X:F2},{material.BaseColor.Y:F2},{material.BaseColor.Z:F2}), Metallic={material.Metallic:F2}, Roughness={material.Roughness:F2}, Transmission={material.Transmission:F2}, IOR={material.IOR:F2}");
            
            return new InteropSphereData
            {
                Position = new InteropVector3(data.Position.X, data.Position.Y, data.Position.Z),
                Radius = data.Radius,
                Color = new InteropVector4(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z, material.BaseColor.W),
                Metallic = material.Metallic,
                Roughness = material.Roughness,
                Transmission = material.Transmission,
                IOR = material.IOR
            };
        }

        private InteropPlaneData ConvertPlaneData(Models.Nodes.PlaneData data)
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
                IOR = material.IOR
            };
        }

        private InteropBoxData ConvertBoxData(Models.Nodes.BoxData data)
        {
            var material = data.Material;
            
            System.Diagnostics.Debug.WriteLine($"[SceneEvaluator] Box Material: Color=({material.BaseColor.X:F2},{material.BaseColor.Y:F2},{material.BaseColor.Z:F2}), Metallic={material.Metallic:F2}, Roughness={material.Roughness:F2}, Transmission={material.Transmission:F2}, IOR={material.IOR:F2}");
            
            return new InteropBoxData
            {
                Center = new InteropVector3(data.Center.X, data.Center.Y, data.Center.Z),
                Size = new InteropVector3(data.Size.X, data.Size.Y, data.Size.Z),
                Color = new InteropVector4(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z, material.BaseColor.W),
                Metallic = material.Metallic,
                Roughness = material.Roughness,
                Transmission = material.Transmission,
                IOR = material.IOR
            };
        }

        private InteropCameraData ConvertCameraData(Models.Nodes.CameraData data)
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

        private InteropLightData ConvertLightData(Models.Nodes.LightData data)
        {
            // LightTypeを正しく変換
            var interopType = data.Type switch
            {
                Models.Nodes.LightType.Ambient => InteropLightType.Ambient,
                Models.Nodes.LightType.Directional => InteropLightType.Directional,
                Models.Nodes.LightType.Point => InteropLightType.Point,
                _ => InteropLightType.Point
            };
            
            // Directionalライトの場合、Positionに方向ベクトルを格納
            var position = data.Type == Models.Nodes.LightType.Directional 
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

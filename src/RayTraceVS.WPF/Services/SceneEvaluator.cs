using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Nodes;
using InteropSphereData = RayTraceVS.Interop.SphereData;
using InteropPlaneData = RayTraceVS.Interop.PlaneData;
using InteropCylinderData = RayTraceVS.Interop.CylinderData;
using InteropCameraData = RayTraceVS.Interop.CameraData;
using InteropLightData = RayTraceVS.Interop.LightData;
using InteropVector3 = RayTraceVS.Interop.Vector3;
using InteropVector4 = RayTraceVS.Interop.Vector4;
using InteropLightType = RayTraceVS.Interop.LightType;

namespace RayTraceVS.WPF.Services
{
    public class SceneEvaluator
    {
        public (InteropSphereData[], InteropPlaneData[], InteropCylinderData[], InteropCameraData, InteropLightData[]) EvaluateScene(NodeGraph nodeGraph)
        {
            var spheres = new List<InteropSphereData>();
            var planes = new List<InteropPlaneData>();
            var cylinders = new List<InteropCylinderData>();
            var lights = new List<InteropLightData>();
            InteropCameraData camera = new InteropCameraData
            {
                Position = new InteropVector3(0, 2, -5),
                LookAt = new InteropVector3(0, 0, 0),
                Up = new InteropVector3(0, 1, 0),
                FieldOfView = 60.0f,
                AspectRatio = 16.0f / 9.0f
            };

            // 接続があるかチェック
            var connections = nodeGraph.GetAllConnections();
            
            // 接続がある場合のみグラフ評価を実行
            bool hasResults = false;
            if (connections.Any())
            {
                var results = nodeGraph.EvaluateGraph();
                
                // グラフ評価の結果から各オブジェクトを抽出
                foreach (var kvp in results)
            {
                var value = kvp.Value;

                if (value is Models.Nodes.SphereData sphereDataFromGraph)
                {
                    // 有効なデータかチェック（半径が0より大きい）
                    if (sphereDataFromGraph.Radius > 0)
                    {
                        spheres.Add(ConvertSphereData(sphereDataFromGraph));
                        hasResults = true;
                    }
                }
                else if (value is Models.Nodes.PlaneData planeDataFromGraph)
                {
                    planes.Add(ConvertPlaneData(planeDataFromGraph));
                    hasResults = true;
                }
                else if (value is Models.Nodes.CylinderData cylinderDataFromGraph)
                {
                    // 有効なデータかチェック
                    if (cylinderDataFromGraph.Radius > 0 && cylinderDataFromGraph.Height > 0)
                    {
                        cylinders.Add(ConvertCylinderData(cylinderDataFromGraph));
                        hasResults = true;
                    }
                }
                else if (value is Models.Nodes.CameraData cameraDataFromGraph)
                {
                    camera = ConvertCameraData(cameraDataFromGraph);
                    hasResults = true;
                }
                else if (value is Models.Nodes.LightData lightDataFromGraph)
                {
                    lights.Add(ConvertLightData(lightDataFromGraph));
                    hasResults = true;
                }
                else if (value is Models.Nodes.SceneData sceneData)
                {
                    // シーンデータが有効なデータを含むかチェック
                    if (sceneData.Objects.Count > 0 || sceneData.Lights.Count > 0)
                    {
                        // シーンデータから個別オブジェクトを展開
                        camera = ConvertCameraData(sceneData.Camera);
                        
                        foreach (var obj in sceneData.Objects)
                        {
                            if (obj is Models.Nodes.SphereData sd)
                                spheres.Add(ConvertSphereData(sd));
                            else if (obj is Models.Nodes.PlaneData pd)
                                planes.Add(ConvertPlaneData(pd));
                            else if (obj is Models.Nodes.CylinderData cd)
                                cylinders.Add(ConvertCylinderData(cd));
                        }

                        lights.AddRange(sceneData.Lights.Select(ConvertLightData));
                        hasResults = true;
                    }
                }
                }
            }
            
            // グラフ評価で結果が得られなかった場合のみ、ノードから直接取得
            if (!hasResults)
            {
                var allNodes = nodeGraph.GetAllNodes();
                
                foreach (var node in allNodes)
                {
                    if (node is Models.Nodes.SphereNode sphereNode)
                {
                    var sphereData = new Models.Nodes.SphereData
                    {
                        Position = sphereNode.ObjectTransform.Position,
                        Radius = sphereNode.Radius,
                        Material = Models.Nodes.MaterialData.Default
                    };
                    spheres.Add(ConvertSphereData(sphereData));
                }
                else if (node is Models.Nodes.PlaneNode planeNode)
                {
                    var planeData = new Models.Nodes.PlaneData
                    {
                        Position = planeNode.ObjectTransform.Position,
                        Normal = planeNode.Normal,
                        Material = Models.Nodes.MaterialData.Default
                    };
                    planes.Add(ConvertPlaneData(planeData));
                }
                else if (node is Models.Nodes.CylinderNode cylinderNode)
                {
                    var cylinderData = new Models.Nodes.CylinderData
                    {
                        Position = cylinderNode.ObjectTransform.Position,
                        Axis = cylinderNode.Axis,
                        Radius = cylinderNode.Radius,
                        Height = cylinderNode.Height,
                        Material = Models.Nodes.MaterialData.Default
                    };
                    cylinders.Add(ConvertCylinderData(cylinderData));
                }
                else if (node is Models.Nodes.CameraNode cameraNode)
                {
                    var cameraData = new Models.Nodes.CameraData
                    {
                        Position = cameraNode.CameraPosition,
                        LookAt = cameraNode.LookAt,
                        Up = cameraNode.Up,
                        FieldOfView = cameraNode.FieldOfView,
                        Near = cameraNode.Near,
                        Far = cameraNode.Far
                    };
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
            }

            return (spheres.ToArray(), planes.ToArray(), cylinders.ToArray(), camera, lights.ToArray());
        }

        private InteropSphereData ConvertSphereData(Models.Nodes.SphereData data)
        {
            // MaterialDataから旧形式のパラメータに変換
            var material = data.Material;
            
            return new InteropSphereData
            {
                Position = new InteropVector3(data.Position.X, data.Position.Y, data.Position.Z),
                Radius = data.Radius,
                Color = new InteropVector4(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z, material.BaseColor.W),
                Reflectivity = material.Metallic,  // 金属度を反射率として使用
                Transparency = material.Transmission,
                IOR = material.IOR
            };
        }

        private InteropPlaneData ConvertPlaneData(Models.Nodes.PlaneData data)
        {
            // MaterialDataから旧形式のパラメータに変換
            var material = data.Material;
            
            return new InteropPlaneData
            {
                Position = new InteropVector3(data.Position.X, data.Position.Y, data.Position.Z),
                Normal = new InteropVector3(data.Normal.X, data.Normal.Y, data.Normal.Z),
                Color = new InteropVector4(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z, material.BaseColor.W),
                Reflectivity = material.Metallic
            };
        }

        private InteropCylinderData ConvertCylinderData(Models.Nodes.CylinderData data)
        {
            // MaterialDataから旧形式のパラメータに変換
            var material = data.Material;
            
            return new InteropCylinderData
            {
                Position = new InteropVector3(data.Position.X, data.Position.Y, data.Position.Z),
                Axis = new InteropVector3(data.Axis.X, data.Axis.Y, data.Axis.Z),
                Radius = data.Radius,
                Height = data.Height,
                Color = new InteropVector4(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z, material.BaseColor.W),
                Reflectivity = material.Metallic
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
                Far = data.Far
            };
        }

        private InteropLightData ConvertLightData(Models.Nodes.LightData data)
        {
            // LightTypeを変換
            var interopType = data.Type switch
            {
                Models.Nodes.LightType.Ambient => InteropLightType.Point,  // Ambientは現在Pointとして扱う
                Models.Nodes.LightType.Directional => InteropLightType.Point,  // Directionalも現在Pointとして扱う
                Models.Nodes.LightType.Point => InteropLightType.Point,
                _ => InteropLightType.Point
            };
            
            return new InteropLightData
            {
                Position = new InteropVector3(data.Position.X, data.Position.Y, data.Position.Z),
                Color = new InteropVector4(data.Color.X, data.Color.Y, data.Color.Z, data.Color.W),
                Intensity = data.Intensity,
                Type = interopType
            };
        }
    }
}

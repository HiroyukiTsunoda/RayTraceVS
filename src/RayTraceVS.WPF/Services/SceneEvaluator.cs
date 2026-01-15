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
            // グラフ評価
            var results = nodeGraph.EvaluateGraph();

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

            // 結果から各オブジェクトを抽出
            foreach (var kvp in results)
            {
                var value = kvp.Value;

                if (value is Models.Nodes.SphereData sphereData)
                {
                    spheres.Add(ConvertSphereData(sphereData));
                }
                else if (value is Models.Nodes.PlaneData planeData)
                {
                    planes.Add(ConvertPlaneData(planeData));
                }
                else if (value is Models.Nodes.CylinderData cylinderData)
                {
                    cylinders.Add(ConvertCylinderData(cylinderData));
                }
                else if (value is Models.Nodes.CameraData cameraData)
                {
                    camera = ConvertCameraData(cameraData);
                }
                else if (value is Models.Nodes.LightData lightData)
                {
                    lights.Add(ConvertLightData(lightData));
                }
                else if (value is Models.Nodes.SceneData sceneData)
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
                }
            }

            return (spheres.ToArray(), planes.ToArray(), cylinders.ToArray(), camera, lights.ToArray());
        }

        private InteropSphereData ConvertSphereData(Models.Nodes.SphereData data)
        {
            return new InteropSphereData
            {
                Position = new InteropVector3(data.Position.X, data.Position.Y, data.Position.Z),
                Radius = data.Radius,
                Color = new InteropVector4(data.Color.X, data.Color.Y, data.Color.Z, data.Color.W),
                Reflectivity = data.Reflectivity,
                Transparency = data.Transparency,
                IOR = data.IOR
            };
        }

        private InteropPlaneData ConvertPlaneData(Models.Nodes.PlaneData data)
        {
            return new InteropPlaneData
            {
                Position = new InteropVector3(data.Position.X, data.Position.Y, data.Position.Z),
                Normal = new InteropVector3(data.Normal.X, data.Normal.Y, data.Normal.Z),
                Color = new InteropVector4(data.Color.X, data.Color.Y, data.Color.Z, data.Color.W),
                Reflectivity = data.Reflectivity
            };
        }

        private InteropCylinderData ConvertCylinderData(Models.Nodes.CylinderData data)
        {
            return new InteropCylinderData
            {
                Position = new InteropVector3(data.Position.X, data.Position.Y, data.Position.Z),
                Axis = new InteropVector3(data.Axis.X, data.Axis.Y, data.Axis.Z),
                Radius = data.Radius,
                Height = data.Height,
                Color = new InteropVector4(data.Color.X, data.Color.Y, data.Color.Z, data.Color.W),
                Reflectivity = data.Reflectivity
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
                AspectRatio = 16.0f / 9.0f
            };
        }

        private InteropLightData ConvertLightData(Models.Nodes.LightData data)
        {
            return new InteropLightData
            {
                Position = new InteropVector3(data.Position.X, data.Position.Y, data.Position.Z),
                Color = new InteropVector4(data.Color.X, data.Color.Y, data.Color.Z, data.Color.W),
                Intensity = data.Intensity,
                Type = InteropLightType.Point
            };
        }
    }
}

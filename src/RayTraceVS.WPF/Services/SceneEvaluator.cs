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
        /// <summary>
        /// 評価結果のデータ型 → SceneCollectorへの振り分け処理。
        /// 新しいデータ型を追加する場合はここに1エントリ追加するだけでよい
        /// （SceneNodeあり/なしの両経路で共通に使われる）。
        /// </summary>
        private static readonly Dictionary<Type, Action<object, SceneCollector>> Dispatchers = new()
        {
            [typeof(SphereData)] = (obj, c) =>
            {
                var data = (SphereData)obj;
                if (data.Radius > 0)
                {
#if DEBUG
                    Debug.WriteLine($"[SceneEvaluator] Sphere Pos({data.Position.X:F3}, {data.Position.Y:F3}, {data.Position.Z:F3}) R={data.Radius:F3} " +
                                    $"Base({data.Material.BaseColor.X:F3}, {data.Material.BaseColor.Y:F3}, {data.Material.BaseColor.Z:F3}, {data.Material.BaseColor.W:F3}) " +
                                    $"M={data.Material.Metallic:F3} Rgh={data.Material.Roughness:F3} T={data.Material.Transmission:F3} IOR={data.Material.IOR:F3}");
#endif
                    c.Spheres.Add(ConvertSphereData(data));
                }
            },
            [typeof(PlaneData)] = (obj, c) => c.Planes.Add(ConvertPlaneData((PlaneData)obj)),
            [typeof(BoxData)] = (obj, c) => c.Boxes.Add(ConvertBoxData((BoxData)obj)),
            [typeof(LightData)] = (obj, c) => c.Lights.Add(ConvertLightData((LightData)obj)),
            [typeof(CameraData)] = (obj, c) => c.Camera = ConvertCameraData((CameraData)obj),
            [typeof(MeshObjectData)] = (obj, c) => c.AddMeshInstance((MeshObjectData)obj),
        };

        public SceneEvaluationResult EvaluateScene(NodeGraph nodeGraph)
        {
            var collector = new SceneCollector();
            var allNodes = nodeGraph.GetAllNodes();
            var connections = nodeGraph.GetAllConnections();

            // SceneNodeが存在するか確認
            var sceneNode = allNodes.OfType<Models.Nodes.SceneNode>().FirstOrDefault();

            // グラフを評価（増分評価: Dirtyなノードのみ再評価）
            var results = nodeGraph.EvaluateGraph();

            if (sceneNode != null && connections.Any())
            {
                // SceneNodeが存在する場合：SceneNodeの出力（接続されたオブジェクト/ライトのみ）を使用
                if (results.TryGetValue(sceneNode.Id, out var sceneResult) && sceneResult is SceneData sceneData)
                {
#if DEBUG
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
#endif

                    // カメラの設定（デフォルト値でなければ使用）
                    if (sceneData.Camera.FieldOfView > 0)
                    {
                        collector.Camera = ConvertCameraData(sceneData.Camera);
                    }

                    // SceneNodeに接続されたオブジェクトのみを追加
                    foreach (var obj in sceneData.Objects)
                    {
                        collector.Dispatch(obj);
                    }

                    // SceneNodeに接続されたライトのみを追加
                    foreach (var light in sceneData.Lights)
                    {
                        collector.Lights.Add(ConvertLightData(light));
                    }

#if DEBUG
                    Debug.WriteLine($"[SceneEvaluator] Objects: spheres={collector.Spheres.Count}, planes={collector.Planes.Count}, boxes={collector.Boxes.Count}, meshInstances={collector.MeshInstances.Count}");
                    Debug.WriteLine($"[SceneEvaluator] Lights: {collector.Lights.Count}");
#endif

                    // レンダリング設定を取得
                    collector.ApplyRenderSettings(sceneData);
                }
                // SceneNodeの評価結果が取得できない場合は空シーンのまま（従来挙動）
            }
            else
            {
                // SceneNodeがない場合（フォールバック）：全ノードの評価結果をディスパッチ
                // 各ノードのEvaluateは入力未接続時に内部プロパティへフォールバックするため、
                // 接続の有無に関わらず評価結果をそのまま使える
                foreach (var node in allNodes)
                {
                    if (results.TryGetValue(node.Id, out var result) && result != null)
                    {
                        collector.Dispatch(result);
                    }
                }
            }

            return collector.ToResult();
        }

        /// <summary>
        /// 評価結果の収集先。Interop型への変換結果とレンダリング設定を蓄積し、
        /// SceneEvaluationResult を構築する。
        /// </summary>
        private sealed class SceneCollector
        {
            public List<InteropSphereData> Spheres { get; } = new();
            public List<InteropPlaneData> Planes { get; } = new();
            public List<InteropBoxData> Boxes { get; } = new();
            public List<InteropLightData> Lights { get; } = new();
            public List<InteropMeshInstanceData> MeshInstances { get; } = new();
            public Dictionary<string, InteropMeshCacheData> MeshCaches { get; } = new();

            public InteropCameraData Camera { get; set; } = new InteropCameraData
            {
                Position = new InteropVector3(0, 2, -5),
                LookAt = new InteropVector3(0, 0, 0),
                Up = new InteropVector3(0, 1, 0),
                FieldOfView = 60.0f,
                AspectRatio = 16.0f / 9.0f
            };

            // レンダリング設定（デフォルト値で初期化、SceneNodeがあれば上書き）
            private int _samplesPerPixel = 1;
            private int _maxBounces = 6;
            private int _traceRecursionDepth = 2;
            private float _exposure = 1.0f;
            private int _toneMapOperator = 2;
            private float _denoiserStabilization = 1.0f;
            private float _shadowStrength = 1.0f;
            private float _shadowAbsorptionScale = 4.0f;
            private bool _enableDenoiser = true;
            private float _gamma = 1.0f;
            // P1 optimization settings
            private float _lightAttenuationConstant = 1.0f;
            private float _lightAttenuationLinear = 0.0f;
            private float _lightAttenuationQuadratic = 0.01f;
            private int _maxShadowLights = 2;
            private float _nrdBypassDistance = 8.0f;
            private float _nrdBypassBlendRange = 2.0f;

            /// <summary>
            /// 評価結果（データ型）をディスパッチテーブル経由で振り分ける。
            /// 未登録の型（算術ノードの中間値など）は無視される。
            /// </summary>
            public void Dispatch(object? value)
            {
                if (value != null && Dispatchers.TryGetValue(value.GetType(), out var dispatcher))
                {
                    dispatcher(value, this);
                }
            }

            /// <summary>
            /// メッシュインスタンスを追加する。キャッシュが存在しない場合はスキップ（AccessViolation防止）
            /// </summary>
            public void AddMeshInstance(MeshObjectData data)
            {
                if (string.IsNullOrEmpty(data.MeshName)) return;

                if (!MeshCaches.ContainsKey(data.MeshName))
                {
                    var cache = CreateMeshCacheData(data.MeshName);
                    if (cache == null)
                    {
                        // キャッシュが存在しない場合はこのメッシュインスタンスをスキップ
                        return;
                    }
                    MeshCaches[data.MeshName] = cache;
                }
                MeshInstances.Add(ConvertMeshInstanceData(data));
            }

            /// <summary>
            /// SceneNodeの評価結果からレンダリング設定を取得する（不正値はデフォルトに置換）
            /// </summary>
            public void ApplyRenderSettings(SceneData sceneData)
            {
                _samplesPerPixel = sceneData.SamplesPerPixel > 0 ? sceneData.SamplesPerPixel : 1;
                _maxBounces = sceneData.MaxBounces > 0 ? sceneData.MaxBounces : 6;
                _traceRecursionDepth = sceneData.TraceRecursionDepth > 0 ? sceneData.TraceRecursionDepth : 2;
                _exposure = sceneData.Exposure > 0 ? sceneData.Exposure : 1.0f;
                _toneMapOperator = sceneData.ToneMapOperator;
                _denoiserStabilization = sceneData.DenoiserStabilization > 0 ? sceneData.DenoiserStabilization : 1.0f;
                _shadowStrength = sceneData.ShadowStrength >= 0 ? sceneData.ShadowStrength : 1.0f;
                _shadowAbsorptionScale = sceneData.ShadowAbsorptionScale >= 0 ? sceneData.ShadowAbsorptionScale : 4.0f;
                _enableDenoiser = sceneData.EnableDenoiser;
                _gamma = sceneData.Gamma > 0 ? sceneData.Gamma : 1.0f;
                // P1 optimization settings
                _lightAttenuationConstant = sceneData.LightAttenuationConstant > 0 ? sceneData.LightAttenuationConstant : 1.0f;
                _lightAttenuationLinear = sceneData.LightAttenuationLinear >= 0 ? sceneData.LightAttenuationLinear : 0.0f;
                _lightAttenuationQuadratic = sceneData.LightAttenuationQuadratic >= 0 ? sceneData.LightAttenuationQuadratic : 0.01f;
                _maxShadowLights = sceneData.MaxShadowLights > 0 ? sceneData.MaxShadowLights : 2;
                _nrdBypassDistance = sceneData.NRDBypassDistance > 0 ? sceneData.NRDBypassDistance : 8.0f;
                _nrdBypassBlendRange = sceneData.NRDBypassBlendRange > 0 ? sceneData.NRDBypassBlendRange : 2.0f;
            }

            public SceneEvaluationResult ToResult()
            {
                return new SceneEvaluationResult
                {
                    Spheres = Spheres.ToArray(),
                    Planes = Planes.ToArray(),
                    Boxes = Boxes.ToArray(),
                    Camera = Camera,
                    Lights = Lights.ToArray(),
                    MeshInstances = MeshInstances.ToArray(),
                    MeshCaches = MeshCaches.Values.ToArray(),
                    SamplesPerPixel = _samplesPerPixel,
                    MaxBounces = _maxBounces,
                    TraceRecursionDepth = _traceRecursionDepth,
                    Exposure = _exposure,
                    ToneMapOperator = _toneMapOperator,
                    DenoiserStabilization = _denoiserStabilization,
                    ShadowStrength = _shadowStrength,
                    ShadowAbsorptionScale = _shadowAbsorptionScale,
                    EnableDenoiser = _enableDenoiser,
                    Gamma = _gamma,
                    LightAttenuationConstant = _lightAttenuationConstant,
                    LightAttenuationLinear = _lightAttenuationLinear,
                    LightAttenuationQuadratic = _lightAttenuationQuadratic,
                    MaxShadowLights = _maxShadowLights,
                    NRDBypassDistance = _nrdBypassDistance,
                    NRDBypassBlendRange = _nrdBypassBlendRange
                };
            }
        }

        private static InteropSphereData ConvertSphereData(SphereData data)
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

        private static InteropPlaneData ConvertPlaneData(PlaneData data)
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

        private static InteropBoxData ConvertBoxData(BoxData data)
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

        private static InteropCameraData ConvertCameraData(CameraData data)
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

        private static InteropLightData ConvertLightData(LightData data)
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

        private static InteropMeshInstanceData ConvertMeshInstanceData(MeshObjectData data)
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

        private static InteropMeshCacheData? CreateMeshCacheData(string meshName)
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

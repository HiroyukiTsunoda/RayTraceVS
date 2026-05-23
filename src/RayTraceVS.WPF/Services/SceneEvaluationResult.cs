using System;
using RayTraceVS.Interop;

namespace RayTraceVS.WPF.Services
{
    /// <summary>
    /// SceneEvaluator.EvaluateScene の評価結果。
    /// 旧来の23要素タプルを置き換える。各値はそのままエンジン(Interop)へ渡すレンダリングパラメータ。
    /// </summary>
    public sealed class SceneEvaluationResult
    {
        public SphereData[] Spheres { get; init; } = Array.Empty<SphereData>();
        public PlaneData[] Planes { get; init; } = Array.Empty<PlaneData>();
        public BoxData[] Boxes { get; init; } = Array.Empty<BoxData>();
        public CameraData Camera { get; init; }
        public LightData[] Lights { get; init; } = Array.Empty<LightData>();
        public MeshInstanceData[] MeshInstances { get; init; } = Array.Empty<MeshInstanceData>();
        public MeshCacheData[] MeshCaches { get; init; } = Array.Empty<MeshCacheData>();

        public int SamplesPerPixel { get; init; }
        public int MaxBounces { get; init; }
        public int TraceRecursionDepth { get; init; }
        public float Exposure { get; init; }
        public int ToneMapOperator { get; init; }
        public float DenoiserStabilization { get; init; }
        public float ShadowStrength { get; init; }
        public float ShadowAbsorptionScale { get; init; }
        public bool EnableDenoiser { get; init; }
        public float Gamma { get; init; }

        // P1 optimization settings
        public float LightAttenuationConstant { get; init; }
        public float LightAttenuationLinear { get; init; }
        public float LightAttenuationQuadratic { get; init; }
        public int MaxShadowLights { get; init; }
        public float NRDBypassDistance { get; init; }
        public float NRDBypassBlendRange { get; init; }
    }
}

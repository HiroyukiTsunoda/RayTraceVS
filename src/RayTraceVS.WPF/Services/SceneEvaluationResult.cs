using System;
using RayTraceVS.Interop;

namespace RayTraceVS.WPF.Services
{
    /// <summary>
    /// SceneEvaluator.EvaluateScene の評価結果。
    /// 旧来の23要素タプルを置き換える。各値はそのままエンジン(Interop)へ渡すレンダリングパラメータ。
    /// 既定値は RenderService.UpdateScene の旧デフォルト引数と同値（ウォームアップ等の最小構築用）。
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

        public int SamplesPerPixel { get; init; } = 1;
        public int MaxBounces { get; init; } = 6;
        public int TraceRecursionDepth { get; init; } = 2;
        public float Exposure { get; init; } = 1.0f;
        public int ToneMapOperator { get; init; } = 2;
        public float DenoiserStabilization { get; init; } = 1.0f;
        public float ShadowStrength { get; init; } = 1.0f;
        public float ShadowAbsorptionScale { get; init; } = 4.0f;
        public bool EnableDenoiser { get; init; } = true;
        public float Gamma { get; init; } = 1.0f;

        /// <summary>
        /// フォトンデバッグ表示モード（シーン評価後にUI側で設定するため set 可能）
        /// </summary>
        public int PhotonDebugMode { get; set; } = 0;

        /// <summary>
        /// フォトンデバッグ表示スケール（シーン評価後にUI側で設定するため set 可能）
        /// </summary>
        public float PhotonDebugScale { get; set; } = 1.0f;

        // P1 optimization settings
        public float LightAttenuationConstant { get; init; } = 1.0f;
        public float LightAttenuationLinear { get; init; } = 0.0f;
        public float LightAttenuationQuadratic { get; init; } = 0.01f;
        public int MaxShadowLights { get; init; } = 2;
        public float NRDBypassDistance { get; init; } = 8.0f;
        public float NRDBypassBlendRange { get; init; } = 2.0f;
    }
}

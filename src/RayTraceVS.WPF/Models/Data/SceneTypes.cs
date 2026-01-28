using System.Collections.Generic;

namespace RayTraceVS.WPF.Models.Data
{
    /// <summary>
    /// シーンデータ構造体
    /// レンダリング時に使用するシーン全体のデータを保持
    /// </summary>
    public struct SceneData
    {
        public CameraData Camera;
        public List<object> Objects;
        public List<LightData> Lights;
        public int SamplesPerPixel;
        public int MaxBounces;
        public int TraceRecursionDepth;
        public float Exposure;
        public int ToneMapOperator;
        public float DenoiserStabilization;
        public float ShadowStrength;
        public float ShadowAbsorptionScale;
        public bool EnableDenoiser;
        public float Gamma;
        
        // P1 optimization settings
        public float LightAttenuationConstant;
        public float LightAttenuationLinear;
        public float LightAttenuationQuadratic;
        public int MaxShadowLights;
        public float NRDBypassDistance;
        public float NRDBypassBlendRange;
    }
}

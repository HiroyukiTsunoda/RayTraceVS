using System.Numerics;

namespace RayTraceVS.WPF.Models.Data
{
    /// <summary>
    /// ライトタイプ列挙型
    /// </summary>
    public enum LightType
    {
        Ambient,        // 環境光
        Directional,    // 並行光源（太陽光など）
        Point           // 点光源
    }

    /// <summary>
    /// ライトデータ構造体
    /// </summary>
    public struct LightData
    {
        public LightType Type;
        public Vector3 Position;        // Point用
        public Vector3 Direction;       // Directional用
        public Vector4 Color;
        public float Intensity;
        public float Attenuation;       // Point用の減衰係数
        public float Radius;            // エリアライトの半径（0=ポイントライト、ハードシャドウ）
        public float SoftShadowSamples; // ソフトシャドウのサンプル数（1-16）
    }
}

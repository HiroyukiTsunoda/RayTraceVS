using System.Numerics;

namespace RayTraceVS.WPF.Models.Data
{
    /// <summary>
    /// マテリアルデータ構造体
    /// レンダリング時に使用するマテリアルパラメータを保持
    /// </summary>
    public struct MaterialData
    {
        public Vector4 BaseColor;
        public float Metallic;
        public float Roughness;
        public float Transmission;
        public float IOR;
        public Vector4 Emission;
        public float Specular;  // Specular intensity (0.0 = none, 1.0 = full)
        public Vector3 Absorption; // Beer-Lambert sigmaA

        /// <summary>
        /// デフォルトマテリアル（白色Diffuse）
        /// </summary>
        public static MaterialData Default => new MaterialData
        {
            BaseColor = new Vector4(0.8f, 0.8f, 0.8f, 1.0f),
            Metallic = 0.0f,
            Roughness = 0.5f,
            Transmission = 0.0f,
            IOR = 1.5f,
            Emission = Vector4.Zero,
            Specular = 0.5f,
            Absorption = Vector3.Zero
        };
    }
}

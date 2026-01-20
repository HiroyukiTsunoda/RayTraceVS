using System;
using System.Collections.Generic;
using System.Numerics;
using RayTraceVS.WPF.Models.Data;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// 統合BSDFマテリアルノード
    /// Principled BSDFスタイルで、様々なマテリアルを1つのノードで表現
    /// </summary>
    public class MaterialBSDFNode : Node
    {
        // ベースカラー（基本色）
        public Vector4 BaseColor { get; set; } = new Vector4(0.8f, 0.8f, 0.8f, 1.0f);

        // 金属度（0.0 = 誘電体, 1.0 = 金属）
        public float Metallic { get; set; } = 0.0f;

        // 粗さ（0.0 = 完全鏡面, 1.0 = 完全拡散）
        public float Roughness { get; set; } = 0.5f;

        // 透過度（0.0 = 不透明, 1.0 = 完全透過）
        public float Transmission { get; set; } = 0.0f;

        // 屈折率（Index of Refraction）
        public float IOR { get; set; } = 1.5f;

        // 発光色・強度
        public Vector4 Emission { get; set; } = Vector4.Zero;

        public MaterialBSDFNode() : base("Material BSDF", NodeCategory.Material)
        {
            // 入力ソケット
            AddInputSocket("Base Color", SocketType.Color);
            AddInputSocket("Metallic", SocketType.Float);
            AddInputSocket("Roughness", SocketType.Float);
            AddInputSocket("Transmission", SocketType.Float);
            AddInputSocket("IOR", SocketType.Float);
            AddInputSocket("Emission", SocketType.Color);

            // 出力ソケット
            AddOutputSocket("Material", SocketType.Material);
        }

        public override object? Evaluate(Dictionary<Guid, object?> inputValues)
        {
            // 入力値を取得（接続されていない場合はデフォルト値を使用）
            var baseColorInput = GetInputValue<Vector4?>("Base Color", inputValues);
            var baseColor = baseColorInput ?? BaseColor;
            
            var metallic = GetInputValue<float?>("Metallic", inputValues) ?? Metallic;
            var roughness = GetInputValue<float?>("Roughness", inputValues) ?? Roughness;
            var transmission = GetInputValue<float?>("Transmission", inputValues) ?? Transmission;
            var ior = GetInputValue<float?>("IOR", inputValues) ?? IOR;
            
            var emissionInput = GetInputValue<Vector4?>("Emission", inputValues);
            var emission = emissionInput ?? Emission;

            // 値の範囲を制限
            metallic = Math.Clamp(metallic, 0.0f, 1.0f);
            roughness = Math.Clamp(roughness, 0.0f, 1.0f);
            transmission = Math.Clamp(transmission, 0.0f, 1.0f);
            ior = Math.Max(ior, 1.0f);

            return new MaterialData
            {
                BaseColor = baseColor,
                Metallic = metallic,
                Roughness = roughness,
                Transmission = transmission,
                IOR = ior,
                Emission = emission
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using RayTraceVS.WPF.Models.Data;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// 金属マテリアルノード
    /// </summary>
    public class MetalMaterialNode : Node
    {
        /// <summary>
        /// ベースカラー（金属の反射色）
        /// 金: (1.0, 0.843, 0.0)
        /// 銀: (0.95, 0.93, 0.88)
        /// 銅: (0.955, 0.637, 0.538)
        /// </summary>
        public Vector4 BaseColor { get; set; } = new Vector4(0.95f, 0.93f, 0.88f, 1.0f); // デフォルト: 銀

        /// <summary>
        /// 粗さ（0.0 = 完全鏡面, 1.0 = ブラシ仕上げ）
        /// </summary>
        public float Roughness { get; set; } = 0.1f;

        public MetalMaterialNode() : base("Metal", NodeCategory.Material)
        {
            // 入力ソケット
            AddInputSocket("Base Color", SocketType.Color);
            AddInputSocket("Roughness", SocketType.Float);

            // 出力ソケット
            AddOutputSocket("Material", SocketType.Material);
        }

        public override object? Evaluate(Dictionary<Guid, object?> inputValues)
        {
            // 入力値を取得
            var baseColorInput = GetInputValue<Vector4?>("Base Color", inputValues);
            var baseColor = baseColorInput ?? BaseColor;

            var roughness = GetInputValue<float?>("Roughness", inputValues) ?? Roughness;
            roughness = Math.Clamp(roughness, 0.0f, 1.0f);

            return new MaterialData
            {
                BaseColor = baseColor,
                Metallic = 1.0f,           // 完全金属
                Roughness = roughness,
                Transmission = 0.0f,       // 不透明
                IOR = 1.5f,                // 金属のIORは通常無視される
                Emission = Vector4.Zero    // 発光なし
            };
        }
    }
}

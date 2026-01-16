using System;
using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// シンプルな拡散反射（Diffuse/Lambertian）マテリアルノード
    /// </summary>
    public class DiffuseMaterialNode : Node
    {
        /// <summary>
        /// ベースカラー（拡散色）
        /// </summary>
        public Vector4 BaseColor { get; set; } = new Vector4(0.8f, 0.8f, 0.8f, 1.0f);

        /// <summary>
        /// 粗さ（0.0 = 少し光沢あり, 1.0 = 完全拡散）
        /// </summary>
        public float Roughness { get; set; } = 1.0f;

        public DiffuseMaterialNode() : base("Diffuse", NodeCategory.Material)
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
                Metallic = 0.0f,           // 非金属
                Roughness = roughness,
                Transmission = 0.0f,       // 不透明
                IOR = 1.5f,
                Emission = Vector4.Zero    // 発光なし
            };
        }
    }
}

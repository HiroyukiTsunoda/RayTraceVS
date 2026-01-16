using System;
using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// ガラス/透明マテリアルノード
    /// </summary>
    public class GlassMaterialNode : Node
    {
        /// <summary>
        /// 透過色（透明なガラスは白、色付きガラスはRGBで指定）
        /// </summary>
        public Vector4 Color { get; set; } = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);

        /// <summary>
        /// 粗さ（0.0 = 完全透明/鏡面, 1.0 = すりガラス）
        /// </summary>
        public float Roughness { get; set; } = 0.0f;

        /// <summary>
        /// 屈折率（IOR）- ガラスは約1.5、ダイヤモンドは約2.4
        /// </summary>
        public float IOR { get; set; } = 1.5f;

        /// <summary>
        /// 透明度（0.0 = 不透明, 1.0 = 完全透明）
        /// </summary>
        public float Transparency { get; set; } = 1.0f;

        public GlassMaterialNode() : base("Glass", NodeCategory.Material)
        {
            // 入力ソケット
            AddInputSocket("Color", SocketType.Color);
            AddInputSocket("Roughness", SocketType.Float);
            AddInputSocket("IOR", SocketType.Float);
            AddInputSocket("Transparency", SocketType.Float);

            // 出力ソケット
            AddOutputSocket("Material", SocketType.Material);
        }

        public override object? Evaluate(Dictionary<Guid, object?> inputValues)
        {
            // 入力値を取得
            var colorInput = GetInputValue<Vector4?>("Color", inputValues);
            var color = colorInput ?? Color;

            var roughness = GetInputValue<float?>("Roughness", inputValues) ?? Roughness;
            roughness = Math.Clamp(roughness, 0.0f, 1.0f);

            var ior = GetInputValue<float?>("IOR", inputValues) ?? IOR;
            ior = Math.Max(ior, 1.0f);

            var transparency = GetInputValue<float?>("Transparency", inputValues) ?? Transparency;
            transparency = Math.Clamp(transparency, 0.0f, 1.0f);

            return new MaterialData
            {
                BaseColor = color,
                Metallic = 0.0f,           // 非金属
                Roughness = roughness,
                Transmission = transparency,
                IOR = ior,
                Emission = Vector4.Zero    // 発光なし
            };
        }
    }
}

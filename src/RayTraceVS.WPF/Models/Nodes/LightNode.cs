using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
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

    /// <summary>
    /// ポイントライトノード（点光源/エリアライト）
    /// </summary>
    public class PointLightNode : Node
    {
        public Vector3 LightPosition { get; set; } = new Vector3(5, 5, -5);
        public Vector4 Color { get; set; } = Vector4.One;
        public float Intensity { get; set; } = 1.0f;
        public float Attenuation { get; set; } = 0.1f;
        public float Radius { get; set; } = 0.0f;           // 0 = ポイントライト（ハードシャドウ）
        public float SoftShadowSamples { get; set; } = 4.0f; // ソフトシャドウのサンプル数

        public PointLightNode() : base("Point Light", NodeCategory.Light)
        {
            AddInputSocket("Position", SocketType.Vector3);
            AddInputSocket("Color", SocketType.Color);
            AddInputSocket("Intensity", SocketType.Float);
            AddInputSocket("Radius", SocketType.Float);     // エリアライトの半径
            AddInputSocket("Shadow Samples", SocketType.Float); // サンプル数
            AddOutputSocket("Light", SocketType.Light);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            var positionInput = GetInputValue<Vector3?>("Position", inputValues);
            var colorInput = GetInputValue<Vector4?>("Color", inputValues);
            var intensityInput = GetInputValue<float?>("Intensity", inputValues);
            var radiusInput = GetInputValue<float?>("Radius", inputValues);
            var samplesInput = GetInputValue<float?>("Shadow Samples", inputValues);
            
            var position = positionInput ?? LightPosition;
            var color = colorInput ?? Color;
            var intensity = intensityInput ?? Intensity;
            var radius = radiusInput ?? Radius;
            var samples = samplesInput ?? SoftShadowSamples;

            return new LightData
            {
                Type = LightType.Point,
                Position = position,
                Direction = Vector3.Zero,
                Color = color,
                Intensity = intensity,
                Attenuation = Attenuation,
                Radius = radius,
                SoftShadowSamples = System.Math.Clamp(samples, 1.0f, 16.0f)
            };
        }
    }
}

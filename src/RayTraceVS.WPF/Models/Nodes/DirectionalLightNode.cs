using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// ディレクショナルライトノード（並行光源）
    /// 太陽光のように、無限遠から平行に照らす光源
    /// ソフトシャドウ用のAngular Radiusを設定可能
    /// </summary>
    public class DirectionalLightNode : Node
    {
        public Vector3 Direction { get; set; } = new Vector3(0, -1, 0);
        public Vector4 Color { get; set; } = Vector4.One;
        public float Intensity { get; set; } = 1.0f;
        public float AngularRadius { get; set; } = 0.0f;     // 0 = ハードシャドウ、>0 = ソフトシャドウ（太陽なら約0.01）
        public float SoftShadowSamples { get; set; } = 4.0f; // ソフトシャドウのサンプル数

        public DirectionalLightNode() : base("Directional Light", NodeCategory.Light)
        {
            AddInputSocket("Direction", SocketType.Vector3);
            AddInputSocket("Color", SocketType.Color);
            AddInputSocket("Intensity", SocketType.Float);
            AddInputSocket("Angular Radius", SocketType.Float); // 角度半径（ラジアン）
            AddInputSocket("Shadow Samples", SocketType.Float); // サンプル数
            AddOutputSocket("Light", SocketType.Light);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            var directionInput = GetInputValue<Vector3?>("Direction", inputValues);
            var colorInput = GetInputValue<Vector4?>("Color", inputValues);
            var intensityInput = GetInputValue<float?>("Intensity", inputValues);
            var angularRadiusInput = GetInputValue<float?>("Angular Radius", inputValues);
            var samplesInput = GetInputValue<float?>("Shadow Samples", inputValues);
            
            var direction = directionInput ?? Direction;
            var color = colorInput ?? Color;
            var intensity = intensityInput ?? Intensity;
            var angularRadius = angularRadiusInput ?? AngularRadius;
            var samples = samplesInput ?? SoftShadowSamples;

            return new LightData
            {
                Type = LightType.Directional,
                Position = Vector3.Zero,
                Direction = Vector3.Normalize(direction),
                Color = color,
                Intensity = intensity,
                Attenuation = 0.0f,
                Radius = angularRadius,  // DirectionalライトではRadiusを角度半径として使用
                SoftShadowSamples = System.Math.Clamp(samples, 1.0f, 16.0f)
            };
        }
    }
}

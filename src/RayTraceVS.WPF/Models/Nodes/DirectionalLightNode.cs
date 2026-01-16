using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// ディレクショナルライトノード（並行光源）
    /// 太陽光のように、無限遠から平行に照らす光源
    /// </summary>
    public class DirectionalLightNode : Node
    {
        public Vector3 Direction { get; set; } = new Vector3(0, -1, 0);
        public Vector4 Color { get; set; } = Vector4.One;
        public float Intensity { get; set; } = 1.0f;

        public DirectionalLightNode() : base("Directional Light", NodeCategory.Light)
        {
            AddInputSocket("Direction", SocketType.Vector3);
            AddInputSocket("Color", SocketType.Color);
            AddInputSocket("Intensity", SocketType.Float);
            AddOutputSocket("Light", SocketType.Light);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            var directionInput = GetInputValue<Vector3?>("Direction", inputValues);
            var colorInput = GetInputValue<Vector4?>("Color", inputValues);
            var intensityInput = GetInputValue<float?>("Intensity", inputValues);
            
            var direction = directionInput ?? Direction;
            var color = colorInput ?? Color;
            var intensity = intensityInput ?? Intensity;

            return new LightData
            {
                Type = LightType.Directional,
                Position = Vector3.Zero,
                Direction = Vector3.Normalize(direction),
                Color = color,
                Intensity = intensity,
                Attenuation = 0.0f
            };
        }
    }
}

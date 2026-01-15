using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    public class LightNode : Node
    {
        public Vector3 ObjectPosition { get; set; } = new Vector3(5, 5, -5);
        public Vector4 Color { get; set; } = Vector4.One;
        public float Intensity { get; set; } = 1.0f;

        public LightNode() : base("ポイントライト", NodeCategory.Light)
        {
            AddInputSocket("位置", SocketType.Vector3);
            AddInputSocket("色", SocketType.Color);
            AddInputSocket("強度", SocketType.Float);
            AddOutputSocket("ライト", SocketType.Light);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            var positionInput = GetInputValue<Vector3>("位置", inputValues);
            var colorInput = GetInputValue<Vector4>("色", inputValues);
            var intensityInput = GetInputValue<float>("強度", inputValues);
            
            var position = positionInput != null ? (Vector3)positionInput : ObjectPosition;
            var color = colorInput != null ? (Vector4)colorInput : Color;
            var intensity = intensityInput != null ? (float)intensityInput : Intensity;

            return new LightData
            {
                Position = position,
                Color = color,
                Intensity = intensity
            };
        }
    }

    public struct LightData
    {
        public Vector3 Position;
        public Vector4 Color;
        public float Intensity;
    }
}

using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    public class SphereNode : Node
    {
        public Vector3 ObjectPosition { get; set; } = Vector3.Zero;
        public float Radius { get; set; } = 1.0f;
        public Vector4 Color { get; set; } = new Vector4(1, 1, 1, 1);
        public float Reflectivity { get; set; } = 0.0f;
        public float Transparency { get; set; } = 0.0f;
        public float IOR { get; set; } = 1.5f; // 屈折率

        public SphereNode() : base("球", NodeCategory.Object)
        {
            AddInputSocket("位置", SocketType.Vector3);
            AddInputSocket("半径", SocketType.Float);
            AddInputSocket("色", SocketType.Color);
            AddOutputSocket("オブジェクト", SocketType.Object);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            var positionInput = GetInputValue<Vector3>("位置", inputValues);
            var radiusInput = GetInputValue<float>("半径", inputValues);
            var colorInput = GetInputValue<Vector4>("色", inputValues);
            
            var position = positionInput != null ? (Vector3)positionInput : ObjectPosition;
            var radius = radiusInput != null ? (float)radiusInput : Radius;
            var color = colorInput != null ? (Vector4)colorInput : Color;

            return new SphereData
            {
                Position = position,
                Radius = radius,
                Color = color,
                Reflectivity = Reflectivity,
                Transparency = Transparency,
                IOR = IOR
            };
        }
    }

    public struct SphereData
    {
        public Vector3 Position;
        public float Radius;
        public Vector4 Color;
        public float Reflectivity;
        public float Transparency;
        public float IOR;
    }
}

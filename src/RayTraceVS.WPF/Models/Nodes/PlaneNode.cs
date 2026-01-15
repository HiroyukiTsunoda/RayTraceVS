using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    public class PlaneNode : Node
    {
        public Vector3 ObjectPosition { get; set; } = Vector3.Zero;
        public Vector3 Normal { get; set; } = Vector3.UnitY;
        public Vector4 Color { get; set; } = new Vector4(0.8f, 0.8f, 0.8f, 1);
        public float Reflectivity { get; set; } = 0.0f;

        public PlaneNode() : base("平面", NodeCategory.Object)
        {
            AddInputSocket("位置", SocketType.Vector3);
            AddInputSocket("法線", SocketType.Vector3);
            AddInputSocket("色", SocketType.Color);
            AddOutputSocket("オブジェクト", SocketType.Object);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            var positionInput = GetInputValue<Vector3>("位置", inputValues);
            var normalInput = GetInputValue<Vector3>("法線", inputValues);
            var colorInput = GetInputValue<Vector4>("色", inputValues);
            
            var position = positionInput != null ? (Vector3)positionInput : ObjectPosition;
            var normal = normalInput != null ? (Vector3)normalInput : Normal;
            var color = colorInput != null ? (Vector4)colorInput : Color;

            return new PlaneData
            {
                Position = position,
                Normal = Vector3.Normalize(normal),
                Color = color,
                Reflectivity = Reflectivity
            };
        }
    }

    public struct PlaneData
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Color;
        public float Reflectivity;
    }
}

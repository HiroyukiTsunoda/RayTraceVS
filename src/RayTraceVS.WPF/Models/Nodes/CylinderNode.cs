using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    public class CylinderNode : Node
    {
        public Vector3 ObjectPosition { get; set; } = Vector3.Zero;
        public Vector3 Axis { get; set; } = Vector3.UnitY;
        public float Radius { get; set; } = 0.5f;
        public float Height { get; set; } = 2.0f;
        public Vector4 Color { get; set; } = new Vector4(1, 1, 1, 1);
        public float Reflectivity { get; set; } = 0.0f;

        public CylinderNode() : base("円柱", NodeCategory.Object)
        {
            AddInputSocket("位置", SocketType.Vector3);
            AddInputSocket("軸", SocketType.Vector3);
            AddInputSocket("半径", SocketType.Float);
            AddInputSocket("高さ", SocketType.Float);
            AddInputSocket("色", SocketType.Color);
            AddOutputSocket("オブジェクト", SocketType.Object);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            var positionInput = GetInputValue<Vector3>("位置", inputValues);
            var axisInput = GetInputValue<Vector3>("軸", inputValues);
            var radiusInput = GetInputValue<float>("半径", inputValues);
            var heightInput = GetInputValue<float>("高さ", inputValues);
            var colorInput = GetInputValue<Vector4>("色", inputValues);
            
            var position = positionInput != null ? (Vector3)positionInput : ObjectPosition;
            var axis = axisInput != null ? (Vector3)axisInput : Axis;
            var radius = radiusInput != null ? (float)radiusInput : Radius;
            var height = heightInput != null ? (float)heightInput : Height;
            var color = colorInput != null ? (Vector4)colorInput : Color;

            return new CylinderData
            {
                Position = position,
                Axis = Vector3.Normalize(axis),
                Radius = radius,
                Height = height,
                Color = color,
                Reflectivity = Reflectivity
            };
        }
    }

    public struct CylinderData
    {
        public Vector3 Position;
        public Vector3 Axis;
        public float Radius;
        public float Height;
        public Vector4 Color;
        public float Reflectivity;
    }
}

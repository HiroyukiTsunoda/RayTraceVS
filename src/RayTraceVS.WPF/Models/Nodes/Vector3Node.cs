using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    public class Vector3Node : Node
    {
        public float X { get; set; } = 0.0f;
        public float Y { get; set; } = 0.0f;
        public float Z { get; set; } = 0.0f;

        public Vector3Node() : base("Vector3", NodeCategory.Math)
        {
            AddInputSocket("X", SocketType.Float);
            AddInputSocket("Y", SocketType.Float);
            AddInputSocket("Z", SocketType.Float);
            AddOutputSocket("ベクトル", SocketType.Vector3);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            var xInput = GetInputValue<float>("X", inputValues);
            var yInput = GetInputValue<float>("Y", inputValues);
            var zInput = GetInputValue<float>("Z", inputValues);
            
            var x = xInput != null ? (float)xInput : X;
            var y = yInput != null ? (float)yInput : Y;
            var z = zInput != null ? (float)zInput : Z;

            return new Vector3(x, y, z);
        }
    }
}

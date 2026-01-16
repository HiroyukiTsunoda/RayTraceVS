using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    public class CameraNode : Node
    {
        public Vector3 CameraPosition { get; set; } = new Vector3(0, 2, -5);
        public Vector3 LookAt { get; set; } = Vector3.Zero;
        public Vector3 Up { get; set; } = Vector3.UnitY;
        public float FieldOfView { get; set; } = 60.0f;
        public float Near { get; set; } = 0.1f;
        public float Far { get; set; } = 1000.0f;

        public CameraNode() : base("Camera", NodeCategory.Camera)
        {
            AddInputSocket("Position", SocketType.Vector3);
            AddInputSocket("Look At", SocketType.Vector3);
            AddOutputSocket("Camera", SocketType.Camera);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            var positionInput = GetInputValue<Vector3?>("Position", inputValues);
            var lookAtInput = GetInputValue<Vector3?>("Look At", inputValues);
            
            var position = positionInput ?? CameraPosition;
            var lookAt = lookAtInput ?? LookAt;

            return new CameraData
            {
                Position = position,
                LookAt = lookAt,
                Up = Up,
                FieldOfView = FieldOfView,
                Near = Near,
                Far = Far
            };
        }
    }

    public struct CameraData
    {
        public Vector3 Position;
        public Vector3 LookAt;
        public Vector3 Up;
        public float FieldOfView;
        public float Near;
        public float Far;
    }
}

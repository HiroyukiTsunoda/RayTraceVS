using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    public class CameraNode : Node
    {
        public Vector3 ObjectPosition { get; set; } = new Vector3(0, 2, -5);
        public Vector3 LookAt { get; set; } = Vector3.Zero;
        public Vector3 Up { get; set; } = Vector3.UnitY;
        public float FieldOfView { get; set; } = 60.0f;

        public CameraNode() : base("カメラ", NodeCategory.Camera)
        {
            AddInputSocket("位置", SocketType.Vector3);
            AddInputSocket("注視点", SocketType.Vector3);
            AddOutputSocket("カメラ", SocketType.Camera);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            var positionInput = GetInputValue<Vector3>("位置", inputValues);
            var lookAtInput = GetInputValue<Vector3>("注視点", inputValues);
            
            var position = positionInput != null ? (Vector3)positionInput : ObjectPosition;
            var lookAt = lookAtInput != null ? (Vector3)lookAtInput : LookAt;

            return new CameraData
            {
                Position = position,
                LookAt = lookAt,
                Up = Up,
                FieldOfView = FieldOfView
            };
        }
    }

    public struct CameraData
    {
        public Vector3 Position;
        public Vector3 LookAt;
        public Vector3 Up;
        public float FieldOfView;
    }
}

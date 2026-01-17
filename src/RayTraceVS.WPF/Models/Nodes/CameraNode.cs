using System.Collections.Generic;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RayTraceVS.WPF.Models.Nodes
{
    public partial class CameraNode : Node
    {
        [ObservableProperty]
        private Vector3 cameraPosition = new Vector3(0, 2, -5);
        
        [ObservableProperty]
        private Vector3 lookAt = Vector3.Zero;
        
        [ObservableProperty]
        private Vector3 up = Vector3.UnitY;
        
        [ObservableProperty]
        private float fieldOfView = 60.0f;
        
        [ObservableProperty]
        private float near = 0.1f;
        
        [ObservableProperty]
        private float far = 1000.0f;
        
        // DoF (Depth of Field) parameters
        [ObservableProperty]
        private float apertureSize = 0.0f;  // 0.0 = DoF disabled, larger = stronger bokeh
        
        [ObservableProperty]
        private float focusDistance = 5.0f; // Distance to the focal plane

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
                Far = Far,
                ApertureSize = ApertureSize,
                FocusDistance = FocusDistance
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
        public float ApertureSize;
        public float FocusDistance;
    }
}

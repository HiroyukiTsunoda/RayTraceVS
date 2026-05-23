using System;
using System.Collections.Generic;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using RayTraceVS.WPF.Models.Data;
using RayTraceVS.WPF.Models.Serialization;

namespace RayTraceVS.WPF.Models.Nodes
{
    public partial class CameraNode : Node, ISerializableNode
    {
        private Vector3 _cameraPosition = new Vector3(0, 2, -5);
        public Vector3 CameraPosition
        {
            get => _cameraPosition;
            set
            {
                if (SetProperty(ref _cameraPosition, value))
                {
                    MarkDirty();
                }
            }
        }
        
        private Vector3 _lookAt = Vector3.Zero;
        public Vector3 LookAt
        {
            get => _lookAt;
            set
            {
                if (SetProperty(ref _lookAt, value))
                {
                    MarkDirty();
                }
            }
        }
        
        private Vector3 _up = Vector3.UnitY;
        public Vector3 Up
        {
            get => _up;
            set
            {
                if (SetProperty(ref _up, value))
                {
                    MarkDirty();
                }
            }
        }
        
        private float _fieldOfView = 60.0f;
        public float FieldOfView
        {
            get => _fieldOfView;
            set
            {
                if (SetProperty(ref _fieldOfView, value))
                {
                    MarkDirty();
                }
            }
        }
        
        private float _near = 0.1f;
        public float Near
        {
            get => _near;
            set
            {
                if (SetProperty(ref _near, value))
                {
                    MarkDirty();
                }
            }
        }
        
        private float _far = 1000.0f;
        public float Far
        {
            get => _far;
            set
            {
                if (SetProperty(ref _far, value))
                {
                    MarkDirty();
                }
            }
        }
        
        // DoF (Depth of Field) parameters
        private float _apertureSize = 0.0f;  // 0.0 = DoF disabled, larger = stronger bokeh
        public float ApertureSize
        {
            get => _apertureSize;
            set
            {
                if (SetProperty(ref _apertureSize, value))
                {
                    MarkDirty();
                }
            }
        }
        
        private float _focusDistance = 5.0f; // Distance to the focal plane
        public float FocusDistance
        {
            get => _focusDistance;
            set
            {
                if (SetProperty(ref _focusDistance, value))
                {
                    MarkDirty();
                }
            }
        }

        public CameraNode() : base("Camera", NodeCategory.Camera)
        {
            AddInputSocket("Position", SocketType.Vector3);
            AddInputSocket("Look At", SocketType.Vector3);
            AddOutputSocket("Camera", SocketType.Camera);
        }

        public override object? Evaluate(Dictionary<Guid, object?> inputValues)
        {
            var positionInput = GetInputValue<Vector3?>("Position", inputValues);
            var lookAtInput = GetInputValue<Vector3?>("Look At", inputValues);

            var position = positionInput ?? CameraPosition;
            var lookAtValue = lookAtInput ?? LookAt;

            return new CameraData
            {
                Position = position,
                LookAt = lookAtValue,
                Up = Up,
                FieldOfView = FieldOfView,
                Near = Near,
                Far = Far,
                ApertureSize = ApertureSize,
                FocusDistance = FocusDistance
            };
        }

        #region ISerializableNode
        public void SerializeProperties(IDictionary<string, object?> properties)
        {
            properties["CameraPosition"] = CameraPosition;
            properties["LookAt"] = LookAt;
            properties["Up"] = Up;
            properties["FieldOfView"] = FieldOfView;
            properties["Near"] = Near;
            properties["Far"] = Far;
            properties["ApertureSize"] = ApertureSize;
            properties["FocusDistance"] = FocusDistance;
        }

        public void DeserializeProperties(IReadOnlyDictionary<string, object?> properties)
        {
            // 新形式
            if (properties.TryGetValue("CameraPosition", out var camPos))
                CameraPosition = SerializationHelpers.ConvertToVector3(camPos);
            // 後方互換性: 旧形式のPosition
            else if (properties.TryGetValue("Position", out var oldCamPos))
                CameraPosition = SerializationHelpers.ConvertToVector3(oldCamPos);

            if (properties.TryGetValue("LookAt", out var lookAt))
                LookAt = SerializationHelpers.ConvertToVector3(lookAt);
            if (properties.TryGetValue("Up", out var up))
                Up = SerializationHelpers.ConvertToVector3(up);
            if (properties.TryGetValue("FieldOfView", out var fov))
                FieldOfView = Convert.ToSingle(fov);
            if (properties.TryGetValue("Near", out var near))
                Near = Convert.ToSingle(near);
            if (properties.TryGetValue("Far", out var far))
                Far = Convert.ToSingle(far);
            if (properties.TryGetValue("ApertureSize", out var aperture))
                ApertureSize = Convert.ToSingle(aperture);
            if (properties.TryGetValue("FocusDistance", out var focusDist))
                FocusDistance = Convert.ToSingle(focusDist);
        }
        #endregion
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Numerics;
using RayTraceVS.WPF.Models.Data;
using RayTraceVS.WPF.Models.Serialization;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// ポイントライトノード（点光源/エリアライト）
    /// </summary>
    public partial class PointLightNode : Node, ISerializableNode
    {
        private Vector3 _lightPosition = new Vector3(5, 5, -5);
        public Vector3 LightPosition
        {
            get => _lightPosition;
            set
            {
                if (_lightPosition != value)
                {
                    _lightPosition = value;
                    OnPropertyChanged(nameof(LightPosition));
                    MarkDirty();
                }
            }
        }
        
        private Vector4 _color = Vector4.One;
        public Vector4 Color
        {
            get => _color;
            set
            {
                if (_color != value)
                {
                    _color = value;
                    OnPropertyChanged(nameof(Color));
                    MarkDirty();
                }
            }
        }
        
        private float _intensity = 1.0f;
        public float Intensity
        {
            get => _intensity;
            set
            {
                if (SetProperty(ref _intensity, value))
                {
                    MarkDirty();
                }
            }
        }
        
        private float _attenuation = 0.1f;
        public float Attenuation
        {
            get => _attenuation;
            set
            {
                if (SetProperty(ref _attenuation, value))
                {
                    MarkDirty();
                }
            }
        }
        
        private float _radius = 0.0f;           // 0 = ポイントライト（ハードシャドウ）
        public float Radius
        {
            get => _radius;
            set
            {
                if (SetProperty(ref _radius, value))
                {
                    MarkDirty();
                }
            }
        }
        
        private float _softShadowSamples = 4.0f; // ソフトシャドウのサンプル数
        public float SoftShadowSamples
        {
            get => _softShadowSamples;
            set
            {
                if (SetProperty(ref _softShadowSamples, value))
                {
                    MarkDirty();
                }
            }
        }

        public PointLightNode() : base("Point Light", NodeCategory.Light)
        {
            AddInputSocket("Position", SocketType.Vector3);
            AddInputSocket("Color", SocketType.Color);
            AddInputSocket("Intensity", SocketType.Float);
            AddInputSocket("Radius", SocketType.Float);     // エリアライトの半径
            AddInputSocket("Shadow Samples", SocketType.Float); // サンプル数
            AddOutputSocket("Light", SocketType.Light);
        }

        public override object? Evaluate(Dictionary<Guid, object?> inputValues)
        {
            var positionInput = GetInputValue<Vector3?>("Position", inputValues);
            var colorInput = GetInputValue<Vector4?>("Color", inputValues);
            var intensityInput = GetInputValue<float?>("Intensity", inputValues);
            var radiusInput = GetInputValue<float?>("Radius", inputValues);
            var samplesInput = GetInputValue<float?>("Shadow Samples", inputValues);
            
            var position = positionInput ?? LightPosition;
            var color = colorInput ?? Color;
            var intensity = intensityInput ?? Intensity;
            var radiusValue = radiusInput ?? Radius;
            var samples = samplesInput ?? SoftShadowSamples;

            return new LightData
            {
                Type = LightType.Point,
                Position = position,
                Direction = Vector3.Zero,
                Color = color,
                Intensity = intensity,
                Attenuation = Attenuation,
                Radius = radiusValue,
                SoftShadowSamples = Math.Clamp(samples, 1.0f, 16.0f)
            };
        }

        #region ISerializableNode
        public void SerializeProperties(IDictionary<string, object?> properties)
        {
            properties["LightPosition"] = LightPosition;
            properties["Color"] = Color;
            properties["Intensity"] = Intensity;
            properties["Attenuation"] = Attenuation;
        }

        public void DeserializeProperties(IReadOnlyDictionary<string, object?> properties)
        {
            // 新形式
            if (properties.TryGetValue("LightPosition", out var lightPos))
                LightPosition = SerializationHelpers.ConvertToVector3(lightPos);
            // 後方互換性: 旧形式のPosition
            else if (properties.TryGetValue("Position", out var oldLightPos))
                LightPosition = SerializationHelpers.ConvertToVector3(oldLightPos);

            if (properties.TryGetValue("Color", out var pointLightColor))
                Color = SerializationHelpers.ConvertToVector4(pointLightColor);
            if (properties.TryGetValue("Intensity", out var pointIntensity))
                Intensity = Convert.ToSingle(pointIntensity);
            if (properties.TryGetValue("Attenuation", out var attenuation))
                Attenuation = Convert.ToSingle(attenuation);
        }
        #endregion
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using RayTraceVS.WPF.Models.Data;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// アンビエントライトノード（環境光）
    /// シーン全体を均一に照らす光源
    /// </summary>
    public partial class AmbientLightNode : Node
    {
        private Vector4 _color = new Vector4(0.2f, 0.2f, 0.2f, 1.0f);
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

        public AmbientLightNode() : base("Ambient Light", NodeCategory.Light)
        {
            AddInputSocket("Color", SocketType.Color);
            AddInputSocket("Intensity", SocketType.Float);
            AddOutputSocket("Light", SocketType.Light);
        }

        public override object? Evaluate(Dictionary<Guid, object?> inputValues)
        {
            var colorInput = GetInputValue<Vector4?>("Color", inputValues);
            var intensityInput = GetInputValue<float?>("Intensity", inputValues);
            
            var color = colorInput ?? Color;
            var intensity = intensityInput ?? Intensity;

            return new LightData
            {
                Type = LightType.Ambient,
                Position = Vector3.Zero,
                Direction = Vector3.Zero,
                Color = color,
                Intensity = intensity,
                Attenuation = 0.0f,
                Radius = 0.0f,              // Ambientライトはシャドウなし
                SoftShadowSamples = 1.0f
            };
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Numerics;
using RayTraceVS.WPF.Models.Data;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// ディレクショナルライトノード（並行光源）
    /// 太陽光のように、無限遠から平行に照らす光源
    /// ソフトシャドウ用のAngular Radiusを設定可能
    /// </summary>
    public partial class DirectionalLightNode : Node
    {
        public Vector3 Direction { get; set; } = new Vector3(0, -1, 0);
        public Vector4 Color { get; set; } = Vector4.One;
        public float Intensity { get; set; } = 1.0f;
        
        private float _angularRadius = 0.0f;     // 0 = ハードシャドウ、>0 = ソフトシャドウ（太陽なら約0.01）
        public float AngularRadius
        {
            get => _angularRadius;
            set
            {
                if (SetProperty(ref _angularRadius, value))
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

        public DirectionalLightNode() : base("Directional Light", NodeCategory.Light)
        {
            AddInputSocket("Direction", SocketType.Vector3);
            AddInputSocket("Color", SocketType.Color);
            AddInputSocket("Intensity", SocketType.Float);
            AddInputSocket("Angular Radius", SocketType.Float); // 角度半径（ラジアン）
            AddInputSocket("Shadow Samples", SocketType.Float); // サンプル数
            AddOutputSocket("Light", SocketType.Light);
        }

        public override object? Evaluate(Dictionary<Guid, object?> inputValues)
        {
            var directionInput = GetInputValue<Vector3?>("Direction", inputValues);
            var colorInput = GetInputValue<Vector4?>("Color", inputValues);
            var intensityInput = GetInputValue<float?>("Intensity", inputValues);
            var angularRadiusInput = GetInputValue<float?>("Angular Radius", inputValues);
            var samplesInput = GetInputValue<float?>("Shadow Samples", inputValues);
            
            var direction = directionInput ?? Direction;
            var color = colorInput ?? Color;
            var intensity = intensityInput ?? Intensity;
            var angularRadiusValue = angularRadiusInput ?? AngularRadius;
            var samples = samplesInput ?? SoftShadowSamples;

            return new LightData
            {
                Type = LightType.Directional,
                Position = Vector3.Zero,
                Direction = Vector3.Normalize(direction),
                Color = color,
                Intensity = intensity,
                Attenuation = 0.0f,
                Radius = angularRadiusValue,  // DirectionalライトではRadiusを角度半径として使用
                SoftShadowSamples = System.Math.Clamp(samples, 1.0f, 16.0f)
            };
        }
    }
}

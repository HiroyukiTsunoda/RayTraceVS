using System;
using System.Collections.Generic;
using System.Numerics;
using RayTraceVS.WPF.Models.Data;
using RayTraceVS.WPF.Models.Serialization;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// 発光（Emission）マテリアルノード
    /// 光源として機能するマテリアル
    /// </summary>
    public partial class EmissionMaterialNode : Node, ISerializableNode
    {
        private Vector4 _emissionColor = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
        private float _strength = 1.0f;
        private Vector4 _baseColor = new Vector4(0.0f, 0.0f, 0.0f, 1.0f);

        /// <summary>
        /// 発光色
        /// </summary>
        public Vector4 EmissionColor
        {
            get => _emissionColor;
            set
            {
                if (_emissionColor != value)
                {
                    _emissionColor = value;
                    OnPropertyChanged(nameof(EmissionColor));
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// 発光強度（0以上の値、1.0が標準）
        /// </summary>
        public float Strength
        {
            get => _strength;
            set
            {
                if (SetProperty(ref _strength, value))
                {
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// ベースカラー（発光していない部分の色）
        /// </summary>
        public Vector4 BaseColor
        {
            get => _baseColor;
            set
            {
                if (_baseColor != value)
                {
                    _baseColor = value;
                    OnPropertyChanged(nameof(BaseColor));
                    MarkDirty();
                }
            }
        }

        public EmissionMaterialNode() : base("Emission", NodeCategory.Material)
        {
            // 入力ソケット
            AddInputSocket("Emission Color", SocketType.Color);
            AddInputSocket("Strength", SocketType.Float);
            AddInputSocket("Base Color", SocketType.Color);

            // 出力ソケット
            AddOutputSocket("Material", SocketType.Material);
        }

        public override object? Evaluate(Dictionary<Guid, object?> inputValues)
        {
            // 入力値を取得
            var emissionColorInput = GetInputValue<Vector4?>("Emission Color", inputValues);
            var emissionColor = emissionColorInput ?? EmissionColor;

            var strength = GetInputValue<float?>("Strength", inputValues) ?? Strength;
            strength = Math.Max(strength, 0.0f);

            var baseColorInput = GetInputValue<Vector4?>("Base Color", inputValues);
            var baseColor = baseColorInput ?? BaseColor;

            // 発光色に強度を適用
            var emission = new Vector4(
                emissionColor.X * strength,
                emissionColor.Y * strength,
                emissionColor.Z * strength,
                emissionColor.W
            );

            return new MaterialData
            {
                BaseColor = baseColor,
                Metallic = 0.0f,
                Roughness = 1.0f,
                Transmission = 0.0f,
                IOR = 1.5f,
                Emission = emission,
                Specular = 0.5f,
                Absorption = Vector3.Zero
            };
        }

        #region ISerializableNode
        public void SerializeProperties(IDictionary<string, object?> properties)
        {
            properties["EmissionColor"] = EmissionColor;
            properties["Strength"] = Strength;
            properties["BaseColor"] = BaseColor;
        }

        public void DeserializeProperties(IReadOnlyDictionary<string, object?> properties)
        {
            if (properties.TryGetValue("EmissionColor", out var emissionColor))
                EmissionColor = SerializationHelpers.ConvertToVector4(emissionColor);
            if (properties.TryGetValue("Strength", out var strength))
                Strength = Convert.ToSingle(strength);
            if (properties.TryGetValue("BaseColor", out var emissionBaseColor))
                BaseColor = SerializationHelpers.ConvertToVector4(emissionBaseColor);
        }
        #endregion
    }
}

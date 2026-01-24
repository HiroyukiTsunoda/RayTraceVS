using System;
using System.Collections.Generic;
using System.Numerics;
using RayTraceVS.WPF.Models.Data;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// 統合BSDFマテリアルノード
    /// Principled BSDFスタイルで、様々なマテリアルを1つのノードで表現
    /// </summary>
    public partial class MaterialBSDFNode : Node
    {
        private Vector4 _baseColor = new Vector4(0.8f, 0.8f, 0.8f, 1.0f);
        private float _metallic = 0.0f;
        private float _roughness = 0.5f;
        private float _transmission = 0.0f;
        private float _ior = 1.5f;
        private Vector4 _emission = Vector4.Zero;
        private Vector3 _absorption = Vector3.Zero;

        // ベースカラー（基本色）
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

        // 金属度（0.0 = 誘電体, 1.0 = 金属）
        public float Metallic
        {
            get => _metallic;
            set
            {
                if (SetProperty(ref _metallic, value))
                {
                    MarkDirty();
                }
            }
        }

        // 粗さ（0.0 = 完全鏡面, 1.0 = 完全拡散）
        public float Roughness
        {
            get => _roughness;
            set
            {
                if (SetProperty(ref _roughness, value))
                {
                    MarkDirty();
                }
            }
        }

        // 透過度（0.0 = 不透明, 1.0 = 完全透過）
        public float Transmission
        {
            get => _transmission;
            set
            {
                if (SetProperty(ref _transmission, value))
                {
                    MarkDirty();
                }
            }
        }

        // 屈折率（Index of Refraction）
        public float IOR
        {
            get => _ior;
            set
            {
                if (SetProperty(ref _ior, value))
                {
                    MarkDirty();
                }
            }
        }

        // 発光色・強度
        public Vector4 Emission
        {
            get => _emission;
            set
            {
                if (_emission != value)
                {
                    _emission = value;
                    OnPropertyChanged(nameof(Emission));
                    MarkDirty();
                }
            }
        }

        // 吸収係数（Beer-Lambert sigmaA）
        public Vector3 Absorption
        {
            get => _absorption;
            set
            {
                if (_absorption != value)
                {
                    _absorption = value;
                    OnPropertyChanged(nameof(Absorption));
                    MarkDirty();
                }
            }
        }

        public MaterialBSDFNode() : base("Material BSDF", NodeCategory.Material)
        {
            // 入力ソケット
            AddInputSocket("Base Color", SocketType.Color);
            AddInputSocket("Metallic", SocketType.Float);
            AddInputSocket("Roughness", SocketType.Float);
            AddInputSocket("Transmission", SocketType.Float);
            AddInputSocket("IOR", SocketType.Float);
            AddInputSocket("Emission", SocketType.Color);
            AddInputSocket("Absorption", SocketType.Vector3);

            // 出力ソケット
            AddOutputSocket("Material", SocketType.Material);
        }

        public override object? Evaluate(Dictionary<Guid, object?> inputValues)
        {
            // 入力値を取得（接続されていない場合はデフォルト値を使用）
            var baseColorInput = GetInputValue<Vector4?>("Base Color", inputValues);
            var baseColor = baseColorInput ?? BaseColor;
            
            var metallic = GetInputValue<float?>("Metallic", inputValues) ?? Metallic;
            var roughness = GetInputValue<float?>("Roughness", inputValues) ?? Roughness;
            var transmission = GetInputValue<float?>("Transmission", inputValues) ?? Transmission;
            var ior = GetInputValue<float?>("IOR", inputValues) ?? IOR;
            
            var emissionInput = GetInputValue<Vector4?>("Emission", inputValues);
            var emission = emissionInput ?? Emission;
            
            var absorptionInput = GetInputValue<Vector3?>("Absorption", inputValues);
            var absorption = absorptionInput ?? Absorption;

            // 値の範囲を制限
            metallic = Math.Clamp(metallic, 0.0f, 1.0f);
            roughness = Math.Clamp(roughness, 0.0f, 1.0f);
            transmission = Math.Clamp(transmission, 0.0f, 1.0f);
            ior = Math.Max(ior, 1.0f);
            absorption = new Vector3(
                Math.Max(0.0f, absorption.X),
                Math.Max(0.0f, absorption.Y),
                Math.Max(0.0f, absorption.Z));

            return new MaterialData
            {
                BaseColor = baseColor,
                Metallic = metallic,
                Roughness = roughness,
                Transmission = transmission,
                IOR = ior,
                Emission = emission,
                Absorption = absorption
            };
        }
    }
}

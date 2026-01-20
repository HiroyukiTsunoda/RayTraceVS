using System;
using System.Collections.Generic;
using System.Numerics;
using RayTraceVS.WPF.Models.Data;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// Universal PBR マテリアルノード
    /// 標準的なMetallic-Roughnessワークフローに基づくPBRマテリアル
    /// </summary>
    public class UniversalPBRNode : Node
    {
        private Vector4 _baseColor = new Vector4(0.8f, 0.8f, 0.8f, 1.0f);
        private float _metallic = 0.0f;
        private float _roughness = 0.5f;
        private Vector3 _emissive = Vector3.Zero;

        /// <summary>
        /// ベースカラー（アルベド）
        /// 非金属: 拡散反射色
        /// 金属: F0（反射色）
        /// </summary>
        public Vector4 BaseColor
        {
            get => _baseColor;
            set { if (_baseColor != value) { _baseColor = value; OnPropertyChanged(); MarkDirty(); } }
        }

        /// <summary>
        /// メタリック（金属度）
        /// 0.0 = 誘電体（非金属）、F0 = 0.04
        /// 1.0 = 金属、F0 = BaseColor
        /// </summary>
        public float Metallic
        {
            get => _metallic;
            set { if (_metallic != value) { _metallic = value; OnPropertyChanged(); MarkDirty(); } }
        }

        /// <summary>
        /// ラフネス（粗さ）
        /// 0.0 = 完全鏡面（ツルツル）
        /// 1.0 = 完全拡散（ザラザラ）
        /// </summary>
        public float Roughness
        {
            get => _roughness;
            set { if (_roughness != value) { _roughness = value; OnPropertyChanged(); MarkDirty(); } }
        }

        /// <summary>
        /// エミッシブ（発光色）
        /// 自己発光する色、ライティングに影響されない
        /// </summary>
        public Vector3 Emissive
        {
            get => _emissive;
            set { if (_emissive != value) { _emissive = value; OnPropertyChanged(); MarkDirty(); } }
        }

        public UniversalPBRNode() : base("Universal PBR", NodeCategory.Material)
        {
            // 入力ソケット
            AddInputSocket("Base Color", SocketType.Color);
            AddInputSocket("Metallic", SocketType.Float);
            AddInputSocket("Roughness", SocketType.Float);
            AddInputSocket("Emissive", SocketType.Vector3);

            // 出力ソケット
            AddOutputSocket("Material", SocketType.Material);
        }

        public override object? Evaluate(Dictionary<Guid, object?> inputValues)
        {
            // 入力値を取得
            var baseColorInput = GetInputValue<Vector4?>("Base Color", inputValues);
            var baseColor = baseColorInput ?? BaseColor;

            var metallic = GetInputValue<float?>("Metallic", inputValues) ?? Metallic;
            metallic = Math.Clamp(metallic, 0.0f, 1.0f);

            var roughness = GetInputValue<float?>("Roughness", inputValues) ?? Roughness;
            roughness = Math.Clamp(roughness, 0.0f, 1.0f);

            var emissive = GetInputValue<Vector3?>("Emissive", inputValues) ?? Emissive;

            return new MaterialData
            {
                BaseColor = baseColor,
                Metallic = metallic,
                Roughness = roughness,
                Transmission = 0.0f,       // 不透明（Phase 2で拡張予定）
                IOR = 1.5f,
                Emission = new Vector4(emissive.X, emissive.Y, emissive.Z, 1.0f),
                Specular = 0.5f            // Universal PBRではF0が暗黙計算されるため使用しない
            };
        }
    }
}

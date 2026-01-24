using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RayTraceVS.WPF.Models.Data;
using RayTraceVS.WPF.Models.Serialization;

namespace RayTraceVS.WPF.Models.Nodes
{
    public partial class SphereNode : Node, ISerializableNode
    {
        private Transform _objectTransform = Transform.Identity;
        private float _radius = 1.0f;

        // Transform（位置・回転・スケール）
        public Transform ObjectTransform
        {
            get => _objectTransform;
            set
            {
                if (!_objectTransform.Equals(value))
                {
                    _objectTransform = value;
                    OnPropertyChanged(nameof(ObjectTransform));
                    MarkDirty();
                }
            }
        }
        
        // 形状固有パラメータ
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

        public SphereNode() : base("Sphere", NodeCategory.Object)
        {
            // 入力ソケット
            AddInputSocket("Transform", SocketType.Transform);
            AddInputSocket("Material", SocketType.Material);
            AddInputSocket("Radius", SocketType.Float);
            
            // 出力ソケット
            AddOutputSocket("Object", SocketType.Object);
        }

        public override object? Evaluate(Dictionary<Guid, object?> inputValues)
        {
            // Transform入力を取得（未接続の場合は内部プロパティを使用）
            var transformSocket = InputSockets.FirstOrDefault(s => s.Name == "Transform");
            Transform transform = ObjectTransform;
            if (transformSocket != null && inputValues.TryGetValue(transformSocket.Id, out var transformVal) && transformVal is Transform inputTransform)
            {
                transform = inputTransform;
            }

            // マテリアル入力を取得（未接続の場合はデフォルト）
            var material = GetInputValue<MaterialData?>("Material", inputValues) ?? MaterialData.Default;
            
            // 半径入力を取得
            var radiusInput = GetInputValue<float?>("Radius", inputValues);
            var radius = radiusInput ?? Radius;
            
            // スケールを考慮した半径を計算
            var scaledRadius = radius * Math.Max(Math.Max(transform.Scale.X, transform.Scale.Y), transform.Scale.Z);

            return new SphereData
            {
                Position = transform.Position,
                Radius = scaledRadius,
                Material = material
            };
        }

        #region ISerializableNode
        public void SerializeProperties(JObject json)
        {
            json["transform"] = ObjectTransform.ToJson();
            json["radius"] = Radius;
        }

        public void DeserializeProperties(JObject json)
        {
            ObjectTransform = json["transform"].ToTransform();
            Radius = json.GetFloat("radius", 1.0f);
        }
        #endregion
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json.Linq;
using RayTraceVS.WPF.Models.Data;
using RayTraceVS.WPF.Models.Serialization;

namespace RayTraceVS.WPF.Models.Nodes
{
    public partial class PlaneNode : Node, ISerializableNode
    {
        private Transform _objectTransform = Transform.Identity;
        private Vector3 _normal = Vector3.UnitY;

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
        public Vector3 Normal
        {
            get => _normal;
            set
            {
                if (_normal != value)
                {
                    _normal = value;
                    OnPropertyChanged(nameof(Normal));
                    MarkDirty();
                }
            }
        }

        public PlaneNode() : base("Plane", NodeCategory.Object)
        {
            // 入力ソケット
            AddInputSocket("Transform", SocketType.Transform);
            AddInputSocket("Material", SocketType.Material);
            AddInputSocket("Normal", SocketType.Vector3);
            
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
            
            // 法線入力を取得
            var normalInput = GetInputValue<Vector3?>("Normal", inputValues);
            var normal = normalInput ?? Normal;
            
            // 回転を法線に適用
            var rotatedNormal = Vector3.Transform(normal, transform.Rotation);

            return new PlaneData
            {
                Position = transform.Position,
                Normal = Vector3.Normalize(rotatedNormal),
                Material = material
            };
        }

        #region ISerializableNode
        public void SerializeProperties(JObject json)
        {
            json["transform"] = ObjectTransform.ToJson();
            json["normal"] = Normal.ToJson();
        }

        public void DeserializeProperties(JObject json)
        {
            ObjectTransform = json["transform"].ToTransform();
            Normal = json["normal"].ToVector3(Vector3.UnitY);
        }
        #endregion
    }
}

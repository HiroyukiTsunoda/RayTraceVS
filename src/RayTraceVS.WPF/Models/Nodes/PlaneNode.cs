using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    public class PlaneNode : Node
    {
        // Transform（位置・回転・スケール）
        public Transform ObjectTransform { get; set; } = Transform.Identity;
        
        // 形状固有パラメータ
        public Vector3 Normal { get; set; } = Vector3.UnitY;

        public PlaneNode() : base("Plane", NodeCategory.Object)
        {
            // 入力ソケット
            AddInputSocket("Transform", SocketType.Transform);
            AddInputSocket("Material", SocketType.Material);
            AddInputSocket("Normal", SocketType.Vector3);
            
            // 出力ソケット
            AddOutputSocket("Object", SocketType.Object);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
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
    }

    public struct PlaneData
    {
        public Vector3 Position;
        public Vector3 Normal;
        public MaterialData Material;
    }
}

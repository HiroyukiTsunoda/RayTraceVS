using System.Collections.Generic;
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
            AddInputSocket("Material", SocketType.Material);
            AddInputSocket("Normal", SocketType.Vector3);
            
            // 出力ソケット
            AddOutputSocket("Object", SocketType.Object);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            // マテリアル入力を取得（未接続の場合はデフォルト）
            var material = GetInputValue<MaterialData?>("Material", inputValues) ?? MaterialData.Default;
            
            // 法線入力を取得
            var normalInput = GetInputValue<Vector3?>("Normal", inputValues);
            var normal = normalInput ?? Normal;
            
            // 回転を法線に適用
            var rotationQuat = ObjectTransform.GetQuaternion();
            var rotatedNormal = Vector3.Transform(normal, rotationQuat);

            return new PlaneData
            {
                Position = ObjectTransform.Position,
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

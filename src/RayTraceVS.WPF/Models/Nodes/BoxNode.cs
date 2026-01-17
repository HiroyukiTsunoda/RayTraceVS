using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    public class BoxNode : Node
    {
        // Transform（位置・回転・スケール）
        public Transform ObjectTransform { get; set; } = Transform.Identity;
        
        // 形状固有パラメータ
        public Vector3 Size { get; set; } = new Vector3(1.0f, 1.0f, 1.0f);  // full size (not half-extents)

        public BoxNode() : base("Box", NodeCategory.Object)
        {
            // 入力ソケット
            AddInputSocket("Transform", SocketType.Transform);
            AddInputSocket("Material", SocketType.Material);
            AddInputSocket("Size", SocketType.Vector3);
            
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
            System.Diagnostics.Debug.WriteLine($"[BoxNode] Material: Transmission={material.Transmission}, Metallic={material.Metallic}, IOR={material.IOR}");
            
            // 形状パラメータ入力を取得
            var sizeInput = GetInputValue<Vector3?>("Size", inputValues);
            
            var size = sizeInput ?? Size;
            
            // スケールをサイズに適用し、half-extents に変換
            var scaledSize = new Vector3(
                size.X * transform.Scale.X * 0.5f,
                size.Y * transform.Scale.Y * 0.5f,
                size.Z * transform.Scale.Z * 0.5f
            );

            return new BoxData
            {
                Center = transform.Position,
                Size = scaledSize,  // half-extents
                Material = material
            };
        }
    }

    public struct BoxData
    {
        public Vector3 Center;
        public Vector3 Size;  // half-extents
        public MaterialData Material;
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    public class SphereNode : Node
    {
        // Transform（位置・回転・スケール）
        public Transform ObjectTransform { get; set; } = Transform.Identity;
        
        // 形状固有パラメータ
        public float Radius { get; set; } = 1.0f;

        public SphereNode() : base("Sphere", NodeCategory.Object)
        {
            // 入力ソケット
            AddInputSocket("Material", SocketType.Material);
            AddInputSocket("Radius", SocketType.Float);
            
            // 出力ソケット
            AddOutputSocket("Object", SocketType.Object);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            // マテリアル入力を取得（未接続の場合はデフォルト）
            var material = GetInputValue<MaterialData?>("Material", inputValues) ?? MaterialData.Default;
            
            // 半径入力を取得
            var radiusInput = GetInputValue<float?>("Radius", inputValues);
            var radius = radiusInput ?? Radius;
            
            // スケールを考慮した半径を計算
            var scaledRadius = radius * Math.Max(Math.Max(ObjectTransform.Scale.X, ObjectTransform.Scale.Y), ObjectTransform.Scale.Z);

            return new SphereData
            {
                Position = ObjectTransform.Position,
                Radius = scaledRadius,
                Material = material
            };
        }
    }

    public struct SphereData
    {
        public Vector3 Position;
        public float Radius;
        public MaterialData Material;
    }
}

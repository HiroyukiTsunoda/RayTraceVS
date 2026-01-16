using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    public class CylinderNode : Node
    {
        // Transform（位置・回転・スケール）
        public Transform ObjectTransform { get; set; } = Transform.Identity;
        
        // 形状固有パラメータ
        public Vector3 Axis { get; set; } = Vector3.UnitY;
        public float Radius { get; set; } = 0.5f;
        public float Height { get; set; } = 2.0f;

        public CylinderNode() : base("Cylinder", NodeCategory.Object)
        {
            // 入力ソケット
            AddInputSocket("Material", SocketType.Material);
            AddInputSocket("Axis", SocketType.Vector3);
            AddInputSocket("Radius", SocketType.Float);
            AddInputSocket("Height", SocketType.Float);
            
            // 出力ソケット
            AddOutputSocket("Object", SocketType.Object);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            // マテリアル入力を取得（未接続の場合はデフォルト）
            var material = GetInputValue<MaterialData?>("Material", inputValues) ?? MaterialData.Default;
            
            // 形状パラメータ入力を取得
            var axisInput = GetInputValue<Vector3?>("Axis", inputValues);
            var radiusInput = GetInputValue<float?>("Radius", inputValues);
            var heightInput = GetInputValue<float?>("Height", inputValues);
            
            var axis = axisInput ?? Axis;
            var radius = radiusInput ?? Radius;
            var height = heightInput ?? Height;
            
            // 回転を軸に適用
            var rotationQuat = ObjectTransform.GetQuaternion();
            var rotatedAxis = Vector3.Transform(axis, rotationQuat);
            
            // スケールを半径と高さに適用
            var avgScale = (ObjectTransform.Scale.X + ObjectTransform.Scale.Z) / 2.0f;
            var scaledRadius = radius * avgScale;
            var scaledHeight = height * ObjectTransform.Scale.Y;

            return new CylinderData
            {
                Position = ObjectTransform.Position,
                Axis = Vector3.Normalize(rotatedAxis),
                Radius = scaledRadius,
                Height = scaledHeight,
                Material = material
            };
        }
    }

    public struct CylinderData
    {
        public Vector3 Position;
        public Vector3 Axis;
        public float Radius;
        public float Height;
        public MaterialData Material;
    }
}

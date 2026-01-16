using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// 減算ノード（引き算）
    /// Float同士、Vector3同士の減算に対応
    /// </summary>
    public class SubNode : Node
    {
        public SubNode() : base("Sub", NodeCategory.Math)
        {
            AddInputSocket("A", SocketType.Float);
            AddInputSocket("B", SocketType.Float);
            AddOutputSocket("Result", SocketType.Float);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            var aInput = GetInputValue<object>("A", inputValues);
            var bInput = GetInputValue<object>("B", inputValues);

            // 両方がVector3の場合
            if (aInput is Vector3 vecA && bInput is Vector3 vecB)
            {
                return vecA - vecB;
            }

            // 少なくとも一方がFloatの場合
            float a = aInput switch
            {
                float f => f,
                Vector3 v => v.X, // Vector3の場合はX成分を使用
                _ => 0.0f
            };

            float b = bInput switch
            {
                float f => f,
                Vector3 v => v.X,
                _ => 0.0f
            };

            return a - b;
        }
    }
}

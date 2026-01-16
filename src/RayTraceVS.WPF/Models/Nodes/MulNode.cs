using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// 乗算ノード（掛け算）
    /// Float同士、Vector3とFloatの乗算に対応
    /// </summary>
    public class MulNode : Node
    {
        public MulNode() : base("Mul", NodeCategory.Math)
        {
            AddInputSocket("A", SocketType.Float);
            AddInputSocket("B", SocketType.Float);
            AddOutputSocket("Result", SocketType.Float);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            var aInput = GetInputValue<object>("A", inputValues);
            var bInput = GetInputValue<object>("B", inputValues);

            // Vector3とFloatの乗算
            if (aInput is Vector3 vecA && bInput is float scalarB)
            {
                return vecA * scalarB;
            }
            if (aInput is float scalarA && bInput is Vector3 vecB)
            {
                return scalarA * vecB;
            }

            // 両方がVector3の場合（成分ごとの乗算）
            if (aInput is Vector3 vecA2 && bInput is Vector3 vecB2)
            {
                return vecA2 * vecB2;
            }

            // Float同士の乗算
            float a = aInput switch
            {
                float f => f,
                Vector3 v => v.X,
                _ => 1.0f
            };

            float b = bInput switch
            {
                float f => f,
                Vector3 v => v.X,
                _ => 1.0f
            };

            return a * b;
        }
    }
}

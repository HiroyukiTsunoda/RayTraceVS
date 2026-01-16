using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// 除算ノード（割り算）
    /// Float同士、Vector3とFloatの除算に対応
    /// </summary>
    public class DivNode : Node
    {
        public DivNode() : base("Div", NodeCategory.Math)
        {
            AddInputSocket("A", SocketType.Float);
            AddInputSocket("B", SocketType.Float);
            AddOutputSocket("Result", SocketType.Float);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            var aInput = GetInputValue<object>("A", inputValues);
            var bInput = GetInputValue<object>("B", inputValues);

            // Vector3とFloatの除算
            if (aInput is Vector3 vecA && bInput is float scalarB)
            {
                if (scalarB == 0.0f) return vecA; // ゼロ除算回避
                return vecA / scalarB;
            }

            // 両方がVector3の場合（成分ごとの除算）
            if (aInput is Vector3 vecA2 && bInput is Vector3 vecB2)
            {
                return new Vector3(
                    vecB2.X != 0 ? vecA2.X / vecB2.X : vecA2.X,
                    vecB2.Y != 0 ? vecA2.Y / vecB2.Y : vecA2.Y,
                    vecB2.Z != 0 ? vecA2.Z / vecB2.Z : vecA2.Z
                );
            }

            // Float同士の除算
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

            if (b == 0.0f) return a; // ゼロ除算回避
            return a / b;
        }
    }
}

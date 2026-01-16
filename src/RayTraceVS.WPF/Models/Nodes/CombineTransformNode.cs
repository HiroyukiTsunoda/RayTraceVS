using System.Collections.Generic;
using System.Linq;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// 親子関係の階層化を実現するノード
    /// UE5の MultiplyUsingMatrixWithScale 相当の合成を行う
    /// </summary>
    public class CombineTransformNode : Node
    {
        public CombineTransformNode() : base("Combine Transform", NodeCategory.Math)
        {
            // 入力ソケット
            AddInputSocket("Parent", SocketType.Transform);
            AddInputSocket("Local", SocketType.Transform);

            // 出力ソケット
            AddOutputSocket("Combined", SocketType.Transform);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            // 入力ソケットから値を取得
            var parentSocket = InputSockets.FirstOrDefault(s => s.Name == "Parent");
            var localSocket = InputSockets.FirstOrDefault(s => s.Name == "Local");

            Transform parent = Transform.Identity;
            Transform local = Transform.Identity;

            // Parent Transform入力
            if (parentSocket != null && inputValues.TryGetValue(parentSocket.Id, out var parentVal) && parentVal is Transform parentTransform)
            {
                parent = parentTransform;
            }

            // Local Transform入力
            if (localSocket != null && inputValues.TryGetValue(localSocket.Id, out var localVal) && localVal is Transform localTransform)
            {
                local = localTransform;
            }

            // 行列で合成（非一様スケール対応）
            // LocalTransform を ParentTransform の座標系で適用
            return local.Combine(parent);
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    public partial class Vector4Node : Node
    {
        [ObservableProperty]
        private float _x = 1.0f;

        [ObservableProperty]
        private float _y = 1.0f;

        [ObservableProperty]
        private float _z = 1.0f;

        [ObservableProperty]
        private float _w = 1.0f;

        public override bool HasEditableVector4Inputs => true;

        public Vector4Node() : base("Vector4", NodeCategory.Math)
        {
            AddInputSocket("X", SocketType.Float);
            AddInputSocket("Y", SocketType.Float);
            AddInputSocket("Z", SocketType.Float);
            AddInputSocket("W", SocketType.Float);
            AddOutputSocket("Vector", SocketType.Color);  // Vector4はColorタイプとして出力
        }

        /// <summary>
        /// 入力ソケットに対応するプロパティ値を取得
        /// </summary>
        public float GetSocketValue(string socketName)
        {
            return socketName switch
            {
                "X" => X,
                "Y" => Y,
                "Z" => Z,
                "W" => W,
                _ => 0.0f
            };
        }

        /// <summary>
        /// 入力ソケットに対応するプロパティ値を設定
        /// </summary>
        public void SetSocketValue(string socketName, float value)
        {
            switch (socketName)
            {
                case "X": X = value; break;
                case "Y": Y = value; break;
                case "Z": Z = value; break;
                case "W": W = value; break;
            }
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            // 入力ソケットから値を取得（接続がある場合）
            var xSocket = InputSockets.FirstOrDefault(s => s.Name == "X");
            var ySocket = InputSockets.FirstOrDefault(s => s.Name == "Y");
            var zSocket = InputSockets.FirstOrDefault(s => s.Name == "Z");
            var wSocket = InputSockets.FirstOrDefault(s => s.Name == "W");

            float x = X;
            float y = Y;
            float z = Z;
            float w = W;

            // 接続されている場合は入力値を使用し、プロパティも更新
            if (xSocket != null && inputValues.TryGetValue(xSocket.Id, out var xVal) && xVal is float xFloat)
            {
                x = xFloat;
                X = xFloat; // 値を保持
            }
            if (ySocket != null && inputValues.TryGetValue(ySocket.Id, out var yVal) && yVal is float yFloat)
            {
                y = yFloat;
                Y = yFloat; // 値を保持
            }
            if (zSocket != null && inputValues.TryGetValue(zSocket.Id, out var zVal) && zVal is float zFloat)
            {
                z = zFloat;
                Z = zFloat; // 値を保持
            }
            if (wSocket != null && inputValues.TryGetValue(wSocket.Id, out var wVal) && wVal is float wFloat)
            {
                w = wFloat;
                W = wFloat; // 値を保持
            }

            return new Vector4(x, y, z, w);
        }
    }
}

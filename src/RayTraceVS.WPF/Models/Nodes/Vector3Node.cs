using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    public partial class Vector3Node : Node
    {
        [ObservableProperty]
        private float _x = 1.0f;

        [ObservableProperty]
        private float _y = 1.0f;

        [ObservableProperty]
        private float _z = 1.0f;

        public override bool HasEditableVector3Inputs => true;

        public Vector3Node() : base("Vector3", NodeCategory.Math)
        {
            AddInputSocket("X", SocketType.Float);
            AddInputSocket("Y", SocketType.Float);
            AddInputSocket("Z", SocketType.Float);
            AddOutputSocket("Vector", SocketType.Vector3);
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
            }
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            // 入力ソケットから値を取得（接続がある場合）
            var xSocket = InputSockets.FirstOrDefault(s => s.Name == "X");
            var ySocket = InputSockets.FirstOrDefault(s => s.Name == "Y");
            var zSocket = InputSockets.FirstOrDefault(s => s.Name == "Z");

            float x = X;
            float y = Y;
            float z = Z;

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

            return new Vector3(x, y, z);
        }
    }
}

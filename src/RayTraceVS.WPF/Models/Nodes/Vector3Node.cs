using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    public partial class Vector3Node : Node
    {
        private float _x = 1.0f;
        public float X
        {
            get => _x;
            set
            {
                if (SetProperty(ref _x, value))
                {
                    MarkDirty();
                }
            }
        }

        private float _y = 1.0f;
        public float Y
        {
            get => _y;
            set
            {
                if (SetProperty(ref _y, value))
                {
                    MarkDirty();
                }
            }
        }

        private float _z = 1.0f;
        public float Z
        {
            get => _z;
            set
            {
                if (SetProperty(ref _z, value))
                {
                    MarkDirty();
                }
            }
        }

        // ソケットキャッシュ（毎回FirstOrDefaultを呼ばない）
        private readonly NodeSocket _xSocket;
        private readonly NodeSocket _ySocket;
        private readonly NodeSocket _zSocket;

        public override bool HasEditableVector3Inputs => true;

        public Vector3Node() : base("Vector3", NodeCategory.Math)
        {
            _xSocket = AddInputSocket("X", SocketType.Float);
            _ySocket = AddInputSocket("Y", SocketType.Float);
            _zSocket = AddInputSocket("Z", SocketType.Float);
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
            float x = X;
            float y = Y;
            float z = Z;

            // キャッシュされたソケット参照を使用（FirstOrDefault不要）
            if (inputValues.TryGetValue(_xSocket.Id, out var xVal) && xVal is float xFloat)
            {
                x = xFloat;
                X = xFloat;
            }
            if (inputValues.TryGetValue(_ySocket.Id, out var yVal) && yVal is float yFloat)
            {
                y = yFloat;
                Y = yFloat;
            }
            if (inputValues.TryGetValue(_zSocket.Id, out var zVal) && zVal is float zFloat)
            {
                z = zFloat;
                Z = zFloat;
            }

            return new Vector3(x, y, z);
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// RGB/RGBAカラーを出力するノード
    /// </summary>
    public partial class ColorNode : Node
    {
        private float _r = 0.8f;
        public float R
        {
            get => _r;
            set
            {
                if (SetProperty(ref _r, value))
                {
                    MarkDirty();
                }
            }
        }

        private float _g = 0.8f;
        public float G
        {
            get => _g;
            set
            {
                if (SetProperty(ref _g, value))
                {
                    MarkDirty();
                }
            }
        }

        private float _b = 0.8f;
        public float B
        {
            get => _b;
            set
            {
                if (SetProperty(ref _b, value))
                {
                    MarkDirty();
                }
            }
        }

        private float _a = 1.0f;
        public float A
        {
            get => _a;
            set
            {
                if (SetProperty(ref _a, value))
                {
                    MarkDirty();
                }
            }
        }

        public override bool HasEditableColorInputs => true;

        /// <summary>
        /// プロパティパネルでの編集用にカラー値を取得/設定
        /// </summary>
        public Vector4 Color
        {
            get => new Vector4(R, G, B, A);
            set
            {
                R = value.X;
                G = value.Y;
                B = value.Z;
                A = value.W;
            }
        }

        public ColorNode() : base("Color", NodeCategory.Material)
        {
            // 入力ソケット（R, G, B, Aを個別に入力可能）
            AddInputSocket("R", SocketType.Float);
            AddInputSocket("G", SocketType.Float);
            AddInputSocket("B", SocketType.Float);
            AddInputSocket("A", SocketType.Float);

            // 出力ソケット
            AddOutputSocket("Color", SocketType.Color);
        }

        /// <summary>
        /// 入力ソケットに対応するプロパティ値を取得
        /// </summary>
        public float GetSocketValue(string socketName)
        {
            return socketName switch
            {
                "R" => R,
                "G" => G,
                "B" => B,
                "A" => A,
                _ => 0.0f
            };
        }

        /// <summary>
        /// 入力ソケットに対応するプロパティ値を設定
        /// </summary>
        public void SetSocketValue(string socketName, float value)
        {
            // 0-1の範囲にクランプ
            value = Math.Clamp(value, 0f, 1f);
            
            switch (socketName)
            {
                case "R": R = value; break;
                case "G": G = value; break;
                case "B": B = value; break;
                case "A": A = value; break;
            }
        }

        public override object? Evaluate(Dictionary<Guid, object?> inputValues)
        {
            // 入力ソケットから値を取得（接続がある場合）
            var rSocket = InputSockets.FirstOrDefault(s => s.Name == "R");
            var gSocket = InputSockets.FirstOrDefault(s => s.Name == "G");
            var bSocket = InputSockets.FirstOrDefault(s => s.Name == "B");
            var aSocket = InputSockets.FirstOrDefault(s => s.Name == "A");

            float r = R;
            float g = G;
            float b = B;
            float a = A;

            // 接続されている場合は入力値を使用し、プロパティも更新
            if (rSocket != null && inputValues.TryGetValue(rSocket.Id, out var rVal) && rVal is float rFloat)
            {
                r = Math.Clamp(rFloat, 0f, 1f);
                R = r; // 値を保持
            }
            if (gSocket != null && inputValues.TryGetValue(gSocket.Id, out var gVal) && gVal is float gFloat)
            {
                g = Math.Clamp(gFloat, 0f, 1f);
                G = g; // 値を保持
            }
            if (bSocket != null && inputValues.TryGetValue(bSocket.Id, out var bVal) && bVal is float bFloat)
            {
                b = Math.Clamp(bFloat, 0f, 1f);
                B = b; // 値を保持
            }
            if (aSocket != null && inputValues.TryGetValue(aSocket.Id, out var aVal) && aVal is float aFloat)
            {
                a = Math.Clamp(aFloat, 0f, 1f);
                A = a; // 値を保持
            }

            return new Vector4(r, g, b, a);
        }
    }
}

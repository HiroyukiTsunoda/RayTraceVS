using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// Position、Rotation（オイラー角）、Scaleの3つのVector3入力を受け取り、Transformを出力するノード
    /// </summary>
    public partial class TransformNode : Node
    {
        [ObservableProperty]
        private float _positionX = 0.0f;

        [ObservableProperty]
        private float _positionY = 0.0f;

        [ObservableProperty]
        private float _positionZ = 0.0f;

        [ObservableProperty]
        private float _rotationX = 0.0f;

        [ObservableProperty]
        private float _rotationY = 0.0f;

        [ObservableProperty]
        private float _rotationZ = 0.0f;

        [ObservableProperty]
        private float _scaleX = 1.0f;

        [ObservableProperty]
        private float _scaleY = 1.0f;

        [ObservableProperty]
        private float _scaleZ = 1.0f;

        // プロパティ変更時にMarkDirty()を呼び出す
        partial void OnPositionXChanged(float value) => MarkDirty();
        partial void OnPositionYChanged(float value) => MarkDirty();
        partial void OnPositionZChanged(float value) => MarkDirty();
        partial void OnRotationXChanged(float value) => MarkDirty();
        partial void OnRotationYChanged(float value) => MarkDirty();
        partial void OnRotationZChanged(float value) => MarkDirty();
        partial void OnScaleXChanged(float value) => MarkDirty();
        partial void OnScaleYChanged(float value) => MarkDirty();
        partial void OnScaleZChanged(float value) => MarkDirty();

        public TransformNode() : base("Transform", NodeCategory.Math)
        {
            // 入力ソケット
            AddInputSocket("Position", SocketType.Vector3);
            AddInputSocket("Rotation", SocketType.Vector3);  // オイラー角で入力
            AddInputSocket("Scale", SocketType.Vector3);

            // 出力ソケット
            AddOutputSocket("Transform", SocketType.Transform);
        }

        /// <summary>
        /// デフォルトのPosition値を取得
        /// </summary>
        public Vector3 DefaultPosition => new Vector3(PositionX, PositionY, PositionZ);

        /// <summary>
        /// デフォルトのRotation値を取得（オイラー角）
        /// </summary>
        public Vector3 DefaultRotation => new Vector3(RotationX, RotationY, RotationZ);

        /// <summary>
        /// デフォルトのScale値を取得
        /// </summary>
        public Vector3 DefaultScale => new Vector3(ScaleX, ScaleY, ScaleZ);

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            // 入力ソケットから値を取得（接続がある場合は接続値、なければデフォルト値）
            var posSocket = InputSockets.FirstOrDefault(s => s.Name == "Position");
            var rotSocket = InputSockets.FirstOrDefault(s => s.Name == "Rotation");
            var scaleSocket = InputSockets.FirstOrDefault(s => s.Name == "Scale");

            Vector3 position = DefaultPosition;
            Vector3 rotation = DefaultRotation;
            Vector3 scale = DefaultScale;

            // Position入力
            if (posSocket != null && inputValues.TryGetValue(posSocket.Id, out var posVal) && posVal is Vector3 posVec)
            {
                position = posVec;
            }

            // Rotation入力（オイラー角）
            if (rotSocket != null && inputValues.TryGetValue(rotSocket.Id, out var rotVal) && rotVal is Vector3 rotVec)
            {
                rotation = rotVec;
            }

            // Scale入力
            if (scaleSocket != null && inputValues.TryGetValue(scaleSocket.Id, out var scaleVal) && scaleVal is Vector3 scaleVec)
            {
                scale = scaleVec;
            }

            // Transformを作成して返す
            var transform = new Transform
            {
                Position = position,
                Scale = scale
            };
            // EulerAnglesプロパティを使用してRotationを設定
            transform.EulerAngles = rotation;

            return transform;
        }
    }
}

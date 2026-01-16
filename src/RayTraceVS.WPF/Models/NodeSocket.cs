using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Windows;
using System.Windows.Media;

namespace RayTraceVS.WPF.Models
{
    public enum SocketType
    {
        Object,      // オブジェクト（球、平面など）
        Vector3,     // 3Dベクトル
        Float,       // 浮動小数点数
        Color,       // 色
        Material,    // マテリアル
        Camera,      // カメラ
        Light,       // ライト
        Scene,       // シーン
        Transform    // トランスフォーム
    }

    public partial class NodeSocket : ObservableObject
    {
        [ObservableProperty]
        private Guid id;

        [ObservableProperty]
        private string name = string.Empty;

        [ObservableProperty]
        private SocketType socketType;

        [ObservableProperty]
        private bool isInput;

        [ObservableProperty]
        private Node? parentNode;

        [ObservableProperty]
        private Point position;

        [ObservableProperty]
        private object? value;

        /// <summary>
        /// ソケットが接続されているかどうか
        /// </summary>
        [ObservableProperty]
        private bool isConnected;

        /// <summary>
        /// 接続から受け取った値（表示用）
        /// </summary>
        [ObservableProperty]
        private float connectedValue;

        // 位置が変更されたときに発生するイベント
        public event EventHandler? PositionChanged;

        partial void OnPositionChanged(Point value)
        {
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }

        // ソケットの型に応じた色を返す
        public Brush SocketColor => socketType switch
        {
            SocketType.Object => new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2)),    // 青
            SocketType.Vector3 => new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C)),   // 赤
            SocketType.Float => new SolidColorBrush(Color.FromRgb(0x95, 0xA5, 0xA6)),     // グレー
            SocketType.Color => new SolidColorBrush(Color.FromRgb(0xF3, 0x9C, 0x12)),     // オレンジ
            SocketType.Material => new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)),  // 濃いオレンジ
            SocketType.Camera => new SolidColorBrush(Color.FromRgb(0x9B, 0x59, 0xB6)),    // 紫
            SocketType.Light => new SolidColorBrush(Color.FromRgb(0xF1, 0xC4, 0x0F)),     // 黄色
            SocketType.Scene => new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71)),     // 緑
            SocketType.Transform => new SolidColorBrush(Color.FromRgb(0x1A, 0xBC, 0x9C)),  // 青緑
            _ => new SolidColorBrush(Colors.White)
        };

        public NodeSocket(string name, SocketType type, bool isInput)
        {
            id = Guid.NewGuid();
            this.name = name;
            socketType = type;
            this.isInput = isInput;
        }
    }
}

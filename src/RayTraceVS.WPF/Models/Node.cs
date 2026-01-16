using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace RayTraceVS.WPF.Models
{
    public enum NodeCategory
    {
        Object,
        Material,
        Math,
        Camera,
        Light,
        Scene
    }

    public abstract partial class Node : ObservableObject
    {
        [ObservableProperty]
        private Guid id;

        [ObservableProperty]
        private string title = string.Empty;

        [ObservableProperty]
        private Point position;

        [ObservableProperty]
        private ObservableCollection<NodeSocket> inputSockets;

        [ObservableProperty]
        private ObservableCollection<NodeSocket> outputSockets;

        [ObservableProperty]
        private NodeCategory category;

        [ObservableProperty]
        private bool isSelected;

        /// <summary>
        /// ノードが「汚れている」（再評価が必要）かどうか
        /// </summary>
        [ObservableProperty]
        private bool isDirty = true;

        /// <summary>
        /// 前回の評価結果のキャッシュ
        /// </summary>
        public object? CachedResult { get; private set; }

        /// <summary>
        /// ノードがDirtyになったときに発火するイベント
        /// </summary>
        public event EventHandler? DirtyChanged;

        /// <summary>
        /// ノードをDirty状態にする
        /// </summary>
        public void MarkDirty()
        {
            if (!IsDirty)
            {
                IsDirty = true;
                DirtyChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// キャッシュ結果を設定してDirtyフラグをクリアする
        /// </summary>
        public void SetCachedResult(object? result)
        {
            CachedResult = result;
            IsDirty = false;
        }

        /// <summary>
        /// キャッシュをクリアしてDirty状態にする
        /// </summary>
        public void InvalidateCache()
        {
            CachedResult = null;
            MarkDirty();
        }

        public Brush CategoryColor => category switch
        {
            NodeCategory.Object => new SolidColorBrush(Color.FromRgb(0x2C, 0x54, 0x87)),   // 青（暗め）
            NodeCategory.Material => new SolidColorBrush(Color.FromRgb(0xB3, 0x4D, 0x4D)), // 赤（暗め）
            NodeCategory.Math => new SolidColorBrush(Color.FromRgb(0x87, 0x49, 0x26)),     // オレンジ（暗め）
            NodeCategory.Camera => new SolidColorBrush(Color.FromRgb(0x5B, 0x35, 0x6C)),   // 紫（暗め）
            NodeCategory.Light => new SolidColorBrush(Color.FromRgb(0x8F, 0x74, 0x09)),    // 黄色（暗め）
            NodeCategory.Scene => new SolidColorBrush(Color.FromRgb(0x1B, 0x7A, 0x43)),    // 緑（暗め）
            _ => new SolidColorBrush(Colors.Gray)
        };

        /// <summary>
        /// float値の直接編集が可能かどうか（FloatNodeでオーバーライド）
        /// </summary>
        public virtual bool HasEditableFloat => false;
        
        /// <summary>
        /// 編集可能なfloat値（FloatNodeでオーバーライド）
        /// </summary>
        public virtual float EditableFloatValue { get; set; }

        /// <summary>
        /// Vector3入力の直接編集が可能かどうか（Vector3Nodeでオーバーライド）
        /// </summary>
        public virtual bool HasEditableVector3Inputs => false;

        /// <summary>
        /// Vector4入力の直接編集が可能かどうか（Vector4Nodeでオーバーライド）
        /// </summary>
        public virtual bool HasEditableVector4Inputs => false;

        /// <summary>
        /// Color入力の直接編集が可能かどうか（ColorNodeでオーバーライド）
        /// </summary>
        public virtual bool HasEditableColorInputs => false;

        protected Node(string title, NodeCategory category)
        {
            id = Guid.NewGuid();
            this.title = title;
            this.category = category;
            inputSockets = new ObservableCollection<NodeSocket>();
            outputSockets = new ObservableCollection<NodeSocket>();
        }

        protected void AddInputSocket(string name, SocketType type)
        {
            var socket = new NodeSocket(name, type, true) { ParentNode = this };
            inputSockets.Add(socket);
        }

        protected void AddOutputSocket(string name, SocketType type)
        {
            var socket = new NodeSocket(name, type, false) { ParentNode = this };
            outputSockets.Add(socket);
        }

        public abstract object? Evaluate(Dictionary<Guid, object?> inputValues);

        protected T? GetInputValue<T>(string socketName, Dictionary<Guid, object?> inputValues)
        {
            foreach (var socket in inputSockets)
            {
                if (socket.Name == socketName && inputValues.TryGetValue(socket.Id, out var value))
                {
                    if (value is T typedValue)
                        return typedValue;
                }
            }
            return default;
        }
    }
}

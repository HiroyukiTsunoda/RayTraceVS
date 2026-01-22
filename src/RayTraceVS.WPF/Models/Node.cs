using CommunityToolkit.Mvvm.ComponentModel;
using RayTraceVS.WPF.Utils;
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
        /// ノードの作成順序を示すインデックス（描画順序の基準）
        /// </summary>
        [ObservableProperty]
        private int creationIndex;

        /// <summary>
        /// ノードの描画幅（概算値、実際のUIサイズは異なる可能性あり）
        /// </summary>
        public virtual double NodeWidth => 150;

        /// <summary>
        /// ノードの描画高さ（ソケット数に基づいて計算）
        /// </summary>
        public virtual double NodeHeight
        {
            get
            {
                const double headerHeight = 30;
                const double socketSpacing = 20;
                const double padding = 20;
                int maxSockets = Math.Max(InputSockets?.Count ?? 0, OutputSockets?.Count ?? 0);
                return headerHeight + (maxSockets * socketSpacing) + padding;
            }
        }

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
        /// キャッシュをクリアしてDirty状態にする（イベント発火あり）
        /// </summary>
        public void InvalidateCache()
        {
            CachedResult = null;
            MarkDirty();
        }

        /// <summary>
        /// キャッシュをクリアしてDirty状態にする（イベント発火なし）。
        /// DirtyTrackerからの一括伝播時に使用。
        /// </summary>
        public void InvalidateCacheOnly()
        {
            CachedResult = null;
            IsDirty = true;
            // DirtyChangedイベントは発火しない
        }

        public Brush CategoryColor => category switch
        {
            NodeCategory.Object => BrushCache.Get(0x40, 0x70, 0xB0),   // 青
            NodeCategory.Material => BrushCache.Get(0x40, 0xA0, 0x50), // 緑
            NodeCategory.Math => BrushCache.Get(0xA0, 0x40, 0x40),     // 赤
            NodeCategory.Camera => BrushCache.Get(0x80, 0x40, 0xA0),   // 紫
            NodeCategory.Light => BrushCache.Get(0xB0, 0xA0, 0x20),    // 黄色
            NodeCategory.Scene => BrushCache.Get(0x40, 0xA0, 0xA0),    // シアン（水色）
            _ => BrushCache.Get(Colors.Gray)
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

        /// <summary>
        /// 入力ソケットを追加し、そのソケットを返す。
        /// </summary>
        protected NodeSocket AddInputSocket(string name, SocketType type)
        {
            var socket = new NodeSocket(name, type, true) { ParentNode = this };
            inputSockets.Add(socket);
            return socket;
        }

        /// <summary>
        /// 出力ソケットを追加し、そのソケットを返す。
        /// </summary>
        protected NodeSocket AddOutputSocket(string name, SocketType type)
        {
            var socket = new NodeSocket(name, type, false) { ParentNode = this };
            outputSockets.Add(socket);
            return socket;
        }

        public abstract object? Evaluate(Dictionary<Guid, object?> inputValues);

        /// <summary>
        /// ソケット名で入力値を取得する（後方互換性のため維持）。
        /// </summary>
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

        /// <summary>
        /// キャッシュされたソケット参照で入力値を取得する（パフォーマンス向上版）。
        /// </summary>
        protected T? GetInputValue<T>(NodeSocket? socket, Dictionary<Guid, object?> inputValues)
        {
            if (socket != null && inputValues.TryGetValue(socket.Id, out var value) && value is T typedValue)
            {
                return typedValue;
            }
            return default;
        }
    }
}

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.ViewModels;

namespace RayTraceVS.WPF.Views.Handlers
{
    /// <summary>
    /// NodeEditorViewの入力状態を共有するためのクラス
    /// 各ハンドラクラス間で状態を共有するために使用
    /// </summary>
    public class EditorInputState
    {
        // マウス・ドラッグ状態
        public Point LastMousePosition { get; set; }
        public bool IsPanning { get; set; }
        public bool IsDraggingNode { get; set; }
        public bool IsDraggingConnection { get; set; }
        public bool IsRectSelecting { get; set; }
        
        // ドラッグ対象
        public Node? DraggedNode { get; set; }
        public NodeSocket? DraggedSocket { get; set; }
        public Ellipse? DraggedSocketElement { get; set; }
        public Point DragStartOffset { get; set; }
        
        // プレビュー線
        public Line? PreviewLine { get; set; }
        
        // 複数選択関連
        public HashSet<Node> SelectedNodes { get; } = new HashSet<Node>();
        public Point RectSelectStartPoint { get; set; }
        public Rectangle? SelectionRectangle { get; set; }
        public Dictionary<Node, Point> MultiDragOffsets { get; } = new Dictionary<Node, Point>();
        
        // パン・ズーム
        public TranslateTransform PanTransform { get; } = new TranslateTransform();
        public ScaleTransform ZoomTransform { get; } = new ScaleTransform();
        public TransformGroup TransformGroup { get; } = new TransformGroup();
        
        public double CurrentZoom { get; set; } = 1.0;
        public const double MinZoom = 0.1;
        public const double MaxZoom = 5.0;
        public const double ZoomSpeed = 0.001;
        
        // 接続線のPath要素管理
        public Dictionary<NodeConnection, Path> ConnectionPaths { get; } = new Dictionary<NodeConnection, Path>();
        
        // UIコンポーネント参照
        public Canvas? NodeCanvas { get; set; }
        public Canvas? ConnectionLayer { get; set; }
        
        public EditorInputState()
        {
            TransformGroup.Children.Add(ZoomTransform);
            TransformGroup.Children.Add(PanTransform);
        }
        
        /// <summary>
        /// すべてのドラッグ状態をリセット
        /// </summary>
        public void ResetDragState()
        {
            IsPanning = false;
            IsDraggingNode = false;
            IsDraggingConnection = false;
            IsRectSelecting = false;
            DraggedNode = null;
            DraggedSocket = null;
            DraggedSocketElement = null;
        }
        
        /// <summary>
        /// ViewModel取得用のデリゲート
        /// </summary>
        public System.Func<MainViewModel?>? GetViewModel { get; set; }
    }
}

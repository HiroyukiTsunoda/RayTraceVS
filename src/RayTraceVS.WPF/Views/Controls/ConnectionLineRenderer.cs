using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using RayTraceVS.WPF.Models;

namespace RayTraceVS.WPF.Views.Controls
{
    /// <summary>
    /// DrawingVisualを使用して接続線を一括描画するカスタムコントロール
    /// 多数のPath要素を1つのDrawingVisualに統合することでパフォーマンスを向上
    /// </summary>
    public class ConnectionLineRenderer : FrameworkElement
    {
        private readonly VisualCollection _visuals;
        private DrawingVisual _connectionVisual;
        
        // Penをキャッシュ（同じ色のPenを再利用）
        private readonly Dictionary<Color, Pen> _penCache = new();
        
        // 描画設定
        private const double StrokeThickness = 3.0;
        private const double StrokeOpacity = 0.9;
        
        // 現在監視中のコレクション
        private INotifyCollectionChanged? _currentCollection;

        /// <summary>
        /// 描画タイプ（中間部分、始点端部分、終点端部分、全体）
        /// </summary>
        public enum RenderType
        {
            Middle,      // 中間部分のみ
            StartEnd,    // 始点・終点の端部分
            Full         // 全体（選択ノード用）
        }

        public static readonly DependencyProperty ConnectionsProperty =
            DependencyProperty.Register(
                nameof(Connections),
                typeof(IEnumerable<NodeConnection>),
                typeof(ConnectionLineRenderer),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnConnectionsChanged));

        public static readonly DependencyProperty RenderModeProperty =
            DependencyProperty.Register(
                nameof(RenderMode),
                typeof(RenderType),
                typeof(ConnectionLineRenderer),
                new FrameworkPropertyMetadata(RenderType.Middle, FrameworkPropertyMetadataOptions.AffectsRender));

        public IEnumerable<NodeConnection>? Connections
        {
            get => (IEnumerable<NodeConnection>?)GetValue(ConnectionsProperty);
            set => SetValue(ConnectionsProperty, value);
        }

        public RenderType RenderMode
        {
            get => (RenderType)GetValue(RenderModeProperty);
            set => SetValue(RenderModeProperty, value);
        }

        public ConnectionLineRenderer()
        {
            _visuals = new VisualCollection(this);
            _connectionVisual = new DrawingVisual();
            _visuals.Add(_connectionVisual);
            
            IsHitTestVisible = false;
        }

        private static void OnConnectionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ConnectionLineRenderer renderer)
            {
                // 古いコレクションの監視を解除
                if (renderer._currentCollection != null)
                {
                    renderer._currentCollection.CollectionChanged -= renderer.OnCollectionChanged;
                    renderer._currentCollection = null;
                }
                
                // 新しいコレクションの監視を開始
                if (e.NewValue is INotifyCollectionChanged newCollection)
                {
                    newCollection.CollectionChanged += renderer.OnCollectionChanged;
                    renderer._currentCollection = newCollection;
                }
                
                renderer.InvalidateVisual();
            }
        }
        
        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            InvalidateVisual();
        }

        /// <summary>
        /// 接続線の再描画をリクエスト
        /// </summary>
        public void RequestRender()
        {
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            
            if (Connections == null)
                return;

            // DrawingVisualを使って一括描画
            using var dc = _connectionVisual.RenderOpen();
            
            foreach (var conn in Connections)
            {
                var pen = GetOrCreatePen(conn.ConnectionColor);
                
                switch (RenderMode)
                {
                    case RenderType.Middle:
                        if (conn.MiddlePathGeometry != null)
                        {
                            dc.DrawGeometry(null, pen, conn.MiddlePathGeometry);
                        }
                        break;
                        
                    case RenderType.StartEnd:
                        if (conn.StartSegmentGeometry != null)
                        {
                            dc.DrawGeometry(null, pen, conn.StartSegmentGeometry);
                        }
                        if (conn.EndSegmentGeometry != null)
                        {
                            dc.DrawGeometry(null, pen, conn.EndSegmentGeometry);
                        }
                        break;
                        
                    case RenderType.Full:
                        if (conn.PathGeometry != null)
                        {
                            dc.DrawGeometry(null, pen, conn.PathGeometry);
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// 指定された色のPenを取得（キャッシュから、なければ作成）
        /// </summary>
        private Pen GetOrCreatePen(Brush brush)
        {
            // SolidColorBrushの場合、色をキーとしてキャッシュ
            if (brush is SolidColorBrush solidBrush)
            {
                var color = solidBrush.Color;
                if (!_penCache.TryGetValue(color, out var cachedPen))
                {
                    cachedPen = CreatePen(brush);
                    _penCache[color] = cachedPen;
                }
                return cachedPen;
            }
            
            // SolidColorBrush以外の場合は毎回作成
            return CreatePen(brush);
        }

        /// <summary>
        /// Penを作成してFreezeする
        /// </summary>
        private static Pen CreatePen(Brush brush)
        {
            var pen = new Pen(brush, StrokeThickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            
            // 不透明度を適用
            if (pen.Brush.CanFreeze)
            {
                var clonedBrush = pen.Brush.Clone();
                clonedBrush.Opacity = StrokeOpacity;
                clonedBrush.Freeze();
                pen.Brush = clonedBrush;
            }
            
            pen.Freeze();
            return pen;
        }

        protected override int VisualChildrenCount => _visuals.Count;

        protected override Visual GetVisualChild(int index)
        {
            if (index < 0 || index >= _visuals.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _visuals[index];
        }
    }
}

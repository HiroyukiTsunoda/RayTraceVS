using CommunityToolkit.Mvvm.ComponentModel;
using RayTraceVS.WPF.Utils;
using System;
using System.Windows;
using System.Windows.Media;

namespace RayTraceVS.WPF.Models
{
    public partial class NodeConnection : ObservableObject, IDisposable
    {
        [ObservableProperty]
        private Guid id;

        [ObservableProperty]
        private NodeSocket? outputSocket;

        [ObservableProperty]
        private NodeSocket? inputSocket;

        [ObservableProperty]
        private Geometry? pathGeometry;

        // 分割された接続線のパーツ（StreamGeometryで高速化）
        [ObservableProperty]
        private Geometry? middlePathGeometry;  // 中間部分（最下層）

        [ObservableProperty]
        private Geometry? startSegmentGeometry;  // 始点側の端部分

        [ObservableProperty]
        private Geometry? endSegmentGeometry;  // 終点側の端部分

        // 接続線の選択状態（どちらかのノードが選択されている場合true）
        [ObservableProperty]
        private bool isSelected;

        // イベントハンドラを保持（解除可能にするため）
        private EventHandler? _outputPositionHandler;
        private EventHandler? _inputPositionHandler;
        
        // ノードの選択状態変更を監視するハンドラ
        private System.ComponentModel.PropertyChangedEventHandler? _outputNodeSelectionHandler;
        private System.ComponentModel.PropertyChangedEventHandler? _inputNodeSelectionHandler;

        private bool _disposed;
        
        // ダーティフラグ（遅延更新用）
        private bool _isDirty;

        // デフォルトの接続線の色（キャッシュ済み）
        private static readonly Brush DefaultConnectionColor = BrushCache.Get(0x00, 0x7A, 0xCC);

        // 接続線の色を出力ソケットの型に基づいて決定
        public Brush ConnectionColor => outputSocket?.SocketColor ?? DefaultConnectionColor;

        // 端部分のZIndex（ノードのCreationIndexに基づく）
        public int StartSegmentZIndex => outputSocket?.ParentNode?.CreationIndex + 1 ?? 0;
        public int EndSegmentZIndex => inputSocket?.ParentNode?.CreationIndex + 1 ?? 0;

        public NodeConnection()
        {
            id = Guid.NewGuid();
        }

        public NodeConnection(NodeSocket output, NodeSocket input)
        {
            id = Guid.NewGuid();
            outputSocket = output;
            inputSocket = input;
            
            // イベントハンドラを作成して保持（ラムダではなくフィールドに）
            _outputPositionHandler = (s, e) => UpdatePath();
            _inputPositionHandler = (s, e) => UpdatePath();
            
            // ソケットの位置変更イベントを監視
            if (outputSocket != null)
            {
                outputSocket.PositionChanged += _outputPositionHandler;
            }
            if (inputSocket != null)
            {
                inputSocket.PositionChanged += _inputPositionHandler;
            }
            
            // ノードの選択状態変更を監視
            _outputNodeSelectionHandler = (s, e) => OnNodeSelectionChanged(e);
            _inputNodeSelectionHandler = (s, e) => OnNodeSelectionChanged(e);
            
            if (outputSocket?.ParentNode != null)
            {
                outputSocket.ParentNode.PropertyChanged += _outputNodeSelectionHandler;
            }
            if (inputSocket?.ParentNode != null)
            {
                inputSocket.ParentNode.PropertyChanged += _inputNodeSelectionHandler;
            }
            
            // 初期選択状態を設定
            UpdateIsSelected();
        }
        
        /// <summary>
        /// ノードの選択状態が変更されたときに呼ばれる
        /// </summary>
        private void OnNodeSelectionChanged(System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Node.IsSelected))
            {
                UpdateIsSelected();
            }
        }
        
        /// <summary>
        /// 接続線の選択状態を更新（どちらかのノードが選択されていればtrue）
        /// </summary>
        public void UpdateIsSelected()
        {
            IsSelected = (outputSocket?.ParentNode?.IsSelected ?? false) || 
                         (inputSocket?.ParentNode?.IsSelected ?? false);
        }
        
        /// <summary>
        /// 接続線を更新が必要な状態としてマーク（遅延更新用）
        /// </summary>
        public void MarkDirty()
        {
            _isDirty = true;
        }
        
        /// <summary>
        /// ダーティフラグが立っている場合のみ更新を実行
        /// </summary>
        /// <returns>更新が実行された場合true</returns>
        public bool UpdateIfDirty()
        {
            if (_isDirty)
            {
                UpdatePath();
                _isDirty = false;
                return true;
            }
            return false;
        }
        
        /// <summary>
        /// ダーティフラグの状態を取得
        /// </summary>
        public bool IsDirty => _isDirty;

        public void UpdatePath()
        {
            if (outputSocket?.ParentNode == null || inputSocket?.ParentNode == null)
                return;

            // ソケットのPositionプロパティから直接位置を取得
            Point startPoint = outputSocket.Position;
            Point endPoint = inputSocket.Position;

            // 位置が未設定（0,0）の場合はフォールバックを使用
            if (startPoint.X == 0 && startPoint.Y == 0)
            {
                startPoint = CalculateSocketPosition(outputSocket, false);
            }
            if (endPoint.X == 0 && endPoint.Y == 0)
            {
                endPoint = CalculateSocketPosition(inputSocket, true);
            }

            // ベジェ曲線のコントロールポイントを計算
            double distance = Math.Abs(endPoint.X - startPoint.X);
            double controlPointOffset = Math.Min(distance * 0.5, 100);
            
            var controlPoint1 = new Point(startPoint.X + controlPointOffset, startPoint.Y);
            var controlPoint2 = new Point(endPoint.X - controlPointOffset, endPoint.Y);

            // StreamGeometryで高速描画（Freeze済みの不変オブジェクト）
            PathGeometry = CreateBezierStreamGeometry(startPoint, controlPoint1, controlPoint2, endPoint);

            // 分割されたパーツを更新
            UpdateSegmentedPaths(startPoint, controlPoint1, controlPoint2, endPoint);
        }

        /// <summary>
        /// StreamGeometryでベジェ曲線を作成（Freeze済みで高速）
        /// </summary>
        private static StreamGeometry CreateBezierStreamGeometry(Point start, Point ctrl1, Point ctrl2, Point end)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.BezierTo(ctrl1, ctrl2, end, true, false);
            }
            geometry.Freeze(); // 重要: パフォーマンス向上
            return geometry;
        }

        /// <summary>
        /// 接続線を中間部分と端部分に分割して更新
        /// </summary>
        private void UpdateSegmentedPaths(Point start, Point control1, Point control2, Point end)
        {
            // ノードの境界を取得
            var startNodeBounds = GetNodeBounds(outputSocket!.ParentNode!);
            var endNodeBounds = GetNodeBounds(inputSocket!.ParentNode!);

            // ベジェ曲線とノード境界の交点のtパラメータを計算
            double tStart = FindBezierRectIntersectionT(start, control1, control2, end, startNodeBounds, true);
            double tEnd = FindBezierRectIntersectionT(start, control1, control2, end, endNodeBounds, false);

            // tパラメータが無効な場合のフォールバック
            if (tStart >= tEnd)
            {
                tStart = 0.1;
                tEnd = 0.9;
            }

            // 始点側の端部分 (t=0 から t=tStart)
            var (startSegStart, startSegCtrl1, startSegCtrl2, startSegEnd) = 
                SplitBezierSegment(start, control1, control2, end, 0, tStart);
            UpdateStartSegment(startSegStart, startSegCtrl1, startSegCtrl2, startSegEnd);

            // 中間部分 (t=tStart から t=tEnd)
            var (midStart, midCtrl1, midCtrl2, midEnd) = 
                SplitBezierSegment(start, control1, control2, end, tStart, tEnd);
            UpdateMiddleSegment(midStart, midCtrl1, midCtrl2, midEnd);

            // 終点側の端部分 (t=tEnd から t=1)
            var (endSegStart, endSegCtrl1, endSegCtrl2, endSegEnd) = 
                SplitBezierSegment(start, control1, control2, end, tEnd, 1);
            UpdateEndSegment(endSegStart, endSegCtrl1, endSegCtrl2, endSegEnd);
        }

        /// <summary>
        /// ノードの境界矩形を取得
        /// </summary>
        private Rect GetNodeBounds(Node node)
        {
            // ノードのサイズ（概算値）
            double nodeWidth = node.NodeWidth;
            double nodeHeight = node.NodeHeight;
            return new Rect(node.Position.X, node.Position.Y, nodeWidth, nodeHeight);
        }

        /// <summary>
        /// ベジェ曲線とRectの交点のtパラメータを二分探索で計算
        /// </summary>
        /// <param name="fromStart">trueの場合は始点から探索、falseの場合は終点から探索</param>
        private double FindBezierRectIntersectionT(Point p0, Point p1, Point p2, Point p3, Rect bounds, bool fromStart)
        {
            const int iterations = 20;
            const double epsilon = 0.001;

            double tLow = fromStart ? 0 : 0.5;
            double tHigh = fromStart ? 0.5 : 1;

            // 二分探索で交点を見つける
            for (int i = 0; i < iterations; i++)
            {
                double tMid = (tLow + tHigh) / 2;
                Point pointAtT = EvaluateBezier(p0, p1, p2, p3, tMid);
                bool isInside = bounds.Contains(pointAtT);

                if (fromStart)
                {
                    // 始点から探索: 内部から外部への境界を探す
                    if (isInside)
                        tLow = tMid;
                    else
                        tHigh = tMid;
                }
                else
                {
                    // 終点から探索: 外部から内部への境界を探す
                    if (isInside)
                        tHigh = tMid;
                    else
                        tLow = tMid;
                }

                if (Math.Abs(tHigh - tLow) < epsilon)
                    break;
            }

            return fromStart ? tHigh : tLow;
        }

        /// <summary>
        /// ベジェ曲線上のtパラメータにおける点を計算
        /// </summary>
        private Point EvaluateBezier(Point p0, Point p1, Point p2, Point p3, double t)
        {
            double u = 1 - t;
            double tt = t * t;
            double uu = u * u;
            double ttt = tt * t;
            double uuu = uu * u;

            double x = uuu * p0.X + 3 * uu * t * p1.X + 3 * u * tt * p2.X + ttt * p3.X;
            double y = uuu * p0.Y + 3 * uu * t * p1.Y + 3 * u * tt * p2.Y + ttt * p3.Y;

            return new Point(x, y);
        }

        /// <summary>
        /// ベジェ曲線を指定されたtの範囲で分割
        /// De Casteljauのアルゴリズムを使用
        /// </summary>
        private (Point start, Point ctrl1, Point ctrl2, Point end) SplitBezierSegment(
            Point p0, Point p1, Point p2, Point p3, double t0, double t1)
        {
            // t0からt1の範囲のベジェ曲線を抽出
            // 新しいパラメータ範囲にマッピング
            double scale = t1 - t0;

            // t0での点と接線
            Point start = EvaluateBezier(p0, p1, p2, p3, t0);
            Point end = EvaluateBezier(p0, p1, p2, p3, t1);

            // コントロールポイントの計算（ホドグラフを使用）
            Point d0 = EvaluateBezierDerivative(p0, p1, p2, p3, t0);
            Point d1 = EvaluateBezierDerivative(p0, p1, p2, p3, t1);

            Point ctrl1 = new Point(
                start.X + d0.X * scale / 3,
                start.Y + d0.Y * scale / 3);
            Point ctrl2 = new Point(
                end.X - d1.X * scale / 3,
                end.Y - d1.Y * scale / 3);

            return (start, ctrl1, ctrl2, end);
        }

        /// <summary>
        /// ベジェ曲線の一次導関数を計算
        /// </summary>
        private Point EvaluateBezierDerivative(Point p0, Point p1, Point p2, Point p3, double t)
        {
            double u = 1 - t;
            double tt = t * t;
            double uu = u * u;

            // 一次導関数: 3[(1-t)^2(P1-P0) + 2(1-t)t(P2-P1) + t^2(P3-P2)]
            double dx = 3 * (uu * (p1.X - p0.X) + 2 * u * t * (p2.X - p1.X) + tt * (p3.X - p2.X));
            double dy = 3 * (uu * (p1.Y - p0.Y) + 2 * u * t * (p2.Y - p1.Y) + tt * (p3.Y - p2.Y));

            return new Point(dx, dy);
        }

        /// <summary>
        /// 始点側の端部分を更新
        /// </summary>
        private void UpdateStartSegment(Point start, Point ctrl1, Point ctrl2, Point end)
        {
            StartSegmentGeometry = CreateBezierStreamGeometry(start, ctrl1, ctrl2, end);
        }

        /// <summary>
        /// 中間部分を更新
        /// </summary>
        private void UpdateMiddleSegment(Point start, Point ctrl1, Point ctrl2, Point end)
        {
            MiddlePathGeometry = CreateBezierStreamGeometry(start, ctrl1, ctrl2, end);
        }

        /// <summary>
        /// 終点側の端部分を更新
        /// </summary>
        private void UpdateEndSegment(Point start, Point ctrl1, Point ctrl2, Point end)
        {
            EndSegmentGeometry = CreateBezierStreamGeometry(start, ctrl1, ctrl2, end);
        }

        private Point CalculateSocketPosition(NodeSocket socket, bool isInput)
        {
            if (socket.ParentNode == null)
                return new Point(0, 0);

            var node = socket.ParentNode;
            double nodeWidth = 150;
            double headerHeight = 30;
            double socketSize = 12;
            double socketSpacing = 20;

            int index = isInput 
                ? node.InputSockets.IndexOf(socket)
                : node.OutputSockets.IndexOf(socket);

            double x = isInput 
                ? node.Position.X 
                : node.Position.X + nodeWidth;
            double y = node.Position.Y + headerHeight + (index * socketSpacing) + socketSize / 2;

            return new Point(x, y);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                // イベント購読を解除
                if (outputSocket != null && _outputPositionHandler != null)
                {
                    outputSocket.PositionChanged -= _outputPositionHandler;
                }
                if (inputSocket != null && _inputPositionHandler != null)
                {
                    inputSocket.PositionChanged -= _inputPositionHandler;
                }
                
                // ノードの選択状態変更ハンドラを解除
                if (outputSocket?.ParentNode != null && _outputNodeSelectionHandler != null)
                {
                    outputSocket.ParentNode.PropertyChanged -= _outputNodeSelectionHandler;
                }
                if (inputSocket?.ParentNode != null && _inputNodeSelectionHandler != null)
                {
                    inputSocket.ParentNode.PropertyChanged -= _inputNodeSelectionHandler;
                }

                _outputPositionHandler = null;
                _inputPositionHandler = null;
                _outputNodeSelectionHandler = null;
                _inputNodeSelectionHandler = null;
            }

            _disposed = true;
        }
    }
}

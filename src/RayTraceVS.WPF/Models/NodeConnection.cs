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
        private PathGeometry? pathGeometry;

        // 分割された接続線のパーツ
        [ObservableProperty]
        private PathGeometry? middlePathGeometry;  // 中間部分（最下層）

        [ObservableProperty]
        private PathGeometry? startSegmentGeometry;  // 始点側の端部分

        [ObservableProperty]
        private PathGeometry? endSegmentGeometry;  // 終点側の端部分

        // イベントハンドラを保持（解除可能にするため）
        private EventHandler? _outputPositionHandler;
        private EventHandler? _inputPositionHandler;

        // PathGeometry関連オブジェクトを再利用
        private PathFigure? _pathFigure;
        private BezierSegment? _bezierSegment;
        
        // 分割用PathFigureとBezierSegment
        private PathFigure? _middlePathFigure;
        private BezierSegment? _middleBezierSegment;
        private PathFigure? _startPathFigure;
        private BezierSegment? _startBezierSegment;
        private PathFigure? _endPathFigure;
        private BezierSegment? _endBezierSegment;
        
        private bool _disposed;

        // デフォルトの接続線の色（キャッシュ済み）
        private static readonly Brush DefaultConnectionColor = BrushCache.Get(0x00, 0x7A, 0xCC);

        // 接続線の色を出力ソケットの型に基づいて決定
        public Brush ConnectionColor => outputSocket?.SocketColor ?? DefaultConnectionColor;

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
        }

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

            // 既存オブジェクトを再利用（毎回新規作成しない）
            if (_bezierSegment == null)
            {
                _bezierSegment = new BezierSegment(controlPoint1, controlPoint2, endPoint, true);
                _pathFigure = new PathFigure
                {
                    StartPoint = startPoint,
                    IsClosed = false
                };
                _pathFigure.Segments.Add(_bezierSegment);
                PathGeometry = new PathGeometry();
                PathGeometry.Figures.Add(_pathFigure);
            }
            else
            {
                // 既存オブジェクトのプロパティを更新
                _pathFigure!.StartPoint = startPoint;
                _bezierSegment.Point1 = controlPoint1;
                _bezierSegment.Point2 = controlPoint2;
                _bezierSegment.Point3 = endPoint;
            }

            // 分割されたパーツを更新
            UpdateSegmentedPaths(startPoint, controlPoint1, controlPoint2, endPoint);
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
            if (_startBezierSegment == null)
            {
                _startBezierSegment = new BezierSegment(ctrl1, ctrl2, end, true);
                _startPathFigure = new PathFigure { StartPoint = start, IsClosed = false };
                _startPathFigure.Segments.Add(_startBezierSegment);
                StartSegmentGeometry = new PathGeometry();
                StartSegmentGeometry.Figures.Add(_startPathFigure);
            }
            else
            {
                _startPathFigure!.StartPoint = start;
                _startBezierSegment.Point1 = ctrl1;
                _startBezierSegment.Point2 = ctrl2;
                _startBezierSegment.Point3 = end;
            }
        }

        /// <summary>
        /// 中間部分を更新
        /// </summary>
        private void UpdateMiddleSegment(Point start, Point ctrl1, Point ctrl2, Point end)
        {
            if (_middleBezierSegment == null)
            {
                _middleBezierSegment = new BezierSegment(ctrl1, ctrl2, end, true);
                _middlePathFigure = new PathFigure { StartPoint = start, IsClosed = false };
                _middlePathFigure.Segments.Add(_middleBezierSegment);
                MiddlePathGeometry = new PathGeometry();
                MiddlePathGeometry.Figures.Add(_middlePathFigure);
            }
            else
            {
                _middlePathFigure!.StartPoint = start;
                _middleBezierSegment.Point1 = ctrl1;
                _middleBezierSegment.Point2 = ctrl2;
                _middleBezierSegment.Point3 = end;
            }
        }

        /// <summary>
        /// 終点側の端部分を更新
        /// </summary>
        private void UpdateEndSegment(Point start, Point ctrl1, Point ctrl2, Point end)
        {
            if (_endBezierSegment == null)
            {
                _endBezierSegment = new BezierSegment(ctrl1, ctrl2, end, true);
                _endPathFigure = new PathFigure { StartPoint = start, IsClosed = false };
                _endPathFigure.Segments.Add(_endBezierSegment);
                EndSegmentGeometry = new PathGeometry();
                EndSegmentGeometry.Figures.Add(_endPathFigure);
            }
            else
            {
                _endPathFigure!.StartPoint = start;
                _endBezierSegment.Point1 = ctrl1;
                _endBezierSegment.Point2 = ctrl2;
                _endBezierSegment.Point3 = end;
            }
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

                _outputPositionHandler = null;
                _inputPositionHandler = null;
            }

            _disposed = true;
        }
    }
}

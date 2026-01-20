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

        // イベントハンドラを保持（解除可能にするため）
        private EventHandler? _outputPositionHandler;
        private EventHandler? _inputPositionHandler;

        // PathGeometry関連オブジェクトを再利用
        private PathFigure? _pathFigure;
        private BezierSegment? _bezierSegment;
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

using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Windows;
using System.Windows.Media;

namespace RayTraceVS.WPF.Models
{
    public partial class NodeConnection : ObservableObject
    {
        [ObservableProperty]
        private Guid id;

        [ObservableProperty]
        private NodeSocket? outputSocket;

        [ObservableProperty]
        private NodeSocket? inputSocket;

        [ObservableProperty]
        private PathGeometry? pathGeometry;

        // 接続線の色を出力ソケットの型に基づいて決定
        public Brush ConnectionColor => outputSocket?.SocketColor ?? new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));

        public NodeConnection()
        {
            id = Guid.NewGuid();
        }

        public NodeConnection(NodeSocket output, NodeSocket input)
        {
            id = Guid.NewGuid();
            outputSocket = output;
            inputSocket = input;
            
            // ソケットの位置変更イベントを監視
            if (outputSocket != null)
            {
                outputSocket.PositionChanged += (s, e) => UpdatePath();
            }
            if (inputSocket != null)
            {
                inputSocket.PositionChanged += (s, e) => UpdatePath();
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
                System.Diagnostics.Debug.WriteLine($"  Fallback start: ({startPoint.X:F1},{startPoint.Y:F1})");
            }
            if (endPoint.X == 0 && endPoint.Y == 0)
            {
                endPoint = CalculateSocketPosition(inputSocket, true);
                System.Diagnostics.Debug.WriteLine($"  Fallback end: ({endPoint.X:F1},{endPoint.Y:F1})");
            }

            // ベジェ曲線のコントロールポイントを計算
            double distance = Math.Abs(endPoint.X - startPoint.X);
            double controlPointOffset = Math.Min(distance * 0.5, 100);
            
            var bezierSegment = new BezierSegment(
                new Point(startPoint.X + controlPointOffset, startPoint.Y),
                new Point(endPoint.X - controlPointOffset, endPoint.Y),
                endPoint,
                true);

            var pathFigure = new PathFigure(startPoint, new[] { bezierSegment }, false);
            PathGeometry = new PathGeometry(new[] { pathFigure });
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
    }
}

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
            UpdatePath();
        }

        public void UpdatePath()
        {
            if (outputSocket?.ParentNode == null || inputSocket?.ParentNode == null)
                return;

            // ソケットに保存された実際の位置を使用
            Point startPoint = outputSocket.Position;
            Point endPoint = inputSocket.Position;

            System.Diagnostics.Debug.WriteLine($"UpdatePath - Output position: {startPoint}, Input position: {endPoint}");

            // 位置が設定されていない場合は、概算値を使用
            if (startPoint.X == 0 && startPoint.Y == 0)
            {
                var outputNode = outputSocket.ParentNode;
                double nodeWidth = 150;
                double headerHeight = 30;
                double socketSize = 12;
                int outputIndex = outputNode.OutputSockets.IndexOf(outputSocket);
                double outputY = outputNode.Position.Y + headerHeight + (outputIndex * 20) + socketSize / 2;
                startPoint = new Point(outputNode.Position.X + nodeWidth, outputY);
                System.Diagnostics.Debug.WriteLine($"UpdatePath - Using estimated output position: {startPoint}");
            }

            if (endPoint.X == 0 && endPoint.Y == 0)
            {
                var inputNode = inputSocket.ParentNode;
                double headerHeight = 30;
                double socketSize = 12;
                int inputIndex = inputNode.InputSockets.IndexOf(inputSocket);
                double inputY = inputNode.Position.Y + headerHeight + (inputIndex * 20) + socketSize / 2;
                endPoint = new Point(inputNode.Position.X, inputY);
                System.Diagnostics.Debug.WriteLine($"UpdatePath - Using estimated input position: {endPoint}");
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
    }
}

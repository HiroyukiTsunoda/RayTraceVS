using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Utils;
using RayTraceVS.WPF.ViewModels;

namespace RayTraceVS.WPF.Views.Handlers
{
    /// <summary>
    /// ノードエディタの接続線処理を担当するハンドラ
    /// 接続線の作成、プレビュー、削除を処理
    /// </summary>
    public class ConnectionHandler
    {
        private readonly EditorInputState _state;

        public ConnectionHandler(EditorInputState state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        /// <summary>
        /// 接続ドラッグを開始
        /// </summary>
        public void StartConnectionDrag(NodeSocket socket, Ellipse socketElement)
        {
            _state.IsDraggingConnection = true;
            _state.DraggedSocket = socket;
            _state.DraggedSocketElement = socketElement;
            CreatePreviewLine(socket);
        }

        /// <summary>
        /// 接続ドラッグを更新
        /// </summary>
        public void UpdateConnectionDrag(Point currentPosition)
        {
            if (!_state.IsDraggingConnection || _state.PreviewLine == null)
                return;

            UpdatePreviewLine(currentPosition);
        }

        /// <summary>
        /// 接続ドラッグを終了し、接続を作成
        /// </summary>
        public void EndConnectionDrag(NodeSocket? targetSocket, MainViewModel? viewModel, Action<NodeConnection>? onConnectionCreated)
        {
            if (!_state.IsDraggingConnection)
                return;

            if (targetSocket != null && _state.DraggedSocket != null && viewModel != null)
            {
                var connection = TryCreateConnection(_state.DraggedSocket, targetSocket, viewModel);
                if (connection != null)
                {
                    onConnectionCreated?.Invoke(connection);
                }
            }

            _state.IsDraggingConnection = false;
            _state.DraggedSocket = null;
            _state.DraggedSocketElement = null;
            RemovePreviewLine();
        }

        /// <summary>
        /// 接続ドラッグをキャンセル
        /// </summary>
        public void CancelConnectionDrag()
        {
            _state.IsDraggingConnection = false;
            _state.DraggedSocket = null;
            _state.DraggedSocketElement = null;
            RemovePreviewLine();
        }

        /// <summary>
        /// 接続を作成（ソケットの互換性チェック付き）
        /// </summary>
        private NodeConnection? TryCreateConnection(NodeSocket source, NodeSocket target, MainViewModel viewModel)
        {
            // 同じソケットには接続不可
            if (source == target) return null;

            // 同じノードには接続不可
            if (source.ParentNode == target.ParentNode) return null;

            // 入力/出力の方向チェック
            NodeSocket outputSocket, inputSocket;
            if (source.IsInput && !target.IsInput)
            {
                outputSocket = target;
                inputSocket = source;
            }
            else if (!source.IsInput && target.IsInput)
            {
                outputSocket = source;
                inputSocket = target;
            }
            else
            {
                // 同じ方向同士は接続不可
                return null;
            }

            // 型互換性チェック
            if (!AreSocketsCompatible(outputSocket, inputSocket))
                return null;

            // 既存の接続を削除（入力ソケットは1つの接続のみ許可）
            RemoveExistingInputConnection(inputSocket, viewModel);

            // 接続を作成
            var connection = new NodeConnection(outputSocket, inputSocket);
            viewModel.AddConnection(connection);

            return connection;
        }

        /// <summary>
        /// ソケットの型互換性をチェック
        /// </summary>
        public bool AreSocketsCompatible(NodeSocket output, NodeSocket input)
        {
            // 同じ型は互換性あり
            if (output.SocketType == input.SocketType)
                return true;

            // Color ↔ Vector3 は互換性あり（RGB部分）
            if ((output.SocketType == SocketType.Color && input.SocketType == SocketType.Vector3) ||
                (output.SocketType == SocketType.Vector3 && input.SocketType == SocketType.Color))
                return true;

            return false;
        }

        /// <summary>
        /// 入力ソケットの既存接続を削除
        /// </summary>
        private void RemoveExistingInputConnection(NodeSocket inputSocket, MainViewModel viewModel)
        {
            NodeConnection? existingConnection = null;
            foreach (var conn in viewModel.Connections)
            {
                if (conn.InputSocket == inputSocket)
                {
                    existingConnection = conn;
                    break;
                }
            }

            if (existingConnection != null)
            {
                viewModel.RemoveConnection(existingConnection);
            }
        }

        /// <summary>
        /// プレビュー線の互換性表示を更新
        /// </summary>
        public void UpdatePreviewLineCompatibility(NodeSocket? targetSocket)
        {
            if (_state.PreviewLine == null || _state.DraggedSocket == null)
                return;

            if (targetSocket == null || targetSocket == _state.DraggedSocket)
            {
                // デフォルト色に戻す
                ResetPreviewLineColor();
                return;
            }

            // 互換性チェック
            bool isCompatible;
            if (_state.DraggedSocket.IsInput && !targetSocket.IsInput)
            {
                isCompatible = AreSocketsCompatible(targetSocket, _state.DraggedSocket);
            }
            else if (!_state.DraggedSocket.IsInput && targetSocket.IsInput)
            {
                isCompatible = AreSocketsCompatible(_state.DraggedSocket, targetSocket);
            }
            else
            {
                isCompatible = false;
            }

            if (isCompatible)
            {
                // 互換性あり：緑色
                _state.PreviewLine.Stroke = BrushCache.Get(0x2E, 0xCC, 0x71);
                _state.PreviewLine.Opacity = 1.0;
            }
            else
            {
                // 互換性なし：赤色
                _state.PreviewLine.Stroke = BrushCache.Get(Colors.Red);
                _state.PreviewLine.Opacity = 0.5;
            }
        }

        #region プレビュー線

        private void CreatePreviewLine(NodeSocket socket)
        {
            var socketColor = socket.SocketColor as SolidColorBrush;
            var startPoint = socket.Position;

            _state.PreviewLine = new Line
            {
                X1 = startPoint.X,
                Y1 = startPoint.Y,
                X2 = startPoint.X,
                Y2 = startPoint.Y,
                Stroke = socketColor ?? BrushCache.Get(0x00, 0x7A, 0xCC),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                IsHitTestVisible = false
            };

            _state.ConnectionLayer?.Children.Add(_state.PreviewLine);
        }

        private void UpdatePreviewLine(Point endPoint)
        {
            if (_state.PreviewLine == null) return;

            _state.PreviewLine.X2 = endPoint.X;
            _state.PreviewLine.Y2 = endPoint.Y;
        }

        private void ResetPreviewLineColor()
        {
            if (_state.PreviewLine == null || _state.DraggedSocket == null)
                return;

            var socketColor = _state.DraggedSocket.SocketColor as SolidColorBrush;
            _state.PreviewLine.Stroke = socketColor ?? BrushCache.Get(0x00, 0x7A, 0xCC);
            _state.PreviewLine.Opacity = 1.0;
        }

        private void RemovePreviewLine()
        {
            if (_state.PreviewLine != null && _state.ConnectionLayer != null)
            {
                _state.ConnectionLayer.Children.Remove(_state.PreviewLine);
                _state.PreviewLine = null;
            }
        }

        #endregion

        /// <summary>
        /// 接続ドラッグ中かどうか
        /// </summary>
        public bool IsDragging => _state.IsDraggingConnection;

        /// <summary>
        /// 現在ドラッグ中のソケット
        /// </summary>
        public NodeSocket? DraggedSocket => _state.DraggedSocket;
    }
}

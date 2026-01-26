using System;
using System.Windows;
using System.Windows.Input;

namespace RayTraceVS.WPF.Views.Handlers
{
    /// <summary>
    /// ノードエディタのパン（移動）とズーム処理を担当するハンドラ
    /// </summary>
    public class PanZoomHandler
    {
        private readonly EditorInputState _state;
        private CoordinateTransformer? _coordinateTransformer;

        public PanZoomHandler(EditorInputState state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        /// <summary>
        /// CoordinateTransformerを設定
        /// </summary>
        public void SetCoordinateTransformer(CoordinateTransformer transformer)
        {
            _coordinateTransformer = transformer;
        }

        /// <summary>
        /// パン開始
        /// </summary>
        public void StartPan(Point mousePosition)
        {
            _state.IsPanning = true;
            _state.LastMousePosition = mousePosition;
        }

        /// <summary>
        /// パン更新
        /// </summary>
        public void UpdatePan(Point currentPosition)
        {
            if (!_state.IsPanning) return;

            var delta = currentPosition - _state.LastMousePosition;
            _state.PanTransform.X += delta.X;
            _state.PanTransform.Y += delta.Y;
            _state.LastMousePosition = currentPosition;
        }

        /// <summary>
        /// パン終了
        /// </summary>
        public void EndPan()
        {
            _state.IsPanning = false;
        }

        /// <summary>
        /// マウスホイールによるズーム処理
        /// </summary>
        public void HandleZoom(MouseWheelEventArgs e, Point mousePosition)
        {
            // ズーム前のマウス位置（Canvas座標系）
            var canvasMousePosBefore = TransformToCanvas(mousePosition);

            // ズーム量を計算
            double zoomDelta = e.Delta * EditorInputState.ZoomSpeed;
            double newZoom = Math.Clamp(
                _state.CurrentZoom + zoomDelta,
                EditorInputState.MinZoom,
                EditorInputState.MaxZoom
            );

            // ズームを適用
            _state.CurrentZoom = newZoom;
            _state.ZoomTransform.ScaleX = newZoom;
            _state.ZoomTransform.ScaleY = newZoom;

            // ズーム後のマウス位置（Canvas座標系）
            var canvasMousePosAfter = TransformToCanvas(mousePosition);

            // マウス位置を中心にズームするように平行移動を調整
            var diff = canvasMousePosAfter - canvasMousePosBefore;
            _state.PanTransform.X += diff.X * newZoom;
            _state.PanTransform.Y += diff.Y * newZoom;
        }

        /// <summary>
        /// スクリーン座標からキャンバス座標への変換
        /// CoordinateTransformerに委譲
        /// </summary>
        public Point TransformToCanvas(Point screenPoint)
        {
            if (_coordinateTransformer != null)
            {
                return _coordinateTransformer.ToCanvasPoint(screenPoint);
            }
            
            // フォールバック（CoordinateTransformerが未設定の場合）
            return new Point(
                (screenPoint.X - _state.PanTransform.X) / _state.CurrentZoom,
                (screenPoint.Y - _state.PanTransform.Y) / _state.CurrentZoom
            );
        }

        /// <summary>
        /// キャンバス座標からスクリーン座標への変換
        /// CoordinateTransformerに委譲
        /// </summary>
        public Point TransformToScreen(Point canvasPoint)
        {
            if (_coordinateTransformer != null)
            {
                return _coordinateTransformer.ToScreenPoint(canvasPoint);
            }
            
            // フォールバック（CoordinateTransformerが未設定の場合）
            return new Point(
                canvasPoint.X * _state.CurrentZoom + _state.PanTransform.X,
                canvasPoint.Y * _state.CurrentZoom + _state.PanTransform.Y
            );
        }

        /// <summary>
        /// 現在のズーム倍率を取得
        /// </summary>
        public double CurrentZoom => _state.CurrentZoom;

        /// <summary>
        /// ビューをリセット（ズーム100%、原点に移動）
        /// </summary>
        public void ResetView()
        {
            _state.CurrentZoom = 1.0;
            _state.ZoomTransform.ScaleX = 1.0;
            _state.ZoomTransform.ScaleY = 1.0;
            _state.PanTransform.X = 0;
            _state.PanTransform.Y = 0;
        }

        /// <summary>
        /// 指定した点を中心にビューを移動
        /// </summary>
        public void CenterOn(Point canvasPoint, Size viewSize)
        {
            _state.PanTransform.X = viewSize.Width / 2 - canvasPoint.X * _state.CurrentZoom;
            _state.PanTransform.Y = viewSize.Height / 2 - canvasPoint.Y * _state.CurrentZoom;
        }
    }
}

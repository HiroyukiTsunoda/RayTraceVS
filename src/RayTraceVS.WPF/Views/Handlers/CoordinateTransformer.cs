using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RayTraceVS.WPF.Views.Handlers
{
    /// <summary>
    /// ノードエディタの座標変換を一元管理するクラス
    /// スクリーン座標⇄キャンバス座標の変換を統一的に提供
    /// </summary>
    public class CoordinateTransformer
    {
        private readonly EditorInputState _state;
        private readonly FrameworkElement _container;

        /// <summary>
        /// CoordinateTransformerを初期化
        /// </summary>
        /// <param name="state">エディタの入力状態（パン/ズーム情報を含む）</param>
        /// <param name="container">スクリーン座標の基準となるコンテナ（通常はUserControl）</param>
        public CoordinateTransformer(EditorInputState state, FrameworkElement container)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _container = container ?? throw new ArgumentNullException(nameof(container));
        }

        /// <summary>
        /// スクリーン座標（コンテナ相対）をキャンバス座標に変換
        /// </summary>
        /// <param name="screenPoint">スクリーン座標（コンテナ相対）</param>
        /// <returns>キャンバス座標</returns>
        public Point ToCanvasPoint(Point screenPoint)
        {
            return new Point(
                (screenPoint.X - _state.PanTransform.X) / _state.CurrentZoom,
                (screenPoint.Y - _state.PanTransform.Y) / _state.CurrentZoom
            );
        }

        /// <summary>
        /// キャンバス座標をスクリーン座標（コンテナ相対）に変換
        /// </summary>
        /// <param name="canvasPoint">キャンバス座標</param>
        /// <returns>スクリーン座標（コンテナ相対）</returns>
        public Point ToScreenPoint(Point canvasPoint)
        {
            return new Point(
                canvasPoint.X * _state.CurrentZoom + _state.PanTransform.X,
                canvasPoint.Y * _state.CurrentZoom + _state.PanTransform.Y
            );
        }

        /// <summary>
        /// MouseEventArgsからキャンバス座標を取得
        /// パン/ズームを考慮した正確なキャンバス上の位置を返す
        /// </summary>
        /// <param name="e">マウスイベント引数</param>
        /// <returns>キャンバス座標</returns>
        public Point GetCanvasPosition(MouseEventArgs e)
        {
            // コンテナ相対のスクリーン座標を取得し、キャンバス座標に変換
            var screenPoint = e.GetPosition(_container);
            return ToCanvasPoint(screenPoint);
        }

        /// <summary>
        /// MouseEventArgsからスクリーン座標（コンテナ相対）を取得
        /// パン/ズーム操作自体に使用（変換前の座標が必要な場合）
        /// </summary>
        /// <param name="e">マウスイベント引数</param>
        /// <returns>スクリーン座標（コンテナ相対）</returns>
        public Point GetScreenPosition(MouseEventArgs e)
        {
            return e.GetPosition(_container);
        }

        /// <summary>
        /// 静的なマウス位置からキャンバス座標を取得
        /// イベント引数が利用できない場合に使用
        /// </summary>
        /// <returns>キャンバス座標</returns>
        public Point GetCurrentCanvasPosition()
        {
            var screenPoint = Mouse.GetPosition(_container);
            return ToCanvasPoint(screenPoint);
        }

        /// <summary>
        /// UI要素の中心座標をキャンバス座標で取得
        /// ソケットの位置取得などに使用
        /// </summary>
        /// <param name="element">対象のUI要素</param>
        /// <returns>キャンバス座標での中心位置</returns>
        public Point GetElementCenterOnCanvas(FrameworkElement element)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            if (_state.NodeCanvas == null)
                throw new InvalidOperationException("NodeCanvas is not set in EditorInputState");

            var transform = element.TransformToAncestor(_state.NodeCanvas);
            var center = new Point(element.ActualWidth / 2, element.ActualHeight / 2);
            return transform.Transform(center);
        }

        /// <summary>
        /// UI要素の指定位置をキャンバス座標で取得
        /// </summary>
        /// <param name="element">対象のUI要素</param>
        /// <param name="localPoint">要素内のローカル座標</param>
        /// <returns>キャンバス座標での位置</returns>
        public Point GetElementPointOnCanvas(FrameworkElement element, Point localPoint)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            if (_state.NodeCanvas == null)
                throw new InvalidOperationException("NodeCanvas is not set in EditorInputState");

            var transform = element.TransformToAncestor(_state.NodeCanvas);
            return transform.Transform(localPoint);
        }

        /// <summary>
        /// 現在のズーム倍率を取得
        /// </summary>
        public double CurrentZoom => _state.CurrentZoom;

        /// <summary>
        /// スクリーン上の距離をキャンバス上の距離に変換
        /// </summary>
        /// <param name="screenDistance">スクリーン上の距離</param>
        /// <returns>キャンバス上の距離</returns>
        public double ToCanvasDistance(double screenDistance)
        {
            return screenDistance / _state.CurrentZoom;
        }

        /// <summary>
        /// キャンバス上の距離をスクリーン上の距離に変換
        /// </summary>
        /// <param name="canvasDistance">キャンバス上の距離</param>
        /// <returns>スクリーン上の距離</returns>
        public double ToScreenDistance(double canvasDistance)
        {
            return canvasDistance * _state.CurrentZoom;
        }
    }
}

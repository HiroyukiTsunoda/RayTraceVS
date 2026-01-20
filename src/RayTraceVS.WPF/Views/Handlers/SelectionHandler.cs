using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Utils;
using RayTraceVS.WPF.ViewModels;

namespace RayTraceVS.WPF.Views.Handlers
{
    /// <summary>
    /// ノードエディタの選択処理を担当するハンドラ
    /// 単一選択、複数選択、矩形選択を処理
    /// </summary>
    public class SelectionHandler
    {
        private readonly EditorInputState _state;

        public SelectionHandler(EditorInputState state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        /// <summary>
        /// すべての選択をクリア
        /// </summary>
        public void ClearAllSelections(MainViewModel? viewModel)
        {
            if (viewModel == null) return;

            viewModel.SelectedNode = null;
            foreach (var n in viewModel.Nodes)
            {
                n.IsSelected = false;
            }
            _state.SelectedNodes.Clear();
        }

        /// <summary>
        /// ノードを選択状態にする
        /// </summary>
        public void SelectNode(Node node, MainViewModel viewModel, bool addToSelection = false)
        {
            if (!addToSelection)
            {
                ClearAllSelections(viewModel);
            }

            node.IsSelected = true;
            _state.SelectedNodes.Add(node);
            viewModel.SelectedNode = node;
        }

        /// <summary>
        /// ノードの選択状態をトグル
        /// </summary>
        public void ToggleNodeSelection(Node node, MainViewModel viewModel)
        {
            if (_state.SelectedNodes.Contains(node))
            {
                node.IsSelected = false;
                _state.SelectedNodes.Remove(node);
                if (viewModel.SelectedNode == node)
                {
                    viewModel.SelectedNode = _state.SelectedNodes.Count > 0 
                        ? System.Linq.Enumerable.First(_state.SelectedNodes) 
                        : null;
                }
            }
            else
            {
                node.IsSelected = true;
                _state.SelectedNodes.Add(node);
                viewModel.SelectedNode = node;
            }
        }

        /// <summary>
        /// 選択されているノードの集合を取得
        /// </summary>
        public IReadOnlyCollection<Node> GetSelectedNodes() => _state.SelectedNodes;

        /// <summary>
        /// ノードが選択されているかどうか
        /// </summary>
        public bool IsNodeSelected(Node node) => _state.SelectedNodes.Contains(node);

        #region 矩形選択

        /// <summary>
        /// 矩形選択を開始
        /// </summary>
        public void StartRectSelection(Point startPoint)
        {
            _state.IsRectSelecting = true;
            _state.RectSelectStartPoint = startPoint;
            CreateSelectionRectangle(startPoint);
        }

        /// <summary>
        /// 矩形選択を更新
        /// </summary>
        public void UpdateRectSelection(Point currentPoint)
        {
            if (!_state.IsRectSelecting || _state.SelectionRectangle == null)
                return;

            UpdateSelectionRectangle(currentPoint);
        }

        /// <summary>
        /// 矩形選択を終了し、範囲内のノードを選択
        /// </summary>
        public void EndRectSelection(MainViewModel? viewModel, Point endPoint, bool addToSelection = false)
        {
            if (!_state.IsRectSelecting) return;

            _state.IsRectSelecting = false;

            if (viewModel != null)
            {
                SelectNodesInRectangle(viewModel, _state.RectSelectStartPoint, endPoint, addToSelection);
            }

            RemoveSelectionRectangle();
        }

        /// <summary>
        /// 矩形選択をキャンセル
        /// </summary>
        public void CancelRectSelection()
        {
            _state.IsRectSelecting = false;
            RemoveSelectionRectangle();
        }

        private void CreateSelectionRectangle(Point startPoint)
        {
            _state.SelectionRectangle = new Rectangle
            {
                Stroke = BrushCache.Get(100, 150, 255),
                StrokeThickness = 1,
                Fill = BrushCache.Get(30, 100, 150, 255),
                IsHitTestVisible = false
            };

            Canvas.SetLeft(_state.SelectionRectangle, startPoint.X);
            Canvas.SetTop(_state.SelectionRectangle, startPoint.Y);
            _state.SelectionRectangle.Width = 0;
            _state.SelectionRectangle.Height = 0;

            _state.NodeCanvas?.Children.Add(_state.SelectionRectangle);
        }

        private void UpdateSelectionRectangle(Point currentPoint)
        {
            if (_state.SelectionRectangle == null) return;

            var startPoint = _state.RectSelectStartPoint;

            double left = Math.Min(startPoint.X, currentPoint.X);
            double top = Math.Min(startPoint.Y, currentPoint.Y);
            double width = Math.Abs(currentPoint.X - startPoint.X);
            double height = Math.Abs(currentPoint.Y - startPoint.Y);

            Canvas.SetLeft(_state.SelectionRectangle, left);
            Canvas.SetTop(_state.SelectionRectangle, top);
            _state.SelectionRectangle.Width = width;
            _state.SelectionRectangle.Height = height;
        }

        private void RemoveSelectionRectangle()
        {
            if (_state.SelectionRectangle != null && _state.NodeCanvas != null)
            {
                _state.NodeCanvas.Children.Remove(_state.SelectionRectangle);
                _state.SelectionRectangle = null;
            }
        }

        private void SelectNodesInRectangle(MainViewModel viewModel, Point startPoint, Point endPoint, bool addToSelection)
        {
            // 選択矩形の範囲を計算
            double left = Math.Min(startPoint.X, endPoint.X);
            double top = Math.Min(startPoint.Y, endPoint.Y);
            double right = Math.Max(startPoint.X, endPoint.X);
            double bottom = Math.Max(startPoint.Y, endPoint.Y);
            var selectionRect = new Rect(left, top, right - left, bottom - top);

            // 最小サイズチェック（クリックと区別するため）
            if (selectionRect.Width < 5 && selectionRect.Height < 5)
                return;

            if (!addToSelection)
            {
                ClearAllSelections(viewModel);
            }

            // 範囲内のノードを選択
            double nodeWidth = 150;
            double nodeHeight = 100;

            foreach (var node in viewModel.Nodes)
            {
                var nodeRect = new Rect(node.Position.X, node.Position.Y, nodeWidth, nodeHeight);
                
                if (selectionRect.IntersectsWith(nodeRect))
                {
                    node.IsSelected = true;
                    _state.SelectedNodes.Add(node);
                    viewModel.SelectedNode = node;
                }
            }
        }

        #endregion
    }
}

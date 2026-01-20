using System;
using System.Collections.Generic;
using System.Windows;
using RayTraceVS.WPF.Models;

namespace RayTraceVS.WPF.Views.Handlers
{
    /// <summary>
    /// ノードエディタのノードドラッグ処理を担当するハンドラ
    /// </summary>
    public class NodeDragHandler
    {
        private readonly EditorInputState _state;
        private readonly Action<Node>? _onNodeMoved;

        public NodeDragHandler(EditorInputState state, Action<Node>? onNodeMoved = null)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _onNodeMoved = onNodeMoved;
        }

        /// <summary>
        /// ノードドラッグを開始
        /// </summary>
        public void StartDrag(Node node, Point mousePosition, IEnumerable<Node> selectedNodes)
        {
            _state.IsDraggingNode = true;
            _state.DraggedNode = node;
            _state.DragStartOffset = new Point(
                mousePosition.X - node.Position.X,
                mousePosition.Y - node.Position.Y
            );

            // 複数選択の場合、各ノードのオフセットを記録
            _state.MultiDragOffsets.Clear();
            foreach (var selectedNode in selectedNodes)
            {
                _state.MultiDragOffsets[selectedNode] = new Point(
                    mousePosition.X - selectedNode.Position.X,
                    mousePosition.Y - selectedNode.Position.Y
                );
            }
        }

        /// <summary>
        /// ノードドラッグを更新（単一ノード）
        /// </summary>
        public void UpdateDrag(Point currentPosition)
        {
            if (!_state.IsDraggingNode || _state.DraggedNode == null)
                return;

            // 複数選択でドラッグしている場合
            if (_state.MultiDragOffsets.Count > 1)
            {
                UpdateMultiDrag(currentPosition);
            }
            else
            {
                // 単一ノードのドラッグ
                var newPosition = new Point(
                    currentPosition.X - _state.DragStartOffset.X,
                    currentPosition.Y - _state.DragStartOffset.Y
                );
                _state.DraggedNode.Position = newPosition;
                _onNodeMoved?.Invoke(_state.DraggedNode);
            }
        }

        /// <summary>
        /// 複数ノードのドラッグを更新
        /// </summary>
        private void UpdateMultiDrag(Point currentPosition)
        {
            foreach (var kvp in _state.MultiDragOffsets)
            {
                var node = kvp.Key;
                var offset = kvp.Value;
                var newPosition = new Point(
                    currentPosition.X - offset.X,
                    currentPosition.Y - offset.Y
                );
                node.Position = newPosition;
                _onNodeMoved?.Invoke(node);
            }
        }

        /// <summary>
        /// ノードドラッグを終了
        /// </summary>
        public void EndDrag()
        {
            _state.IsDraggingNode = false;
            _state.DraggedNode = null;
            _state.MultiDragOffsets.Clear();
        }

        /// <summary>
        /// ドラッグ中かどうか
        /// </summary>
        public bool IsDragging => _state.IsDraggingNode;

        /// <summary>
        /// 現在ドラッグ中のノード
        /// </summary>
        public Node? DraggedNode => _state.DraggedNode;
    }
}

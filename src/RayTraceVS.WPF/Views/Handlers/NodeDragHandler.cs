using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RayTraceVS.WPF.Commands;
using RayTraceVS.WPF.Models;

namespace RayTraceVS.WPF.Views.Handlers
{
    /// <summary>
    /// ノードエディタのノードドラッグ処理を担当するハンドラ
    /// Undo/Redo対応
    /// </summary>
    public class NodeDragHandler
    {
        private readonly EditorInputState _state;
        private readonly Action<Node>? _onNodeMoved;
        
        // Undo用の開始位置を内部で管理
        private readonly Dictionary<Node, Point> _dragStartPositions = new();
        
        // スロットリング用
        private DateTime _lastConnectionUpdate = DateTime.MinValue;
        private const int UpdateIntervalMs = 16; // 約60fps
        
        /// <summary>
        /// Canvas.UpdateLayout() を呼び出すコールバック
        /// </summary>
        public Action? OnRequestLayoutUpdate { get; set; }

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

            // 複数選択の場合、各ノードのオフセットとドラッグ開始位置を記録
            _state.MultiDragOffsets.Clear();
            _dragStartPositions.Clear();
            
            foreach (var selectedNode in selectedNodes)
            {
                _state.MultiDragOffsets[selectedNode] = new Point(
                    mousePosition.X - selectedNode.Position.X,
                    mousePosition.Y - selectedNode.Position.Y
                );
                // Undo用に開始位置を記録（ハンドラ内部で管理）
                _dragStartPositions[selectedNode] = selectedNode.Position;
            }
        }

        /// <summary>
        /// ノードドラッグを更新
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
                
                // 接続線更新のスロットリング（約60fps）
                if (ShouldUpdateConnections())
                {
                    // レイアウト更新を先に行う（UIが更新されないとソケット位置が正しく取得できない）
                    OnRequestLayoutUpdate?.Invoke();
                    _onNodeMoved?.Invoke(_state.DraggedNode);
                    _lastConnectionUpdate = DateTime.Now;
                    return;
                }
            }
            
            // レイアウト更新を要求
            OnRequestLayoutUpdate?.Invoke();
        }
        
        /// <summary>
        /// 接続線の更新が必要かどうかを判定（スロットリング）
        /// </summary>
        private bool ShouldUpdateConnections()
        {
            return (DateTime.Now - _lastConnectionUpdate).TotalMilliseconds >= UpdateIntervalMs;
        }

        /// <summary>
        /// 複数ノードのドラッグを更新
        /// </summary>
        private void UpdateMultiDrag(Point currentPosition)
        {
            // 接続線更新のスロットリング判定
            bool shouldUpdate = ShouldUpdateConnections();
            
            // まずすべてのノードの位置を更新
            foreach (var kvp in _state.MultiDragOffsets)
            {
                var node = kvp.Key;
                var offset = kvp.Value;
                var newPosition = new Point(
                    currentPosition.X - offset.X,
                    currentPosition.Y - offset.Y
                );
                node.Position = newPosition;
            }
            
            // スロットリング適用
            if (shouldUpdate)
            {
                // レイアウト更新を先に行う（UIが更新されないとソケット位置が正しく取得できない）
                OnRequestLayoutUpdate?.Invoke();
                
                // ソケット位置と接続線を更新
                foreach (var kvp in _state.MultiDragOffsets)
                {
                    _onNodeMoved?.Invoke(kvp.Key);
                }
                _lastConnectionUpdate = DateTime.Now;
            }
        }

        /// <summary>
        /// ノードドラッグを終了（Undo/Redo用コマンドを登録）
        /// </summary>
        /// <param name="commandManager">コマンドマネージャ（nullの場合はコマンド登録しない）</param>
        public void EndDrag(CommandManager? commandManager = null)
        {
            if (!_state.IsDraggingNode)
                return;
            
            // ドラッグ終了時に接続線を最終更新（スロットリングで更新されていない場合のため）
            foreach (var node in _dragStartPositions.Keys)
            {
                _onNodeMoved?.Invoke(node);
            }
                
            // Undo用にコマンドを登録
            if (commandManager != null && _dragStartPositions.Count > 0)
            {
                RegisterMoveCommand(commandManager);
            }
            
            _state.IsDraggingNode = false;
            _state.DraggedNode = null;
            _state.MultiDragOffsets.Clear();
            _dragStartPositions.Clear();
        }
        
        /// <summary>
        /// 移動コマンドを登録
        /// </summary>
        private void RegisterMoveCommand(CommandManager commandManager)
        {
            // 実際に移動があったノードのみ対象にする
            var moves = new List<(Node Node, Point OldPosition, Point NewPosition)>();
            
            foreach (var kvp in _dragStartPositions)
            {
                var node = kvp.Key;
                var oldPos = kvp.Value;
                var newPos = node.Position;
                
                // 位置が変わっていない場合はスキップ
                if (Math.Abs(oldPos.X - newPos.X) < 0.1 && Math.Abs(oldPos.Y - newPos.Y) < 0.1)
                    continue;
                    
                moves.Add((node, oldPos, newPos));
            }
            
            if (moves.Count == 0)
                return;
                
            if (moves.Count == 1)
            {
                var (node, oldPos, newPos) = moves[0];
                commandManager.RegisterExecuted(new MoveNodeCommand(node, oldPos, newPos));
            }
            else
            {
                commandManager.RegisterExecuted(new MoveNodesCommand(moves.ToArray()));
            }
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

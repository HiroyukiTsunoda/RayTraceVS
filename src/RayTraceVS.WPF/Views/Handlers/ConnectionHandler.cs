using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RayTraceVS.WPF.Commands;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Nodes;
using RayTraceVS.WPF.Utils;
using RayTraceVS.WPF.ViewModels;

namespace RayTraceVS.WPF.Views.Handlers
{
    /// <summary>
    /// ノードエディタの接続線処理を担当するハンドラ
    /// 接続線の作成、プレビュー、削除を処理
    /// Undo/Redo対応
    /// </summary>
    public class ConnectionHandler
    {
        private readonly EditorInputState _state;
        
        // 接続ドラッグ開始時の元接続（置換の場合に使用、ドラッグ中は接続を維持）
        private NodeConnection? _originalConnection;
        // 元接続のソケット情報（RemoveConnectionCommand用）
        private NodeSocket? _originalSocket;
        private int _originalSocketIndex = -1;

        public ConnectionHandler(EditorInputState state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }
        
        /// <summary>
        /// 元接続の情報をクリア
        /// </summary>
        private void ClearOriginalConnectionState()
        {
            _originalConnection = null;
            _originalSocket = null;
            _originalSocketIndex = -1;
        }
        
        /// <summary>
        /// ドラッグ開始時の元接続があるかどうか
        /// </summary>
        public bool HasOriginalConnection => _originalConnection != null;
        
        /// <summary>
        /// ドラッグ開始時の元接続
        /// </summary>
        public NodeConnection? OriginalConnection => _originalConnection;

        /// <summary>
        /// 接続ドラッグを開始（新規接続）
        /// </summary>
        public void StartConnectionDrag(NodeSocket socket, Ellipse? socketElement)
        {
            ClearOriginalConnectionState();
            _state.IsDraggingConnection = true;
            _state.DraggedSocket = socket;
            _state.DraggedSocketElement = socketElement;
            CreatePreviewLine(socket);
        }
        
        /// <summary>
        /// 接続ドラッグを開始（プレビュー線の開始位置を明示的に指定、新規接続）
        /// </summary>
        public void StartConnectionDrag(NodeSocket socket, Ellipse? socketElement, Point startPosition)
        {
            ClearOriginalConnectionState();
            _state.IsDraggingConnection = true;
            _state.DraggedSocket = socket;
            _state.DraggedSocketElement = socketElement;
            CreatePreviewLine(socket);
            
            // 開始位置を明示的に設定
            if (_state.PreviewLine != null)
            {
                _state.PreviewLine.X1 = startPosition.X;
                _state.PreviewLine.Y1 = startPosition.Y;
                _state.PreviewLine.X2 = startPosition.X;
                _state.PreviewLine.Y2 = startPosition.Y;
            }
        }
        
        /// <summary>
        /// 既存接続からドラッグを開始（接続は削除せず記憶のみ）
        /// </summary>
        /// <param name="existingConnection">ドラッグ対象の既存接続</param>
        /// <param name="outputSocket">出力ソケット（ドラッグ元）</param>
        /// <param name="socketElement">ソケットのUI要素</param>
        /// <param name="startPosition">プレビュー線の開始位置</param>
        public void StartConnectionDragFromExisting(
            NodeConnection existingConnection, 
            NodeSocket outputSocket, 
            Ellipse? socketElement, 
            Point startPosition)
        {
            // 元接続を記憶（削除はしない）
            _originalConnection = existingConnection;
            
            // SceneNodeの動的ソケットの場合、ソケット情報を記録（Undo用）
            if (existingConnection.InputSocket?.ParentNode is SceneNode sceneNode)
            {
                _originalSocket = existingConnection.InputSocket;
                _originalSocketIndex = sceneNode.InputSockets.IndexOf(_originalSocket);
            }
            else
            {
                _originalSocket = null;
                _originalSocketIndex = -1;
            }
            
            _state.IsDraggingConnection = true;
            _state.DraggedSocket = outputSocket;
            _state.DraggedSocketElement = socketElement;
            CreatePreviewLine(outputSocket);
            
            // 開始位置を明示的に設定
            if (_state.PreviewLine != null)
            {
                _state.PreviewLine.X1 = startPosition.X;
                _state.PreviewLine.Y1 = startPosition.Y;
                _state.PreviewLine.X2 = startPosition.X;
                _state.PreviewLine.Y2 = startPosition.Y;
            }
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
        /// 接続ドラッグを終了し、接続を作成（コマンド発行なし、レガシー用）
        /// </summary>
        public void EndConnectionDrag(NodeSocket? targetSocket, MainViewModel? viewModel, Action<NodeConnection>? onConnectionCreated)
        {
            EndConnectionDragWithCommand(targetSocket, viewModel, null, onConnectionCreated);
        }
        
        /// <summary>
        /// 接続ドラッグを終了し、接続を作成（コマンド発行あり）
        /// </summary>
        /// <param name="targetSocket">接続先ソケット（nullの場合は接続削除）</param>
        /// <param name="viewModel">ViewModel</param>
        /// <param name="commandManager">コマンドマネージャ</param>
        /// <param name="onConnectionCreated">接続作成時のコールバック</param>
        /// <returns>接続が作成されたかどうか</returns>
        public bool EndConnectionDragWithCommand(
            NodeSocket? targetSocket, 
            MainViewModel? viewModel, 
            CommandManager? commandManager,
            Action<NodeConnection>? onConnectionCreated)
        {
            if (!_state.IsDraggingConnection)
                return false;

            bool connectionCreated = false;
            
            if (targetSocket != null && _state.DraggedSocket != null && viewModel != null)
            {
                // 接続を作成
                var connection = TryCreateConnectionWithCommand(
                    _state.DraggedSocket, 
                    targetSocket, 
                    viewModel, 
                    commandManager);
                    
                if (connection != null)
                {
                    connectionCreated = true;
                    onConnectionCreated?.Invoke(connection);
                }
            }
            else if (_originalConnection != null && viewModel != null)
            {
                // 何もない場所にドロップした場合、元接続を削除
                CommitConnectionRemoval(viewModel, commandManager);
            }

            _state.IsDraggingConnection = false;
            _state.DraggedSocket = null;
            _state.DraggedSocketElement = null;
            RemovePreviewLine();
            ClearOriginalConnectionState();
            
            return connectionCreated;
        }

        /// <summary>
        /// 接続ドラッグをキャンセル（元接続は維持される）
        /// </summary>
        public void CancelConnectionDrag()
        {
            _state.IsDraggingConnection = false;
            _state.DraggedSocket = null;
            _state.DraggedSocketElement = null;
            RemovePreviewLine();
            // 元接続は削除していないので復元不要、情報をクリアするだけ
            ClearOriginalConnectionState();
        }
        
        /// <summary>
        /// 何もない場所にドロップした場合、元接続を削除（コマンド発行）
        /// </summary>
        private void CommitConnectionRemoval(MainViewModel viewModel, CommandManager? commandManager)
        {
            if (_originalConnection == null) return;
            
            if (commandManager != null)
            {
                // コマンドとして実行（Undo可能）
                commandManager.Execute(new RemoveConnectionCommand(viewModel, _originalConnection));
            }
            else
            {
                // コマンドなしで削除
                viewModel.RemoveConnection(_originalConnection);
            }
        }

        /// <summary>
        /// 接続を作成（ソケットの互換性チェック付き、コマンドなし）
        /// </summary>
        private NodeConnection? TryCreateConnection(NodeSocket source, NodeSocket target, MainViewModel viewModel)
        {
            return TryCreateConnectionWithCommand(source, target, viewModel, null);
        }
        
        /// <summary>
        /// 接続を作成（ソケットの互換性チェック付き、コマンド発行）
        /// </summary>
        private NodeConnection? TryCreateConnectionWithCommand(
            NodeSocket source, 
            NodeSocket target, 
            MainViewModel viewModel,
            CommandManager? commandManager)
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

            // 接続を作成
            var connection = new NodeConnection(outputSocket, inputSocket);
            
            if (commandManager != null)
            {
                // 元接続がある場合は置換コマンドを使用
                if (_originalConnection != null && _originalConnection.InputSocket == inputSocket)
                {
                    // 元接続と同じ入力ソケットへの再接続（置換）
                    commandManager.Execute(new ReplaceConnectionCommand(viewModel, _originalConnection, connection));
                }
                else if (_originalConnection != null)
                {
                    // 元接続とは異なる入力ソケットへの接続
                    // 元接続を削除し、新接続を追加（別々のコマンド）
                    commandManager.Execute(new RemoveConnectionCommand(viewModel, _originalConnection));
                    
                    // 入力ソケットに既存接続があれば削除
                    var existingConnection = FindExistingInputConnection(inputSocket, viewModel);
                    if (existingConnection != null)
                    {
                        commandManager.Execute(new RemoveConnectionCommand(viewModel, existingConnection));
                    }
                    
                    commandManager.Execute(new AddConnectionCommand(viewModel, connection));
                }
                else
                {
                    // 新規接続
                    // 入力ソケットに既存接続があれば削除
                    var existingConnection = FindExistingInputConnection(inputSocket, viewModel);
                    if (existingConnection != null)
                    {
                        commandManager.Execute(new RemoveConnectionCommand(viewModel, existingConnection));
                    }
                    
                    commandManager.Execute(new AddConnectionCommand(viewModel, connection));
                }
            }
            else
            {
                // コマンドなしの場合（レガシー）
                RemoveExistingInputConnection(inputSocket, viewModel);
                viewModel.AddConnection(connection);
            }

            return connection;
        }
        
        /// <summary>
        /// 入力ソケットの既存接続を検索
        /// </summary>
        private NodeConnection? FindExistingInputConnection(NodeSocket inputSocket, MainViewModel viewModel)
        {
            foreach (var conn in viewModel.Connections)
            {
                if (conn.InputSocket == inputSocket)
                {
                    return conn;
                }
            }
            return null;
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

            // PreviewLayerに追加（最上層に表示）
            _state.PreviewLayer?.Children.Add(_state.PreviewLine);
        }

        private void UpdatePreviewLine(Point endPoint)
        {
            if (_state.PreviewLine == null) return;

            _state.PreviewLine.X2 = endPoint.X;
            _state.PreviewLine.Y2 = endPoint.Y;
        }

        /// <summary>
        /// プレビュー線の色をデフォルトにリセット
        /// </summary>
        public void ResetPreviewLineColor()
        {
            if (_state.PreviewLine == null || _state.DraggedSocket == null)
                return;

            var socketColor = _state.DraggedSocket.SocketColor as SolidColorBrush;
            _state.PreviewLine.Stroke = socketColor ?? BrushCache.Get(0x00, 0x7A, 0xCC);
            _state.PreviewLine.Opacity = 1.0;
        }

        /// <summary>
        /// プレビュー線を削除
        /// </summary>
        public void RemovePreviewLine()
        {
            if (_state.PreviewLine != null && _state.PreviewLayer != null)
            {
                _state.PreviewLayer.Children.Remove(_state.PreviewLine);
                _state.PreviewLine = null;
            }
        }
        
        /// <summary>
        /// プレビュー線の開始位置を設定
        /// </summary>
        public void SetPreviewLineStart(Point position)
        {
            if (_state.PreviewLine == null) return;
            _state.PreviewLine.X1 = position.X;
            _state.PreviewLine.Y1 = position.Y;
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

using System;
using System.Linq;
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
        /// 何もない場所にドロップした場合、ドラッグ元の既存接続を削除（Undo可能）
        /// </summary>
        public void RemoveOriginalConnection(MainViewModel? viewModel)
        {
            if (_originalConnection != null && viewModel != null)
            {
                viewModel.CommandManager.Execute(new RemoveConnectionCommand(viewModel, _originalConnection));
            }
        }

        /// <summary>
        /// ドラッグ終了時の接続作成。方向判定・型互換チェック・既存/元接続の置換コマンド発行・
        /// SceneNodeの動的ソケット自動追加までを行う（旧NodeEditorView.CreateConnectionのロジック部分）。
        /// </summary>
        /// <param name="source">ドラッグ元ソケット</param>
        /// <param name="target">ドロップ先ソケット</param>
        /// <param name="viewModel">ViewModel</param>
        /// <param name="prepareSocketPositions">接続線の初期描画位置を整えるUI処理（output, inputの順で渡る）</param>
        /// <param name="refreshSceneNodeLayout">SceneNodeへソケットを自動追加した際のUIレイアウト更新</param>
        /// <returns>接続が作成されたかどうか</returns>
        public bool CreateConnectionFromDrag(
            NodeSocket source,
            NodeSocket target,
            MainViewModel viewModel,
            Action<NodeSocket, NodeSocket> prepareSocketPositions,
            Action<SceneNode> refreshSceneNodeLayout)
        {
            // 出力→入力の接続のみ許可
            NodeSocket outputSocket, inputSocket;
            if (!source.IsInput && target.IsInput)
            {
                outputSocket = source;
                inputSocket = target;
            }
            else if (source.IsInput && !target.IsInput)
            {
                outputSocket = target;
                inputSocket = source;
            }
            else
            {
                return false; // 無効な接続
            }

            // 同じノード間の接続は禁止
            if (outputSocket.ParentNode == inputSocket.ParentNode)
                return false;

            // 型チェック: ソケットの型が互換性があるか確認
            if (!AreSocketsCompatible(outputSocket, inputSocket))
                return false;

            // UI側でソケット位置を確定させる（接続線が正しい位置に描画されるように）
            prepareSocketPositions(outputSocket, inputSocket);

            // 新しい接続を作成（ソケット位置が設定された後なので正しく描画される）
            var connection = new NodeConnection(outputSocket, inputSocket);

            // ドラッグ開始時の元接続を取得
            var originalConnection = _originalConnection;

            // 既存の接続を確認（入力ソケットには1つの接続のみ）
            // ただし、ドラッグ開始時の元接続（OriginalConnection）は除外
            var existingConnection = viewModel.Connections.FirstOrDefault(c =>
                c.InputSocket == inputSocket && c != originalConnection);

            if (existingConnection != null)
            {
                // 既存接続がある場合は置換コマンドを使用
                viewModel.CommandManager.Execute(new ReplaceConnectionCommand(viewModel, existingConnection, connection));

                // 元接続がある場合（別のソケットへの接続）は元接続も削除
                if (originalConnection != null)
                {
                    viewModel.CommandManager.Execute(new RemoveConnectionCommand(viewModel, originalConnection));
                }
            }
            else if (originalConnection != null && originalConnection.InputSocket == inputSocket)
            {
                // ドラッグ開始時の元接続と同じ入力ソケットへの再接続の場合は置換コマンドを使用
                viewModel.CommandManager.Execute(new ReplaceConnectionCommand(viewModel, originalConnection, connection));
            }
            else if (originalConnection != null)
            {
                // ドラッグ開始時の元接続とは異なる入力ソケットへの接続
                // 元接続を削除し、新接続を追加
                viewModel.CommandManager.Execute(new RemoveConnectionCommand(viewModel, originalConnection));
                viewModel.CommandManager.Execute(new AddConnectionCommand(viewModel, connection));
            }
            else
            {
                // 新規接続
                viewModel.CommandManager.Execute(new AddConnectionCommand(viewModel, connection));
            }

            // 明示的に接続線を描画
            connection.UpdatePath();

            // シーンノードの場合、空きソケットがなければ自動的に次のソケットを追加
            if (inputSocket.ParentNode is SceneNode sceneNode)
            {
                bool socketAdded = false;
                if (inputSocket.SocketType == SocketType.Object)
                {
                    bool hasEmptyObjectSocket = sceneNode.InputSockets.Any(s =>
                        s.SocketType == SocketType.Object &&
                        !viewModel.Connections.Any(c => c.InputSocket == s));

                    if (!hasEmptyObjectSocket)
                    {
                        sceneNode.AddObjectSocket();
                        socketAdded = true;
                    }
                }
                else if (inputSocket.SocketType == SocketType.Light)
                {
                    bool hasEmptyLightSocket = sceneNode.InputSockets.Any(s =>
                        s.SocketType == SocketType.Light &&
                        !viewModel.Connections.Any(c => c.InputSocket == s));

                    if (!hasEmptyLightSocket)
                    {
                        sceneNode.AddLightSocket();
                        socketAdded = true;
                    }
                }

                if (socketAdded)
                {
                    refreshSceneNodeLayout(sceneNode);
                }
            }

            return true;
        }

        /// <summary>
        /// ソケットの型互換性をチェック。
        /// 実際の接続作成（CreateConnectionFromDrag）とプレビュー線の色表示の両方で使われる唯一のルール。
        /// </summary>
        public bool AreSocketsCompatible(NodeSocket output, NodeSocket input)
        {
            var outputType = output.SocketType;
            var inputType = input.SocketType;

            // 基本ルール: 同じ型同士は接続可能
            if (outputType == inputType)
                return true;

            // 特殊ルール: Object入力ソケットは様々なオブジェクト型を受け入れる
            // ただし、基本的なデータ型（Vector3、Float、Color）やシステム型（Camera、Light、Scene）は除外
            if (inputType == SocketType.Object)
            {
                return outputType != SocketType.Vector3 &&
                       outputType != SocketType.Float &&
                       outputType != SocketType.Color &&
                       outputType != SocketType.Camera &&
                       outputType != SocketType.Light &&
                       outputType != SocketType.Scene;
            }

            // 互換性がない
            return false;
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
                // 互換性あり：出力ソケットの色（実際の接続線と同じ色）、実線
                // ドラッグ中のソケットが出力の場合はその色、入力の場合はターゲット（出力）の色
                Brush connectionColor;
                if (!_state.DraggedSocket.IsInput)
                {
                    connectionColor = _state.DraggedSocket.SocketColor ?? BrushCache.Get(0x00, 0x7A, 0xCC);
                }
                else
                {
                    connectionColor = targetSocket.SocketColor ?? BrushCache.Get(0x00, 0x7A, 0xCC);
                }
                _state.PreviewLine.Stroke = connectionColor;
                _state.PreviewLine.Opacity = 1.0;
                _state.PreviewLine.StrokeDashArray = null;
            }
            else
            {
                // 互換性なし：赤色、点線
                _state.PreviewLine.Stroke = BrushCache.Get(Colors.Red);
                _state.PreviewLine.Opacity = 0.5;
                _state.PreviewLine.StrokeDashArray = new DoubleCollection { 5, 3 };
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
            _state.PreviewLine.StrokeDashArray = new DoubleCollection { 5, 3 };
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

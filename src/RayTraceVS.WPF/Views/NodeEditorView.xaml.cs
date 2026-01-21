using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RayTraceVS.WPF.Commands;
using RayTraceVS.WPF.ViewModels;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Nodes;
using RayTraceVS.WPF.Utils;

namespace RayTraceVS.WPF.Views
{
    public partial class NodeEditorView : UserControl
    {
        private Point lastMousePosition;
        private bool isPanning = false;
        private bool isDraggingNode = false;
        private bool isDraggingConnection = false;
        private bool isRectSelecting = false;
        private Node? draggedNode = null;
        private NodeSocket? draggedSocket = null;
        private Ellipse? draggedSocketElement = null;
        private Point dragStartOffset;
        private Line? previewLine = null;
        
        // 複数選択関連
        private HashSet<Node> selectedNodes = new HashSet<Node>();
        private Point rectSelectStartPoint;
        private Rectangle? selectionRectangle = null;
        private Dictionary<Node, Point> multiDragOffsets = new Dictionary<Node, Point>();
        
        // ドラッグ開始時の位置を記録（Undo用）
        private Dictionary<Node, Point> dragStartPositions = new Dictionary<Node, Point>();
        
        // 接続ドラッグ開始時に一時削除した接続（キャンセル時に復元するため）
        private NodeConnection? _tempRemovedConnection = null;
        // 一時削除時にSceneNodeから削除されたソケット情報
        private NodeSocket? _tempRemovedSocket = null;
        private int _tempRemovedSocketIndex = -1;
        
        // パラメーター変更のUndo用（TextBoxのフォーカス取得時に変更前の値を記録）
        private Dictionary<TextBox, float> _textBoxOriginalValues = new Dictionary<TextBox, float>();
        
        // パン・ズーム用
        private TranslateTransform panTransform = new TranslateTransform();
        private ScaleTransform zoomTransform = new ScaleTransform();
        private TransformGroup transformGroup = new TransformGroup();
        
        private double currentZoom = 1.0;
        private const double MinZoom = 0.1;
        private const double MaxZoom = 5.0;
        private const double ZoomSpeed = 0.001;
        
        // 接続線のPath要素を管理（必ず最後に追加＝最前面に描画）
        private Dictionary<NodeConnection, Path> connectionPaths = new Dictionary<NodeConnection, Path>();

        public NodeEditorView()
        {
            InitializeComponent();
            
            // トランスフォームを設定
            transformGroup.Children.Add(zoomTransform);
            transformGroup.Children.Add(panTransform);
            NodeCanvas.RenderTransform = transformGroup;
            NodeCanvas.RenderTransformOrigin = new Point(0, 0);
            ConnectionLayer.RenderTransform = transformGroup;
            ConnectionLayer.RenderTransformOrigin = new Point(0, 0);
            
            // ロード後にフォーカスを設定
            Loaded += (s, e) =>
            {
                this.Focus(); // UserControlにフォーカスを設定
            };
            
            // マウスクリックでもフォーカスを設定
            MouseDown += (s, e) =>
            {
                this.Focus();
            };
            
            // DataContextの変更を監視
            DataContextChanged += (s, e) =>
            {
                
                // ViewModelの接続変更を監視
                if (e.NewValue is MainViewModel viewModel)
                {
                    viewModel.Connections.CollectionChanged += (_, __) => RebuildConnectionPaths();
                }
            };
        }
        
        /// <summary>
        /// 接続線のPath要素を再構築（ConnectionLayerに追加）
        /// </summary>
        private void RebuildConnectionPaths()
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return;

            // 既存の接続線Pathをすべて削除
            foreach (var path in connectionPaths.Values)
            {
                ConnectionLayer.Children.Remove(path);
            }
            connectionPaths.Clear();

            // 新しい接続線Pathを追加（ConnectionLayerに追加＝ノードの下に描画）
            foreach (var connection in viewModel.Connections)
            {
                var path = new Path
                {
                    Stroke = connection.ConnectionColor,
                    StrokeThickness = 3,
                    Data = connection.PathGeometry,
                    IsHitTestVisible = false,
                    Opacity = 0.9
                };
                
                // ConnectionLayerに追加（ノードの下に描画される）
                ConnectionLayer.Children.Add(path);
                connectionPaths[connection] = path;
                
                // 接続の変更を監視
                connection.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(NodeConnection.PathGeometry) && connectionPaths.TryGetValue(connection, out var p))
                    {
                        p.Data = connection.PathGeometry;
                    }
                };
            }
            
        }

        /// <summary>
        /// ソケットのEllipse要素が読み込まれたとき
        /// </summary>
        private void Socket_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Ellipse ellipse && ellipse.DataContext is NodeSocket socket)
            {
                // Ellipse要素からソケットの実際の位置を取得
                UpdateSocketPositionFromUI(ellipse, socket);
            }
        }

        /// <summary>
        /// ソケットのUI要素から実際の位置を取得して設定
        /// </summary>
        private void UpdateSocketPositionFromUI(Ellipse ellipse, NodeSocket socket)
        {
            try
            {
                // Ellipseの中心のCanvas上の座標を取得
                var transform = ellipse.TransformToAncestor(NodeCanvas);
                var centerPoint = transform.Transform(new Point(ellipse.ActualWidth / 2, ellipse.ActualHeight / 2));
                socket.Position = centerPoint;
            }
            catch (Exception)
            {
                // TransformToAncestor can fail if the element is not in the visual tree
            }
        }

        /// <summary>
        /// すべての接続線を更新（ファイル読み込み後などに使用）
        /// </summary>
        public void RefreshConnectionLines()
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return;

            // シーンノードのソケット数を調整
            EnsureSceneNodeSocketCounts();
            
            // レイアウト更新を強制
            NodeCanvas.UpdateLayout();
            
            // すべてのノードのソケット位置をUIから更新（これにより接続線も自動更新）
            foreach (var node in viewModel.Nodes)
            {
                UpdateAllSocketPositionsForNode(node);
            }
            
            // すべての接続のPathを明示的に更新（Undo/Redoで新しく作成された接続のため）
            foreach (var connection in viewModel.Connections)
            {
                connection.UpdatePath();
            }
            
            // 接続線を再構築（必ず最前面に描画される）
            RebuildConnectionPaths();
        }

        /// <summary>
        /// すべてのノードのTextBox値を更新（Undo/Redo後に使用）
        /// </summary>
        public void RefreshNodeTextBoxValues()
        {
            // Vector3Node, Vector4Node, FloatNodeのTextBox値を更新
            foreach (var textBox in FindVisualChildren<TextBox>(NodeCanvas))
            {
                if (textBox.Tag is NodeSocket socket)
                {
                    if (socket.ParentNode is Vector3Node vector3Node)
                    {
                        textBox.Text = vector3Node.GetSocketValue(socket.Name).ToString("G");
                    }
                    else if (socket.ParentNode is Vector4Node vector4Node)
                    {
                        textBox.Text = vector4Node.GetSocketValue(socket.Name).ToString("G");
                    }
                }
                else if (textBox.DataContext is FloatNode floatNode && floatNode.HasEditableFloat)
                {
                    textBox.Text = floatNode.Value.ToString("G");
                }
            }
        }
        
        /// <summary>
        /// ビューポートの状態を取得
        /// </summary>
        public Services.ViewportState GetViewportState()
        {
            return new Services.ViewportState
            {
                PanX = panTransform.X,
                PanY = panTransform.Y,
                Zoom = currentZoom
            };
        }
        
        /// <summary>
        /// ビューポートの状態を設定
        /// </summary>
        public void SetViewportState(Services.ViewportState? viewportState)
        {
            if (viewportState == null)
                return;
                
            panTransform.X = viewportState.PanX;
            panTransform.Y = viewportState.PanY;
            currentZoom = viewportState.Zoom;
            zoomTransform.ScaleX = currentZoom;
            zoomTransform.ScaleY = currentZoom;
            
            // トランスフォームの変更を即座に反映
            NodeCanvas.UpdateLayout();
            ConnectionLayer.UpdateLayout();
        }
        
        /// <summary>
        /// 現在のビューポート中央のキャンバス座標を取得
        /// </summary>
        public Point GetViewportCenterInCanvas()
        {
            // ビューポート（UserControl）の中央座標
            double viewportCenterX = ActualWidth / 2;
            double viewportCenterY = ActualHeight / 2;
            
            // ビューポート座標をキャンバス座標に変換
            // キャンバス座標 = (ビューポート座標 - パン) / ズーム
            double canvasX = (viewportCenterX - panTransform.X) / currentZoom;
            double canvasY = (viewportCenterY - panTransform.Y) / currentZoom;
            
            return new Point(canvasX, canvasY);
        }

        /// <summary>
        /// シーンノードのソケット数が「接続数+1」になるように調整
        /// </summary>
        private void EnsureSceneNodeSocketCounts()
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return;

            foreach (var node in viewModel.Nodes)
            {
                if (node is Models.Nodes.SceneNode sceneNode)
                {
                    // オブジェクトソケットをチェック
                    var objectSockets = sceneNode.InputSockets.Where(s => s.SocketType == SocketType.Object).ToList();
                    var connectedObjectSockets = objectSockets.Count(s => viewModel.Connections.Any(c => c.InputSocket == s));
                    var emptyObjectSockets = objectSockets.Count - connectedObjectSockets;

                    // 空のソケットが0個なら1個追加
                    if (emptyObjectSockets == 0)
                    {
                        sceneNode.AddObjectSocket();
                    }
                    // 空のソケットが2個以上なら余分を削除
                    else if (emptyObjectSockets > 1)
                    {
                        var emptySockets = objectSockets.Where(s => !viewModel.Connections.Any(c => c.InputSocket == s)).Skip(1).ToList();
                        foreach (var socket in emptySockets)
                        {
                            sceneNode.RemoveSocket(socket.Name);
                        }
                    }

                    // ライトソケットをチェック
                    var lightSockets = sceneNode.InputSockets.Where(s => s.SocketType == SocketType.Light).ToList();
                    var connectedLightSockets = lightSockets.Count(s => viewModel.Connections.Any(c => c.InputSocket == s));
                    var emptyLightSockets = lightSockets.Count - connectedLightSockets;

                    // 空のソケットが0個なら1個追加
                    if (emptyLightSockets == 0)
                    {
                        sceneNode.AddLightSocket();
                    }
                    // 空のソケットが2個以上なら余分を削除
                    else if (emptyLightSockets > 1)
                    {
                        var emptySockets = lightSockets.Where(s => !viewModel.Connections.Any(c => c.InputSocket == s)).Skip(1).ToList();
                        foreach (var socket in emptySockets)
                        {
                            sceneNode.RemoveSocket(socket.Name);
                        }
                    }
                }
            }
        }

        private MainViewModel? GetViewModel()
        {
            return DataContext as MainViewModel;
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // フォーカスされている TextBox があればバインディングを更新してから処理を続ける
            UpdateFocusedTextBoxBinding();
            
            var viewModel = GetViewModel();
            if (viewModel == null) return;

            var mousePos = e.GetPosition(NodeCanvas);
            
            // ノードやソケットをヒットテストで探す
            var hitElement = NodeCanvas.InputHitTest(mousePos) as DependencyObject;
            
            // デバッグ: ヒットした要素を確認
            if (hitElement != null)
            {
                var dataContext = (hitElement as FrameworkElement)?.DataContext;
            }
            
            // ソケットのクリックを検出
            var socket = FindVisualParent<Ellipse>(hitElement);
            if (socket != null && socket.DataContext is NodeSocket nodeSocket)
            {
                
                // 入力ソケットの場合、既存の接続を確認
                if (nodeSocket.IsInput)
                {
                    var existingConnection = viewModel.Connections.FirstOrDefault(c => c.InputSocket == nodeSocket);
                    if (existingConnection != null)
                    {
                        // 既存の接続を削除し、出力ソケット側からドラッグを開始
                        var outputSocket = existingConnection.OutputSocket;
                        
                        if (outputSocket != null)
                        {
                            // 既存の接続線が持っている出力ソケットの位置を使用（最も正確）
                            Point savedOutputSocketPos = outputSocket.Position;
                            
                            draggedSocket = outputSocket;
                            draggedSocketElement = null;
                            
                            // UI要素も探しておく（ドラッグ中の更新用）
                            var outputNode = outputSocket.ParentNode;
                            if (outputNode != null)
                            {
                                var outputNodeContainer = FindNodeContainer(outputNode);
                                if (outputNodeContainer != null)
                                {
                                    draggedSocketElement = FindSocketElement(outputNodeContainer, outputSocket);
                                }
                            }
                            
                            // 接続を一時的に削除（キャンセル時に復元するため）
                            _tempRemovedConnection = existingConnection;
                            
                            // SceneNodeのソケット情報を記録（削除される前に）
                            _tempRemovedSocket = null;
                            _tempRemovedSocketIndex = -1;
                            if (nodeSocket.ParentNode is SceneNode sceneNode1)
                            {
                                _tempRemovedSocketIndex = sceneNode1.InputSockets.IndexOf(nodeSocket);
                            }
                            
                            // ソケット数を記録
                            int socketCountBefore1 = 0;
                            SceneNode? sn1 = nodeSocket.ParentNode as SceneNode;
                            if (sn1 != null) socketCountBefore1 = sn1.InputSockets.Count;
                            
                            viewModel.RemoveConnection(existingConnection);
                            
                            // ソケットが削除されたか確認
                            if (sn1 != null && sn1.InputSockets.Count < socketCountBefore1 && !sn1.InputSockets.Contains(nodeSocket))
                            {
                                _tempRemovedSocket = nodeSocket;
                            }
                            
                            // 接続のドラッグ開始
                            isDraggingConnection = true;
                            CreatePreviewLine(outputSocket);
                            
                            // 既存の接続線が持っていた正確な位置を使用
                            if (previewLine != null)
                            {
                                previewLine.X1 = savedOutputSocketPos.X;
                                previewLine.Y1 = savedOutputSocketPos.Y;
                                previewLine.X2 = savedOutputSocketPos.X;
                                previewLine.Y2 = savedOutputSocketPos.Y;
                            }
                            
                            NodeCanvas.CaptureMouse();
                            e.Handled = true;
                            return;
                        }
                    }
                }
                
                // 接続のドラッグ開始（通常の新規接続）
                isDraggingConnection = true;
                draggedSocket = nodeSocket;
                draggedSocketElement = socket;
                ClearTempRemovedConnectionState();  // 新規接続なので一時削除した接続はない
                CreatePreviewLine(nodeSocket);
                
                // 初期位置を設定
                var socketPos = GetSocketElementPosition(socket);
                if (previewLine != null)
                {
                    previewLine.X1 = socketPos.X;
                    previewLine.Y1 = socketPos.Y;
                    previewLine.X2 = socketPos.X;
                    previewLine.Y2 = socketPos.Y;
                }
                
                NodeCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }
            
            // ノードのクリックを検出
            var border = FindVisualParent<Border>(hitElement);
            
            if (border != null && border.DataContext is Node node)
            {
                
                // 既に選択されているノードをクリックした場合は複数選択を維持
                if (selectedNodes.Contains(node))
                {
                    // 複数選択されている場合は全てのノードのオフセットを計算
                    isDraggingNode = true;
                    draggedNode = node;
                    multiDragOffsets.Clear();
                    dragStartPositions.Clear();
                    
                    foreach (var selectedNode in selectedNodes)
                    {
                        multiDragOffsets[selectedNode] = new Point(
                            mousePos.X - selectedNode.Position.X,
                            mousePos.Y - selectedNode.Position.Y
                        );
                        // ドラッグ開始位置を記録（Undo用）
                        dragStartPositions[selectedNode] = selectedNode.Position;
                    }
                    
                    NodeCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
                
                // 新しいノードを単一選択
                ClearAllSelections(viewModel);
                
                node.IsSelected = true;
                selectedNodes.Add(node);
                viewModel.SelectedNode = node; // プロパティパネル用に設定
                
                isDraggingNode = true;
                draggedNode = node;
                
                // 常にmultiDragOffsetsを使用（単一選択でも統一）
                multiDragOffsets.Clear();
                multiDragOffsets[node] = new Point(mousePos.X - node.Position.X, mousePos.Y - node.Position.Y);
                
                // ドラッグ開始位置を記録（Undo用）
                dragStartPositions.Clear();
                dragStartPositions[node] = node.Position;
                
                // フォールバック用にdragStartOffsetも設定
                dragStartOffset = new Point(mousePos.X - node.Position.X, mousePos.Y - node.Position.Y);
                
                NodeCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }
            
            // 何もない場所をクリックした場合は矩形選択開始
            ClearAllSelections(viewModel);
            
            // 矩形選択開始
            isRectSelecting = true;
            rectSelectStartPoint = mousePos;
            CreateSelectionRectangle(mousePos);
            NodeCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // 矩形選択終了
            if (isRectSelecting)
            {
                var viewModel = GetViewModel();
                if (viewModel != null)
                {
                    var mousePos = e.GetPosition(NodeCanvas);
                    SelectNodesInRectangle(viewModel, rectSelectStartPoint, mousePos);
                }
                
                RemoveSelectionRectangle();
                isRectSelecting = false;
                NodeCanvas.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }
            
            if (isDraggingConnection && draggedSocket != null)
            {
                // 接続先のソケットを探す（拡張ヒット判定）
                var mousePos = e.GetPosition(NodeCanvas);
                var (targetElement, targetNodeSocket) = FindNearestSocket(mousePos);
                
                if (targetNodeSocket != null)
                {
                    // 接続を作成（出力→入力のみ許可）
                    CreateConnection(draggedSocket, targetNodeSocket);
                }
                else
                {
                    // 何もない場所にドロップした場合、接続を削除（Undo可能）
                    CommitTempRemovedConnection();
                }
                
                // プレビュー線を削除
                RemovePreviewLine();
            }
            
            // ノードドラッグ終了時の処理
            if (isDraggingNode)
            {
                FinishNodeDrag();
            }
            
            // ドラッグ状態をリセット
            isDraggingNode = false;
            isDraggingConnection = false;
            draggedNode = null;
            draggedSocket = null;
            multiDragOffsets.Clear();
            dragStartPositions.Clear();
            NodeCanvas.ReleaseMouseCapture();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            // 矩形選択中
            if (isRectSelecting && e.LeftButton == MouseButtonState.Pressed)
            {
                var mousePos = e.GetPosition(NodeCanvas);
                UpdateSelectionRectangle(mousePos);
                e.Handled = true;
                return;
            }
            
            // ノードのドラッグ（複数選択対応）
            if (isDraggingNode && draggedNode != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var mousePos = e.GetPosition(NodeCanvas);
                
                // multiDragOffsetsを使用（単一・複数の両方に対応）
                if (multiDragOffsets.Count > 0)
                {
                    foreach (var kvp in multiDragOffsets)
                    {
                        var node = kvp.Key;
                        var offset = kvp.Value;
                        
                        node.Position = new Point(
                            mousePos.X - offset.X,
                            mousePos.Y - offset.Y
                        );
                        UpdateSocketPositionsFromNodePosition(node);
                    }
                }
                else
                {
                    // フォールバック：単一ノードの移動（dragStartOffsetを使用）
                    draggedNode.Position = new Point(
                        mousePos.X - dragStartOffset.X,
                        mousePos.Y - dragStartOffset.Y
                    );
                    UpdateSocketPositionsFromNodePosition(draggedNode);
                }
                
                // レイアウト更新を強制
                NodeCanvas.UpdateLayout();
                
                // 接続線を更新
                IEnumerable<Node> nodesToUpdate = multiDragOffsets.Count > 0 ? multiDragOffsets.Keys : new[] { draggedNode };
                foreach (var node in nodesToUpdate)
                {
                    UpdateNodeConnections(node);
                }
                
                e.Handled = true;
                return;
            }
            
            // 接続のドラッグ中
            if (isDraggingConnection && draggedSocket != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var mousePos = e.GetPosition(NodeCanvas);
                UpdatePreviewLine(mousePos);
                
                // ホバーしているソケットをチェックして、互換性を表示（拡張ヒット判定）
                var (targetElement, targetNodeSocket) = FindNearestSocket(mousePos);
                if (targetNodeSocket != null)
                {
                    UpdatePreviewLineCompatibility(targetNodeSocket);
                }
                else
                {
                    // ソケットから離れた場合はデフォルトの色に戻す
                    ResetPreviewLineColor();
                }
                
                e.Handled = true;
                return;
            }
            
            // パン操作
            if (isPanning && e.RightButton == MouseButtonState.Pressed)
            {
                Point currentPosition = e.GetPosition(this);
                Vector delta = currentPosition - lastMousePosition;
                
                panTransform.X += delta.X;
                panTransform.Y += delta.Y;
                
                lastMousePosition = currentPosition;
                e.Handled = true;
            }
        }

        private void Canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // パン開始
            isPanning = true;
            lastMousePosition = e.GetPosition(this);
            NodeCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // マウス位置をビューポート（UserControl）に対して取得
            var mousePos = e.GetPosition(this);
            
            double zoomDelta = e.Delta * ZoomSpeed;
            double newZoom = currentZoom + zoomDelta;
            newZoom = Math.Max(MinZoom, Math.Min(MaxZoom, newZoom));
            
            if (newZoom != currentZoom)
            {
                // マウスカーソル位置のキャンバス座標を計算（ズーム前）
                double canvasX = (mousePos.X - panTransform.X) / currentZoom;
                double canvasY = (mousePos.Y - panTransform.Y) / currentZoom;
                
                // 新しいズームレベルを適用
                zoomTransform.ScaleX = newZoom;
                zoomTransform.ScaleY = newZoom;
                
                // 同じキャンバス座標がマウスの下に来るようにパンを調整
                panTransform.X = mousePos.X - canvasX * newZoom;
                panTransform.Y = mousePos.Y - canvasY * newZoom;
                
                currentZoom = newZoom;
            }
            
            e.Handled = true;
        }

        private void Canvas_KeyDown(object sender, KeyEventArgs e)
        {
            HandleDeleteKey(e);
        }
        
        private void UserControl_KeyDown(object sender, KeyEventArgs e)
        {
            HandleDeleteKey(e);
        }
        
        private void HandleDeleteKey(KeyEventArgs e)
        {
            // Deleteキーでノードを削除（複数選択対応）
            if (e.Key == Key.Delete && selectedNodes.Count > 0)
            {
                DeleteSelectedNodesInternal();
                e.Handled = true;
            }
        }
        
        /// <summary>
        /// 選択されたノードを削除（MainWindowから呼び出し用）
        /// </summary>
        public void DeleteSelectedNodes()
        {
            if (selectedNodes.Count > 0)
            {
                DeleteSelectedNodesInternal();
            }
        }

        /// <summary>
        /// 選択されたノードを削除する内部処理（コマンド経由）
        /// </summary>
        private void DeleteSelectedNodesInternal()
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return;

            var nodesToDelete = selectedNodes.ToList();
            ClearAllSelections(viewModel);

            if (nodesToDelete.Count == 1)
            {
                // 単一ノード削除
                viewModel.CommandManager.Execute(new RemoveNodeCommand(viewModel, nodesToDelete[0]));
            }
            else if (nodesToDelete.Count > 1)
            {
                // 複数ノード削除 - CompositeCommandでまとめる
                var composite = new CompositeCommand($"{nodesToDelete.Count}個のノードを削除");
                foreach (var node in nodesToDelete)
                {
                    composite.Add(new RemoveNodeCommand(viewModel, node));
                }
                viewModel.CommandManager.Execute(composite);
            }
        }

        // ノード上でのマウスイベント
        private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // フォーカスされている TextBox があればバインディングを更新してから処理を続ける
            UpdateFocusedTextBoxBinding();
            
            var viewModel = GetViewModel();
            if (viewModel == null) return;

            var border = sender as Border;
            
            if (border == null || !(border.DataContext is Node node)) return;


            var mousePos = e.GetPosition(NodeCanvas);
            
            // ソケットのクリックかどうか確認
            var hitElement = border.InputHitTest(e.GetPosition(border)) as DependencyObject;
            var socket = FindVisualParent<Ellipse>(hitElement);
            if (socket != null && socket.DataContext is NodeSocket nodeSocket)
            {
                
                // 入力ソケットの場合、既存の接続を確認
                if (nodeSocket.IsInput)
                {
                    var existingConnection = viewModel.Connections.FirstOrDefault(c => c.InputSocket == nodeSocket);
                    if (existingConnection != null)
                    {
                        // 既存の接続を削除し、出力ソケット側からドラッグを開始
                        var outputSocket = existingConnection.OutputSocket;
                        
                        if (outputSocket != null)
                        {
                            // 既存の接続線が持っている出力ソケットの位置を使用（最も正確）
                            Point savedOutputSocketPos = outputSocket.Position;
                            
                            draggedSocket = outputSocket;
                            draggedSocketElement = null;
                            
                            // UI要素も探しておく（ドラッグ中の更新用）
                            var outputNode = outputSocket.ParentNode;
                            if (outputNode != null)
                            {
                                var outputNodeContainer = FindNodeContainer(outputNode);
                                if (outputNodeContainer != null)
                                {
                                    draggedSocketElement = FindSocketElement(outputNodeContainer, outputSocket);
                                }
                            }
                            
                            // 接続を一時的に削除（キャンセル時に復元するため）
                            _tempRemovedConnection = existingConnection;
                            
                            // SceneNodeのソケット情報を記録（削除される前に）
                            _tempRemovedSocket = null;
                            _tempRemovedSocketIndex = -1;
                            if (nodeSocket.ParentNode is SceneNode sceneNode2)
                            {
                                _tempRemovedSocketIndex = sceneNode2.InputSockets.IndexOf(nodeSocket);
                            }
                            
                            // ソケット数を記録
                            int socketCountBefore2 = 0;
                            SceneNode? sn2 = nodeSocket.ParentNode as SceneNode;
                            if (sn2 != null) socketCountBefore2 = sn2.InputSockets.Count;
                            
                            viewModel.RemoveConnection(existingConnection);
                            
                            // ソケットが削除されたか確認
                            if (sn2 != null && sn2.InputSockets.Count < socketCountBefore2 && !sn2.InputSockets.Contains(nodeSocket))
                            {
                                _tempRemovedSocket = nodeSocket;
                            }
                            
                            // 接続のドラッグ開始
                            isDraggingConnection = true;
                            CreatePreviewLine(outputSocket);
                            
                            // 既存の接続線が持っていた正確な位置を使用
                            if (previewLine != null)
                            {
                                previewLine.X1 = savedOutputSocketPos.X;
                                previewLine.Y1 = savedOutputSocketPos.Y;
                                previewLine.X2 = savedOutputSocketPos.X;
                                previewLine.Y2 = savedOutputSocketPos.Y;
                            }
                            
                            // Canvasにマウスをキャプチャさせる（ノード全体ではなく）
                            NodeCanvas.CaptureMouse();
                            e.Handled = true;
                            return;
                        }
                    }
                }
                
                // 接続のドラッグ開始（通常の新規接続）
                isDraggingConnection = true;
                draggedSocket = nodeSocket;
                draggedSocketElement = socket;
                _tempRemovedConnection = null;  // 新規接続なので一時削除した接続はない
                CreatePreviewLine(nodeSocket);
                
                // 初期位置を設定
                var socketPos = GetSocketElementPosition(socket);
                if (previewLine != null)
                {
                    previewLine.X1 = socketPos.X;
                    previewLine.Y1 = socketPos.Y;
                    previewLine.X2 = socketPos.X;
                    previewLine.Y2 = socketPos.Y;
                }
                
                // Canvasにマウスをキャプチャさせる（ノード全体ではなく）
                NodeCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            // 既に選択されているノードをクリックした場合は複数選択を維持
            if (selectedNodes.Contains(node))
            {
                // 複数選択されている場合は全てのノードのオフセットを計算
                isDraggingNode = true;
                draggedNode = node;
                multiDragOffsets.Clear();
                dragStartPositions.Clear();
                
                foreach (var selectedNode in selectedNodes)
                {
                    multiDragOffsets[selectedNode] = new Point(
                        mousePos.X - selectedNode.Position.X,
                        mousePos.Y - selectedNode.Position.Y
                    );
                    // ドラッグ開始位置を記録（Undo用）
                    dragStartPositions[selectedNode] = selectedNode.Position;
                }
                
                NodeCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }
            
            // 新しいノードを単一選択
            ClearAllSelections(viewModel);
            
            node.IsSelected = true;
            selectedNodes.Add(node);
            viewModel.SelectedNode = node; // プロパティパネル用に設定
            
            isDraggingNode = true;
            draggedNode = node;
            
            // 常にmultiDragOffsetsを使用（単一選択でも統一）
            multiDragOffsets.Clear();
            multiDragOffsets[node] = new Point(mousePos.X - node.Position.X, mousePos.Y - node.Position.Y);
            
            // ドラッグ開始位置を記録（Undo用）
            dragStartPositions.Clear();
            dragStartPositions[node] = node.Position;
            
            // フォールバック用にdragStartOffsetも設定
            dragStartOffset = new Point(mousePos.X - node.Position.X, mousePos.Y - node.Position.Y);
            
            // NodeCanvasでマウスキャプチャ（Borderでキャプチャするとそのノードが最前面に来てしまう）
            NodeCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void Node_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            if (border == null) return;


            if (isDraggingConnection && draggedSocket != null)
            {
                // 接続先のソケットを探す（拡張ヒット判定）
                var mousePos = e.GetPosition(NodeCanvas);
                var (targetElement, targetNodeSocket) = FindNearestSocket(mousePos);
                
                if (targetNodeSocket != null)
                {
                    CreateConnection(draggedSocket, targetNodeSocket);
                }
                else
                {
                    // 何もない場所にドロップした場合、接続を削除（Undo可能）
                    CommitTempRemovedConnection();
                }
                
                // プレビュー線を削除
                RemovePreviewLine();
                
                // ドラッグ状態をリセット
                isDraggingConnection = false;
                draggedSocket = null;
                draggedSocketElement = null;
                NodeCanvas.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }
            
            if (isDraggingNode)
            {
                FinishNodeDrag();
                isDraggingNode = false;
                draggedNode = null;
                multiDragOffsets.Clear();
                dragStartPositions.Clear();
                // NodeCanvasでマウスリリース
                NodeCanvas.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        /// <summary>
        /// ノードドラッグ終了時の処理（移動コマンドを発行）
        /// </summary>
        private void FinishNodeDrag()
        {
            var viewModel = GetViewModel();
            if (viewModel == null || dragStartPositions.Count == 0) return;

            // 移動したノードを収集
            var movedNodes = new List<(Node Node, Point OldPosition, Point NewPosition)>();
            foreach (var kvp in dragStartPositions)
            {
                var node = kvp.Key;
                var startPos = kvp.Value;
                if (node.Position != startPos)
                {
                    movedNodes.Add((node, startPos, node.Position));
                }
            }

            if (movedNodes.Count == 0) return;

            if (movedNodes.Count == 1)
            {
                // 単一ノード移動
                var (node, oldPos, newPos) = movedNodes[0];
                viewModel.CommandManager.RegisterExecuted(new MoveNodeCommand(node, oldPos, newPos));
            }
            else
            {
                // 複数ノード移動
                var moves = movedNodes.ToArray();
                viewModel.CommandManager.RegisterExecuted(new MoveNodesCommand(moves));
            }
        }

        /// <summary>
        /// 接続ドラッグがキャンセルされた場合（Escapeキー）、一時削除した接続を復元
        /// </summary>
        private void RestoreTempRemovedConnection()
        {
            if (_tempRemovedConnection == null) return;

            var viewModel = GetViewModel();
            if (viewModel == null)
            {
                ClearTempRemovedConnectionState();
                return;
            }

            // SceneNodeのソケットを復元（削除されていた場合）
            if (_tempRemovedSocket != null && _tempRemovedSocket.ParentNode is SceneNode sceneNode)
            {
                if (!sceneNode.InputSockets.Contains(_tempRemovedSocket))
                {
                    if (_tempRemovedSocketIndex >= 0 && _tempRemovedSocketIndex <= sceneNode.InputSockets.Count)
                    {
                        sceneNode.InputSockets.Insert(_tempRemovedSocketIndex, _tempRemovedSocket);
                    }
                    else
                    {
                        sceneNode.InputSockets.Add(_tempRemovedSocket);
                    }
                }
            }

            // 元の接続を復元（履歴に残さない）
            viewModel.AddConnection(_tempRemovedConnection);
            ClearTempRemovedConnectionState();
        }

        /// <summary>
        /// 接続ドラッグで何もない場所にドロップした場合、接続削除をコマンドとして記録
        /// </summary>
        private void CommitTempRemovedConnection()
        {
            if (_tempRemovedConnection == null) return;

            var viewModel = GetViewModel();
            if (viewModel == null)
            {
                ClearTempRemovedConnectionState();
                return;
            }

            // 接続削除をコマンドとして記録（Undo可能）
            // 注: 接続は既に削除されているので、RegisterExecutedを使用
            // ソケット情報も渡す
            viewModel.CommandManager.RegisterExecuted(
                new RemoveConnectionCommand(viewModel, _tempRemovedConnection, _tempRemovedSocket, _tempRemovedSocketIndex));
            ClearTempRemovedConnectionState();
        }

        /// <summary>
        /// 一時削除の接続状態をクリア
        /// </summary>
        private void ClearTempRemovedConnectionState()
        {
            _tempRemovedConnection = null;
            _tempRemovedSocket = null;
            _tempRemovedSocketIndex = -1;
        }

        private void Node_MouseMove(object sender, MouseEventArgs e)
        {
            
            if (isDraggingNode && draggedNode != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var mousePos = e.GetPosition(NodeCanvas);
                
                // multiDragOffsetsを使用（単一・複数の両方に対応）
                if (multiDragOffsets.Count > 0)
                {
                    foreach (var kvp in multiDragOffsets)
                    {
                        var node = kvp.Key;
                        var offset = kvp.Value;
                        
                        node.Position = new Point(
                            mousePos.X - offset.X,
                            mousePos.Y - offset.Y
                        );
                        UpdateSocketPositionsFromNodePosition(node);
                    }
                }
                else
                {
                    // フォールバック：単一ノードの移動（dragStartOffsetを使用）
                    draggedNode.Position = new Point(
                        mousePos.X - dragStartOffset.X,
                        mousePos.Y - dragStartOffset.Y
                    );
                    UpdateSocketPositionsFromNodePosition(draggedNode);
                }
                
                e.Handled = true;
            }
        }

        /// <summary>
        /// ノード位置からソケット位置を計算して更新（ノード移動時専用）
        /// </summary>
        private void UpdateSocketPositionsFromNodePosition(Node node)
        {
            const double nodeWidth = 150;
            const double headerHeight = 30;
            const double socketSpacing = 20;
            const double socketSize = 6;

            // 入力ソケット（左側）
            for (int i = 0; i < node.InputSockets.Count; i++)
            {
                var socket = node.InputSockets[i];
                double x = node.Position.X;
                double y = node.Position.Y + headerHeight + (i * socketSpacing) + socketSize;
                socket.Position = new Point(x, y); // これでPositionChangedが発火して接続線が自動更新
            }

            // 出力ソケット（右側）
            for (int i = 0; i < node.OutputSockets.Count; i++)
            {
                var socket = node.OutputSockets[i];
                double x = node.Position.X + nodeWidth;
                double y = node.Position.Y + headerHeight + (i * socketSpacing) + socketSize;
                socket.Position = new Point(x, y); // これでPositionChangedが発火して接続線が自動更新
            }
        }

        /// <summary>
        /// ノードのすべてのソケット位置をUI要素から取得して更新
        /// </summary>
        private void UpdateAllSocketPositionsForNode(Node node)
        {
            // ノードのコンテナ要素を探す
            var nodeContainer = FindNodeContainer(node);
            if (nodeContainer == null)
            {
                return;
            }

            // 入力ソケット
            foreach (var socket in node.InputSockets)
            {
                var ellipse = FindSocketElement(nodeContainer, socket);
                if (ellipse != null)
                {
                    UpdateSocketPositionFromUI(ellipse, socket);
                }
                else
                {
                }
            }

            // 出力ソケット
            foreach (var socket in node.OutputSockets)
            {
                var ellipse = FindSocketElement(nodeContainer, socket);
                if (ellipse != null)
                {
                    UpdateSocketPositionFromUI(ellipse, socket);
                }
                else
                {
                }
            }
        }

        // ヒットテストヘルパー
        private T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T parent)
                    return parent;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        /// <summary>
        /// マウス位置から最も近いソケットを探す（拡張ヒット判定）
        /// ソケット間隔16px（12px + 4px margin）を考慮し、最大8pxの範囲でヒット判定
        /// </summary>
        private (Ellipse? element, NodeSocket? socket) FindNearestSocket(Point mousePos, double maxDistance = 8.0)
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return (null, null);

            Ellipse? nearestElement = null;
            NodeSocket? nearestSocket = null;
            double nearestDistance = double.MaxValue;

            foreach (var node in viewModel.Nodes)
            {
                var nodeContainer = FindNodeContainer(node);
                if (nodeContainer == null) continue;

                // 入力ソケット
                foreach (var socket in node.InputSockets)
                {
                    var ellipse = FindSocketElement(nodeContainer, socket);
                    if (ellipse == null) continue;

                    var socketCenter = GetSocketElementPosition(ellipse);
                    double distance = Math.Sqrt(
                        Math.Pow(mousePos.X - socketCenter.X, 2) + 
                        Math.Pow(mousePos.Y - socketCenter.Y, 2));

                    if (distance < nearestDistance && distance <= maxDistance)
                    {
                        nearestDistance = distance;
                        nearestElement = ellipse;
                        nearestSocket = socket;
                    }
                }

                // 出力ソケット
                foreach (var socket in node.OutputSockets)
                {
                    var ellipse = FindSocketElement(nodeContainer, socket);
                    if (ellipse == null) continue;

                    var socketCenter = GetSocketElementPosition(ellipse);
                    double distance = Math.Sqrt(
                        Math.Pow(mousePos.X - socketCenter.X, 2) + 
                        Math.Pow(mousePos.Y - socketCenter.Y, 2));

                    if (distance < nearestDistance && distance <= maxDistance)
                    {
                        nearestDistance = distance;
                        nearestElement = ellipse;
                        nearestSocket = socket;
                    }
                }
            }

            return (nearestElement, nearestSocket);
        }

        // 接続を作成
        private void CreateConnection(NodeSocket source, NodeSocket target)
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return;
            
            // 出力→入力の接続のみ許可
            NodeSocket? outputSocket = null;
            NodeSocket? inputSocket = null;
            Ellipse? outputElement = null;
            Ellipse? inputElement = null;
            
            if (!source.IsInput && target.IsInput)
            {
                outputSocket = source;
                inputSocket = target;
                outputElement = draggedSocketElement;
            }
            else if (source.IsInput && !target.IsInput)
            {
                outputSocket = target;
                inputSocket = source;
                inputElement = draggedSocketElement;
            }
            else
            {
                return; // 無効な接続
            }
            
            // 同じノード間の接続は禁止
            if (outputSocket.ParentNode == inputSocket.ParentNode)
            {
                return;
            }
            
            // 型チェック: ソケットの型が互換性があるか確認
            if (!AreSocketTypesCompatible(outputSocket.SocketType, inputSocket.SocketType))
            {
                return;
            }
            
            // ターゲットソケットの要素を見つける
            var mousePos = Mouse.GetPosition(NodeCanvas);
            var hitElement = NodeCanvas.InputHitTest(mousePos) as DependencyObject;
            var targetElement = FindVisualParent<Ellipse>(hitElement);
            
            if (outputElement == null)
                outputElement = targetElement;
            if (inputElement == null)
                inputElement = targetElement;
            
            // ソケット位置を設定
            if (outputElement != null)
            {
                outputSocket.Position = GetSocketElementPosition(outputElement);
            }
            
            if (inputElement != null)
            {
                inputSocket.Position = GetSocketElementPosition(inputElement);
            }
            
            // レイアウト更新を強制
            NodeCanvas.UpdateLayout();
            
            // 両端のノードのソケット位置をUIから更新
            if (outputSocket.ParentNode != null)
            {
                UpdateAllSocketPositionsForNode(outputSocket.ParentNode);
            }
            if (inputSocket.ParentNode != null)
            {
                UpdateAllSocketPositionsForNode(inputSocket.ParentNode);
            }
            
            // 新しい接続を作成（ソケット位置が設定された後なので正しく描画される）
            var connection = new NodeConnection(outputSocket, inputSocket);
            
            // 既存の接続を確認（入力ソケットには1つの接続のみ）
            // ただし、一時削除した接続がある場合は除外
            var existingConnection = viewModel.Connections.FirstOrDefault(c => c.InputSocket == inputSocket);
            
            if (existingConnection != null)
            {
                // 既存接続がある場合は置換コマンドを使用
                viewModel.CommandManager.Execute(new ReplaceConnectionCommand(viewModel, existingConnection, connection));
            }
            else if (_tempRemovedConnection != null && _tempRemovedConnection.InputSocket == inputSocket)
            {
                // ドラッグ開始時に一時削除した接続への再接続の場合は置換コマンドを使用
                viewModel.CommandManager.Execute(new ReplaceConnectionCommand(viewModel, _tempRemovedConnection, connection));
                _tempRemovedConnection = null;
            }
            else
            {
                // 新規接続
                viewModel.CommandManager.Execute(new AddConnectionCommand(viewModel, connection));
            }
            
            // 明示的に接続線を描画
            connection.UpdatePath();
            
            
            // シーンノードの場合、自動的に次のソケットを追加
            if (inputSocket.ParentNode is Models.Nodes.SceneNode sceneNode)
            {
                if (inputSocket.SocketType == SocketType.Object)
                {
                    // 空のオブジェクトソケットがあるかチェック
                    bool hasEmptyObjectSocket = sceneNode.InputSockets.Any(s => 
                        s.SocketType == SocketType.Object && 
                        !viewModel.Connections.Any(c => c.InputSocket == s));
                    
                    if (!hasEmptyObjectSocket)
                    {
                        sceneNode.AddObjectSocket();
                    }
                }
                else if (inputSocket.SocketType == SocketType.Light)
                {
                    // 空のライトソケットがあるかチェック
                    bool hasEmptyLightSocket = sceneNode.InputSockets.Any(s => 
                        s.SocketType == SocketType.Light && 
                        !viewModel.Connections.Any(c => c.InputSocket == s));
                    
                    if (!hasEmptyLightSocket)
                    {
                        sceneNode.AddLightSocket();
                    }
                }
            }
        }
        
        // ソケット型の互換性をチェック
        private bool AreSocketTypesCompatible(SocketType outputType, SocketType inputType)
        {
            // 基本ルール: 同じ型同士は接続可能
            if (outputType == inputType)
                return true;
            
            // 特殊ルール: Objectタイプは他のオブジェクト型と互換性がある
            // （例: Sphere、Plane、CylinderなどをObjectとして扱う場合）
            if (inputType == SocketType.Object)
            {
                // Objectソケットは様々なオブジェクト型を受け入れる
                // ただし、基本的なデータ型（Vector3、Float、Color）やシステム型（Camera、Light、Scene）は除外
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

        // ノードの接続線を更新
        private void UpdateNodeConnections(Node node)
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return;

            // 動かしているノードのコンテナを見つける
            var nodeContainer = FindNodeContainer(node);
            
            // このノードのソケット位置を更新
            foreach (var socket in node.InputSockets)
            {
                if (nodeContainer != null)
                {
                    var socketElement = FindSocketElement(nodeContainer, socket);
                    if (socketElement != null)
                    {
                        socket.Position = GetSocketElementPosition(socketElement);
                    }
                    else
                    {
                        // UI要素が見つからない場合は概算位置を使用
                        socket.Position = GetSocketPosition(socket);
                    }
                }
                else
                {
                    // コンテナが見つからない場合は概算位置を使用
                    socket.Position = GetSocketPosition(socket);
                }
            }

            foreach (var socket in node.OutputSockets)
            {
                if (nodeContainer != null)
                {
                    var socketElement = FindSocketElement(nodeContainer, socket);
                    if (socketElement != null)
                    {
                        socket.Position = GetSocketElementPosition(socketElement);
                    }
                    else
                    {
                        // UI要素が見つからない場合は概算位置を使用
                        socket.Position = GetSocketPosition(socket);
                    }
                }
                else
                {
                    // コンテナが見つからない場合は概算位置を使用
                    socket.Position = GetSocketPosition(socket);
                }
            }
            
            // このノードに関連する接続線を更新
            foreach (var connection in viewModel.Connections)
            {
                if (connection.OutputSocket?.ParentNode == node || connection.InputSocket?.ParentNode == node)
                {
                    connection.UpdatePath();
                }
            }
        }

        // プレビュー線を作成
        private void CreatePreviewLine(NodeSocket socket)
        {
            if (previewLine != null)
            {
                ConnectionLayer.Children.Remove(previewLine);
            }

            previewLine = new Line
            {
                Stroke = socket.SocketColor,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                IsHitTestVisible = false,
                Opacity = 0.7  // デフォルトは少し薄く
            };

            // 初期位置は後で更新されるので、とりあえず0,0に設定
            previewLine.X1 = 0;
            previewLine.Y1 = 0;
            previewLine.X2 = 0;
            previewLine.Y2 = 0;

            ConnectionLayer.Children.Add(previewLine);
        }

        // プレビュー線を更新
        private void UpdatePreviewLine(Point endPoint)
        {
            if (previewLine == null || draggedSocket == null) return;

            Point startPos;
            if (draggedSocketElement != null)
            {
                // UI要素から位置を取得
                startPos = GetSocketElementPosition(draggedSocketElement);
            }
            else
            {
                // UI要素が見つからない場合は、ソケットが既に持っている位置を使用
                startPos = draggedSocket.Position;
            }

            previewLine.X1 = startPos.X;
            previewLine.Y1 = startPos.Y;
            previewLine.X2 = endPoint.X;
            previewLine.Y2 = endPoint.Y;
        }

        // プレビュー線の色を互換性に応じて更新
        private void UpdatePreviewLineCompatibility(NodeSocket targetSocket)
        {
            if (previewLine == null || draggedSocket == null) return;

            // 出力→入力の確認
            bool isValidDirection = (!draggedSocket.IsInput && targetSocket.IsInput) ||
                                   (draggedSocket.IsInput && !targetSocket.IsInput);

            // 同じノードかどうか確認
            bool isSameNode = draggedSocket.ParentNode == targetSocket.ParentNode;

            // 型の互換性を確認
            bool isCompatible = false;
            if (!draggedSocket.IsInput && targetSocket.IsInput)
            {
                isCompatible = AreSocketTypesCompatible(draggedSocket.SocketType, targetSocket.SocketType);
            }
            else if (draggedSocket.IsInput && !targetSocket.IsInput)
            {
                isCompatible = AreSocketTypesCompatible(targetSocket.SocketType, draggedSocket.SocketType);
            }

            // 互換性に応じて色とスタイルを変更
            if (isValidDirection && !isSameNode && isCompatible)
            {
                // 接続可能：実線、通常の色、不透明度1.0
                previewLine.Stroke = draggedSocket.SocketColor;
                previewLine.StrokeDashArray = null; // 実線
                previewLine.Opacity = 1.0;
            }
            else
            {
                // 接続不可能：実線、赤色、不透明度0.5
                previewLine.Stroke = BrushCache.Get(Colors.Red);
                previewLine.StrokeDashArray = null; // 実線
                previewLine.Opacity = 0.5;
            }
        }

        // プレビュー線の色をデフォルトに戻す
        private void ResetPreviewLineColor()
        {
            if (previewLine == null || draggedSocket == null) return;

            // デフォルトの状態：点線、ソケットの色、不透明度0.7
            previewLine.Stroke = draggedSocket.SocketColor;
            previewLine.StrokeDashArray = new DoubleCollection { 5, 3 }; // 点線
            previewLine.Opacity = 0.7;
        }

        // プレビュー線を削除
        private void RemovePreviewLine()
        {
            if (previewLine != null)
            {
                ConnectionLayer.Children.Remove(previewLine);
                previewLine = null;
            }
            draggedSocketElement = null;
        }

        // ソケットの位置を取得（実際のUI要素から）
        private Point GetSocketElementPosition(Ellipse socketElement)
        {
            try
            {
                // Ellipseの中心位置をキャンバス座標系に変換
                var transform = socketElement.TransformToAncestor(NodeCanvas);
                var center = new Point(socketElement.Width / 2, socketElement.Height / 2);
                return transform.Transform(center);
            }
            catch
            {
                // エラーが発生した場合は0,0を返す
                return new Point(0, 0);
            }
        }

        // すべてのソケットの位置を更新
        private void UpdateSocketPositions()
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return;

            foreach (var node in viewModel.Nodes)
            {
                // ノードのUI要素を見つける
                var nodeContainer = FindNodeContainer(node);
                if (nodeContainer == null)
                {
                    continue;
                }

                // 入力ソケットの位置を更新
                foreach (var socket in node.InputSockets)
                {
                    var socketElement = FindSocketElement(nodeContainer, socket);
                    if (socketElement != null)
                    {
                        var newPos = GetSocketElementPosition(socketElement);
                        socket.Position = newPos;
                    }
                    else
                    {
                    }
                }

                // 出力ソケットの位置を更新
                foreach (var socket in node.OutputSockets)
                {
                    var socketElement = FindSocketElement(nodeContainer, socket);
                    if (socketElement != null)
                    {
                        var newPos = GetSocketElementPosition(socketElement);
                        socket.Position = newPos;
                    }
                    else
                    {
                    }
                }
            }
        }

        // ノードのコンテナ要素を見つける
        private Border? FindNodeContainer(Node node)
        {
            // NodeLayer内のItemsControlを見つける
            var itemsControl = FindVisualChild<ItemsControl>(NodeLayer);
            if (itemsControl == null) return null;
            
            // ItemsControlのパネル（Canvas）を取得
            var panel = FindVisualChild<Canvas>(itemsControl);
            if (panel == null) return null;
            
            // パネル内のContentPresenterを探す
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(panel); i++)
            {
                var container = VisualTreeHelper.GetChild(panel, i) as ContentPresenter;
                if (container?.Content == node)
                {
                    return FindVisualChild<Border>(container);
                }
            }
            return null;
        }

        // ビジュアルツリーから子要素を検索
        private T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                    return result;

                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                    return childOfChild;
            }
            return null;
        }

        // ソケットのEllipse要素を見つける
        private Ellipse? FindSocketElement(Border nodeContainer, NodeSocket socket)
        {
            return FindSocketElementRecursive(nodeContainer, socket);
        }

        private Ellipse? FindSocketElementRecursive(DependencyObject parent, NodeSocket socket)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                
                if (child is Ellipse ellipse && ellipse.DataContext == socket)
                    return ellipse;

                var result = FindSocketElementRecursive(child, socket);
                if (result != null)
                    return result;
            }
            return null;
        }
        
        // ソケットの位置を取得（計算による推定）
        private Point GetSocketPosition(NodeSocket socket)
        {
            if (socket.ParentNode == null)
                return new Point(0, 0);

            var node = socket.ParentNode;
            double nodeWidth = 150;
            double headerHeight = 30;
            double socketSize = 12;
            double socketSpacing = 20;

            double x, y;

            if (socket.IsInput)
            {
                // 入力ソケットは左側
                x = node.Position.X;
                int inputIndex = node.InputSockets.IndexOf(socket);
                y = node.Position.Y + headerHeight + (inputIndex * socketSpacing) + socketSize / 2;
            }
            else
            {
                // 出力ソケットは右側
                x = node.Position.X + nodeWidth;
                int outputIndex = node.OutputSockets.IndexOf(socket);
                y = node.Position.Y + headerHeight + (outputIndex * socketSpacing) + socketSize / 2;
            }

            return new Point(x, y);
        }

        /// <summary>
        /// ソケットの実際のUI位置を取得（接続線の更新時に常に呼ばれる）
        /// </summary>
        private Point GetSocketPositionFromUI(NodeSocket socket)
        {
            if (socket?.ParentNode == null)
                return new Point(0, 0);

            // ノードコンテナを探す
            var nodeContainer = FindNodeContainer(socket.ParentNode);
            if (nodeContainer == null)
            {
                // コンテナが見つからない場合は計算で推定
                return GetSocketPosition(socket);
            }

            // ソケットのUI要素を探す
            var socketElement = FindSocketElement(nodeContainer, socket);
            if (socketElement == null)
            {
                // ソケット要素が見つからない場合は計算で推定
                return GetSocketPosition(socket);
            }

            // ソケット要素のCanvas上での実際の位置を取得
            try
            {
                var transform = socketElement.TransformToAncestor(NodeCanvas);
                var point = transform.Transform(new Point(socketElement.ActualWidth / 2, socketElement.ActualHeight / 2));
                return point;
            }
            catch
            {
                // 変換に失敗した場合は計算で推定
                return GetSocketPosition(socket);
            }
        }

        // 複数選択関連のヘルパーメソッド
        
        /// <summary>
        /// 全ての選択をクリア
        /// </summary>
        private void ClearAllSelections(MainViewModel viewModel)
        {
            foreach (var node in selectedNodes)
            {
                node.IsSelected = false;
            }
            selectedNodes.Clear();
            
            // プロパティパネルは変化させない（SelectedNodeはnullのまま）
            if (viewModel.SelectedNode != null)
            {
                viewModel.SelectedNode.IsSelected = false;
                viewModel.SelectedNode = null;
            }
        }

        /// <summary>
        /// 矩形選択用の矩形を作成
        /// </summary>
        private void CreateSelectionRectangle(Point startPoint)
        {
            if (selectionRectangle != null)
            {
                ConnectionLayer.Children.Remove(selectionRectangle);
            }

            selectionRectangle = new Rectangle
            {
                Stroke = BrushCache.Get(100, 150, 255),
                StrokeThickness = 1,
                Fill = BrushCache.Get(30, 100, 150, 255),
                IsHitTestVisible = false
            };

            ConnectionLayer.Children.Add(selectionRectangle);
            Canvas.SetLeft(selectionRectangle, startPoint.X);
            Canvas.SetTop(selectionRectangle, startPoint.Y);
            selectionRectangle.Width = 0;
            selectionRectangle.Height = 0;
        }

        /// <summary>
        /// 矩形選択用の矩形を更新
        /// </summary>
        private void UpdateSelectionRectangle(Point currentPoint)
        {
            if (selectionRectangle == null) return;

            double left = Math.Min(rectSelectStartPoint.X, currentPoint.X);
            double top = Math.Min(rectSelectStartPoint.Y, currentPoint.Y);
            double width = Math.Abs(currentPoint.X - rectSelectStartPoint.X);
            double height = Math.Abs(currentPoint.Y - rectSelectStartPoint.Y);

            Canvas.SetLeft(selectionRectangle, left);
            Canvas.SetTop(selectionRectangle, top);
            selectionRectangle.Width = width;
            selectionRectangle.Height = height;
        }

        /// <summary>
        /// 矩形選択用の矩形を削除
        /// </summary>
        private void RemoveSelectionRectangle()
        {
            if (selectionRectangle != null)
            {
                ConnectionLayer.Children.Remove(selectionRectangle);
                selectionRectangle = null;
            }
        }

        /// <summary>
        /// 矩形内に完全に収まっているノードを選択
        /// </summary>
        private void SelectNodesInRectangle(MainViewModel viewModel, Point startPoint, Point endPoint)
        {
            double left = Math.Min(startPoint.X, endPoint.X);
            double top = Math.Min(startPoint.Y, endPoint.Y);
            double right = Math.Max(startPoint.X, endPoint.X);
            double bottom = Math.Max(startPoint.Y, endPoint.Y);

            var rect = new Rect(left, top, right - left, bottom - top);

            // 全てのノードをチェック
            foreach (var node in viewModel.Nodes)
            {
                // ノードの境界を取得（概算）
                var nodeContainer = FindNodeContainer(node);
                if (nodeContainer != null)
                {
                    var nodeRect = new Rect(
                        node.Position.X,
                        node.Position.Y,
                        nodeContainer.ActualWidth,
                        nodeContainer.ActualHeight
                    );

                    // ノードが矩形内に完全に収まっているかチェック
                    if (rect.Contains(nodeRect))
                    {
                        node.IsSelected = true;
                        selectedNodes.Add(node);
                    }
                }
                else
                {
                    // コンテナが見つからない場合は概算サイズで判定
                    var nodeRect = new Rect(
                        node.Position.X,
                        node.Position.Y,
                        150, // 最小幅
                        Math.Max(60, 30 + Math.Max(node.InputSockets.Count, node.OutputSockets.Count) * 20)
                    );

                    if (rect.Contains(nodeRect))
                    {
                        node.IsSelected = true;
                        selectedNodes.Add(node);
                    }
                }
            }
            
            // 単一ノード選択の場合のみ、プロパティパネルに表示
            if (selectedNodes.Count == 1)
            {
                viewModel.SelectedNode = selectedNodes.First();
            }
            else
            {
                // 複数選択または選択なしの場合はプロパティパネルをクリア
                viewModel.SelectedNode = null;
            }
        }

        // ======================================================================
        // FloatNode テキストボックス関連イベント
        // ======================================================================
        
        /// <summary>
        /// 浮動小数点数の入力のみ許可（数字、小数点、マイナス記号）
        /// </summary>
        private void FloatTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null) return;

            // 入力される文字
            string input = e.Text;
            
            // 現在のテキストと新しい入力後のテキストを作成
            string currentText = textBox.Text;
            int selectionStart = textBox.SelectionStart;
            int selectionLength = textBox.SelectionLength;
            string newText = currentText.Substring(0, selectionStart) + input + 
                            currentText.Substring(selectionStart + selectionLength);
            
            // 有効なfloat形式かチェック
            e.Handled = !IsValidFloatInput(newText);
        }
        
        /// <summary>
        /// 文字列が有効なfloat入力かどうかをチェック
        /// </summary>
        private bool IsValidFloatInput(string text)
        {
            if (string.IsNullOrEmpty(text))
                return true;
            
            // 入力途中も許可するパターン（マイナス記号のみ、小数点で終わるなど）
            // パターン: オプションのマイナス、数字、オプションの小数点、オプションの数字
            var regex = new Regex(@"^-?(\d*\.?\d*)$");
            return regex.IsMatch(text);
        }
        
        /// <summary>
        /// Enterキー/Tabキーで入力を確定
        /// </summary>
        private void FloatTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                var textBox = sender as TextBox;
                if (textBox != null)
                {
                    // 変更前の値を取得
                    float? oldValue = null;
                    if (_textBoxOriginalValues.TryGetValue(textBox, out float originalValue))
                    {
                        oldValue = originalValue;
                        _textBoxOriginalValues.Remove(textBox);
                    }
                    
                    // バインディングを強制更新
                    var bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty);
                    bindingExpression?.UpdateSource();
                    
                    // FloatNodeの場合、Undo/Redoコマンドを発行
                    if (oldValue.HasValue && textBox.DataContext is FloatNode floatNode)
                    {
                        float newValue = floatNode.Value;
                        if (oldValue.Value != newValue)
                        {
                            var viewModel = GetViewModel();
                            viewModel?.CommandManager.RegisterExecuted(
                                new ChangePropertyCommand<float>(floatNode, "Value", oldValue.Value, newValue, 
                                    "Float値を変更"));
                        }
                    }
                    
                    // フォーカスを外す
                    Keyboard.ClearFocus();
                    NodeCanvas.Focus();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                var textBox = sender as TextBox;
                if (textBox != null)
                {
                    // 変更を破棄（Undo用の値も削除）
                    _textBoxOriginalValues.Remove(textBox);
                    
                    // バインディングをリセット（元の値に戻す）
                    var bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty);
                    bindingExpression?.UpdateTarget();
                    
                    // フォーカスを外す
                    Keyboard.ClearFocus();
                    NodeCanvas.Focus();
                }
                e.Handled = true;
            }
        }
        
        /// <summary>
        /// フォーカスを失ったときに入力を確定
        /// </summary>
        private void FloatTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null)
            {
                // 空の場合は0に設定
                if (string.IsNullOrWhiteSpace(textBox.Text) || textBox.Text == "-" || textBox.Text == ".")
                {
                    textBox.Text = "0";
                }
                
                // 変更前の値を取得
                float? oldValue = null;
                if (_textBoxOriginalValues.TryGetValue(textBox, out float originalValue))
                {
                    oldValue = originalValue;
                    _textBoxOriginalValues.Remove(textBox);
                }
                
                // バインディングを強制更新
                var bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty);
                bindingExpression?.UpdateSource();
                
                // FloatNodeの場合、Undo/Redoコマンドを発行
                if (oldValue.HasValue && textBox.DataContext is FloatNode floatNode)
                {
                    float newValue = floatNode.Value;
                    if (oldValue.Value != newValue)
                    {
                        var viewModel = GetViewModel();
                        viewModel?.CommandManager.RegisterExecuted(
                            new ChangePropertyCommand<float>(floatNode, "Value", oldValue.Value, newValue, 
                                "Float値を変更"));
                    }
                }
            }
        }
        
        /// <summary>
        /// フォーカス取得時にテキストを全選択し、変更前の値を記録
        /// </summary>
        private void FloatTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null)
            {
                // 変更前の値を記録（Undo用）
                if (textBox.DataContext is FloatNode floatNode)
                {
                    _textBoxOriginalValues[textBox] = floatNode.Value;
                }
                
                // Dispatcherを使って遅延実行（SelectAllが即座に動作しない場合があるため）
                textBox.Dispatcher.BeginInvoke(new Action(() =>
                {
                    textBox.SelectAll();
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }
        
        /// <summary>
        /// ノード内の次の（または前の）TextBoxにフォーカスを移動する
        /// </summary>
        /// <param name="currentTextBox">現在のTextBox</param>
        /// <param name="forward">trueで次へ、falseで前へ</param>
        private void MoveToNextTextBoxInNode(TextBox currentTextBox, bool forward)
        {
            // 現在のTextBoxが属するノードのコンテナを探す
            var nodeContainer = FindParentNodeContainer(currentTextBox);
            if (nodeContainer == null)
            {
                Keyboard.ClearFocus();
                NodeCanvas.Focus();
                return;
            }
            
            // ノードコンテナ内のすべての有効なTextBoxを取得
            var textBoxes = FindVisualChildren<TextBox>(nodeContainer)
                .Where(tb => tb.IsVisible && tb.IsEnabled)
                .ToList();
            
            if (textBoxes.Count <= 1)
            {
                // 1つ以下なら移動先がないのでフォーカス解除
                Keyboard.ClearFocus();
                NodeCanvas.Focus();
                return;
            }
            
            // 現在のTextBoxのインデックスを取得
            int currentIndex = textBoxes.IndexOf(currentTextBox);
            if (currentIndex < 0)
            {
                Keyboard.ClearFocus();
                NodeCanvas.Focus();
                return;
            }
            
            // 次（または前）のインデックスを計算（ループ）
            int nextIndex;
            if (forward)
            {
                nextIndex = (currentIndex + 1) % textBoxes.Count;
            }
            else
            {
                nextIndex = (currentIndex - 1 + textBoxes.Count) % textBoxes.Count;
            }
            
            // 次のTextBoxにフォーカス
            var nextTextBox = textBoxes[nextIndex];
            nextTextBox.Focus();
            nextTextBox.SelectAll();
        }
        
        /// <summary>
        /// 親のノードコンテナ（Border）を探す
        /// </summary>
        private FrameworkElement? FindParentNodeContainer(DependencyObject child)
        {
            DependencyObject parent = VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                // ノードのコンテナはBorderでDataContextがNode
                if (parent is Border border && border.DataContext is Node)
                {
                    return border;
                }
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }
        
        /// <summary>
        /// ビジュアルツリーから指定された型の子要素をすべて取得
        /// </summary>
        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;
            
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    yield return typedChild;
                }
                
                foreach (var descendant in FindVisualChildren<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        // ======================================================================
        // Vector3Node 入力テキストボックス関連イベント
        // ======================================================================
        
        /// <summary>
        /// Vector3テキストボックスがロードされたとき、初期値を設定
        /// </summary>
        private void Vector3TextBox_Loaded(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox?.Tag is NodeSocket socket && socket.ParentNode is Vector3Node vector3Node)
            {
                // 初期値を設定
                float value = vector3Node.GetSocketValue(socket.Name);
                textBox.Text = value.ToString("G");
            }
        }

        /// <summary>
        /// 浮動小数点数の入力のみ許可
        /// </summary>
        private void Vector3TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null) return;

            string input = e.Text;
            string currentText = textBox.Text;
            int selectionStart = textBox.SelectionStart;
            int selectionLength = textBox.SelectionLength;
            string newText = currentText.Substring(0, selectionStart) + input + 
                            currentText.Substring(selectionStart + selectionLength);
            
            e.Handled = !IsValidFloatInput(newText);
        }
        
        /// <summary>
        /// Enterキーで入力を確定
        /// </summary>
        private void Vector3TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var textBox = sender as TextBox;
                if (textBox != null)
                {
                    ApplyVector3TextBoxValue(textBox);
                    Keyboard.ClearFocus();
                    NodeCanvas.Focus();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Tab)
            {
                var textBox = sender as TextBox;
                if (textBox != null)
                {
                    ApplyVector3TextBoxValue(textBox);
                    MoveToNextTextBoxInNode(textBox, !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                var textBox = sender as TextBox;
                if (textBox?.Tag is NodeSocket socket && socket.ParentNode is Vector3Node vector3Node)
                {
                    // 変更を破棄（Undo用の値も削除）
                    _textBoxOriginalValues.Remove(textBox);
                    
                    // 元の値に戻す
                    textBox.Text = vector3Node.GetSocketValue(socket.Name).ToString("G");
                    Keyboard.ClearFocus();
                    NodeCanvas.Focus();
                }
                e.Handled = true;
            }
        }
        
        /// <summary>
        /// フォーカスを失ったときに入力を確定
        /// </summary>
        private void Vector3TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null)
            {
                ApplyVector3TextBoxValue(textBox);
            }
        }
        
        /// <summary>
        /// フォーカス取得時にテキストを全選択し、変更前の値を記録
        /// </summary>
        private void Vector3TextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null)
            {
                // 変更前の値を記録（Undo用）
                if (textBox.Tag is NodeSocket socket && socket.ParentNode is Vector3Node vector3Node)
                {
                    _textBoxOriginalValues[textBox] = vector3Node.GetSocketValue(socket.Name);
                }
                
                textBox.Dispatcher.BeginInvoke(new Action(() =>
                {
                    textBox.SelectAll();
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }
        
        /// <summary>
        /// テキストボックスの値をVector3Nodeに適用
        /// </summary>
        private void ApplyVector3TextBoxValue(TextBox textBox)
        {
            if (textBox.Tag is NodeSocket socket && socket.ParentNode is Vector3Node vector3Node)
            {
                // 空または無効な場合は現在の値を維持
                if (string.IsNullOrWhiteSpace(textBox.Text) || textBox.Text == "-" || textBox.Text == ".")
                {
                    textBox.Text = vector3Node.GetSocketValue(socket.Name).ToString("G");
                    _textBoxOriginalValues.Remove(textBox);
                    return;
                }
                
                if (float.TryParse(textBox.Text, out float newValue))
                {
                    // 変更前の値を取得
                    float oldValue = vector3Node.GetSocketValue(socket.Name);
                    if (_textBoxOriginalValues.TryGetValue(textBox, out float originalValue))
                    {
                        oldValue = originalValue;
                        _textBoxOriginalValues.Remove(textBox);
                    }
                    
                    // 値が変更された場合のみコマンドを発行
                    if (oldValue != newValue)
                    {
                        var viewModel = GetViewModel();
                        if (viewModel != null)
                        {
                            // 値を設定してからコマンドを登録（UIは既に適用済み）
                            vector3Node.SetSocketValue(socket.Name, newValue);
                            
                            // プロパティ名を特定
                            string propertyName = socket.Name switch
                            {
                                "X" => "X",
                                "Y" => "Y",
                                "Z" => "Z",
                                _ => socket.Name
                            };
                            
                            viewModel.CommandManager.RegisterExecuted(
                                new ChangePropertyCommand<float>(vector3Node, propertyName, oldValue, newValue, 
                                    $"Vector3.{propertyName} を変更"));
                        }
                        else
                        {
                            vector3Node.SetSocketValue(socket.Name, newValue);
                        }
                    }
                    
                    textBox.Text = newValue.ToString("G");
                }
                else
                {
                    // パース失敗時は現在の値に戻す
                    textBox.Text = vector3Node.GetSocketValue(socket.Name).ToString("G");
                    _textBoxOriginalValues.Remove(textBox);
                }
            }
        }

        // ===== Vector4Node用のTextBox編集機能 =====
        
        /// <summary>
        /// Vector4Node用のTextBoxがロードされたとき
        /// </summary>
        private void Vector4TextBox_Loaded(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox?.Tag is NodeSocket socket && socket.ParentNode is Vector4Node vector4Node)
            {
                // 初期値を設定
                float value = vector4Node.GetSocketValue(socket.Name);
                textBox.Text = value.ToString("G");
            }
        }

        /// <summary>
        /// 浮動小数点数の入力のみ許可（Vector4用）
        /// </summary>
        private void Vector4TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null) return;

            string input = e.Text;
            string currentText = textBox.Text;
            int selectionStart = textBox.SelectionStart;
            int selectionLength = textBox.SelectionLength;
            string newText = currentText.Substring(0, selectionStart) + input + 
                            currentText.Substring(selectionStart + selectionLength);
            
            e.Handled = !IsValidFloatInput(newText);
        }
        
        /// <summary>
        /// Enterキーで入力を確定（Vector4用）
        /// </summary>
        private void Vector4TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var textBox = sender as TextBox;
                if (textBox != null)
                {
                    ApplyVector4TextBoxValue(textBox);
                    Keyboard.ClearFocus();
                    NodeCanvas.Focus();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Tab)
            {
                var textBox = sender as TextBox;
                if (textBox != null)
                {
                    ApplyVector4TextBoxValue(textBox);
                    MoveToNextTextBoxInNode(textBox, !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                var textBox = sender as TextBox;
                if (textBox?.Tag is NodeSocket socket && socket.ParentNode is Vector4Node vector4Node)
                {
                    // 変更を破棄（Undo用の値も削除）
                    _textBoxOriginalValues.Remove(textBox);
                    
                    // 元の値に戻す
                    textBox.Text = vector4Node.GetSocketValue(socket.Name).ToString("G");
                    Keyboard.ClearFocus();
                    NodeCanvas.Focus();
                }
                e.Handled = true;
            }
        }
        
        /// <summary>
        /// フォーカスを失ったときに入力を確定（Vector4用）
        /// </summary>
        private void Vector4TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null)
            {
                ApplyVector4TextBoxValue(textBox);
            }
        }
        
        /// <summary>
        /// フォーカス取得時にテキストを全選択し、変更前の値を記録（Vector4用）
        /// </summary>
        private void Vector4TextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null)
            {
                // 変更前の値を記録（Undo用）
                if (textBox.Tag is NodeSocket socket && socket.ParentNode is Vector4Node vector4Node)
                {
                    _textBoxOriginalValues[textBox] = vector4Node.GetSocketValue(socket.Name);
                }
                
                textBox.Dispatcher.BeginInvoke(new Action(() =>
                {
                    textBox.SelectAll();
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }
        
        /// <summary>
        /// テキストボックスの値をVector4Nodeに適用
        /// </summary>
        private void ApplyVector4TextBoxValue(TextBox textBox)
        {
            if (textBox.Tag is NodeSocket socket && socket.ParentNode is Vector4Node vector4Node)
            {
                // 空または無効な場合は現在の値を維持
                if (string.IsNullOrWhiteSpace(textBox.Text) || textBox.Text == "-" || textBox.Text == ".")
                {
                    textBox.Text = vector4Node.GetSocketValue(socket.Name).ToString("G");
                    _textBoxOriginalValues.Remove(textBox);
                    return;
                }
                
                if (float.TryParse(textBox.Text, out float newValue))
                {
                    // 変更前の値を取得
                    float oldValue = vector4Node.GetSocketValue(socket.Name);
                    if (_textBoxOriginalValues.TryGetValue(textBox, out float originalValue))
                    {
                        oldValue = originalValue;
                        _textBoxOriginalValues.Remove(textBox);
                    }
                    
                    // 値が変更された場合のみコマンドを発行
                    if (oldValue != newValue)
                    {
                        var viewModel = GetViewModel();
                        if (viewModel != null)
                        {
                            // 値を設定してからコマンドを登録（UIは既に適用済み）
                            vector4Node.SetSocketValue(socket.Name, newValue);
                            
                            // プロパティ名を特定
                            string propertyName = socket.Name switch
                            {
                                "X" => "X",
                                "Y" => "Y",
                                "Z" => "Z",
                                "W" => "W",
                                _ => socket.Name
                            };
                            
                            viewModel.CommandManager.RegisterExecuted(
                                new ChangePropertyCommand<float>(vector4Node, propertyName, oldValue, newValue, 
                                    $"Vector4.{propertyName} を変更"));
                        }
                        else
                        {
                            vector4Node.SetSocketValue(socket.Name, newValue);
                        }
                    }
                    
                    textBox.Text = newValue.ToString("G");
                }
                else
                {
                    // パース失敗時は現在の値に戻す
                    textBox.Text = vector4Node.GetSocketValue(socket.Name).ToString("G");
                    _textBoxOriginalValues.Remove(textBox);
                }
            }
        }

        // ===== ColorNode用のTextBox編集機能 =====
        
        /// <summary>
        /// ColorNode用のTextBoxがロードされたとき
        /// </summary>
        private void ColorTextBox_Loaded(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox?.Tag is NodeSocket socket && socket.ParentNode is ColorNode colorNode)
            {
                // 初期値を設定
                float value = colorNode.GetSocketValue(socket.Name);
                textBox.Text = value.ToString("G");
            }
        }

        /// <summary>
        /// 浮動小数点数の入力のみ許可（Color用）
        /// </summary>
        private void ColorTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null) return;

            string input = e.Text;
            string currentText = textBox.Text;
            int selectionStart = textBox.SelectionStart;
            int selectionLength = textBox.SelectionLength;
            string newText = currentText.Substring(0, selectionStart) + input + 
                            currentText.Substring(selectionStart + selectionLength);
            
            e.Handled = !IsValidFloatInput(newText);
        }
        
        /// <summary>
        /// Enterキーで入力を確定（Color用）
        /// </summary>
        private void ColorTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var textBox = sender as TextBox;
                if (textBox != null)
                {
                    ApplyColorTextBoxValue(textBox);
                    Keyboard.ClearFocus();
                    NodeCanvas.Focus();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Tab)
            {
                var textBox = sender as TextBox;
                if (textBox != null)
                {
                    ApplyColorTextBoxValue(textBox);
                    MoveToNextTextBoxInNode(textBox, !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                var textBox = sender as TextBox;
                if (textBox?.Tag is NodeSocket socket && socket.ParentNode is ColorNode colorNode)
                {
                    // 元の値に戻す
                    textBox.Text = colorNode.GetSocketValue(socket.Name).ToString("G");
                    Keyboard.ClearFocus();
                    NodeCanvas.Focus();
                }
                e.Handled = true;
            }
        }
        
        /// <summary>
        /// フォーカスを失ったときに入力を確定（Color用）
        /// </summary>
        private void ColorTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null)
            {
                ApplyColorTextBoxValue(textBox);
            }
        }
        
        /// <summary>
        /// フォーカス取得時にテキストを全選択（Color用）
        /// </summary>
        private void ColorTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null)
            {
                textBox.Dispatcher.BeginInvoke(new Action(() =>
                {
                    textBox.SelectAll();
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }
        
        /// <summary>
        /// テキストボックスの値をColorNodeに適用
        /// </summary>
        private void ApplyColorTextBoxValue(TextBox textBox)
        {
            if (textBox.Tag is NodeSocket socket && socket.ParentNode is ColorNode colorNode)
            {
                // 空または無効な場合は現在の値を維持
                if (string.IsNullOrWhiteSpace(textBox.Text) || textBox.Text == "-" || textBox.Text == ".")
                {
                    textBox.Text = colorNode.GetSocketValue(socket.Name).ToString("G");
                    return;
                }
                
                if (float.TryParse(textBox.Text, out float value))
                {
                    colorNode.SetSocketValue(socket.Name, value);
                    textBox.Text = value.ToString("G");
                }
                else
                {
                    // パース失敗時は現在の値に戻す
                    textBox.Text = colorNode.GetSocketValue(socket.Name).ToString("G");
                }
            }
        }

        /// <summary>
        /// 現在フォーカスされている TextBox のバインディングを即座に更新
        /// ノードエディター上をクリックした時に、プロパティパネルの入力値を確定させる
        /// </summary>
        private void UpdateFocusedTextBoxBinding()
        {
            var focusedElement = Keyboard.FocusedElement as TextBox;
            if (focusedElement != null)
            {
                var binding = BindingOperations.GetBindingExpression(focusedElement, TextBox.TextProperty);
                binding?.UpdateSource();
                
                // フォーカスをクリア（別のノードをクリックしたときにテキストボックスのフォーカスが残らないようにする）
                Keyboard.ClearFocus();
            }
        }
    }
}

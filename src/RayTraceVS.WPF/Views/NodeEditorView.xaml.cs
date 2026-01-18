using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RayTraceVS.WPF.ViewModels;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Nodes;

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
            
            // 接続線を再構築（必ず最前面に描画される）
            RebuildConnectionPaths();
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
                            
                            // 接続を削除
                            viewModel.RemoveConnection(existingConnection);
                            
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
                    
                    foreach (var selectedNode in selectedNodes)
                    {
                        multiDragOffsets[selectedNode] = new Point(
                            mousePos.X - selectedNode.Position.X,
                            mousePos.Y - selectedNode.Position.Y
                        );
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
                // 接続先のソケットを探す
                var mousePos = e.GetPosition(NodeCanvas);
                var hitElement = NodeCanvas.InputHitTest(mousePos) as DependencyObject;
                
                
                var targetSocket = FindVisualParent<Ellipse>(hitElement);
                
                if (targetSocket != null && targetSocket.DataContext is NodeSocket targetNodeSocket)
                {
                    // 接続を作成（出力→入力のみ許可）
                    CreateConnection(draggedSocket, targetNodeSocket);
                }
                else
                {
                }
                
                // プレビュー線を削除
                RemovePreviewLine();
            }
            
            // ドラッグ状態をリセット
            isDraggingNode = false;
            isDraggingConnection = false;
            draggedNode = null;
            draggedSocket = null;
            multiDragOffsets.Clear();
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
                
                // ホバーしているソケットをチェックして、互換性を表示
                var hitElement = NodeCanvas.InputHitTest(mousePos) as DependencyObject;
                var targetSocket = FindVisualParent<Ellipse>(hitElement);
                if (targetSocket != null && targetSocket.DataContext is NodeSocket targetNodeSocket)
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
            var viewModel = GetViewModel();
            if (viewModel == null) return;
            
            // Deleteキーでノードを削除（複数選択対応）
            if (e.Key == Key.Delete && selectedNodes.Count > 0)
            {
                var nodesToDelete = selectedNodes.ToList();
                ClearAllSelections(viewModel);
                
                foreach (var node in nodesToDelete)
                {
                    viewModel.RemoveNode(node);
                }
                
                e.Handled = true;
            }
        }
        
        /// <summary>
        /// 選択されたノードを削除（MainWindowから呼び出し用）
        /// </summary>
        public void DeleteSelectedNodes()
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return;
            
            // 選択されているノードがある場合のみ削除
            if (selectedNodes.Count > 0)
            {
                var nodesToDelete = selectedNodes.ToList();
                ClearAllSelections(viewModel);
                
                foreach (var node in nodesToDelete)
                {
                    viewModel.RemoveNode(node);
                }
            }
        }

        // ノード上でのマウスイベント
        private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
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
                            
                            // 接続を削除
                            viewModel.RemoveConnection(existingConnection);
                            
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
                
                foreach (var selectedNode in selectedNodes)
                {
                    multiDragOffsets[selectedNode] = new Point(
                        mousePos.X - selectedNode.Position.X,
                        mousePos.Y - selectedNode.Position.Y
                    );
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
                // Canvasに対してヒットテストを行う（グローバル座標で）
                var mousePos = e.GetPosition(NodeCanvas);
                var hitElement = NodeCanvas.InputHitTest(mousePos) as DependencyObject;
                
                
                var targetSocket = FindVisualParent<Ellipse>(hitElement);
                
                if (targetSocket != null && targetSocket.DataContext is NodeSocket targetNodeSocket)
                {
                    CreateConnection(draggedSocket, targetNodeSocket);
                }
                else
                {
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
                isDraggingNode = false;
                draggedNode = null;
                // NodeCanvasでマウスリリース
                NodeCanvas.ReleaseMouseCapture();
                e.Handled = true;
            }
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
            
            // 既存の接続を確認（入力ソケットには1つの接続のみ）
            var existingConnection = viewModel.Connections.FirstOrDefault(c => c.InputSocket == inputSocket);
            if (existingConnection != null)
            {
                viewModel.RemoveConnection(existingConnection);
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
            viewModel.AddConnection(connection);
            
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
                previewLine.Stroke = new SolidColorBrush(Colors.Red);
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
                Stroke = new SolidColorBrush(Color.FromRgb(100, 150, 255)),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(30, 100, 150, 255)),
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
        /// Enterキーで入力を確定
        /// </summary>
        private void FloatTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var textBox = sender as TextBox;
                if (textBox != null)
                {
                    // バインディングを強制更新
                    var bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty);
                    bindingExpression?.UpdateSource();
                    
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
                
                // バインディングを強制更新
                var bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty);
                bindingExpression?.UpdateSource();
            }
        }
        
        /// <summary>
        /// フォーカス取得時にテキストを全選択
        /// </summary>
        private void FloatTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null)
            {
                // Dispatcherを使って遅延実行（SelectAllが即座に動作しない場合があるため）
                textBox.Dispatcher.BeginInvoke(new Action(() =>
                {
                    textBox.SelectAll();
                }), System.Windows.Threading.DispatcherPriority.Input);
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
            else if (e.Key == Key.Escape)
            {
                var textBox = sender as TextBox;
                if (textBox?.Tag is NodeSocket socket && socket.ParentNode is Vector3Node vector3Node)
                {
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
        /// フォーカス取得時にテキストを全選択
        /// </summary>
        private void Vector3TextBox_GotFocus(object sender, RoutedEventArgs e)
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
                    return;
                }
                
                if (float.TryParse(textBox.Text, out float value))
                {
                    vector3Node.SetSocketValue(socket.Name, value);
                    textBox.Text = value.ToString("G");
                }
                else
                {
                    // パース失敗時は現在の値に戻す
                    textBox.Text = vector3Node.GetSocketValue(socket.Name).ToString("G");
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
            else if (e.Key == Key.Escape)
            {
                var textBox = sender as TextBox;
                if (textBox?.Tag is NodeSocket socket && socket.ParentNode is Vector4Node vector4Node)
                {
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
        /// フォーカス取得時にテキストを全選択（Vector4用）
        /// </summary>
        private void Vector4TextBox_GotFocus(object sender, RoutedEventArgs e)
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
                    return;
                }
                
                if (float.TryParse(textBox.Text, out float value))
                {
                    vector4Node.SetSocketValue(socket.Name, value);
                    textBox.Text = value.ToString("G");
                }
                else
                {
                    // パース失敗時は現在の値に戻す
                    textBox.Text = vector4Node.GetSocketValue(socket.Name).ToString("G");
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
    }
}

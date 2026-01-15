using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RayTraceVS.WPF.ViewModels;
using RayTraceVS.WPF.Models;

namespace RayTraceVS.WPF.Views
{
    public partial class NodeEditorView : UserControl
    {
        private Point lastMousePosition;
        private bool isPanning = false;
        private bool isDraggingNode = false;
        private bool isDraggingConnection = false;
        private Node? draggedNode = null;
        private NodeSocket? draggedSocket = null;
        private Ellipse? draggedSocketElement = null;
        private Point dragStartOffset;
        private Line? previewLine = null;
        
        // パン・ズーム用
        private TranslateTransform panTransform = new TranslateTransform();
        private ScaleTransform zoomTransform = new ScaleTransform();
        private TransformGroup transformGroup = new TransformGroup();
        
        private double currentZoom = 1.0;
        private const double MinZoom = 0.1;
        private const double MaxZoom = 5.0;
        private const double ZoomSpeed = 0.001;

        public NodeEditorView()
        {
            InitializeComponent();
            
            // トランスフォームを設定
            transformGroup.Children.Add(zoomTransform);
            transformGroup.Children.Add(panTransform);
            NodeCanvas.RenderTransform = transformGroup;
            NodeCanvas.RenderTransformOrigin = new Point(0, 0);
            
            // ロード後にフォーカスを設定
            Loaded += (s, e) =>
            {
                NodeCanvas.Focus();
            };
            
            // DataContextの変更を監視
            DataContextChanged += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine($"NodeEditorView DataContext changed: {e.NewValue?.GetType().Name}");
            };
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
            System.Diagnostics.Debug.WriteLine($"Hit element: {hitElement?.GetType().Name}");
            if (hitElement != null)
            {
                var dataContext = (hitElement as FrameworkElement)?.DataContext;
                System.Diagnostics.Debug.WriteLine($"DataContext: {dataContext?.GetType().Name}");
            }
            
            // ソケットのクリックを検出
            var socket = FindVisualParent<Ellipse>(hitElement);
            if (socket != null && socket.DataContext is NodeSocket nodeSocket)
            {
                System.Diagnostics.Debug.WriteLine("Socket clicked!");
                
                // 入力ソケットの場合、既存の接続を確認
                if (nodeSocket.IsInput)
                {
                    var existingConnection = viewModel.Connections.FirstOrDefault(c => c.InputSocket == nodeSocket);
                    if (existingConnection != null)
                    {
                        // 既存の接続を削除し、出力ソケット側からドラッグを開始
                        var outputSocket = existingConnection.OutputSocket;
                        System.Diagnostics.Debug.WriteLine("Disconnecting and starting drag from output socket");
                        
                        if (outputSocket != null)
                        {
                            // 既存の接続線が持っている出力ソケットの位置を使用（最も正確）
                            Point savedOutputSocketPos = outputSocket.Position;
                            System.Diagnostics.Debug.WriteLine($"Output socket position from existing connection: {savedOutputSocketPos}");
                            
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
            System.Diagnostics.Debug.WriteLine($"Border found: {border != null}, DataContext: {border?.DataContext?.GetType().Name}");
            
            if (border != null && border.DataContext is Node node)
            {
                System.Diagnostics.Debug.WriteLine($"Node clicked: {node.Title}");
                
                // 前に選択されていたノードの選択を解除
                if (viewModel.SelectedNode != null)
                {
                    viewModel.SelectedNode.IsSelected = false;
                }
                
                // ノードの選択とドラッグ開始（強制的に更新）
                viewModel.SelectedNode = null; // 一度nullにしてから設定することで確実に変更通知を発生させる
                viewModel.SelectedNode = node;
                node.IsSelected = true;
                
                isDraggingNode = true;
                draggedNode = node;
                dragStartOffset = new Point(mousePos.X - node.Position.X, mousePos.Y - node.Position.Y);
                NodeCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }
            
            // 何もない場所をクリックした場合は選択解除
            if (viewModel.SelectedNode != null)
            {
                viewModel.SelectedNode.IsSelected = false;
                viewModel.SelectedNode = null;
            }
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"Canvas_MouseLeftButtonUp: isDraggingConnection={isDraggingConnection}");
            
            if (isDraggingConnection && draggedSocket != null)
            {
                // 接続先のソケットを探す
                var mousePos = e.GetPosition(NodeCanvas);
                var hitElement = NodeCanvas.InputHitTest(mousePos) as DependencyObject;
                
                System.Diagnostics.Debug.WriteLine($"HitElement type: {hitElement?.GetType().Name}");
                
                var targetSocket = FindVisualParent<Ellipse>(hitElement);
                
                if (targetSocket != null && targetSocket.DataContext is NodeSocket targetNodeSocket)
                {
                    System.Diagnostics.Debug.WriteLine($"Target socket found: {targetNodeSocket.Name}");
                    // 接続を作成（出力→入力のみ許可）
                    CreateConnection(draggedSocket, targetNodeSocket);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("No target socket found");
                }
                
                // プレビュー線を削除
                RemovePreviewLine();
            }
            
            // ドラッグ状態をリセット
            isDraggingNode = false;
            isDraggingConnection = false;
            draggedNode = null;
            draggedSocket = null;
            NodeCanvas.ReleaseMouseCapture();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            // ノードのドラッグ
            if (isDraggingNode && draggedNode != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var mousePos = e.GetPosition(NodeCanvas);
                draggedNode.Position = new Point(
                    mousePos.X - dragStartOffset.X,
                    mousePos.Y - dragStartOffset.Y
                );
                
                // 接続線を更新
                UpdateNodeConnections(draggedNode);
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
            // ズーム処理
            var mousePos = e.GetPosition(NodeCanvas);
            
            double zoomDelta = e.Delta * ZoomSpeed;
            double newZoom = currentZoom + zoomDelta;
            newZoom = Math.Max(MinZoom, Math.Min(MaxZoom, newZoom));
            
            if (newZoom != currentZoom)
            {
                // マウス位置を中心にズーム
                double scaleChange = newZoom / currentZoom;
                
                panTransform.X = mousePos.X - (mousePos.X - panTransform.X) * scaleChange;
                panTransform.Y = mousePos.Y - (mousePos.Y - panTransform.Y) * scaleChange;
                
                zoomTransform.ScaleX = newZoom;
                zoomTransform.ScaleY = newZoom;
                
                currentZoom = newZoom;
            }
            
            e.Handled = true;
        }

        private void Canvas_KeyDown(object sender, KeyEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return;
            
            // Deleteキーでノードを削除
            if (e.Key == Key.Delete && viewModel.SelectedNode != null)
            {
                var nodeToDelete = viewModel.SelectedNode;
                viewModel.SelectedNode = null;
                viewModel.RemoveNode(nodeToDelete);
                e.Handled = true;
            }
        }

        // ノード上でのマウスイベント
        private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return;

            var border = sender as Border;
            System.Diagnostics.Debug.WriteLine($"Border sender: {border}, DataContext type: {border?.DataContext?.GetType().Name}");
            
            if (border == null || !(border.DataContext is Node node)) return;

            System.Diagnostics.Debug.WriteLine($"Node_MouseLeftButtonDown: {node.Title}");

            var mousePos = e.GetPosition(NodeCanvas);
            
            // ソケットのクリックかどうか確認
            var hitElement = border.InputHitTest(e.GetPosition(border)) as DependencyObject;
            var socket = FindVisualParent<Ellipse>(hitElement);
            if (socket != null && socket.DataContext is NodeSocket nodeSocket)
            {
                System.Diagnostics.Debug.WriteLine("Socket clicked via Node handler!");
                
                // 入力ソケットの場合、既存の接続を確認
                if (nodeSocket.IsInput)
                {
                    var existingConnection = viewModel.Connections.FirstOrDefault(c => c.InputSocket == nodeSocket);
                    if (existingConnection != null)
                    {
                        // 既存の接続を削除し、出力ソケット側からドラッグを開始
                        var outputSocket = existingConnection.OutputSocket;
                        System.Diagnostics.Debug.WriteLine("Disconnecting and starting drag from output socket via Node handler");
                        
                        if (outputSocket != null)
                        {
                            // 既存の接続線が持っている出力ソケットの位置を使用（最も正確）
                            Point savedOutputSocketPos = outputSocket.Position;
                            System.Diagnostics.Debug.WriteLine($"Output socket position from existing connection (Node handler): {savedOutputSocketPos}");
                            
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

            // 前に選択されていたノードの選択を解除
            if (viewModel.SelectedNode != null)
            {
                viewModel.SelectedNode.IsSelected = false;
            }
            
            // ノードの選択とドラッグ開始（強制的に更新）
            viewModel.SelectedNode = null; // 一度nullにしてから設定することで確実に変更通知を発生させる
            viewModel.SelectedNode = node;
            node.IsSelected = true;
            
            isDraggingNode = true;
            draggedNode = node;
            dragStartOffset = new Point(mousePos.X - node.Position.X, mousePos.Y - node.Position.Y);
            border.CaptureMouse();
            e.Handled = true;
        }

        private void Node_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            if (border == null) return;

            System.Diagnostics.Debug.WriteLine($"Node_MouseLeftButtonUp: isDraggingConnection={isDraggingConnection}");

            if (isDraggingConnection && draggedSocket != null)
            {
                // Canvasに対してヒットテストを行う（グローバル座標で）
                var mousePos = e.GetPosition(NodeCanvas);
                var hitElement = NodeCanvas.InputHitTest(mousePos) as DependencyObject;
                
                System.Diagnostics.Debug.WriteLine($"Node hit element type: {hitElement?.GetType().Name}");
                
                var targetSocket = FindVisualParent<Ellipse>(hitElement);
                
                if (targetSocket != null && targetSocket.DataContext is NodeSocket targetNodeSocket)
                {
                    System.Diagnostics.Debug.WriteLine($"Node target socket found: {targetNodeSocket.Name}");
                    CreateConnection(draggedSocket, targetNodeSocket);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("Node: No target socket found");
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
                border.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void Node_MouseMove(object sender, MouseEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"Node_MouseMove called. isDraggingNode={isDraggingNode}, draggedNode={draggedNode?.Title}, LeftButton={e.LeftButton}");
            
            if (isDraggingNode && draggedNode != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var mousePos = e.GetPosition(NodeCanvas);
                var newPos = new Point(
                    mousePos.X - dragStartOffset.X,
                    mousePos.Y - dragStartOffset.Y
                );
                
                System.Diagnostics.Debug.WriteLine($"Moving node to: {newPos}");
                draggedNode.Position = newPos;
                
                UpdateNodeConnections(draggedNode);
                e.Handled = true;
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
                System.Diagnostics.Debug.WriteLine("接続失敗: 出力→入力の接続のみ許可されています");
                return; // 無効な接続
            }
            
            // 同じノード間の接続は禁止
            if (outputSocket.ParentNode == inputSocket.ParentNode)
            {
                System.Diagnostics.Debug.WriteLine("接続失敗: 同じノード内での接続は許可されていません");
                return;
            }
            
            // 型チェック: ソケットの型が互換性があるか確認
            if (!AreSocketTypesCompatible(outputSocket.SocketType, inputSocket.SocketType))
            {
                System.Diagnostics.Debug.WriteLine($"接続失敗: 型が互換性がありません ({outputSocket.SocketType} → {inputSocket.SocketType})");
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
                System.Diagnostics.Debug.WriteLine($"Output socket position: {outputSocket.Position}");
            }
            
            if (inputElement != null)
            {
                inputSocket.Position = GetSocketElementPosition(inputElement);
                System.Diagnostics.Debug.WriteLine($"Input socket position: {inputSocket.Position}");
            }
            
            // 既存の接続を確認（入力ソケットには1つの接続のみ）
            var existingConnection = viewModel.Connections.FirstOrDefault(c => c.InputSocket == inputSocket);
            if (existingConnection != null)
            {
                viewModel.RemoveConnection(existingConnection);
            }
            
            // 新しい接続を作成
            var connection = new NodeConnection(outputSocket, inputSocket);
            viewModel.AddConnection(connection);
            System.Diagnostics.Debug.WriteLine($"接続成功: {outputSocket.ParentNode?.Title}.{outputSocket.Name} ({outputSocket.Position}) → {inputSocket.ParentNode?.Title}.{inputSocket.Name} ({inputSocket.Position})");
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

            // ソケット位置を更新
            UpdateSocketPositions();
            
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
                NodeCanvas.Children.Remove(previewLine);
            }

            previewLine = new Line
            {
                Stroke = socket.SocketColor,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                IsHitTestVisible = false
            };

            // 初期位置は後で更新されるので、とりあえず0,0に設定
            previewLine.X1 = 0;
            previewLine.Y1 = 0;
            previewLine.X2 = 0;
            previewLine.Y2 = 0;

            NodeCanvas.Children.Add(previewLine);
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

            // 互換性に応じて色を変更
            if (isValidDirection && !isSameNode && isCompatible)
            {
                previewLine.Stroke = draggedSocket.SocketColor;
                previewLine.Opacity = 1.0;
            }
            else
            {
                previewLine.Stroke = new SolidColorBrush(Colors.Red);
                previewLine.Opacity = 0.5;
            }
        }

        // プレビュー線を削除
        private void RemovePreviewLine()
        {
            if (previewLine != null)
            {
                NodeCanvas.Children.Remove(previewLine);
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
                if (nodeContainer == null) continue;

                // 入力ソケットの位置を更新
                foreach (var socket in node.InputSockets)
                {
                    var socketElement = FindSocketElement(nodeContainer, socket);
                    if (socketElement != null)
                    {
                        socket.Position = GetSocketElementPosition(socketElement);
                    }
                }

                // 出力ソケットの位置を更新
                foreach (var socket in node.OutputSockets)
                {
                    var socketElement = FindSocketElement(nodeContainer, socket);
                    if (socketElement != null)
                    {
                        socket.Position = GetSocketElementPosition(socketElement);
                    }
                }
            }
        }

        // ノードのコンテナ要素を見つける
        private Border? FindNodeContainer(Node node)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(NodeLayer); i++)
            {
                var container = VisualTreeHelper.GetChild(NodeLayer, i) as ContentPresenter;
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
    }
}

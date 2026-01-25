using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        
        // 接続線のPath要素を管理（後方互換性のため維持）
        private Dictionary<NodeConnection, Path> connectionPaths = new Dictionary<NodeConnection, Path>();
        
        // 分割された接続線のPath要素を管理
        private Dictionary<NodeConnection, Path> middlePaths = new Dictionary<NodeConnection, Path>();
        private Dictionary<NodeConnection, Path> startSegmentPaths = new Dictionary<NodeConnection, Path>();
        private Dictionary<NodeConnection, Path> endSegmentPaths = new Dictionary<NodeConnection, Path>();

        public NodeEditorView()
        {
            InitializeComponent();
            
            // トランスフォームを設定
            transformGroup.Children.Add(zoomTransform);
            transformGroup.Children.Add(panTransform);
            NodeCanvas.RenderTransform = transformGroup;
            NodeCanvas.RenderTransformOrigin = new Point(0, 0);
            // 新しいレイヤーはNodeCanvas内にあるため、親のトランスフォームが適用される
            // 後方互換性のためConnectionLayerのトランスフォームも設定
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
                    viewModel.Connections.CollectionChanged += (_, __) =>
                    {
                        RefreshSceneNodeLayoutsAfterConnectionChange();
                    };
                }
            };
        }
        
        /// <summary>
        /// 接続線のPath要素を再構築（分割されたレイヤーに追加）
        /// </summary>
        private void RebuildConnectionPaths()
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return;

            // 既存の接続線Pathをすべて削除
            ClearAllConnectionPaths();

            // 新しい接続線Pathを追加
            foreach (var connection in viewModel.Connections)
            {
                AddConnectionToLayers(connection);
            }
        }

        /// <summary>
        /// すべての接続線Pathをクリア
        /// </summary>
        private void ClearAllConnectionPaths()
        {
            // SelectedConnectionLayer上の一部要素は保持（ドラッグ中プレビュー線など）
            var preservedPreview = previewLine;
            var preservedSelection = selectionRectangle;

            // 中間部分
            foreach (var path in middlePaths.Values)
            {
                MiddleConnectionLayer.Children.Remove(path);
            }
            middlePaths.Clear();

            // 始点側端部分
            foreach (var path in startSegmentPaths.Values)
            {
                EndSegmentLayer.Children.Remove(path);
            }
            startSegmentPaths.Clear();

            // 終点側端部分
            foreach (var path in endSegmentPaths.Values)
            {
                EndSegmentLayer.Children.Remove(path);
            }
            endSegmentPaths.Clear();

            // 選択ノード用レイヤー
            SelectedConnectionLayer.Children.Clear();

            if (preservedPreview != null)
            {
                SelectedConnectionLayer.Children.Add(preservedPreview);
            }
            if (preservedSelection != null)
            {
                SelectedConnectionLayer.Children.Add(preservedSelection);
            }

            // 後方互換性のため古いPathも削除
            foreach (var path in connectionPaths.Values)
            {
                ConnectionLayer.Children.Remove(path);
            }
            connectionPaths.Clear();
        }

        /// <summary>
        /// 接続線を適切なレイヤーに追加
        /// </summary>
        private void AddConnectionToLayers(NodeConnection connection)
        {
            var outputNode = connection.OutputSocket?.ParentNode;
            var inputNode = connection.InputSocket?.ParentNode;
            
            if (outputNode == null || inputNode == null)
                return;

            bool isOutputSelected = outputNode.IsSelected;
            bool isInputSelected = inputNode.IsSelected;
            bool isSelected = isOutputSelected || isInputSelected;

            if (isSelected)
            {
                // 選択ノードの接続線は全体をSelectedConnectionLayerに描画
                var fullPath = new Path
                {
                    Stroke = connection.ConnectionColor,
                    StrokeThickness = 3,
                    Data = connection.PathGeometry,
                    IsHitTestVisible = false,
                    Opacity = 0.9
                };
                SelectedConnectionLayer.Children.Add(fullPath);
                connectionPaths[connection] = fullPath;
                
                // 変更を監視
                connection.PropertyChanged += ConnectionPropertyChangedHandler;
            }
            else
            {
                // 非選択ノードの接続線は分割して描画
                
                // 中間部分（最下層）
                var middlePath = new Path
                {
                    Stroke = connection.ConnectionColor,
                    StrokeThickness = 3,
                    Data = connection.MiddlePathGeometry,
                    IsHitTestVisible = false,
                    Opacity = 0.9
                };
                MiddleConnectionLayer.Children.Add(middlePath);
                middlePaths[connection] = middlePath;

                // 始点側端部分（出力ノードのZIndexに合わせる）
                var startPath = new Path
                {
                    Stroke = connection.ConnectionColor,
                    StrokeThickness = 3,
                    Data = connection.StartSegmentGeometry,
                    IsHitTestVisible = false,
                    Opacity = 0.9
                };
                Panel.SetZIndex(startPath, outputNode.CreationIndex + 1);
                EndSegmentLayer.Children.Add(startPath);
                startSegmentPaths[connection] = startPath;

                // 終点側端部分（入力ノードのZIndexに合わせる）
                var endPath = new Path
                {
                    Stroke = connection.ConnectionColor,
                    StrokeThickness = 3,
                    Data = connection.EndSegmentGeometry,
                    IsHitTestVisible = false,
                    Opacity = 0.9
                };
                Panel.SetZIndex(endPath, inputNode.CreationIndex + 1);
                EndSegmentLayer.Children.Add(endPath);
                endSegmentPaths[connection] = endPath;

                // 変更を監視
                connection.PropertyChanged += ConnectionSegmentPropertyChangedHandler;
            }
        }

        /// <summary>
        /// 接続線のプロパティ変更ハンドラ（選択時用）
        /// </summary>
        private void ConnectionPropertyChangedHandler(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is NodeConnection connection && 
                e.PropertyName == nameof(NodeConnection.PathGeometry) && 
                connectionPaths.TryGetValue(connection, out var path))
            {
                path.Data = connection.PathGeometry;
            }
        }

        /// <summary>
        /// 接続線のプロパティ変更ハンドラ（非選択時の分割表示用）
        /// </summary>
        private void ConnectionSegmentPropertyChangedHandler(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is not NodeConnection connection)
                return;

            switch (e.PropertyName)
            {
                case nameof(NodeConnection.MiddlePathGeometry):
                    if (middlePaths.TryGetValue(connection, out var middlePath))
                        middlePath.Data = connection.MiddlePathGeometry;
                    break;
                case nameof(NodeConnection.StartSegmentGeometry):
                    if (startSegmentPaths.TryGetValue(connection, out var startPath))
                        startPath.Data = connection.StartSegmentGeometry;
                    break;
                case nameof(NodeConnection.EndSegmentGeometry):
                    if (endSegmentPaths.TryGetValue(connection, out var endPath))
                        endPath.Data = connection.EndSegmentGeometry;
                    break;
            }
        }

        /// <summary>
        /// 選択状態が変更されたときに接続線のレイヤーを更新
        /// </summary>
        private void UpdateConnectionLayersForSelectionChange()
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return;

            // 接続線を再構築
            RebuildConnectionPaths();
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
            catch (Exception ex)
            {
                // TransformToAncestor can fail if the element is not in the visual tree
                System.Diagnostics.Debug.WriteLine($"UpdateSocketPositionFromUI: 座標変換に失敗 - {ex.Message}");
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
                
                // 選択状態が変更されたので接続線のレイヤーを更新
                UpdateConnectionLayersForSelectionChange();
                
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
        
        /// <summary>
        /// IME有効時やSystemキー押下時の実キーを取得
        /// </summary>
        private static Key GetRealKey(KeyEventArgs e)
        {
            var key = e.Key;
            if (key == Key.ImeProcessed)
                key = e.ImeProcessedKey;
            else if (key == Key.System)
                key = e.SystemKey;
            return key;
        }

        /// <summary>
        /// PreviewKeyDown - コピー＆ペーストのショートカットを処理（トンネリングイベント）
        /// MainWindowで処理されなかった場合のフォールバック
        /// </summary>
        private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // TextBoxにフォーカスがある場合は、テキスト編集のCTRL+C/Vを優先
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            var key = GetRealKey(e);
            
            // CTRL+C: コピー
            if (key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                HandleCopy();
                e.Handled = true;
                return;
            }
            
            // CTRL+V: ペースト
            if (key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                HandlePaste();
                e.Handled = true;
                return;
            }
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
        
        #region コピー＆ペースト
        
        /// <summary>
        /// クリップボードにコピーするデータの形式
        /// </summary>
        private const string ClipboardFormat = "RayTraceVS.NodeClipboard";
        
        /// <summary>
        /// 選択されたノードをクリップボードにコピー（外部公開用）
        /// </summary>
        public void CopySelectedNodes()
        {
            HandleCopy();
        }
        
        /// <summary>
        /// クリップボードからノードをペースト（外部公開用）
        /// </summary>
        public void PasteNodes()
        {
            HandlePaste();
        }
        
        /// <summary>
        /// 選択されたノードをクリップボードにコピー
        /// </summary>
        private void HandleCopy()
        {
            if (selectedNodes.Count == 0) return;
            
            var viewModel = GetViewModel();
            if (viewModel == null) return;
            
            try
            {
                // 選択されたノードのIDセット
                var selectedNodeIds = new HashSet<Guid>(selectedNodes.Select(n => n.Id));
                
                // ノードをシリアライズ
                var nodeDataList = selectedNodes.Select(n => SerializeNodeForClipboard(n)).ToList();
                
                // 選択されたノード間の接続のみをシリアライズ
                var connectionDataList = viewModel.Connections
                    .Where(c => c.OutputSocket?.ParentNode != null && c.InputSocket?.ParentNode != null &&
                               selectedNodeIds.Contains(c.OutputSocket.ParentNode.Id) &&
                               selectedNodeIds.Contains(c.InputSocket.ParentNode.Id))
                    .Select(c => new ClipboardConnectionData
                    {
                        OutputNodeId = c.OutputSocket!.ParentNode!.Id,
                        OutputSocketName = c.OutputSocket.Name,
                        InputNodeId = c.InputSocket!.ParentNode!.Id,
                        InputSocketName = c.InputSocket.Name
                    })
                    .ToList();
                
                var clipboardData = new ClipboardData
                {
                    Nodes = nodeDataList,
                    Connections = connectionDataList
                };
                
                var json = JsonConvert.SerializeObject(clipboardData, Formatting.Indented);
                
                // クリップボードに設定
                var dataObject = new DataObject();
                dataObject.SetData(ClipboardFormat, json);
                dataObject.SetData(DataFormats.Text, json); // テキストとしてもコピー（デバッグ用）
                Clipboard.SetDataObject(dataObject, true);
                
                Debug.WriteLine($"コピー完了: {selectedNodes.Count}個のノード, {connectionDataList.Count}個の接続");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"コピー失敗: {ex.Message}");
                Debug.WriteLine($"スタックトレース: {ex.StackTrace}");
                System.Windows.MessageBox.Show(
                    $"ノードのコピーに失敗しました。\n{ex.Message}",
                    "コピーエラー",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }
        
        /// <summary>
        /// クリップボードからノードをペースト
        /// </summary>
        private void HandlePaste()
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return;
            
            try
            {
                // クリップボードからデータを取得
                var dataObject = Clipboard.GetDataObject();
                if (dataObject == null) return;
                
                string? json = null;
                if (dataObject.GetDataPresent(ClipboardFormat))
                {
                    json = dataObject.GetData(ClipboardFormat) as string;
                }
                else if (dataObject.GetDataPresent(DataFormats.Text))
                {
                    // テキストとして取得を試みる
                    json = dataObject.GetData(DataFormats.Text) as string;
                }
                
                if (string.IsNullOrEmpty(json)) return;
                
                var clipboardData = JsonConvert.DeserializeObject<ClipboardData>(json);
                if (clipboardData?.Nodes == null || clipboardData.Nodes.Count == 0) return;
                
                // マウス位置を取得（Canvas座標系）
                var mousePos = Mouse.GetPosition(NodeCanvas);
                
                // コピー元のノードの位置の中心を計算
                double minX = clipboardData.Nodes.Min(n => n.PositionX);
                double minY = clipboardData.Nodes.Min(n => n.PositionY);
                double maxX = clipboardData.Nodes.Max(n => n.PositionX);
                double maxY = clipboardData.Nodes.Max(n => n.PositionY);
                double centerX = (minX + maxX) / 2;
                double centerY = (minY + maxY) / 2;
                
                // オフセット（マウス位置を中心にペースト）
                double offsetX = mousePos.X - centerX;
                double offsetY = mousePos.Y - centerY;
                
                // 旧ID -> 新ノードのマッピング
                var idMapping = new Dictionary<Guid, Node>();
                var newNodes = new List<Node>();
                var newConnections = new List<NodeConnection>();
                
                // ノードをデシリアライズして新しいIDを割り当て
                foreach (var nodeData in clipboardData.Nodes)
                {
                    var node = DeserializeNodeFromClipboard(nodeData);
                    if (node != null)
                    {
                        // 新しい位置を設定
                        node.Position = new Point(nodeData.PositionX + offsetX, nodeData.PositionY + offsetY);
                        idMapping[nodeData.Id] = node;
                        newNodes.Add(node);
                    }
                }
                
                // 接続を復元
                if (clipboardData.Connections != null)
                {
                    foreach (var connData in clipboardData.Connections)
                    {
                        if (idMapping.TryGetValue(connData.OutputNodeId, out var outputNode) &&
                            idMapping.TryGetValue(connData.InputNodeId, out var inputNode))
                        {
                            var outputSocket = outputNode.OutputSockets.FirstOrDefault(s => s.Name == connData.OutputSocketName);
                            var inputSocket = inputNode.InputSockets.FirstOrDefault(s => s.Name == connData.InputSocketName);
                            
                            if (outputSocket != null && inputSocket != null)
                            {
                                newConnections.Add(new NodeConnection(outputSocket, inputSocket));
                            }
                        }
                    }
                }
                
                // 選択をクリア
                ClearAllSelections(viewModel);
                
                // コマンドとして実行（Undo対応）
                if (newNodes.Count > 0)
                {
                    var composite = new CompositeCommand($"{newNodes.Count}個のノードをペースト");
                    
                    foreach (var node in newNodes)
                    {
                        composite.Add(new AddNodeCommand(viewModel, node));
                    }
                    
                    foreach (var connection in newConnections)
                    {
                        composite.Add(new AddConnectionCommand(viewModel, connection));
                    }
                    
                    viewModel.CommandManager.Execute(composite);
                    
                    // ペーストしたノードを選択状態にする
                    foreach (var node in newNodes)
                    {
                        node.IsSelected = true;
                        selectedNodes.Add(node);
                    }
                    
                    Debug.WriteLine($"ペースト完了: {newNodes.Count}個のノード, {newConnections.Count}個の接続");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ペースト失敗: {ex.Message}");
                Debug.WriteLine($"スタックトレース: {ex.StackTrace}");
                System.Windows.MessageBox.Show(
                    $"ノードのペーストに失敗しました。\n{ex.Message}",
                    "ペーストエラー",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }
        
        /// <summary>
        /// ノードをクリップボード用にシリアライズ
        /// </summary>
        private ClipboardNodeData SerializeNodeForClipboard(Node node)
        {
            return new ClipboardNodeData
            {
                Id = node.Id,
                Type = node.GetType().Name,
                Title = node.Title,
                PositionX = node.Position.X,
                PositionY = node.Position.Y,
                Properties = SerializeNodeProperties(node)
            };
        }
        
        /// <summary>
        /// クリップボードデータからノードをデシリアライズ（新しいIDを割り当て）
        /// </summary>
        private Node? DeserializeNodeFromClipboard(ClipboardNodeData data)
        {
            Node? node = data.Type switch
            {
                nameof(SphereNode) => new SphereNode(),
                nameof(PlaneNode) => new PlaneNode(),
                nameof(BoxNode) => new BoxNode(),
                nameof(FBXMeshNode) => new FBXMeshNode(),
                nameof(CameraNode) => new CameraNode(),
                nameof(PointLightNode) => new PointLightNode(),
                nameof(AmbientLightNode) => new AmbientLightNode(),
                nameof(DirectionalLightNode) => new DirectionalLightNode(),
                nameof(MaterialBSDFNode) => new MaterialBSDFNode(),
                nameof(ColorNode) => new ColorNode(),
                nameof(EmissionMaterialNode) => new EmissionMaterialNode(),
                nameof(UniversalPBRNode) => new UniversalPBRNode(),
                nameof(SceneNode) => new SceneNode(),
                nameof(Vector3Node) => new Vector3Node(),
                nameof(Vector4Node) => new Vector4Node(),
                nameof(FloatNode) => new FloatNode(),
                nameof(AddNode) => new AddNode(),
                nameof(SubNode) => new SubNode(),
                nameof(MulNode) => new MulNode(),
                nameof(DivNode) => new DivNode(),
                nameof(TransformNode) => new TransformNode(),
                nameof(CombineTransformNode) => new CombineTransformNode(),
                _ => null
            };

            if (node != null)
            {
                // 新しいIDを自動的に生成（コンストラクタで）
                // 位置は呼び出し元で設定
                DeserializeNodeProperties(node, data.Properties);
            }

            return node;
        }
        
        /// <summary>
        /// ノードのプロパティをシリアライズ
        /// </summary>
        private Dictionary<string, object?> SerializeNodeProperties(Node node)
        {
            var properties = new Dictionary<string, object?>();

            switch (node)
            {
                case SphereNode sphere:
                    properties["Transform"] = sphere.ObjectTransform;
                    properties["Radius"] = sphere.Radius;
                    break;

                case PlaneNode plane:
                    properties["Transform"] = plane.ObjectTransform;
                    properties["Normal"] = plane.Normal;
                    break;

                case BoxNode box:
                    properties["Transform"] = box.ObjectTransform;
                    properties["Size"] = box.Size;
                    break;

                case FBXMeshNode fbxMesh:
                    properties["MeshName"] = fbxMesh.MeshName;
                    properties["Transform"] = fbxMesh.ObjectTransform;
                    break;

                case CameraNode camera:
                    properties["CameraPosition"] = camera.CameraPosition;
                    properties["LookAt"] = camera.LookAt;
                    properties["Up"] = camera.Up;
                    properties["FieldOfView"] = camera.FieldOfView;
                    properties["Near"] = camera.Near;
                    properties["Far"] = camera.Far;
                    properties["ApertureSize"] = camera.ApertureSize;
                    properties["FocusDistance"] = camera.FocusDistance;
                    break;

                case PointLightNode pointLight:
                    properties["LightPosition"] = pointLight.LightPosition;
                    properties["Color"] = pointLight.Color;
                    properties["Intensity"] = pointLight.Intensity;
                    properties["Attenuation"] = pointLight.Attenuation;
                    break;

                case AmbientLightNode ambientLight:
                    properties["Color"] = ambientLight.Color;
                    properties["Intensity"] = ambientLight.Intensity;
                    break;

                case DirectionalLightNode directionalLight:
                    properties["Direction"] = directionalLight.Direction;
                    properties["Color"] = directionalLight.Color;
                    properties["Intensity"] = directionalLight.Intensity;
                    break;

                case MaterialBSDFNode material:
                    properties["BaseColor"] = material.BaseColor;
                    properties["Metallic"] = material.Metallic;
                    properties["Roughness"] = material.Roughness;
                    properties["Transmission"] = material.Transmission;
                    properties["IOR"] = material.IOR;
                    properties["Emission"] = material.Emission;
                    break;

                case ColorNode color:
                    properties["R"] = color.R;
                    properties["G"] = color.G;
                    properties["B"] = color.B;
                    properties["A"] = color.A;
                    break;

                case EmissionMaterialNode emission:
                    properties["EmissionColor"] = emission.EmissionColor;
                    properties["Strength"] = emission.Strength;
                    properties["BaseColor"] = emission.BaseColor;
                    break;

                case UniversalPBRNode universalPBR:
                    properties["BaseColor"] = universalPBR.BaseColor;
                    properties["Metallic"] = universalPBR.Metallic;
                    properties["Roughness"] = universalPBR.Roughness;
                    properties["Emissive"] = universalPBR.Emissive;
                    break;

                case SceneNode sceneNode:
                    var objectSocketNames = sceneNode.InputSockets
                        .Where(s => s.SocketType == SocketType.Object)
                        .Select(s => s.Name)
                        .ToList();
                    var lightSocketNames = sceneNode.InputSockets
                        .Where(s => s.SocketType == SocketType.Light)
                        .Select(s => s.Name)
                        .ToList();
                    properties["ObjectSocketNames"] = objectSocketNames;
                    properties["LightSocketNames"] = lightSocketNames;
                    properties["SamplesPerPixel"] = sceneNode.SamplesPerPixel;
                    properties["MaxBounces"] = sceneNode.MaxBounces;
                    properties["TraceRecursionDepth"] = sceneNode.TraceRecursionDepth;
                    properties["Exposure"] = sceneNode.Exposure;
                    properties["ToneMapOperator"] = sceneNode.ToneMapOperator;
                    properties["DenoiserStabilization"] = sceneNode.DenoiserStabilization;
                    properties["ShadowStrength"] = sceneNode.ShadowStrength;
                    properties["EnableDenoiser"] = sceneNode.EnableDenoiser;
                    properties["Gamma"] = sceneNode.Gamma;
                    break;

                case Vector3Node vector3:
                    properties["X"] = vector3.X;
                    properties["Y"] = vector3.Y;
                    properties["Z"] = vector3.Z;
                    break;

                case Vector4Node vector4:
                    properties["X"] = vector4.X;
                    properties["Y"] = vector4.Y;
                    properties["Z"] = vector4.Z;
                    properties["W"] = vector4.W;
                    break;

                case FloatNode floatNode:
                    properties["Value"] = floatNode.Value;
                    break;

                case TransformNode transformNode:
                    properties["PositionX"] = transformNode.PositionX;
                    properties["PositionY"] = transformNode.PositionY;
                    properties["PositionZ"] = transformNode.PositionZ;
                    properties["RotationX"] = transformNode.RotationX;
                    properties["RotationY"] = transformNode.RotationY;
                    properties["RotationZ"] = transformNode.RotationZ;
                    properties["ScaleX"] = transformNode.ScaleX;
                    properties["ScaleY"] = transformNode.ScaleY;
                    properties["ScaleZ"] = transformNode.ScaleZ;
                    break;

                case CombineTransformNode:
                    break;
            }

            return properties;
        }

        /// <summary>
        /// ノードのプロパティをデシリアライズ
        /// </summary>
        private void DeserializeNodeProperties(Node node, Dictionary<string, object?>? properties)
        {
            if (properties == null) return;

            switch (node)
            {
                case SphereNode sphere:
                    if (properties.TryGetValue("Transform", out var sphereTransform))
                        sphere.ObjectTransform = ConvertToTransform(sphereTransform);
                    if (properties.TryGetValue("Radius", out var radius))
                        sphere.Radius = Convert.ToSingle(radius);
                    break;

                case PlaneNode plane:
                    if (properties.TryGetValue("Transform", out var planeTransform))
                        plane.ObjectTransform = ConvertToTransform(planeTransform);
                    if (properties.TryGetValue("Normal", out var normal))
                        plane.Normal = ConvertToVector3(normal);
                    break;

                case BoxNode box:
                    if (properties.TryGetValue("Transform", out var boxTransform))
                        box.ObjectTransform = ConvertToTransform(boxTransform);
                    if (properties.TryGetValue("Size", out var size))
                        box.Size = ConvertToVector3(size);
                    break;

                case FBXMeshNode fbxMesh:
                    if (properties.TryGetValue("MeshName", out var meshNameObj))
                        fbxMesh.MeshName = meshNameObj?.ToString() ?? "";
                    if (properties.TryGetValue("Transform", out var fbxTransform))
                        fbxMesh.ObjectTransform = ConvertToTransform(fbxTransform);
                    break;

                case CameraNode camera:
                    if (properties.TryGetValue("CameraPosition", out var camPos))
                        camera.CameraPosition = ConvertToVector3(camPos);
                    if (properties.TryGetValue("LookAt", out var lookAt))
                        camera.LookAt = ConvertToVector3(lookAt);
                    if (properties.TryGetValue("Up", out var up))
                        camera.Up = ConvertToVector3(up);
                    if (properties.TryGetValue("FieldOfView", out var fov))
                        camera.FieldOfView = Convert.ToSingle(fov);
                    if (properties.TryGetValue("Near", out var near))
                        camera.Near = Convert.ToSingle(near);
                    if (properties.TryGetValue("Far", out var far))
                        camera.Far = Convert.ToSingle(far);
                    if (properties.TryGetValue("ApertureSize", out var aperture))
                        camera.ApertureSize = Convert.ToSingle(aperture);
                    if (properties.TryGetValue("FocusDistance", out var focusDist))
                        camera.FocusDistance = Convert.ToSingle(focusDist);
                    break;

                case PointLightNode pointLight:
                    if (properties.TryGetValue("LightPosition", out var lightPos))
                        pointLight.LightPosition = ConvertToVector3(lightPos);
                    if (properties.TryGetValue("Color", out var pointLightColor))
                        pointLight.Color = ConvertToVector4(pointLightColor);
                    if (properties.TryGetValue("Intensity", out var pointIntensity))
                        pointLight.Intensity = Convert.ToSingle(pointIntensity);
                    if (properties.TryGetValue("Attenuation", out var attenuation))
                        pointLight.Attenuation = Convert.ToSingle(attenuation);
                    break;

                case AmbientLightNode ambientLight:
                    if (properties.TryGetValue("Color", out var ambientColor))
                        ambientLight.Color = ConvertToVector4(ambientColor);
                    if (properties.TryGetValue("Intensity", out var ambientIntensity))
                        ambientLight.Intensity = Convert.ToSingle(ambientIntensity);
                    break;

                case DirectionalLightNode directionalLight:
                    if (properties.TryGetValue("Direction", out var direction))
                        directionalLight.Direction = ConvertToVector3(direction);
                    if (properties.TryGetValue("Color", out var dirColor))
                        directionalLight.Color = ConvertToVector4(dirColor);
                    if (properties.TryGetValue("Intensity", out var dirIntensity))
                        directionalLight.Intensity = Convert.ToSingle(dirIntensity);
                    break;

                case MaterialBSDFNode material:
                    if (properties.TryGetValue("BaseColor", out var baseColor))
                        material.BaseColor = ConvertToVector4(baseColor);
                    if (properties.TryGetValue("Metallic", out var metallic))
                        material.Metallic = Convert.ToSingle(metallic);
                    if (properties.TryGetValue("Roughness", out var roughness))
                        material.Roughness = Convert.ToSingle(roughness);
                    if (properties.TryGetValue("Transmission", out var transmission))
                        material.Transmission = Convert.ToSingle(transmission);
                    if (properties.TryGetValue("IOR", out var ior))
                        material.IOR = Convert.ToSingle(ior);
                    if (properties.TryGetValue("Emission", out var emission))
                        material.Emission = ConvertToVector4(emission);
                    break;

                case ColorNode color:
                    if (properties.TryGetValue("R", out var r))
                        color.R = Convert.ToSingle(r);
                    if (properties.TryGetValue("G", out var g))
                        color.G = Convert.ToSingle(g);
                    if (properties.TryGetValue("B", out var b))
                        color.B = Convert.ToSingle(b);
                    if (properties.TryGetValue("A", out var a))
                        color.A = Convert.ToSingle(a);
                    break;

                case EmissionMaterialNode emissionMat:
                    if (properties.TryGetValue("EmissionColor", out var emissionColor))
                        emissionMat.EmissionColor = ConvertToVector4(emissionColor);
                    if (properties.TryGetValue("Strength", out var strength))
                        emissionMat.Strength = Convert.ToSingle(strength);
                    if (properties.TryGetValue("BaseColor", out var emissionBaseColor))
                        emissionMat.BaseColor = ConvertToVector4(emissionBaseColor);
                    break;

                case UniversalPBRNode universalPBR:
                    if (properties.TryGetValue("BaseColor", out var pbrBaseColor))
                        universalPBR.BaseColor = ConvertToVector4(pbrBaseColor);
                    if (properties.TryGetValue("Metallic", out var pbrMetallic))
                        universalPBR.Metallic = Convert.ToSingle(pbrMetallic);
                    if (properties.TryGetValue("Roughness", out var pbrRoughness))
                        universalPBR.Roughness = Convert.ToSingle(pbrRoughness);
                    if (properties.TryGetValue("Emissive", out var pbrEmissive))
                        universalPBR.Emissive = ConvertToVector3(pbrEmissive);
                    break;

                case SceneNode sceneNode:
                    if (properties.TryGetValue("ObjectSocketNames", out var objSocketNamesObj) && objSocketNamesObj is JArray objSocketArray)
                    {
                        var objectSocketNames = objSocketArray.ToObject<List<string>>() ?? new List<string>();
                        var existingObjectSockets = sceneNode.InputSockets.Where(s => s.SocketType == SocketType.Object).ToList();
                        foreach (var socket in existingObjectSockets)
                        {
                            sceneNode.InputSockets.Remove(socket);
                        }
                        foreach (var socketName in objectSocketNames)
                        {
                            sceneNode.AddNamedInputSocket(socketName, SocketType.Object);
                        }
                    }
                    
                    if (properties.TryGetValue("LightSocketNames", out var lightSocketNamesObj) && lightSocketNamesObj is JArray lightSocketArray)
                    {
                        var lightSocketNames = lightSocketArray.ToObject<List<string>>() ?? new List<string>();
                        var existingLightSockets = sceneNode.InputSockets.Where(s => s.SocketType == SocketType.Light).ToList();
                        foreach (var socket in existingLightSockets)
                        {
                            sceneNode.InputSockets.Remove(socket);
                        }
                        foreach (var socketName in lightSocketNames)
                        {
                            sceneNode.AddNamedInputSocket(socketName, SocketType.Light);
                        }
                    }
                    
                    sceneNode.RestoreSocketCounters();
                    
                    if (properties.TryGetValue("SamplesPerPixel", out var samplesObj))
                        sceneNode.SamplesPerPixel = Convert.ToInt32(samplesObj);
                    if (properties.TryGetValue("MaxBounces", out var bouncesObj))
                        sceneNode.MaxBounces = Convert.ToInt32(bouncesObj);
                    if (properties.TryGetValue("TraceRecursionDepth", out var depthObj))
                        sceneNode.TraceRecursionDepth = Convert.ToInt32(depthObj);
                    if (properties.TryGetValue("Exposure", out var exposureObj))
                        sceneNode.Exposure = Convert.ToSingle(exposureObj);
                    if (properties.TryGetValue("ToneMapOperator", out var toneMapObj))
                        sceneNode.ToneMapOperator = Convert.ToInt32(toneMapObj);
                    if (properties.TryGetValue("DenoiserStabilization", out var stabObj))
                        sceneNode.DenoiserStabilization = Convert.ToSingle(stabObj);
                    if (properties.TryGetValue("ShadowStrength", out var shadowObj))
                        sceneNode.ShadowStrength = Convert.ToSingle(shadowObj);
                    if (properties.TryGetValue("EnableDenoiser", out var denoiserObj))
                        sceneNode.EnableDenoiser = Convert.ToBoolean(denoiserObj);
                    if (properties.TryGetValue("Gamma", out var gammaObj))
                        sceneNode.Gamma = Convert.ToSingle(gammaObj);
                    break;

                case Vector3Node vector3:
                    if (properties.TryGetValue("X", out var x))
                        vector3.X = Convert.ToSingle(x);
                    if (properties.TryGetValue("Y", out var y))
                        vector3.Y = Convert.ToSingle(y);
                    if (properties.TryGetValue("Z", out var z))
                        vector3.Z = Convert.ToSingle(z);
                    break;

                case Vector4Node vector4:
                    if (properties.TryGetValue("X", out var v4x))
                        vector4.X = Convert.ToSingle(v4x);
                    if (properties.TryGetValue("Y", out var v4y))
                        vector4.Y = Convert.ToSingle(v4y);
                    if (properties.TryGetValue("Z", out var v4z))
                        vector4.Z = Convert.ToSingle(v4z);
                    if (properties.TryGetValue("W", out var v4w))
                        vector4.W = Convert.ToSingle(v4w);
                    break;

                case FloatNode floatNode:
                    if (properties.TryGetValue("Value", out var value))
                        floatNode.Value = Convert.ToSingle(value);
                    break;

                case TransformNode transformNode:
                    if (properties.TryGetValue("PositionX", out var posX))
                        transformNode.PositionX = Convert.ToSingle(posX);
                    if (properties.TryGetValue("PositionY", out var posY))
                        transformNode.PositionY = Convert.ToSingle(posY);
                    if (properties.TryGetValue("PositionZ", out var posZ))
                        transformNode.PositionZ = Convert.ToSingle(posZ);
                    if (properties.TryGetValue("RotationX", out var rotX))
                        transformNode.RotationX = Convert.ToSingle(rotX);
                    if (properties.TryGetValue("RotationY", out var rotY))
                        transformNode.RotationY = Convert.ToSingle(rotY);
                    if (properties.TryGetValue("RotationZ", out var rotZ))
                        transformNode.RotationZ = Convert.ToSingle(rotZ);
                    if (properties.TryGetValue("ScaleX", out var scaleX))
                        transformNode.ScaleX = Convert.ToSingle(scaleX);
                    if (properties.TryGetValue("ScaleY", out var scaleY))
                        transformNode.ScaleY = Convert.ToSingle(scaleY);
                    if (properties.TryGetValue("ScaleZ", out var scaleZ))
                        transformNode.ScaleZ = Convert.ToSingle(scaleZ);
                    break;

                case CombineTransformNode:
                    break;
            }
        }
        
        /// <summary>
        /// オブジェクトをVector3に変換
        /// </summary>
        private System.Numerics.Vector3 ConvertToVector3(object? obj)
        {
            if (obj == null)
                return System.Numerics.Vector3.Zero;
                
            if (obj is System.Numerics.Vector3 vec3)
                return vec3;
                
            if (obj is JObject jobj)
            {
                return new System.Numerics.Vector3(
                    jobj["X"]?.Value<float>() ?? 0,
                    jobj["Y"]?.Value<float>() ?? 0,
                    jobj["Z"]?.Value<float>() ?? 0
                );
            }
            
            return System.Numerics.Vector3.Zero;
        }
        
        /// <summary>
        /// オブジェクトをVector4に変換
        /// </summary>
        private System.Numerics.Vector4 ConvertToVector4(object? obj)
        {
            if (obj == null)
                return System.Numerics.Vector4.One;
                
            if (obj is System.Numerics.Vector4 vec4)
                return vec4;
                
            if (obj is JObject jobj)
            {
                return new System.Numerics.Vector4(
                    jobj["X"]?.Value<float>() ?? 0,
                    jobj["Y"]?.Value<float>() ?? 0,
                    jobj["Z"]?.Value<float>() ?? 0,
                    jobj["W"]?.Value<float>() ?? 1
                );
            }
            
            return System.Numerics.Vector4.One;
        }
        
        /// <summary>
        /// オブジェクトをTransformに変換
        /// </summary>
        private Models.Transform ConvertToTransform(object? obj)
        {
            if (obj == null)
                return Models.Transform.Identity;

            if (obj is Models.Transform transform)
                return transform;

            if (obj is JObject jobj)
            {
                var position = ConvertToVector3(jobj["Position"]);
                var rotationEuler = jobj["Rotation"] != null 
                    ? ConvertToVector3(jobj["Rotation"]) 
                    : ConvertToVector3(jobj["EulerAngles"]);
                var scale = ConvertToVector3(jobj["Scale"]);

                var result = new Models.Transform
                {
                    Position = position,
                    Scale = scale
                };
                result.EulerAngles = rotationEuler;
                return result;
            }

            return Models.Transform.Identity;
        }
        
        #endregion コピー＆ペースト
        
        #region クリップボード用データクラス
        
        /// <summary>
        /// クリップボードに保存するデータ
        /// </summary>
        private class ClipboardData
        {
            public List<ClipboardNodeData> Nodes { get; set; } = new();
            public List<ClipboardConnectionData> Connections { get; set; } = new();
        }
        
        /// <summary>
        /// ノードのクリップボードデータ
        /// </summary>
        private class ClipboardNodeData
        {
            public Guid Id { get; set; }
            public string Type { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public double PositionX { get; set; }
            public double PositionY { get; set; }
            public Dictionary<string, object?>? Properties { get; set; }
        }
        
        /// <summary>
        /// 接続のクリップボードデータ
        /// </summary>
        private class ClipboardConnectionData
        {
            public Guid OutputNodeId { get; set; }
            public string OutputSocketName { get; set; } = string.Empty;
            public Guid InputNodeId { get; set; }
            public string InputSocketName { get; set; } = string.Empty;
        }
        
        #endregion クリップボード用データクラス
        
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
            
            // 選択状態が変更されたので接続線のレイヤーを更新
            UpdateConnectionLayersForSelectionChange();
            
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

        /// <summary>
        /// SceneNodeのソケット追加後に位置と接続線を更新
        /// </summary>
        private void RefreshSceneNodeSocketLayout(SceneNode sceneNode)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                NodeCanvas.UpdateLayout();
                sceneNode.RenumberSceneSockets();
                UpdateAllSocketPositionsForNode(sceneNode);
                UpdateNodeConnections(sceneNode);
                RebuildConnectionPaths();
            }), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// 接続追加/削除後にSceneNodeのソケット位置と接続線を更新
        /// </summary>
        private void RefreshSceneNodeLayoutsAfterConnectionChange()
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                NodeCanvas.UpdateLayout();
                foreach (var sceneNode in viewModel.Nodes.OfType<SceneNode>())
                {
                    sceneNode.RenumberSceneSockets();
                    UpdateAllSocketPositionsForNode(sceneNode);
                    UpdateNodeConnections(sceneNode);
                }
                RebuildConnectionPaths();
            }), DispatcherPriority.Loaded);
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
                bool socketAdded = false;
                if (inputSocket.SocketType == SocketType.Object)
                {
                    // 空のオブジェクトソケットがあるかチェック
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
                    // 空のライトソケットがあるかチェック
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
                    RefreshSceneNodeSocketLayout(sceneNode);
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
                SelectedConnectionLayer.Children.Remove(previewLine);
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

            SelectedConnectionLayer.Children.Add(previewLine);
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
                SelectedConnectionLayer.Children.Remove(previewLine);
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
            bool hadSelection = selectedNodes.Count > 0 || viewModel.SelectedNode != null;
            
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
            
            // 選択状態が変更された場合、接続線のレイヤーを更新
            if (hadSelection)
            {
                UpdateConnectionLayersForSelectionChange();
            }
        }

        /// <summary>
        /// 矩形選択用の矩形を作成
        /// </summary>
        private void CreateSelectionRectangle(Point startPoint)
        {
            if (selectionRectangle != null)
            {
                SelectedConnectionLayer.Children.Remove(selectionRectangle);
            }

            selectionRectangle = new Rectangle
            {
                Stroke = BrushCache.Get(100, 150, 255),
                StrokeThickness = 1,
                Fill = BrushCache.Get(30, 100, 150, 255),
                IsHitTestVisible = false
            };

            SelectedConnectionLayer.Children.Add(selectionRectangle);
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
                SelectedConnectionLayer.Children.Remove(selectionRectangle);
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
            
            // 選択状態が変更されたので接続線のレイヤーを更新
            if (selectedNodes.Count > 0)
            {
                UpdateConnectionLayersForSelectionChange();
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
                ApplyFloatTextBoxValue(textBox);
            }
        }

        private void ApplyFloatTextBoxValue(TextBox textBox)
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
                if (focusedElement.Tag is NodeSocket socket)
                {
                    if (socket.ParentNode is Vector3Node)
                    {
                        ApplyVector3TextBoxValue(focusedElement);
                    }
                    else if (socket.ParentNode is Vector4Node)
                    {
                        ApplyVector4TextBoxValue(focusedElement);
                    }
                    else if (socket.ParentNode is ColorNode)
                    {
                        ApplyColorTextBoxValue(focusedElement);
                    }
                }
                else if (focusedElement.DataContext is FloatNode)
                {
                    ApplyFloatTextBoxValue(focusedElement);
                }
                else
                {
                    var binding = BindingOperations.GetBindingExpression(focusedElement, TextBox.TextProperty);
                    binding?.UpdateSource();
                }
                
                // フォーカスをクリア（別のノードをクリックしたときにテキストボックスのフォーカスが残らないようにする）
                Keyboard.ClearFocus();
            }
        }
    }
}

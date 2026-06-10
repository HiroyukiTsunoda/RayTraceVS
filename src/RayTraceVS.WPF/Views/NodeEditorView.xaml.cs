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
using RayTraceVS.WPF.Services;
using RayTraceVS.WPF.Utils;
using RayTraceVS.WPF.Views.Handlers;

namespace RayTraceVS.WPF.Views
{
    public partial class NodeEditorView : UserControl
    {
        // ハンドラーと共有状態
        private readonly EditorInputState _inputState;
        private readonly CoordinateTransformer _coordTransformer;
        private readonly PanZoomHandler _panZoomHandler;
        private readonly SelectionHandler _selectionHandler;
        private readonly NodeDragHandler _nodeDragHandler;
        private readonly ConnectionHandler _connectionHandler;
        private readonly TextBoxInputHandler _textBoxInputHandler;
        private readonly EditCommandHandler _editCommandHandler;
        
        // EditorInputState へのショートカットプロパティ（移行期間中の互換性のため）
        private Point lastMousePosition { get => _inputState.LastMousePosition; set => _inputState.LastMousePosition = value; }
        private bool isPanning { get => _inputState.IsPanning; set => _inputState.IsPanning = value; }
        private bool isDraggingNode { get => _inputState.IsDraggingNode; set => _inputState.IsDraggingNode = value; }
        private bool isDraggingConnection { get => _inputState.IsDraggingConnection; set => _inputState.IsDraggingConnection = value; }
        private bool isRectSelecting { get => _inputState.IsRectSelecting; set => _inputState.IsRectSelecting = value; }
        private Node? draggedNode { get => _inputState.DraggedNode; set => _inputState.DraggedNode = value; }
        private NodeSocket? draggedSocket { get => _inputState.DraggedSocket; set => _inputState.DraggedSocket = value; }
        private Ellipse? draggedSocketElement { get => _inputState.DraggedSocketElement; set => _inputState.DraggedSocketElement = value; }
        private Point dragStartOffset { get => _inputState.DragStartOffset; set => _inputState.DragStartOffset = value; }
        private Line? previewLine { get => _inputState.PreviewLine; set => _inputState.PreviewLine = value; }
        
        // 複数選択関連
        private HashSet<Node> selectedNodes => _inputState.SelectedNodes;
        private Point rectSelectStartPoint { get => _inputState.RectSelectStartPoint; set => _inputState.RectSelectStartPoint = value; }
        private Rectangle? selectionRectangle { get => _inputState.SelectionRectangle; set => _inputState.SelectionRectangle = value; }
        private Dictionary<Node, Point> multiDragOffsets => _inputState.MultiDragOffsets;
        
        // パラメーター変更のUndo用（TextBoxのフォーカス取得時に変更前の値を記録）
        private Dictionary<TextBox, float> _textBoxOriginalValues => _inputState.TextBoxOriginalValues;
        
        // パン・ズーム用
        private TranslateTransform panTransform => _inputState.PanTransform;
        private ScaleTransform zoomTransform => _inputState.ZoomTransform;
        private TransformGroup transformGroup => _inputState.TransformGroup;
        
        private double currentZoom { get => _inputState.CurrentZoom; set => _inputState.CurrentZoom = value; }
        private const double MinZoom = EditorInputState.MinZoom;
        private const double MaxZoom = EditorInputState.MaxZoom;
        private const double ZoomSpeed = EditorInputState.ZoomSpeed;

        // UIキャッシュ（パフォーマンス最適化用）
        // VisualTree探索を避けるため、ノード/ソケットとUI要素の対応をキャッシュ
        private readonly Dictionary<Guid, Border> _nodeContainerCache = new();
        private readonly Dictionary<Guid, Ellipse> _socketElementCache = new();

        public NodeEditorView()
        {
            InitializeComponent();
            
            // ハンドラーと共有状態を初期化
            _inputState = new EditorInputState();
            _inputState.GetViewModel = GetViewModel;
            
            // 座標変換を一元管理するトランスフォーマーを初期化
            _coordTransformer = new CoordinateTransformer(_inputState, this);
            
            _panZoomHandler = new PanZoomHandler(_inputState);
            _panZoomHandler.SetCoordinateTransformer(_coordTransformer);
            
            _selectionHandler = new SelectionHandler(_inputState)
            {
                OnSelectionChanged = UpdateConnectionLayersForSelectionChange,
                GetNodeSize = GetNodeSizeForSelection
            };
            _nodeDragHandler = new NodeDragHandler(_inputState, OnNodeMoved)
            {
                OnRequestLayoutUpdate = () => NodeCanvas.UpdateLayout()
            };
            _connectionHandler = new ConnectionHandler(_inputState);
            _textBoxInputHandler = new TextBoxInputHandler(_inputState)
            {
                ClearFocusToCanvas = () => { Keyboard.ClearFocus(); NodeCanvas.Focus(); },
                GetViewModel = GetViewModel
            };
            _editCommandHandler = new EditCommandHandler(_inputState)
            {
                GetViewModel = GetViewModel,
                ClearSelections = vm => _selectionHandler.ClearAllSelections(vm),
                GetCurrentCanvasPosition = () => _coordTransformer.GetCurrentCanvasPosition()
            };
            
            // UIコンポーネント参照を設定
            _inputState.NodeCanvas = NodeCanvas;
            _inputState.PreviewLayer = PreviewLayer;
            
            // トランスフォームを設定
            NodeCanvas.RenderTransform = transformGroup;
            NodeCanvas.RenderTransformOrigin = new Point(0, 0);
            
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
        /// ノードが移動されたときのコールバック（ハンドラーから呼び出される）
        /// </summary>
        private void OnNodeMoved(Node node)
        {
            // 実際のUI要素から位置を取得して更新
            // （固定値計算ではUIとずれる可能性があるため）
            // UpdateNodeConnectionsがソケット位置の更新＋接続線更新を行う
            UpdateNodeConnections(node);
        }
        
        /// <summary>
        /// 選択判定用のノードサイズを取得するコールバック
        /// </summary>
        private Size? GetNodeSizeForSelection(Node node)
        {
            var container = FindNodeContainer(node);
            if (container != null)
            {
                return new Size(container.ActualWidth, container.ActualHeight);
            }
            return null;
        }
        
        /// <summary>
        /// 選択状態が変更されたときに接続線のレイヤーを更新
        /// </summary>
        private void UpdateConnectionLayersForSelectionChange()
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return;

            // ItemsControlのフィルタリングビューを更新
            viewModel.RefreshConnectionViews();
            
            // 選択されたノードがSelectedNodeLayerに移動するため、
            // ノードコンテナのキャッシュをクリアして再探索を強制
            InvalidateNodeContainerCache(selectedNodes);
            
            // 選択されたノードがSelectedNodeLayerに移動するため、
            // レイアウト完了後にソケット位置を更新する必要がある
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var node in selectedNodes)
                {
                    UpdateAllSocketPositionsForNode(node);
                }
            }), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// ソケットのEllipse要素が読み込まれたとき
        /// </summary>
        private void Socket_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Ellipse ellipse && ellipse.DataContext is NodeSocket socket)
            {
                // ソケット要素をキャッシュに登録（VisualTree探索を回避）
                RegisterSocketElementToCache(ellipse, socket);
                
                // Ellipse要素からソケットの実際の位置を取得
                UpdateSocketPositionFromUI(ellipse, socket);
            }
        }

        /// <summary>
        /// ソケットにマウスが乗ったときにポップアップで型情報を表示
        /// </summary>
        private void Socket_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse ellipse && ellipse.DataContext is NodeSocket socket)
            {
                // ソケットの型名を取得して表示
                SocketTypeText.Text = socket.SocketType.ToString();
                
                // ポップアップを表示
                SocketTypePopup.IsOpen = true;
            }
        }

        /// <summary>
        /// ソケットからマウスが離れたときにポップアップを閉じる
        /// </summary>
        private void Socket_MouseLeave(object sender, MouseEventArgs e)
        {
            // ポップアップを閉じる
            SocketTypePopup.IsOpen = false;
        }

        /// <summary>
        /// ソケットのUI要素から実際の位置を取得して設定
        /// </summary>
        private void UpdateSocketPositionFromUI(Ellipse ellipse, NodeSocket socket)
        {
            try
            {
                // CoordinateTransformerを使用してEllipseの中心座標を取得
                var centerPoint = _coordTransformer.GetElementCenterOnCanvas(ellipse);
                socket.Position = centerPoint;
            }
            catch (Exception ex)
            {
                // GetElementCenterOnCanvas can fail if the element is not in the visual tree
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

            // UIキャッシュをクリア（Undo/Redoで構造が変わっている可能性があるため）
            ClearAllUICache();
            
            // 接続インデックスを再構築（Undo/Redoで接続が変わっている可能性があるため）
            viewModel.RebuildNodeConnectionIndex();

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
                connection.UpdateIsSelected();
            }
            
            // 接続線のフィルタリングビューを更新
            viewModel.RefreshConnectionViews();
        }

        /// <summary>
        /// すべてのノードのTextBox値を更新（Undo/Redo後に使用）
        /// </summary>
        public void RefreshNodeTextBoxValues()
        {
            // Vector3Node, Vector4Node, FloatNodeのTextBox値を更新
            foreach (var textBox in TextBoxInputHandler.FindVisualChildren<TextBox>(NodeCanvas))
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

            var mousePos = _coordTransformer.GetCanvasPosition(e);
            
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
                        // 出力ソケット側からドラッグを開始（接続は削除せず記憶のみ）
                        var outputSocket = existingConnection.OutputSocket;
                        
                        if (outputSocket != null)
                        {
                            // 既存の接続線が持っている出力ソケットの位置を使用（最も正確）
                            Point savedOutputSocketPos = outputSocket.Position;
                            
                            Ellipse? outputSocketElement = null;
                            
                            // UI要素も探しておく（ドラッグ中の更新用）
                            var outputNode = outputSocket.ParentNode;
                            if (outputNode != null)
                            {
                                var outputNodeContainer = FindNodeContainer(outputNode);
                                if (outputNodeContainer != null)
                                {
                                    outputSocketElement = FindSocketElement(outputNodeContainer, outputSocket);
                                }
                            }
                            
                            // 接続のドラッグ開始（ハンドラーに委譲、接続は削除せず記憶のみ）
                            _connectionHandler.StartConnectionDragFromExisting(
                                existingConnection, 
                                outputSocket, 
                                outputSocketElement, 
                                savedOutputSocketPos);
                            
                            NodeCanvas.CaptureMouse();
                            e.Handled = true;
                            return;
                        }
                    }
                }
                
                // 接続のドラッグ開始（ハンドラーに委譲、新規接続）
                var socketPos = GetSocketElementPosition(socket);
                _connectionHandler.StartConnectionDrag(nodeSocket, socket, socketPos);
                
                NodeCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }
            
            // ノードのクリックを検出
            var border = FindVisualParent<Border>(hitElement);
            
            if (border != null && border.DataContext is Node node)
            {
                
                // 既に選択されているノードをクリックした場合は複数選択を維持してドラッグ開始（ハンドラーに委譲）
                if (selectedNodes.Contains(node))
                {
                    _nodeDragHandler.StartDrag(node, mousePos, selectedNodes);
                    NodeCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
                
                // 新しいノードを単一選択（ハンドラーに委譲）
                _selectionHandler.SelectNode(node, viewModel);
                
                // ノードがSelectedNodeLayerに移動するため、レイアウトを強制更新
                NodeCanvas.UpdateLayout();
                
                // ソケット位置を更新（レイアウト更新後でないと正しい位置が取得できない）
                UpdateAllSocketPositionsForNode(node);
                
                // ノードドラッグ開始（ハンドラーに委譲）
                _nodeDragHandler.StartDrag(node, mousePos, new[] { node });
                
                NodeCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }
            
            // 何もない場所をクリックした場合は矩形選択開始（ハンドラーに委譲）
            _selectionHandler.ClearAllSelections(viewModel);
            _selectionHandler.StartRectSelection(mousePos);
            NodeCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // 矩形選択終了（ハンドラーに委譲）
            if (isRectSelecting)
            {
                var viewModel = GetViewModel();
                var mousePos = _coordTransformer.GetCanvasPosition(e);
                _selectionHandler.EndRectSelection(viewModel, mousePos);
                NodeCanvas.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }
            
            if (isDraggingConnection && draggedSocket != null)
            {
                // 接続先のソケットを探す（拡張ヒット判定）
                var mousePos = _coordTransformer.GetCanvasPosition(e);
                var (targetElement, targetNodeSocket) = FindNearestSocket(mousePos);
                
                if (targetNodeSocket != null)
                {
                    // 接続を作成（出力→入力のみ許可）
                    CreateConnection(draggedSocket, targetNodeSocket);
                }
                else
                {
                    // 何もない場所にドロップした場合、元接続を削除（Undo可能）
                    _connectionHandler.RemoveOriginalConnection(GetViewModel());
                }

                // 接続ドラッグを終了（ハンドラーに委譲）
                _connectionHandler.CancelConnectionDrag();
            }

            // ノードドラッグ終了時の処理（ハンドラーに委譲）
            if (_nodeDragHandler.IsDragging)
            {
                var viewModel = GetViewModel();
                _nodeDragHandler.EndDrag(viewModel?.CommandManager);
            }
            
            // ドラッグ状態をリセット
            isDraggingConnection = false;
            draggedSocket = null;
            NodeCanvas.ReleaseMouseCapture();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            // 矩形選択中（ハンドラーに委譲）
            if (isRectSelecting && e.LeftButton == MouseButtonState.Pressed)
            {
                var mousePos = _coordTransformer.GetCanvasPosition(e);
                _selectionHandler.UpdateRectSelection(mousePos);
                e.Handled = true;
                return;
            }
            
            // ノードのドラッグ（ハンドラーに委譲）
            if (_nodeDragHandler.IsDragging && e.LeftButton == MouseButtonState.Pressed)
            {
                var mousePos = _coordTransformer.GetCanvasPosition(e);
                _nodeDragHandler.UpdateDrag(mousePos);
                e.Handled = true;
                return;
            }
            
            // 接続のドラッグ中（ハンドラーに委譲）
            if (_connectionHandler.IsDragging && e.LeftButton == MouseButtonState.Pressed)
            {
                var mousePos = _coordTransformer.GetCanvasPosition(e);
                _connectionHandler.UpdateConnectionDrag(mousePos);
                
                // ホバーしているソケットをチェックして、互換性を表示（拡張ヒット判定）
                var (targetElement, targetNodeSocket) = FindNearestSocket(mousePos);
                if (targetNodeSocket != null)
                {
                    _connectionHandler.UpdatePreviewLineCompatibility(targetNodeSocket);
                }
                else
                {
                    // ソケットから離れた場合はデフォルトの色に戻す
                    _connectionHandler.ResetPreviewLineColor();
                }
                
                e.Handled = true;
                return;
            }
            
            // パン操作（ハンドラーに委譲）
            if (isPanning && e.RightButton == MouseButtonState.Pressed)
            {
                _panZoomHandler.UpdatePan(_coordTransformer.GetScreenPosition(e));
                e.Handled = true;
            }
        }

        private void Canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // パン開始（ハンドラーに委譲）
            _panZoomHandler.StartPan(_coordTransformer.GetScreenPosition(e));
            NodeCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // ズーム処理（ハンドラーに委譲）
            _panZoomHandler.HandleZoom(e, _coordTransformer.GetScreenPosition(e));
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

            // CTRL+C: コピー（ハンドラーに委譲）
            if (key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                _editCommandHandler.HandleCopy();
                e.Handled = true;
                return;
            }

            // CTRL+V: ペースト（ハンドラーに委譲）
            if (key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                _editCommandHandler.HandlePaste();
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
            // Deleteキーでノードを削除（ハンドラーに委譲）
            _editCommandHandler.HandleDeleteKey(e);
        }
        
        #region コピー＆ペースト

        /// <summary>
        /// 選択されたノードをクリップボードにコピー（外部公開用、実装はEditCommandHandler）
        /// </summary>
        public void CopySelectedNodes()
        {
            _editCommandHandler.HandleCopy();
        }

        /// <summary>
        /// クリップボードからノードをペースト（外部公開用、実装はEditCommandHandler）
        /// </summary>
        public void PasteNodes()
        {
            _editCommandHandler.HandlePaste();
        }

        #endregion コピー＆ペースト
        
        /// <summary>
        /// 選択されたノードを削除（MainWindowから呼び出し用）
        /// </summary>
        public void DeleteSelectedNodes()
        {
            // ハンドラーに委譲
            _editCommandHandler.DeleteSelectedNodes();
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


            var mousePos = _coordTransformer.GetCanvasPosition(e);
            
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
                        // 出力ソケット側からドラッグを開始（接続は削除せず記憶のみ）
                        var outputSocket = existingConnection.OutputSocket;
                        
                        if (outputSocket != null)
                        {
                            // 既存の接続線が持っている出力ソケットの位置を使用（最も正確）
                            Point savedOutputSocketPos = outputSocket.Position;
                            
                            Ellipse? outputSocketElement = null;
                            
                            // UI要素も探しておく（ドラッグ中の更新用）
                            var outputNode = outputSocket.ParentNode;
                            if (outputNode != null)
                            {
                                var outputNodeContainer = FindNodeContainer(outputNode);
                                if (outputNodeContainer != null)
                                {
                                    outputSocketElement = FindSocketElement(outputNodeContainer, outputSocket);
                                }
                            }
                            
                            // 接続のドラッグ開始（ハンドラーに委譲、接続は削除せず記憶のみ）
                            _connectionHandler.StartConnectionDragFromExisting(
                                existingConnection, 
                                outputSocket, 
                                outputSocketElement, 
                                savedOutputSocketPos);
                            
                            // Canvasにマウスをキャプチャさせる（ノード全体ではなく）
                            NodeCanvas.CaptureMouse();
                            e.Handled = true;
                            return;
                        }
                    }
                }
                
                // 接続のドラッグ開始（ハンドラーに委譲、新規接続）
                var socketPos = GetSocketElementPosition(socket);
                _connectionHandler.StartConnectionDrag(nodeSocket, socket, socketPos);
                
                // Canvasにマウスをキャプチャさせる（ノード全体ではなく）
                NodeCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            // 既に選択されているノードをクリックした場合は複数選択を維持してドラッグ開始（ハンドラーに委譲）
            if (selectedNodes.Contains(node))
            {
                _nodeDragHandler.StartDrag(node, mousePos, selectedNodes);
                NodeCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }
            
            // 新しいノードを単一選択（ハンドラーに委譲）
            _selectionHandler.SelectNode(node, viewModel);
            
            // ノードがSelectedNodeLayerに移動するため、レイアウトを強制更新
            NodeCanvas.UpdateLayout();
            
            // ソケット位置を更新（レイアウト更新後でないと正しい位置が取得できない）
            UpdateAllSocketPositionsForNode(node);
            
            // ノードドラッグ開始（ハンドラーに委譲）
            _nodeDragHandler.StartDrag(node, mousePos, new[] { node });
            
            // NodeCanvasでマウスキャプチャ（Borderでキャプチャするとそのノードが最前面に来てしまう）
            NodeCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void Node_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            if (border == null) return;


            if (_connectionHandler.IsDragging)
            {
                // 接続先のソケットを探す（拡張ヒット判定）
                var mousePos = _coordTransformer.GetCanvasPosition(e);
                var (targetElement, targetNodeSocket) = FindNearestSocket(mousePos);
                
                if (targetNodeSocket != null && _connectionHandler.DraggedSocket != null)
                {
                    CreateConnection(_connectionHandler.DraggedSocket, targetNodeSocket);
                }
                else
                {
                    // 何もない場所にドロップした場合、元接続を削除（Undo可能）
                    _connectionHandler.RemoveOriginalConnection(GetViewModel());
                }

                // 接続ドラッグを終了（ハンドラーに委譲）
                _connectionHandler.CancelConnectionDrag();

                NodeCanvas.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }
            
            // ノードドラッグ終了（ハンドラーに委譲）
            if (_nodeDragHandler.IsDragging)
            {
                var viewModel = GetViewModel();
                _nodeDragHandler.EndDrag(viewModel?.CommandManager);
                // NodeCanvasでマウスリリース
                NodeCanvas.ReleaseMouseCapture();
                e.Handled = true;
            }
        }
        private void Node_MouseMove(object sender, MouseEventArgs e)
        {
            // ノードドラッグ更新（ハンドラーに委譲）
            if (_nodeDragHandler.IsDragging && e.LeftButton == MouseButtonState.Pressed)
            {
                var mousePos = _coordTransformer.GetCanvasPosition(e);
                _nodeDragHandler.UpdateDrag(mousePos);
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
                UpdateNodeConnections(sceneNode);
                GetViewModel()?.RefreshConnectionViews();
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
                    UpdateNodeConnections(sceneNode);
                }
                viewModel.RefreshConnectionViews();
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
            double maxDistanceSq = maxDistance * maxDistance;
            double nearestDistanceSq = double.MaxValue;
            // 粗カリング用マージン: NodeWidth/NodeHeightは概算値のため、
            // ソケットのはみ出し＋概算誤差を吸収できる十分大きな値にする
            double cullMargin = maxDistance + 50.0;

            foreach (var node in viewModel.Nodes)
            {
                // 粗カリング: ノード矩形から十分遠いノードはソケット探索をスキップ
                if (mousePos.X < node.Position.X - cullMargin ||
                    mousePos.X > node.Position.X + node.NodeWidth + cullMargin ||
                    mousePos.Y < node.Position.Y - cullMargin ||
                    mousePos.Y > node.Position.Y + node.NodeHeight + cullMargin)
                {
                    continue;
                }

                var nodeContainer = FindNodeContainer(node);
                if (nodeContainer == null) continue;

                // 入力ソケット
                foreach (var socket in node.InputSockets)
                {
                    var ellipse = FindSocketElement(nodeContainer, socket);
                    if (ellipse == null) continue;

                    var socketCenter = GetSocketElementPosition(ellipse);
                    double dx = mousePos.X - socketCenter.X;
                    double dy = mousePos.Y - socketCenter.Y;
                    double distanceSq = dx * dx + dy * dy;

                    if (distanceSq < nearestDistanceSq && distanceSq <= maxDistanceSq)
                    {
                        nearestDistanceSq = distanceSq;
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
                    double dx = mousePos.X - socketCenter.X;
                    double dy = mousePos.Y - socketCenter.Y;
                    double distanceSq = dx * dx + dy * dy;

                    if (distanceSq < nearestDistanceSq && distanceSq <= maxDistanceSq)
                    {
                        nearestDistanceSq = distanceSq;
                        nearestElement = ellipse;
                        nearestSocket = socket;
                    }
                }
            }

            return (nearestElement, nearestSocket);
        }

        // 接続を作成（方向判定・型互換・コマンド発行はConnectionHandlerに委譲、UI位置更新のみ担当）
        private void CreateConnection(NodeSocket source, NodeSocket target)
        {
            var viewModel = GetViewModel();
            if (viewModel == null) return;

            _connectionHandler.CreateConnectionFromDrag(
                source, target, viewModel,
                prepareSocketPositions: (outputSocket, inputSocket) =>
                {
                    // ドラッグした側の要素は記憶済み、もう一端はマウス位置のヒットテストから取得
                    Ellipse? outputElement = !source.IsInput ? draggedSocketElement : null;
                    Ellipse? inputElement = source.IsInput ? draggedSocketElement : null;

                    var mousePos = _coordTransformer.GetCurrentCanvasPosition();
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

                    // レイアウト更新を強制し、両端のノードのソケット位置をUIから更新
                    NodeCanvas.UpdateLayout();
                    if (outputSocket.ParentNode != null)
                    {
                        UpdateAllSocketPositionsForNode(outputSocket.ParentNode);
                    }
                    if (inputSocket.ParentNode != null)
                    {
                        UpdateAllSocketPositionsForNode(inputSocket.ParentNode);
                    }
                },
                refreshSceneNodeLayout: RefreshSceneNodeSocketLayout);
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
            
            // このノードに関連する接続線を更新（インデックスを使用してO(1)アクセス）
            var relatedConnections = viewModel.GetConnectionsForNode(node.Id);
            foreach (var connection in relatedConnections)
            {
                connection.UpdatePath();
            }
            
            // ConnectionLineRendererの再描画をリクエスト
            RequestConnectionRender();
        }
        
        /// <summary>
        /// 全てのConnectionLineRendererの再描画をリクエスト
        /// </summary>
        private void RequestConnectionRender()
        {
            MiddleConnectionRenderer?.RequestRender();
            EndSegmentRenderer?.RequestRender();
            SelectedConnectionRenderer?.RequestRender();
        }

        // ソケットの位置を取得（実際のUI要素から）
        private Point GetSocketElementPosition(Ellipse socketElement)
        {
            try
            {
                // CoordinateTransformerを使用してEllipseの中心座標を取得
                return _coordTransformer.GetElementCenterOnCanvas(socketElement);
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

        // ノードのコンテナ要素を見つける（キャッシュ使用）
        private Border? FindNodeContainer(Node node)
        {
            // キャッシュをチェック
            if (_nodeContainerCache.TryGetValue(node.Id, out var cached))
            {
                // キャッシュされた要素がまだ有効か確認
                if (cached.IsLoaded && cached.DataContext == node)
                {
                    return cached;
                }
                // キャッシュが無効な場合は削除
                _nodeContainerCache.Remove(node.Id);
            }

            // キャッシュになければVisualTree探索
            var result = FindNodeContainerInVisualTree(node);
            
            // 結果をキャッシュに保存
            if (result != null)
            {
                _nodeContainerCache[node.Id] = result;
            }
            
            return result;
        }

        // VisualTree探索でノードコンテナを見つける（内部用）
        private Border? FindNodeContainerInVisualTree(Node node)
        {
            // NodeLayerとSelectedNodeLayerの両方を検索
            // （選択されたノードはSelectedNodeLayerに移動するため）
            var layers = new[] { NodeLayer, SelectedNodeLayer };
            
            foreach (var layer in layers)
            {
                // レイヤー内のItemsControlを見つける
                var itemsControl = FindVisualChild<ItemsControl>(layer);
                if (itemsControl == null) continue;
                
                // ItemsControlのパネル（Canvas）を取得
                var panel = FindVisualChild<Canvas>(itemsControl);
                if (panel == null) continue;
                
                // パネル内のContentPresenterを探す
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(panel); i++)
                {
                    var container = VisualTreeHelper.GetChild(panel, i) as ContentPresenter;
                    if (container?.Content == node)
                    {
                        return FindVisualChild<Border>(container);
                    }
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

        #region UIキャッシュ管理

        /// <summary>
        /// 指定したノードのコンテナキャッシュを無効化
        /// （ノードがレイヤー間を移動したときに呼び出す）
        /// </summary>
        private void InvalidateNodeContainerCache(IEnumerable<Node> nodes)
        {
            foreach (var node in nodes)
            {
                _nodeContainerCache.Remove(node.Id);
                // ソケットのキャッシュも無効化（親コンテナが変わるため）
                foreach (var socket in node.InputSockets)
                {
                    _socketElementCache.Remove(socket.Id);
                }
                foreach (var socket in node.OutputSockets)
                {
                    _socketElementCache.Remove(socket.Id);
                }
            }
        }

        /// <summary>
        /// 指定したノードのキャッシュを完全に削除
        /// （ノードが削除されたときに呼び出す）
        /// </summary>
        private void RemoveNodeFromCache(Node node)
        {
            _nodeContainerCache.Remove(node.Id);
            foreach (var socket in node.InputSockets)
            {
                _socketElementCache.Remove(socket.Id);
            }
            foreach (var socket in node.OutputSockets)
            {
                _socketElementCache.Remove(socket.Id);
            }
        }

        /// <summary>
        /// 全てのUIキャッシュをクリア
        /// （Undo/Redoなど大規模な変更後に呼び出す）
        /// </summary>
        private void ClearAllUICache()
        {
            _nodeContainerCache.Clear();
            _socketElementCache.Clear();
        }

        /// <summary>
        /// ソケット要素をキャッシュに登録
        /// </summary>
        private void RegisterSocketElementToCache(Ellipse ellipse, NodeSocket socket)
        {
            _socketElementCache[socket.Id] = ellipse;
        }

        #endregion

        // ソケットのEllipse要素を見つける（キャッシュ使用）
        private Ellipse? FindSocketElement(Border nodeContainer, NodeSocket socket)
        {
            // キャッシュをチェック
            if (_socketElementCache.TryGetValue(socket.Id, out var cached))
            {
                // キャッシュされた要素がまだ有効か確認
                if (cached.IsLoaded && cached.DataContext == socket)
                {
                    return cached;
                }
                // キャッシュが無効な場合は削除
                _socketElementCache.Remove(socket.Id);
            }

            // キャッシュになければVisualTree探索
            var result = FindSocketElementInVisualTree(nodeContainer, socket);
            
            // 結果をキャッシュに保存
            if (result != null)
            {
                _socketElementCache[socket.Id] = result;
            }
            
            return result;
        }

        // VisualTree探索でソケット要素を見つける（内部用）
        private Ellipse? FindSocketElementInVisualTree(DependencyObject parent, NodeSocket socket)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                
                if (child is Ellipse ellipse && ellipse.DataContext == socket)
                    return ellipse;

                var result = FindSocketElementInVisualTree(child, socket);
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
                // CoordinateTransformerを使用してソケット要素の中心座標を取得
                return _coordTransformer.GetElementCenterOnCanvas(socketElement);
            }
            catch
            {
                // 変換に失敗した場合は計算で推定
                return GetSocketPosition(socket);
            }
        }

        // ======================================================================
        // TextBox編集ハンドラ（共通ロジックは _textBoxInputHandler に委譲）
        // ======================================================================

        // --- Float ---

        private void FloatTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandlePreviewTextInput(textBox, e);
        }

        private void FloatTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandleFloatTextBox_KeyDown(textBox, e);
        }

        private void FloatTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.ApplyFloatTextBoxValue(textBox);
        }

        private void FloatTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandleFloatTextBox_GotFocus(textBox);
        }

        // --- Vector3 ---

        private void Vector3TextBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandleSocketTextBox_Loaded(textBox);
        }

        private void Vector3TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandlePreviewTextInput(textBox, e);
        }

        private void Vector3TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandleSocketTextBox_KeyDown(textBox, e, supportsUndo: true);
        }

        private void Vector3TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandleSocketTextBox_LostFocus(textBox, supportsUndo: true);
        }

        private void Vector3TextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandleSocketTextBox_GotFocus(textBox, supportsUndo: true);
        }

        // --- Vector4 ---

        private void Vector4TextBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandleSocketTextBox_Loaded(textBox);
        }

        private void Vector4TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandlePreviewTextInput(textBox, e);
        }

        private void Vector4TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandleSocketTextBox_KeyDown(textBox, e, supportsUndo: true);
        }

        private void Vector4TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandleSocketTextBox_LostFocus(textBox, supportsUndo: true);
        }

        private void Vector4TextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandleSocketTextBox_GotFocus(textBox, supportsUndo: true);
        }

        // --- Color ---

        private void ColorTextBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandleSocketTextBox_Loaded(textBox);
        }

        private void ColorTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandlePreviewTextInput(textBox, e);
        }

        private void ColorTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandleSocketTextBox_KeyDown(textBox, e, supportsUndo: false);
        }

        private void ColorTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandleSocketTextBox_LostFocus(textBox, supportsUndo: false);
        }

        private void ColorTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
                _textBoxInputHandler.HandleSocketTextBox_GotFocus(textBox, supportsUndo: false);
        }

        // --- 共通 ---

        /// <summary>
        /// 現在フォーカスされている TextBox のバインディングを即座に更新。
        /// ノードエディター上をクリックした時に、プロパティパネルの入力値を確定させる。
        /// </summary>
        private void UpdateFocusedTextBoxBinding()
        {
            var focusedElement = Keyboard.FocusedElement as TextBox;
            if (focusedElement != null)
            {
                if (focusedElement.Tag is NodeSocket socket)
                {
                    // Vector3 / Vector4 は Undo あり、Color は Undo なし
                    bool supportsUndo = !(socket.ParentNode is ColorNode);
                    _textBoxInputHandler.ApplySocketTextBoxValue(focusedElement, supportsUndo);
                }
                else if (focusedElement.DataContext is FloatNode)
                {
                    _textBoxInputHandler.ApplyFloatTextBoxValue(focusedElement);
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

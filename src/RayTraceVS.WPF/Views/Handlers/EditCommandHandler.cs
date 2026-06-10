using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;
using RayTraceVS.WPF.Commands;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Services;
using RayTraceVS.WPF.ViewModels;

namespace RayTraceVS.WPF.Views.Handlers
{
    /// <summary>
    /// ノードエディタの編集コマンド（Delete/Copy/Paste）を担当するハンドラ
    /// コピー＆ペーストはSceneFileServiceのシリアライズを共用し、Undo対応のコマンドとして実行する
    /// </summary>
    public class EditCommandHandler
    {
        private readonly EditorInputState _state;

        /// <summary>
        /// クリップボードにコピーするデータの形式
        /// </summary>
        private const string ClipboardFormat = "RayTraceVS.NodeClipboard";

        /// <summary>
        /// クリップボードに保存するデータ（SceneFileServiceのNodeData/ConnectionDataを共用）
        /// </summary>
        private class ClipboardData
        {
            public List<SceneFileService.NodeData> Nodes { get; set; } = new();
            public List<SceneFileService.ConnectionData> Connections { get; set; } = new();
        }

        /// <summary>
        /// ViewModelを取得するコールバック
        /// </summary>
        public Func<MainViewModel?>? GetViewModel { get; set; }

        /// <summary>
        /// 選択をクリアするコールバック
        /// </summary>
        public Action<MainViewModel>? ClearSelections { get; set; }

        /// <summary>
        /// 現在のマウス位置（Canvas座標系）を取得するコールバック（ペースト位置の決定用）
        /// </summary>
        public Func<Point>? GetCurrentCanvasPosition { get; set; }

        public EditCommandHandler(EditorInputState state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        /// <summary>
        /// Deleteキーの処理
        /// </summary>
        /// <param name="e">キーイベント引数</param>
        /// <returns>処理が行われた場合はtrue</returns>
        public bool HandleDeleteKey(KeyEventArgs e)
        {
            if (e.Key == Key.Delete && _state.SelectedNodes.Count > 0)
            {
                DeleteSelectedNodes();
                e.Handled = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// キーボードショートカットの処理（Ctrl+C/V など）
        /// </summary>
        /// <param name="e">キーイベント引数</param>
        /// <returns>処理が行われた場合はtrue</returns>
        public bool HandleKeyboardShortcuts(KeyEventArgs e)
        {
            // Ctrl+C: コピー
            if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                HandleCopy();
                e.Handled = true;
                return true;
            }

            // Ctrl+V: ペースト
            if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                HandlePaste();
                e.Handled = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 選択されたノードを削除
        /// </summary>
        public void DeleteSelectedNodes()
        {
            var viewModel = GetViewModel?.Invoke();
            if (viewModel == null) return;

            if (_state.SelectedNodes.Count == 0) return;

            var nodesToDelete = _state.SelectedNodes.ToList();
            ClearSelections?.Invoke(viewModel);

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

        /// <summary>
        /// 選択されたノードをクリップボードにコピー
        /// </summary>
        public void HandleCopy()
        {
            if (_state.SelectedNodes.Count == 0) return;

            var viewModel = GetViewModel?.Invoke();
            if (viewModel == null) return;

            try
            {
                // 選択されたノードのIDセット
                var selectedNodeIds = new HashSet<Guid>(_state.SelectedNodes.Select(n => n.Id));

                // ノードをシリアライズ（SceneFileServiceの公開メソッドを共用）
                var nodeDataList = _state.SelectedNodes.Select(n => SceneFileService.SerializeNode(n)).ToList();

                // 選択されたノード間の接続のみをシリアライズ
                var connectionDataList = viewModel.Connections
                    .Where(c => c.OutputSocket?.ParentNode != null && c.InputSocket?.ParentNode != null &&
                               selectedNodeIds.Contains(c.OutputSocket.ParentNode.Id) &&
                               selectedNodeIds.Contains(c.InputSocket.ParentNode.Id))
                    .Select(c => new SceneFileService.ConnectionData
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

                Debug.WriteLine($"コピー完了: {_state.SelectedNodes.Count}個のノード, {connectionDataList.Count}個の接続");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"コピー失敗: {ex.Message}");
                Debug.WriteLine($"スタックトレース: {ex.StackTrace}");
                MessageBox.Show(
                    $"ノードのコピーに失敗しました。\n{ex.Message}",
                    "コピーエラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// クリップボードからノードをペースト（マウス位置を中心に配置、Undo対応）
        /// </summary>
        public void HandlePaste()
        {
            var viewModel = GetViewModel?.Invoke();
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
                var mousePos = GetCurrentCanvasPosition?.Invoke() ?? new Point(0, 0);

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

                // ノードをデシリアライズして新しいIDを割り当て（SceneFileService共用）
                foreach (var nodeData in clipboardData.Nodes)
                {
                    // DeserializeNodeは元のIDを設定するので、まずそれを使って復元
                    var node = SceneFileService.DeserializeNode(nodeData);
                    if (node != null)
                    {
                        // 旧IDを記録してから新しいIDを割り当て
                        idMapping[nodeData.Id] = node;
                        node.Id = Guid.NewGuid();
                        // 新しい位置を設定
                        node.Position = new Point(nodeData.PositionX + offsetX, nodeData.PositionY + offsetY);
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
                ClearSelections?.Invoke(viewModel);

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
                        _state.SelectedNodes.Add(node);
                    }

                    Debug.WriteLine($"ペースト完了: {newNodes.Count}個のノード, {newConnections.Count}個の接続");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ペースト失敗: {ex.Message}");
                Debug.WriteLine($"スタックトレース: {ex.StackTrace}");
                MessageBox.Show(
                    $"ノードのペーストに失敗しました。\n{ex.Message}",
                    "ペーストエラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 選択されているノードがあるかどうか
        /// </summary>
        public bool HasSelection => _state.SelectedNodes.Count > 0;

        /// <summary>
        /// 選択されているノードの数
        /// </summary>
        public int SelectionCount => _state.SelectedNodes.Count;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using RayTraceVS.WPF.Commands;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Data;
using RayTraceVS.WPF.Models.Nodes;
using RayTraceVS.WPF.ViewModels;
using RayTraceVS.WPF.Views.Handlers;
using EditorCommandManager = RayTraceVS.WPF.Commands.CommandManager;

public static class GraphCases
{
    public static void Run()
    {
        ParallelEdgesKeepDependencies();
        RemovingNodeInvalidatesScene();
        ConnectionHistoryKeepsIdentityAndSubscriptions();
        ReplacementSurvivesRepeatedHistory();
        GroupDeletionRestoresNodesBeforeConnections();
        NodeDeletionRestoresTrimmedSceneSockets();
        SceneSocketsSurviveHistory();
        RerouteIsOneUndoOperation();
        InvalidConnectionsLeaveGraphUnchanged();
        ClearRemovesGraphAndSubscriptions();
        SelectionTracksGraphAndCannotResurrectAbsentNodes();
        FailedCommandsKeepHistoryAndRollback();
        Console.WriteLine("PASS graph: dependencies, cache invalidation, connection lifecycle, grouped history, sockets, validation, reset");
    }

    private static void ParallelEdgesKeepDependencies()
    {
        var graph = new NodeGraph();
        var source = new FloatNode { Value = 2 };
        var sum = new AddNode();
        graph.AddNode(source);
        graph.AddNode(sum);
        var a = new NodeConnection(source.OutputSockets[0], sum.InputSockets[0]);
        var b = new NodeConnection(source.OutputSockets[0], sum.InputSockets[1]);
        graph.AddConnection(a);
        graph.AddConnection(b);
        Check((float)graph.EvaluateGraph()[sum.Id]! == 4, "Initial sum must use both edges");
        graph.RemoveConnection(a);
        Check((float)graph.EvaluateGraph()[sum.Id]! == 2, "Deleting one edge must invalidate the input");
        source.Value = 7;
        Check(graph.GetDownstreamNodes(source).Single() == sum, "Remaining parallel edge must preserve adjacency");
        Check((float)graph.EvaluateGraph()[sum.Id]! == 7, "Source edits must propagate through the remaining edge");
    }

    private static void RemovingNodeInvalidatesScene()
    {
        var vm = new MainViewModel();
        var sphere = new SphereNode();
        var scene = new SceneNode();
        vm.AddNode(sphere);
        vm.AddNode(scene);
        var connection = new NodeConnection(sphere.OutputSockets[0], scene.InputSockets.First(s => s.SocketType == SocketType.Object));
        vm.AddConnection(connection);
        Check(((SceneData)vm.NodeGraph.EvaluateGraph()[scene.Id]!).Objects.Count == 1, "Scene should contain the sphere");
        vm.RemoveNode(sphere);
        Check(((SceneData)vm.NodeGraph.EvaluateGraph()[scene.Id]!).Objects.Count == 0, "Removing a node must invalidate downstream scene cache");
        Check(!connection.InputSocket!.IsConnected, "Removed node must disconnect the scene socket");
        Check(vm.GetConnectionsForNode(scene.Id).Count == 0, "Removed node must clear the view-model connection index");
        AssertGraph(vm);
    }

    private static void ConnectionHistoryKeepsIdentityAndSubscriptions()
    {
        var (vm, source, target) = FloatGraph();
        var connection = new NodeConnection(source.OutputSockets[0], target.InputSockets[0]);
        var command = new AddConnectionCommand(vm, connection);
        vm.CommandManager.Execute(command);
        vm.CommandManager.Undo();
        source.IsSelected = true;
        Check(!connection.IsSelected, "Detached connection must unsubscribe from nodes");
        vm.CommandManager.Redo();
        Check(ReferenceEquals(vm.Connections.Single(), connection), "Redo must preserve the connection object");
        Check(connection.IsSelected, "Redo must refresh initial selection");
        source.IsSelected = false;
        Check(!connection.IsSelected, "Redo must restore selection subscriptions");
        var previousPath = connection.PathGeometry;
        source.OutputSockets[0].Position = new Point(75, 25);
        Check(!ReferenceEquals(previousPath, connection.PathGeometry), "Redo must restore position subscriptions");

        vm.CommandManager.Execute(new RemoveConnectionCommand(vm, connection));
        vm.CommandManager.Undo();
        Check(ReferenceEquals(vm.Connections.Single(), connection), "Remove undo must restore the original object");
        vm.CommandManager.Undo();
        Check(vm.Connections.Count == 0, "Undoing the preceding add must remove the restored connection");
        vm.CommandManager.Redo();
        vm.CommandManager.Redo();
        Check(vm.Connections.Count == 0, "Redoing add then remove must not leave a ghost connection");
        AssertGraph(vm);
    }

    private static void ReplacementSurvivesRepeatedHistory()
    {
        var (vm, source, target) = FloatGraph();
        var other = new FloatNode { Value = 8 };
        vm.AddNode(other);
        var before = new NodeConnection(source.OutputSockets[0], target.InputSockets[0]);
        var after = new NodeConnection(other.OutputSockets[0], target.InputSockets[0]);
        vm.AddConnection(before);
        vm.CommandManager.Execute(new ReplaceConnectionCommand(vm, before, after));
        for (int i = 0; i < 3; i++)
        {
            Check(ReferenceEquals(vm.Connections.Single(), after), "Replace redo must keep one new edge");
            vm.CommandManager.Undo();
            Check(ReferenceEquals(vm.Connections.Single(), before), "Replace undo must recover the old edge identity");
            AssertGraph(vm);
            vm.CommandManager.Redo();
            AssertGraph(vm);
        }
    }

    private static void GroupDeletionRestoresNodesBeforeConnections()
    {
        var (vm, source, target) = FloatGraph();
        var connection = new NodeConnection(source.OutputSockets[0], target.InputSockets[0]);
        vm.AddConnection(connection);
        vm.CommandManager.Execute(new RemoveNodesCommand(vm, new Node[] { source, target }));
        for (int i = 0; i < 3; i++)
        {
            Check(vm.Nodes.Count == 0 && vm.Connections.Count == 0, "Group delete must remove both endpoints and the shared edge");
            vm.CommandManager.Undo();
            Check(vm.Nodes.Count == 2 && ReferenceEquals(vm.Connections.Single(), connection), "Group undo must restore endpoints before the shared edge exactly once");
            AssertGraph(vm);
            vm.CommandManager.Redo();
        }
    }

    private static void NodeDeletionRestoresTrimmedSceneSockets()
    {
        var vm = new MainViewModel();
        var scene = new SceneNode();
        var first = new SphereNode();
        var second = new SphereNode();
        vm.AddNode(scene);
        vm.AddNode(first);
        vm.AddNode(second);
        var firstSocket = scene.InputSockets.Single(s => s.SocketType == SocketType.Object);
        vm.AddConnection(new NodeConnection(first.OutputSockets[0], firstSocket));
        var secondSocket = scene.InputSockets.Single(s => s.SocketType == SocketType.Object && !s.IsConnected);
        var connection = new NodeConnection(second.OutputSockets[0], secondSocket);
        vm.AddConnection(connection);
        // 先頭の入力を未接続にし、後方のノードを削除した後にUIの正規化で入力が除去される状態を作る。
        vm.RemoveConnection(vm.Connections.First(c => c.InputSocket == firstSocket));
        var before = scene.InputSockets.ToArray();
        vm.CommandManager.Execute(new RemoveNodeCommand(vm, second));
        for (int i = 0; i < 3; i++)
        {
            vm.EnsureSceneNodeSocketCounts();
            Check(!scene.InputSockets.Contains(secondSocket), "UI refresh should reproduce trimmed deleted-node input");
            vm.CommandManager.Undo();
            vm.EnsureSceneNodeSocketCounts();
            Check(scene.InputSockets.SequenceEqual(before) && vm.Connections.Contains(connection), "Node undo must restore trimmed scene socket identity");
            AssertGraph(vm);
            vm.CommandManager.Redo();
        }
    }

    private static void SceneSocketsSurviveHistory()
    {
        var vm = new MainViewModel();
        var sphere = new SphereNode();
        var other = new SphereNode();
        var scene = new SceneNode();
        vm.AddNode(sphere);
        vm.AddNode(other);
        vm.AddNode(scene);
        var before = scene.InputSockets.ToArray();
        var light = before.Single(s => s.SocketType == SocketType.Light);
        var connection = new NodeConnection(sphere.OutputSockets[0], before.Single(s => s.SocketType == SocketType.Object));
        vm.CommandManager.Execute(new AddConnectionCommand(vm, connection));
        var after = scene.InputSockets.ToArray();
        Check(after.Length == before.Length + 1, "Adding an object must create one spare socket");
        Check(ReferenceEquals(after.Last(), light), "Object socket insertion must preserve the existing light socket");
        for (int i = 0; i < 3; i++)
        {
            vm.CommandManager.Undo();
            vm.EnsureSceneNodeSocketCounts();
            Check(scene.InputSockets.SequenceEqual(before), "Add undo must restore the exact socket sequence");
            vm.CommandManager.Redo();
            vm.EnsureSceneNodeSocketCounts();
            Check(scene.InputSockets.SequenceEqual(after), "Add redo must reuse spare socket identity");
            AssertGraph(vm);
        }
        var second = new NodeConnection(other.OutputSockets[0], after.First(s => s.SocketType == SocketType.Object && !s.IsConnected));
        vm.CommandManager.Execute(new AddConnectionCommand(vm, second));
        var allSockets = scene.InputSockets.ToArray();
        vm.CommandManager.Execute(new RemoveConnectionCommand(vm, second));
        vm.EnsureSceneNodeSocketCounts();
        vm.CommandManager.Undo();
        vm.EnsureSceneNodeSocketCounts();
        Check(scene.InputSockets.SequenceEqual(allSockets), "Remove undo must restore even a trimmed spare socket");
        vm.CommandManager.Undo();
        vm.EnsureSceneNodeSocketCounts();
        Check(scene.InputSockets.SequenceEqual(after), "Undoing the previous add must still locate the same connection and socket");
        AssertGraph(vm);

        var replacement = new NodeConnection(other.OutputSockets[0], connection.InputSocket!);
        vm.CommandManager.Execute(new ReplaceConnectionCommand(vm, connection, replacement));
        for (int i = 0; i < 3; i++)
        {
            Check(scene.InputSockets.SequenceEqual(after), "Replacement must retain scene socket identity");
            vm.CommandManager.Undo();
            vm.EnsureSceneNodeSocketCounts();
            Check(ReferenceEquals(vm.Connections.Single(), connection), "Scene replacement undo must restore old connection");
            vm.CommandManager.Redo();
            vm.EnsureSceneNodeSocketCounts();
            Check(ReferenceEquals(vm.Connections.Single(), replacement), "Scene replacement redo must restore new connection");
            AssertGraph(vm);
        }
    }

    private static void RerouteIsOneUndoOperation()
    {
        var (vm, source, firstTarget) = FloatGraph();
        var secondSource = new FloatNode();
        var secondTarget = new AddNode();
        vm.AddNode(secondSource);
        vm.AddNode(secondTarget);
        var original = new NodeConnection(source.OutputSockets[0], firstTarget.InputSockets[0]);
        var occupied = new NodeConnection(secondSource.OutputSockets[0], secondTarget.InputSockets[0]);
        vm.AddConnection(original);
        vm.AddConnection(occupied);
        var handler = new ConnectionHandler(new EditorInputState());
        handler.StartConnectionDragFromExisting(original, source.OutputSockets[0], null, new Point(10, 10));
        Check(handler.CreateConnectionFromDrag(source.OutputSockets[0], secondTarget.InputSockets[0], vm, (_, _) => { }, _ => { }), "Reroute should connect");
        handler.CancelConnectionDrag();
        Check(vm.Connections.Count == 1, "Reroute must replace target and remove source edge");
        vm.CommandManager.Undo();
        Check(vm.Connections.Count == 2 && vm.Connections.Contains(original) && vm.Connections.Contains(occupied), "One undo must restore both edges");
        Check(!vm.CommandManager.CanUndo, "One drag must create one history entry");
        vm.CommandManager.Redo();
        Check(vm.Connections.Count == 1, "Reroute redo must apply both changes together");
        AssertGraph(vm);
    }

    private static void InvalidConnectionsLeaveGraphUnchanged()
    {
        var (vm, source, target) = FloatGraph();
        var connection = new NodeConnection(source.OutputSockets[0], target.InputSockets[0]);
        vm.AddConnection(connection);
        Expect<NodeGraphException>(() => vm.AddConnection(new NodeConnection(source.OutputSockets[0], target.InputSockets[0])));
        Expect<NodeGraphException>(() => vm.AddConnection(new NodeConnection(target.InputSockets[1], source.OutputSockets[0])));
        var absent = new FloatNode();
        Expect<NodeGraphException>(() => vm.AddConnection(new NodeConnection(absent.OutputSockets[0], target.InputSockets[1])));
        var next = new AddNode();
        vm.AddNode(next);
        vm.AddConnection(new NodeConnection(target.OutputSockets[0], next.InputSockets[0]));
        Expect<NodeGraphException>(() => vm.AddConnection(new NodeConnection(next.OutputSockets[0], target.InputSockets[1])));
        Check(!vm.NodeGraph.HasCycle(), "Invalid cycle must never enter the graph");
        var material = new MaterialBSDFNode();
        var scene = new SceneNode();
        vm.AddNode(material);
        vm.AddNode(scene);
        Expect<NodeGraphException>(() => vm.AddConnection(new NodeConnection(material.OutputSockets[0], scene.InputSockets.First(s => s.SocketType == SocketType.Object))));
        Check(vm.Connections.Count == 2, "Rejected connections must not alter any collection");
        AssertGraph(vm);
    }

    private static void ClearRemovesGraphAndSubscriptions()
    {
        var (vm, source, target) = FloatGraph();
        var connection = new NodeConnection(source.OutputSockets[0], target.InputSockets[0]);
        vm.AddConnection(connection);
        vm.SelectedNode = source;
        source.IsSelected = true;
        vm.NodeGraph.Clear();
        Check(vm.Nodes.Count == 0 && vm.Connections.Count == 0 && vm.NodeGraph.EvaluateGraph().Count == 0, "Clear must reset UI and evaluation together");
        Check(vm.SelectedNode == null && vm.SelectedNodes.Count == 0 && vm.SelectedNodeConnections.Count == 0, "Clear must reset selection views");
        Check(vm.GetConnectionsForNode(source.Id).Count == 0 && !target.InputSockets[0].IsConnected, "Clear must reset indices and socket state");
        vm.HasUnsavedChanges = false;
        source.Value = 13;
        Check(!vm.HasUnsavedChanges, "Cleared node must no longer send graph notifications");
    }

    private static void SelectionTracksGraphAndCannotResurrectAbsentNodes()
    {
        var vm = new MainViewModel();
        var state = new EditorInputState();
        var selection = new SelectionHandler(state);
        selection.ObserveGraph(vm);
        var node = new FloatNode();
        vm.CommandManager.Execute(new AddNodeCommand(vm, node));
        selection.SelectNode(node, vm);
        vm.CommandManager.Undo();
        Check(state.SelectedNodes.Count == 0, "Undoing an added selected node must remove its editor selection");

        // 別のハンドラーなどから古い選択が渡されても、Delete操作でRedo履歴を消去しない。
        state.SelectedNodes.Add(node);
        var edit = new EditCommandHandler(state)
        {
            GetViewModel = () => vm,
            ClearSelections = selection.ClearAllSelections
        };
        edit.DeleteSelectedNodes();
        Check(vm.Nodes.Count == 0 && !vm.CommandManager.CanUndo && vm.CommandManager.CanRedo,
            "Deleting an absent selection must not create a resurrection command or clear redo");
        vm.CommandManager.Redo();
        selection.SelectNode(node, vm);
        vm.NewScene();
        Check(state.SelectedNodes.Count == 0, "NewScene must clear editor selection references");
        var absentDeletion = new RemoveNodesCommand(vm, new[] { node });
        Check(!absentDeletion.CanUndo, "Absent nodes must not be captured by a deletion command");
        absentDeletion.Execute();
        absentDeletion.Undo();
        Check(vm.Nodes.Count == 0, "Undo of an absent-node deletion must not resurrect the node");

        var otherVm = new MainViewModel();
        var otherNode = new FloatNode { IsSelected = true };
        otherVm.AddNode(otherNode);
        selection.ObserveGraph(otherVm);
        vm.AddNode(node);
        vm.NewScene();
        Check(state.SelectedNodes.SetEquals(new[] { otherNode }), "Switching contexts must unsubscribe selection from the old graph");
        selection.ObserveGraph(null);
        Check(state.SelectedNodes.Count == 0, "Detaching the graph must clear selection");
    }

    private static void FailedCommandsKeepHistoryAndRollback()
    {
        var manager = new EditorCommandManager();
        int value = 0;
        bool failUndo = true;
        bool failRedo = false;
        var command = new DelegateCommand(() => { if (failRedo) throw new InvalidOperationException(); value++; }, () => { if (failUndo) throw new InvalidOperationException(); value--; });
        manager.Execute(command);
        Expect<InvalidOperationException>(() => manager.Undo());
        Check(manager.CanUndo && value == 1, "Failed undo must preserve history");
        failUndo = false;
        manager.Undo();
        failRedo = true;
        Expect<InvalidOperationException>(() => manager.Redo());
        Check(manager.CanRedo && value == 0, "Failed redo must preserve history");
        var composite = new CompositeCommand("failure");
        composite.Add(new DelegateCommand(() => value++, () => value--));
        composite.Add(new DelegateCommand(() => throw new InvalidOperationException(), () => { }));
        Expect<InvalidOperationException>(() => manager.Execute(composite));
        Check(value == 0 && manager.CanRedo, "Failed composite must roll back completed subcommands without changing history");
    }

    private static (MainViewModel Vm, FloatNode Source, AddNode Target) FloatGraph()
    {
        var vm = new MainViewModel();
        var source = new FloatNode { Value = 3 };
        var target = new AddNode();
        vm.AddNode(source);
        vm.AddNode(target);
        return (vm, source, target);
    }

    private static void AssertGraph(MainViewModel vm)
    {
        Check(ReferenceEquals(vm.Nodes, vm.NodeGraph.Nodes) && ReferenceEquals(vm.Connections, vm.NodeGraph.Connections), "UI must observe the graph's owned collections");
        Check(vm.Connections.Select(c => c.Id).Distinct().Count() == vm.Connections.Count, "Connection IDs must be unique");
        Check(vm.Connections.Select(c => c.InputSocket).Distinct().Count() == vm.Connections.Count, "Each input must have at most one edge");
        foreach (var node in vm.Nodes)
        {
            var expected = vm.Connections.Where(c => c.InputSocket?.ParentNode == node || c.OutputSocket?.ParentNode == node).ToHashSet();
            Check(expected.SetEquals(vm.GetConnectionsForNode(node.Id)), "View-model node index must match the graph");
            foreach (var socket in node.InputSockets)
                Check(socket.IsConnected == vm.Connections.Any(c => c.InputSocket == socket), "Socket state must match the graph");
        }
        foreach (var connection in vm.Connections)
        {
            Check(vm.Nodes.Contains(connection.OutputSocket!.ParentNode!) && vm.Nodes.Contains(connection.InputSocket!.ParentNode!), "Both connection endpoints must belong to the graph");
            Check(connection.InputSocket!.ParentNode!.InputSockets.Contains(connection.InputSocket), "Connected input must remain in its owning node");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private sealed class DelegateCommand : IEditorCommand
    {
        private readonly Action _execute;
        private readonly Action _undo;
        public DelegateCommand(Action execute, Action undo) { _execute = execute; _undo = undo; }
        public string Description => "test";
        public bool CanUndo => true;
        public void Execute() => _execute();
        public void Undo() => _undo();
    }
}

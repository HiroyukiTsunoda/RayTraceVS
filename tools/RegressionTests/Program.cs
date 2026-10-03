using System.Numerics;
using System.IO;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Nodes;
using RayTraceVS.WPF.Services;
using RayTraceVS.WPF.ViewModels;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            PersistenceCases.Run();
            GraphCases.Run();
            ViewModelAndEvaluationCases();
            Console.WriteLine("All regression cases passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void ViewModelAndEvaluationCases()
    {
        var vm = new MainViewModel();
        var source = new FloatNode();
        var sum = new AddNode();
        vm.AddNode(source);
        vm.AddNode(sum);
        var connection = new NodeConnection
        {
            OutputSocket = source.OutputSockets[0],
            InputSocket = sum.InputSockets[0]
        };
        vm.AddConnection(connection);
        Assert(ReferenceEquals(vm.Nodes, vm.NodeGraph.Nodes), "UI and graph share node ownership");
        vm.SelectedNode = source;
        source.IsSelected = true;
        vm.NodeGraph.RemoveNode(source);
        Assert(vm.Connections.Count == 0 && vm.GetConnectionsForNode(sum.Id).Count == 0,
            "direct graph deletion updates UI and indexes");
        Assert(vm.SelectedNode == null, "deleted node selection clears");
        vm.NewScene();
        Assert(vm.NodeGraph.GetAllNodes().Count() == 0 && vm.UnselectedNodes.Count == 0 &&
            vm.SelectedNodeConnections.Count == 0 && vm.UnselectedNodeConnections.Count == 0,
            "new scene clears evaluation and display state");
        source.Value = 19;
        Assert(!vm.HasUnsavedChanges, "removed nodes no longer dirty a new scene");
        var plane = new PlaneNode { Normal = Vector3.Normalize(Vector3.One) };
        vm.AddNode(plane);
        vm.HasUnsavedChanges = false;
        plane.Position = new System.Windows.Point(15, 25);
        Assert(vm.HasUnsavedChanges, "layout changes are persisted edits");
        var evaluator = new SceneEvaluator(null);
        var result = evaluator.EvaluateScene(vm.NodeGraph);
        Assert(result.Planes.Length == 1 && Math.Abs(result.Planes[0].Normal.X - plane.Normal.X) < 1e-6,
            "diagonal plane normal remains unchanged at origin");
        var scene = new SceneNode { Exposure = 2.5f };
        vm.AddNode(scene);
        result = evaluator.EvaluateScene(vm.NodeGraph);
        Assert(result.Planes.Length == 0 && result.Exposure == 2.5f,
            "disconnected Scene remains authoritative and retains render settings");

        var directory = Path.Combine(Path.GetTempPath(), "raytrace-vm-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "scene.rtvs");
            vm.SaveScene(path, new ViewportState());
            vm.LoadScene(path, _ => { });
            vm.LoadScene(path, _ => { });
            Assert(vm.Nodes.Count == 2 && vm.NodeGraph.GetAllNodes().Count() == 2,
                "repeated load replaces all old graph nodes");
            Assert(!vm.HasUnsavedChanges, "loaded scene starts clean");
            plane.Normal = Vector3.UnitZ;
            Assert(!vm.HasUnsavedChanges, "previous graph subscriptions are detached after load");
            var currentNodes = vm.Nodes.ToArray();
            try { vm.LoadScene(path, _ => throw new IOException("viewport failed")); }
            catch (IOException) { }
            Assert(vm.Nodes.SequenceEqual(currentNodes) && vm.CurrentFilePath == path,
                "viewport failure preserves the current document");

            var invalidPath = Path.Combine(directory, "invalid.rtvs");
            var left = new FloatNode();
            var right = new FloatNode();
            var add = new AddNode();
            new SceneFileService().SaveScene(invalidPath, new Node[] { left, right, add },
                new[] { new NodeConnection(left.OutputSockets[0], add.InputSockets[0]),
                        new NodeConnection(right.OutputSockets[0], add.InputSockets[0]) });
            bool invalidRejected = false;
            try { vm.LoadScene(invalidPath, _ => { }); }
            catch (NodeGraphException) { invalidRejected = true; }
            Assert(invalidRejected && vm.Nodes.SequenceEqual(currentNodes) && vm.CurrentFilePath == path,
                "invalid graph load preserves the current document");
        }
        finally { Directory.Delete(directory, true); }

        using var renderer = new RenderService();
        renderer.Dispose();
        bool rejected = false;
        try { renderer.Initialize(IntPtr.Zero, 16, 16); }
        catch (ObjectDisposedException) { rejected = true; }
        Assert(rejected, "disposed renderer cannot allocate another engine");
        Console.WriteLine("PASS view-model ownership, reload, evaluation, renderer lifetime");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

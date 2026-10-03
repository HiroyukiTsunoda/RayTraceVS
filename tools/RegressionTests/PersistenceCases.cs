using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RayTraceVS.WPF.Models;
using RayTraceVS.WPF.Models.Data;
using RayTraceVS.WPF.Models.Nodes;
using RayTraceVS.WPF.Models.Serialization;
using RayTraceVS.WPF.Services;

public static class PersistenceCases
{
    public static void Run()
    {
        TransformRoundTripPreservesQuaternion();
        LegacyTransformsKeepTheirMeaning();
        SceneSettingsAndSocketsRoundTrip();
        EvaluationPreservesEditableDefaults();
        SceneFilesPreserveUnavailableMeshes();
        MeshEvaluationUsesInjectedProvider();
        LegacySceneConnectionsRestoreSockets();
        InvalidNodeIdsAreRejected();
        InvalidConnectionReferencesAreRejected();
        FailedSavePreservesExistingFile();
        ResaveRejectsUnknownNodes();
        Console.WriteLine("PASS persistence: transforms, settings, sockets, evaluation, missing assets, IDs, atomic save");
    }

    private static void TransformRoundTripPreservesQuaternion()
    {
        var transform = Transform.Identity;
        transform.Position = new Vector3(3, -4, 5);
        transform.EulerAngles = new Vector3(35, 112, -63);
        transform.Scale = new Vector3(2, 3, 4);

        foreach (var node in new Node[]
        {
            new SphereNode { ObjectTransform = transform },
            new PlaneNode { ObjectTransform = transform },
            new BoxNode { ObjectTransform = transform },
            new FBXMeshNode("unavailable") { ObjectTransform = transform }
        })
        {
            node.Title = "Saved custom title";
            var data = SceneFileService.SerializeNode(node);
            for (int i = 0; i < 3; i++)
            {
                data = JsonConvert.DeserializeObject<SceneFileService.NodeData>(JsonConvert.SerializeObject(data))!;
                var restored = SceneFileService.DeserializeNode(data)!;
                Require(restored.Title == node.Title, "Node titles must survive reload.");
                var properties = SceneFileService.SerializeNodeProperties(restored);
                var actual = (Transform)properties["Transform"]!;
                Require(actual.Position == transform.Position && actual.Scale == transform.Scale,
                    "Transform position and scale changed on reload.");
                Require(Quaternion.Dot(actual.Rotation, transform.Rotation) > 0.999999f,
                    $"{node.GetType().Name} rotation changed on reload.");
                data = SceneFileService.SerializeNode(restored);
            }
        }
    }

    private static void LegacyTransformsKeepTheirMeaning()
    {
        var expected = Transform.Identity;
        expected.EulerAngles = new Vector3(10, 20, 30);
        var euler = SerializationHelpers.ConvertToTransform(JObject.Parse(
            "{ 'Position': { 'X': 2 }, 'Rotation': { 'X': 10, 'Y': 20, 'Z': 30 } }"));
        Require(euler.Scale == Vector3.One, "Missing legacy scale must default to identity.");
        Require(Quaternion.Dot(euler.Rotation, expected.Rotation) > 0.999999f,
            "Legacy Euler Rotation must remain in degrees.");

        expected.Position = new Vector3(1, 2, 3);
        expected.Scale = new Vector3(2, 2, 2);
        var lowerCase = SerializationHelpers.ConvertToTransform(expected.ToJson());
        Require(lowerCase.Position == expected.Position && lowerCase.Scale == expected.Scale &&
            Quaternion.Dot(lowerCase.Rotation, expected.Rotation) > 0.999999f,
            "Legacy lowercase quaternion JSON must retain its transform.");

        var eulerOnly = SerializationHelpers.ConvertToTransform(JObject.Parse(
            "{ 'EulerAngles': { 'X': 10, 'Y': 20, 'Z': 30 } }"));
        Require(Quaternion.Dot(eulerOnly.Rotation, expected.Rotation) > 0.999999f,
            "EulerAngles-only files must remain readable.");
    }

    private static void SceneSettingsAndSocketsRoundTrip()
    {
        var scene = new SceneNode
        {
            SamplesPerPixel = 5, MaxBounces = 6, TraceRecursionDepth = 7,
            Exposure = 1.25f, ToneMapOperator = 2, DenoiserStabilization = 0.75f,
            ShadowStrength = 0.8f, ShadowAbsorptionScale = 3.25f,
            EnableDenoiser = false, Gamma = 2.2f,
            LightAttenuationConstant = 2.5f, LightAttenuationLinear = 0.25f,
            LightAttenuationQuadratic = 0.125f, MaxShadowLights = 4,
            NRDBypassDistance = 12.5f, NRDBypassBlendRange = 3.5f
        };
        scene.AddObjectSocket();
        scene.AddLightSocket();
        var original = SceneFileService.SerializeNode(scene);
        var copies = new[]
        {
            SceneFileService.DeserializeNode(original)!,
            SceneFileService.DeserializeNode(JsonConvert.DeserializeObject<SceneFileService.NodeData>(
                JsonConvert.SerializeObject(original))!)!
        };
        foreach (var copy in copies)
        {
            var expected = JObject.FromObject(original.Properties!);
            var actual = JObject.FromObject(SceneFileService.SerializeNodeProperties(copy));
            Require(JToken.DeepEquals(expected, actual), "Scene settings or dynamic sockets changed on round-trip.");
            Require(copy.InputSockets.Select(s => s.Name).SequenceEqual(scene.InputSockets.Select(s => s.Name)),
                "In-memory and JSON copies must restore the same scene sockets.");
        }
        Require(original.Properties!.ContainsKey("ShadowAbsorptionScale") &&
            original.Properties.ContainsKey("NRDBypassBlendRange"), "All render settings must be serialized.");
    }

    private static void EvaluationPreservesEditableDefaults()
    {
        CheckEvaluation(new Vector3Node { X = 2, Y = 3, Z = 4 }, "X", 8f, new Vector3(8, 3, 4));
        CheckEvaluation(new Vector4Node { X = 2, Y = 3, Z = 4, W = 5 }, "W", 8f, new Vector4(2, 3, 4, 8));
        CheckEvaluation(new ColorNode { R = 0.2f, G = 0.3f, B = 0.4f, A = 0.5f }, "R", 2f,
            new Vector4(1, 0.3f, 0.4f, 0.5f));
    }

    private static void CheckEvaluation(Node node, string socketName, float input, object expected)
    {
        var before = JObject.FromObject(SceneFileService.SerializeNodeProperties(node));
        int notifications = 0;
        node.PropertyChanged += (_, _) => notifications++;
        var socket = node.InputSockets.Single(s => s.Name == socketName);
        var result = node.Evaluate(new Dictionary<Guid, object?> { [socket.Id] = input });
        Require(Equals(result, expected), "Connected values must still be evaluated.");
        Require(notifications == 0 && JToken.DeepEquals(before,
            JObject.FromObject(SceneFileService.SerializeNodeProperties(node))),
            "Rendering must not change editable defaults or fire property changes.");
    }

    private static void SceneFilesPreserveUnavailableMeshes()
    {
        WithTemporaryDirectory(directory =>
        {
            var mesh = new FBXMeshNode("missing-on-this-machine");
            var scene = new SceneNode();
            var connection = new NodeConnection(mesh.OutputSockets[0],
                scene.InputSockets.Single(s => s.SocketType == SocketType.Object));
            var path = Path.Combine(directory, "mesh.rtvs");
            var service = new SceneFileService(_ => false);
            service.SaveScene(path, new ObservableCollection<Node> { mesh, scene },
                new ObservableCollection<NodeConnection> { connection });
            var (nodes, connections, viewport) = service.LoadScene(path);
            Require(nodes.Count == 2 && connections.Count == 1 && service.MissingMeshInfos.Count == 1 &&
                service.RemovedNodeInfos.Count == 0, "Unavailable meshes and connections must be retained.");
            service.SaveScene(path, new ObservableCollection<Node>(nodes),
                new ObservableCollection<NodeConnection>(connections), viewport);
            var (resaved, resavedConnections, _) = new SceneFileService().LoadScene(path);
            Require(resaved.OfType<FBXMeshNode>().Single().MeshName == mesh.MeshName && resavedConnections.Count == 1,
                "Headless load/save must work without app or mesh-cache initialization.");
            var cliOutput = Path.Combine(directory, "resaved-mesh.rtvs");
            Require(SceneResaver.Resave(path, cliOutput) == 0,
                "The resave command must not require mesh-cache initialization.");
            var (cliNodes, cliConnections, _) = new SceneFileService().LoadScene(cliOutput);
            Require(cliNodes.OfType<FBXMeshNode>().Single().MeshName == mesh.MeshName && cliConnections.Count == 1,
                "The resave command must retain missing mesh references and their connections.");
        });
    }

    private static void LegacySceneConnectionsRestoreSockets()
    {
        WithTemporaryDirectory(directory =>
        {
            foreach (var socketName in new[] { "Object3", "オブジェクト3" })
            {
                var sphere = new SphereNode();
                var scene = new SceneNode();
                var data = new SceneFileService.SceneFileData
                {
                    Nodes = new() { SceneFileService.SerializeNode(sphere), SceneFileService.SerializeNode(scene) },
                    Connections = new()
                    {
                        new() { OutputNodeId = sphere.Id, OutputSocketName = "Object", InputNodeId = scene.Id,
                            InputSocketName = socketName }
                    }
                };
                data.Nodes[1].Properties!.Remove("ObjectSocketNames");
                var path = Path.Combine(directory, "legacy.rtvs");
                File.WriteAllText(path, JsonConvert.SerializeObject(data));
                var (_, connections, _) = new SceneFileService().LoadScene(path);
                Require(connections.Count == 1 && connections[0].InputSocket!.Name == socketName,
                    "Legacy scene files must restore referenced dynamic sockets.");
            }
        });
    }

    private static void MeshEvaluationUsesInjectedProvider()
    {
        var previousProvider = Node.MeshCacheProvider;
        try
        {
            Node.MeshCacheProvider = null;
            var mesh = new FBXMeshNode("injected-mesh");
            var graph = new NodeGraph();
            graph.AddNode(mesh);
            var missing = new SceneEvaluator(null).EvaluateScene(graph);
            Require(missing.MeshInstances.Length == 0, "A missing mesh must be omitted from rendering.");
            var resolved = new SceneEvaluator(new TestMeshProvider()).EvaluateScene(graph);
            Require(resolved.MeshInstances.Length == 1 && resolved.MeshCaches.Length == 1,
                "Injected cache must resolve a mesh even after a cached evaluation with no global cache.");
            graph.Clear();
        }
        finally { Node.MeshCacheProvider = previousProvider; }
    }

    private sealed class TestMeshProvider : IMeshCacheProvider
    {
        public CachedMeshData? GetMesh(string meshName) => meshName == "injected-mesh" ? new CachedMeshData
        {
            Vertices = new float[24],
            Indices = new uint[] { 0, 1, 2 },
            BoundsMin = Vector3.Zero,
            BoundsMax = Vector3.One
        } : null;
    }

    private static void InvalidNodeIdsAreRejected()
    {
        WithTemporaryDirectory(directory =>
        {
            var node = SceneFileService.SerializeNode(new FloatNode());
            var data = new SceneFileService.SceneFileData { Nodes = new() { node, node } };
            var path = Path.Combine(directory, "invalid.rtvs");
            File.WriteAllText(path, JsonConvert.SerializeObject(data));
            bool rejected = false;
            try { new SceneFileService().LoadScene(path); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Duplicate node IDs must be rejected before mutating the current scene.");
        });
    }

    private static void FailedSavePreservesExistingFile()
    {
        WithTemporaryDirectory(directory =>
        {
            var path = Path.Combine(directory, "existing.rtvs");
            const string original = "original scene bytes";
            File.WriteAllText(path, original);
            using (var lockedFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false;
                try { new SceneFileService().SaveScene(path, Array.Empty<Node>(), Array.Empty<NodeConnection>()); }
                catch (IOException) { failed = true; }
                Require(failed, "Replacing a locked destination must fail.");
                Require(File.ReadAllText(path) == original, "Failed save must preserve the existing file.");
            }
            Require(Directory.GetFiles(directory).Length == 1, "Failed saves must clean up their temporary file.");
        });
    }

    private static void InvalidConnectionReferencesAreRejected()
    {
        WithTemporaryDirectory(directory =>
        {
            var source = new FloatNode();
            var target = new AddNode();
            var data = new SceneFileService.SceneFileData
            {
                Nodes = new() { SceneFileService.SerializeNode(source), SceneFileService.SerializeNode(target) },
                Connections = new()
                {
                    new() { OutputNodeId = source.Id, OutputSocketName = "no-such-output",
                        InputNodeId = target.Id, InputSocketName = "A" }
                }
            };
            var path = Path.Combine(directory, "invalid-connection.rtvs");
            for (int i = 0; i < 2; i++)
            {
                if (i == 1) data.Connections[0].OutputNodeId = Guid.NewGuid();
                File.WriteAllText(path, JsonConvert.SerializeObject(data));
                bool rejected = false;
                try { new SceneFileService().LoadScene(path); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "Missing socket or node references must not silently discard connections.");
            }

            data.Nodes[0].Type = "FutureNode";
            data.Connections[0].OutputNodeId = source.Id;
            File.WriteAllText(path, JsonConvert.SerializeObject(data));
            var service = new SceneFileService();
            var (nodes, connections, _) = service.LoadScene(path);
            Require(nodes.Count == 1 && connections.Count == 0 && service.RemovedNodeInfos.Count == 1,
                "Connections to explicitly warned unsupported nodes may be omitted.");
        });
    }

    private static void ResaveRejectsUnknownNodes()
    {
        WithTemporaryDirectory(directory =>
        {
            var data = new SceneFileService.SceneFileData
            {
                Nodes = new() { new() { Id = Guid.NewGuid(), Type = "FutureNode", Title = "Future" } }
            };
            var path = Path.Combine(directory, "unknown.rtvs");
            var original = JsonConvert.SerializeObject(data);
            File.WriteAllText(path, original);
            Require(SceneResaver.Resave(path, path) != 0 && File.ReadAllText(path) == original,
                "Resave must refuse lossy conversion of unknown nodes, including in-place saves.");
        });
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "RayTraceVS-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally
        {
            foreach (var path in Directory.GetFiles(directory))
                File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

using System.Collections.Generic;
using System.Linq;

namespace RayTraceVS.WPF.Models.Nodes
{
    public class SceneNode : Node
    {
        public SceneNode() : base("シーン", NodeCategory.Scene)
        {
            AddInputSocket("カメラ", SocketType.Camera);
            AddInputSocket("オブジェクト1", SocketType.Object);
            AddInputSocket("オブジェクト2", SocketType.Object);
            AddInputSocket("オブジェクト3", SocketType.Object);
            AddInputSocket("ライト1", SocketType.Light);
            AddInputSocket("ライト2", SocketType.Light);
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            var cameraObj = GetInputValue<object>("カメラ", inputValues);
            
            var objects = new List<object>();
            var lights = new List<LightData>();

            // すべてのオブジェクト入力を収集
            for (int i = 1; i <= 3; i++)
            {
                var obj = GetInputValue<object>($"オブジェクト{i}", inputValues);
                if (obj != null)
                    objects.Add(obj);
            }

            // すべてのライト入力を収集
            for (int i = 1; i <= 2; i++)
            {
                var lightObj = GetInputValue<object>($"ライト{i}", inputValues);
                if (lightObj != null && lightObj is LightData light)
                    lights.Add(light);
            }

            return new SceneData
            {
                Camera = cameraObj is CameraData camera ? camera : default,
                Objects = objects,
                Lights = lights
            };
        }
    }

    public struct SceneData
    {
        public CameraData Camera;
        public List<object> Objects;
        public List<LightData> Lights;
    }
}

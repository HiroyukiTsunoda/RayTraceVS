using System.Collections.Generic;
using System.Linq;

namespace RayTraceVS.WPF.Models.Nodes
{
    public class SceneNode : Node
    {
        private int objectSocketCount = 0;
        private int lightSocketCount = 0;

        public SceneNode() : base("Scene", NodeCategory.Scene)
        {
            // カメラソケット（固定）
            AddInputSocket("Camera", SocketType.Camera);
            
            // 初期状態で1つずつのソケットを追加
            AddObjectSocket();
            AddLightSocket();
        }

        /// <summary>
        /// オブジェクト入力ソケットを動的に追加
        /// </summary>
        public void AddObjectSocket()
        {
            objectSocketCount++;
            AddInputSocket($"Object{objectSocketCount}", SocketType.Object);
        }

        /// <summary>
        /// ライト入力ソケットを動的に追加
        /// </summary>
        public void AddLightSocket()
        {
            lightSocketCount++;
            AddInputSocket($"Light{lightSocketCount}", SocketType.Light);
        }

        /// <summary>
        /// 指定した名前のソケットを追加（シーン読み込み時に使用）
        /// </summary>
        public void AddNamedInputSocket(string socketName, SocketType socketType)
        {
            AddInputSocket(socketName, socketType);
        }

        /// <summary>
        /// 指定した名前のソケットを削除
        /// </summary>
        public void RemoveSocket(string socketName)
        {
            var socket = InputSockets.FirstOrDefault(s => s.Name == socketName);
            if (socket != null)
            {
                InputSockets.Remove(socket);
            }
        }

        /// <summary>
        /// ソケット名からカウンタを復元（シーン読み込み時に使用）
        /// </summary>
        public void RestoreSocketCounters()
        {
            // オブジェクトソケットの最大インデックスを取得
            var objectSockets = InputSockets.Where(s => s.SocketType == SocketType.Object).ToList();
            if (objectSockets.Any())
            {
                var maxIndex = objectSockets
                    .Select(s => s.Name)
                    .Where(name => name.StartsWith("Object"))
                    .Select(name => 
                    {
                        var indexStr = name.Replace("Object", "");
                        return int.TryParse(indexStr, out var index) ? index : 0;
                    })
                    .DefaultIfEmpty(0)
                    .Max();
                objectSocketCount = maxIndex;
            }

            // ライトソケットの最大インデックスを取得
            var lightSockets = InputSockets.Where(s => s.SocketType == SocketType.Light).ToList();
            if (lightSockets.Any())
            {
                var maxIndex = lightSockets
                    .Select(s => s.Name)
                    .Where(name => name.StartsWith("Light"))
                    .Select(name => 
                    {
                        var indexStr = name.Replace("Light", "");
                        return int.TryParse(indexStr, out var index) ? index : 0;
                    })
                    .DefaultIfEmpty(0)
                    .Max();
                lightSocketCount = maxIndex;
            }
        }

        /// <summary>
        /// 空のオブジェクトソケットがあるかチェック
        /// </summary>
        public bool HasEmptyObjectSocket()
        {
            return InputSockets.Any(s => s.SocketType == SocketType.Object && !IsSocketConnected(s));
        }

        /// <summary>
        /// 空のライトソケットがあるかチェック
        /// </summary>
        public bool HasEmptyLightSocket()
        {
            return InputSockets.Any(s => s.SocketType == SocketType.Light && !IsSocketConnected(s));
        }

        /// <summary>
        /// ソケットが接続されているかチェック（外部から設定される）
        /// </summary>
        private bool IsSocketConnected(NodeSocket socket)
        {
            // この情報はNodeGraphまたはConnectionから取得する必要がある
            // とりあえずここでは常にfalseを返すが、実際はNodeEditorで管理
            return false;
        }

        public override object? Evaluate(Dictionary<System.Guid, object?> inputValues)
        {
            var cameraObj = GetInputValue<object>("Camera", inputValues);
            
            var objects = new List<object>();
            var lights = new List<LightData>();

            // すべての入力ソケットをスキャンして値を収集
            foreach (var socket in InputSockets)
            {
                if (socket.SocketType == SocketType.Object)
                {
                    if (inputValues.TryGetValue(socket.Id, out var obj) && obj != null)
                    {
                        objects.Add(obj);
                    }
                }
                else if (socket.SocketType == SocketType.Light)
                {
                    if (inputValues.TryGetValue(socket.Id, out var lightObj) && lightObj is LightData light)
                    {
                        lights.Add(light);
                    }
                }
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

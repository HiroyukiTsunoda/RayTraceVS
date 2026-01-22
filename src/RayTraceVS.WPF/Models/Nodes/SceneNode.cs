using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using RayTraceVS.WPF.Models.Data;

namespace RayTraceVS.WPF.Models.Nodes
{
    public partial class SceneNode : Node
    {
        private int objectSocketCount = 0;
        private int lightSocketCount = 0;

        private int _samplesPerPixel = 2;
        public int SamplesPerPixel
        {
            get => _samplesPerPixel;
            set
            {
                try { System.IO.File.AppendAllText(@"C:\git\RayTraceVS\debug.log", $"[SceneNode] SamplesPerPixel setter called with: {value}, current: {_samplesPerPixel}\n"); } catch { }
                if (SetProperty(ref _samplesPerPixel, value))
                {
                    MarkDirty();
                    try { System.IO.File.AppendAllText(@"C:\git\RayTraceVS\debug.log", $"[SceneNode] SamplesPerPixel changed to: {value}, MarkDirty called\n"); } catch { }
                }
            }
        }

        private int _maxBounces = 6;
        public int MaxBounces
        {
            get => _maxBounces;
            set
            {
                if (SetProperty(ref _maxBounces, value))
                {
                    MarkDirty();
                }
            }
        }

        private float _exposure = 1.0f;
        public float Exposure
        {
            get => _exposure;
            set
            {
                if (SetProperty(ref _exposure, value))
                {
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// 0 = Reinhard, 1 = ACES, 2 = None
        /// </summary>
        private int _toneMapOperator = 2;
        public int ToneMapOperator
        {
            get => _toneMapOperator;
            set
            {
                if (SetProperty(ref _toneMapOperator, value))
                {
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// NRD Reblur stabilizationStrength (1.0 = default)
        /// </summary>
        private float _denoiserStabilization = 1.0f;
        public float DenoiserStabilization
        {
            get => _denoiserStabilization;
            set
            {
                if (SetProperty(ref _denoiserStabilization, value))
                {
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// 影の濃さ (0.0 = 影なし, 1.0 = 通常, >1.0 = より濃い影)
        /// </summary>
        private float _shadowStrength = 1.0f;
        public float ShadowStrength
        {
            get => _shadowStrength;
            set
            {
                if (SetProperty(ref _shadowStrength, value))
                {
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// デノイザー有効/無効 (true = NRDデノイザー有効)
        /// </summary>
        private bool _enableDenoiser = true;
        public bool EnableDenoiser
        {
            get => _enableDenoiser;
            set
            {
                if (SetProperty(ref _enableDenoiser, value))
                {
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// ガンマ値 (1.0 = リニア, 2.2 = 標準sRGB)
        /// </summary>
        private float _gamma = 1.0f;
        public float Gamma
        {
            get => _gamma;
            set
            {
                if (SetProperty(ref _gamma, value))
                {
                    MarkDirty();
                }
            }
        }

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
        /// Lightソケットの前に挿入して、Object同士がまとまるようにする
        /// </summary>
        public void AddObjectSocket()
        {
            objectSocketCount++;
            var socket = new NodeSocket($"Object{objectSocketCount}", SocketType.Object, true) { ParentNode = this };
            
            // 最初のLightソケットのインデックスを見つける
            int lightIndex = -1;
            for (int i = 0; i < InputSockets.Count; i++)
            {
                if (InputSockets[i].SocketType == SocketType.Light)
                {
                    lightIndex = i;
                    break;
                }
            }
            
            // Lightソケットがあればその前に挿入、なければ末尾に追加
            if (lightIndex >= 0)
            {
                InputSockets.Insert(lightIndex, socket);
            }
            else
            {
                InputSockets.Add(socket);
            }
        }

        /// <summary>
        /// ライト入力ソケットを動的に追加（末尾に追加）
        /// </summary>
        public void AddLightSocket()
        {
            lightSocketCount++;
            var socket = new NodeSocket($"Light{lightSocketCount}", SocketType.Light, true) { ParentNode = this };
            InputSockets.Add(socket);
        }

        /// <summary>
        /// 指定した名前のソケットを追加（シーン読み込み時に使用）
        /// ソケットタイプに応じて適切な位置に挿入する
        /// </summary>
        public void AddNamedInputSocket(string socketName, SocketType socketType)
        {
            var socket = new NodeSocket(socketName, socketType, true) { ParentNode = this };
            
            if (socketType == SocketType.Object)
            {
                // Objectソケットは最初のLightソケットの前に挿入
                int lightIndex = -1;
                for (int i = 0; i < InputSockets.Count; i++)
                {
                    if (InputSockets[i].SocketType == SocketType.Light)
                    {
                        lightIndex = i;
                        break;
                    }
                }
                
                if (lightIndex >= 0)
                {
                    InputSockets.Insert(lightIndex, socket);
                }
                else
                {
                    InputSockets.Add(socket);
                }
            }
            else
            {
                // Light、その他は末尾に追加
                InputSockets.Add(socket);
            }
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

        public override object? Evaluate(Dictionary<Guid, object?> inputValues)
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
                Lights = lights,
                SamplesPerPixel = SamplesPerPixel,
                MaxBounces = MaxBounces,
                Exposure = Exposure,
                ToneMapOperator = ToneMapOperator,
                DenoiserStabilization = DenoiserStabilization,
                ShadowStrength = ShadowStrength,
                EnableDenoiser = EnableDenoiser,
                Gamma = Gamma
            };
        }
    }
}

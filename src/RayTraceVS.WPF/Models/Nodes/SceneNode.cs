using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using RayTraceVS.WPF.Models.Data;

namespace RayTraceVS.WPF.Models.Nodes
{
    public partial class SceneNode : Node
    {
        private int objectSocketCount = 0;
        private int lightSocketCount = 0;

#if DEBUG
        // Debug log path relative to the executable location
        private static readonly string DebugLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
#endif

        private int _samplesPerPixel = 2;
        public int SamplesPerPixel
        {
            get => _samplesPerPixel;
            set
            {
#if DEBUG
                try { File.AppendAllText(DebugLogPath, $"[SceneNode] SamplesPerPixel setter called with: {value}, current: {_samplesPerPixel}\n"); } catch { }
#endif
                if (SetProperty(ref _samplesPerPixel, value))
                {
                    MarkDirty();
#if DEBUG
                    try { File.AppendAllText(DebugLogPath, $"[SceneNode] SamplesPerPixel changed to: {value}, MarkDirty called\n"); } catch { }
#endif
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

        private int _traceRecursionDepth = 2;
        public int TraceRecursionDepth
        {
            get => _traceRecursionDepth;
            set
            {
                if (SetProperty(ref _traceRecursionDepth, value))
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
        /// 透過影の吸収スケール (1.0 = 通常, 2.0+ = より濃い色付き影)
        /// </summary>
        private float _shadowAbsorptionScale = 4.0f;
        public float ShadowAbsorptionScale
        {
            get => _shadowAbsorptionScale;
            set
            {
                if (SetProperty(ref _shadowAbsorptionScale, value))
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

        // ============================================
        // P1 Optimization Settings
        // ============================================
        
        /// <summary>
        /// ライト減衰の定数項 (通常 1.0)
        /// </summary>
        private float _lightAttenuationConstant = 1.0f;
        public float LightAttenuationConstant
        {
            get => _lightAttenuationConstant;
            set
            {
                if (SetProperty(ref _lightAttenuationConstant, value))
                {
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// ライト減衰の線形項 (距離に比例, 0.0 = 無効)
        /// </summary>
        private float _lightAttenuationLinear = 0.0f;
        public float LightAttenuationLinear
        {
            get => _lightAttenuationLinear;
            set
            {
                if (SetProperty(ref _lightAttenuationLinear, value))
                {
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// ライト減衰の2次項 (距離の2乗に比例, 物理的には 1.0, 視覚調整用 0.01)
        /// </summary>
        private float _lightAttenuationQuadratic = 0.01f;
        public float LightAttenuationQuadratic
        {
            get => _lightAttenuationQuadratic;
            set
            {
                if (SetProperty(ref _lightAttenuationQuadratic, value))
                {
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// シャドウ計算する最大ライト数 (パフォーマンス最適化, デフォルト 2)
        /// </summary>
        private int _maxShadowLights = 2;
        public int MaxShadowLights
        {
            get => _maxShadowLights;
            set
            {
                if (SetProperty(ref _maxShadowLights, value))
                {
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// NRDバイパスの距離閾値 (この距離より遠いとNRDをバイパス, デフォルト 8.0)
        /// </summary>
        private float _nrdBypassDistance = 8.0f;
        public float NRDBypassDistance
        {
            get => _nrdBypassDistance;
            set
            {
                if (SetProperty(ref _nrdBypassDistance, value))
                {
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// NRDバイパスのブレンド範囲 (スムーズな遷移用, デフォルト 2.0)
        /// </summary>
        private float _nrdBypassBlendRange = 2.0f;
        public float NRDBypassBlendRange
        {
            get => _nrdBypassBlendRange;
            set
            {
                if (SetProperty(ref _nrdBypassBlendRange, value))
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

            RenumberSceneSockets();
        }

        /// <summary>
        /// ライト入力ソケットを動的に追加（末尾に追加）
        /// </summary>
        public void AddLightSocket()
        {
            lightSocketCount++;
            var socket = new NodeSocket($"Light{lightSocketCount}", SocketType.Light, true) { ParentNode = this };
            InputSockets.Add(socket);
            RenumberSceneSockets();
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
        /// Object/Lightソケット名を1から連番で再割り当て
        /// </summary>
        public void RenumberSceneSockets()
        {
            int objectIndex = 1;
            int lightIndex = 1;

            foreach (var socket in InputSockets)
            {
                if (socket.SocketType == SocketType.Object)
                {
                    socket.Name = $"Object{objectIndex++}";
                }
                else if (socket.SocketType == SocketType.Light)
                {
                    socket.Name = $"Light{lightIndex++}";
                }
            }

            objectSocketCount = objectIndex - 1;
            lightSocketCount = lightIndex - 1;
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
                TraceRecursionDepth = TraceRecursionDepth,
                Exposure = Exposure,
                ToneMapOperator = ToneMapOperator,
                DenoiserStabilization = DenoiserStabilization,
                ShadowStrength = ShadowStrength,
                ShadowAbsorptionScale = ShadowAbsorptionScale,
                EnableDenoiser = EnableDenoiser,
                Gamma = Gamma,
                // P1 optimization settings
                LightAttenuationConstant = LightAttenuationConstant,
                LightAttenuationLinear = LightAttenuationLinear,
                LightAttenuationQuadratic = LightAttenuationQuadratic,
                MaxShadowLights = MaxShadowLights,
                NRDBypassDistance = NRDBypassDistance,
                NRDBypassBlendRange = NRDBypassBlendRange
            };
        }
    }
}

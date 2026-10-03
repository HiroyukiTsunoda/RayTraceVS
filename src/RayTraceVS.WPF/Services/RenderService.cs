using System;
using RayTraceVS.Interop;

namespace RayTraceVS.WPF.Services
{
    /// <summary>ネイティブエンジンを管理し、描画と解放の同時実行を防ぐ。</summary>
    public sealed class RenderService : IDisposable
    {
        private readonly object _engineLock = new();
        private EngineWrapper? engineWrapper;
        private bool disposed;

        public bool Initialize(IntPtr windowHandle, int width, int height)
        {
            lock (_engineLock)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (engineWrapper != null)
                    throw new InvalidOperationException("レンダリングエンジンは初期化済みです。");
                if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue / 4)
                    throw new ArgumentOutOfRangeException(nameof(width), "レンダリング解像度が不正です。");
                EngineWrapper? candidate = null;
                try
                {
                    candidate = new EngineWrapper(windowHandle, width, height);
                    if (!candidate.IsInitialized()) return false;
                    engineWrapper = candidate;
                    candidate = null;
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"RenderService.Initialize failed: {ex}");
                    return false;
                }
                finally
                {
                    candidate?.Dispose();
                }
            }
        }

        private EngineWrapper GetEngine()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return engineWrapper ?? throw new InvalidOperationException("レンダリングエンジンが初期化されていません。");
        }

        public void UpdateScene(SceneEvaluationResult scene)
        {
            ArgumentNullException.ThrowIfNull(scene);
            lock (_engineLock)
            {
                var engineWrapper = GetEngine();
                var settings = new RenderSettings
                {
                    SamplesPerPixel = scene.SamplesPerPixel,
                    MaxBounces = scene.MaxBounces,
                    TraceRecursionDepth = scene.TraceRecursionDepth,
                    Exposure = scene.Exposure,
                    ToneMapOperator = scene.ToneMapOperator,
                    DenoiserStabilization = scene.DenoiserStabilization,
                    ShadowStrength = scene.ShadowStrength,
                    ShadowAbsorptionScale = scene.ShadowAbsorptionScale,
                    EnableDenoiser = scene.EnableDenoiser,
                    Gamma = scene.Gamma,
                    PhotonDebugMode = scene.PhotonDebugMode,
                    PhotonDebugScale = scene.PhotonDebugScale,
                    LightAttenuationConstant = scene.LightAttenuationConstant,
                    LightAttenuationLinear = scene.LightAttenuationLinear,
                    LightAttenuationQuadratic = scene.LightAttenuationQuadratic,
                    MaxShadowLights = scene.MaxShadowLights,
                    NRDBypassDistance = scene.NRDBypassDistance,
                    NRDBypassBlendRange = scene.NRDBypassBlendRange
                };
                engineWrapper.UpdateScene(scene.Spheres, scene.Planes, scene.Boxes, scene.Camera, scene.Lights,
                    scene.MeshInstances, scene.MeshCaches, settings);
            }
        }

        public void Render()
        {
            lock (_engineLock) GetEngine().Render();
        }

        // ウィンドウを閉じる際のエンジン解放は、更新・描画・読み戻しが終わるまで待機する。
        public byte[]? RenderFrame(SceneEvaluationResult scene)
        {
            lock (_engineLock)
            {
                UpdateScene(scene);
                var engine = GetEngine();
                engine.Render();
                return engine.GetPixelData();
            }
        }

        public IntPtr GetRenderTargetTexture()
        {
            lock (_engineLock) return GetEngine().GetRenderTargetTexture();
        }

        public byte[]? GetPixelData()
        {
            lock (_engineLock) return GetEngine().GetPixelData();
        }

        public void Dispose()
        {
            lock (_engineLock)
            {
                if (disposed) return;
                disposed = true;
                var engine = engineWrapper;
                engineWrapper = null;
                engine?.Dispose();
            }
        }
    }
}

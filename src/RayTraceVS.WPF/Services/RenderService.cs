using System;
using RayTraceVS.Interop;

namespace RayTraceVS.WPF.Services
{
    public class RenderService : IDisposable
    {
        private EngineWrapper? engineWrapper;
        private bool isInitialized = false;
        private bool disposed = false;

        public bool Initialize(IntPtr windowHandle, int width, int height)
        {
            try
            {
                engineWrapper = new EngineWrapper(windowHandle, width, height);
                isInitialized = engineWrapper.IsInitialized();
                return isInitialized;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RenderService.Initialize failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// シーン評価結果をエンジンに反映する。
        /// 新しいレンダリングパラメータの追加時は SceneEvaluationResult にプロパティを足し、
        /// ここで EngineWrapper へ渡すだけでよい。
        /// </summary>
        public void UpdateScene(SceneEvaluationResult scene)
        {
            if (!isInitialized || engineWrapper == null)
                return;

            try
            {
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RenderService.UpdateScene failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
            }
        }

        public void Render()
        {
            if (!isInitialized || engineWrapper == null)
                return;

            try
            {
                engineWrapper.Render();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RenderService.Render failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
            }
        }

        public IntPtr GetRenderTargetTexture()
        {
            if (!isInitialized || engineWrapper == null)
                return IntPtr.Zero;

            return engineWrapper.GetRenderTargetTexture();
        }

        public byte[]? GetPixelData()
        {
            if (!isInitialized || engineWrapper == null)
                return null;

            return engineWrapper.GetPixelData();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
                return;

            if (disposing)
            {
                // マネージドリソースの解放
                if (engineWrapper != null)
                {
                    // EngineWrapper の ~EngineWrapper() (IDisposable.Dispose) を呼び出し
                    // これによりネイティブリソースが即座に解放される
                    engineWrapper.Dispose();
                    engineWrapper = null;
                }
            }

            isInitialized = false;
            disposed = true;
        }
    }
}

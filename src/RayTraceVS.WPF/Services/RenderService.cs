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
                engineWrapper.UpdateScene(scene.Spheres, scene.Planes, scene.Boxes, scene.Camera, scene.Lights,
                    scene.MeshInstances, scene.MeshCaches,
                    scene.SamplesPerPixel, scene.MaxBounces, scene.TraceRecursionDepth,
                    scene.Exposure, scene.ToneMapOperator,
                    scene.DenoiserStabilization, scene.ShadowStrength, scene.ShadowAbsorptionScale,
                    scene.EnableDenoiser, scene.Gamma,
                    scene.PhotonDebugMode, scene.PhotonDebugScale,
                    scene.LightAttenuationConstant, scene.LightAttenuationLinear, scene.LightAttenuationQuadratic,
                    scene.MaxShadowLights, scene.NRDBypassDistance, scene.NRDBypassBlendRange);
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

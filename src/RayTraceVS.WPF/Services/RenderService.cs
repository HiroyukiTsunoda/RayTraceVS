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

        public void UpdateScene(
            SphereData[] spheres,
            PlaneData[] planes,
            BoxData[] boxes,
            CameraData camera,
            LightData[] lights,
            MeshInstanceData[]? meshInstances = null,
            MeshCacheData[]? meshCaches = null,
            int samplesPerPixel = 1,
            int maxBounces = 6,
            int traceRecursionDepth = 2,
            float exposure = 1.0f,
            int toneMapOperator = 2,
            float denoiserStabilization = 1.0f,
            float shadowStrength = 1.0f,
            bool enableDenoiser = true,
            float gamma = 1.0f,
            int photonDebugMode = 0,
            float photonDebugScale = 1.0f)
        {
            if (!isInitialized || engineWrapper == null)
                return;

            try
            {
                engineWrapper.UpdateScene(spheres, planes, boxes, camera, lights, 
                    meshInstances ?? Array.Empty<MeshInstanceData>(), 
                    meshCaches ?? Array.Empty<MeshCacheData>(),
                    samplesPerPixel, maxBounces, traceRecursionDepth, exposure, toneMapOperator, denoiserStabilization, shadowStrength, enableDenoiser, gamma, photonDebugMode, photonDebugScale);
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

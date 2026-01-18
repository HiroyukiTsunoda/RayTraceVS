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
            catch (Exception)
            {
                return false;
            }
        }

        public void UpdateScene(
            SphereData[] spheres,
            PlaneData[] planes,
            BoxData[] boxes,
            CameraData camera,
            LightData[] lights,
            int samplesPerPixel = 1,
            int maxBounces = 4,
            float exposure = 1.0f,
            int toneMapOperator = 2,
            float denoiserStabilization = 1.0f)
        {
            if (!isInitialized || engineWrapper == null)
                return;

            try
            {
                engineWrapper.UpdateScene(spheres, planes, boxes, camera, lights, samplesPerPixel, maxBounces, exposure, toneMapOperator, denoiserStabilization);
            }
            catch
            {
                // Silently handle errors
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
            catch
            {
                // Silently handle errors
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

using System;
using RayTraceVS.Interop;

namespace RayTraceVS.WPF.Services
{
    public class RenderService : IDisposable
    {
        private EngineWrapper? engineWrapper;
        private bool isInitialized = false;

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
                System.Diagnostics.Debug.WriteLine($"Failed to initialize render service: {ex.Message}");
                return false;
            }
        }

        public void UpdateScene(
            SphereData[] spheres,
            PlaneData[] planes,
            CylinderData[] cylinders,
            CameraData camera,
            LightData[] lights)
        {
            if (!isInitialized || engineWrapper == null)
                return;

            try
            {
                engineWrapper.UpdateScene(spheres, planes, cylinders, camera, lights);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update scene: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"Failed to render: {ex.Message}");
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
            if (engineWrapper != null)
            {
                engineWrapper = null;
            }

            isInitialized = false;
        }
    }
}

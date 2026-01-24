#pragma once

#include "SceneData.h"

namespace RayTraceVS::DXEngine
{
    class DXContext;
    class DXRPipeline;
    class Scene;
    class RenderTarget;
}

namespace RayTraceVS::Interop
{
    public ref class EngineWrapper
    {
    public:
        EngineWrapper(System::IntPtr windowHandle, int width, int height);
        ~EngineWrapper();
        !EngineWrapper();

        // Update scene
        void UpdateScene(
            array<SphereData>^ spheres,
            array<PlaneData>^ planes,
            array<BoxData>^ boxes,
            CameraData camera,
            array<LightData>^ lights,
            array<MeshInstanceData>^ meshInstances,
            array<MeshCacheData^>^ meshCaches,
            int samplesPerPixel,
            int maxBounces,
            int traceRecursionDepth,
            float exposure,
            int toneMapOperator,
            float denoiserStabilization,
            float shadowStrength,
            bool enableDenoiser,
            float gamma,
            int photonDebugMode,
            float photonDebugScale);

        // Rendering
        void Render();

        // Get render target
        System::IntPtr GetRenderTargetTexture();
        
        // Get pixel data (RGBA format)
        array<System::Byte>^ GetPixelData();

        // Initialization state
        bool IsInitialized() { return isInitialized; }

    private:
        RayTraceVS::DXEngine::DXContext* nativeContext;
        RayTraceVS::DXEngine::DXRPipeline* nativePipeline;
        RayTraceVS::DXEngine::Scene* nativeScene;
        RayTraceVS::DXEngine::RenderTarget* nativeRenderTarget;
        
        bool isInitialized;
        int renderWidth;
        int renderHeight;
    };
}

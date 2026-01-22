// Include bridge headers
#include "EngineWrapper.h"
#include "Marshalling.h"
#include "NativeBridge.h"
#include <cstdio>
#include <cstdarg>

// Declare OutputDebugStringA without including windows.h (avoids C++/CLI conflicts)
extern "C" __declspec(dllimport) void __stdcall OutputDebugStringA(const char* lpOutputString);

// Error log only
static void LogError(const char* msg)
{
    OutputDebugStringA(msg);
}

// Debug log with printf-style formatting
static void LogDebug(const char* format, ...)
{
    char buffer[1024];
    va_list args;
    va_start(args, format);
    vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);
    OutputDebugStringA(buffer);
}

namespace RayTraceVS::Interop
{
    EngineWrapper::EngineWrapper(System::IntPtr windowHandle, int width, int height)
        : isInitialized(false)
        , renderWidth(width)
        , renderHeight(height)
        , nativeRenderTarget(nullptr)
    {
        try
        {
            // Create native context
            nativeContext = Bridge::CreateDXContext();
            
            void* hwnd = windowHandle.ToPointer();
            if (!Bridge::InitializeDXContext(nativeContext, hwnd, width, height))
            {
                throw gcnew System::Exception("Failed to initialize DirectX context");
            }

            // Create DXR pipeline
            nativePipeline = Bridge::CreateDXRPipeline(nativeContext);
            // Pipeline initialization is optional - continue even if it fails
            // (will fall back to error color rendering)
            Bridge::InitializeDXRPipeline(nativePipeline);

            // Create scene
            nativeScene = Bridge::CreateScene();
            
            // Create render target
            nativeRenderTarget = Bridge::CreateRenderTarget(nativeContext);
            if (!Bridge::InitializeRenderTarget(nativeRenderTarget, width, height))
            {
                throw gcnew System::Exception("Failed to initialize render target");
            }

            isInitialized = true;
        }
        catch (...)
        {
            throw gcnew System::Exception("Native initialization failed");
        }
    }

    EngineWrapper::~EngineWrapper()
    {
        this->!EngineWrapper();
    }

    EngineWrapper::!EngineWrapper()
    {
        if (nativeRenderTarget)
        {
            Bridge::DestroyRenderTarget(nativeRenderTarget);
            nativeRenderTarget = nullptr;
        }

        if (nativeScene)
        {
            Bridge::DestroyScene(nativeScene);
            nativeScene = nullptr;
        }

        if (nativePipeline)
        {
            Bridge::DestroyDXRPipeline(nativePipeline);
            nativePipeline = nullptr;
        }

        if (nativeContext)
        {
            Bridge::ShutdownDXContext(nativeContext);
            Bridge::DestroyDXContext(nativeContext);
            nativeContext = nullptr;
        }
    }

    void EngineWrapper::UpdateScene(
        array<SphereData>^ spheres,
        array<PlaneData>^ planes,
        array<BoxData>^ boxes,
        CameraData camera,
        array<LightData>^ lights,
        array<MeshInstanceData>^ meshInstances,
        array<MeshCacheData^>^ meshCaches,
        int samplesPerPixel,
        int maxBounces,
        float exposure,
        int toneMapOperator,
        float denoiserStabilization,
        float shadowStrength,
        bool enableDenoiser,
        float gamma)
    {
        if (!isInitialized || !nativeScene)
            return;

        // Clear scene
        Bridge::ClearScene(nativeScene);

        // Set camera
        auto nativeCamera = Marshalling::ToNativeCamera(camera);
        Bridge::SetCamera(nativeScene, nativeCamera);

        // Set render settings
        Bridge::SetRenderSettings(nativeScene, samplesPerPixel, maxBounces, exposure, toneMapOperator, denoiserStabilization, shadowStrength, enableDenoiser, gamma);

        // Add spheres
        if (spheres != nullptr)
        {
            for each (SphereData sphere in spheres)
            {
                auto nativeSphere = Marshalling::ToNativeSphere(sphere);
                Bridge::AddSphere(nativeScene, nativeSphere);
            }
        }

        // Add planes
        if (planes != nullptr)
        {
            for each (PlaneData plane in planes)
            {
                auto nativePlane = Marshalling::ToNativePlane(plane);
                Bridge::AddPlane(nativeScene, nativePlane);
            }
        }

        // Add boxes
        if (boxes != nullptr)
        {
            for each (BoxData box in boxes)
            {
                auto nativeBox = Marshalling::ToNativeBox(box);
                Bridge::AddBox(nativeScene, nativeBox);
            }
        }

        // Add lights
        if (lights != nullptr)
        {
            for each (LightData light in lights)
            {
                auto nativeLight = Marshalling::ToNativeLight(light);
                Bridge::AddLight(nativeScene, nativeLight);
            }
        }

        // Add mesh caches (shared geometry)
        if (meshCaches != nullptr)
        {
            for each (MeshCacheData^ cache in meshCaches)
            {
                if (cache == nullptr || cache->MeshName == nullptr)
                    continue;
                    
                Bridge::MeshCacheDataNative nativeCache;
                std::string meshNameStr = Marshalling::ToNativeString(cache->MeshName);
                nativeCache.name = meshNameStr.c_str();
                
                // Pin managed arrays to get native pointers
                pin_ptr<float> pinnedVertices = nullptr;
                pin_ptr<unsigned int> pinnedIndices = nullptr;
                
                if (cache->Vertices != nullptr && cache->Vertices->Length > 0)
                {
                    pinnedVertices = &cache->Vertices[0];
                    nativeCache.vertices = pinnedVertices;
                    nativeCache.vertexCount = cache->Vertices->Length / 8;  // 8 floats per vertex
                }
                else
                {
                    LogError("[EngineWrapper] ERROR: Mesh cache has no vertices\n");
                    nativeCache.vertices = nullptr;
                    nativeCache.vertexCount = 0;
                }
                
                if (cache->Indices != nullptr && cache->Indices->Length > 0)
                {
                    pinnedIndices = &cache->Indices[0];
                    nativeCache.indices = pinnedIndices;
                    nativeCache.indexCount = cache->Indices->Length;
                }
                else
                {
                    LogError("[EngineWrapper] ERROR: Mesh cache has no indices\n");
                    nativeCache.indices = nullptr;
                    nativeCache.indexCount = 0;
                }
                
                nativeCache.boundsMin = { cache->BoundsMin.X, cache->BoundsMin.Y, cache->BoundsMin.Z };
                nativeCache.boundsMax = { cache->BoundsMax.X, cache->BoundsMax.Y, cache->BoundsMax.Z };
                
                Bridge::AddMeshCache(nativeScene, nativeCache);
            }
        }

        // Add mesh instances
        LogDebug("[EngineWrapper::UpdateScene] Adding mesh instances...\n");
        if (meshInstances != nullptr)
        {
            LogDebug("[EngineWrapper::UpdateScene] meshInstances count: %d\n", meshInstances->Length);
            for (int i = 0; i < meshInstances->Length; i++)
            {
                MeshInstanceData instance = meshInstances[i];
                LogDebug("[EngineWrapper::UpdateScene] Processing instance %d\n", i);
                if (instance.MeshName == nullptr)
                {
                    LogDebug("[EngineWrapper::UpdateScene] MeshName is null, skipping\n");
                    continue;
                }
                
                LogDebug("[EngineWrapper::UpdateScene] MeshName is valid, converting...\n");
                Bridge::MeshInstanceDataNative nativeInstance;
                std::string meshNameStr = Marshalling::ToNativeString(instance.MeshName);
                LogDebug("[EngineWrapper::UpdateScene] meshNameStr: %s\n", meshNameStr.c_str());
                nativeInstance.meshName = meshNameStr.c_str();
                
                nativeInstance.position = { instance.Position.X, instance.Position.Y, instance.Position.Z };
                nativeInstance.rotation = { instance.Rotation.X, instance.Rotation.Y, instance.Rotation.Z };
                nativeInstance.scale = { instance.Scale.X, instance.Scale.Y, instance.Scale.Z };
                nativeInstance.material.color = { instance.Color.X, instance.Color.Y, instance.Color.Z, instance.Color.W };
                nativeInstance.material.metallic = instance.Metallic;
                nativeInstance.material.roughness = instance.Roughness;
                nativeInstance.material.transmission = instance.Transmission;
                nativeInstance.material.ior = instance.IOR;
                nativeInstance.material.specular = instance.Specular;
                nativeInstance.material.emission = { instance.Emission.X, instance.Emission.Y, instance.Emission.Z };
                
                LogDebug("[EngineWrapper::UpdateScene] Calling Bridge::AddMeshInstance...\n");
                Bridge::AddMeshInstance(nativeScene, nativeInstance);
                LogDebug("[EngineWrapper::UpdateScene] Bridge::AddMeshInstance completed\n");
            }
        }
        LogDebug("[EngineWrapper::UpdateScene] All mesh instances added\n");
    }

    void EngineWrapper::Render()
    {
        LogDebug("[EngineWrapper::Render] Starting...\n");
        if (!isInitialized || !nativePipeline || !nativeRenderTarget || !nativeContext)
        {
            LogError("[EngineWrapper::Render] ERROR: Not initialized or null pointers\n");
            return;
        }

        try
        {
            // Wait for previous GPU work to complete before resetting command allocator
            LogDebug("[EngineWrapper::Render] WaitForGPU (before reset)...\n");
            Bridge::WaitForGPU(nativeContext);
            
            // Reset command list
            LogDebug("[EngineWrapper::Render] ResetCommandList...\n");
            Bridge::ResetCommandList(nativeContext);
            
            // Render
            LogDebug("[EngineWrapper::Render] RenderTestPattern...\n");
            Bridge::RenderTestPattern(nativePipeline, nativeRenderTarget, nativeScene);
            LogDebug("[EngineWrapper::Render] RenderTestPattern completed\n");
            
            // Execute command list
            LogDebug("[EngineWrapper::Render] ExecuteCommandList...\n");
            Bridge::ExecuteCommandList(nativeContext);
            
            // Wait for GPU completion
            LogDebug("[EngineWrapper::Render] WaitForGPU...\n");
            Bridge::WaitForGPU(nativeContext);
            
            // Copy to readback buffer
            LogDebug("[EngineWrapper::Render] CopyRenderTargetToReadback...\n");
            Bridge::ResetCommandList(nativeContext);
            Bridge::CopyRenderTargetToReadback(nativeRenderTarget, nativeContext);
            Bridge::ExecuteCommandList(nativeContext);
            Bridge::WaitForGPU(nativeContext);
            LogDebug("[EngineWrapper::Render] Completed\n");
        }
        catch (System::Exception^)
        {
            LogError("[EngineWrapper::Render] ERROR: Managed exception\n");
            throw;
        }
        catch (...)
        {
            LogError("[EngineWrapper::Render] ERROR: Native exception\n");
            throw gcnew System::Exception("Native rendering error");
        }
    }

    System::IntPtr EngineWrapper::GetRenderTargetTexture()
    {
        if (!isInitialized)
            return System::IntPtr::Zero;

        // Return render target pointer
        // TODO: Implementation
        return System::IntPtr::Zero;
    }
    
    array<System::Byte>^ EngineWrapper::GetPixelData()
    {
        if (!isInitialized)
            return nullptr;
            
        if (!nativeRenderTarget)
            return nullptr;
            
        // Calculate pixel data size
        int dataSize = renderWidth * renderHeight * 4; // RGBA
        
        // Create managed array
        array<System::Byte>^ pixelData = gcnew array<System::Byte>(dataSize);
        
        // Pin and get native pointer
        pin_ptr<System::Byte> pinnedData = &pixelData[0];
        
        // Read pixel data in native code
        bool result = false;
        try
        {
            result = Bridge::ReadRenderTargetPixels(nativeRenderTarget, pinnedData, dataSize);
        }
        catch (System::Exception^)
        {
            return nullptr;
        }
        catch (...)
        {
            return nullptr;
        }
        
        if (!result)
            return nullptr;
        
        return pixelData;
    }
}

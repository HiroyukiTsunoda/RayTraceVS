// Include bridge headers
#include "EngineWrapper.h"
#include "Marshalling.h"
#include "NativeBridge.h"
#include <cstdio>
#include <cstdarg>
#include <cmath>

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

static bool IsFiniteFloat(float value)
{
    return std::isfinite(value);
}

static float ClampFinite(float value, float minVal, float maxVal, float fallback, const char* label, const char* objectType, int index)
{
    if (!std::isfinite(value))
    {
        LogDebug("[EngineWrapper::UpdateScene] %s[%d] %s invalid (NaN/Inf): %.6f\n", objectType, index, label, value);
        return fallback;
    }
    if (value < minVal)
    {
        LogDebug("[EngineWrapper::UpdateScene] %s[%d] %s below min: %.6f\n", objectType, index, label, value);
        return minVal;
    }
    if (value > maxVal)
    {
        LogDebug("[EngineWrapper::UpdateScene] %s[%d] %s above max: %.6f\n", objectType, index, label, value);
        return maxVal;
    }
    return value;
}

static float SanitizeFinite(float value, float fallback, const char* label, const char* objectType, int index)
{
    if (!std::isfinite(value))
    {
        LogDebug("[EngineWrapper::UpdateScene] %s[%d] %s invalid (NaN/Inf): %.6f\n", objectType, index, label, value);
        return fallback;
    }
    return value;
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
        int traceRecursionDepth,
        float exposure,
        int toneMapOperator,
        float denoiserStabilization,
        float shadowStrength,
        bool enableDenoiser,
        float gamma,
        int photonDebugMode,
        float photonDebugScale)
    {
        if (!isInitialized || !nativeScene)
            return;

        // Clear scene
        Bridge::ClearScene(nativeScene);

        // Set camera
        auto nativeCamera = Marshalling::ToNativeCamera(camera);
        Bridge::SetCamera(nativeScene, nativeCamera);

        // Set render settings
        Bridge::SetRenderSettings(nativeScene, samplesPerPixel, maxBounces, traceRecursionDepth, exposure, toneMapOperator, denoiserStabilization, shadowStrength, enableDenoiser, gamma, photonDebugMode, photonDebugScale);

        // Add spheres
        if (spheres != nullptr)
        {
            LogDebug("[EngineWrapper::UpdateScene] spheres count: %d\n", spheres->Length);
            int sphereIndex = 0;
            for each (SphereData sphere in spheres)
            {
                SphereData safeSphere = sphere;
                safeSphere.Position.X = ClampFinite(safeSphere.Position.X, -10000.0f, 10000.0f, 0.0f, "Position.X", "Sphere", sphereIndex);
                safeSphere.Position.Y = ClampFinite(safeSphere.Position.Y, -10000.0f, 10000.0f, 0.0f, "Position.Y", "Sphere", sphereIndex);
                safeSphere.Position.Z = ClampFinite(safeSphere.Position.Z, -10000.0f, 10000.0f, 0.0f, "Position.Z", "Sphere", sphereIndex);
                safeSphere.Color.X = ClampFinite(safeSphere.Color.X, 0.0f, 1.0f, 0.8f, "BaseColor.X", "Sphere", sphereIndex);
                safeSphere.Color.Y = ClampFinite(safeSphere.Color.Y, 0.0f, 1.0f, 0.8f, "BaseColor.Y", "Sphere", sphereIndex);
                safeSphere.Color.Z = ClampFinite(safeSphere.Color.Z, 0.0f, 1.0f, 0.8f, "BaseColor.Z", "Sphere", sphereIndex);
                safeSphere.Color.W = ClampFinite(safeSphere.Color.W, 0.0f, 1.0f, 1.0f, "BaseColor.W", "Sphere", sphereIndex);
                
                safeSphere.Metallic = ClampFinite(safeSphere.Metallic, 0.0f, 1.0f, 0.0f, "Metallic", "Sphere", sphereIndex);
                safeSphere.Roughness = ClampFinite(safeSphere.Roughness, 0.0f, 1.0f, 0.5f, "Roughness", "Sphere", sphereIndex);
                safeSphere.Transmission = ClampFinite(safeSphere.Transmission, 0.0f, 1.0f, 0.0f, "Transmission", "Sphere", sphereIndex);
                safeSphere.IOR = ClampFinite(safeSphere.IOR, 1.0f, 4.0f, 1.5f, "IOR", "Sphere", sphereIndex);
                safeSphere.Specular = ClampFinite(safeSphere.Specular, 0.0f, 1.0f, 0.5f, "Specular", "Sphere", sphereIndex);
                safeSphere.Absorption.X = ClampFinite(safeSphere.Absorption.X, 0.0f, 100.0f, 0.0f, "Absorption.X", "Sphere", sphereIndex);
                safeSphere.Absorption.Y = ClampFinite(safeSphere.Absorption.Y, 0.0f, 100.0f, 0.0f, "Absorption.Y", "Sphere", sphereIndex);
                safeSphere.Absorption.Z = ClampFinite(safeSphere.Absorption.Z, 0.0f, 100.0f, 0.0f, "Absorption.Z", "Sphere", sphereIndex);
                
                safeSphere.Emission.X = SanitizeFinite(safeSphere.Emission.X, 0.0f, "Emission.X", "Sphere", sphereIndex);
                safeSphere.Emission.Y = SanitizeFinite(safeSphere.Emission.Y, 0.0f, "Emission.Y", "Sphere", sphereIndex);
                safeSphere.Emission.Z = SanitizeFinite(safeSphere.Emission.Z, 0.0f, "Emission.Z", "Sphere", sphereIndex);
                
                if (!IsFiniteFloat(safeSphere.Radius) || safeSphere.Radius <= 0.0f)
                {
                    LogDebug("[EngineWrapper::UpdateScene] Sphere[%d] Radius invalid: %.6f\n", sphereIndex, safeSphere.Radius);
                    safeSphere.Radius = 0.01f;
                }
                
                if (safeSphere.Transmission >= 0.6f)
                {
                    LogDebug("[EngineWrapper::UpdateScene] Sphere Transmission high: %.6f\n", safeSphere.Transmission);
                }
                
                LogDebug(
                    "[EngineWrapper::UpdateScene] Sphere[%d] Pos(%.3f, %.3f, %.3f) R=%.3f "
                    "Base(%.3f, %.3f, %.3f, %.3f) M=%.3f Rgh=%.3f T=%.3f IOR=%.3f Sp=%.3f Em(%.3f, %.3f, %.3f)\n",
                    sphereIndex,
                    safeSphere.Position.X, safeSphere.Position.Y, safeSphere.Position.Z,
                    safeSphere.Radius,
                    safeSphere.Color.X, safeSphere.Color.Y, safeSphere.Color.Z, safeSphere.Color.W,
                    safeSphere.Metallic, safeSphere.Roughness, safeSphere.Transmission, safeSphere.IOR, safeSphere.Specular,
                    safeSphere.Emission.X, safeSphere.Emission.Y, safeSphere.Emission.Z);
                auto nativeSphere = Marshalling::ToNativeSphere(safeSphere);
                Bridge::AddSphere(nativeScene, nativeSphere);
                sphereIndex++;
            }
        }
        else
        {
            LogDebug("[EngineWrapper::UpdateScene] spheres is null\n");
        }

        // Add planes
        if (planes != nullptr)
        {
            LogDebug("[EngineWrapper::UpdateScene] planes count: %d\n", planes->Length);
            int planeIndex = 0;
            for each (PlaneData plane in planes)
            {
                PlaneData safePlane = plane;
                safePlane.Color.X = ClampFinite(safePlane.Color.X, 0.0f, 1.0f, 0.8f, "BaseColor.X", "Plane", planeIndex);
                safePlane.Color.Y = ClampFinite(safePlane.Color.Y, 0.0f, 1.0f, 0.8f, "BaseColor.Y", "Plane", planeIndex);
                safePlane.Color.Z = ClampFinite(safePlane.Color.Z, 0.0f, 1.0f, 0.8f, "BaseColor.Z", "Plane", planeIndex);
                safePlane.Color.W = ClampFinite(safePlane.Color.W, 0.0f, 1.0f, 1.0f, "BaseColor.W", "Plane", planeIndex);
                
                safePlane.Metallic = ClampFinite(safePlane.Metallic, 0.0f, 1.0f, 0.0f, "Metallic", "Plane", planeIndex);
                safePlane.Roughness = ClampFinite(safePlane.Roughness, 0.0f, 1.0f, 0.5f, "Roughness", "Plane", planeIndex);
                safePlane.Transmission = ClampFinite(safePlane.Transmission, 0.0f, 1.0f, 0.0f, "Transmission", "Plane", planeIndex);
                safePlane.IOR = ClampFinite(safePlane.IOR, 1.0f, 4.0f, 1.5f, "IOR", "Plane", planeIndex);
                safePlane.Specular = ClampFinite(safePlane.Specular, 0.0f, 1.0f, 0.5f, "Specular", "Plane", planeIndex);
                safePlane.Absorption.X = ClampFinite(safePlane.Absorption.X, 0.0f, 100.0f, 0.0f, "Absorption.X", "Plane", planeIndex);
                safePlane.Absorption.Y = ClampFinite(safePlane.Absorption.Y, 0.0f, 100.0f, 0.0f, "Absorption.Y", "Plane", planeIndex);
                safePlane.Absorption.Z = ClampFinite(safePlane.Absorption.Z, 0.0f, 100.0f, 0.0f, "Absorption.Z", "Plane", planeIndex);
                
                safePlane.Emission.X = SanitizeFinite(safePlane.Emission.X, 0.0f, "Emission.X", "Plane", planeIndex);
                safePlane.Emission.Y = SanitizeFinite(safePlane.Emission.Y, 0.0f, "Emission.Y", "Plane", planeIndex);
                safePlane.Emission.Z = SanitizeFinite(safePlane.Emission.Z, 0.0f, "Emission.Z", "Plane", planeIndex);
                
                if (safePlane.Transmission >= 0.6f)
                {
                    LogDebug("[EngineWrapper::UpdateScene] Plane Transmission high: %.6f\n", safePlane.Transmission);
                }
                auto nativePlane = Marshalling::ToNativePlane(safePlane);
                Bridge::AddPlane(nativeScene, nativePlane);
                planeIndex++;
            }
        }
        else
        {
            LogDebug("[EngineWrapper::UpdateScene] planes is null\n");
        }

        // Add boxes
        if (boxes != nullptr)
        {
            LogDebug("[EngineWrapper::UpdateScene] boxes count: %d\n", boxes->Length);
            int boxIndex = 0;
            for each (BoxData box in boxes)
            {
                BoxData safeBox = box;
                safeBox.Color.X = ClampFinite(safeBox.Color.X, 0.0f, 1.0f, 0.8f, "BaseColor.X", "Box", boxIndex);
                safeBox.Color.Y = ClampFinite(safeBox.Color.Y, 0.0f, 1.0f, 0.8f, "BaseColor.Y", "Box", boxIndex);
                safeBox.Color.Z = ClampFinite(safeBox.Color.Z, 0.0f, 1.0f, 0.8f, "BaseColor.Z", "Box", boxIndex);
                safeBox.Color.W = ClampFinite(safeBox.Color.W, 0.0f, 1.0f, 1.0f, "BaseColor.W", "Box", boxIndex);
                
                safeBox.Metallic = ClampFinite(safeBox.Metallic, 0.0f, 1.0f, 0.0f, "Metallic", "Box", boxIndex);
                safeBox.Roughness = ClampFinite(safeBox.Roughness, 0.0f, 1.0f, 0.5f, "Roughness", "Box", boxIndex);
                safeBox.Transmission = ClampFinite(safeBox.Transmission, 0.0f, 1.0f, 0.0f, "Transmission", "Box", boxIndex);
                safeBox.IOR = ClampFinite(safeBox.IOR, 1.0f, 4.0f, 1.5f, "IOR", "Box", boxIndex);
                safeBox.Specular = ClampFinite(safeBox.Specular, 0.0f, 1.0f, 0.5f, "Specular", "Box", boxIndex);
                safeBox.Absorption.X = ClampFinite(safeBox.Absorption.X, 0.0f, 100.0f, 0.0f, "Absorption.X", "Box", boxIndex);
                safeBox.Absorption.Y = ClampFinite(safeBox.Absorption.Y, 0.0f, 100.0f, 0.0f, "Absorption.Y", "Box", boxIndex);
                safeBox.Absorption.Z = ClampFinite(safeBox.Absorption.Z, 0.0f, 100.0f, 0.0f, "Absorption.Z", "Box", boxIndex);
                
                safeBox.Emission.X = SanitizeFinite(safeBox.Emission.X, 0.0f, "Emission.X", "Box", boxIndex);
                safeBox.Emission.Y = SanitizeFinite(safeBox.Emission.Y, 0.0f, "Emission.Y", "Box", boxIndex);
                safeBox.Emission.Z = SanitizeFinite(safeBox.Emission.Z, 0.0f, "Emission.Z", "Box", boxIndex);
                
                if (safeBox.Transmission >= 0.6f)
                {
                    LogDebug("[EngineWrapper::UpdateScene] Box Transmission high: %.6f\n", safeBox.Transmission);
                }
                auto nativeBox = Marshalling::ToNativeBox(safeBox);
                Bridge::AddBox(nativeScene, nativeBox);
                boxIndex++;
            }
        }
        else
        {
            LogDebug("[EngineWrapper::UpdateScene] boxes is null\n");
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
                nativeInstance.material.color = {
                    ClampFinite(instance.Color.X, 0.0f, 1.0f, 0.8f, "BaseColor.X", "MeshInstance", i),
                    ClampFinite(instance.Color.Y, 0.0f, 1.0f, 0.8f, "BaseColor.Y", "MeshInstance", i),
                    ClampFinite(instance.Color.Z, 0.0f, 1.0f, 0.8f, "BaseColor.Z", "MeshInstance", i),
                    ClampFinite(instance.Color.W, 0.0f, 1.0f, 1.0f, "BaseColor.W", "MeshInstance", i)
                };
                nativeInstance.material.metallic = ClampFinite(instance.Metallic, 0.0f, 1.0f, 0.0f, "Metallic", "MeshInstance", i);
                nativeInstance.material.roughness = ClampFinite(instance.Roughness, 0.0f, 1.0f, 0.5f, "Roughness", "MeshInstance", i);
                nativeInstance.material.transmission = ClampFinite(instance.Transmission, 0.0f, 1.0f, 0.0f, "Transmission", "MeshInstance", i);
                nativeInstance.material.ior = ClampFinite(instance.IOR, 1.0f, 4.0f, 1.5f, "IOR", "MeshInstance", i);
                nativeInstance.material.specular = ClampFinite(instance.Specular, 0.0f, 1.0f, 0.5f, "Specular", "MeshInstance", i);
                nativeInstance.material.emission = {
                    SanitizeFinite(instance.Emission.X, 0.0f, "Emission.X", "MeshInstance", i),
                    SanitizeFinite(instance.Emission.Y, 0.0f, "Emission.Y", "MeshInstance", i),
                    SanitizeFinite(instance.Emission.Z, 0.0f, "Emission.Z", "MeshInstance", i)
                };
                nativeInstance.material.absorption = {
                    ClampFinite(instance.Absorption.X, 0.0f, 100.0f, 0.0f, "Absorption.X", "MeshInstance", i),
                    ClampFinite(instance.Absorption.Y, 0.0f, 100.0f, 0.0f, "Absorption.Y", "MeshInstance", i),
                    ClampFinite(instance.Absorption.Z, 0.0f, 100.0f, 0.0f, "Absorption.Z", "MeshInstance", i)
                };
                
                if (nativeInstance.material.transmission >= 0.6f)
                {
                    LogDebug("[EngineWrapper::UpdateScene] MeshInstance Transmission high: %.6f\n", nativeInstance.material.transmission);
                }
                
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

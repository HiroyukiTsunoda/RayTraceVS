// Include bridge headers
#include "EngineWrapper.h"
#include "Marshalling.h"
#include "NativeBridge.h"
#include <cstdio>
#include <cstdarg>
#include <cmath>

// Declare OutputDebugStringA without including windows.h (avoids C++/CLI conflicts)
extern "C" __declspec(dllimport) void __stdcall OutputDebugStringA(const char* lpOutputString);

// Debug logging toggle. UpdateScene/Render call LogDebug 150+ times per frame;
// the vsnprintf + OutputDebugStringA cost is wasted when no one is reading the
// output, so it is disabled by default. Errors (LogError) are always emitted.
static bool g_InteropLogEnabled = false;

// Error log only
static void LogError(const char* msg)
{
    OutputDebugStringA(msg);
}

// Debug log with printf-style formatting
static void LogDebug(const char* format, ...)
{
    if (!g_InteropLogEnabled)
        return;
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

// Clamp/sanitize all PBR material fields in-place.
// Shared by the Sphere/Plane/Box/MeshInstance loops (was copy-pasted 4 times).
static void SanitizeMaterial(RayTraceVS::Interop::Bridge::MaterialNative& mat, const char* objectType, int index)
{
    mat.color.r = ClampFinite(mat.color.r, 0.0f, 1.0f, 0.8f, "BaseColor.X", objectType, index);
    mat.color.g = ClampFinite(mat.color.g, 0.0f, 1.0f, 0.8f, "BaseColor.Y", objectType, index);
    mat.color.b = ClampFinite(mat.color.b, 0.0f, 1.0f, 0.8f, "BaseColor.Z", objectType, index);
    mat.color.a = ClampFinite(mat.color.a, 0.0f, 1.0f, 1.0f, "BaseColor.W", objectType, index);

    mat.metallic = ClampFinite(mat.metallic, 0.0f, 1.0f, 0.0f, "Metallic", objectType, index);
    mat.roughness = ClampFinite(mat.roughness, 0.0f, 1.0f, 0.5f, "Roughness", objectType, index);
    mat.transmission = ClampFinite(mat.transmission, 0.0f, 1.0f, 0.0f, "Transmission", objectType, index);
    mat.ior = ClampFinite(mat.ior, 1.0f, 4.0f, 1.5f, "IOR", objectType, index);
    mat.specular = ClampFinite(mat.specular, 0.0f, 1.0f, 0.5f, "Specular", objectType, index);
    mat.absorption.x = ClampFinite(mat.absorption.x, 0.0f, 100.0f, 0.0f, "Absorption.X", objectType, index);
    mat.absorption.y = ClampFinite(mat.absorption.y, 0.0f, 100.0f, 0.0f, "Absorption.Y", objectType, index);
    mat.absorption.z = ClampFinite(mat.absorption.z, 0.0f, 100.0f, 0.0f, "Absorption.Z", objectType, index);

    mat.emission.x = SanitizeFinite(mat.emission.x, 0.0f, "Emission.X", objectType, index);
    mat.emission.y = SanitizeFinite(mat.emission.y, 0.0f, "Emission.Y", objectType, index);
    mat.emission.z = SanitizeFinite(mat.emission.z, 0.0f, "Emission.Z", objectType, index);

    if (mat.transmission >= 0.6f)
    {
        LogDebug("[EngineWrapper::UpdateScene] %s Transmission high: %.6f\n", objectType, mat.transmission);
    }
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
        RenderSettings settings)
    {
        if (!isInitialized || !nativeScene)
            return;

        // Clear scene
        Bridge::ClearScene(nativeScene);

        // Set camera
        auto nativeCamera = Marshalling::ToNativeCamera(camera);
        Bridge::SetCamera(nativeScene, nativeCamera);

        // Set render settings (including P1 optimization parameters)
        Bridge::SetRenderSettings(nativeScene, Marshalling::ToNativeRenderSettings(settings));

        // Add spheres
        if (spheres != nullptr)
        {
            LogDebug("[EngineWrapper::UpdateScene] spheres count: %d\n", spheres->Length);
            int sphereIndex = 0;
            for each (SphereData sphere in spheres)
            {
                auto nativeSphere = Marshalling::ToNativeSphere(sphere);
                nativeSphere.center.x = ClampFinite(nativeSphere.center.x, -10000.0f, 10000.0f, 0.0f, "Position.X", "Sphere", sphereIndex);
                nativeSphere.center.y = ClampFinite(nativeSphere.center.y, -10000.0f, 10000.0f, 0.0f, "Position.Y", "Sphere", sphereIndex);
                nativeSphere.center.z = ClampFinite(nativeSphere.center.z, -10000.0f, 10000.0f, 0.0f, "Position.Z", "Sphere", sphereIndex);
                SanitizeMaterial(nativeSphere.material, "Sphere", sphereIndex);

                if (!IsFiniteFloat(nativeSphere.radius) || nativeSphere.radius <= 0.0f)
                {
                    LogDebug("[EngineWrapper::UpdateScene] Sphere[%d] Radius invalid: %.6f\n", sphereIndex, nativeSphere.radius);
                    nativeSphere.radius = 0.01f;
                }

                LogDebug(
                    "[EngineWrapper::UpdateScene] Sphere[%d] Pos(%.3f, %.3f, %.3f) R=%.3f "
                    "Base(%.3f, %.3f, %.3f, %.3f) M=%.3f Rgh=%.3f T=%.3f IOR=%.3f Sp=%.3f Em(%.3f, %.3f, %.3f)\n",
                    sphereIndex,
                    nativeSphere.center.x, nativeSphere.center.y, nativeSphere.center.z,
                    nativeSphere.radius,
                    nativeSphere.material.color.r, nativeSphere.material.color.g, nativeSphere.material.color.b, nativeSphere.material.color.a,
                    nativeSphere.material.metallic, nativeSphere.material.roughness, nativeSphere.material.transmission, nativeSphere.material.ior, nativeSphere.material.specular,
                    nativeSphere.material.emission.x, nativeSphere.material.emission.y, nativeSphere.material.emission.z);
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
                auto nativePlane = Marshalling::ToNativePlane(plane);
                SanitizeMaterial(nativePlane.material, "Plane", planeIndex);
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
                auto nativeBox = Marshalling::ToNativeBox(box);
                SanitizeMaterial(nativeBox.material, "Box", boxIndex);
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
                nativeInstance.material.color = { instance.Color.X, instance.Color.Y, instance.Color.Z, instance.Color.W };
                nativeInstance.material.metallic = instance.Metallic;
                nativeInstance.material.roughness = instance.Roughness;
                nativeInstance.material.transmission = instance.Transmission;
                nativeInstance.material.ior = instance.IOR;
                nativeInstance.material.specular = instance.Specular;
                nativeInstance.material.emission = { instance.Emission.X, instance.Emission.Y, instance.Emission.Z };
                nativeInstance.material.absorption = { instance.Absorption.X, instance.Absorption.Y, instance.Absorption.Z };
                SanitizeMaterial(nativeInstance.material, "MeshInstance", i);

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

            // Render (records commands only; nothing is executed yet)
            LogDebug("[EngineWrapper::Render] RenderTestPattern...\n");
            Bridge::RenderTestPattern(nativePipeline, nativeRenderTarget, nativeScene);
            LogDebug("[EngineWrapper::Render] RenderTestPattern completed\n");

            // Record the readback copy into the same command list. CopyToReadback
            // performs its own UAV -> COPY_SOURCE -> UAV transitions, and barriers
            // are ordered within a command list, so a separate submit (and the
            // extra WaitForGPU round-trip it required) is unnecessary.
            LogDebug("[EngineWrapper::Render] CopyRenderTargetToReadback...\n");
            Bridge::CopyRenderTargetToReadback(nativeRenderTarget, nativeContext);

            // Execute render + readback together, then wait once
            LogDebug("[EngineWrapper::Render] ExecuteCommandList...\n");
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

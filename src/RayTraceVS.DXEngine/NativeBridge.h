#pragma once

// Fully native bridge interface
// No managed types

#include <cstdint>

#ifdef DXENGINE_EXPORTS
#define DXENGINE_API __declspec(dllexport)
#else
#define DXENGINE_API __declspec(dllimport)
#endif

namespace RayTraceVS::DXEngine
{
    class DXContext;
    class DXRPipeline;
    class Scene;
    class Camera;
    class Light;
    class Sphere;
    class Plane;
    class Box;
    class RenderTarget;
}

namespace RayTraceVS::Interop::Bridge
{
    struct Vector3Native
    {
        float x, y, z;
    };

    struct ColorNative
    {
        float r, g, b, a;
    };

    struct MaterialNative
    {
        ColorNative color;
        float metallic;     // 0.0 = dielectric, 1.0 = metal
        float roughness;    // 0.0 = smooth, 1.0 = rough
        float transmission; // 0.0 = opaque, 1.0 = transparent (glass)
        float ior;          // Index of Refraction (1.5 for glass)
        float specular;     // Specular intensity (0.0 = none, 1.0 = full)
        Vector3Native emission; // Emissive color (self-illumination)
        Vector3Native absorption; // Beer-Lambert sigmaA
    };

    struct CameraDataNative
    {
        Vector3Native position;
        Vector3Native lookAt;
        Vector3Native up;
        float fov;
        float aspectRatio;
        float apertureSize;   // DoF: 0.0 = disabled, larger = stronger bokeh
        float focusDistance;  // DoF: distance to the focal plane
    };

    struct LightDataNative
    {
        Vector3Native position;
        ColorNative color;
        float intensity;
        int type; // 0: Ambient, 1: Point, 2: Directional
        float radius;       // Area light radius (0 = point light)
        float softShadowSamples; // Number of shadow samples (1-16)
    };

    struct SphereDataNative
    {
        Vector3Native center;
        float radius;
        MaterialNative material;
    };

    struct PlaneDataNative
    {
        Vector3Native position;
        Vector3Native normal;
        MaterialNative material;
    };

    struct BoxDataNative
    {
        Vector3Native center;
        Vector3Native size;  // half-extents
        // Local axes (rotation matrix columns) for OBB
        Vector3Native axisX;
        Vector3Native axisY;
        Vector3Native axisZ;
        MaterialNative material;
    };

    // Mesh cache data (shared geometry)
    struct MeshCacheDataNative
    {
        const char* name;           // Mesh name (key)
        const float* vertices;      // 8 floats per vertex (pos3 + pad + normal3 + pad)
        const uint32_t* indices;
        uint32_t vertexCount;
        uint32_t indexCount;
        Vector3Native boundsMin;
        Vector3Native boundsMax;
    };

    // Mesh instance data (per-instance transform + material)
    struct MeshInstanceDataNative
    {
        const char* meshName;       // Reference to MeshCacheDataNative by name
        Vector3Native position;
        Vector3Native rotation;     // Euler angles (degrees)
        Vector3Native scale;
        MaterialNative material;
    };

    // Bridge functions (fully native)
    DXENGINE_API RayTraceVS::DXEngine::DXContext* CreateDXContext();
    DXENGINE_API bool InitializeDXContext(RayTraceVS::DXEngine::DXContext* context, void* hwnd, int width, int height);
    DXENGINE_API void ShutdownDXContext(RayTraceVS::DXEngine::DXContext* context);
    DXENGINE_API void DestroyDXContext(RayTraceVS::DXEngine::DXContext* context);
    DXENGINE_API void ResetCommandList(RayTraceVS::DXEngine::DXContext* context);

    DXENGINE_API RayTraceVS::DXEngine::DXRPipeline* CreateDXRPipeline(RayTraceVS::DXEngine::DXContext* context);
    DXENGINE_API bool InitializeDXRPipeline(RayTraceVS::DXEngine::DXRPipeline* pipeline);
    DXENGINE_API void DestroyDXRPipeline(RayTraceVS::DXEngine::DXRPipeline* pipeline);
    DXENGINE_API void DispatchRays(RayTraceVS::DXEngine::DXRPipeline* pipeline, int width, int height);

    DXENGINE_API RayTraceVS::DXEngine::Scene* CreateScene();
    DXENGINE_API void DestroyScene(RayTraceVS::DXEngine::Scene* scene);
    DXENGINE_API void ClearScene(RayTraceVS::DXEngine::Scene* scene);
    DXENGINE_API void SetCamera(RayTraceVS::DXEngine::Scene* scene, const CameraDataNative& camera);
    DXENGINE_API void SetRenderSettings(RayTraceVS::DXEngine::Scene* scene, int samplesPerPixel, int maxBounces, int traceRecursionDepth, float exposure, int toneMapOperator, float denoiserStabilization, float shadowStrength, bool enableDenoiser, float gamma, int photonDebugMode, float photonDebugScale);
    DXENGINE_API void AddSphere(RayTraceVS::DXEngine::Scene* scene, const SphereDataNative& sphere);
    DXENGINE_API void AddPlane(RayTraceVS::DXEngine::Scene* scene, const PlaneDataNative& plane);
    DXENGINE_API void AddBox(RayTraceVS::DXEngine::Scene* scene, const BoxDataNative& box);
    DXENGINE_API void AddLight(RayTraceVS::DXEngine::Scene* scene, const LightDataNative& light);
    DXENGINE_API void AddMeshCache(RayTraceVS::DXEngine::Scene* scene, const MeshCacheDataNative& meshCache);
    DXENGINE_API void AddMeshInstance(RayTraceVS::DXEngine::Scene* scene, const MeshInstanceDataNative& meshInstance);

    // Render target related
    DXENGINE_API RayTraceVS::DXEngine::RenderTarget* CreateRenderTarget(RayTraceVS::DXEngine::DXContext* context);
    DXENGINE_API void DestroyRenderTarget(RayTraceVS::DXEngine::RenderTarget* target);
    DXENGINE_API bool InitializeRenderTarget(RayTraceVS::DXEngine::RenderTarget* target, int width, int height);
    DXENGINE_API void RenderTestPattern(RayTraceVS::DXEngine::DXRPipeline* pipeline, RayTraceVS::DXEngine::RenderTarget* target, RayTraceVS::DXEngine::Scene* scene);
    DXENGINE_API bool CopyRenderTargetToReadback(RayTraceVS::DXEngine::RenderTarget* target, RayTraceVS::DXEngine::DXContext* context);
    DXENGINE_API bool ReadRenderTargetPixels(RayTraceVS::DXEngine::RenderTarget* target, unsigned char* outData, int dataSize);
    
    // Command list execution and GPU wait
    DXENGINE_API void ExecuteCommandList(RayTraceVS::DXEngine::DXContext* context);
    DXENGINE_API void WaitForGPU(RayTraceVS::DXEngine::DXContext* context);
}

#pragma once

#include <vector>
#include <memory>
#include <string>
#include <unordered_map>
#include <DirectXMath.h>
#include "Camera.h"
#include "Light.h"
#include "Objects/RayTracingObject.h"
#include "../RenderSettings.h"

namespace RayTraceVS::DXEngine
{
    // ============================================
    // Mesh Data Structures (C++ side, for Scene management)
    // ============================================

    // Raw mesh data from cache (interleaved vertex format)
    struct MeshCacheEntry
    {
        std::string name;
        std::vector<float> vertices;    // 8 floats per vertex (pos3 + pad + normal3 + pad)
        std::vector<uint32_t> indices;
        DirectX::XMFLOAT3 boundsMin;
        DirectX::XMFLOAT3 boundsMax;
    };

    // Material for a mesh instance
    struct MeshMaterial
    {
        DirectX::XMFLOAT4 color = { 0.8f, 0.8f, 0.8f, 1.0f };
        float metallic = 0.0f;
        float roughness = 0.5f;
        float transmission = 0.0f;
        float ior = 1.5f;
        float specular = 0.5f;
        DirectX::XMFLOAT3 emission = { 0.0f, 0.0f, 0.0f };
        DirectX::XMFLOAT3 absorption = { 0.0f, 0.0f, 0.0f };
    };

    // Transform for a mesh instance
    struct MeshTransform
    {
        DirectX::XMFLOAT3 position = { 0.0f, 0.0f, 0.0f };
        DirectX::XMFLOAT3 rotation = { 0.0f, 0.0f, 0.0f };  // Euler angles (degrees)
        DirectX::XMFLOAT3 scale = { 1.0f, 1.0f, 1.0f };
    };

    // A mesh instance in the scene
    struct MeshInstance
    {
        std::string meshName;       // Reference to MeshCacheEntry by name
        MeshTransform transform;
        MeshMaterial material;
    };

    class Scene
    {
    public:
        Scene();
        ~Scene();

        void SetCamera(const Camera& cam) { camera = cam; }
        Camera& GetCamera() { return camera; }
        const Camera& GetCamera() const { return camera; }

        void SetRenderSettings(const RenderSettings& settings) { renderSettings = settings; }
        const RenderSettings& GetRenderSettings() const { return renderSettings; }

        // Individual getters kept so existing engine code (DXRPipeline etc.) is unchanged
        int GetSamplesPerPixel() const { return renderSettings.samplesPerPixel; }
        int GetMaxBounces() const { return renderSettings.maxBounces; }
        int GetTraceRecursionDepth() const { return renderSettings.traceRecursionDepth; }
        float GetExposure() const { return renderSettings.exposure; }
        int GetToneMapOperator() const { return renderSettings.toneMapOperator; }
        float GetDenoiserStabilization() const { return renderSettings.denoiserStabilization; }
        float GetShadowStrength() const { return renderSettings.shadowStrength; }
        float GetShadowAbsorptionScale() const { return renderSettings.shadowAbsorptionScale; }
        bool GetEnableDenoiser() const { return renderSettings.enableDenoiser; }
        float GetGamma() const { return renderSettings.gamma; }
        int GetPhotonDebugMode() const { return renderSettings.photonDebugMode; }
        float GetPhotonDebugScale() const { return renderSettings.photonDebugScale; }

        // P1 optimization getters
        float GetLightAttenuationConstant() const { return renderSettings.lightAttenuationConstant; }
        float GetLightAttenuationLinear() const { return renderSettings.lightAttenuationLinear; }
        float GetLightAttenuationQuadratic() const { return renderSettings.lightAttenuationQuadratic; }
        int GetMaxShadowLights() const { return renderSettings.maxShadowLights; }
        float GetNRDBypassDistanceThreshold() const { return renderSettings.nrdBypassDistanceThreshold; }
        float GetNRDBypassBlendRange() const { return renderSettings.nrdBypassBlendRange; }

        void AddObject(std::shared_ptr<RayTracingObject> obj);
        void AddLight(const Light& light);

        // Mesh support
        void AddMeshCache(const MeshCacheEntry& cache);
        void AddMeshInstance(const MeshInstance& instance);
        bool HasMeshCache(const std::string& name) const { return meshCaches.find(name) != meshCaches.end(); }

        const std::unordered_map<std::string, MeshCacheEntry>& GetMeshCaches() const { return meshCaches; }
        const std::vector<MeshInstance>& GetMeshInstances() const { return meshInstances; }
        size_t GetMeshInstanceCount() const { return meshInstances.size(); }

        void Clear();

        const std::vector<std::shared_ptr<RayTracingObject>>& GetObjects() const { return objects; }
        const std::vector<Light>& GetLights() const { return lights; }

    private:
        Camera camera;
        std::vector<std::shared_ptr<RayTracingObject>> objects;
        std::vector<Light> lights;
        
        // Mesh data
        std::unordered_map<std::string, MeshCacheEntry> meshCaches;  // Shared mesh geometry by name
        std::vector<MeshInstance> meshInstances;  // Instances referencing mesh caches

        RenderSettings renderSettings;
    };
}

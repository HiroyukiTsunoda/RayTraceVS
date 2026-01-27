#pragma once

#include <vector>
#include <memory>
#include <string>
#include <unordered_map>
#include <DirectXMath.h>
#include "Camera.h"
#include "Light.h"
#include "Objects/RayTracingObject.h"

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

        void SetRenderSettings(int samples, int bounces, int traceRecursion, float exp = 1.0f, int tone = 2, float stab = 1.0f, float shadow = 1.0f, float shadowAbsorb = 4.0f, bool denoiser = true, float gam = 2.2f, int photonDebug = 0, float photonDebugScaleVal = 1.0f,
                               float lightAttenConst = 1.0f, float lightAttenLinear = 0.0f, float lightAttenQuad = 0.01f, int maxShadowLights = 2,
                               float nrdBypassDist = 8.0f, float nrdBypassRange = 2.0f)
        {
            samplesPerPixel = samples;
            maxBounces = bounces;
            traceRecursionDepth = traceRecursion;
            exposure = exp;
            toneMapOperator = tone;
            denoiserStabilization = stab;
            shadowStrength = shadow;
            shadowAbsorptionScale = shadowAbsorb;
            enableDenoiser = denoiser;
            gamma = gam;
            photonDebugMode = photonDebug;
            photonDebugScale = photonDebugScaleVal;
            // P1 optimization settings
            lightAttenuationConstant = lightAttenConst;
            lightAttenuationLinear = lightAttenLinear;
            lightAttenuationQuadratic = lightAttenQuad;
            this->maxShadowLights = maxShadowLights;
            nrdBypassDistanceThreshold = nrdBypassDist;
            nrdBypassBlendRange = nrdBypassRange;
        }
        int GetSamplesPerPixel() const { return samplesPerPixel; }
        int GetMaxBounces() const { return maxBounces; }
        int GetTraceRecursionDepth() const { return traceRecursionDepth; }
        float GetExposure() const { return exposure; }
        int GetToneMapOperator() const { return toneMapOperator; }
        float GetDenoiserStabilization() const { return denoiserStabilization; }
        float GetShadowStrength() const { return shadowStrength; }
        float GetShadowAbsorptionScale() const { return shadowAbsorptionScale; }
        bool GetEnableDenoiser() const { return enableDenoiser; }
        float GetGamma() const { return gamma; }
        int GetPhotonDebugMode() const { return photonDebugMode; }
        float GetPhotonDebugScale() const { return photonDebugScale; }
        
        // P1 optimization getters
        float GetLightAttenuationConstant() const { return lightAttenuationConstant; }
        float GetLightAttenuationLinear() const { return lightAttenuationLinear; }
        float GetLightAttenuationQuadratic() const { return lightAttenuationQuadratic; }
        int GetMaxShadowLights() const { return maxShadowLights; }
        float GetNRDBypassDistanceThreshold() const { return nrdBypassDistanceThreshold; }
        float GetNRDBypassBlendRange() const { return nrdBypassBlendRange; }

        void AddObject(std::shared_ptr<RayTracingObject> obj);
        void AddLight(const Light& light);

        // Mesh support
        void AddMeshCache(const MeshCacheEntry& cache);
        void AddMeshInstance(const MeshInstance& instance);
        
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
        
        int samplesPerPixel = 1;
        int maxBounces = 6;
        int traceRecursionDepth = 2;
        float exposure = 1.0f;
        int toneMapOperator = 2;
        float denoiserStabilization = 1.0f;
        float shadowStrength = 1.0f;
        float shadowAbsorptionScale = 4.0f;
        bool enableDenoiser = true;
        float gamma = 1.0f;
        int photonDebugMode = 0;
        float photonDebugScale = 1.0f;
        
        // P1 optimization settings
        float lightAttenuationConstant = 1.0f;
        float lightAttenuationLinear = 0.0f;
        float lightAttenuationQuadratic = 0.01f;
        int maxShadowLights = 2;
        float nrdBypassDistanceThreshold = 8.0f;
        float nrdBypassBlendRange = 2.0f;
    };
}

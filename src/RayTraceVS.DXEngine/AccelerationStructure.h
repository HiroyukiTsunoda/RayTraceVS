#pragma once

#include <d3d12.h>
#include "d3dx12.h"
#include <wrl/client.h>
#include <vector>
#include <memory>
#include <unordered_map>
#include <set>
#include <string>
#include <DirectXMath.h>

using Microsoft::WRL::ComPtr;
using namespace DirectX;

namespace RayTraceVS::DXEngine
{
    class DXContext;
    class Scene;
    struct MeshCacheEntry;

    // Forward declare ObjectType from RayTracingObject.h
    enum class ObjectType;

    // AABB structure for procedural geometry (must match D3D12_RAYTRACING_AABB)
    struct AABB
    {
        float MinX, MinY, MinZ;
        float MaxX, MaxY, MaxZ;
    };

    // Geometry instance info for shader access
    struct GeometryInstanceInfo
    {
        ObjectType type;
        UINT objectIndex;  // Index into the type-specific buffer
    };

    // Legacy triangle-based geometry data (kept for compatibility)
    struct GeometryData
    {
        ComPtr<ID3D12Resource> vertexBuffer;
        UINT vertexCount;
        ComPtr<ID3D12Resource> indexBuffer;
        UINT indexCount;
    };

    // Mesh BLAS entry (shared per mesh type)
    struct MeshBLASEntry
    {
        ComPtr<ID3D12Resource> blas;
        ComPtr<ID3D12Resource> vertexBuffer;
        ComPtr<ID3D12Resource> indexBuffer;
        ComPtr<ID3D12Resource> scratchBuffer;  // Must persist until GPU finishes building
        UINT vertexCount;
        UINT indexCount;
    };

    class AccelerationStructure
    {
    public:
        AccelerationStructure(DXContext* context);
        ~AccelerationStructure();

        // Legacy triangle-based BLAS (kept for compatibility)
        void BuildBLAS(const std::vector<GeometryData>& geometries);
        void BuildTLAS(const std::vector<D3D12_RAYTRACING_INSTANCE_DESC>& instances);

        // Procedural geometry BLAS for ray tracing analytic shapes
        bool BuildProceduralBLAS(Scene* scene);
        bool BuildProceduralTLAS();
        
        // Mesh BLAS support (shared BLAS per mesh type)
        bool BuildMeshBLAS(const std::string& meshName, const MeshCacheEntry& meshCache);
        bool HasMeshBLAS(const std::string& meshName) const;
        MeshBLASEntry* GetMeshBLAS(const std::string& meshName);
        
        // Combined TLAS (procedural + triangle meshes)
        bool BuildCombinedTLAS(Scene* scene);

        ID3D12Resource* GetTLAS() const { return topLevelAS.Get(); }
        ID3D12Resource* GetBLAS() const { return bottomLevelAS.Get(); }
        
        // Get instance info for shader
        const std::vector<GeometryInstanceInfo>& GetInstanceInfo() const { return instanceInfo; }
        UINT GetTotalObjectCount() const { return totalObjectCount; }
        
        // Clear mesh BLASes (for scene reload)
        // Also resets TLAS to prevent dangling references
        void ClearMeshBLAS() { 
            topLevelAS.Reset();  // Reset TLAS first to avoid dangling BLAS references
            meshBLASMap.clear(); 
        }
        
        // Remove mesh BLASes not in the current scene (safer than clearing all)
        // Takes a set of mesh names that should be kept
        void RemoveStaleMeshBLAS(const std::set<std::string>& currentMeshNames) {
            // First reset TLAS to avoid dangling references during removal
            topLevelAS.Reset();
            for (auto it = meshBLASMap.begin(); it != meshBLASMap.end(); ) {
                if (currentMeshNames.find(it->first) == currentMeshNames.end()) {
                    it = meshBLASMap.erase(it);
                } else {
                    ++it;
                }
            }
        }

    private:
        DXContext* dxContext;

        // Acceleration structures
        ComPtr<ID3D12Resource> bottomLevelAS;       // Procedural BLAS
        ComPtr<ID3D12Resource> topLevelAS;          // Combined TLAS
        ComPtr<ID3D12Resource> scratchBuffer;
        ComPtr<ID3D12Resource> instanceBuffer;

        // AABB buffer for procedural geometry
        ComPtr<ID3D12Resource> aabbBuffer;
        ComPtr<ID3D12Resource> aabbUploadBuffer;
        
        // TLAS scratch buffer (must persist until GPU finishes building)
        ComPtr<ID3D12Resource> tlasScratchBuffer;
        
        // Mesh BLASes (shared per mesh type, keyed by mesh name)
        std::unordered_map<std::string, MeshBLASEntry> meshBLASMap;

        // Instance info for shader
        std::vector<GeometryInstanceInfo> instanceInfo;
        UINT totalObjectCount = 0;

        // Helper functions
        void CreateBuffer(UINT64 size, D3D12_RESOURCE_FLAGS flags, D3D12_RESOURCE_STATES initialState, ID3D12Resource** resource);
        void CreateUploadBuffer(UINT64 size, ID3D12Resource** resource);
        
        // AABB calculation for each object type
        static AABB CalculateSphereAABB(const XMFLOAT3& center, float radius);
        static AABB CalculatePlaneAABB(const XMFLOAT3& position, const XMFLOAT3& normal);
        static AABB CalculateBoxAABB(const XMFLOAT3& center, const XMFLOAT3& size);
    };
}

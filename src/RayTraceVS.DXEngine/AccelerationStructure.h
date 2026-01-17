#pragma once

#include <d3d12.h>
#include "d3dx12.h"
#include <wrl/client.h>
#include <vector>
#include <memory>
#include <DirectXMath.h>

using Microsoft::WRL::ComPtr;
using namespace DirectX;

namespace RayTraceVS::DXEngine
{
    class DXContext;
    class Scene;

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

        ID3D12Resource* GetTLAS() const { return topLevelAS.Get(); }
        ID3D12Resource* GetBLAS() const { return bottomLevelAS.Get(); }
        
        // Get instance info for shader
        const std::vector<GeometryInstanceInfo>& GetInstanceInfo() const { return instanceInfo; }
        UINT GetTotalObjectCount() const { return totalObjectCount; }

    private:
        DXContext* dxContext;

        // Acceleration structures
        ComPtr<ID3D12Resource> bottomLevelAS;
        ComPtr<ID3D12Resource> topLevelAS;
        ComPtr<ID3D12Resource> scratchBuffer;
        ComPtr<ID3D12Resource> instanceBuffer;

        // AABB buffer for procedural geometry
        ComPtr<ID3D12Resource> aabbBuffer;
        ComPtr<ID3D12Resource> aabbUploadBuffer;

        // Instance info for shader
        std::vector<GeometryInstanceInfo> instanceInfo;
        UINT totalObjectCount = 0;

        // Helper functions
        void CreateBuffer(UINT64 size, D3D12_RESOURCE_FLAGS flags, D3D12_RESOURCE_STATES initialState, ID3D12Resource** resource);
        void CreateUploadBuffer(UINT64 size, ID3D12Resource** resource);
        
        // AABB calculation for each object type
        static AABB CalculateSphereAABB(const XMFLOAT3& center, float radius);
        static AABB CalculatePlaneAABB(const XMFLOAT3& position, const XMFLOAT3& normal);
        static AABB CalculateCylinderAABB(const XMFLOAT3& position, const XMFLOAT3& axis, float radius, float height);
    };
}

#pragma once

#include <d3d12.h>
#include "d3dx12.h"
#include <wrl/client.h>
#include <vector>
#include <memory>

using Microsoft::WRL::ComPtr;

namespace RayTraceVS::DXEngine
{
    class DXContext;

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

        void BuildBLAS(const std::vector<GeometryData>& geometries);
        void BuildTLAS(const std::vector<D3D12_RAYTRACING_INSTANCE_DESC>& instances);

        ID3D12Resource* GetTLAS() const { return topLevelAS.Get(); }

    private:
        DXContext* dxContext;

        ComPtr<ID3D12Resource> bottomLevelAS;
        ComPtr<ID3D12Resource> topLevelAS;
        ComPtr<ID3D12Resource> scratchBuffer;
        ComPtr<ID3D12Resource> instanceBuffer;

        void CreateBuffer(UINT64 size, D3D12_RESOURCE_FLAGS flags, D3D12_RESOURCE_STATES initialState, ID3D12Resource** resource);
    };
}

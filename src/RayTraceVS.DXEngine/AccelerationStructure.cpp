#include "AccelerationStructure.h"
#include "DXContext.h"
#include <stdexcept>

namespace RayTraceVS::DXEngine
{
    AccelerationStructure::AccelerationStructure(DXContext* context)
        : dxContext(context)
    {
    }

    AccelerationStructure::~AccelerationStructure()
    {
    }

    void AccelerationStructure::BuildBLAS(const std::vector<GeometryData>& geometries)
    {
        // Build BLAS
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS inputs = {};
        inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
        inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
        inputs.NumDescs = static_cast<UINT>(geometries.size());
        inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;

        // Create geometry descriptors
        std::vector<D3D12_RAYTRACING_GEOMETRY_DESC> geometryDescs;
        for (const auto& geom : geometries)
        {
            D3D12_RAYTRACING_GEOMETRY_DESC desc = {};
            desc.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES;
            desc.Flags = D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE;
            
            desc.Triangles.VertexBuffer.StartAddress = geom.vertexBuffer->GetGPUVirtualAddress();
            desc.Triangles.VertexBuffer.StrideInBytes = sizeof(float) * 3;
            desc.Triangles.VertexFormat = DXGI_FORMAT_R32G32B32_FLOAT;
            desc.Triangles.VertexCount = geom.vertexCount;

            if (geom.indexBuffer)
            {
                desc.Triangles.IndexBuffer = geom.indexBuffer->GetGPUVirtualAddress();
                desc.Triangles.IndexFormat = DXGI_FORMAT_R32_UINT;
                desc.Triangles.IndexCount = geom.indexCount;
            }

            geometryDescs.push_back(desc);
        }

        inputs.pGeometryDescs = geometryDescs.data();

        // Get sizes
        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO prebuildInfo = {};
        dxContext->GetDevice()->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &prebuildInfo);

        // Create buffers
        CreateBuffer(prebuildInfo.ResultDataMaxSizeInBytes, 
                    D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                    D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE,
                    &bottomLevelAS);

        CreateBuffer(prebuildInfo.ScratchDataSizeInBytes,
                    D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                    D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                    &scratchBuffer);

        // BLAS build descriptor
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC buildDesc = {};
        buildDesc.Inputs = inputs;
        buildDesc.DestAccelerationStructureData = bottomLevelAS->GetGPUVirtualAddress();
        buildDesc.ScratchAccelerationStructureData = scratchBuffer->GetGPUVirtualAddress();

        // Record to command list
        dxContext->GetCommandList()->BuildRaytracingAccelerationStructure(&buildDesc, 0, nullptr);

        // UAV barrier
        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        barrier.UAV.pResource = bottomLevelAS.Get();
        dxContext->GetCommandList()->ResourceBarrier(1, &barrier);
    }

    void AccelerationStructure::BuildTLAS(const std::vector<D3D12_RAYTRACING_INSTANCE_DESC>& instances)
    {
        // Create instance buffer
        UINT64 instanceBufferSize = instances.size() * sizeof(D3D12_RAYTRACING_INSTANCE_DESC);
        CreateBuffer(instanceBufferSize,
                    D3D12_RESOURCE_FLAG_NONE,
                    D3D12_RESOURCE_STATE_GENERIC_READ,
                    &instanceBuffer);

        // Upload instance data
        void* mappedData;
        instanceBuffer->Map(0, nullptr, &mappedData);
        memcpy(mappedData, instances.data(), instanceBufferSize);
        instanceBuffer->Unmap(0, nullptr);

        // Build TLAS
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS inputs = {};
        inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
        inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
        inputs.NumDescs = static_cast<UINT>(instances.size());
        inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
        inputs.InstanceDescs = instanceBuffer->GetGPUVirtualAddress();

        // Get sizes
        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO prebuildInfo = {};
        dxContext->GetDevice()->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &prebuildInfo);

        // Create TLAS buffer
        CreateBuffer(prebuildInfo.ResultDataMaxSizeInBytes,
                    D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                    D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE,
                    &topLevelAS);

        // TLAS build descriptor
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC buildDesc = {};
        buildDesc.Inputs = inputs;
        buildDesc.DestAccelerationStructureData = topLevelAS->GetGPUVirtualAddress();
        buildDesc.ScratchAccelerationStructureData = scratchBuffer->GetGPUVirtualAddress();

        // Record to command list
        dxContext->GetCommandList()->BuildRaytracingAccelerationStructure(&buildDesc, 0, nullptr);

        // UAV barrier
        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        barrier.UAV.pResource = topLevelAS.Get();
        dxContext->GetCommandList()->ResourceBarrier(1, &barrier);
    }

    void AccelerationStructure::CreateBuffer(UINT64 size, D3D12_RESOURCE_FLAGS flags, D3D12_RESOURCE_STATES initialState, ID3D12Resource** resource)
    {
        CD3DX12_HEAP_PROPERTIES heapProps(D3D12_HEAP_TYPE_DEFAULT);
        CD3DX12_RESOURCE_DESC resourceDesc = CD3DX12_RESOURCE_DESC::Buffer(size, flags);

        if (FAILED(dxContext->GetDevice()->CreateCommittedResource(
            &heapProps,
            D3D12_HEAP_FLAG_NONE,
            &resourceDesc,
            initialState,
            nullptr,
            IID_PPV_ARGS(resource))))
        {
            throw std::runtime_error("Failed to create buffer");
        }
    }
}

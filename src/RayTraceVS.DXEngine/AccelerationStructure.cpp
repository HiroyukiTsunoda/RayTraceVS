// Prevent Windows min/max macro conflicts
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include "AccelerationStructure.h"
#include "DXContext.h"
#include "DebugLog.h"
#include "Scene/Scene.h"
#include "Scene/Objects/Sphere.h"
#include "Scene/Objects/Plane.h"
#include "Scene/Objects/Box.h"
#include <stdexcept>
#include <algorithm>
#include <cmath>

namespace RayTraceVS::DXEngine
{
    static void SetCommandListName(ID3D12GraphicsCommandList* commandList, const wchar_t* name)
    {
        if (commandList && name)
        {
            commandList->SetName(name);
        }
    }

    AccelerationStructure::AccelerationStructure(DXContext* context)
        : dxContext(context)
    {
    }

    AccelerationStructure::~AccelerationStructure()
    {
    }

    // ============================================
    // AABB Calculation Functions
    // ============================================

    AABB AccelerationStructure::CalculateSphereAABB(const XMFLOAT3& center, float radius)
    {
        AABB aabb;
        aabb.MinX = center.x - radius;
        aabb.MinY = center.y - radius;
        aabb.MinZ = center.z - radius;
        aabb.MaxX = center.x + radius;
        aabb.MaxY = center.y + radius;
        aabb.MaxZ = center.z + radius;
        return aabb;
    }

    AABB AccelerationStructure::CalculatePlaneAABB(const XMFLOAT3& position, const XMFLOAT3& normal)
    {
        // Planes are infinite, so we use a large but finite AABB
        // The AABB is a thin slab centered at the plane position
        const float extent = 1000.0f;  // Large extent for infinite plane
        const float thickness = 0.01f;

        // Normalize the normal vector
        XMVECTOR n = XMVector3Normalize(XMLoadFloat3(&normal));
        XMFLOAT3 normalizedNormal;
        XMStoreFloat3(&normalizedNormal, n);

        // Create tangent vectors for the plane
        XMVECTOR tangent, bitangent;
        if (std::abs(normalizedNormal.y) < 0.999f)
        {
            tangent = XMVector3Cross(XMVectorSet(0, 1, 0, 0), n);
        }
        else
        {
            tangent = XMVector3Cross(XMVectorSet(1, 0, 0, 0), n);
        }
        tangent = XMVector3Normalize(tangent);
        bitangent = XMVector3Cross(n, tangent);

        // Calculate AABB corners
        AABB aabb;
        aabb.MinX = position.x - extent;
        aabb.MinY = position.y - extent;
        aabb.MinZ = position.z - extent;
        aabb.MaxX = position.x + extent;
        aabb.MaxY = position.y + extent;
        aabb.MaxZ = position.z + extent;

        return aabb;
    }

    AABB AccelerationStructure::CalculateBoxAABB(const XMFLOAT3& center, const XMFLOAT3& size)
    {
        // size contains half-extents
        AABB aabb;
        aabb.MinX = center.x - size.x;
        aabb.MinY = center.y - size.y;
        aabb.MinZ = center.z - size.z;
        aabb.MaxX = center.x + size.x;
        aabb.MaxY = center.y + size.y;
        aabb.MaxZ = center.z + size.z;

        return aabb;
    }

    // ============================================
    // Procedural Geometry BLAS/TLAS
    // ============================================

    bool AccelerationStructure::BuildProceduralBLAS(Scene* scene)
    {
        if (!scene || !dxContext->IsDXRSupported())
            return false;

        auto device = dxContext->GetDevice();
        auto commandList = dxContext->GetCommandList();
        SetCommandListName(commandList, L"CmdList_BuildMeshBLAS");
        SetCommandListName(commandList, L"CmdList_BuildProceduralBLAS");

        // Collect objects and calculate AABBs
        const auto& objects = scene->GetObjects();
        std::vector<AABB> aabbs;
        instanceInfo.clear();

        UINT sphereIndex = 0, planeIndex = 0, boxIndex = 0;

        // Collect objects by type to match shader PrimitiveIndex ordering
        std::vector<Sphere*> spheres;
        std::vector<Plane*> planes;
        std::vector<Box*> boxes;
        spheres.reserve(objects.size());
        planes.reserve(objects.size());
        boxes.reserve(objects.size());

        for (const auto& obj : objects)
        {
            if (auto sphere = dynamic_cast<Sphere*>(obj.get()))
                spheres.push_back(sphere);
            else if (auto plane = dynamic_cast<Plane*>(obj.get()))
                planes.push_back(plane);
            else if (auto box = dynamic_cast<Box*>(obj.get()))
                boxes.push_back(box);
        }

        for (auto sphere : spheres)
        {
            AABB aabb = CalculateSphereAABB(sphere->GetCenter(), sphere->GetRadius());
            GeometryInstanceInfo info;
            info.type = ObjectType::Sphere;
            info.objectIndex = sphereIndex++;
            aabbs.push_back(aabb);
            instanceInfo.push_back(info);
        }

        for (auto plane : planes)
        {
            AABB aabb = CalculatePlaneAABB(plane->GetPosition(), plane->GetNormal());
            GeometryInstanceInfo info;
            info.type = ObjectType::Plane;
            info.objectIndex = planeIndex++;
            aabbs.push_back(aabb);
            instanceInfo.push_back(info);
        }

        for (auto box : boxes)
        {
            // Compute AABB for OBB using axes (world-space)
            const XMFLOAT3 center = box->GetCenter();
            const XMFLOAT3 size = box->GetSize(); // half-extents
            XMFLOAT3 ax = box->GetAxisX();
            XMFLOAT3 ay = box->GetAxisY();
            XMFLOAT3 az = box->GetAxisZ();
            // Normalize axes to be safe
            auto norm = [](const XMFLOAT3& v) {
                float len = std::sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
                if (len > 1e-6f) return XMFLOAT3(v.x / len, v.y / len, v.z / len);
                return XMFLOAT3(0.0f, 0.0f, 0.0f);
            };
            ax = norm(ax);
            ay = norm(ay);
            az = norm(az);

            // AABB half-extents = sum of absolute axis components scaled by size
            const float hx = std::abs(ax.x) * size.x + std::abs(ay.x) * size.y + std::abs(az.x) * size.z;
            const float hy = std::abs(ax.y) * size.x + std::abs(ay.y) * size.y + std::abs(az.y) * size.z;
            const float hz = std::abs(ax.z) * size.x + std::abs(ay.z) * size.y + std::abs(az.z) * size.z;

            AABB aabb;
            aabb.MinX = center.x - hx;
            aabb.MinY = center.y - hy;
            aabb.MinZ = center.z - hz;
            aabb.MaxX = center.x + hx;
            aabb.MaxY = center.y + hy;
            aabb.MaxZ = center.z + hz;

            GeometryInstanceInfo info;
            info.type = ObjectType::Box;
            info.objectIndex = boxIndex++;
            aabbs.push_back(aabb);
            instanceInfo.push_back(info);
        }

        if (aabbs.empty())
        {
            // No procedural objects: treat as a valid empty BLAS state
            aabbBuffer.Reset();
            aabbUploadBuffer.Reset();
            bottomLevelAS.Reset();
            scratchBuffer.Reset();
            instanceInfo.clear();
            totalObjectCount = 0;
            return true;
        }

        totalObjectCount = static_cast<UINT>(aabbs.size());

        // Create AABB buffer
        UINT64 aabbBufferSize = sizeof(AABB) * aabbs.size();
        
        // Create default heap buffer
        CD3DX12_HEAP_PROPERTIES defaultHeapProps(D3D12_HEAP_TYPE_DEFAULT);
        CD3DX12_RESOURCE_DESC aabbDesc = CD3DX12_RESOURCE_DESC::Buffer(aabbBufferSize);
        
        if (FAILED(device->CreateCommittedResource(
            &defaultHeapProps,
            D3D12_HEAP_FLAG_NONE,
            &aabbDesc,
            D3D12_RESOURCE_STATE_COMMON,
            nullptr,
            IID_PPV_ARGS(&aabbBuffer))))
        {
            return false;
        }

        // Create upload buffer
        CreateUploadBuffer(aabbBufferSize, &aabbUploadBuffer);

        // Upload AABB data
        void* mappedData = nullptr;
        aabbUploadBuffer->Map(0, nullptr, &mappedData);
        memcpy(mappedData, aabbs.data(), aabbBufferSize);
        aabbUploadBuffer->Unmap(0, nullptr);

        // Copy to default heap
        commandList->CopyResource(aabbBuffer.Get(), aabbUploadBuffer.Get());

        // Transition AABB buffer to non-pixel shader resource
        CD3DX12_RESOURCE_BARRIER barrier = CD3DX12_RESOURCE_BARRIER::Transition(
            aabbBuffer.Get(),
            D3D12_RESOURCE_STATE_COPY_DEST,
            D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        commandList->ResourceBarrier(1, &barrier);

        // Create geometry descriptor for procedural primitives
        D3D12_RAYTRACING_GEOMETRY_DESC geometryDesc = {};
        geometryDesc.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_PROCEDURAL_PRIMITIVE_AABBS;
        // Allow any-hit shaders (needed for shadow/skip-self handling)
        geometryDesc.Flags = D3D12_RAYTRACING_GEOMETRY_FLAG_NONE;
        geometryDesc.AABBs.AABBCount = static_cast<UINT64>(aabbs.size());
        geometryDesc.AABBs.AABBs.StartAddress = aabbBuffer->GetGPUVirtualAddress();
        geometryDesc.AABBs.AABBs.StrideInBytes = sizeof(AABB);

        // Build BLAS inputs
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS inputs = {};
        inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
        inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
        inputs.NumDescs = 1;
        inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
        inputs.pGeometryDescs = &geometryDesc;

        // Get prebuild info
        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO prebuildInfo = {};
        device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &prebuildInfo);

        // Create BLAS buffer
        CreateBuffer(prebuildInfo.ResultDataMaxSizeInBytes,
            D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
            D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE,
            &bottomLevelAS);

        // Create scratch buffer
        UINT64 scratchSize = (std::max)(prebuildInfo.ScratchDataSizeInBytes, prebuildInfo.UpdateScratchDataSizeInBytes);
        CreateBuffer(scratchSize,
            D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
            D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            &scratchBuffer);

        // Build BLAS
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC buildDesc = {};
        buildDesc.Inputs = inputs;
        buildDesc.DestAccelerationStructureData = bottomLevelAS->GetGPUVirtualAddress();
        buildDesc.ScratchAccelerationStructureData = scratchBuffer->GetGPUVirtualAddress();

        commandList->BuildRaytracingAccelerationStructure(&buildDesc, 0, nullptr);

        // UAV barrier
        D3D12_RESOURCE_BARRIER uavBarrier = {};
        uavBarrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        uavBarrier.UAV.pResource = bottomLevelAS.Get();
        commandList->ResourceBarrier(1, &uavBarrier);

        return true;
    }

    bool AccelerationStructure::BuildProceduralTLAS()
    {
        if (!bottomLevelAS || !dxContext->IsDXRSupported())
            return false;

        auto device = dxContext->GetDevice();
        auto commandList = dxContext->GetCommandList();
        SetCommandListName(commandList, L"CmdList_BuildCombinedTLAS");
        SetCommandListName(commandList, L"CmdList_BuildProceduralTLAS");

        // Create single instance pointing to BLAS
        D3D12_RAYTRACING_INSTANCE_DESC instanceDesc = {};
        
        // Identity transform
        instanceDesc.Transform[0][0] = 1.0f;
        instanceDesc.Transform[1][1] = 1.0f;
        instanceDesc.Transform[2][2] = 1.0f;
        
        instanceDesc.InstanceID = 0;
        instanceDesc.InstanceMask = 0xFF;
        instanceDesc.InstanceContributionToHitGroupIndex = 0;
        instanceDesc.Flags = D3D12_RAYTRACING_INSTANCE_FLAG_NONE;
        instanceDesc.AccelerationStructure = bottomLevelAS->GetGPUVirtualAddress();

        // Create instance buffer (upload heap for simplicity)
        UINT64 instanceBufferSize = sizeof(D3D12_RAYTRACING_INSTANCE_DESC);
        CD3DX12_HEAP_PROPERTIES uploadHeapProps(D3D12_HEAP_TYPE_UPLOAD);
        CD3DX12_RESOURCE_DESC instanceBufferDesc = CD3DX12_RESOURCE_DESC::Buffer(instanceBufferSize);

        if (FAILED(device->CreateCommittedResource(
            &uploadHeapProps,
            D3D12_HEAP_FLAG_NONE,
            &instanceBufferDesc,
            D3D12_RESOURCE_STATE_GENERIC_READ,
            nullptr,
            IID_PPV_ARGS(&instanceBuffer))))
        {
            return false;
        }

        // Upload instance data
        void* mappedData = nullptr;
        instanceBuffer->Map(0, nullptr, &mappedData);
        memcpy(mappedData, &instanceDesc, sizeof(instanceDesc));
        instanceBuffer->Unmap(0, nullptr);

        // Build TLAS inputs
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS inputs = {};
        inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
        inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
        inputs.NumDescs = 1;
        inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
        inputs.InstanceDescs = instanceBuffer->GetGPUVirtualAddress();

        // Get prebuild info
        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO prebuildInfo = {};
        device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &prebuildInfo);

        // Create TLAS buffer
        CreateBuffer(prebuildInfo.ResultDataMaxSizeInBytes,
            D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
            D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE,
            &topLevelAS);

        // Build TLAS
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC buildDesc = {};
        buildDesc.Inputs = inputs;
        buildDesc.DestAccelerationStructureData = topLevelAS->GetGPUVirtualAddress();
        buildDesc.ScratchAccelerationStructureData = scratchBuffer->GetGPUVirtualAddress();

        commandList->BuildRaytracingAccelerationStructure(&buildDesc, 0, nullptr);

        // UAV barrier
        D3D12_RESOURCE_BARRIER uavBarrier = {};
        uavBarrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        uavBarrier.UAV.pResource = topLevelAS.Get();
        commandList->ResourceBarrier(1, &uavBarrier);

        return true;
    }

    // ============================================
    // Legacy Triangle-based BLAS/TLAS (kept for compatibility)
    // ============================================

    void AccelerationStructure::BuildBLAS(const std::vector<GeometryData>& geometries)
    {
        auto commandList = dxContext->GetCommandList();
        SetCommandListName(commandList, L"CmdList_BuildBLAS");

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
        commandList->BuildRaytracingAccelerationStructure(&buildDesc, 0, nullptr);

        // UAV barrier
        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        barrier.UAV.pResource = bottomLevelAS.Get();
        commandList->ResourceBarrier(1, &barrier);
    }

    void AccelerationStructure::BuildTLAS(const std::vector<D3D12_RAYTRACING_INSTANCE_DESC>& instances)
    {
        auto commandList = dxContext->GetCommandList();
        SetCommandListName(commandList, L"CmdList_BuildTLAS");

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
        commandList->BuildRaytracingAccelerationStructure(&buildDesc, 0, nullptr);

        // UAV barrier
        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        barrier.UAV.pResource = topLevelAS.Get();
        commandList->ResourceBarrier(1, &barrier);
    }

    // ============================================
    // Helper Functions
    // ============================================

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

    void AccelerationStructure::CreateUploadBuffer(UINT64 size, ID3D12Resource** resource)
    {
        CD3DX12_HEAP_PROPERTIES heapProps(D3D12_HEAP_TYPE_UPLOAD);
        CD3DX12_RESOURCE_DESC resourceDesc = CD3DX12_RESOURCE_DESC::Buffer(size);

        if (FAILED(dxContext->GetDevice()->CreateCommittedResource(
            &heapProps,
            D3D12_HEAP_FLAG_NONE,
            &resourceDesc,
            D3D12_RESOURCE_STATE_GENERIC_READ,
            nullptr,
            IID_PPV_ARGS(resource))))
        {
            throw std::runtime_error("Failed to create upload buffer");
        }
    }

    // ============================================
    // Mesh BLAS Functions
    // ============================================

    bool AccelerationStructure::HasMeshBLAS(const std::string& meshName) const
    {
        return meshBLASMap.find(meshName) != meshBLASMap.end();
    }

    MeshBLASEntry* AccelerationStructure::GetMeshBLAS(const std::string& meshName)
    {
        auto it = meshBLASMap.find(meshName);
        return (it != meshBLASMap.end()) ? &it->second : nullptr;
    }

    bool AccelerationStructure::BuildMeshBLAS(const std::string& meshName, const MeshCacheEntry& meshCache)
    {
        if (meshCache.vertices.empty() || meshCache.indices.empty())
        {
            OutputDebugStringA("[BuildMeshBLAS] ERROR: Empty vertices or indices\n");
            return false;
        }

        auto device = dxContext->GetDevice();
        auto commandList = dxContext->GetCommandList();
        
        if (!device || !commandList)
        {
            OutputDebugStringA("[BuildMeshBLAS] ERROR: device or commandList is null\n");
            return false;
        }

        MeshBLASEntry entry;
        entry.vertexCount = static_cast<UINT>(meshCache.vertices.size() / 8);  // 8 floats per vertex
        entry.indexCount = static_cast<UINT>(meshCache.indices.size());

        // Create vertex buffer (upload heap for simplicity)
        UINT64 vertexBufferSize = meshCache.vertices.size() * sizeof(float);
        {
            CD3DX12_HEAP_PROPERTIES heapProps(D3D12_HEAP_TYPE_UPLOAD);
            CD3DX12_RESOURCE_DESC desc = CD3DX12_RESOURCE_DESC::Buffer(vertexBufferSize);
            device->CreateCommittedResource(&heapProps, D3D12_HEAP_FLAG_NONE, &desc,
                D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&entry.vertexBuffer));
            
            void* mapped = nullptr;
            entry.vertexBuffer->Map(0, nullptr, &mapped);
            memcpy(mapped, meshCache.vertices.data(), vertexBufferSize);
            entry.vertexBuffer->Unmap(0, nullptr);
        }

        // Create index buffer (upload heap for simplicity)
        UINT64 indexBufferSize = meshCache.indices.size() * sizeof(uint32_t);
        {
            CD3DX12_HEAP_PROPERTIES heapProps(D3D12_HEAP_TYPE_UPLOAD);
            CD3DX12_RESOURCE_DESC desc = CD3DX12_RESOURCE_DESC::Buffer(indexBufferSize);
            device->CreateCommittedResource(&heapProps, D3D12_HEAP_FLAG_NONE, &desc,
                D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&entry.indexBuffer));
            
            void* mapped = nullptr;
            entry.indexBuffer->Map(0, nullptr, &mapped);
            memcpy(mapped, meshCache.indices.data(), indexBufferSize);
            entry.indexBuffer->Unmap(0, nullptr);
        }

        // Build geometry descriptor for triangle BLAS
        D3D12_RAYTRACING_GEOMETRY_DESC geometryDesc = {};
        geometryDesc.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES;
        geometryDesc.Flags = D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE;
        geometryDesc.Triangles.VertexBuffer.StartAddress = entry.vertexBuffer->GetGPUVirtualAddress();
        geometryDesc.Triangles.VertexBuffer.StrideInBytes = 32;  // 8 floats * 4 bytes = 32 bytes per vertex
        geometryDesc.Triangles.VertexCount = entry.vertexCount;
        geometryDesc.Triangles.VertexFormat = DXGI_FORMAT_R32G32B32_FLOAT;  // Position is first 3 floats
        geometryDesc.Triangles.IndexBuffer = entry.indexBuffer->GetGPUVirtualAddress();
        geometryDesc.Triangles.IndexCount = entry.indexCount;
        geometryDesc.Triangles.IndexFormat = DXGI_FORMAT_R32_UINT;

        // Get prebuild info
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS inputs = {};
        inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
        inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
        inputs.NumDescs = 1;
        inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
        inputs.pGeometryDescs = &geometryDesc;

        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO prebuildInfo = {};
        device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &prebuildInfo);

        // Create BLAS buffer
        CreateBuffer(prebuildInfo.ResultDataMaxSizeInBytes,
                    D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                    D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE,
                    &entry.blas);

        // Create scratch buffer (stored in entry so it persists until GPU finishes building)
        CreateBuffer(prebuildInfo.ScratchDataSizeInBytes,
                    D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                    D3D12_RESOURCE_STATE_COMMON,
                    &entry.scratchBuffer);

        // Build BLAS
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC buildDesc = {};
        buildDesc.Inputs = inputs;
        buildDesc.DestAccelerationStructureData = entry.blas->GetGPUVirtualAddress();
        buildDesc.ScratchAccelerationStructureData = entry.scratchBuffer->GetGPUVirtualAddress();

        commandList->BuildRaytracingAccelerationStructure(&buildDesc, 0, nullptr);

        // UAV barrier
        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        barrier.UAV.pResource = entry.blas.Get();
        commandList->ResourceBarrier(1, &barrier);

        // Store in map
        meshBLASMap[meshName] = std::move(entry);

        return true;
    }

    bool AccelerationStructure::BuildCombinedTLAS(Scene* scene)
    {
        if (!scene) return false;

        auto device = dxContext->GetDevice();
        auto commandList = dxContext->GetCommandList();

        // Count total instances (procedural + mesh)
        UINT proceduralInstanceCount = (bottomLevelAS != nullptr) ? 1 : 0;
        const auto& meshInstances = scene->GetMeshInstances();
        UINT meshInstanceCount = static_cast<UINT>(meshInstances.size());
        UINT totalInstanceCount = proceduralInstanceCount + meshInstanceCount;

        if (totalInstanceCount == 0)
        {
            // No instances to render
            topLevelAS.Reset();
            return true;
        }

        // Build instance descriptors
        std::vector<D3D12_RAYTRACING_INSTANCE_DESC> instanceDescs;
        instanceDescs.reserve(totalInstanceCount);

        // Add procedural instance (if exists)
        if (bottomLevelAS != nullptr)
        {
            D3D12_RAYTRACING_INSTANCE_DESC proceduralInst = {};
            // Identity transform
            proceduralInst.Transform[0][0] = 1.0f;
            proceduralInst.Transform[1][1] = 1.0f;
            proceduralInst.Transform[2][2] = 1.0f;
            proceduralInst.InstanceID = 0;  // Not used for procedural
            proceduralInst.InstanceMask = 0xFF;
            proceduralInst.InstanceContributionToHitGroupIndex = 0;  // Hit groups 0-2 (procedural)
            proceduralInst.Flags = D3D12_RAYTRACING_INSTANCE_FLAG_NONE;
            proceduralInst.AccelerationStructure = bottomLevelAS->GetGPUVirtualAddress();
            instanceDescs.push_back(proceduralInst);
        }

        // Add mesh instances
        UINT meshInstanceIndex = 0;
        char logBuf[512];
        sprintf_s(logBuf, "[BuildCombinedTLAS] Processing %zu mesh instances", meshInstances.size());
        LOG_INFO(logBuf);
        
        for (const auto& meshInst : meshInstances)
        {
            sprintf_s(logBuf, "[BuildCombinedTLAS] Instance '%s': pos=(%.2f,%.2f,%.2f), rot=(%.2f,%.2f,%.2f), scale=(%.2f,%.2f,%.2f)",
                meshInst.meshName.c_str(),
                meshInst.transform.position.x, meshInst.transform.position.y, meshInst.transform.position.z,
                meshInst.transform.rotation.x, meshInst.transform.rotation.y, meshInst.transform.rotation.z,
                meshInst.transform.scale.x, meshInst.transform.scale.y, meshInst.transform.scale.z);
            LOG_INFO(logBuf);
            
            auto* blasEntry = GetMeshBLAS(meshInst.meshName);
            if (!blasEntry || !blasEntry->blas)
            {
                // Try to build BLAS if not exists
                auto cacheIt = scene->GetMeshCaches().find(meshInst.meshName);
                if (cacheIt != scene->GetMeshCaches().end())
                {
                    BuildMeshBLAS(meshInst.meshName, cacheIt->second);
                    blasEntry = GetMeshBLAS(meshInst.meshName);
                }
                else
                {
                    char buf[256];
                    sprintf_s(buf, "[BuildCombinedTLAS] ERROR: No cache found for '%s'\n", meshInst.meshName.c_str());
                    OutputDebugStringA(buf);
                }
            }
            
            if (!blasEntry || !blasEntry->blas)
            {
                OutputDebugStringA("[BuildCombinedTLAS] WARNING: Skipping instance - no BLAS available\n");
                continue;  // Skip if BLAS still not available
            }

            D3D12_RAYTRACING_INSTANCE_DESC meshInstDesc = {};
            
            // Build transform matrix from position, rotation, scale
            XMMATRIX translation = XMMatrixTranslation(
                meshInst.transform.position.x,
                meshInst.transform.position.y,
                meshInst.transform.position.z);
            XMMATRIX rotation = XMMatrixRotationRollPitchYaw(
                XMConvertToRadians(meshInst.transform.rotation.x),
                XMConvertToRadians(meshInst.transform.rotation.y),
                XMConvertToRadians(meshInst.transform.rotation.z));
            XMMATRIX scale = XMMatrixScaling(
                meshInst.transform.scale.x,
                meshInst.transform.scale.y,
                meshInst.transform.scale.z);
            
            XMMATRIX worldMatrix = scale * rotation * translation;
            
            // Copy to 3x4 row-major format
            // DXR expects column-major style: Transform[row][col] where col=3 is translation
            // DirectXMath stores translation in row 3 (m[3][0..2]), so we need to transpose
            XMFLOAT4X4 worldFloat;
            XMStoreFloat4x4(&worldFloat, XMMatrixTranspose(worldMatrix));
            for (int row = 0; row < 3; row++)
            {
                meshInstDesc.Transform[row][0] = worldFloat.m[row][0];
                meshInstDesc.Transform[row][1] = worldFloat.m[row][1];
                meshInstDesc.Transform[row][2] = worldFloat.m[row][2];
                meshInstDesc.Transform[row][3] = worldFloat.m[row][3];
            }
            
            meshInstDesc.InstanceID = meshInstanceIndex++;  // Used in shader to lookup material
            meshInstDesc.InstanceMask = 0xFF;
            meshInstDesc.InstanceContributionToHitGroupIndex = 3;  // Hit groups 3-5 (triangle)
            meshInstDesc.Flags = D3D12_RAYTRACING_INSTANCE_FLAG_NONE;
            meshInstDesc.AccelerationStructure = blasEntry->blas->GetGPUVirtualAddress();
            instanceDescs.push_back(meshInstDesc);
        }

        if (instanceDescs.empty())
        {
            topLevelAS.Reset();
            return true;
        }

        // Create instance buffer
        UINT64 instanceBufferSize = instanceDescs.size() * sizeof(D3D12_RAYTRACING_INSTANCE_DESC);
        ComPtr<ID3D12Resource> newInstanceBuffer;
        {
            CD3DX12_HEAP_PROPERTIES heapProps(D3D12_HEAP_TYPE_UPLOAD);
            CD3DX12_RESOURCE_DESC desc = CD3DX12_RESOURCE_DESC::Buffer(instanceBufferSize);
            device->CreateCommittedResource(&heapProps, D3D12_HEAP_FLAG_NONE, &desc,
                D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&newInstanceBuffer));
            
            void* mapped = nullptr;
            newInstanceBuffer->Map(0, nullptr, &mapped);
            memcpy(mapped, instanceDescs.data(), instanceBufferSize);
            newInstanceBuffer->Unmap(0, nullptr);
        }

        // Build TLAS
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS inputs = {};
        inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
        inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
        inputs.NumDescs = static_cast<UINT>(instanceDescs.size());
        inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
        inputs.InstanceDescs = newInstanceBuffer->GetGPUVirtualAddress();

        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO prebuildInfo = {};
        device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &prebuildInfo);

        // Create TLAS buffer
        ComPtr<ID3D12Resource> newTopLevelAS;
        CreateBuffer(prebuildInfo.ResultDataMaxSizeInBytes,
                    D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                    D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE,
                    &newTopLevelAS);

        // Create scratch buffer (use member variable so it persists until GPU finishes)
        CreateBuffer(prebuildInfo.ScratchDataSizeInBytes,
                    D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                    D3D12_RESOURCE_STATE_COMMON,
                    &tlasScratchBuffer);

        // Build TLAS
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC buildDesc = {};
        buildDesc.Inputs = inputs;
        buildDesc.DestAccelerationStructureData = newTopLevelAS->GetGPUVirtualAddress();
        buildDesc.ScratchAccelerationStructureData = tlasScratchBuffer->GetGPUVirtualAddress();

        commandList->BuildRaytracingAccelerationStructure(&buildDesc, 0, nullptr);

        // UAV barrier
        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        barrier.UAV.pResource = newTopLevelAS.Get();
        commandList->ResourceBarrier(1, &barrier);

        // Update member variables
        topLevelAS = std::move(newTopLevelAS);
        instanceBuffer = std::move(newInstanceBuffer);

        return true;
    }
}

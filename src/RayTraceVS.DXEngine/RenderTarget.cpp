#include "RenderTarget.h"
#include "DXContext.h"
#include <stdexcept>
#include <stdio.h>

namespace RayTraceVS::DXEngine
{
    RenderTarget::RenderTarget(DXContext* context)
        : dxContext(context), width(0), height(0), readbackMappedData(nullptr)
    {
    }

    RenderTarget::~RenderTarget()
    {
        // Unmap readback buffer if mapped
        if (readbackMappedData && readbackBuffer)
        {
            readbackBuffer->Unmap(0, nullptr);
            readbackMappedData = nullptr;
        }
    }

    bool RenderTarget::Create(UINT w, UINT h)
    {
        width = w;
        height = h;

        // Create UAV resource (R8G8B8A8 for DXR UAV compatibility)
        // RGBA->BGRA conversion is done in ReadPixels for WPF compatibility
        CD3DX12_HEAP_PROPERTIES heapProps(D3D12_HEAP_TYPE_DEFAULT);
        CD3DX12_RESOURCE_DESC resourceDesc = CD3DX12_RESOURCE_DESC::Tex2D(
            DXGI_FORMAT_R8G8B8A8_UNORM,
            width,
            height,
            1, 1, 1, 0,
            D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);

        if (FAILED(dxContext->GetDevice()->CreateCommittedResource(
            &heapProps,
            D3D12_HEAP_FLAG_NONE,
            &resourceDesc,
            D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            nullptr,
            IID_PPV_ARGS(&resource))))
        {
            return false;
        }

        // Create readback buffer
        CD3DX12_HEAP_PROPERTIES readbackHeapProps(D3D12_HEAP_TYPE_READBACK);
        
        // Readback buffer needs to consider row pitch
        UINT64 totalSize = 0;
        UINT64 rowPitch = 0;
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT layout;
        dxContext->GetDevice()->GetCopyableFootprints(
            &resourceDesc,
            0, 1, 0,
            &layout,
            nullptr,
            &rowPitch,
            &totalSize);

        CD3DX12_RESOURCE_DESC readbackDesc = CD3DX12_RESOURCE_DESC::Buffer(totalSize);

        if (FAILED(dxContext->GetDevice()->CreateCommittedResource(
            &readbackHeapProps,
            D3D12_HEAP_FLAG_NONE,
            &readbackDesc,
            D3D12_RESOURCE_STATE_COPY_DEST,
            nullptr,
            IID_PPV_ARGS(&readbackBuffer))))
        {
            return false;
        }

        // Map readback buffer initially (keep mapped)
        HRESULT hr = readbackBuffer->Map(0, nullptr, &readbackMappedData);
        if (FAILED(hr))
        {
            return false;
        }

        return true;
    }

    void RenderTarget::Clear(float r, float g, float b, float a)
    {
        // Clear processing (to be implemented)
    }

    bool RenderTarget::CopyToReadback(ID3D12GraphicsCommandList* commandList)
    {
        if (!resource || !readbackBuffer)
            return false;

        // Transition from UAV to COPY_SOURCE
        CD3DX12_RESOURCE_BARRIER barrier = CD3DX12_RESOURCE_BARRIER::Transition(
            resource.Get(),
            D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            D3D12_RESOURCE_STATE_COPY_SOURCE);
        commandList->ResourceBarrier(1, &barrier);

        // Copy from texture to readback buffer
        D3D12_TEXTURE_COPY_LOCATION srcLocation = {};
        srcLocation.pResource = resource.Get();
        srcLocation.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        srcLocation.SubresourceIndex = 0;

        D3D12_TEXTURE_COPY_LOCATION dstLocation = {};
        dstLocation.pResource = readbackBuffer.Get();
        dstLocation.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        
        // Get footprint information
        D3D12_RESOURCE_DESC desc = resource->GetDesc();
        dxContext->GetDevice()->GetCopyableFootprints(
            &desc,
            0, 1, 0,
            &dstLocation.PlacedFootprint,
            nullptr, nullptr, nullptr);

        commandList->CopyTextureRegion(&dstLocation, 0, 0, 0, &srcLocation, nullptr);

        // Transition from COPY_SOURCE to UAV
        barrier = CD3DX12_RESOURCE_BARRIER::Transition(
            resource.Get(),
            D3D12_RESOURCE_STATE_COPY_SOURCE,
            D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        commandList->ResourceBarrier(1, &barrier);

        return true;
    }

    bool RenderTarget::ReadPixels(std::vector<unsigned char>& outData)
    {
        try
        {
            if (!readbackBuffer)
            {
                // Error code 1: readbackBuffer is null
                outData.resize(4);
                outData[0] = 1;
                return false;
            }
            
            if (!resource)
            {
                // Error code 2: resource is null
                outData.resize(4);
                outData[0] = 2;
                return false;
            }

            if (!readbackMappedData)
            {
                // Error code 4: mappedData is null
                outData.resize(4);
                outData[0] = 4;
                return false;
            }

            // Copy pixel data
            D3D12_RESOURCE_DESC desc = resource->GetDesc();
            
            // Get footprint information to calculate rowPitch
            D3D12_PLACED_SUBRESOURCE_FOOTPRINT layout;
            dxContext->GetDevice()->GetCopyableFootprints(
                &desc,
                0, 1, 0,
                &layout,
                nullptr, nullptr, nullptr);

            UINT rowPitch = layout.Footprint.RowPitch;
            UINT imageRowPitch = width * 4; // RGBA

            outData.resize(imageRowPitch * height);

            // Copy per row (excluding padding)
            for (UINT y = 0; y < height; ++y)
            {
                memcpy(
                    &outData[y * imageRowPitch],
                    static_cast<unsigned char*>(readbackMappedData) + y * rowPitch,
                    imageRowPitch);
            }

            return true;
        }
        catch (...)
        {
            // Error code 5: exception
            outData.resize(4);
            outData[0] = 5;
            return false;
        }
    }
}

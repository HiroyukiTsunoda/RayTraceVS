#pragma once

#include <d3d12.h>
#include "d3dx12.h"
#include <wrl/client.h>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace RayTraceVS::DXEngine
{
    class DXContext;

    class RenderTarget
    {
    public:
        RenderTarget(DXContext* context);
        ~RenderTarget();

        bool Create(UINT width, UINT height);
        void Clear(float r, float g, float b, float a);
        
        // Copy from render target to readback buffer
        bool CopyToReadback(ID3D12GraphicsCommandList* commandList);
        
        // Read pixel data from readback buffer
        bool ReadPixels(std::vector<unsigned char>& outData);
        
        ID3D12Resource* GetResource() const { return resource.Get(); }
        UINT GetWidth() const { return width; }
        UINT GetHeight() const { return height; }

    private:
        DXContext* dxContext;
        ComPtr<ID3D12Resource> resource;
        ComPtr<ID3D12Resource> readbackBuffer;
        void* readbackMappedData;
        UINT width;
        UINT height;
    };
}

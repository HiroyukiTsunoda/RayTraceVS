#include "DXRPipeline.h"
#include "DXContext.h"
#include "RenderTarget.h"
#include <d3dcompiler.h>
#include <stdexcept>
#include <DirectXMath.h>

#pragma comment(lib, "d3dcompiler.lib")

using namespace DirectX;

namespace RayTraceVS::DXEngine
{
    DXRPipeline::DXRPipeline(DXContext* context)
        : dxContext(context)
    {
    }

    DXRPipeline::~DXRPipeline()
    {
    }

    bool DXRPipeline::Initialize()
    {
        // Skip DXR initialization for now (test pattern only)
        OutputDebugStringA("DXRPipeline::Initialize - Using test pattern mode only\n");
        return true;
    }

    void DXRPipeline::BuildPipeline()
    {
        // Not used currently
    }

    void DXRPipeline::CreateRootSignatures()
    {
        // Not used currently
    }

    void DXRPipeline::CreatePipelineStateObject()
    {
        // Not used currently
    }

    void DXRPipeline::CreateShaderTables()
    {
        // Not used currently
    }

    void DXRPipeline::DispatchRays(UINT width, UINT height)
    {
        // Execute ray tracing
        auto commandList = dxContext->GetCommandList();

        D3D12_DISPATCH_RAYS_DESC dispatchDesc = {};
        dispatchDesc.Width = width;
        dispatchDesc.Height = height;
        dispatchDesc.Depth = 1;

        // TODO: Configure shader tables and call DispatchRays
    }

    bool DXRPipeline::LoadShader(const wchar_t* filename, ID3DBlob** shader)
    {
        if (FAILED(D3DReadFileToBlob(filename, shader)))
        {
            return false;
        }
        return true;
    }

    void DXRPipeline::RenderTestPattern(RenderTarget* renderTarget)
    {
        if (!renderTarget || !renderTarget->GetResource())
        {
            OutputDebugStringA("RenderTestPattern: Invalid render target\n");
            return;
        }

        try
        {
            auto commandList = dxContext->GetCommandList();
            auto device = dxContext->GetDevice();
            
            if (!commandList || !device)
            {
                OutputDebugStringA("RenderTestPattern: Invalid command list or device\n");
                return;
            }
            
            // CPU ray tracing
            UINT width = renderTarget->GetWidth();
            UINT height = renderTarget->GetHeight();
            
            OutputDebugStringA("RenderTestPattern: Ray tracing sphere\n");
            
            // Calculate rowPitch with 256-byte alignment
            UINT rowPitch = (width * 4 + 255) & ~255;
            UINT totalSize = rowPitch * height;
            
            // Buffer data for upload
            std::vector<unsigned char> patternData(totalSize, 0);
            
            // Scene setup
            XMFLOAT3 cameraPos(0.0f, 2.0f, -5.0f);
            XMFLOAT3 sphereCenter(0.0f, 1.0f, 0.0f);
            float sphereRadius = 1.0f;
            XMFLOAT3 lightPos(-3.0f, 5.0f, -3.0f);
            XMFLOAT3 lightColor(1.0f, 1.0f, 1.0f);
            float lightIntensity = 1.5f;
            XMFLOAT3 sphereColor(1.0f, 0.3f, 0.3f);
            
            float aspectRatio = (float)width / (float)height;
            float fovRadians = XM_PI / 3.0f;
            float tanHalfFov = tanf(fovRadians * 0.5f);
            
            // Ray tracing
            for (UINT y = 0; y < height; ++y)
            {
                for (UINT x = 0; x < width; ++x)
                {
                    // Calculate NDC coordinates
                    float pixelCenterX = (float)x + 0.5f;
                    float pixelCenterY = (float)y + 0.5f;
                    float ndcX = (pixelCenterX / (float)width) * 2.0f - 1.0f;
                    float ndcY = -((pixelCenterY / (float)height) * 2.0f - 1.0f);
                    
                    // Ray direction
                    XMFLOAT3 rayDir(
                        ndcX * aspectRatio * tanHalfFov,
                        ndcY * tanHalfFov,
                        1.0f
                    );
                    
                    // Normalize
                    XMVECTOR rayDirVec = XMLoadFloat3(&rayDir);
                    rayDirVec = XMVector3Normalize(rayDirVec);
                    XMStoreFloat3(&rayDir, rayDirVec);
                    
                    // Background color (sky gradient)
                    float t = 0.5f * (rayDir.y + 1.0f);
                    float bgR = 1.0f * (1.0f - t) + 0.5f * t;
                    float bgG = 1.0f * (1.0f - t) + 0.7f * t;
                    float bgB = 1.0f * (1.0f - t) + 1.0f * t;
                    
                    // Sphere intersection test
                    XMVECTOR camPosVec = XMLoadFloat3(&cameraPos);
                    XMVECTOR sphereCenterVec = XMLoadFloat3(&sphereCenter);
                    XMVECTOR oc = XMVectorSubtract(camPosVec, sphereCenterVec);
                    
                    float a = XMVectorGetX(XMVector3Dot(rayDirVec, rayDirVec));
                    float b = 2.0f * XMVectorGetX(XMVector3Dot(oc, rayDirVec));
                    float c = XMVectorGetX(XMVector3Dot(oc, oc)) - sphereRadius * sphereRadius;
                    float discriminant = b * b - 4.0f * a * c;
                    
                    float finalR = bgR;
                    float finalG = bgG;
                    float finalB = bgB;
                    
                    if (discriminant >= 0.0f)
                    {
                        // Hit sphere
                        float t = (-b - sqrtf(discriminant)) / (2.0f * a);
                        
                        if (t > 0.0f)
                        {
                            // Hit position
                            XMVECTOR hitPos = XMVectorAdd(camPosVec, XMVectorScale(rayDirVec, t));
                            
                            // Normal
                            XMVECTOR normal = XMVector3Normalize(XMVectorSubtract(hitPos, sphereCenterVec));
                            
                            // Ambient
                            float ambient = 0.2f;
                            finalR = sphereColor.x * ambient;
                            finalG = sphereColor.y * ambient;
                            finalB = sphereColor.z * ambient;
                            
                            // Light direction
                            XMVECTOR lightPosVec = XMLoadFloat3(&lightPos);
                            XMVECTOR lightDir = XMVector3Normalize(XMVectorSubtract(lightPosVec, hitPos));
                            
                            // Diffuse reflection
                            float diff = fmaxf(0.0f, XMVectorGetX(XMVector3Dot(normal, lightDir)));
                            
                            finalR += sphereColor.x * lightColor.x * lightIntensity * diff;
                            finalG += sphereColor.y * lightColor.y * lightIntensity * diff;
                            finalB += sphereColor.z * lightColor.z * lightIntensity * diff;
                            
                            // Specular
                            XMVECTOR viewDir = XMVector3Normalize(XMVectorSubtract(camPosVec, hitPos));
                            XMVECTOR reflectDir = XMVectorSubtract(XMVectorScale(normal, 2.0f * XMVectorGetX(XMVector3Dot(lightDir, normal))), lightDir);
                            float spec = powf(fmaxf(0.0f, XMVectorGetX(XMVector3Dot(viewDir, reflectDir))), 32.0f);
                            
                            finalR += lightColor.x * lightIntensity * spec * 0.5f;
                            finalG += lightColor.y * lightIntensity * spec * 0.5f;
                            finalB += lightColor.z * lightIntensity * spec * 0.5f;
                        }
                    }
                    
                    // Clamp
                    finalR = fminf(1.0f, fmaxf(0.0f, finalR));
                    finalG = fminf(1.0f, fmaxf(0.0f, finalG));
                    finalB = fminf(1.0f, fmaxf(0.0f, finalB));
                    
                    UINT index = y * rowPitch + x * 4;
                    patternData[index + 0] = static_cast<unsigned char>(finalR * 255.0f);
                    patternData[index + 1] = static_cast<unsigned char>(finalG * 255.0f);
                    patternData[index + 2] = static_cast<unsigned char>(finalB * 255.0f);
                    patternData[index + 3] = 255;
                }
            }
            
            // Create upload buffer (hold as member variable)
            CD3DX12_HEAP_PROPERTIES uploadHeapProps(D3D12_HEAP_TYPE_UPLOAD);
            CD3DX12_RESOURCE_DESC uploadBufferDesc = CD3DX12_RESOURCE_DESC::Buffer(totalSize);
            
            HRESULT hr = device->CreateCommittedResource(
                &uploadHeapProps,
                D3D12_HEAP_FLAG_NONE,
                &uploadBufferDesc,
                D3D12_RESOURCE_STATE_GENERIC_READ,
                nullptr,
                IID_PPV_ARGS(&uploadBuffer));
                
            if (FAILED(hr))
            {
                OutputDebugStringA("RenderTestPattern: Failed to create upload buffer\n");
                return;
            }
            
            // Copy data to upload buffer
            void* mappedData = nullptr;
            hr = uploadBuffer->Map(0, nullptr, &mappedData);
            if (FAILED(hr))
            {
                OutputDebugStringA("RenderTestPattern: Failed to map upload buffer\n");
                return;
            }
            
            memcpy(mappedData, patternData.data(), totalSize);
            uploadBuffer->Unmap(0, nullptr);
            
            // Transition render target to COPY_DEST
            CD3DX12_RESOURCE_BARRIER barrier = CD3DX12_RESOURCE_BARRIER::Transition(
                renderTarget->GetResource(),
                D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                D3D12_RESOURCE_STATE_COPY_DEST);
            commandList->ResourceBarrier(1, &barrier);
            
            // Copy from upload buffer to texture
            D3D12_TEXTURE_COPY_LOCATION srcLocation = {};
            srcLocation.pResource = uploadBuffer.Get();
            srcLocation.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            srcLocation.PlacedFootprint.Offset = 0;
            srcLocation.PlacedFootprint.Footprint.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            srcLocation.PlacedFootprint.Footprint.Width = width;
            srcLocation.PlacedFootprint.Footprint.Height = height;
            srcLocation.PlacedFootprint.Footprint.Depth = 1;
            srcLocation.PlacedFootprint.Footprint.RowPitch = rowPitch;
            
            D3D12_TEXTURE_COPY_LOCATION dstLocation = {};
            dstLocation.pResource = renderTarget->GetResource();
            dstLocation.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            dstLocation.SubresourceIndex = 0;
            
            commandList->CopyTextureRegion(&dstLocation, 0, 0, 0, &srcLocation, nullptr);
            
            // Transition from COPY_DEST to UAV
            barrier = CD3DX12_RESOURCE_BARRIER::Transition(
                renderTarget->GetResource(),
                D3D12_RESOURCE_STATE_COPY_DEST,
                D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            commandList->ResourceBarrier(1, &barrier);
            
            OutputDebugStringA("RenderTestPattern: Success\n");
        }
        catch (...)
        {
            OutputDebugStringA("RenderTestPattern: Exception caught\n");
        }
    }
}

#include "DXRPipeline.h"
#include "DXContext.h"
#include "RenderTarget.h"
#include "Scene/Scene.h"
#include "Scene/Objects/Sphere.h"
#include "Scene/Objects/Plane.h"
#include "Scene/Objects/Cylinder.h"
#include <d3dcompiler.h>
#include <stdexcept>
#include <DirectXMath.h>
#include <algorithm>

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

    void DXRPipeline::RenderTestPattern(RenderTarget* renderTarget, Scene* scene)
    {
        if (!renderTarget || !renderTarget->GetResource())
            return;
        
        if (!scene)
            return;

        try
        {
            auto commandList = dxContext->GetCommandList();
            auto device = dxContext->GetDevice();
            
            if (!commandList || !device)
                return;
            
            // CPU ray tracing
            UINT width = renderTarget->GetWidth();
            UINT height = renderTarget->GetHeight();
            
            // Calculate rowPitch with 256-byte alignment
            UINT rowPitch = (width * 4 + 255) & ~255;
            UINT totalSize = rowPitch * height;
            
            // Buffer data for upload
            std::vector<unsigned char> patternData(totalSize, 0);
            
            // Fixed scene setup
            XMFLOAT3 cameraPos(0.0f, 2.0f, -5.0f);
            XMFLOAT3 lightPos(3.0f, 5.0f, -3.0f);
            XMFLOAT4 lightColor(1.0f, 1.0f, 1.0f, 1.0f);
            float lightIntensity = 1.5f;
            
            // Scene objects
            // Red sphere on left
            XMFLOAT3 sphereCenter(-2.0f, 1.0f, 0.0f);
            float sphereRadius = 1.0f;
            XMFLOAT4 sphereColor(1.0f, 0.3f, 0.3f, 1.0f);
            
            // Ground plane with checkerboard
            XMFLOAT3 planePosition(0.0f, 0.0f, 0.0f);
            XMFLOAT3 planeNormal(0.0f, 1.0f, 0.0f);
            
            // Blue cylinder on right
            XMFLOAT3 cylinderPosition(2.0f, 0.0f, 0.0f);
            XMFLOAT3 cylinderAxis(0.0f, 1.0f, 0.0f);
            float cylinderRadius = 0.5f;
            float cylinderHeight = 2.5f;
            XMFLOAT4 cylinderColor(0.3f, 0.5f, 1.0f, 1.0f);
            
            float aspectRatio = (float)width / (float)height;
            float fovRadians = 60.0f * XM_PI / 180.0f;
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
                    float skyT = 0.5f * (rayDir.y + 1.0f);
                    float bgR = 1.0f * (1.0f - skyT) + 0.5f * skyT;
                    float bgG = 1.0f * (1.0f - skyT) + 0.7f * skyT;
                    float bgB = 1.0f * (1.0f - skyT) + 1.0f * skyT;
                    
                    XMVECTOR camPosVec = XMLoadFloat3(&cameraPos);
                    
                    // Find closest intersection with all objects
                    float closestT = FLT_MAX;
                    XMVECTOR hitNormal;
                    XMFLOAT3 hitPosition3;
                    XMFLOAT4 objColor(bgR, bgG, bgB, 1.0f);
                    bool hitAnything = false;
                    
                    // 1. Sphere intersection
                    {
                        XMVECTOR sphereCenterVec = XMLoadFloat3(&sphereCenter);
                        XMVECTOR oc = XMVectorSubtract(camPosVec, sphereCenterVec);
                        
                        float a = XMVectorGetX(XMVector3Dot(rayDirVec, rayDirVec));
                        float b = 2.0f * XMVectorGetX(XMVector3Dot(oc, rayDirVec));
                        float c = XMVectorGetX(XMVector3Dot(oc, oc)) - sphereRadius * sphereRadius;
                        float discriminant = b * b - 4.0f * a * c;
                        
                        if (discriminant >= 0.0f)
                        {
                            float t = (-b - sqrtf(discriminant)) / (2.0f * a);
                            
                            if (t > 0.0f && t < closestT)
                            {
                                closestT = t;
                                XMVECTOR hitPos = XMVectorAdd(camPosVec, XMVectorScale(rayDirVec, t));
                                hitNormal = XMVector3Normalize(XMVectorSubtract(hitPos, sphereCenterVec));
                                XMStoreFloat3(&hitPosition3, hitPos);
                                objColor = sphereColor;
                                hitAnything = true;
                            }
                        }
                    }
                    
                    // 2. Plane intersection
                    {
                        XMVECTOR planeNormalVec = XMVector3Normalize(XMLoadFloat3(&planeNormal));
                        float denom = XMVectorGetX(XMVector3Dot(planeNormalVec, rayDirVec));
                        
                        if (fabsf(denom) > 0.0001f)
                        {
                            XMVECTOR planePosVec = XMLoadFloat3(&planePosition);
                            XMVECTOR p0 = XMVectorSubtract(planePosVec, camPosVec);
                            float t = XMVectorGetX(XMVector3Dot(p0, planeNormalVec)) / denom;
                            
                            if (t > 0.0f && t < closestT)
                            {
                                closestT = t;
                                hitNormal = planeNormalVec;
                                XMVECTOR hitPos = XMVectorAdd(camPosVec, XMVectorScale(rayDirVec, t));
                                XMStoreFloat3(&hitPosition3, hitPos);
                                
                                // Checkerboard pattern
                                float checkerSize = 1.0f;
                                int checkX = (int)floorf(hitPosition3.x / checkerSize);
                                int checkZ = (int)floorf(hitPosition3.z / checkerSize);
                                bool isWhite = ((checkX + checkZ) % 2) == 0;
                                
                                if (isWhite)
                                    objColor = XMFLOAT4(0.9f, 0.9f, 0.9f, 1.0f);
                                else
                                    objColor = XMFLOAT4(0.1f, 0.1f, 0.1f, 1.0f);
                                
                                hitAnything = true;
                            }
                        }
                    }
                    
                    // 3. Cylinder intersection
                    {
                        XMVECTOR cylPosVec = XMLoadFloat3(&cylinderPosition);
                        XMVECTOR cylAxisVec = XMVector3Normalize(XMLoadFloat3(&cylinderAxis));
                        XMVECTOR oc = XMVectorSubtract(camPosVec, cylPosVec);
                        
                        // Side surface intersection
                        XMVECTOR dirCrossAxis = XMVector3Cross(rayDirVec, cylAxisVec);
                        XMVECTOR ocCrossAxis = XMVector3Cross(oc, cylAxisVec);
                        
                        float a = XMVectorGetX(XMVector3Dot(dirCrossAxis, dirCrossAxis));
                        float b = 2.0f * XMVectorGetX(XMVector3Dot(dirCrossAxis, ocCrossAxis));
                        float c = XMVectorGetX(XMVector3Dot(ocCrossAxis, ocCrossAxis)) - cylinderRadius * cylinderRadius;
                        
                        float discriminant = b * b - 4.0f * a * c;
                        
                        if (discriminant >= 0.0f && a > 0.0001f)
                        {
                            float sqrtDiscriminant = sqrtf(discriminant);
                            float t1 = (-b - sqrtDiscriminant) / (2.0f * a);
                            float t2 = (-b + sqrtDiscriminant) / (2.0f * a);
                            
                            float tValues[2] = { t1, t2 };
                            
                            for (int i = 0; i < 2; i++)
                            {
                                float t = tValues[i];
                                
                                if (t > 0.0f && t < closestT)
                                {
                                    XMVECTOR hitPos = XMVectorAdd(camPosVec, XMVectorScale(rayDirVec, t));
                                    XMVECTOR localHitPoint = XMVectorSubtract(hitPos, cylPosVec);
                                    float height = XMVectorGetX(XMVector3Dot(localHitPoint, cylAxisVec));
                                    
                                    if (height >= 0.0f && height <= cylinderHeight)
                                    {
                                        closestT = t;
                                        XMVECTOR hitOnAxis = XMVectorAdd(cylPosVec, XMVectorScale(cylAxisVec, height));
                                        hitNormal = XMVector3Normalize(XMVectorSubtract(hitPos, hitOnAxis));
                                        XMStoreFloat3(&hitPosition3, hitPos);
                                        objColor = cylinderColor;
                                        hitAnything = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    
                    float finalR = bgR;
                    float finalG = bgG;
                    float finalB = bgB;
                    
                    if (hitAnything)
                    {
                        XMVECTOR hitPosVec = XMLoadFloat3(&hitPosition3);
                        
                        // Ambient
                        float ambient = 0.2f;
                        finalR = objColor.x * ambient;
                        finalG = objColor.y * ambient;
                        finalB = objColor.z * ambient;
                        
                        // Light direction
                        XMVECTOR lightPosVec = XMLoadFloat3(&lightPos);
                        XMVECTOR lightDir = XMVector3Normalize(XMVectorSubtract(lightPosVec, hitPosVec));
                        
                        // Diffuse reflection
                        float diff = fmaxf(0.0f, XMVectorGetX(XMVector3Dot(hitNormal, lightDir)));
                        
                        finalR += objColor.x * lightColor.x * lightIntensity * diff;
                        finalG += objColor.y * lightColor.y * lightIntensity * diff;
                        finalB += objColor.z * lightColor.z * lightIntensity * diff;
                        
                        // Specular
                        XMVECTOR viewDir = XMVector3Normalize(XMVectorSubtract(camPosVec, hitPosVec));
                        XMVECTOR reflectDir = XMVectorSubtract(XMVectorScale(hitNormal, 2.0f * XMVectorGetX(XMVector3Dot(lightDir, hitNormal))), lightDir);
                        float spec = powf(fmaxf(0.0f, XMVectorGetX(XMVector3Dot(viewDir, reflectDir))), 32.0f);
                        
                        finalR += lightColor.x * lightIntensity * spec * 0.5f;
                        finalG += lightColor.y * lightIntensity * spec * 0.5f;
                        finalB += lightColor.z * lightIntensity * spec * 0.5f;
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
                return;
            
            // Copy data to upload buffer
            void* mappedData = nullptr;
            hr = uploadBuffer->Map(0, nullptr, &mappedData);
            if (FAILED(hr))
                return;
            
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
        }
        catch (...)
        {
            // Silently handle exceptions
        }
    }
}

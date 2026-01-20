#include "DXRPipeline.h"
#include "DXContext.h"
#include "RenderTarget.h"
#include "AccelerationStructure.h"
#include "Denoiser/NRDDenoiser.h"
#include "ShaderCache.h"
#include "Scene/Scene.h"
#include "Scene/Camera.h"
#include "Scene/Light.h"
#include "Scene/Objects/Sphere.h"
#include "Scene/Objects/Plane.h"
#include "Scene/Objects/Box.h"
#include <d3dcompiler.h>
#include <dxcapi.h>
#include <stdexcept>
#include <algorithm>
#include <string>
#include <fstream>

#pragma comment(lib, "dxcompiler.lib")

// Debug log helper
static void LogToFile(const char* message)
{
    std::ofstream log("C:\\git\\RayTraceVS\\debug_log.txt", std::ios::app);
    if (log.is_open())
    {
        log << message << std::endl;
        log.close();
    }
}

static void LogToFile(const char* message, HRESULT hr)
{
    char buf[512];
    sprintf_s(buf, "%s: 0x%08X", message, hr);
    LogToFile(buf);
}

#pragma comment(lib, "d3dcompiler.lib")

namespace RayTraceVS::DXEngine
{
    DXRPipeline::DXRPipeline(DXContext* context)
        : dxContext(context), mappedConstantData(nullptr)
    {
    }

    DXRPipeline::~DXRPipeline()
    {
        if (mappedConstantData && constantBuffer)
        {
            constantBuffer->Unmap(0, nullptr);
            mappedConstantData = nullptr;
        }
    }

    bool DXRPipeline::InitializeShaderPath()
    {
        // Fixed shader paths:
        //   Source: C:\git\RayTraceVS\src\Shader (for .hlsl)
        //   Cache:  C:\git\RayTraceVS\src\Shader\Cache (for .cso)
        shaderBasePath = L"C:\\git\\RayTraceVS\\src\\Shader\\Cache\\";
        shaderSourcePath = L"C:\\git\\RayTraceVS\\src\\Shader\\";
        
        LogToFile(("Shader source path: " + std::string(shaderSourcePath.begin(), shaderSourcePath.end())).c_str());
        LogToFile(("Shader cache path: " + std::string(shaderBasePath.begin(), shaderBasePath.end())).c_str());
        return true;
    }

    bool DXRPipeline::Initialize()
    {
        // Clear log file
        std::ofstream log("C:\\git\\RayTraceVS\\debug_log.txt", std::ios::trunc);
        log.close();
        
        LogToFile("DXRPipeline::Initialize called");
        
        // Initialize shader path first
        if (!InitializeShaderPath())
        {
            LogToFile("Failed to initialize shader path");
            return false;
        }
        
        // Initialize shader cache system
        shaderCache = std::make_unique<ShaderCache>(dxContext);
        if (!shaderCache->Initialize(shaderBasePath, shaderSourcePath))
        {
            LogToFile("Failed to initialize shader cache");
            return false;
        }
        LogToFile("Shader cache initialized");
        
        // Pre-compile all shaders if needed (first run or driver change)
        if (shaderCache->NeedsRecompilation())
        {
            LogToFile("Shaders need compilation, pre-compiling all...");
            shaderCache->PrecompileAll();
        }
        
        // Always create compute pipeline (fallback)
        bool computeResult = CreateComputePipeline();
        LogToFile(computeResult ? "Compute pipeline succeeded" : "Compute pipeline failed");
        
        // Try to create DXR pipeline if supported
        if (dxContext->IsDXRSupported())
        {
            LogToFile("DXR supported, creating DXR pipeline...");
            dxrPipelineReady = CreateDXRPipeline();
            LogToFile(dxrPipelineReady ? "DXR pipeline succeeded" : "DXR pipeline failed, using compute fallback");
        }
        else
        {
            LogToFile("DXR not supported, using compute shader fallback");
            dxrPipelineReady = false;
        }
        
        return computeResult;  // Return true if at least compute pipeline works
    }
    
    // ============================================
    // Main Render Function
    // ============================================
    
    void DXRPipeline::Render(RenderTarget* renderTarget, Scene* scene)
    {
        if (dxrPipelineReady)
        {
            LogToFile("Render: using DXR path");
            RenderWithDXR(renderTarget, scene);
        }
        else
        {
            LogToFile("Render: using Compute path (dxrPipelineReady=false)");
            RenderWithComputeShader(renderTarget, scene);
        }
    }

    bool DXRPipeline::CreateComputePipeline()
    {
        LogToFile("CreateComputePipeline started");
        
        auto device = dxContext->GetDevice();
        if (!device)
        {
            LogToFile("Device is null");
            return false;
        }

        // Use ShaderCache if available
        if (shaderCache)
        {
            LogToFile("CreateComputePipeline: using ShaderCache");
            if (!shaderCache->GetComputeShader(L"RayTraceCompute", L"CSMain", &computeShader))
            {
                LogToFile("CreateComputePipeline: ShaderCache failed to get RayTraceCompute");
                return false;
            }
            LogToFile("CreateComputePipeline: got RayTraceCompute from cache");
        }
        else
        {
            // Fallback: compile directly
            std::wstring computeShaderPath = shaderSourcePath + L"RayTraceCompute.hlsl";
            ComPtr<ID3DBlob> errorBlob;

            LogToFile(("CreateComputePipeline: compiling " + std::string(computeShaderPath.begin(), computeShaderPath.end())).c_str());
            HRESULT hr = D3DCompileFromFile(
                computeShaderPath.c_str(),
                nullptr,
                D3D_COMPILE_STANDARD_FILE_INCLUDE,
                "CSMain",
                "cs_5_1",
                D3DCOMPILE_OPTIMIZATION_LEVEL3 | D3DCOMPILE_DEBUG,
                0,
                &computeShader,
                &errorBlob);

            if (FAILED(hr))
            {
                if (errorBlob)
                {
                    LogToFile("CreateComputePipeline: compute shader compile error");
                    LogToFile((char*)errorBlob->GetBufferPointer());
                }
                return false;
            }
        }

        LogToFile("Creating root signature...");
        HRESULT hr;
        
        // Create root signature
        // Root parameters:
        // 0: CBV - Scene constants
        // 1: UAV - Output texture
        // 2: SRV - Spheres buffer
        // 3: SRV - Planes buffer
        // 4: SRV - Boxes buffer
        // 5: SRV - Lights buffer
        CD3DX12_DESCRIPTOR_RANGE1 ranges[6];
        ranges[0].Init(D3D12_DESCRIPTOR_RANGE_TYPE_CBV, 1, 0);  // b0
        ranges[1].Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 0);  // u0
        ranges[2].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 0);  // t0 - Spheres
        ranges[3].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 1);  // t1 - Planes
        ranges[4].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 2);  // t2 - Boxes
        ranges[5].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 3);  // t3 - Lights

        CD3DX12_ROOT_PARAMETER1 rootParameters[6];
        rootParameters[0].InitAsDescriptorTable(1, &ranges[0]);
        rootParameters[1].InitAsDescriptorTable(1, &ranges[1]);
        rootParameters[2].InitAsDescriptorTable(1, &ranges[2]);
        rootParameters[3].InitAsDescriptorTable(1, &ranges[3]);
        rootParameters[4].InitAsDescriptorTable(1, &ranges[4]);
        rootParameters[5].InitAsDescriptorTable(1, &ranges[5]);

        CD3DX12_VERSIONED_ROOT_SIGNATURE_DESC rootSignatureDesc;
        rootSignatureDesc.Init_1_1(_countof(rootParameters), rootParameters, 0, nullptr,
            D3D12_ROOT_SIGNATURE_FLAG_NONE);

        ComPtr<ID3DBlob> signature;
        ComPtr<ID3DBlob> error;
        hr = D3DX12SerializeVersionedRootSignature(&rootSignatureDesc, D3D_ROOT_SIGNATURE_VERSION_1_1,
            &signature, &error);
        
        if (FAILED(hr))
        {
            LogToFile("Failed to serialize root signature", hr);
            if (error)
            {
                LogToFile((char*)error->GetBufferPointer());
            }
            return false;
        }

        hr = device->CreateRootSignature(0, signature->GetBufferPointer(), signature->GetBufferSize(),
            IID_PPV_ARGS(&computeRootSignature));
        
        if (FAILED(hr))
        {
            LogToFile("Failed to create root signature", hr);
            return false;
        }

        LogToFile("Creating compute pipeline state...");
        
        // Create compute pipeline state
        D3D12_COMPUTE_PIPELINE_STATE_DESC psoDesc = {};
        psoDesc.pRootSignature = computeRootSignature.Get();
        psoDesc.CS = { computeShader->GetBufferPointer(), computeShader->GetBufferSize() };

        hr = device->CreateComputePipelineState(&psoDesc, IID_PPV_ARGS(&computePipelineState));
        
        if (FAILED(hr))
        {
            LogToFile("Failed to create compute pipeline state", hr);
            return false;
        }

        LogToFile("Creating descriptor heap...");
        
        // Create descriptor heap for SRV/UAV/CBV
        D3D12_DESCRIPTOR_HEAP_DESC heapDesc = {};
        heapDesc.NumDescriptors = 6;  // CBV + UAV + 4 SRVs
        heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        heapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;

        hr = device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&computeSrvUavHeap));
        
        if (FAILED(hr))
        {
            LogToFile("Failed to create descriptor heap", hr);
            return false;
        }

        srvUavDescriptorSize = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);

        LogToFile("Creating constant buffer...");
        
        // Create constant buffer
        CD3DX12_HEAP_PROPERTIES uploadHeapProps(D3D12_HEAP_TYPE_UPLOAD);
        CD3DX12_RESOURCE_DESC cbDesc = CD3DX12_RESOURCE_DESC::Buffer(sizeof(SceneConstants));

        hr = device->CreateCommittedResource(
            &uploadHeapProps,
            D3D12_HEAP_FLAG_NONE,
            &cbDesc,
            D3D12_RESOURCE_STATE_GENERIC_READ,
            nullptr,
            IID_PPV_ARGS(&constantBuffer));

        if (FAILED(hr))
        {
            LogToFile("Failed to create constant buffer", hr);
            return false;
        }

        // Map constant buffer
        hr = constantBuffer->Map(0, nullptr, reinterpret_cast<void**>(&mappedConstantData));
        if (FAILED(hr))
        {
            LogToFile("Failed to map constant buffer", hr);
            return false;
        }

        LogToFile("Compute pipeline created successfully!");
        return true;
    }

    bool DXRPipeline::CreateBuffers(UINT width, UINT height)
    {
        auto device = dxContext->GetDevice();
        if (!device)
            return false;

        const UINT maxSpheres = 32;
        const UINT maxPlanes = 32;
        const UINT maxBoxes = 32;
        const UINT maxLights = 8;

        CD3DX12_HEAP_PROPERTIES defaultHeapProps(D3D12_HEAP_TYPE_DEFAULT);
        CD3DX12_HEAP_PROPERTIES uploadHeapProps(D3D12_HEAP_TYPE_UPLOAD);

        // Create sphere buffer
        UINT64 sphereBufferSize = sizeof(GPUSphere) * maxSpheres;
        CD3DX12_RESOURCE_DESC sphereDesc = CD3DX12_RESOURCE_DESC::Buffer(sphereBufferSize);
        
        device->CreateCommittedResource(&defaultHeapProps, D3D12_HEAP_FLAG_NONE, &sphereDesc,
            D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&sphereBuffer));
        device->CreateCommittedResource(&uploadHeapProps, D3D12_HEAP_FLAG_NONE, &sphereDesc,
            D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&sphereUploadBuffer));

        // Create plane buffer
        UINT64 planeBufferSize = sizeof(GPUPlane) * maxPlanes;
        CD3DX12_RESOURCE_DESC planeDesc = CD3DX12_RESOURCE_DESC::Buffer(planeBufferSize);
        
        device->CreateCommittedResource(&defaultHeapProps, D3D12_HEAP_FLAG_NONE, &planeDesc,
            D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&planeBuffer));
        device->CreateCommittedResource(&uploadHeapProps, D3D12_HEAP_FLAG_NONE, &planeDesc,
            D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&planeUploadBuffer));

        // Create box buffer
        UINT64 boxBufferSize = sizeof(GPUBox) * maxBoxes;
        CD3DX12_RESOURCE_DESC boxDesc = CD3DX12_RESOURCE_DESC::Buffer(boxBufferSize);
        
        device->CreateCommittedResource(&defaultHeapProps, D3D12_HEAP_FLAG_NONE, &boxDesc,
            D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&boxBuffer));
        device->CreateCommittedResource(&uploadHeapProps, D3D12_HEAP_FLAG_NONE, &boxDesc,
            D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&boxUploadBuffer));

        // Create light buffer
        UINT64 lightBufferSize = sizeof(GPULight) * maxLights;
        CD3DX12_RESOURCE_DESC lightDesc = CD3DX12_RESOURCE_DESC::Buffer(lightBufferSize);
        
        device->CreateCommittedResource(&defaultHeapProps, D3D12_HEAP_FLAG_NONE, &lightDesc,
            D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&lightBuffer));
        device->CreateCommittedResource(&uploadHeapProps, D3D12_HEAP_FLAG_NONE, &lightDesc,
            D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&lightUploadBuffer));

        return true;
    }

    void DXRPipeline::UpdateSceneData(Scene* scene, UINT width, UINT height)
    {
        if (!scene || !mappedConstantData)
            return;

        // DEBUG: Log struct sizes to verify alignment
        static bool loggedOnce = false;
        if (!loggedOnce) {
            char buf[256];
            sprintf_s(buf, "STRUCT SIZES: GPUSphere=%zu, GPUPlane=%zu, GPUBox=%zu", 
                sizeof(GPUSphere), sizeof(GPUPlane), sizeof(GPUBox));
            LogToFile(buf);
            loggedOnce = true;
        }

        auto device = dxContext->GetDevice();
        auto commandList = dxContext->GetCommandList();

        // Update constant buffer
        const Camera& camera = scene->GetCamera();
        XMFLOAT3 camPos = camera.GetPosition();
        XMFLOAT3 camLookAt = camera.GetLookAt();
        XMFLOAT3 camUp = camera.GetUp();
        
        // Calculate camera basis vectors (right-handed coordinate system for ray tracing)
        XMVECTOR pos = XMLoadFloat3(&camPos);
        XMVECTOR lookAt = XMLoadFloat3(&camLookAt);
        XMVECTOR up = XMLoadFloat3(&camUp);
        
        XMVECTOR forward = XMVector3Normalize(XMVectorSubtract(lookAt, pos));
        // cross(up, forward) gives RIGHT direction in right-handed system
        XMVECTOR right = XMVector3Normalize(XMVector3Cross(up, forward));
        XMVECTOR realUp = XMVector3Normalize(XMVector3Cross(forward, right));
        
        XMFLOAT3 forwardF, rightF, upF;
        XMStoreFloat3(&forwardF, forward);
        XMStoreFloat3(&rightF, right);
        XMStoreFloat3(&upF, realUp);
        
        mappedConstantData->CameraPosition = camPos;
        mappedConstantData->CameraPadding1 = 0;
        mappedConstantData->CameraForward = forwardF;
        mappedConstantData->CameraPadding2 = 0;
        mappedConstantData->CameraRight = rightF;
        mappedConstantData->CameraPadding3 = 0;
        mappedConstantData->CameraUp = upF;
        mappedConstantData->CameraPadding4 = 0;
        
        // Default light
        mappedConstantData->LightPosition = XMFLOAT3(3.0f, 5.0f, -3.0f);
        mappedConstantData->LightIntensity = 1.5f;
        mappedConstantData->LightColor = XMFLOAT4(1.0f, 1.0f, 1.0f, 1.0f);
        
        mappedConstantData->ScreenWidth = width;
        mappedConstantData->ScreenHeight = height;
        mappedConstantData->AspectRatio = (float)width / (float)height;
        mappedConstantData->TanHalfFov = tanf(camera.GetFieldOfView() * 0.5f * 3.14159265f / 180.0f);
        mappedConstantData->SamplesPerPixel = scene->GetSamplesPerPixel();
        mappedConstantData->MaxBounces = scene->GetMaxBounces();
        
        // DoF parameters
        mappedConstantData->ApertureSize = camera.GetApertureSize();
        mappedConstantData->FocusDistance = camera.GetFocusDistance();
        
        // Shadow parameters and frame counter for temporal variation
        mappedConstantData->ShadowStrength = shadowStrength;
        static UINT s_frameCounter = 0;
        mappedConstantData->FrameIndex = s_frameCounter++;  // Increment each render call

        // View/Projection matrices for motion vectors (column-major for HLSL)
        XMMATRIX viewMatrix = camera.GetViewMatrix();
        float aspect = (float)width / (float)height;
        XMMATRIX projMatrix = camera.GetProjectionMatrix(aspect);
        XMMATRIX prevView = XMLoadFloat4x4(&prevViewMatrix);
        XMMATRIX prevProj = XMLoadFloat4x4(&prevProjMatrix);
        XMMATRIX viewProj = XMMatrixMultiply(viewMatrix, projMatrix);
        XMMATRIX prevViewProj = XMMatrixMultiply(prevView, prevProj);
        XMStoreFloat4x4(&mappedConstantData->ViewProjection, XMMatrixTranspose(viewProj));
        XMStoreFloat4x4(&mappedConstantData->PrevViewProjection, XMMatrixTranspose(prevViewProj));

        // Get objects from scene
        const auto& objects = scene->GetObjects();
        const auto& lights = scene->GetLights();

        std::vector<GPUSphere> spheres;
        std::vector<GPUPlane> planes;
        std::vector<GPUBox> Boxes;
        std::vector<GPULight> gpuLights;

        for (const auto& obj : objects)
        {
            if (auto sphere = dynamic_cast<Sphere*>(obj.get()))
            {
                GPUSphere gs;
                gs.Center = sphere->GetCenter();
                gs.Radius = sphere->GetRadius();
                const Material& mat = sphere->GetMaterial();
                gs.Color = mat.color;
                gs.Metallic = mat.metallic;
                gs.Roughness = mat.roughness;
                gs.Transmission = mat.transmission;
                gs.IOR = mat.ior;
                gs.Specular = mat.specular;
                gs.Padding1 = 0;
                gs.Padding2 = 0;
                gs.Padding3 = 0;
                spheres.push_back(gs);
            }
            else if (auto plane = dynamic_cast<Plane*>(obj.get()))
            {
                GPUPlane gp;
                gp.Position = plane->GetPosition();
                gp.Normal = plane->GetNormal();
                const Material& mat = plane->GetMaterial();
                gp.Color = mat.color;
                gp.Metallic = mat.metallic;
                gp.Roughness = mat.roughness;
                gp.Transmission = mat.transmission;
                gp.IOR = mat.ior;
                gp.Specular = mat.specular;
                gp.Padding1 = 0;
                planes.push_back(gp);
            }
            else if (auto box = dynamic_cast<Box*>(obj.get()))
            {
                GPUBox gb;
                gb.Center = box->GetCenter();
                gb.Padding1 = 0;
                gb.Size = box->GetSize();
                gb.Padding2 = 0;
                const Material& mat = box->GetMaterial();
                gb.Color = mat.color;
                gb.Metallic = mat.metallic;
                gb.Roughness = mat.roughness;
                gb.Transmission = mat.transmission;
                gb.IOR = mat.ior;
                gb.Specular = mat.specular;
                gb.Padding3 = 0;
                gb.Padding4 = 0;
                gb.Padding5 = 0;
                Boxes.push_back(gb);
            }
        }

        for (const auto& light : lights)
        {
            GPULight gl;
            gl.Position = light.GetPosition();
            gl.Intensity = light.GetIntensity();
            gl.Color = light.GetColor();
            
            // Convert light type
            switch (light.GetType())
            {
                case LightType::Directional:
                    gl.Type = GPULightType_Directional;
                    break;
                case LightType::Point:
                    gl.Type = GPULightType_Point;
                    break;
                default:
                    gl.Type = GPULightType_Ambient;
                    break;
            }
            gl.Radius = light.GetRadius();
            gl.SoftShadowSamples = light.GetSoftShadowSamples();
            gl.Padding = 0.0f;
            gpuLights.push_back(gl);

            // Update main light from first non-ambient light
            if (gl.Type == GPULightType_Point && mappedConstantData->LightIntensity == 1.5f)
            {
                mappedConstantData->LightPosition = gl.Position;
                mappedConstantData->LightIntensity = gl.Intensity;
                mappedConstantData->LightColor = gl.Color;
            }
        }

        mappedConstantData->NumSpheres = (UINT)spheres.size();
        mappedConstantData->NumPlanes = (UINT)planes.size();
        mappedConstantData->NumBoxes = (UINT)Boxes.size();
        mappedConstantData->NumLights = (UINT)gpuLights.size();

        // Store UI parameters for later passes
        exposure = (float)scene->GetExposure();
        toneMapOperator = scene->GetToneMapOperator();
        denoiserStabilization = (float)scene->GetDenoiserStabilization();
        shadowStrength = (float)scene->GetShadowStrength();
        denoiserEnabled = scene->GetEnableDenoiser();
        
        char logBuf[256];
        sprintf_s(logBuf, "DXRPipeline: shadowStrength = %.2f, denoiserEnabled = %d", shadowStrength, denoiserEnabled ? 1 : 0);
        LogToFile(logBuf);


        // Upload object data to GPU buffers
        if (!spheres.empty() && sphereUploadBuffer)
        {
            void* mapped = nullptr;
            sphereUploadBuffer->Map(0, nullptr, &mapped);
            memcpy(mapped, spheres.data(), sizeof(GPUSphere) * spheres.size());
            sphereUploadBuffer->Unmap(0, nullptr);

            commandList->CopyResource(sphereBuffer.Get(), sphereUploadBuffer.Get());
        }

        if (!planes.empty() && planeUploadBuffer)
        {
            void* mapped = nullptr;
            planeUploadBuffer->Map(0, nullptr, &mapped);
            memcpy(mapped, planes.data(), sizeof(GPUPlane) * planes.size());
            planeUploadBuffer->Unmap(0, nullptr);

            commandList->CopyResource(planeBuffer.Get(), planeUploadBuffer.Get());
        }

        if (!Boxes.empty() && boxUploadBuffer)
        {
            void* mapped = nullptr;
            boxUploadBuffer->Map(0, nullptr, &mapped);
            memcpy(mapped, Boxes.data(), sizeof(GPUBox) * Boxes.size());
            boxUploadBuffer->Unmap(0, nullptr);

            commandList->CopyResource(boxBuffer.Get(), boxUploadBuffer.Get());
        }

        if (!gpuLights.empty() && lightUploadBuffer)
        {
            void* mapped = nullptr;
            lightUploadBuffer->Map(0, nullptr, &mapped);
            memcpy(mapped, gpuLights.data(), sizeof(GPULight) * gpuLights.size());
            lightUploadBuffer->Unmap(0, nullptr);

            commandList->CopyResource(lightBuffer.Get(), lightUploadBuffer.Get());
        }
    }

    void DXRPipeline::RenderWithComputeShader(RenderTarget* renderTarget, Scene* scene)
    {
        LogToFile("RenderWithComputeShader called");
        
        if (!renderTarget || !scene)
        {
            LogToFile("renderTarget or scene is null");
            return;
        }

        // If compute pipeline is not initialized, render error pattern
        if (!computePipelineState || !computeRootSignature)
        {
            LogToFile("Compute pipeline not initialized, rendering error pattern");
            RenderErrorPattern(renderTarget);
            return;
        }
        
        LogToFile("Compute pipeline OK, proceeding with GPU render");

        auto device = dxContext->GetDevice();
        auto commandList = dxContext->GetCommandList();

        UINT width = renderTarget->GetWidth();
        UINT height = renderTarget->GetHeight();

        // Create buffers if not created
        if (!sphereBuffer)
        {
            if (!CreateBuffers(width, height))
                return;
        }

        // Update scene data
        UpdateSceneData(scene, width, height);

        // Create descriptors
        CD3DX12_CPU_DESCRIPTOR_HANDLE cpuHandle(computeSrvUavHeap->GetCPUDescriptorHandleForHeapStart());

        // CBV for constant buffer
        D3D12_CONSTANT_BUFFER_VIEW_DESC cbvDesc = {};
        cbvDesc.BufferLocation = constantBuffer->GetGPUVirtualAddress();
        cbvDesc.SizeInBytes = sizeof(SceneConstants);
        device->CreateConstantBufferView(&cbvDesc, cpuHandle);
        cpuHandle.Offset(1, srvUavDescriptorSize);

        // UAV for output texture
        D3D12_UNORDERED_ACCESS_VIEW_DESC uavDesc = {};
        uavDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        uavDesc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        uavDesc.Texture2D.MipSlice = 0;
        device->CreateUnorderedAccessView(renderTarget->GetResource(), nullptr, &uavDesc, cpuHandle);
        cpuHandle.Offset(1, srvUavDescriptorSize);

        // SRV for spheres
        D3D12_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
        srvDesc.Format = DXGI_FORMAT_UNKNOWN;
        srvDesc.ViewDimension = D3D12_SRV_DIMENSION_BUFFER;
        srvDesc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        srvDesc.Buffer.FirstElement = 0;
        srvDesc.Buffer.NumElements = 32;
        srvDesc.Buffer.StructureByteStride = sizeof(GPUSphere);
        device->CreateShaderResourceView(sphereBuffer.Get(), &srvDesc, cpuHandle);
        cpuHandle.Offset(1, srvUavDescriptorSize);

        // SRV for planes
        srvDesc.Buffer.NumElements = 32;
        srvDesc.Buffer.StructureByteStride = sizeof(GPUPlane);
        device->CreateShaderResourceView(planeBuffer.Get(), &srvDesc, cpuHandle);
        cpuHandle.Offset(1, srvUavDescriptorSize);

        // SRV for Boxes
        srvDesc.Buffer.NumElements = 32;
        srvDesc.Buffer.StructureByteStride = sizeof(GPUBox);
        device->CreateShaderResourceView(boxBuffer.Get(), &srvDesc, cpuHandle);
        cpuHandle.Offset(1, srvUavDescriptorSize);

        // SRV for lights
        srvDesc.Buffer.NumElements = 8;
        srvDesc.Buffer.StructureByteStride = sizeof(GPULight);
        device->CreateShaderResourceView(lightBuffer.Get(), &srvDesc, cpuHandle);

        // Set pipeline state
        commandList->SetPipelineState(computePipelineState.Get());
        commandList->SetComputeRootSignature(computeRootSignature.Get());

        // Set descriptor heap
        ID3D12DescriptorHeap* heaps[] = { computeSrvUavHeap.Get() };
        commandList->SetDescriptorHeaps(1, heaps);

        // Set root descriptor tables
        CD3DX12_GPU_DESCRIPTOR_HANDLE gpuHandle(computeSrvUavHeap->GetGPUDescriptorHandleForHeapStart());
        commandList->SetComputeRootDescriptorTable(0, gpuHandle);  // CBV
        gpuHandle.Offset(1, srvUavDescriptorSize);
        commandList->SetComputeRootDescriptorTable(1, gpuHandle);  // UAV
        gpuHandle.Offset(1, srvUavDescriptorSize);
        commandList->SetComputeRootDescriptorTable(2, gpuHandle);  // Spheres
        gpuHandle.Offset(1, srvUavDescriptorSize);
        commandList->SetComputeRootDescriptorTable(3, gpuHandle);  // Planes
        gpuHandle.Offset(1, srvUavDescriptorSize);
        commandList->SetComputeRootDescriptorTable(4, gpuHandle);  // Boxes
        gpuHandle.Offset(1, srvUavDescriptorSize);
        commandList->SetComputeRootDescriptorTable(5, gpuHandle);  // Lights

        // Dispatch compute shader
        // Thread group size is 8x8, so dispatch (width/8) x (height/8) groups
        UINT dispatchX = (width + 7) / 8;
        UINT dispatchY = (height + 7) / 8;
        commandList->Dispatch(dispatchX, dispatchY, 1);
    }

    void DXRPipeline::RenderErrorPattern(RenderTarget* renderTarget)
    {
        LogToFile("RenderErrorPattern called");
        
        // Render a simple error pattern when compute shader is not available
        auto device = dxContext->GetDevice();
        auto commandList = dxContext->GetCommandList();
        
        if (!device || !commandList)
        {
            LogToFile("device or commandList is null in RenderErrorPattern");
            return;
        }

        UINT width = renderTarget->GetWidth();
        UINT height = renderTarget->GetHeight();
        
        // Calculate rowPitch with 256-byte alignment
        UINT rowPitch = (width * 4 + 255) & ~255;
        UINT totalSize = rowPitch * height;
        
        // Create pattern data (gradient + error message indication)
        std::vector<unsigned char> patternData(totalSize, 0);
        
        for (UINT y = 0; y < height; ++y)
        {
            for (UINT x = 0; x < width; ++x)
            {
                UINT index = y * rowPitch + x * 4;
                
                // Create a gradient pattern with magenta tint (indicating error)
                float fx = (float)x / (float)width;
                float fy = (float)y / (float)height;
                
                patternData[index + 0] = static_cast<unsigned char>(fx * 200 + 55);  // R
                patternData[index + 1] = static_cast<unsigned char>(fy * 100);       // G
                patternData[index + 2] = static_cast<unsigned char>(fx * 200 + 55);  // B (same as R for magenta)
                patternData[index + 3] = 255;  // A
            }
        }
        
        // Create upload buffer
        ComPtr<ID3D12Resource> uploadBuffer;
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

    // ============================================
    // DXR Pipeline Implementation
    // ============================================

    bool DXRPipeline::CreateDXRPipeline()
    {
        LogToFile("CreateDXRPipeline started");
        
        if (!CreateGlobalRootSignature())
        {
            LogToFile("Failed to create global root signature");
            return false;
        }
        
        if (!CreateDXRStateObject())
        {
            LogToFile("Failed to create DXR state object");
            return false;
        }
        
        if (!CreateDXRDescriptorHeap())
        {
            LogToFile("Failed to create DXR descriptor heap");
            return false;
        }
        
        if (!CreateDXRShaderTables())
        {
            LogToFile("Failed to create DXR shader tables");
            return false;
        }
        
        // Create acceleration structure object
        accelerationStructure = std::make_unique<AccelerationStructure>(dxContext);
        
        // Initialize photon mapping for caustics
        if (CreatePhotonMappingResources())
        {
            if (CreatePhotonStateObject())
            {
                CreatePhotonShaderTables();
                
                // Initialize spatial hash resources for O(1) photon lookup
                if (CreatePhotonHashResources())
                {
                    LogToFile("Photon mapping with spatial hash initialized successfully");
                }
                else
                {
                    LogToFile("Photon mapping initialized (without spatial hash - will use brute force)");
                }
            }
        }
        
        LogToFile("CreateDXRPipeline completed successfully");
        return true;
    }

    bool DXRPipeline::CreateGlobalRootSignature()
    {
        auto device = dxContext->GetDevice();
        
        // Global root signature layout:
        // [0] UAV - Output texture (u0)
        // [1] SRV - Acceleration structure (t0)
        // [2] CBV - Scene constants (b0)
        // [3] SRV - Spheres buffer (t1)
        // [4] SRV - Planes buffer (t2)
        // [5] SRV - Boxes buffer (t3)
        // [6] SRV - Lights buffer (t4)
        // [7] UAV - Photon map buffer (u1)
        // [8] UAV - Photon counter (u2)
        // [9-16] UAV - G-Buffer for NRD (u3-u10)
        // [17] UAV - Photon hash table (u11)
        
        CD3DX12_DESCRIPTOR_RANGE1 ranges[18];
        ranges[0].Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 0);  // u0 - Output
        ranges[1].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 0);  // t0 - TLAS
        ranges[2].Init(D3D12_DESCRIPTOR_RANGE_TYPE_CBV, 1, 0);  // b0 - Constants
        ranges[3].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 1);  // t1 - Spheres
        ranges[4].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 2);  // t2 - Planes
        ranges[5].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 3);  // t3 - Boxes
        ranges[6].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 4);  // t4 - Lights
        ranges[7].Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 1);  // u1 - Photon map
        ranges[8].Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 2);  // u2 - Photon counter
        // G-Buffer UAVs for NRD denoiser
        ranges[9].Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 3);   // u3 - DiffuseRadianceHitDist
        ranges[10].Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 4);  // u4 - SpecularRadianceHitDist
        ranges[11].Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 5);  // u5 - NormalRoughness
        ranges[12].Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 8);  // u8 - Albedo (moved to index 12 for DXR compatibility)
        ranges[13].Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 7);  // u7 - MotionVectors
        ranges[14].Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 6);  // u6 - ViewZ
        ranges[15].Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 9);  // u9 - ShadowData
        ranges[16].Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 10); // u10 - ShadowTranslucency
        ranges[17].Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 11); // u11 - PhotonHashTable

        CD3DX12_ROOT_PARAMETER1 rootParameters[18];
        for (int i = 0; i < 18; i++)
        {
            rootParameters[i].InitAsDescriptorTable(1, &ranges[i]);
        }

        CD3DX12_VERSIONED_ROOT_SIGNATURE_DESC rootSignatureDesc;
        rootSignatureDesc.Init_1_1(_countof(rootParameters), rootParameters, 0, nullptr,
            D3D12_ROOT_SIGNATURE_FLAG_NONE);

        ComPtr<ID3DBlob> signature;
        ComPtr<ID3DBlob> error;
        HRESULT hr = D3DX12SerializeVersionedRootSignature(&rootSignatureDesc, D3D_ROOT_SIGNATURE_VERSION_1_1,
            &signature, &error);
        
        if (FAILED(hr))
        {
            if (error)
                LogToFile((char*)error->GetBufferPointer());
            return false;
        }

        hr = device->CreateRootSignature(0, signature->GetBufferPointer(), signature->GetBufferSize(),
            IID_PPV_ARGS(&globalRootSignature));
        
        return SUCCEEDED(hr);
    }

    bool DXRPipeline::CreateLocalRootSignature()
    {
        // Empty local root signature for now
        auto device = dxContext->GetDevice();
        
        CD3DX12_VERSIONED_ROOT_SIGNATURE_DESC rootSignatureDesc;
        rootSignatureDesc.Init_1_1(0, nullptr, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_LOCAL_ROOT_SIGNATURE);

        ComPtr<ID3DBlob> signature;
        ComPtr<ID3DBlob> error;
        HRESULT hr = D3DX12SerializeVersionedRootSignature(&rootSignatureDesc, D3D_ROOT_SIGNATURE_VERSION_1_1,
            &signature, &error);
        
        if (FAILED(hr))
            return false;

        hr = device->CreateRootSignature(0, signature->GetBufferPointer(), signature->GetBufferSize(),
            IID_PPV_ARGS(&localRootSignature));
        
        return SUCCEEDED(hr);
    }

    bool DXRPipeline::LoadPrecompiledShader(const std::wstring& filename, ID3DBlob** shader)
    {
        // Open the precompiled shader file (.cso)
        std::ifstream file(filename, std::ios::binary | std::ios::ate);
        if (!file.is_open())
        {
            // Convert wstring to string for logging
            std::string filenameStr(filename.begin(), filename.end());
            LogToFile(("Failed to open precompiled shader: " + filenameStr).c_str());
            return false;
        }
        
        // Get file size
        std::streamsize size = file.tellg();
        file.seekg(0, std::ios::beg);
        
        if (size <= 0)
        {
            LogToFile("Precompiled shader file is empty");
            return false;
        }
        
        // Create blob
        HRESULT hr = D3DCreateBlob(static_cast<SIZE_T>(size), shader);
        if (FAILED(hr))
        {
            LogToFile("Failed to create blob for shader", hr);
            return false;
        }
        
        // Read file contents into blob
        if (!file.read(static_cast<char*>((*shader)->GetBufferPointer()), size))
        {
            LogToFile("Failed to read precompiled shader file");
            (*shader)->Release();
            *shader = nullptr;
            return false;
        }
        
        return true;
    }
    
    bool DXRPipeline::CompileShaderFromFile(const std::wstring& filename, const char* entryPoint, const char* target, ID3DBlob** shader)
    {
        // This function is kept for backward compatibility but not used
        // DXR shaders are now precompiled at build time
        LogToFile("CompileShaderFromFile is deprecated - use precompiled shaders");
        return false;
    }
    
    std::wstring DXRPipeline::ResolveDXRShaderSourcePath(const std::wstring& shaderName) const
    {
        // Fixed shader source path: C:\git\RayTraceVS\src\Shader
        std::wstring sourcePath = shaderSourcePath + shaderName + L".hlsl";
        
        std::ifstream file(sourcePath, std::ios::binary);
        if (file.is_open())
        {
            file.close();
            LogToFile(("ResolveDXRShaderSourcePath: found " + std::string(shaderName.begin(), shaderName.end()) + " at " + std::string(sourcePath.begin(), sourcePath.end())).c_str());
            return sourcePath;
        }

        LogToFile(("ResolveDXRShaderSourcePath: " + std::string(shaderName.begin(), shaderName.end()) + " not found at " + std::string(sourcePath.begin(), sourcePath.end())).c_str());
        return L"";
    }
    
    // Compile DXR shader at runtime using DXC (lib_6_3)
    bool DXRPipeline::CompileDXRShaderFromSource(const std::wstring& shaderName, ID3DBlob** shader)
    {
        std::wstring sourcePath = ResolveDXRShaderSourcePath(shaderName);
        if (sourcePath.empty())
        {
            LogToFile("CompileDXRShaderFromSource: source file not found");
            return false;
        }

        ComPtr<IDxcUtils> utils;
        ComPtr<IDxcCompiler3> compiler;
        ComPtr<IDxcIncludeHandler> includeHandler;
        HRESULT hr = DxcCreateInstance(CLSID_DxcUtils, IID_PPV_ARGS(&utils));
        if (FAILED(hr))
        {
            LogToFile("CompileDXRShaderFromSource: failed to create IDxcUtils", hr);
            return false;
        }

        hr = DxcCreateInstance(CLSID_DxcCompiler, IID_PPV_ARGS(&compiler));
        if (FAILED(hr))
        {
            LogToFile("CompileDXRShaderFromSource: failed to create IDxcCompiler3", hr);
            return false;
        }

        hr = utils->CreateDefaultIncludeHandler(&includeHandler);
        if (FAILED(hr))
        {
            LogToFile("CompileDXRShaderFromSource: failed to create include handler", hr);
            return false;
        }

        UINT32 codePage = CP_UTF8;
        ComPtr<IDxcBlobEncoding> sourceBlob;
        hr = utils->LoadFile(sourcePath.c_str(), &codePage, &sourceBlob);
        if (FAILED(hr) || !sourceBlob)
        {
            LogToFile("CompileDXRShaderFromSource: failed to load shader source", hr);
            return false;
        }

        DxcBuffer sourceBuffer = {};
        sourceBuffer.Ptr = sourceBlob->GetBufferPointer();
        sourceBuffer.Size = sourceBlob->GetBufferSize();
        sourceBuffer.Encoding = DXC_CP_UTF8;

        std::wstring includeDir = sourcePath.substr(0, sourcePath.find_last_of(L"\\/"));
        std::vector<LPCWSTR> arguments;
        arguments.push_back(L"-T");
        arguments.push_back(L"lib_6_3");
        arguments.push_back(L"-Zpr");
        arguments.push_back(L"-Zi");
        arguments.push_back(L"-Qembed_debug");
        arguments.push_back(L"-I");
        arguments.push_back(includeDir.c_str());
        arguments.push_back(L"-D");
        arguments.push_back(L"ENABLE_NRD_GBUFFER=1");

        ComPtr<IDxcResult> result;
        hr = compiler->Compile(
            &sourceBuffer,
            arguments.data(),
            static_cast<UINT32>(arguments.size()),
            includeHandler.Get(),
            IID_PPV_ARGS(&result));
        if (FAILED(hr) || !result)
        {
            LogToFile("CompileDXRShaderFromSource: DXC compile failed to start", hr);
            return false;
        }

        HRESULT status = S_OK;
        result->GetStatus(&status);

        ComPtr<IDxcBlobUtf8> errors;
        result->GetOutput(DXC_OUT_ERRORS, IID_PPV_ARGS(&errors), nullptr);
        if (errors && errors->GetStringLength() > 0)
        {
            LogToFile(errors->GetStringPointer());
        }

        if (FAILED(status))
        {
            LogToFile("CompileDXRShaderFromSource: DXC compile failed");
            return false;
        }

        ComPtr<IDxcBlob> dxilBlob;
        hr = result->GetOutput(DXC_OUT_OBJECT, IID_PPV_ARGS(&dxilBlob), nullptr);
        if (FAILED(hr) || !dxilBlob)
        {
            LogToFile("CompileDXRShaderFromSource: failed to get DXIL output", hr);
            return false;
        }

        hr = D3DCreateBlob(static_cast<SIZE_T>(dxilBlob->GetBufferSize()), shader);
        if (FAILED(hr))
        {
            LogToFile("CompileDXRShaderFromSource: failed to create blob for DXIL", hr);
            return false;
        }

        memcpy((*shader)->GetBufferPointer(), dxilBlob->GetBufferPointer(), dxilBlob->GetBufferSize());
        return true;
    }
    
    // Load DXR shader via ShaderCache (handles caching and recompilation)
    bool DXRPipeline::LoadOrCompileDXRShader(const std::wstring& shaderName, ID3DBlob** shader)
    {
        if (shaderCache)
        {
            return shaderCache->GetShader(shaderName, shader);
        }
        
        // Fallback to old method if cache not initialized
        if (CompileDXRShaderFromSource(shaderName, shader))
        {
            LogToFile(("Compiled DXR shader from source: " + std::string(shaderName.begin(), shaderName.end())).c_str());
            return true;
        }

        LogToFile(("Falling back to precompiled shader: " + std::string(shaderName.begin(), shaderName.end())).c_str());
        return LoadPrecompiledShader(GetShaderPath(shaderName + L".cso"), shader);
    }

    bool DXRPipeline::CreateDXRStateObject()
    {
        auto device = dxContext->GetDevice();
        
        LogToFile(("Loading DXR shaders from: " + std::string(shaderBasePath.begin(), shaderBasePath.end())).c_str());
        
        // Compile DXR shaders from source at runtime (ensures latest changes are used)
        ComPtr<ID3DBlob> anyHitShadowShader;
        if (!LoadOrCompileDXRShader(L"RayGen", &rayGenShader) ||
            !LoadOrCompileDXRShader(L"Miss", &missShader) ||
            !LoadOrCompileDXRShader(L"ClosestHit", &closestHitShader) ||
            !LoadOrCompileDXRShader(L"Intersection", &intersectionShader) ||
            !LoadOrCompileDXRShader(L"AnyHit_Shadow", &anyHitShadowShader))
        {
            LogToFile("Failed to load/compile DXR shaders");
            return false;
        }
        LogToFile("Successfully loaded DXR shaders");
        
        // Build state object using CD3DX12_STATE_OBJECT_DESC
        CD3DX12_STATE_OBJECT_DESC stateObjectDesc(D3D12_STATE_OBJECT_TYPE_RAYTRACING_PIPELINE);
        
        // Add DXIL libraries
        auto AddLibrary = [&](ID3DBlob* shader, const wchar_t* exportName) {
            auto lib = stateObjectDesc.CreateSubobject<CD3DX12_DXIL_LIBRARY_SUBOBJECT>();
            D3D12_SHADER_BYTECODE bc = { shader->GetBufferPointer(), shader->GetBufferSize() };
            lib->SetDXILLibrary(&bc);
            lib->DefineExport(exportName);
        };
        
        AddLibrary(rayGenShader.Get(), L"RayGen");
        AddLibrary(missShader.Get(), L"Miss");
        AddLibrary(closestHitShader.Get(), L"ClosestHit");
        AddLibrary(intersectionShader.Get(), L"SphereIntersection");
        AddLibrary(anyHitShadowShader.Get(), L"AnyHit_Shadow");
        
        // Hit group 0: Primary rays (ClosestHit + Intersection)
        auto hitGroup = stateObjectDesc.CreateSubobject<CD3DX12_HIT_GROUP_SUBOBJECT>();
        hitGroup->SetHitGroupExport(L"HitGroup");
        hitGroup->SetClosestHitShaderImport(L"ClosestHit");
        hitGroup->SetIntersectionShaderImport(L"SphereIntersection");
        hitGroup->SetHitGroupType(D3D12_HIT_GROUP_TYPE_PROCEDURAL_PRIMITIVE);
        
        // Hit group 1: Shadow rays (AnyHit_Shadow + Intersection)
        auto shadowHitGroup = stateObjectDesc.CreateSubobject<CD3DX12_HIT_GROUP_SUBOBJECT>();
        shadowHitGroup->SetHitGroupExport(L"ShadowHitGroup");
        shadowHitGroup->SetAnyHitShaderImport(L"AnyHit_Shadow");
        shadowHitGroup->SetIntersectionShaderImport(L"SphereIntersection");
        shadowHitGroup->SetHitGroupType(D3D12_HIT_GROUP_TYPE_PROCEDURAL_PRIMITIVE);
        
        // Shader config
        auto shaderConfig = stateObjectDesc.CreateSubobject<CD3DX12_RAYTRACING_SHADER_CONFIG_SUBOBJECT>();
        // RayPayload (with NRD + SIGMA fields):
        //   Basic: float3 color (12) + uint depth (4) + uint hit (4) + float padding (4) = 24 bytes
        //   NRD: float3 diffuseRadiance (12) + float hitDistance (4) = 16 bytes
        //   NRD: float3 specularRadiance (12) + float roughness (4) = 16 bytes
        //   NRD: float3 worldNormal (12) + float viewZ (4) = 16 bytes
        //   NRD: float3 worldPosition (12) + float metallic (4) = 16 bytes
        //   NRD: float3 albedo (12) + float shadowVisibility (4) = 16 bytes
        //   SIGMA: float shadowPenumbra (4) + float shadowDistance (4) + float padding2 (4) = 12 bytes
        //   Thickness query: 3 * uint (12) + padding (4) = 16 bytes
        //   Total: 132 bytes (align to 8 -> 136)
        UINT payloadSize = 136;
        // ProceduralAttributes: float3 normal (12) + uint objectType (4) + uint objectIndex (4) = 20 bytes
        UINT attribSize = 12 + 4 + 4;   // 20 bytes
        shaderConfig->Config(payloadSize, attribSize);
        
        // Global root signature
        auto globalRS = stateObjectDesc.CreateSubobject<CD3DX12_GLOBAL_ROOT_SIGNATURE_SUBOBJECT>();
        globalRS->SetRootSignature(globalRootSignature.Get());
        
        // Pipeline config
        auto pipelineConfig = stateObjectDesc.CreateSubobject<CD3DX12_RAYTRACING_PIPELINE_CONFIG_SUBOBJECT>();
        pipelineConfig->Config(8);  // Max recursion depth (reduced for compatibility)
        
        // Create state object
        HRESULT hr = device->CreateStateObject(stateObjectDesc, IID_PPV_ARGS(&stateObject));
        if (FAILED(hr))
        {
            LogToFile("Failed to create state object", hr);
            return false;
        }
        
        // Get state object properties
        hr = stateObject->QueryInterface(IID_PPV_ARGS(&stateObjectProperties));
        if (FAILED(hr))
        {
            LogToFile("Failed to get state object properties", hr);
            return false;
        }
        
        return true;
    }

    bool DXRPipeline::CreateDXRDescriptorHeap()
    {
        auto device = dxContext->GetDevice();
        
        // Need descriptors for:
        // [0] UAV output (u0)
        // [1] TLAS SRV (t0)
        // [2] CBV (b0)
        // [3-6] SRVs: spheres, planes, Boxes, lights (t1-t4)
        // [7-8] UAVs: photon map, photon counter (u1-u2)
        // [9-16] UAVs: G-Buffer for NRD (u3-u10)
        // [17] UAV: Photon hash table (u11)
        D3D12_DESCRIPTOR_HEAP_DESC heapDesc = {};
        heapDesc.NumDescriptors = 18;  // 9 + 8 for G-Buffer + 1 for photon hash
        heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        heapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;

        HRESULT hr = device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&dxrSrvUavHeap));
        if (FAILED(hr))
            return false;

        dxrDescriptorSize = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        
        return true;
    }

    bool DXRPipeline::CreateDXRShaderTables()
    {
        auto device = dxContext->GetDevice();
        
        // Get shader identifiers
        void* rayGenId = stateObjectProperties->GetShaderIdentifier(L"RayGen");
        void* missId = stateObjectProperties->GetShaderIdentifier(L"Miss");
        void* hitGroupId = stateObjectProperties->GetShaderIdentifier(L"HitGroup");
        void* shadowHitGroupId = stateObjectProperties->GetShaderIdentifier(L"ShadowHitGroup");
        
        if (!rayGenId || !missId || !hitGroupId || !shadowHitGroupId)
        {
            LogToFile("Failed to get shader identifiers");
            return false;
        }
        
        UINT shaderIdSize = D3D12_SHADER_IDENTIFIER_SIZE_IN_BYTES;
        shaderTableRecordSize = D3D12_RAYTRACING_SHADER_TABLE_BYTE_ALIGNMENT;  // Align to 32 bytes
        
        CD3DX12_HEAP_PROPERTIES uploadHeapProps(D3D12_HEAP_TYPE_UPLOAD);
        
        // Ray generation shader table
        {
            CD3DX12_RESOURCE_DESC desc = CD3DX12_RESOURCE_DESC::Buffer(shaderTableRecordSize);
            device->CreateCommittedResource(&uploadHeapProps, D3D12_HEAP_FLAG_NONE, &desc,
                D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&rayGenShaderTable));
            
            void* mapped = nullptr;
            rayGenShaderTable->Map(0, nullptr, &mapped);
            memcpy(mapped, rayGenId, shaderIdSize);
            rayGenShaderTable->Unmap(0, nullptr);
        }
        
        // Miss shader table
        {
            CD3DX12_RESOURCE_DESC desc = CD3DX12_RESOURCE_DESC::Buffer(shaderTableRecordSize);
            device->CreateCommittedResource(&uploadHeapProps, D3D12_HEAP_FLAG_NONE, &desc,
                D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&missShaderTable));
            
            void* mapped = nullptr;
            missShaderTable->Map(0, nullptr, &mapped);
            memcpy(mapped, missId, shaderIdSize);
            missShaderTable->Unmap(0, nullptr);
        }
        
        // Hit group shader table (2 entries: HitGroup at index 0, ShadowHitGroup at index 1)
        {
            UINT hitGroupTableSize = shaderTableRecordSize * 2;  // 2 hit groups
            CD3DX12_RESOURCE_DESC desc = CD3DX12_RESOURCE_DESC::Buffer(hitGroupTableSize);
            device->CreateCommittedResource(&uploadHeapProps, D3D12_HEAP_FLAG_NONE, &desc,
                D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&hitGroupShaderTable));
            
            void* mapped = nullptr;
            hitGroupShaderTable->Map(0, nullptr, &mapped);
            BYTE* dst = static_cast<BYTE*>(mapped);
            // Hit group 0: Primary rays
            memcpy(dst, hitGroupId, shaderIdSize);
            // Hit group 1: Shadow rays
            memcpy(dst + shaderTableRecordSize, shadowHitGroupId, shaderIdSize);
            hitGroupShaderTable->Unmap(0, nullptr);
        }
        
        return true;
    }

    bool DXRPipeline::BuildAccelerationStructures(Scene* scene)
    {
        if (!accelerationStructure)
            return false;
        
        // Build BLAS and TLAS
        if (!accelerationStructure->BuildProceduralBLAS(scene))
        {
            LogToFile("Failed to build procedural BLAS");
            return false;
        }
        
        if (!accelerationStructure->BuildProceduralTLAS())
        {
            LogToFile("Failed to build procedural TLAS");
            return false;
        }
        
        needsAccelerationStructureRebuild = false;
        lastScene = scene;
        
        return true;
    }

    void DXRPipeline::UpdateDXRDescriptors(RenderTarget* renderTarget)
    {
        auto device = dxContext->GetDevice();
        CD3DX12_CPU_DESCRIPTOR_HANDLE cpuHandle(dxrSrvUavHeap->GetCPUDescriptorHandleForHeapStart());
        
        // [0] UAV for output texture
        D3D12_UNORDERED_ACCESS_VIEW_DESC uavDesc = {};
        uavDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        uavDesc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        device->CreateUnorderedAccessView(renderTarget->GetResource(), nullptr, &uavDesc, cpuHandle);
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        // [1] SRV for TLAS
        D3D12_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
        srvDesc.Format = DXGI_FORMAT_UNKNOWN;
        srvDesc.ViewDimension = D3D12_SRV_DIMENSION_RAYTRACING_ACCELERATION_STRUCTURE;
        srvDesc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        srvDesc.RaytracingAccelerationStructure.Location = accelerationStructure->GetTLAS()->GetGPUVirtualAddress();
        device->CreateShaderResourceView(nullptr, &srvDesc, cpuHandle);
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        // [2] CBV for constants
        D3D12_CONSTANT_BUFFER_VIEW_DESC cbvDesc = {};
        cbvDesc.BufferLocation = constantBuffer->GetGPUVirtualAddress();
        cbvDesc.SizeInBytes = sizeof(SceneConstants);
        device->CreateConstantBufferView(&cbvDesc, cpuHandle);
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        // [3-6] SRVs for object buffers
        D3D12_SHADER_RESOURCE_VIEW_DESC bufferSrvDesc = {};
        bufferSrvDesc.Format = DXGI_FORMAT_UNKNOWN;
        bufferSrvDesc.ViewDimension = D3D12_SRV_DIMENSION_BUFFER;
        bufferSrvDesc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        bufferSrvDesc.Buffer.FirstElement = 0;
        
        // Spheres
        bufferSrvDesc.Buffer.NumElements = 32;
        bufferSrvDesc.Buffer.StructureByteStride = sizeof(GPUSphere);
        device->CreateShaderResourceView(sphereBuffer.Get(), &bufferSrvDesc, cpuHandle);
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        // Planes
        bufferSrvDesc.Buffer.StructureByteStride = sizeof(GPUPlane);
        device->CreateShaderResourceView(planeBuffer.Get(), &bufferSrvDesc, cpuHandle);
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        // Boxes
        bufferSrvDesc.Buffer.StructureByteStride = sizeof(GPUBox);
        device->CreateShaderResourceView(boxBuffer.Get(), &bufferSrvDesc, cpuHandle);
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        // Lights
        bufferSrvDesc.Buffer.NumElements = 8;
        bufferSrvDesc.Buffer.StructureByteStride = sizeof(GPULight);
        device->CreateShaderResourceView(lightBuffer.Get(), &bufferSrvDesc, cpuHandle);
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        // [7] UAV for photon map
        if (photonMapBuffer)
        {
            D3D12_UNORDERED_ACCESS_VIEW_DESC photonUavDesc = {};
            photonUavDesc.Format = DXGI_FORMAT_UNKNOWN;
            photonUavDesc.ViewDimension = D3D12_UAV_DIMENSION_BUFFER;
            photonUavDesc.Buffer.FirstElement = 0;
            photonUavDesc.Buffer.NumElements = maxPhotons;
            photonUavDesc.Buffer.StructureByteStride = sizeof(GPUPhoton);
            device->CreateUnorderedAccessView(photonMapBuffer.Get(), nullptr, &photonUavDesc, cpuHandle);
        }
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        // [8] UAV for photon counter
        if (photonCounterBuffer)
        {
            D3D12_UNORDERED_ACCESS_VIEW_DESC counterUavDesc = {};
            counterUavDesc.Format = DXGI_FORMAT_R32_UINT;
            counterUavDesc.ViewDimension = D3D12_UAV_DIMENSION_BUFFER;
            counterUavDesc.Buffer.FirstElement = 0;
            counterUavDesc.Buffer.NumElements = 1;
            device->CreateUnorderedAccessView(photonCounterBuffer.Get(), nullptr, &counterUavDesc, cpuHandle);
        }
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        // [9-16] UAVs for G-Buffer (NRD denoiser)
        if (denoiser && denoiser->IsReady())
        {
            auto& gBuffer = denoiser->GetGBuffer();
            D3D12_UNORDERED_ACCESS_VIEW_DESC gbufferUavDesc = {};
            gbufferUavDesc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
            gbufferUavDesc.Texture2D.MipSlice = 0;
            
            // [9] u3: DiffuseRadianceHitDist (RGBA16F)
            gbufferUavDesc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
            device->CreateUnorderedAccessView(gBuffer.DiffuseRadianceHitDist.Get(), nullptr, &gbufferUavDesc, cpuHandle);
            cpuHandle.Offset(1, dxrDescriptorSize);
            
            // [10] u4: SpecularRadianceHitDist (RGBA16F)
            device->CreateUnorderedAccessView(gBuffer.SpecularRadianceHitDist.Get(), nullptr, &gbufferUavDesc, cpuHandle);
            cpuHandle.Offset(1, dxrDescriptorSize);
            
            // [11] u5: NormalRoughness (RGBA8)
            gbufferUavDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            device->CreateUnorderedAccessView(gBuffer.NormalRoughness.Get(), nullptr, &gbufferUavDesc, cpuHandle);
            cpuHandle.Offset(1, dxrDescriptorSize);
            
            // [12] u8: Albedo (RGBA8) - placed at index 12 for DXR compatibility
            gbufferUavDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            device->CreateUnorderedAccessView(gBuffer.Albedo.Get(), nullptr, &gbufferUavDesc, cpuHandle);
            cpuHandle.Offset(1, dxrDescriptorSize);
            
            // [13] u7: MotionVectors (RG16F)
            gbufferUavDesc.Format = DXGI_FORMAT_R16G16_FLOAT;
            device->CreateUnorderedAccessView(gBuffer.MotionVectors.Get(), nullptr, &gbufferUavDesc, cpuHandle);
            cpuHandle.Offset(1, dxrDescriptorSize);
            
            // [14] u6: ViewZ (R32F) - placed at index 14
            gbufferUavDesc.Format = DXGI_FORMAT_R32_FLOAT;
            device->CreateUnorderedAccessView(gBuffer.ViewZ.Get(), nullptr, &gbufferUavDesc, cpuHandle);
            cpuHandle.Offset(1, dxrDescriptorSize);
            
            // [15] u9: ShadowData (RG16F)
            gbufferUavDesc.Format = DXGI_FORMAT_R16G16_FLOAT;
            device->CreateUnorderedAccessView(gBuffer.ShadowData.Get(), nullptr, &gbufferUavDesc, cpuHandle);
            cpuHandle.Offset(1, dxrDescriptorSize);
            
            // [16] u10: ShadowTranslucency (RGBA16F)
            gbufferUavDesc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
            device->CreateUnorderedAccessView(gBuffer.ShadowTranslucency.Get(), nullptr, &gbufferUavDesc, cpuHandle);
            cpuHandle.Offset(1, dxrDescriptorSize);
        }
        
        // [17] UAV for photon hash table (u11)
        if (photonHashTableBuffer)
        {
            D3D12_UNORDERED_ACCESS_VIEW_DESC hashUavDesc = {};
            hashUavDesc.Format = DXGI_FORMAT_UNKNOWN;
            hashUavDesc.ViewDimension = D3D12_UAV_DIMENSION_BUFFER;
            hashUavDesc.Buffer.FirstElement = 0;
            hashUavDesc.Buffer.NumElements = PHOTON_HASH_TABLE_SIZE;
            hashUavDesc.Buffer.StructureByteStride = sizeof(PhotonHashCell);
            device->CreateUnorderedAccessView(photonHashTableBuffer.Get(), nullptr, &hashUavDesc, cpuHandle);
        }
    }

    void DXRPipeline::RenderWithDXR(RenderTarget* renderTarget, Scene* scene)
    {
        LogToFile("RenderWithDXR called");
        
        if (!renderTarget || !scene || !dxrPipelineReady)
        {
            LogToFile("RenderWithDXR early return - invalid state");
            return;
        }
        
        LogToFile("RenderWithDXR: getting device and command list");
        auto device = dxContext->GetDevice();
        auto commandList = dxContext->GetCommandList();
        
        UINT width = renderTarget->GetWidth();
        UINT height = renderTarget->GetHeight();
        
        // Create buffers if needed
        if (!sphereBuffer)
        {
            LogToFile("RenderWithDXR: creating buffers");
            if (!CreateBuffers(width, height))
            {
                LogToFile("RenderWithDXR: CreateBuffers failed");
                return;
            }
        }
        
        // Initialize denoiser if enabled and not yet initialized
        // Note: Denoiser is currently in stub mode (NRD_ENABLED=0)
        // When NRD is properly built and linked, set denoiserEnabled = true
        if (denoiserEnabled && !denoiser)
        {
            LogToFile("RenderWithDXR: initializing denoiser");
            if (!InitializeDenoiser(width, height))
            {
                LogToFile("RenderWithDXR: InitializeDenoiser failed, continuing without denoising");
                denoiserEnabled = false;
            }
        }
        
        // Update scene data
        LogToFile("RenderWithDXR: updating scene data");
        UpdateSceneData(scene, width, height);
        
        // Rebuild acceleration structures if needed
        if (needsAccelerationStructureRebuild || scene != lastScene)
        {
            LogToFile("RenderWithDXR: building acceleration structures");
            if (!BuildAccelerationStructures(scene))
            {
                LogToFile("Failed to build acceleration structures, falling back to compute");
                RenderWithComputeShader(renderTarget, scene);
                return;
            }
            LogToFile("RenderWithDXR: acceleration structures built");
        }
        
        // ============================================
        // Pass 1: Photon Emission (for Caustics)
        // ============================================
        if (causticsEnabled && photonStateObject)
        {
            LogToFile("RenderWithDXR: emitting photons for caustics");
            EmitPhotons(scene);
        }
        else
        {
            // No caustics - set photon map size to 0
            mappedConstantData->PhotonMapSize = 0;
        }
        
        // ============================================
        // Pass 2: Main Rendering
        // ============================================
        
        // Update descriptors
        LogToFile("RenderWithDXR: updating descriptors");
        UpdateDXRDescriptors(renderTarget);
        
        // Set descriptor heap
        LogToFile("RenderWithDXR: setting descriptor heaps");
        ID3D12DescriptorHeap* heaps[] = { dxrSrvUavHeap.Get() };
        commandList->SetDescriptorHeaps(1, heaps);
        
        // Set global root signature
        LogToFile("RenderWithDXR: setting root signature");
        commandList->SetComputeRootSignature(globalRootSignature.Get());
        
        // Set root descriptor tables (including photon map UAVs, G-Buffer UAVs, and photon hash table)
        LogToFile("RenderWithDXR: setting descriptor tables");
        CD3DX12_GPU_DESCRIPTOR_HANDLE gpuHandle(dxrSrvUavHeap->GetGPUDescriptorHandleForHeapStart());
        for (int i = 0; i < 18; i++)
        {
            commandList->SetComputeRootDescriptorTable(i, gpuHandle);
            gpuHandle.Offset(1, dxrDescriptorSize);
        }
        
        // Dispatch rays
        LogToFile("RenderWithDXR: preparing dispatch desc");
        D3D12_DISPATCH_RAYS_DESC dispatchDesc = {};
        
        dispatchDesc.RayGenerationShaderRecord.StartAddress = rayGenShaderTable->GetGPUVirtualAddress();
        dispatchDesc.RayGenerationShaderRecord.SizeInBytes = shaderTableRecordSize;
        
        dispatchDesc.MissShaderTable.StartAddress = missShaderTable->GetGPUVirtualAddress();
        dispatchDesc.MissShaderTable.SizeInBytes = shaderTableRecordSize;
        dispatchDesc.MissShaderTable.StrideInBytes = shaderTableRecordSize;
        
        dispatchDesc.HitGroupTable.StartAddress = hitGroupShaderTable->GetGPUVirtualAddress();
        dispatchDesc.HitGroupTable.SizeInBytes = shaderTableRecordSize * 2;  // 2 hit groups
        dispatchDesc.HitGroupTable.StrideInBytes = shaderTableRecordSize;
        
        dispatchDesc.Width = width;
        dispatchDesc.Height = height;
        dispatchDesc.Depth = 1;
        
        LogToFile("RenderWithDXR: dispatching rays");
        commandList->SetPipelineState1(stateObject.Get());
        commandList->DispatchRays(&dispatchDesc);
        LogToFile("RenderWithDXR: dispatch complete");
        
        // ============================================
        // Pass 3: Denoising (NRD)
        // ============================================
        char denoiseBuf[256];
        sprintf_s(denoiseBuf, "RenderWithDXR: denoiserEnabled=%d, denoiser=%p, IsReady=%d, IsSigmaEnabled=%d",
            denoiserEnabled ? 1 : 0, 
            denoiser.get(), 
            (denoiser ? denoiser->IsReady() : 0) ? 1 : 0,
            (denoiser ? denoiser->IsSigmaEnabled() : 0) ? 1 : 0);
        LogToFile(denoiseBuf);
        
        if (denoiserEnabled && denoiser && denoiser->IsReady())
        {
            LogToFile("RenderWithDXR: applying denoising");
            
            // UAV barrier to ensure ray tracing is complete
            D3D12_RESOURCE_BARRIER uavBarrier = {};
            uavBarrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
            uavBarrier.UAV.pResource = nullptr;  // Barrier on all UAVs
            commandList->ResourceBarrier(1, &uavBarrier);
            
            // Apply NRD denoising
            ApplyDenoising(renderTarget, scene);
            
            // Composite denoised output to final render target
            LogToFile("RenderWithDXR: compositing output (with SIGMA shadow)");
            CompositeOutput(renderTarget);
            
            LogToFile("RenderWithDXR: denoising complete");
        }
        // Without denoising: RenderTarget already contains final image with shadows
        // (shadows are applied in ClosestHit shaders via payload.color)
    }

    // ============================================
    // Legacy Functions (kept for compatibility)
    // ============================================

    void DXRPipeline::BuildPipeline()
    {
        // Calls CreateDXRPipeline internally
        if (!dxrPipelineReady && dxContext->IsDXRSupported())
        {
            dxrPipelineReady = CreateDXRPipeline();
        }
    }

    void DXRPipeline::CreateRootSignatures()
    {
        CreateGlobalRootSignature();
        CreateLocalRootSignature();
    }

    void DXRPipeline::CreatePipelineStateObject()
    {
        CreateDXRStateObject();
    }

    void DXRPipeline::CreateShaderTables()
    {
        CreateDXRShaderTables();
    }

    void DXRPipeline::DispatchRays(UINT width, UINT height)
    {
        // This is now handled internally by RenderWithDXR
    }

    bool DXRPipeline::LoadShader(const wchar_t* filename, ID3DBlob** shader)
    {
        if (FAILED(D3DReadFileToBlob(filename, shader)))
        {
            return false;
        }
        return true;
    }

    // ============================================
    // Photon Mapping Implementation (for Caustics)
    // ============================================

    bool DXRPipeline::CreatePhotonMappingResources()
    {
        LogToFile("CreatePhotonMappingResources started");
        
        auto device = dxContext->GetDevice();
        if (!device)
            return false;
        
        // Create photon map buffer (UAV)
        UINT64 photonBufferSize = sizeof(GPUPhoton) * maxPhotons;
        
        CD3DX12_HEAP_PROPERTIES defaultHeapProps(D3D12_HEAP_TYPE_DEFAULT);
        CD3DX12_RESOURCE_DESC photonBufferDesc = CD3DX12_RESOURCE_DESC::Buffer(
            photonBufferSize, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
        
        HRESULT hr = device->CreateCommittedResource(
            &defaultHeapProps,
            D3D12_HEAP_FLAG_NONE,
            &photonBufferDesc,
            D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            nullptr,
            IID_PPV_ARGS(&photonMapBuffer));
        
        if (FAILED(hr))
        {
            LogToFile("Failed to create photon map buffer", hr);
            return false;
        }
        
        // Create photon counter buffer (UAV with single UINT)
        CD3DX12_RESOURCE_DESC counterBufferDesc = CD3DX12_RESOURCE_DESC::Buffer(
            sizeof(UINT), D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
        
        hr = device->CreateCommittedResource(
            &defaultHeapProps,
            D3D12_HEAP_FLAG_NONE,
            &counterBufferDesc,
            D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            nullptr,
            IID_PPV_ARGS(&photonCounterBuffer));
        
        if (FAILED(hr))
        {
            LogToFile("Failed to create photon counter buffer", hr);
            return false;
        }
        
        // Create upload buffer for resetting counter
        CD3DX12_HEAP_PROPERTIES uploadHeapProps(D3D12_HEAP_TYPE_UPLOAD);
        CD3DX12_RESOURCE_DESC resetBufferDesc = CD3DX12_RESOURCE_DESC::Buffer(sizeof(UINT));
        
        hr = device->CreateCommittedResource(
            &uploadHeapProps,
            D3D12_HEAP_FLAG_NONE,
            &resetBufferDesc,
            D3D12_RESOURCE_STATE_GENERIC_READ,
            nullptr,
            IID_PPV_ARGS(&photonCounterResetBuffer));
        
        if (FAILED(hr))
        {
            LogToFile("Failed to create photon counter reset buffer", hr);
            return false;
        }
        
        // Initialize reset buffer to 0
        void* mapped = nullptr;
        photonCounterResetBuffer->Map(0, nullptr, &mapped);
        *static_cast<UINT*>(mapped) = 0;
        photonCounterResetBuffer->Unmap(0, nullptr);
        
        // Create photon descriptor heap
        D3D12_DESCRIPTOR_HEAP_DESC heapDesc = {};
        heapDesc.NumDescriptors = 10;  // UAV output, TLAS, CBV, object SRVs, photon UAVs
        heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        heapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        
        hr = device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&photonSrvUavHeap));
        if (FAILED(hr))
        {
            LogToFile("Failed to create photon descriptor heap", hr);
            return false;
        }
        
        LogToFile("CreatePhotonMappingResources completed successfully");
        return true;
    }

    bool DXRPipeline::CreatePhotonStateObject()
    {
        LogToFile("CreatePhotonStateObject started");
        
        auto device = dxContext->GetDevice();
        
        // Compile photon shaders from source at runtime
        if (!LoadOrCompileDXRShader(L"PhotonEmit", &photonEmitShader) ||
            !LoadOrCompileDXRShader(L"PhotonTrace", &photonTraceClosestHitShader))
        {
            LogToFile("Failed to load/compile photon shaders - caustics disabled");
            causticsEnabled = false;
            return false;
        }
        LogToFile("Successfully loaded photon shaders");
        
        // Build photon state object
        CD3DX12_STATE_OBJECT_DESC stateObjectDesc(D3D12_STATE_OBJECT_TYPE_RAYTRACING_PIPELINE);
        
        // Add DXIL libraries
        auto AddLibrary = [&](ID3DBlob* shader, const wchar_t* exportName) {
            auto lib = stateObjectDesc.CreateSubobject<CD3DX12_DXIL_LIBRARY_SUBOBJECT>();
            D3D12_SHADER_BYTECODE bc = { shader->GetBufferPointer(), shader->GetBufferSize() };
            lib->SetDXILLibrary(&bc);
            lib->DefineExport(exportName);
        };
        
        AddLibrary(photonEmitShader.Get(), L"PhotonEmit");
        AddLibrary(photonTraceClosestHitShader.Get(), L"PhotonTraceClosestHit");
        AddLibrary(photonTraceClosestHitShader.Get(), L"PhotonTraceMiss");
        
        // Also need intersection shader from main pipeline
        AddLibrary(intersectionShader.Get(), L"SphereIntersection");
        
        // Photon hit group
        auto hitGroup = stateObjectDesc.CreateSubobject<CD3DX12_HIT_GROUP_SUBOBJECT>();
        hitGroup->SetHitGroupExport(L"PhotonHitGroup");
        hitGroup->SetClosestHitShaderImport(L"PhotonTraceClosestHit");
        hitGroup->SetIntersectionShaderImport(L"SphereIntersection");
        hitGroup->SetHitGroupType(D3D12_HIT_GROUP_TYPE_PROCEDURAL_PRIMITIVE);
        
        // Shader config
        auto shaderConfig = stateObjectDesc.CreateSubobject<CD3DX12_RAYTRACING_SHADER_CONFIG_SUBOBJECT>();
        // PhotonPayload: float3 color (12) + float power (4) + uint depth (4) + bool isCaustic (4) + bool terminated (4) = 28 bytes
        UINT payloadSize = 28;
        UINT attribSize = 20;  // Same as main pipeline
        shaderConfig->Config(payloadSize, attribSize);
        
        // Global root signature (reuse from main pipeline)
        auto globalRS = stateObjectDesc.CreateSubobject<CD3DX12_GLOBAL_ROOT_SIGNATURE_SUBOBJECT>();
        globalRS->SetRootSignature(globalRootSignature.Get());
        
        // Pipeline config
        auto pipelineConfig = stateObjectDesc.CreateSubobject<CD3DX12_RAYTRACING_PIPELINE_CONFIG_SUBOBJECT>();
        pipelineConfig->Config(8);  // Reduced for compatibility
        
        // Create state object
        HRESULT hr = device->CreateStateObject(stateObjectDesc, IID_PPV_ARGS(&photonStateObject));
        if (FAILED(hr))
        {
            LogToFile("Failed to create photon state object", hr);
            causticsEnabled = false;
            return false;
        }
        
        hr = photonStateObject->QueryInterface(IID_PPV_ARGS(&photonStateObjectProperties));
        if (FAILED(hr))
        {
            LogToFile("Failed to get photon state object properties", hr);
            causticsEnabled = false;
            return false;
        }
        
        LogToFile("CreatePhotonStateObject completed successfully");
        return true;
    }

    bool DXRPipeline::CreatePhotonShaderTables()
    {
        LogToFile("CreatePhotonShaderTables started");
        
        auto device = dxContext->GetDevice();
        
        void* photonEmitId = photonStateObjectProperties->GetShaderIdentifier(L"PhotonEmit");
        void* photonMissId = photonStateObjectProperties->GetShaderIdentifier(L"PhotonTraceMiss");
        void* photonHitGroupId = photonStateObjectProperties->GetShaderIdentifier(L"PhotonHitGroup");
        
        if (!photonEmitId || !photonMissId || !photonHitGroupId)
        {
            LogToFile("Failed to get photon shader identifiers");
            return false;
        }
        
        UINT shaderIdSize = D3D12_SHADER_IDENTIFIER_SIZE_IN_BYTES;
        CD3DX12_HEAP_PROPERTIES uploadHeapProps(D3D12_HEAP_TYPE_UPLOAD);
        
        // Ray generation shader table
        {
            CD3DX12_RESOURCE_DESC desc = CD3DX12_RESOURCE_DESC::Buffer(shaderTableRecordSize);
            device->CreateCommittedResource(&uploadHeapProps, D3D12_HEAP_FLAG_NONE, &desc,
                D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&photonRayGenShaderTable));
            
            void* mapped = nullptr;
            photonRayGenShaderTable->Map(0, nullptr, &mapped);
            memcpy(mapped, photonEmitId, shaderIdSize);
            photonRayGenShaderTable->Unmap(0, nullptr);
        }
        
        // Miss shader table
        {
            CD3DX12_RESOURCE_DESC desc = CD3DX12_RESOURCE_DESC::Buffer(shaderTableRecordSize);
            device->CreateCommittedResource(&uploadHeapProps, D3D12_HEAP_FLAG_NONE, &desc,
                D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&photonMissShaderTable));
            
            void* mapped = nullptr;
            photonMissShaderTable->Map(0, nullptr, &mapped);
            memcpy(mapped, photonMissId, shaderIdSize);
            photonMissShaderTable->Unmap(0, nullptr);
        }
        
        // Hit group shader table
        {
            CD3DX12_RESOURCE_DESC desc = CD3DX12_RESOURCE_DESC::Buffer(shaderTableRecordSize);
            device->CreateCommittedResource(&uploadHeapProps, D3D12_HEAP_FLAG_NONE, &desc,
                D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&photonHitGroupShaderTable));
            
            void* mapped = nullptr;
            photonHitGroupShaderTable->Map(0, nullptr, &mapped);
            memcpy(mapped, photonHitGroupId, shaderIdSize);
            photonHitGroupShaderTable->Unmap(0, nullptr);
        }
        
        LogToFile("CreatePhotonShaderTables completed successfully");
        return true;
    }

    void DXRPipeline::ClearPhotonMap()
    {
        auto commandList = dxContext->GetCommandList();
        
        // Transition counter to copy dest
        CD3DX12_RESOURCE_BARRIER barrier = CD3DX12_RESOURCE_BARRIER::Transition(
            photonCounterBuffer.Get(),
            D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            D3D12_RESOURCE_STATE_COPY_DEST);
        commandList->ResourceBarrier(1, &barrier);
        
        // Copy 0 to counter
        commandList->CopyResource(photonCounterBuffer.Get(), photonCounterResetBuffer.Get());
        
        // Transition back
        barrier = CD3DX12_RESOURCE_BARRIER::Transition(
            photonCounterBuffer.Get(),
            D3D12_RESOURCE_STATE_COPY_DEST,
            D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        commandList->ResourceBarrier(1, &barrier);
    }

    // ============================================
    // Photon Spatial Hash Implementation
    // ============================================

    bool DXRPipeline::CreatePhotonHashResources()
    {
        LogToFile("CreatePhotonHashResources started");
        
        auto device = dxContext->GetDevice();
        if (!device)
            return false;
        
        // Create hash table buffer
        // Size = PHOTON_HASH_TABLE_SIZE * sizeof(PhotonHashCell)
        UINT64 hashTableSize = PHOTON_HASH_TABLE_SIZE * sizeof(PhotonHashCell);
        
        CD3DX12_HEAP_PROPERTIES defaultHeapProps(D3D12_HEAP_TYPE_DEFAULT);
        CD3DX12_RESOURCE_DESC hashTableDesc = CD3DX12_RESOURCE_DESC::Buffer(
            hashTableSize, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
        
        HRESULT hr = device->CreateCommittedResource(
            &defaultHeapProps,
            D3D12_HEAP_FLAG_NONE,
            &hashTableDesc,
            D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            nullptr,
            IID_PPV_ARGS(&photonHashTableBuffer));
        
        if (FAILED(hr))
        {
            LogToFile("Failed to create photon hash table buffer", hr);
            return false;
        }
        
        // Create constant buffer for hash compute shader
        CD3DX12_HEAP_PROPERTIES uploadHeapProps(D3D12_HEAP_TYPE_UPLOAD);
        CD3DX12_RESOURCE_DESC constBufferDesc = CD3DX12_RESOURCE_DESC::Buffer(
            (sizeof(PhotonHashConstants) + 255) & ~255);  // 256-byte aligned
        
        hr = device->CreateCommittedResource(
            &uploadHeapProps,
            D3D12_HEAP_FLAG_NONE,
            &constBufferDesc,
            D3D12_RESOURCE_STATE_GENERIC_READ,
            nullptr,
            IID_PPV_ARGS(&photonHashConstantBuffer));
        
        if (FAILED(hr))
        {
            LogToFile("Failed to create photon hash constant buffer", hr);
            return false;
        }
        
        // Map constant buffer
        photonHashConstantBuffer->Map(0, nullptr, reinterpret_cast<void**>(&mappedPhotonHashConstants));
        
        // Create root signature for hash compute shaders
        CD3DX12_ROOT_PARAMETER1 rootParams[3];
        rootParams[0].InitAsUnorderedAccessView(0);  // u0 - PhotonMap
        rootParams[1].InitAsUnorderedAccessView(1);  // u1 - PhotonHashTable
        rootParams[2].InitAsConstantBufferView(0);   // b0 - Constants
        
        CD3DX12_VERSIONED_ROOT_SIGNATURE_DESC rootSigDesc;
        rootSigDesc.Init_1_1(3, rootParams, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_NONE);
        
        ComPtr<ID3DBlob> signature;
        ComPtr<ID3DBlob> error;
        hr = D3DX12SerializeVersionedRootSignature(&rootSigDesc, D3D_ROOT_SIGNATURE_VERSION_1_1, &signature, &error);
        if (FAILED(hr))
        {
            if (error)
                LogToFile((std::string("Root signature serialization failed: ") + (char*)error->GetBufferPointer()).c_str());
            return false;
        }
        
        hr = device->CreateRootSignature(0, signature->GetBufferPointer(), signature->GetBufferSize(),
            IID_PPV_ARGS(&photonHashRootSignature));
        if (FAILED(hr))
        {
            LogToFile("Failed to create photon hash root signature", hr);
            return false;
        }
        
        // Compile hash compute shaders
        if (!LoadOrCompileDXRShader(L"BuildPhotonHashClear", &photonHashClearShader) ||
            !LoadOrCompileDXRShader(L"BuildPhotonHashBuild", &photonHashBuildShader))
        {
            LogToFile("Failed to compile photon hash shaders");
            return false;
        }
        
        // Create clear pipeline state
        D3D12_COMPUTE_PIPELINE_STATE_DESC clearPipelineDesc = {};
        clearPipelineDesc.pRootSignature = photonHashRootSignature.Get();
        clearPipelineDesc.CS = { photonHashClearShader->GetBufferPointer(), photonHashClearShader->GetBufferSize() };
        
        hr = device->CreateComputePipelineState(&clearPipelineDesc, IID_PPV_ARGS(&photonHashClearPipeline));
        if (FAILED(hr))
        {
            LogToFile("Failed to create photon hash clear pipeline", hr);
            return false;
        }
        
        // Create build pipeline state
        D3D12_COMPUTE_PIPELINE_STATE_DESC buildPipelineDesc = {};
        buildPipelineDesc.pRootSignature = photonHashRootSignature.Get();
        buildPipelineDesc.CS = { photonHashBuildShader->GetBufferPointer(), photonHashBuildShader->GetBufferSize() };
        
        hr = device->CreateComputePipelineState(&buildPipelineDesc, IID_PPV_ARGS(&photonHashBuildPipeline));
        if (FAILED(hr))
        {
            LogToFile("Failed to create photon hash build pipeline", hr);
            return false;
        }
        
        LogToFile("CreatePhotonHashResources completed successfully");
        return true;
    }

    void DXRPipeline::BuildPhotonHashTable()
    {
        if (!photonHashClearPipeline || !photonHashBuildPipeline)
            return;
        
        auto commandList = dxContext->GetCommandList();
        
        // Update constants
        mappedPhotonHashConstants->PhotonCount = mappedConstantData->PhotonMapSize;
        mappedPhotonHashConstants->CellSize = photonRadius * 2.0f;
        
        // Set root signature and resources
        commandList->SetComputeRootSignature(photonHashRootSignature.Get());
        commandList->SetComputeRootUnorderedAccessView(0, photonMapBuffer->GetGPUVirtualAddress());
        commandList->SetComputeRootUnorderedAccessView(1, photonHashTableBuffer->GetGPUVirtualAddress());
        commandList->SetComputeRootConstantBufferView(2, photonHashConstantBuffer->GetGPUVirtualAddress());
        
        // Step 1: Clear hash table
        commandList->SetPipelineState(photonHashClearPipeline.Get());
        UINT clearDispatchX = (PHOTON_HASH_TABLE_SIZE + 255) / 256;
        commandList->Dispatch(clearDispatchX, 1, 1);
        
        // UAV barrier between clear and build
        CD3DX12_RESOURCE_BARRIER barrier = CD3DX12_RESOURCE_BARRIER::UAV(photonHashTableBuffer.Get());
        commandList->ResourceBarrier(1, &barrier);
        
        // Step 2: Build hash table
        commandList->SetPipelineState(photonHashBuildPipeline.Get());
        UINT buildDispatchX = (mappedConstantData->PhotonMapSize + 255) / 256;
        if (buildDispatchX > 0)
        {
            commandList->Dispatch(buildDispatchX, 1, 1);
        }
        
        // UAV barrier to ensure hash table is ready for reading
        commandList->ResourceBarrier(1, &barrier);
    }

    void DXRPipeline::UpdatePhotonDescriptors()
    {
        auto device = dxContext->GetDevice();
        CD3DX12_CPU_DESCRIPTOR_HANDLE cpuHandle(photonSrvUavHeap->GetCPUDescriptorHandleForHeapStart());
        
        // [0] UAV for output (not used in photon pass, but keep layout consistent)
        // Skip this slot
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        // [1] SRV for TLAS
        D3D12_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
        srvDesc.Format = DXGI_FORMAT_UNKNOWN;
        srvDesc.ViewDimension = D3D12_SRV_DIMENSION_RAYTRACING_ACCELERATION_STRUCTURE;
        srvDesc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        srvDesc.RaytracingAccelerationStructure.Location = accelerationStructure->GetTLAS()->GetGPUVirtualAddress();
        device->CreateShaderResourceView(nullptr, &srvDesc, cpuHandle);
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        // [2] CBV for constants
        D3D12_CONSTANT_BUFFER_VIEW_DESC cbvDesc = {};
        cbvDesc.BufferLocation = constantBuffer->GetGPUVirtualAddress();
        cbvDesc.SizeInBytes = sizeof(SceneConstants);
        device->CreateConstantBufferView(&cbvDesc, cpuHandle);
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        // [3-6] Object SRVs
        D3D12_SHADER_RESOURCE_VIEW_DESC bufferSrvDesc = {};
        bufferSrvDesc.Format = DXGI_FORMAT_UNKNOWN;
        bufferSrvDesc.ViewDimension = D3D12_SRV_DIMENSION_BUFFER;
        bufferSrvDesc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        bufferSrvDesc.Buffer.FirstElement = 0;
        
        bufferSrvDesc.Buffer.NumElements = 32;
        bufferSrvDesc.Buffer.StructureByteStride = sizeof(GPUSphere);
        device->CreateShaderResourceView(sphereBuffer.Get(), &bufferSrvDesc, cpuHandle);
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        bufferSrvDesc.Buffer.StructureByteStride = sizeof(GPUPlane);
        device->CreateShaderResourceView(planeBuffer.Get(), &bufferSrvDesc, cpuHandle);
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        bufferSrvDesc.Buffer.StructureByteStride = sizeof(GPUBox);
        device->CreateShaderResourceView(boxBuffer.Get(), &bufferSrvDesc, cpuHandle);
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        bufferSrvDesc.Buffer.NumElements = 8;
        bufferSrvDesc.Buffer.StructureByteStride = sizeof(GPULight);
        device->CreateShaderResourceView(lightBuffer.Get(), &bufferSrvDesc, cpuHandle);
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        // [7] UAV for photon map
        D3D12_UNORDERED_ACCESS_VIEW_DESC uavDesc = {};
        uavDesc.Format = DXGI_FORMAT_UNKNOWN;
        uavDesc.ViewDimension = D3D12_UAV_DIMENSION_BUFFER;
        uavDesc.Buffer.FirstElement = 0;
        uavDesc.Buffer.NumElements = maxPhotons;
        uavDesc.Buffer.StructureByteStride = sizeof(GPUPhoton);
        device->CreateUnorderedAccessView(photonMapBuffer.Get(), nullptr, &uavDesc, cpuHandle);
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        // [8] UAV for photon counter
        D3D12_UNORDERED_ACCESS_VIEW_DESC counterUavDesc = {};
        counterUavDesc.Format = DXGI_FORMAT_R32_UINT;
        counterUavDesc.ViewDimension = D3D12_UAV_DIMENSION_BUFFER;
        counterUavDesc.Buffer.FirstElement = 0;
        counterUavDesc.Buffer.NumElements = 1;
        device->CreateUnorderedAccessView(photonCounterBuffer.Get(), nullptr, &counterUavDesc, cpuHandle);
    }

    void DXRPipeline::EmitPhotons(Scene* scene)
    {
        if (!causticsEnabled || !photonStateObject)
            return;
        
        LogToFile("EmitPhotons started");
        
        auto commandList = dxContext->GetCommandList();
        
        // Clear the photon map
        ClearPhotonMap();
        
        // Update photon descriptors
        UpdatePhotonDescriptors();
        
        // Set descriptor heap
        ID3D12DescriptorHeap* heaps[] = { photonSrvUavHeap.Get() };
        commandList->SetDescriptorHeaps(1, heaps);
        
        // Set root signature
        commandList->SetComputeRootSignature(globalRootSignature.Get());
        
        // Set descriptor tables
        CD3DX12_GPU_DESCRIPTOR_HANDLE gpuHandle(photonSrvUavHeap->GetGPUDescriptorHandleForHeapStart());
        for (int i = 0; i < 9; i++)
        {
            commandList->SetComputeRootDescriptorTable(i, gpuHandle);
            gpuHandle.Offset(1, dxrDescriptorSize);
        }
        
        // Calculate number of photons to emit
        UINT totalPhotons = photonsPerLight * scene->GetLights().size();
        totalPhotons = min(totalPhotons, maxPhotons);
        
        // Dispatch photon rays
        D3D12_DISPATCH_RAYS_DESC dispatchDesc = {};
        
        dispatchDesc.RayGenerationShaderRecord.StartAddress = photonRayGenShaderTable->GetGPUVirtualAddress();
        dispatchDesc.RayGenerationShaderRecord.SizeInBytes = shaderTableRecordSize;
        
        dispatchDesc.MissShaderTable.StartAddress = photonMissShaderTable->GetGPUVirtualAddress();
        dispatchDesc.MissShaderTable.SizeInBytes = shaderTableRecordSize;
        dispatchDesc.MissShaderTable.StrideInBytes = shaderTableRecordSize;
        
        dispatchDesc.HitGroupTable.StartAddress = photonHitGroupShaderTable->GetGPUVirtualAddress();
        dispatchDesc.HitGroupTable.SizeInBytes = shaderTableRecordSize;
        dispatchDesc.HitGroupTable.StrideInBytes = shaderTableRecordSize;
        
        dispatchDesc.Width = totalPhotons;
        dispatchDesc.Height = 1;
        dispatchDesc.Depth = 1;
        
        commandList->SetPipelineState1(photonStateObject.Get());
        commandList->DispatchRays(&dispatchDesc);
        
        // UAV barrier to ensure photons are written before reading
        CD3DX12_RESOURCE_BARRIER barrier = CD3DX12_RESOURCE_BARRIER::UAV(photonMapBuffer.Get());
        commandList->ResourceBarrier(1, &barrier);
        
        // Update scene constants with photon info
        mappedConstantData->NumPhotons = totalPhotons;
        mappedConstantData->PhotonMapSize = totalPhotons;
        mappedConstantData->PhotonRadius = photonRadius;
        mappedConstantData->CausticIntensity = causticIntensity;
        
        // Build spatial hash table for O(1) photon lookup
        BuildPhotonHashTable();
        
        LogToFile("EmitPhotons completed");
    }

    // ============================================
    // NRD Denoiser Integration
    // ============================================

    bool DXRPipeline::InitializeDenoiser(UINT width, UINT height)
    {
        LogToFile("Initializing NRD Denoiser...");
        
        // Create denoiser if not already created
        if (!denoiser)
        {
            denoiser = std::make_unique<NRDDenoiser>(dxContext);
        }
        
        // Initialize or resize
        if (!denoiser->IsReady())
        {
            if (!denoiser->Initialize(width, height))
            {
                LogToFile("Failed to initialize NRD Denoiser");
                return false;
            }
        }
        else
        {
            if (!denoiser->Resize(width, height))
            {
                LogToFile("Failed to resize NRD Denoiser");
                return false;
            }
        }
        
        // Initialize frame tracking
        XMStoreFloat4x4(&prevViewMatrix, XMMatrixIdentity());
        XMStoreFloat4x4(&prevProjMatrix, XMMatrixIdentity());
        isFirstFrame = true;
        frameIndex = 0;
        
        LogToFile("NRD Denoiser initialized successfully");
        return true;
    }

    void DXRPipeline::ApplyDenoising(RenderTarget* renderTarget, Scene* scene)
    {
        LogToFile("ApplyDenoising: called");
        if (!denoiser || !denoiser->IsReady())
        {
            LogToFile("ApplyDenoising: denoiser not ready, skipping");
            return;
        }
        LogToFile("ApplyDenoising: denoiser is ready, proceeding");
        
        auto commandList = dxContext->GetCommandList();
        auto camera = scene->GetCamera();
        
        // Build frame settings for denoiser
        DenoiserFrameSettings settings = {};
        
        // Get current view/projection matrices
        XMMATRIX viewMatrix = camera.GetViewMatrix();
        UINT width = denoiser ? denoiser->GetWidth() : 1920;
        UINT height = denoiser ? denoiser->GetHeight() : 1080;
        float aspectRatio = static_cast<float>(width) / static_cast<float>(height);
        XMMATRIX projMatrix = camera.GetProjectionMatrix(aspectRatio);
        
        XMStoreFloat4x4(&settings.ViewMatrix, viewMatrix);
        XMStoreFloat4x4(&settings.ProjMatrix, projMatrix);
        XMStoreFloat4x4(&settings.WorldToViewMatrix, viewMatrix);
        
        // Previous frame matrices
        settings.ViewMatrixPrev = prevViewMatrix;
        settings.ProjMatrixPrev = prevProjMatrix;
        settings.WorldToViewMatrixPrev = prevViewMatrix;
        
        // Jitter (for TAA-like accumulation) - no jitter for now
        settings.JitterOffset = XMFLOAT2(0.0f, 0.0f);
        settings.JitterOffsetPrev = XMFLOAT2(0.0f, 0.0f);
        
        // Motion vector scale (screen space)
        settings.MotionVectorScale = XMFLOAT2(
            static_cast<float>(renderTarget->GetWidth()),
            static_cast<float>(renderTarget->GetHeight())
        );
        
        // Camera settings
        settings.CameraNear = 0.1f;
        settings.CameraFar = 10000.0f;
        settings.IsFirstFrame = isFirstFrame;
        settings.EnableValidation = false;
        settings.DenoiserStabilization = denoiserStabilization;
        
        // SIGMA enabled - let it process shadow data
        denoiser->SetSigmaEnabled(true);
        LogToFile("ApplyDenoising: SIGMA ENABLED");
        
        // CRITICAL: Copy raw specular data BEFORE NRD processes it
        // NRD corrupts the original SpecularRadianceHitDist buffer, so we need a backup
        // for the mirror bypass in Composite.hlsl
        auto& gBuffer = denoiser->GetGBuffer();
        if (gBuffer.RawSpecularBackup && gBuffer.SpecularRadianceHitDist)
        {
            // Track RawSpecularBackup state across frames
            static bool rawSpecularInSrvState = false;
            
            // CRITICAL: First, flush ALL UAV writes with an explicit UAV barrier
            // This ensures ray tracing shader has finished writing to SpecularRadianceHitDist
            D3D12_RESOURCE_BARRIER uavFlush = {};
            uavFlush.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
            uavFlush.UAV.pResource = gBuffer.SpecularRadianceHitDist.Get();  // Specific resource
            commandList->ResourceBarrier(1, &uavFlush);
            
            // Transition resources for copy operation
            D3D12_RESOURCE_BARRIER copyBarriers[2] = {};
            int barrierCount = 0;
            
            // Source: UAV -> COPY_SOURCE
            copyBarriers[barrierCount].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            copyBarriers[barrierCount].Transition.pResource = gBuffer.SpecularRadianceHitDist.Get();
            copyBarriers[barrierCount].Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
            copyBarriers[barrierCount].Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
            copyBarriers[barrierCount].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            barrierCount++;
            
            // Dest: Previous state -> COPY_DEST
            copyBarriers[barrierCount].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            copyBarriers[barrierCount].Transition.pResource = gBuffer.RawSpecularBackup.Get();
            copyBarriers[barrierCount].Transition.StateBefore = rawSpecularInSrvState 
                ? D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE 
                : D3D12_RESOURCE_STATE_COMMON;
            copyBarriers[barrierCount].Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
            copyBarriers[barrierCount].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            barrierCount++;
            
            commandList->ResourceBarrier(barrierCount, copyBarriers);
            
            // Perform the copy
            commandList->CopyResource(gBuffer.RawSpecularBackup.Get(), gBuffer.SpecularRadianceHitDist.Get());
            
            // Transition back for NRD and Composite usage
            D3D12_RESOURCE_BARRIER postCopyBarriers[2] = {};
            
            // Source: COPY_SOURCE -> UAV (NRD needs it as UAV)
            postCopyBarriers[0].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            postCopyBarriers[0].Transition.pResource = gBuffer.SpecularRadianceHitDist.Get();
            postCopyBarriers[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
            postCopyBarriers[0].Transition.StateAfter = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
            postCopyBarriers[0].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            
            // Dest: COPY_DEST -> SRV (Composite reads it)
            postCopyBarriers[1].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            postCopyBarriers[1].Transition.pResource = gBuffer.RawSpecularBackup.Get();
            postCopyBarriers[1].Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
            postCopyBarriers[1].Transition.StateAfter = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
            postCopyBarriers[1].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            
            commandList->ResourceBarrier(2, postCopyBarriers);
            
            rawSpecularInSrvState = true;  // Now in SRV state for next frame
            
            LogToFile("ApplyDenoising: RawSpecularBackup copied with proper state transitions");
        }
        
        // Apply denoising
        LogToFile("ApplyDenoising: calling denoiser->Denoise()");
        denoiser->Denoise(commandList, settings);
        LogToFile("ApplyDenoising: denoiser->Denoise() returned");
        
        // Transition NRD output from UAV to SRV for CompositeOutput
        // CRITICAL: Need both UAV barrier (for sync) AND state transition (for read access)
        auto& output = denoiser->GetOutput();
        // gBuffer already declared above for the copy operation
        
        // First: UAV barriers for synchronization
        D3D12_RESOURCE_BARRIER uavBarriers[3] = {};
        uavBarriers[0].Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        uavBarriers[0].UAV.pResource = output.DiffuseRadiance.Get();
        uavBarriers[1].Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        uavBarriers[1].UAV.pResource = output.SpecularRadiance.Get();
        uavBarriers[2].Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        uavBarriers[2].UAV.pResource = output.DenoisedShadow.Get();
        commandList->ResourceBarrier(3, uavBarriers);
        
        // Second: State transitions from UAV to SRV for Composite shader read
        // NOTE: Only transition NRD outputs and Albedo. G-Buffer inputs (t4-t8) are also used by NRD
        // and NRD manages their state internally. Transitioning them here causes corruption.
        D3D12_RESOURCE_BARRIER transitionBarriers[4] = {};
        transitionBarriers[0].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        transitionBarriers[0].Transition.pResource = output.DiffuseRadiance.Get();
        transitionBarriers[0].Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        transitionBarriers[0].Transition.StateAfter = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        transitionBarriers[0].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        
        transitionBarriers[1].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        transitionBarriers[1].Transition.pResource = output.SpecularRadiance.Get();
        transitionBarriers[1].Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        transitionBarriers[1].Transition.StateAfter = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        transitionBarriers[1].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        
        transitionBarriers[2].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        transitionBarriers[2].Transition.pResource = output.DenoisedShadow.Get();
        transitionBarriers[2].Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        transitionBarriers[2].Transition.StateAfter = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        transitionBarriers[2].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        
        transitionBarriers[3].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        transitionBarriers[3].Transition.pResource = gBuffer.Albedo.Get();
        transitionBarriers[3].Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        transitionBarriers[3].Transition.StateAfter = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        transitionBarriers[3].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        
        commandList->ResourceBarrier(4, transitionBarriers);
        LogToFile("ApplyDenoising: UAV barriers + state transitions added for NRD outputs");
        
        // Update previous frame data
        XMStoreFloat4x4(&prevViewMatrix, viewMatrix);
        XMStoreFloat4x4(&prevProjMatrix, projMatrix);
        isFirstFrame = false;
        frameIndex++;
    }

    bool DXRPipeline::CreateCompositePipeline()
    {
        LogToFile("CreateCompositePipeline: creating composite compute pipeline");
        
        auto device = dxContext->GetDevice();
        
        // Load or compile composite shader via ShaderCache
        ComPtr<ID3DBlob> compositeShader;
        if (shaderCache)
        {
            LogToFile("CreateCompositePipeline: using ShaderCache");
            if (!shaderCache->GetComputeShader(L"Composite", L"CSMain", &compositeShader))
            {
                LogToFile("CreateCompositePipeline: ShaderCache failed to get Composite");
                return false;
            }
            LogToFile("CreateCompositePipeline: got Composite from cache");
        }
        else
        {
            // Fallback: compile directly
            std::wstring compositeShaderPath = shaderSourcePath + L"Composite.hlsl";
            LogToFile(("CreateCompositePipeline: compiling " + std::string(compositeShaderPath.begin(), compositeShaderPath.end())).c_str());
            
            ComPtr<ID3DBlob> errorBlob;
            HRESULT hr = D3DCompileFromFile(
                compositeShaderPath.c_str(),
                nullptr,
                D3D_COMPILE_STANDARD_FILE_INCLUDE,
                "CSMain",
                "cs_5_1",
                D3DCOMPILE_OPTIMIZATION_LEVEL3 | D3DCOMPILE_DEBUG,
                0,
                &compositeShader,
                &errorBlob);

            if (FAILED(hr))
            {
                if (errorBlob)
                {
                    LogToFile("CreateCompositePipeline: composite shader compile error");
                    LogToFile((char*)errorBlob->GetBufferPointer());
                }
                return false;
            }
        }
        
        // Create root signature for composite pass
        // Inputs: t0-t10 = DenoisedDiffuse, DenoisedSpecular, Albedo, DenoisedShadow, GBuffer textures, RawSpecularBackup
        // Output: u0 = FinalOutput
        // Constants: b0 = CompositeConstants (8 values)
        
        CD3DX12_DESCRIPTOR_RANGE1 srvRange;
        srvRange.Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 11, 0); // t0-t10
        
        CD3DX12_DESCRIPTOR_RANGE1 uavRange;
        uavRange.Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 0); // u0
        
        // Use static samplers instead of descriptor table
        D3D12_STATIC_SAMPLER_DESC staticSamplers[2] = {};
        
        // s0: LinearSampler
        staticSamplers[0].Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
        staticSamplers[0].AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        staticSamplers[0].AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        staticSamplers[0].AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        staticSamplers[0].MaxLOD = D3D12_FLOAT32_MAX;
        staticSamplers[0].ShaderRegister = 0;
        staticSamplers[0].RegisterSpace = 0;
        staticSamplers[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
        
        // s1: PointSampler (no interpolation - for shadow edges)
        staticSamplers[1].Filter = D3D12_FILTER_MIN_MAG_MIP_POINT;
        staticSamplers[1].AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        staticSamplers[1].AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        staticSamplers[1].AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        staticSamplers[1].MaxLOD = D3D12_FLOAT32_MAX;
        staticSamplers[1].ShaderRegister = 1;
        staticSamplers[1].RegisterSpace = 0;
        staticSamplers[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
        
        CD3DX12_ROOT_PARAMETER1 rootParams[3];
        rootParams[0].InitAsDescriptorTable(1, &srvRange, D3D12_SHADER_VISIBILITY_ALL);
        rootParams[1].InitAsDescriptorTable(1, &uavRange, D3D12_SHADER_VISIBILITY_ALL);
        rootParams[2].InitAsConstants(8, 0); // b0: OutputSize (2), ExposureValue, ToneMapOperator, DebugMode, DebugTileScale, UseDenoisedShadow, ShadowStrength
        
        CD3DX12_VERSIONED_ROOT_SIGNATURE_DESC rootSigDesc;
        rootSigDesc.Init_1_1(_countof(rootParams), rootParams, 2, staticSamplers,
            D3D12_ROOT_SIGNATURE_FLAG_NONE);
        
        ComPtr<ID3DBlob> serializedRootSig;
        ComPtr<ID3DBlob> errorBlob;
        HRESULT hr = D3DX12SerializeVersionedRootSignature(&rootSigDesc,
            D3D_ROOT_SIGNATURE_VERSION_1_1, &serializedRootSig, &errorBlob);
        
        if (FAILED(hr))
        {
            if (errorBlob)
                LogToFile((char*)errorBlob->GetBufferPointer());
            LogToFile("CreateCompositePipeline: failed to serialize root signature", hr);
            return false;
        }
        
        hr = device->CreateRootSignature(0, serializedRootSig->GetBufferPointer(),
            serializedRootSig->GetBufferSize(), IID_PPV_ARGS(&compositeRootSignature));
        
        if (FAILED(hr))
        {
            LogToFile("CreateCompositePipeline: failed to create root signature", hr);
            return false;
        }
        
        // Create compute pipeline state
        D3D12_COMPUTE_PIPELINE_STATE_DESC psoDesc = {};
        psoDesc.pRootSignature = compositeRootSignature.Get();
        psoDesc.CS = { compositeShader->GetBufferPointer(), compositeShader->GetBufferSize() };
        
        hr = device->CreateComputePipelineState(&psoDesc, IID_PPV_ARGS(&compositePipelineState));
        
        if (FAILED(hr))
        {
            LogToFile("CreateCompositePipeline: failed to create pipeline state", hr);
            return false;
        }
        
        LogToFile("CreateCompositePipeline: success");
        return true;
    }

    void DXRPipeline::CompositeOutput(RenderTarget* renderTarget)
    {
        if (!denoiser || !denoiser->IsReady())
            return;

        LogToFile("CompositeOutput: entered");
        
        // Always recreate pipeline to pick up shader changes during development
        // TODO: Remove this forced recreation in release builds
        compositePipelineState.Reset();
        compositeRootSignature.Reset();
        
        if (!compositePipelineState)
        {
            // Lazy initialization of composite pipeline
            if (!CreateCompositePipeline())
            {
                LogToFile("CompositeOutput: failed to create composite pipeline");
                return;
            }
        }
        
        // Create composite descriptor heap if not exists
        if (!compositeDescriptorHeap)
        {
            auto device = dxContext->GetDevice();
            D3D12_DESCRIPTOR_HEAP_DESC heapDesc = {};
            heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
            heapDesc.NumDescriptors = 16; // 10 SRVs + 1 UAV + some extra
            heapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
            
            HRESULT hr = device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&compositeDescriptorHeap));
            if (FAILED(hr))
            {
                LogToFile("CompositeOutput: failed to create descriptor heap", hr);
                return;
            }
        }
        
        auto commandList = dxContext->GetCommandList();
        auto device = dxContext->GetDevice();
        
        UINT width = renderTarget->GetWidth();
        UINT height = renderTarget->GetHeight();
        
        // Get descriptor size
        UINT descriptorSize = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        
        // Set up descriptors
        CD3DX12_CPU_DESCRIPTOR_HANDLE cpuHandle(compositeDescriptorHeap->GetCPUDescriptorHandleForHeapStart());
        CD3DX12_GPU_DESCRIPTOR_HANDLE gpuHandle(compositeDescriptorHeap->GetGPUDescriptorHandleForHeapStart());
        
        auto& gBuffer = denoiser->GetGBuffer();
        auto& output = denoiser->GetOutput();
        
        // Create SRVs for all input textures
        // t0: DenoisedDiffuse (use output.DiffuseRadiance)
        D3D12_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
        srvDesc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
        srvDesc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        srvDesc.Texture2D.MipLevels = 1;
        
        // DEBUG: Log resource pointers and GPU descriptor handles
        {
            char debugBuf[1024];
            sprintf_s(debugBuf, "CompositeOutput SRV bindings:\n"
                "  t0 (DenoisedDiffuse)  Resource=%p\n"
                "  t1 (DenoisedSpecular) Resource=%p\n"
                "  t2 (Albedo)           Resource=%p\n"
                "  t4 (GBuffer_DiffuseIn) Resource=%p\n"
                "  GPU Handle Base = %llu, DescriptorSize = %u\n"
                "  t0 GPU Handle = %llu\n"
                "  t1 GPU Handle = %llu\n"
                "  t2 GPU Handle = %llu\n"
                "  Are t0 and t4 different? %s",
                (void*)output.DiffuseRadiance.Get(), 
                (void*)output.SpecularRadiance.Get(), 
                (void*)gBuffer.Albedo.Get(),
                (void*)gBuffer.DiffuseRadianceHitDist.Get(),
                gpuHandle.ptr, descriptorSize,
                gpuHandle.ptr,
                gpuHandle.ptr + descriptorSize,
                gpuHandle.ptr + descriptorSize * 2,
                (output.DiffuseRadiance.Get() != gBuffer.DiffuseRadianceHitDist.Get()) ? "YES (correct)" : "NO (BUG!)");
            LogToFile(debugBuf);
        }
        
        struct CompositeSrvBinding
        {
            ID3D12Resource* resource;
            DXGI_FORMAT format;
        };

        // t0-t10: Match Composite.hlsl SRV expectations (t2 = GBuffer_Albedo)
        CompositeSrvBinding srvs[] = {
            { output.DiffuseRadiance.Get(),        DXGI_FORMAT_R16G16B16A16_FLOAT }, // t0 DenoisedDiffuse
            { output.SpecularRadiance.Get(),       DXGI_FORMAT_R16G16B16A16_FLOAT }, // t1 DenoisedSpecular
            { gBuffer.Albedo.Get(),                DXGI_FORMAT_R8G8B8A8_UNORM },     // t2 GBuffer_Albedo
            { output.DenoisedShadow.Get(),         DXGI_FORMAT_R16G16B16A16_FLOAT }, // t3 DenoisedShadow (SIGMA output - RGBA16F required)
            { gBuffer.DiffuseRadianceHitDist.Get(),DXGI_FORMAT_R16G16B16A16_FLOAT }, // t4 GBuffer_DiffuseIn
            { gBuffer.SpecularRadianceHitDist.Get(),DXGI_FORMAT_R16G16B16A16_FLOAT },// t5 GBuffer_SpecularIn (corrupted by NRD)
            { gBuffer.NormalRoughness.Get(),       DXGI_FORMAT_R8G8B8A8_UNORM },     // t6 GBuffer_NormalRoughness
            { gBuffer.ViewZ.Get(),                 DXGI_FORMAT_R32_FLOAT },          // t7 GBuffer_ViewZ
            { gBuffer.MotionVectors.Get(),         DXGI_FORMAT_R16G16_FLOAT },       // t8 GBuffer_MotionVectors
            { gBuffer.ShadowData.Get(),            DXGI_FORMAT_R16G16_FLOAT },       // t9 GBuffer_ShadowData
            { gBuffer.RawSpecularBackup.Get(),     DXGI_FORMAT_R16G16B16A16_FLOAT }  // t10 RawSpecularBackup (copy before NRD)
        };

        for (const auto& srv : srvs)
        {
            srvDesc.Format = srv.format;
            device->CreateShaderResourceView(srv.resource, &srvDesc, cpuHandle);
            cpuHandle.Offset(descriptorSize);
        }
        
        // Store SRV table GPU handle
        CD3DX12_GPU_DESCRIPTOR_HANDLE srvTableHandle = gpuHandle;
        gpuHandle.Offset(11, descriptorSize); // Move past 11 SRVs (t0-t10)
        cpuHandle = CD3DX12_CPU_DESCRIPTOR_HANDLE(compositeDescriptorHeap->GetCPUDescriptorHandleForHeapStart());
        cpuHandle.Offset(11, descriptorSize); // UAV is at index 11, after 11 SRVs (t0-t10)
        
        // u0: Output UAV (render target)
        D3D12_UNORDERED_ACCESS_VIEW_DESC uavDesc = {};
        uavDesc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        uavDesc.Texture2D.MipSlice = 0;
        uavDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; // Render target format
        device->CreateUnorderedAccessView(renderTarget->GetResource(), nullptr, &uavDesc, cpuHandle);
        
        CD3DX12_GPU_DESCRIPTOR_HANDLE uavTableHandle(compositeDescriptorHeap->GetGPUDescriptorHandleForHeapStart());
        uavTableHandle.Offset(11, descriptorSize);
        
        // Set pipeline state
        commandList->SetPipelineState(compositePipelineState.Get());
        commandList->SetComputeRootSignature(compositeRootSignature.Get());
        
        // Set descriptor heap
        ID3D12DescriptorHeap* heaps[] = { compositeDescriptorHeap.Get() };
        commandList->SetDescriptorHeaps(1, heaps);
        
        // Set root parameters
        commandList->SetComputeRootDescriptorTable(0, srvTableHandle);  // SRVs
        commandList->SetComputeRootDescriptorTable(1, uavTableHandle);  // UAV
        
        // Set constants: OutputSize (2 uints), ExposureValue, ToneMapOperator, DebugMode, DebugTileScale, UseDenoisedShadow, ShadowStrength
        // Shadow source: 0 = InputShadow(t9/noisy), 1 = DenoisedShadow(t3/SIGMA), 2 = No shadow (debug)
        UINT forceUseDenoisedShadow = 1;  // Enable SIGMA denoised shadow
        
        struct CompositeConstants {
            UINT width;
            UINT height;
            float exposureValue;
            float toneMapOperator;
            UINT debugMode;          // 0=off, 6=diffuse only, 7=diffuse*albedo, 8=raw input
            float debugTileScale;
            UINT useDenoisedShadow;
            float shadowStrength;
        } constants = { width, height, exposure, (float)toneMapOperator, 0, 0.15f, forceUseDenoisedShadow, shadowStrength }; // debugMode=1: show debug tiles
        
        char compositeBuf[256];
        sprintf_s(compositeBuf, "CompositeOutput: shadowStrength=%.2f, useDenoisedShadow=%u", constants.shadowStrength, constants.useDenoisedShadow);
        LogToFile(compositeBuf);
        
        commandList->SetComputeRoot32BitConstants(2, sizeof(constants) / 4, &constants, 0);
        
        // Dispatch composite shader
        UINT dispatchX = (width + 7) / 8;
        UINT dispatchY = (height + 7) / 8;
        
        commandList->Dispatch(dispatchX, dispatchY, 1);
        
        // Transition resources back to UAV state for next frame's NRD pass
        D3D12_RESOURCE_BARRIER backToUavBarriers[4] = {};
        backToUavBarriers[0].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        backToUavBarriers[0].Transition.pResource = output.DiffuseRadiance.Get();
        backToUavBarriers[0].Transition.StateBefore = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        backToUavBarriers[0].Transition.StateAfter = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        backToUavBarriers[0].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        
        backToUavBarriers[1].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        backToUavBarriers[1].Transition.pResource = output.SpecularRadiance.Get();
        backToUavBarriers[1].Transition.StateBefore = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        backToUavBarriers[1].Transition.StateAfter = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        backToUavBarriers[1].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        
        backToUavBarriers[2].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        backToUavBarriers[2].Transition.pResource = output.DenoisedShadow.Get();
        backToUavBarriers[2].Transition.StateBefore = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        backToUavBarriers[2].Transition.StateAfter = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        backToUavBarriers[2].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        
        backToUavBarriers[3].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        backToUavBarriers[3].Transition.pResource = gBuffer.Albedo.Get();
        backToUavBarriers[3].Transition.StateBefore = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        backToUavBarriers[3].Transition.StateAfter = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        backToUavBarriers[3].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        
        commandList->ResourceBarrier(4, backToUavBarriers);
        
        LogToFile("CompositeOutput: dispatched composite shader");
    }
}


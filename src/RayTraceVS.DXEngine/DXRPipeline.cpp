#include "DXRPipeline.h"
#include "DXContext.h"
#include "RenderTarget.h"
#include "AccelerationStructure.h"
#include "Scene/Scene.h"
#include "Scene/Camera.h"
#include "Scene/Light.h"
#include "Scene/Objects/Sphere.h"
#include "Scene/Objects/Plane.h"
#include "Scene/Objects/Cylinder.h"
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

    bool DXRPipeline::Initialize()
    {
        // Clear log file
        std::ofstream log("C:\\git\\RayTraceVS\\debug_log.txt", std::ios::trunc);
        log.close();
        
        LogToFile("DXRPipeline::Initialize called");
        
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

        // Get executable path
        wchar_t exePath[MAX_PATH];
        GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        std::wstring exeDir(exePath);
        size_t lastSlash = exeDir.find_last_of(L"\\/");
        if (lastSlash != std::wstring::npos)
            exeDir = exeDir.substr(0, lastSlash + 1);

        // Load compute shader
        // Try to load precompiled shader first
        std::wstring csoPath = exeDir + L"RayTraceCompute.cso";
        LogToFile("Loading shader from cso path");
        HRESULT hr = D3DReadFileToBlob(csoPath.c_str(), &computeShader);
        
        if (FAILED(hr))
        {
            OutputDebugStringA("Failed to load RayTraceCompute.cso, trying runtime compilation...\n");
            
            // Try to compile from source - check multiple paths
            std::wstring shaderPaths[] = {
                exeDir + L"Shaders\\RayTraceCompute.hlsl",
                L"Shaders\\RayTraceCompute.hlsl",
                L"..\\..\\..\\src\\RayTraceVS.DXEngine\\Shaders\\RayTraceCompute.hlsl"
            };
            
            ComPtr<ID3DBlob> errorBlob;
            bool compiled = false;
            
            for (const auto& path : shaderPaths)
            {
                hr = D3DCompileFromFile(
                    path.c_str(),
                    nullptr,
                    D3D_COMPILE_STANDARD_FILE_INCLUDE,
                    "CSMain",
                    "cs_5_1",
                    D3DCOMPILE_OPTIMIZATION_LEVEL3 | D3DCOMPILE_DEBUG,
                    0,
                    &computeShader,
                    &errorBlob);

                if (SUCCEEDED(hr))
                {
                    compiled = true;
                    OutputDebugStringW((L"Compiled shader from: " + path + L"\n").c_str());
                    break;
                }
                
                if (errorBlob)
                {
                    OutputDebugStringA("Shader compile error: ");
                    OutputDebugStringA((char*)errorBlob->GetBufferPointer());
                    errorBlob.Reset();
                }
            }
            
            if (!compiled)
            {
                OutputDebugStringA("Failed to compile compute shader from any path\n");
                return false;
            }
        }
        else
        {
            LogToFile("Loaded precompiled RayTraceCompute.cso");
        }

        LogToFile("Creating root signature...");
        
        // Create root signature
        // Root parameters:
        // 0: CBV - Scene constants
        // 1: UAV - Output texture
        // 2: SRV - Spheres buffer
        // 3: SRV - Planes buffer
        // 4: SRV - Cylinders buffer
        // 5: SRV - Lights buffer
        CD3DX12_DESCRIPTOR_RANGE1 ranges[6];
        ranges[0].Init(D3D12_DESCRIPTOR_RANGE_TYPE_CBV, 1, 0);  // b0
        ranges[1].Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 0);  // u0
        ranges[2].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 0);  // t0 - Spheres
        ranges[3].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 1);  // t1 - Planes
        ranges[4].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 2);  // t2 - Cylinders
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
        const UINT maxCylinders = 32;
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

        // Create cylinder buffer
        UINT64 cylinderBufferSize = sizeof(GPUCylinder) * maxCylinders;
        CD3DX12_RESOURCE_DESC cylinderDesc = CD3DX12_RESOURCE_DESC::Buffer(cylinderBufferSize);
        
        device->CreateCommittedResource(&defaultHeapProps, D3D12_HEAP_FLAG_NONE, &cylinderDesc,
            D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&cylinderBuffer));
        device->CreateCommittedResource(&uploadHeapProps, D3D12_HEAP_FLAG_NONE, &cylinderDesc,
            D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&cylinderUploadBuffer));

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

        // Get objects from scene
        const auto& objects = scene->GetObjects();
        const auto& lights = scene->GetLights();

        std::vector<GPUSphere> spheres;
        std::vector<GPUPlane> planes;
        std::vector<GPUCylinder> cylinders;
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
                gp.Padding1 = 0;
                gp.Padding2 = 0;
                planes.push_back(gp);
            }
            else if (auto cyl = dynamic_cast<Cylinder*>(obj.get()))
            {
                GPUCylinder gc;
                gc.Position = cyl->GetPosition();
                gc.Radius = cyl->GetRadius();
                gc.Axis = cyl->GetAxis();
                gc.Height = cyl->GetHeight();
                const Material& mat = cyl->GetMaterial();
                gc.Color = mat.color;
                gc.Metallic = mat.metallic;
                gc.Roughness = mat.roughness;
                gc.Transmission = mat.transmission;
                gc.IOR = mat.ior;
                cylinders.push_back(gc);
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
            gl.Padding = XMFLOAT3(0, 0, 0);
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
        mappedConstantData->NumCylinders = (UINT)cylinders.size();
        mappedConstantData->NumLights = (UINT)gpuLights.size();


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

        if (!cylinders.empty() && cylinderUploadBuffer)
        {
            void* mapped = nullptr;
            cylinderUploadBuffer->Map(0, nullptr, &mapped);
            memcpy(mapped, cylinders.data(), sizeof(GPUCylinder) * cylinders.size());
            cylinderUploadBuffer->Unmap(0, nullptr);

            commandList->CopyResource(cylinderBuffer.Get(), cylinderUploadBuffer.Get());
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

        // SRV for cylinders
        srvDesc.Buffer.NumElements = 32;
        srvDesc.Buffer.StructureByteStride = sizeof(GPUCylinder);
        device->CreateShaderResourceView(cylinderBuffer.Get(), &srvDesc, cpuHandle);
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
        commandList->SetComputeRootDescriptorTable(4, gpuHandle);  // Cylinders
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
        // [5] SRV - Cylinders buffer (t3)
        // [6] SRV - Lights buffer (t4)
        
        CD3DX12_DESCRIPTOR_RANGE1 ranges[7];
        ranges[0].Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 0);  // u0 - Output
        ranges[1].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 0);  // t0 - TLAS
        ranges[2].Init(D3D12_DESCRIPTOR_RANGE_TYPE_CBV, 1, 0);  // b0 - Constants
        ranges[3].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 1);  // t1 - Spheres
        ranges[4].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 2);  // t2 - Planes
        ranges[5].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 3);  // t3 - Cylinders
        ranges[6].Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 4);  // t4 - Lights

        CD3DX12_ROOT_PARAMETER1 rootParameters[7];
        for (int i = 0; i < 7; i++)
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

    bool DXRPipeline::CreateDXRStateObject()
    {
        auto device = dxContext->GetDevice();
        
        // Get executable directory for precompiled shaders
        wchar_t exePath[MAX_PATH];
        GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        std::wstring exeDir(exePath);
        size_t lastSlash = exeDir.find_last_of(L"\\/");
        if (lastSlash != std::wstring::npos)
            exeDir = exeDir.substr(0, lastSlash + 1);
        
        // Try multiple paths for precompiled shaders (.cso files)
        std::wstring shaderPaths[] = {
            exeDir,                                              // Same directory as exe (WPF output)
            L"",                                                 // Current directory
            L"..\\..\\..\\bin\\Debug\\",                        // Solution bin/Debug
            L"..\\..\\..\\bin\\Release\\"                       // Solution bin/Release
        };
        
        bool shadersLoaded = false;
        for (const auto& path : shaderPaths)
        {
            LogToFile(("Trying to load DXR shaders from: " + std::string(path.begin(), path.end())).c_str());
            
            // Load precompiled shaders (.cso files built by Visual Studio)
            if (LoadPrecompiledShader(path + L"RayGen.cso", &rayGenShader) &&
                LoadPrecompiledShader(path + L"Miss.cso", &missShader) &&
                LoadPrecompiledShader(path + L"ClosestHit.cso", &closestHitShader) &&
                LoadPrecompiledShader(path + L"Intersection.cso", &intersectionShader))
            {
                shadersLoaded = true;
                LogToFile("Successfully loaded precompiled DXR shaders");
                break;
            }
            
            // Reset any partially loaded shaders
            rayGenShader.Reset();
            missShader.Reset();
            closestHitShader.Reset();
            intersectionShader.Reset();
        }
        
        if (!shadersLoaded)
        {
            LogToFile("Failed to load precompiled DXR shaders from any path");
            return false;
        }
        
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
        
        // Hit group for procedural geometry
        auto hitGroup = stateObjectDesc.CreateSubobject<CD3DX12_HIT_GROUP_SUBOBJECT>();
        hitGroup->SetHitGroupExport(L"HitGroup");
        hitGroup->SetClosestHitShaderImport(L"ClosestHit");
        hitGroup->SetIntersectionShaderImport(L"SphereIntersection");
        hitGroup->SetHitGroupType(D3D12_HIT_GROUP_TYPE_PROCEDURAL_PRIMITIVE);
        
        // Shader config
        auto shaderConfig = stateObjectDesc.CreateSubobject<CD3DX12_RAYTRACING_SHADER_CONFIG_SUBOBJECT>();
        // RayPayload: float3 color (12) + uint depth (4) + bool hit (4 in HLSL) = 20 bytes
        UINT payloadSize = 12 + 4 + 4;  // 20 bytes
        // ProceduralAttributes: float3 normal (12) + uint objectType (4) + uint objectIndex (4) = 20 bytes
        UINT attribSize = 12 + 4 + 4;   // 20 bytes
        shaderConfig->Config(payloadSize, attribSize);
        
        // Global root signature
        auto globalRS = stateObjectDesc.CreateSubobject<CD3DX12_GLOBAL_ROOT_SIGNATURE_SUBOBJECT>();
        globalRS->SetRootSignature(globalRootSignature.Get());
        
        // Pipeline config
        auto pipelineConfig = stateObjectDesc.CreateSubobject<CD3DX12_RAYTRACING_PIPELINE_CONFIG_SUBOBJECT>();
        pipelineConfig->Config(31);  // Max recursion depth (hardware maximum for quality)
        
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
        
        // Need descriptors for: UAV, TLAS SRV, CBV, 4 SRVs (spheres, planes, cylinders, lights)
        D3D12_DESCRIPTOR_HEAP_DESC heapDesc = {};
        heapDesc.NumDescriptors = 7;
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
        
        if (!rayGenId || !missId || !hitGroupId)
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
        
        // Hit group shader table
        {
            CD3DX12_RESOURCE_DESC desc = CD3DX12_RESOURCE_DESC::Buffer(shaderTableRecordSize);
            device->CreateCommittedResource(&uploadHeapProps, D3D12_HEAP_FLAG_NONE, &desc,
                D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&hitGroupShaderTable));
            
            void* mapped = nullptr;
            hitGroupShaderTable->Map(0, nullptr, &mapped);
            memcpy(mapped, hitGroupId, shaderIdSize);
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
        
        // Cylinders
        bufferSrvDesc.Buffer.StructureByteStride = sizeof(GPUCylinder);
        device->CreateShaderResourceView(cylinderBuffer.Get(), &bufferSrvDesc, cpuHandle);
        cpuHandle.Offset(1, dxrDescriptorSize);
        
        // Lights
        bufferSrvDesc.Buffer.NumElements = 8;
        bufferSrvDesc.Buffer.StructureByteStride = sizeof(GPULight);
        device->CreateShaderResourceView(lightBuffer.Get(), &bufferSrvDesc, cpuHandle);
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
        
        // Set root descriptor tables
        LogToFile("RenderWithDXR: setting descriptor tables");
        CD3DX12_GPU_DESCRIPTOR_HANDLE gpuHandle(dxrSrvUavHeap->GetGPUDescriptorHandleForHeapStart());
        for (int i = 0; i < 7; i++)
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
        dispatchDesc.HitGroupTable.SizeInBytes = shaderTableRecordSize;
        dispatchDesc.HitGroupTable.StrideInBytes = shaderTableRecordSize;
        
        dispatchDesc.Width = width;
        dispatchDesc.Height = height;
        dispatchDesc.Depth = 1;
        
        LogToFile("RenderWithDXR: dispatching rays");
        commandList->SetPipelineState1(stateObject.Get());
        commandList->DispatchRays(&dispatchDesc);
        LogToFile("RenderWithDXR: dispatch complete");
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
}

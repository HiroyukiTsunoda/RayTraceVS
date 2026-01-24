#include "NRDDenoiser.h"
#include "../DXContext.h"
#include "../d3dx12.h"
#include "../DebugLog.h"
#include <d3dcompiler.h>
#include <fstream>
#include <cstdio>

namespace RayTraceVS::DXEngine
{
    namespace
    {
        D3D12_RESOURCE_BARRIER MakeTransition(ID3D12Resource* resource,
            D3D12_RESOURCE_STATES beforeState,
            D3D12_RESOURCE_STATES afterState)
        {
            D3D12_RESOURCE_BARRIER barrier = {};
            barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            barrier.Transition.pResource = resource;
            barrier.Transition.StateBefore = beforeState;
            barrier.Transition.StateAfter = afterState;
            barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            return barrier;
        }
    }
    NRDDenoiser::NRDDenoiser(DXContext* context)
        : m_dxContext(context)
    {
    }

    NRDDenoiser::~NRDDenoiser()
    {
        DestroyResources();
    }

    bool NRDDenoiser::Initialize(UINT width, UINT height)
    {
        m_width = width;
        m_height = height;

        char buf[256];
        sprintf_s(buf, "NRDDenoiser::Initialize - NRD_ENABLED=%d, width=%u, height=%u", NRD_ENABLED, width, height);
        LOG_DEBUG(buf);

#if NRD_ENABLED
        LOG_DEBUG("NRDDenoiser::Initialize - NRD path active, creating instance...");
        
        // Full NRD initialization - requires NRD library to be built
        // See README for instructions on building NRD with CMake
        
        // Create NRD instance with REBLUR_DIFFUSE_SPECULAR and SIGMA_SHADOW denoisers
        nrd::DenoiserDesc denoiserDescs[2] = {};
        
        // REBLUR for diffuse/specular denoising
        denoiserDescs[0].identifier = m_reblurIdentifier;
        denoiserDescs[0].denoiser = nrd::Denoiser::REBLUR_DIFFUSE_SPECULAR;
        
        // SIGMA for shadow denoising
        denoiserDescs[1].identifier = m_sigmaIdentifier;
        denoiserDescs[1].denoiser = nrd::Denoiser::SIGMA_SHADOW;

        nrd::InstanceCreationDesc instanceDesc = {};
        instanceDesc.denoisers = denoiserDescs;
        instanceDesc.denoisersNum = m_sigmaEnabled ? 2 : 1;

        nrd::Result result = nrd::CreateInstance(instanceDesc, m_nrdInstance);
        if (result != nrd::Result::SUCCESS)
        {
            sprintf_s(buf, "NRD: Failed to create instance, result=%d", (int)result);
            LOG_DEBUG(buf);
            return false;
        }
        sprintf_s(buf, "NRD: Instance created successfully with %d denoisers (REBLUR + %s)", 
            instanceDesc.denoisersNum, m_sigmaEnabled ? "SIGMA" : "none");
        LOG_DEBUG(buf);
#else
        LOG_DEBUG("NRDDenoiser::Initialize - NRD_ENABLED=0, stub mode");
#endif

        // Create resources (always needed for G-Buffer output)
        if (!CreateDescriptorHeaps())
            return false;

        if (!CreateGBufferResources())
            return false;

        if (!CreateOutputResources())
            return false;

#if NRD_ENABLED
        if (!CreateNRDResources())
            return false;

        if (!CreateConstantBuffer())
            return false;

        if (!CreateSamplers())
            return false;
#endif

        m_initialized = true;
#if NRD_ENABLED
        OutputDebugStringW(L"NRD: Denoiser initialized (NRD enabled)\n");
#else
        OutputDebugStringW(L"NRD: Denoiser initialized (stub mode - NRD library not linked)\n");
#endif
        return true;
    }

    bool NRDDenoiser::Resize(UINT width, UINT height)
    {
        if (m_width == width && m_height == height)
            return true;

        DestroyResources();
        return Initialize(width, height);
    }

    bool NRDDenoiser::CreateDescriptorHeaps()
    {
        auto device = m_dxContext->GetDevice();
        m_descriptorSize = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);

        // Create main descriptor heap for UAVs/SRVs
        // Need descriptors for: 5 G-Buffer UAVs + 2 Output UAVs + NRD internal textures (up to 32)
        D3D12_DESCRIPTOR_HEAP_DESC heapDesc = {};
        heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        heapDesc.NumDescriptors = 64;
        heapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;

        HRESULT hr = device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&m_descriptorHeap));
        if (FAILED(hr))
        {
            OutputDebugStringW(L"NRD: Failed to create descriptor heap\n");
            return false;
        }

        return true;
    }

    bool NRDDenoiser::CreateGBufferResources()
    {
        auto device = m_dxContext->GetDevice();

        // Helper lambda to create a texture resource
        auto CreateTexture = [&](ComPtr<ID3D12Resource>& resource, DXGI_FORMAT format, const wchar_t* name) -> bool
        {
            D3D12_RESOURCE_DESC desc = {};
            desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
            desc.Width = m_width;
            desc.Height = m_height;
            desc.DepthOrArraySize = 1;
            desc.MipLevels = 1;
            desc.Format = format;
            desc.SampleDesc.Count = 1;
            desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;

            D3D12_HEAP_PROPERTIES heapProps = {};
            heapProps.Type = D3D12_HEAP_TYPE_DEFAULT;

            HRESULT hr = device->CreateCommittedResource(
                &heapProps,
                D3D12_HEAP_FLAG_NONE,
                &desc,
                D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                nullptr,
                IID_PPV_ARGS(&resource));

            if (FAILED(hr))
            {
                OutputDebugStringW((std::wstring(L"NRD: Failed to create texture: ") + name + L"\n").c_str());
                return false;
            }

            resource->SetName(name);
            m_resourceStates[resource.Get()] = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
            return true;
        };

        // Create G-Buffer textures
        if (!CreateTexture(m_gBuffer.DiffuseRadianceHitDist, DXGI_FORMAT_R16G16B16A16_FLOAT, L"GBuffer_DiffuseRadianceHitDist"))
            return false;

        if (!CreateTexture(m_gBuffer.SpecularRadianceHitDist, DXGI_FORMAT_R16G16B16A16_FLOAT, L"GBuffer_SpecularRadianceHitDist"))
            return false;

        // Create backup buffer for raw specular (before NRD processing)
        // This is needed because NRD corrupts the original SpecularRadianceHitDist
        if (!CreateTexture(m_gBuffer.RawSpecularBackup, DXGI_FORMAT_R16G16B16A16_FLOAT, L"GBuffer_RawSpecularBackup"))
            return false;

        // Create backup buffer for raw diffuse (before NRD processing)
        // This is needed to preserve point light illumination which NRD may smooth out
        if (!CreateTexture(m_gBuffer.RawDiffuseBackup, DXGI_FORMAT_R16G16B16A16_FLOAT, L"GBuffer_RawDiffuseBackup"))
            return false;

        if (!CreateTexture(m_gBuffer.NormalRoughness, DXGI_FORMAT_R8G8B8A8_UNORM, L"GBuffer_NormalRoughness"))
            return false;

        if (!CreateTexture(m_gBuffer.ViewZ, DXGI_FORMAT_R32_FLOAT, L"GBuffer_ViewZ"))
            return false;

        if (!CreateTexture(m_gBuffer.MotionVectors, DXGI_FORMAT_R16G16_FLOAT, L"GBuffer_MotionVectors"))
            return false;
        if (!CreateTexture(m_gBuffer.Albedo, DXGI_FORMAT_R8G8B8A8_UNORM, L"GBuffer_Albedo"))
            return false;

        // Create SIGMA shadow G-Buffer textures
        if (!CreateTexture(m_gBuffer.ShadowData, DXGI_FORMAT_R16G16_FLOAT, L"GBuffer_ShadowData"))
            return false;
        if (!CreateTexture(m_gBuffer.ShadowTranslucency, DXGI_FORMAT_R16G16B16A16_FLOAT, L"GBuffer_ShadowTranslucency"))
            return false;
        
        // Create Object ID buffer for custom shadow denoiser
        if (!CreateTexture(m_gBuffer.ObjectID, DXGI_FORMAT_R32_UINT, L"GBuffer_ObjectID"))
            return false;

        // Create UAV descriptors for G-Buffer
        CD3DX12_CPU_DESCRIPTOR_HANDLE cpuHandle(m_descriptorHeap->GetCPUDescriptorHandleForHeapStart());

        D3D12_UNORDERED_ACCESS_VIEW_DESC uavDesc = {};
        uavDesc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        uavDesc.Texture2D.MipSlice = 0;

        // UAV 0: DiffuseRadianceHitDist
        uavDesc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        device->CreateUnorderedAccessView(m_gBuffer.DiffuseRadianceHitDist.Get(), nullptr, &uavDesc, cpuHandle);
        cpuHandle.Offset(m_descriptorSize);

        // UAV 1: SpecularRadianceHitDist
        device->CreateUnorderedAccessView(m_gBuffer.SpecularRadianceHitDist.Get(), nullptr, &uavDesc, cpuHandle);
        cpuHandle.Offset(m_descriptorSize);

        // UAV 2: NormalRoughness
        uavDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        device->CreateUnorderedAccessView(m_gBuffer.NormalRoughness.Get(), nullptr, &uavDesc, cpuHandle);
        cpuHandle.Offset(m_descriptorSize);

        // UAV 3: ViewZ
        uavDesc.Format = DXGI_FORMAT_R32_FLOAT;
        device->CreateUnorderedAccessView(m_gBuffer.ViewZ.Get(), nullptr, &uavDesc, cpuHandle);
        cpuHandle.Offset(m_descriptorSize);

        // UAV 4: MotionVectors
        uavDesc.Format = DXGI_FORMAT_R16G16_FLOAT;
        device->CreateUnorderedAccessView(m_gBuffer.MotionVectors.Get(), nullptr, &uavDesc, cpuHandle);
        cpuHandle.Offset(m_descriptorSize);

        // UAV 5: ShadowData (for SIGMA)
        device->CreateUnorderedAccessView(m_gBuffer.ShadowData.Get(), nullptr, &uavDesc, cpuHandle);
        cpuHandle.Offset(m_descriptorSize);

        // UAV 6: ShadowTranslucency (for SIGMA)
        uavDesc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        device->CreateUnorderedAccessView(m_gBuffer.ShadowTranslucency.Get(), nullptr, &uavDesc, cpuHandle);
        cpuHandle.Offset(m_descriptorSize);
        
        // UAV 7: ObjectID (for custom shadow denoiser)
        uavDesc.Format = DXGI_FORMAT_R32_UINT;
        device->CreateUnorderedAccessView(m_gBuffer.ObjectID.Get(), nullptr, &uavDesc, cpuHandle);

        return true;
    }

    bool NRDDenoiser::CreateOutputResources()
    {
        auto device = m_dxContext->GetDevice();

        auto CreateTexture = [&](ComPtr<ID3D12Resource>& resource, DXGI_FORMAT format, const wchar_t* name) -> bool
        {
            D3D12_RESOURCE_DESC desc = {};
            desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
            desc.Width = m_width;
            desc.Height = m_height;
            desc.DepthOrArraySize = 1;
            desc.MipLevels = 1;
            desc.Format = format;
            desc.SampleDesc.Count = 1;
            desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;

            D3D12_HEAP_PROPERTIES heapProps = {};
            heapProps.Type = D3D12_HEAP_TYPE_DEFAULT;

            HRESULT hr = device->CreateCommittedResource(
                &heapProps,
                D3D12_HEAP_FLAG_NONE,
                &desc,
                D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                nullptr,
                IID_PPV_ARGS(&resource));

            if (FAILED(hr))
                return false;

            resource->SetName(name);
            m_resourceStates[resource.Get()] = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
            return true;
        };

        if (!CreateTexture(m_output.DiffuseRadiance, DXGI_FORMAT_R16G16B16A16_FLOAT, L"Output_DiffuseRadiance"))
            return false;

        if (!CreateTexture(m_output.SpecularRadiance, DXGI_FORMAT_R16G16B16A16_FLOAT, L"Output_SpecularRadiance"))
            return false;

        // Create denoised shadow output (for SIGMA)
        // IMPORTANT: SIGMA OUT_SHADOW_TRANSLUCENCY requires RGBA16F (4 channels)
        if (!CreateTexture(m_output.DenoisedShadow, DXGI_FORMAT_R16G16B16A16_FLOAT, L"Output_DenoisedShadow"))
            return false;

        // Create UAV descriptors for output (starting at index 7, after G-Buffer UAVs 0-6)
        auto device2 = m_dxContext->GetDevice();
        CD3DX12_CPU_DESCRIPTOR_HANDLE cpuHandle(m_descriptorHeap->GetCPUDescriptorHandleForHeapStart());
        cpuHandle.Offset(7, m_descriptorSize);

        D3D12_UNORDERED_ACCESS_VIEW_DESC uavDesc = {};
        uavDesc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        uavDesc.Texture2D.MipSlice = 0;
        uavDesc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;

        // UAV 7: DiffuseRadiance output
        device2->CreateUnorderedAccessView(m_output.DiffuseRadiance.Get(), nullptr, &uavDesc, cpuHandle);
        cpuHandle.Offset(m_descriptorSize);

        // UAV 8: SpecularRadiance output
        device2->CreateUnorderedAccessView(m_output.SpecularRadiance.Get(), nullptr, &uavDesc, cpuHandle);
        cpuHandle.Offset(m_descriptorSize);

        // UAV 9: DenoisedShadow output (for SIGMA) - RGBA16F required
        uavDesc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        device2->CreateUnorderedAccessView(m_output.DenoisedShadow.Get(), nullptr, &uavDesc, cpuHandle);

        return true;
    }

#if NRD_ENABLED
    bool NRDDenoiser::CreateNRDResources()
    {
        if (!m_nrdInstance)
            return false;

        auto device = m_dxContext->GetDevice();
        const nrd::InstanceDesc& instanceDesc = nrd::GetInstanceDesc(*m_nrdInstance);

        char buf[256];
        sprintf_s(buf, "NRD: Creating resources - permanentPoolSize=%u, transientPoolSize=%u", 
            instanceDesc.permanentPoolSize, instanceDesc.transientPoolSize);
        LOG_DEBUG(buf);

        // Create NRD internal textures
        m_nrdTextures.resize(instanceDesc.permanentPoolSize + instanceDesc.transientPoolSize);

        for (uint32_t i = 0; i < instanceDesc.permanentPoolSize; i++)
        {
            const nrd::TextureDesc& texDesc = instanceDesc.permanentPool[i];
            
            // Calculate dimensions with downsample factor
            UINT width = m_width;
            UINT height = m_height;
            if (texDesc.downsampleFactor > 1)
            {
                width = (m_width + texDesc.downsampleFactor - 1) / texDesc.downsampleFactor;
                height = (m_height + texDesc.downsampleFactor - 1) / texDesc.downsampleFactor;
            }
            
            D3D12_RESOURCE_DESC desc = {};
            desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
            desc.Width = width;
            desc.Height = height;
            desc.DepthOrArraySize = 1;
            desc.MipLevels = 1;
            desc.Format = GetDXGIFormat(texDesc.format);
            desc.SampleDesc.Count = 1;
            desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;

            D3D12_HEAP_PROPERTIES heapProps = {};
            heapProps.Type = D3D12_HEAP_TYPE_DEFAULT;

            HRESULT hr = device->CreateCommittedResource(
                &heapProps,
                D3D12_HEAP_FLAG_NONE,
                &desc,
                D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                nullptr,
                IID_PPV_ARGS(&m_nrdTextures[i]));

            if (FAILED(hr))
            {
                OutputDebugStringW(L"NRD: Failed to create permanent pool texture\n");
                return false;
            }
            m_resourceStates[m_nrdTextures[i].Get()] = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        }

        for (uint32_t i = 0; i < instanceDesc.transientPoolSize; i++)
        {
            const nrd::TextureDesc& texDesc = instanceDesc.transientPool[i];
            uint32_t idx = instanceDesc.permanentPoolSize + i;

            UINT width = m_width;
            UINT height = m_height;
            if (texDesc.downsampleFactor > 1)
            {
                width = (m_width + texDesc.downsampleFactor - 1) / texDesc.downsampleFactor;
                height = (m_height + texDesc.downsampleFactor - 1) / texDesc.downsampleFactor;
            }

            D3D12_RESOURCE_DESC desc = {};
            desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
            desc.Width = width;
            desc.Height = height;
            desc.DepthOrArraySize = 1;
            desc.MipLevels = 1;
            desc.Format = GetDXGIFormat(texDesc.format);
            desc.SampleDesc.Count = 1;
            desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;

            D3D12_HEAP_PROPERTIES heapProps = {};
            heapProps.Type = D3D12_HEAP_TYPE_DEFAULT;

            HRESULT hr = device->CreateCommittedResource(
                &heapProps,
                D3D12_HEAP_FLAG_NONE,
                &desc,
                D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                nullptr,
                IID_PPV_ARGS(&m_nrdTextures[idx]));

            if (FAILED(hr))
            {
                OutputDebugStringW(L"NRD: Failed to create transient pool texture\n");
                return false;
            }
            m_resourceStates[m_nrdTextures[idx].Get()] = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        }

        // Create NRD descriptor heap for dispatch resources
        // Each dispatch needs SRVs + UAVs, and we may have multiple dispatches per frame
        // SIGMA + REBLUR can have up to 50+ dispatches, so use 64 to be safe
        uint32_t maxDispatches = 64;
        uint32_t descriptorsPerDispatch = instanceDesc.descriptorPoolDesc.perSetTexturesMaxNum + 
                                          instanceDesc.descriptorPoolDesc.perSetStorageTexturesMaxNum;
        
        D3D12_DESCRIPTOR_HEAP_DESC nrdHeapDesc = {};
        nrdHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        nrdHeapDesc.NumDescriptors = maxDispatches * descriptorsPerDispatch;
        nrdHeapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;

        HRESULT hr = device->CreateDescriptorHeap(&nrdHeapDesc, IID_PPV_ARGS(&m_nrdDescriptorHeap));
        if (FAILED(hr))
        {
            OutputDebugStringW(L"NRD: Failed to create NRD descriptor heap\n");
            return false;
        }

        // Create NRD pipelines after resources
        if (!CreateNRDPipelines())
        {
            OutputDebugStringW(L"NRD: Failed to create pipelines\n");
            return false;
        }

        return true;
    }

    bool NRDDenoiser::CreateNRDPipelines()
    {
        if (!m_nrdInstance)
            return false;

        auto device = m_dxContext->GetDevice();
        const nrd::InstanceDesc& instanceDesc = nrd::GetInstanceDesc(*m_nrdInstance);

        // Create root signature for NRD shaders
        // NRD uses: CBV (b0), Samplers (s0-s1), SRVs (t0-tN), UAVs (u0-uN)
        {
            // Calculate max descriptors needed
            uint32_t maxSRVs = instanceDesc.descriptorPoolDesc.perSetTexturesMaxNum;
            uint32_t maxUAVs = instanceDesc.descriptorPoolDesc.perSetStorageTexturesMaxNum;

            std::vector<CD3DX12_ROOT_PARAMETER1> rootParams;
            std::vector<CD3DX12_DESCRIPTOR_RANGE1> srvRanges;
            std::vector<CD3DX12_DESCRIPTOR_RANGE1> uavRanges;

            // Root param 0: Constant buffer (CBV)
            rootParams.push_back({});
            rootParams.back().InitAsConstantBufferView(
                instanceDesc.constantBufferRegisterIndex,
                instanceDesc.constantBufferAndResourcesSpaceIndex,
                D3D12_ROOT_DESCRIPTOR_FLAG_DATA_STATIC);

            // Root param 1: SRV descriptor table
            if (maxSRVs > 0)
            {
                srvRanges.push_back({});
                srvRanges.back().Init(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, maxSRVs, 
                    instanceDesc.resourcesBaseRegisterIndex, 
                    instanceDesc.constantBufferAndResourcesSpaceIndex);
                rootParams.push_back({});
                rootParams.back().InitAsDescriptorTable(1, &srvRanges.back());
            }

            // Root param 2: UAV descriptor table
            if (maxUAVs > 0)
            {
                uavRanges.push_back({});
                uavRanges.back().Init(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, maxUAVs, 
                    instanceDesc.resourcesBaseRegisterIndex, 
                    instanceDesc.constantBufferAndResourcesSpaceIndex);
                rootParams.push_back({});
                rootParams.back().InitAsDescriptorTable(1, &uavRanges.back());
            }

            // Static samplers
            std::vector<D3D12_STATIC_SAMPLER_DESC> staticSamplers(instanceDesc.samplersNum);
            for (uint32_t i = 0; i < instanceDesc.samplersNum; i++)
            {
                staticSamplers[i] = {};
                staticSamplers[i].ShaderRegister = instanceDesc.samplersBaseRegisterIndex + i;
                staticSamplers[i].RegisterSpace = instanceDesc.samplersSpaceIndex;
                staticSamplers[i].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
                staticSamplers[i].AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
                staticSamplers[i].AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
                staticSamplers[i].AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
                staticSamplers[i].MaxLOD = D3D12_FLOAT32_MAX;

                switch (instanceDesc.samplers[i])
                {
                case nrd::Sampler::NEAREST_CLAMP:
                    staticSamplers[i].Filter = D3D12_FILTER_MIN_MAG_MIP_POINT;
                    break;
                case nrd::Sampler::LINEAR_CLAMP:
                default:
                    staticSamplers[i].Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
                    break;
                }
            }

            CD3DX12_VERSIONED_ROOT_SIGNATURE_DESC rootSigDesc;
            rootSigDesc.Init_1_1(
                (UINT)rootParams.size(), rootParams.data(),
                (UINT)staticSamplers.size(), staticSamplers.data(),
                D3D12_ROOT_SIGNATURE_FLAG_NONE);

            ComPtr<ID3DBlob> serializedRootSig;
            ComPtr<ID3DBlob> errorBlob;
            HRESULT hr = D3DX12SerializeVersionedRootSignature(&rootSigDesc, 
                D3D_ROOT_SIGNATURE_VERSION_1_1, &serializedRootSig, &errorBlob);

            if (FAILED(hr))
            {
                if (errorBlob)
                    OutputDebugStringA((char*)errorBlob->GetBufferPointer());
                OutputDebugStringW(L"NRD: Failed to serialize root signature\n");
                return false;
            }

            hr = device->CreateRootSignature(0, 
                serializedRootSig->GetBufferPointer(),
                serializedRootSig->GetBufferSize(),
                IID_PPV_ARGS(&m_nrdRootSignature));

            if (FAILED(hr))
            {
                OutputDebugStringW(L"NRD: Failed to create root signature\n");
                return false;
            }
        }

        // Create PSOs for each NRD pipeline
        for (uint32_t i = 0; i < instanceDesc.pipelinesNum; i++)
        {
            const nrd::PipelineDesc& pipelineDesc = instanceDesc.pipelines[i];

            // Prefer DXIL over DXBC
            const nrd::ComputeShaderDesc* shaderDesc = nullptr;
            if (pipelineDesc.computeShaderDXIL.bytecode && pipelineDesc.computeShaderDXIL.size > 0)
                shaderDesc = &pipelineDesc.computeShaderDXIL;
            else if (pipelineDesc.computeShaderDXBC.bytecode && pipelineDesc.computeShaderDXBC.size > 0)
                shaderDesc = &pipelineDesc.computeShaderDXBC;

            if (!shaderDesc)
            {
                OutputDebugStringW(L"NRD: No shader bytecode available for pipeline\n");
                continue;
            }

            D3D12_COMPUTE_PIPELINE_STATE_DESC psoDesc = {};
            psoDesc.pRootSignature = m_nrdRootSignature.Get();
            psoDesc.CS.pShaderBytecode = shaderDesc->bytecode;
            psoDesc.CS.BytecodeLength = shaderDesc->size;

            ComPtr<ID3D12PipelineState> pso;
            HRESULT hr = device->CreateComputePipelineState(&psoDesc, IID_PPV_ARGS(&pso));
            if (FAILED(hr))
            {
                wchar_t msg[256];
                swprintf_s(msg, L"NRD: Failed to create PSO for pipeline %d (%S)\n", i, 
                    pipelineDesc.shaderFileName ? pipelineDesc.shaderFileName : "unknown");
                OutputDebugStringW(msg);
                continue;
            }

            m_pipelineStates[i] = pso;
        }

        char buf[128];
        sprintf_s(buf, "NRD: Pipelines created - total PSOs: %zu out of %u pipelines", 
            m_pipelineStates.size(), instanceDesc.pipelinesNum);
        LOG_DEBUG(buf);
        return m_pipelineStates.size() > 0;
    }

    bool NRDDenoiser::CreateConstantBuffer()
    {
        auto device = m_dxContext->GetDevice();

        // Get max constant buffer size from NRD
        const nrd::InstanceDesc& instanceDesc = nrd::GetInstanceDesc(*m_nrdInstance);
        UINT bufferSize = (instanceDesc.constantBufferMaxDataSize + 255) & ~255; // Align to 256 bytes

        D3D12_RESOURCE_DESC desc = {};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        desc.Width = bufferSize;
        desc.Height = 1;
        desc.DepthOrArraySize = 1;
        desc.MipLevels = 1;
        desc.Format = DXGI_FORMAT_UNKNOWN;
        desc.SampleDesc.Count = 1;
        desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

        D3D12_HEAP_PROPERTIES heapProps = {};
        heapProps.Type = D3D12_HEAP_TYPE_UPLOAD;

        HRESULT hr = device->CreateCommittedResource(
            &heapProps,
            D3D12_HEAP_FLAG_NONE,
            &desc,
            D3D12_RESOURCE_STATE_GENERIC_READ,
            nullptr,
            IID_PPV_ARGS(&m_constantBuffer));

        if (FAILED(hr))
        {
            OutputDebugStringW(L"NRD: Failed to create constant buffer\n");
            return false;
        }

        // Map constant buffer
        D3D12_RANGE readRange = { 0, 0 };
        hr = m_constantBuffer->Map(0, &readRange, &m_constantBufferMapped);
        if (FAILED(hr))
        {
            OutputDebugStringW(L"NRD: Failed to map constant buffer\n");
            return false;
        }

        return true;
    }

    bool NRDDenoiser::CreateSamplers()
    {
        auto device = m_dxContext->GetDevice();
        
        // Create sampler heap
        D3D12_DESCRIPTOR_HEAP_DESC samplerHeapDesc = {};
        samplerHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_SAMPLER;
        samplerHeapDesc.NumDescriptors = 4;
        samplerHeapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;

        HRESULT hr = device->CreateDescriptorHeap(&samplerHeapDesc, IID_PPV_ARGS(&m_samplerHeap));
        if (FAILED(hr))
        {
            OutputDebugStringW(L"NRD: Failed to create sampler heap\n");
            return false;
        }
        
        const nrd::InstanceDesc& instanceDesc = nrd::GetInstanceDesc(*m_nrdInstance);

        CD3DX12_CPU_DESCRIPTOR_HANDLE cpuHandle(m_samplerHeap->GetCPUDescriptorHandleForHeapStart());
        UINT samplerDescriptorSize = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_SAMPLER);

        for (uint32_t i = 0; i < instanceDesc.samplersNum; i++)
        {
            D3D12_SAMPLER_DESC samplerDesc = {};
            
            switch (instanceDesc.samplers[i])
            {
            case nrd::Sampler::NEAREST_CLAMP:
                samplerDesc.Filter = D3D12_FILTER_MIN_MAG_MIP_POINT;
                samplerDesc.AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
                samplerDesc.AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
                samplerDesc.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
                break;

            case nrd::Sampler::LINEAR_CLAMP:
                samplerDesc.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
                samplerDesc.AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
                samplerDesc.AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
                samplerDesc.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
                break;

            default:
                samplerDesc.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
                samplerDesc.AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
                samplerDesc.AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
                samplerDesc.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
                break;
            }

            samplerDesc.MaxLOD = D3D12_FLOAT32_MAX;
            device->CreateSampler(&samplerDesc, cpuHandle);
            cpuHandle.Offset(samplerDescriptorSize);
        }

        return true;
    }

    DXGI_FORMAT NRDDenoiser::GetDXGIFormat(nrd::Format format)
    {
        switch (format)
        {
        case nrd::Format::R8_UNORM:             return DXGI_FORMAT_R8_UNORM;
        case nrd::Format::R8_SNORM:             return DXGI_FORMAT_R8_SNORM;
        case nrd::Format::R8_UINT:              return DXGI_FORMAT_R8_UINT;
        case nrd::Format::R8_SINT:              return DXGI_FORMAT_R8_SINT;
        case nrd::Format::RG8_UNORM:            return DXGI_FORMAT_R8G8_UNORM;
        case nrd::Format::RG8_SNORM:            return DXGI_FORMAT_R8G8_SNORM;
        case nrd::Format::RG8_UINT:             return DXGI_FORMAT_R8G8_UINT;
        case nrd::Format::RG8_SINT:             return DXGI_FORMAT_R8G8_SINT;
        case nrd::Format::RGBA8_UNORM:          return DXGI_FORMAT_R8G8B8A8_UNORM;
        case nrd::Format::RGBA8_SNORM:          return DXGI_FORMAT_R8G8B8A8_SNORM;
        case nrd::Format::RGBA8_UINT:           return DXGI_FORMAT_R8G8B8A8_UINT;
        case nrd::Format::RGBA8_SINT:           return DXGI_FORMAT_R8G8B8A8_SINT;
        case nrd::Format::RGBA8_SRGB:           return DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
        case nrd::Format::R16_UNORM:            return DXGI_FORMAT_R16_UNORM;
        case nrd::Format::R16_SNORM:            return DXGI_FORMAT_R16_SNORM;
        case nrd::Format::R16_UINT:             return DXGI_FORMAT_R16_UINT;
        case nrd::Format::R16_SINT:             return DXGI_FORMAT_R16_SINT;
        case nrd::Format::R16_SFLOAT:           return DXGI_FORMAT_R16_FLOAT;
        case nrd::Format::RG16_UNORM:           return DXGI_FORMAT_R16G16_UNORM;
        case nrd::Format::RG16_SNORM:           return DXGI_FORMAT_R16G16_SNORM;
        case nrd::Format::RG16_UINT:            return DXGI_FORMAT_R16G16_UINT;
        case nrd::Format::RG16_SINT:            return DXGI_FORMAT_R16G16_SINT;
        case nrd::Format::RG16_SFLOAT:          return DXGI_FORMAT_R16G16_FLOAT;
        case nrd::Format::RGBA16_UNORM:         return DXGI_FORMAT_R16G16B16A16_UNORM;
        case nrd::Format::RGBA16_SNORM:         return DXGI_FORMAT_R16G16B16A16_SNORM;
        case nrd::Format::RGBA16_UINT:          return DXGI_FORMAT_R16G16B16A16_UINT;
        case nrd::Format::RGBA16_SINT:          return DXGI_FORMAT_R16G16B16A16_SINT;
        case nrd::Format::RGBA16_SFLOAT:        return DXGI_FORMAT_R16G16B16A16_FLOAT;
        case nrd::Format::R32_UINT:             return DXGI_FORMAT_R32_UINT;
        case nrd::Format::R32_SINT:             return DXGI_FORMAT_R32_SINT;
        case nrd::Format::R32_SFLOAT:           return DXGI_FORMAT_R32_FLOAT;
        case nrd::Format::RG32_UINT:            return DXGI_FORMAT_R32G32_UINT;
        case nrd::Format::RG32_SINT:            return DXGI_FORMAT_R32G32_SINT;
        case nrd::Format::RG32_SFLOAT:          return DXGI_FORMAT_R32G32_FLOAT;
        case nrd::Format::RGB32_UINT:           return DXGI_FORMAT_R32G32B32_UINT;
        case nrd::Format::RGB32_SINT:           return DXGI_FORMAT_R32G32B32_SINT;
        case nrd::Format::RGB32_SFLOAT:         return DXGI_FORMAT_R32G32B32_FLOAT;
        case nrd::Format::RGBA32_UINT:          return DXGI_FORMAT_R32G32B32A32_UINT;
        case nrd::Format::RGBA32_SINT:          return DXGI_FORMAT_R32G32B32A32_SINT;
        case nrd::Format::RGBA32_SFLOAT:        return DXGI_FORMAT_R32G32B32A32_FLOAT;
        case nrd::Format::R10_G10_B10_A2_UNORM: return DXGI_FORMAT_R10G10B10A2_UNORM;
        case nrd::Format::R10_G10_B10_A2_UINT:  return DXGI_FORMAT_R10G10B10A2_UINT;
        case nrd::Format::R11_G11_B10_UFLOAT:   return DXGI_FORMAT_R11G11B10_FLOAT;
        default:                                return DXGI_FORMAT_UNKNOWN;
        }
    }
#endif // NRD_ENABLED

    void NRDDenoiser::Denoise(ID3D12GraphicsCommandList* cmdList, const DenoiserFrameSettings& settings)
    {
        if (!m_initialized)
        {
            LOG_DEBUG("NRDDenoiser::Denoise - not initialized, returning");
            return;
        }

        char buf[256];
        sprintf_s(buf, "NRDDenoiser::Denoise - NRD_ENABLED=%d, m_initialized=%d", NRD_ENABLED, m_initialized ? 1 : 0);
        LOG_DEBUG(buf);

#if NRD_ENABLED
        if (!m_nrdInstance || m_pipelineStates.empty())
        {
            sprintf_s(buf, "NRD: Denoise not ready - instance=%p, pipelineStates.size=%zu", 
                m_nrdInstance, m_pipelineStates.size());
            LOG_DEBUG(buf);
            return;
        }
        LOG_DEBUG("NRDDenoiser::Denoise - NRD path active, proceeding...");

        const nrd::InstanceDesc& instanceDesc = nrd::GetInstanceDesc(*m_nrdInstance);

        // Set common settings for NRD
        nrd::CommonSettings commonSettings = {};
        
        // Copy matrices (row-major to column-major)
        memcpy(commonSettings.viewToClipMatrix, &settings.ProjMatrix, sizeof(float) * 16);
        memcpy(commonSettings.viewToClipMatrixPrev, &settings.ProjMatrixPrev, sizeof(float) * 16);
        memcpy(commonSettings.worldToViewMatrix, &settings.WorldToViewMatrix, sizeof(float) * 16);
        memcpy(commonSettings.worldToViewMatrixPrev, &settings.WorldToViewMatrixPrev, sizeof(float) * 16);
        
        commonSettings.cameraJitter[0] = settings.JitterOffset.x;
        commonSettings.cameraJitter[1] = settings.JitterOffset.y;
        commonSettings.cameraJitterPrev[0] = settings.JitterOffsetPrev.x;
        commonSettings.cameraJitterPrev[1] = settings.JitterOffsetPrev.y;
        
        commonSettings.motionVectorScale[0] = settings.MotionVectorScale.x;
        commonSettings.motionVectorScale[1] = settings.MotionVectorScale.y;
        commonSettings.motionVectorScale[2] = 1.0f;
        
        commonSettings.resourceSize[0] = m_width;
        commonSettings.resourceSize[1] = m_height;
        commonSettings.resourceSizePrev[0] = m_width;
        commonSettings.resourceSizePrev[1] = m_height;
        commonSettings.rectSize[0] = m_width;
        commonSettings.rectSize[1] = m_height;
        commonSettings.rectSizePrev[0] = m_width;
        commonSettings.rectSizePrev[1] = m_height;
        
        commonSettings.denoisingRange = settings.CameraFar;
        commonSettings.frameIndex = m_frameIndex++;
        // Enable temporal accumulation for better denoising quality
        // CONTINUE = use previous frame's history for temporal filtering
        commonSettings.accumulationMode = nrd::AccumulationMode::CONTINUE;
        commonSettings.enableValidation = settings.EnableValidation;
        
        // Log NRD settings for debugging
        sprintf_s(buf, "NRD Settings: frameIndex=%d, denoisingRange=%.1f, accumulationMode=%d (temporal ON)",
            commonSettings.frameIndex, commonSettings.denoisingRange, (int)commonSettings.accumulationMode);
        LOG_DEBUG(buf);
        sprintf_s(buf, "NRD Settings: resourceSize=%ux%u, rectSize=%ux%u",
            commonSettings.resourceSize[0], commonSettings.resourceSize[1],
            commonSettings.rectSize[0], commonSettings.rectSize[1]);
        LOG_DEBUG(buf);
        sprintf_s(buf, "NRD Settings: viewToClip[0]=[%.3f, %.3f, %.3f, %.3f]",
            commonSettings.viewToClipMatrix[0], commonSettings.viewToClipMatrix[1],
            commonSettings.viewToClipMatrix[2], commonSettings.viewToClipMatrix[3]);
        LOG_DEBUG(buf);
        sprintf_s(buf, "NRD Settings: worldToView[0]=[%.3f, %.3f, %.3f, %.3f]",
            commonSettings.worldToViewMatrix[0], commonSettings.worldToViewMatrix[1],
            commonSettings.worldToViewMatrix[2], commonSettings.worldToViewMatrix[3]);
        LOG_DEBUG(buf);

        nrd::SetCommonSettings(*m_nrdInstance, commonSettings);

        // Set REBLUR-specific settings
        nrd::ReblurSettings reblurSettings = {};
        reblurSettings.hitDistanceReconstructionMode = nrd::HitDistanceReconstructionMode::AREA_3X3;
        reblurSettings.enableAntiFirefly = true;
        reblurSettings.maxBlurRadius = 30.0f;
        reblurSettings.minBlurRadius = 0.0f;  // Allow roughness=0 to have zero blur (perfect mirrors)
        
        // For perfect mirrors (roughness=0), use responsive accumulation to avoid temporal blur
        reblurSettings.responsiveAccumulationRoughnessThreshold = 0.05f;
        
        // Reduce specular prepass blur for sharper reflections
        reblurSettings.specularPrepassBlurRadius = 10.0f;
        
        // Reduce history frames for faster convergence
        reblurSettings.maxAccumulatedFrameNum = 16;
        reblurSettings.maxFastAccumulatedFrameNum = 4;
        
        nrd::SetDenoiserSettings(*m_nrdInstance, m_reblurIdentifier, &reblurSettings);

        // Set SIGMA-specific settings (shadow denoising)
        if (m_sigmaEnabled)
        {
            nrd::SigmaSettings sigmaSettings = {};
            // Light direction for directional light sources (normalized)
            // Default to sun direction (straight down)
            sigmaSettings.lightDirection[0] = 0.0f;
            sigmaSettings.lightDirection[1] = -1.0f;
            sigmaSettings.lightDirection[2] = 0.0f;
            // Plane distance sensitivity (controls edge preservation)
            sigmaSettings.planeDistanceSensitivity = 0.02f;
            // Maximum stabilization frames - lower = less history, faster response to changes
            // High values can cause white artifacts to persist at edges
            sigmaSettings.maxStabilizedFrameNum = 2;
            
            nrd::SetDenoiserSettings(*m_nrdInstance, m_sigmaIdentifier, &sigmaSettings);
            LOG_DEBUG("NRD: SIGMA shadow denoiser settings applied");
        }

        // Get compute dispatches for active denoisers
        const nrd::DispatchDesc* dispatchDescs = nullptr;
        uint32_t dispatchDescsNum = 0;
        
        // Include SIGMA identifier if enabled
        nrd::Identifier identifiers[2] = { m_reblurIdentifier, m_sigmaIdentifier };
        uint32_t numIdentifiers = m_sigmaEnabled ? 2 : 1;
        
        nrd::Result result = nrd::GetComputeDispatches(*m_nrdInstance, identifiers, numIdentifiers, dispatchDescs, dispatchDescsNum);
        
        sprintf_s(buf, "NRD: GetComputeDispatches result=%d, dispatchDescsNum=%u", (int)result, dispatchDescsNum);
        LOG_DEBUG(buf);
        
        if (result != nrd::Result::SUCCESS || dispatchDescsNum == 0)
        {
            LOG_DEBUG("NRD: GetComputeDispatches failed, returning");
            return;
        }

        // Set up descriptor heap
        ID3D12DescriptorHeap* heaps[] = { m_nrdDescriptorHeap.Get() };
        cmdList->SetDescriptorHeaps(1, heaps);
        cmdList->SetComputeRootSignature(m_nrdRootSignature.Get());

        // Execute each dispatch
        int dispatchedCount = 0;
        int skippedCount = 0;
        
        // Log first few dispatches for debugging (and any SIGMA dispatches)
        for (uint32_t i = 0; i < dispatchDescsNum; i++)
        {
            const nrd::DispatchDesc& dispatch = dispatchDescs[i];
            
            // Log first 3, or any SIGMA-related dispatch
            bool isSigmaDispatch = dispatch.name && strstr(dispatch.name, "SIGMA") != nullptr;
            if (i < 3 || isSigmaDispatch)
            {
                sprintf_s(buf, "NRD Dispatch[%d]: name=%s, pipeline=%d, grid=%dx%d, resources=%d",
                    i, dispatch.name ? dispatch.name : "null", dispatch.pipelineIndex,
                    dispatch.gridWidth, dispatch.gridHeight, dispatch.resourcesNum);
                LOG_DEBUG(buf);
                
                if (i < 3)
                {
                    for (uint32_t r = 0; r < min(dispatch.resourcesNum, 5u); r++)
                    {
                        const nrd::ResourceDesc& res = dispatch.resources[r];
                        sprintf_s(buf, "  Resource[%d]: type=%d, indexInPool=%d",
                            r, (int)res.type, res.indexInPool);
                        LOG_DEBUG(buf);
                    }
                }
            }
        }
        
        for (uint32_t i = 0; i < dispatchDescsNum; i++)
        {
            const nrd::DispatchDesc& dispatch = dispatchDescs[i];
            
            // Find PSO for this pipeline
            auto psoIt = m_pipelineStates.find(dispatch.pipelineIndex);
            if (psoIt == m_pipelineStates.end())
            {
                sprintf_s(buf, "NRD: Missing PSO for pipeline %d", dispatch.pipelineIndex);
                LOG_DEBUG(buf);
                skippedCount++;
                continue;
            }

            cmdList->SetPipelineState(psoIt->second.Get());

            // Update constant buffer if needed
            if (dispatch.constantBufferData && dispatch.constantBufferDataSize > 0 && 
                !dispatch.constantBufferDataMatchesPreviousDispatch)
            {
                memcpy(m_constantBufferMapped, dispatch.constantBufferData, dispatch.constantBufferDataSize);
            }

            // Set constant buffer
            cmdList->SetComputeRootConstantBufferView(0, m_constantBuffer->GetGPUVirtualAddress());

            // Bind resources following NRD's resourceRanges order to preserve descriptor layout
            const nrd::PipelineDesc& pipelineDesc = instanceDesc.pipelines[dispatch.pipelineIndex];
            
            UINT expectedSrvCount = 0;
            UINT expectedUavCount = 0;
            
            // Count SRVs and UAVs based on resource ranges
            for (uint32_t r = 0; r < pipelineDesc.resourceRangesNum; r++)
            {
                const nrd::ResourceRangeDesc& range = pipelineDesc.resourceRanges[r];
                if (range.descriptorType == nrd::DescriptorType::TEXTURE)
                    expectedSrvCount += range.descriptorsNum;
                else if (range.descriptorType == nrd::DescriptorType::STORAGE_TEXTURE)
                    expectedUavCount += range.descriptorsNum;
            }
            (void)expectedSrvCount;
            (void)expectedUavCount;

            // Create descriptors for this dispatch
            UINT maxSRVs = instanceDesc.descriptorPoolDesc.perSetTexturesMaxNum;
            UINT maxUAVs = instanceDesc.descriptorPoolDesc.perSetStorageTexturesMaxNum;
            UINT descriptorOffset = i * (maxSRVs + maxUAVs);
            
            CD3DX12_CPU_DESCRIPTOR_HANDLE srvCpuHandle(m_nrdDescriptorHeap->GetCPUDescriptorHandleForHeapStart());
            srvCpuHandle.Offset(descriptorOffset, m_descriptorSize);
            CD3DX12_CPU_DESCRIPTOR_HANDLE uavCpuHandle(m_nrdDescriptorHeap->GetCPUDescriptorHandleForHeapStart());
            uavCpuHandle.Offset(descriptorOffset + maxSRVs, m_descriptorSize);

            UINT resourceIdx = 0;
            UINT createdSrvCount = 0;
            UINT createdUavCount = 0;
            for (uint32_t r = 0; r < pipelineDesc.resourceRangesNum; r++)
            {
                const nrd::ResourceRangeDesc& range = pipelineDesc.resourceRanges[r];
                
                for (uint32_t d = 0; d < range.descriptorsNum; d++)
                {
                    if (resourceIdx >= dispatch.resourcesNum)
                        break;
                        
                    const nrd::ResourceDesc& resource = dispatch.resources[resourceIdx++];
                    ID3D12Resource* d3dResource = GetResourceForNRD(resource, instanceDesc);
                    
                    // DEBUG: Log when OUTPUT resources are bound
                    if (resource.type == nrd::ResourceType::OUT_DIFF_RADIANCE_HITDIST ||
                        resource.type == nrd::ResourceType::OUT_SPEC_RADIANCE_HITDIST ||
                        resource.type == nrd::ResourceType::OUT_SHADOW_TRANSLUCENCY ||
                        resource.type == nrd::ResourceType::IN_PENUMBRA ||
                        resource.type == nrd::ResourceType::IN_TRANSLUCENCY)
                    {
                        const char* typeName = "UNKNOWN";
                        if (resource.type == nrd::ResourceType::OUT_DIFF_RADIANCE_HITDIST) typeName = "OUT_DIFF";
                        else if (resource.type == nrd::ResourceType::OUT_SPEC_RADIANCE_HITDIST) typeName = "OUT_SPEC";
                        else if (resource.type == nrd::ResourceType::OUT_SHADOW_TRANSLUCENCY) typeName = "OUT_SHADOW";
                        else if (resource.type == nrd::ResourceType::IN_PENUMBRA) typeName = "IN_PENUMBRA";
                        else if (resource.type == nrd::ResourceType::IN_TRANSLUCENCY) typeName = "IN_TRANSLUCENCY";
                        
                        sprintf_s(buf, "NRD: Binding %s (type=%d), ptr=%p, dispatch=%s",
                            typeName, (int)resource.type, (void*)d3dResource, dispatch.name);
                        LOG_DEBUG(buf);
                    }
                    
                    if (!d3dResource)
                    {
                        sprintf_s(buf, "NRD: NULL resource for type=%d, indexInPool=%d", (int)resource.type, resource.indexInPool);
                        LOG_DEBUG(buf);
                        continue;
                    }

                    // Ensure resource state matches descriptor type for this dispatch
                    D3D12_RESOURCE_STATES desiredState = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
                    if (range.descriptorType == nrd::DescriptorType::STORAGE_TEXTURE)
                    {
                        desiredState = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
                    }

                    auto stateIt = m_resourceStates.find(d3dResource);
                    D3D12_RESOURCE_STATES currentState = (stateIt != m_resourceStates.end())
                        ? stateIt->second
                        : D3D12_RESOURCE_STATE_COMMON;

                    if (currentState != desiredState)
                    {
                        D3D12_RESOURCE_BARRIER barrier = MakeTransition(d3dResource, currentState, desiredState);
                        cmdList->ResourceBarrier(1, &barrier);
                        m_resourceStates[d3dResource] = desiredState;
                    }

                    if (range.descriptorType == nrd::DescriptorType::TEXTURE)
                    {
                        // Create SRV in the SRV block
                        D3D12_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
                        srvDesc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
                        srvDesc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
                        srvDesc.Texture2D.MipLevels = 1;
                        srvDesc.Format = d3dResource->GetDesc().Format;
                        
                        m_dxContext->GetDevice()->CreateShaderResourceView(d3dResource, &srvDesc, srvCpuHandle);
                        srvCpuHandle.Offset(m_descriptorSize);
                        createdSrvCount++;
                    }
                    else
                    {
                        // Create UAV in the UAV block
                        D3D12_UNORDERED_ACCESS_VIEW_DESC uavDesc = {};
                        uavDesc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
                        uavDesc.Texture2D.MipSlice = 0;
                        uavDesc.Format = d3dResource->GetDesc().Format;
                        
                        m_dxContext->GetDevice()->CreateUnorderedAccessView(d3dResource, nullptr, &uavDesc, uavCpuHandle);
                        uavCpuHandle.Offset(m_descriptorSize);
                        createdUavCount++;
                    }
                }
            }

            // Fill remaining slots with null descriptors to satisfy static table requirements
            if (createdSrvCount < maxSRVs)
            {
                D3D12_SHADER_RESOURCE_VIEW_DESC nullSrvDesc = {};
                nullSrvDesc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
                nullSrvDesc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
                nullSrvDesc.Texture2D.MipLevels = 1;
                nullSrvDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;

                for (; createdSrvCount < maxSRVs; ++createdSrvCount)
                {
                    m_dxContext->GetDevice()->CreateShaderResourceView(nullptr, &nullSrvDesc, srvCpuHandle);
                    srvCpuHandle.Offset(m_descriptorSize);
                }
            }

            if (createdUavCount < maxUAVs)
            {
                D3D12_UNORDERED_ACCESS_VIEW_DESC nullUavDesc = {};
                nullUavDesc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
                nullUavDesc.Texture2D.MipSlice = 0;
                nullUavDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;

                for (; createdUavCount < maxUAVs; ++createdUavCount)
                {
                    m_dxContext->GetDevice()->CreateUnorderedAccessView(nullptr, nullptr, &nullUavDesc, uavCpuHandle);
                    uavCpuHandle.Offset(m_descriptorSize);
                }
            }

            // Set descriptor tables
            CD3DX12_GPU_DESCRIPTOR_HANDLE srvGpuHandle(m_nrdDescriptorHeap->GetGPUDescriptorHandleForHeapStart());
            srvGpuHandle.Offset(descriptorOffset, m_descriptorSize);
            CD3DX12_GPU_DESCRIPTOR_HANDLE uavGpuHandle(m_nrdDescriptorHeap->GetGPUDescriptorHandleForHeapStart());
            uavGpuHandle.Offset(descriptorOffset + maxSRVs, m_descriptorSize);
            
            // Root parameter indices are based on descriptor table existence, not usage counts.
            // 0 = CBV, 1 = SRV table (if maxSRVs > 0), 2 = UAV table (if maxUAVs > 0)
            const bool hasSrvTable = (maxSRVs > 0);
            const bool hasUavTable = (maxUAVs > 0);

            if (hasSrvTable)
            {
                cmdList->SetComputeRootDescriptorTable(1, srvGpuHandle);
            }
            if (hasUavTable)
            {
                cmdList->SetComputeRootDescriptorTable(hasSrvTable ? 2 : 1, uavGpuHandle);
            }

            // Dispatch
            cmdList->Dispatch(dispatch.gridWidth, dispatch.gridHeight, 1);
            dispatchedCount++;

            // UAV barrier between dispatches
            D3D12_RESOURCE_BARRIER barrier = {};
            barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
            barrier.UAV.pResource = nullptr;  // All UAVs
            cmdList->ResourceBarrier(1, &barrier);
        }

        sprintf_s(buf, "NRD: Dispatched %d, Skipped %d (PSO count=%zu)", 
            dispatchedCount, skippedCount, m_pipelineStates.size());
        LOG_DEBUG(buf);

        // Ensure NRD outputs and Albedo are in SRV state for composite
        auto ToSrv = [&](ID3D12Resource* res)
        {
            if (!res)
                return;
            auto stateIt = m_resourceStates.find(res);
            D3D12_RESOURCE_STATES currentState = (stateIt != m_resourceStates.end())
                ? stateIt->second
                : D3D12_RESOURCE_STATE_COMMON;
            if (currentState == D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE)
                return;
            D3D12_RESOURCE_BARRIER barrier = MakeTransition(res, currentState, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            cmdList->ResourceBarrier(1, &barrier);
            m_resourceStates[res] = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        };

        ToSrv(m_output.DiffuseRadiance.Get());
        ToSrv(m_output.SpecularRadiance.Get());
        ToSrv(m_output.DenoisedShadow.Get());
        ToSrv(m_gBuffer.Albedo.Get());
        return;
#endif

        // Stub: Just copy input to output for now (pass-through)
        OutputDebugStringW(L"NRD: Denoise called (stub mode - no NRD)\n");
    }

#if NRD_ENABLED
    ID3D12Resource* NRDDenoiser::GetResourceForNRD(const nrd::ResourceDesc& resource, const nrd::InstanceDesc& instanceDesc)
    {
        ID3D12Resource* result = nullptr;
        const char* resourceName = "unknown";
        
        switch (resource.type)
        {
        // REBLUR inputs
        case nrd::ResourceType::IN_MV:
            result = m_gBuffer.MotionVectors.Get();
            resourceName = "IN_MV";
            break;
        case nrd::ResourceType::IN_NORMAL_ROUGHNESS:
            result = m_gBuffer.NormalRoughness.Get();
            resourceName = "IN_NORMAL_ROUGHNESS";
            break;
        case nrd::ResourceType::IN_VIEWZ:
            result = m_gBuffer.ViewZ.Get();
            resourceName = "IN_VIEWZ";
            break;
        case nrd::ResourceType::IN_DIFF_RADIANCE_HITDIST:
            result = m_gBuffer.DiffuseRadianceHitDist.Get();
            resourceName = "IN_DIFF_RADIANCE_HITDIST";
            break;
        case nrd::ResourceType::IN_SPEC_RADIANCE_HITDIST:
            result = m_gBuffer.SpecularRadianceHitDist.Get();
            resourceName = "IN_SPEC_RADIANCE_HITDIST";
            break;
        
        // REBLUR outputs
        case nrd::ResourceType::OUT_DIFF_RADIANCE_HITDIST:
            result = m_output.DiffuseRadiance.Get();
            resourceName = "OUT_DIFF_RADIANCE_HITDIST";
            break;
        case nrd::ResourceType::OUT_SPEC_RADIANCE_HITDIST:
            result = m_output.SpecularRadiance.Get();
            resourceName = "OUT_SPEC_RADIANCE_HITDIST";
            break;
        
        // SIGMA shadow inputs (penumbra and translucency)
        case nrd::ResourceType::IN_PENUMBRA:
            result = m_gBuffer.ShadowData.Get();
            resourceName = "IN_PENUMBRA (ShadowData)";
            break;
        case nrd::ResourceType::IN_TRANSLUCENCY:
            result = m_gBuffer.ShadowTranslucency.Get();
            resourceName = "IN_TRANSLUCENCY";
            break;
        
        // SIGMA shadow outputs
        case nrd::ResourceType::OUT_SHADOW_TRANSLUCENCY:
            result = m_output.DenoisedShadow.Get();
            resourceName = "OUT_SHADOW_TRANSLUCENCY";
            break;
        
        // Internal pools
        case nrd::ResourceType::TRANSIENT_POOL:
            if (resource.indexInPool < instanceDesc.transientPoolSize)
                result = m_nrdTextures[instanceDesc.permanentPoolSize + resource.indexInPool].Get();
            resourceName = "TRANSIENT_POOL";
            break;
        case nrd::ResourceType::PERMANENT_POOL:
            if (resource.indexInPool < instanceDesc.permanentPoolSize)
                result = m_nrdTextures[resource.indexInPool].Get();
            resourceName = "PERMANENT_POOL";
            break;
        default:
            {
                char buf[128];
                sprintf_s(buf, "NRD: Unknown resource type %d requested", (int)resource.type);
                LOG_DEBUG(buf);
            }
            break;
        }
        
        // Log if resource is null (potential crash cause)
        if (!result)
        {
            char buf[256];
            sprintf_s(buf, "NRD WARNING: NULL resource for %s (type=%d, indexInPool=%d)", 
                resourceName, (int)resource.type, resource.indexInPool);
            LOG_DEBUG(buf);
        }
        
        return result;
    }
#endif

    D3D12_GPU_DESCRIPTOR_HANDLE NRDDenoiser::GetDiffuseRadianceUAV() const
    {
        CD3DX12_GPU_DESCRIPTOR_HANDLE handle(m_descriptorHeap->GetGPUDescriptorHandleForHeapStart());
        return handle;
    }

    D3D12_GPU_DESCRIPTOR_HANDLE NRDDenoiser::GetSpecularRadianceUAV() const
    {
        CD3DX12_GPU_DESCRIPTOR_HANDLE handle(m_descriptorHeap->GetGPUDescriptorHandleForHeapStart());
        handle.Offset(1, m_descriptorSize);
        return handle;
    }

    void NRDDenoiser::NotifyResourceState(ID3D12Resource* resource, D3D12_RESOURCE_STATES state)
    {
        if (!resource)
            return;
        m_resourceStates[resource] = state;
    }

    void NRDDenoiser::EnsureResourceState(ID3D12GraphicsCommandList* cmdList,
                                          ID3D12Resource* resource,
                                          D3D12_RESOURCE_STATES desiredState)
    {
        if (!cmdList || !resource)
            return;
        auto stateIt = m_resourceStates.find(resource);
        D3D12_RESOURCE_STATES currentState = (stateIt != m_resourceStates.end())
            ? stateIt->second
            : D3D12_RESOURCE_STATE_COMMON;
        if (currentState == desiredState)
            return;
        D3D12_RESOURCE_BARRIER barrier = MakeTransition(resource, currentState, desiredState);
        cmdList->ResourceBarrier(1, &barrier);
        m_resourceStates[resource] = desiredState;
    }

    D3D12_GPU_DESCRIPTOR_HANDLE NRDDenoiser::GetNormalRoughnessUAV() const
    {
        CD3DX12_GPU_DESCRIPTOR_HANDLE handle(m_descriptorHeap->GetGPUDescriptorHandleForHeapStart());
        handle.Offset(2, m_descriptorSize);
        return handle;
    }

    D3D12_GPU_DESCRIPTOR_HANDLE NRDDenoiser::GetViewZUAV() const
    {
        CD3DX12_GPU_DESCRIPTOR_HANDLE handle(m_descriptorHeap->GetGPUDescriptorHandleForHeapStart());
        handle.Offset(3, m_descriptorSize);
        return handle;
    }

    D3D12_GPU_DESCRIPTOR_HANDLE NRDDenoiser::GetMotionVectorsUAV() const
    {
        CD3DX12_GPU_DESCRIPTOR_HANDLE handle(m_descriptorHeap->GetGPUDescriptorHandleForHeapStart());
        handle.Offset(4, m_descriptorSize);
        return handle;
    }

    D3D12_GPU_DESCRIPTOR_HANDLE NRDDenoiser::GetShadowDataUAV() const
    {
        CD3DX12_GPU_DESCRIPTOR_HANDLE handle(m_descriptorHeap->GetGPUDescriptorHandleForHeapStart());
        handle.Offset(5, m_descriptorSize);
        return handle;
    }

    D3D12_GPU_DESCRIPTOR_HANDLE NRDDenoiser::GetShadowTranslucencyUAV() const
    {
        CD3DX12_GPU_DESCRIPTOR_HANDLE handle(m_descriptorHeap->GetGPUDescriptorHandleForHeapStart());
        handle.Offset(6, m_descriptorSize);
        return handle;
    }

    D3D12_GPU_DESCRIPTOR_HANDLE NRDDenoiser::GetDenoisedShadowUAV() const
    {
        CD3DX12_GPU_DESCRIPTOR_HANDLE handle(m_descriptorHeap->GetGPUDescriptorHandleForHeapStart());
        handle.Offset(9, m_descriptorSize);
        return handle;
    }

    void NRDDenoiser::DestroyResources()
    {
#if NRD_ENABLED
        if (m_constantBufferMapped && m_constantBuffer)
        {
            m_constantBuffer->Unmap(0, nullptr);
            m_constantBufferMapped = nullptr;
        }

        m_constantBuffer.Reset();
        m_samplerHeap.Reset();
        m_nrdDescriptorHeap.Reset();
        
        for (auto& tex : m_nrdTextures)
            tex.Reset();
        m_nrdTextures.clear();

        m_pipelineStates.clear();
        m_nrdRootSignature.Reset();
        m_frameIndex = 0;

        if (m_nrdInstance)
        {
            nrd::DestroyInstance(*m_nrdInstance);
            m_nrdInstance = nullptr;
        }
#endif

        m_descriptorHeap.Reset();
        
        m_gBuffer.DiffuseRadianceHitDist.Reset();
        m_gBuffer.SpecularRadianceHitDist.Reset();
        m_gBuffer.RawSpecularBackup.Reset();
        m_gBuffer.RawDiffuseBackup.Reset();
        m_gBuffer.NormalRoughness.Reset();
        m_gBuffer.ViewZ.Reset();
        m_gBuffer.MotionVectors.Reset();
        m_gBuffer.ShadowData.Reset();
        m_gBuffer.ShadowTranslucency.Reset();
        m_gBuffer.ObjectID.Reset();

        m_output.DiffuseRadiance.Reset();
        m_output.SpecularRadiance.Reset();
        m_output.DenoisedShadow.Reset();

        m_resourceStates.clear();
        m_initialized = false;
    }
}

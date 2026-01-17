#include "NRDDenoiser.h"
#include "../DXContext.h"
#include "../d3dx12.h"
#include <d3dcompiler.h>

namespace RayTraceVS::DXEngine
{
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

#if NRD_ENABLED
        // Full NRD initialization - requires NRD library to be built
        // See README for instructions on building NRD with CMake
        
        // Create NRD instance with REBLUR_DIFFUSE_SPECULAR denoiser
        nrd::DenoiserDesc denoiserDesc = {};
        denoiserDesc.identifier = m_reblurIdentifier;
        denoiserDesc.denoiser = nrd::Denoiser::REBLUR_DIFFUSE_SPECULAR;

        nrd::InstanceCreationDesc instanceDesc = {};
        instanceDesc.denoisers = &denoiserDesc;
        instanceDesc.denoisersNum = 1;

        nrd::Result result = nrd::CreateInstance(instanceDesc, m_nrdInstance);
        if (result != nrd::Result::SUCCESS)
        {
            OutputDebugStringW(L"NRD: Failed to create instance\n");
            return false;
        }
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
            return true;
        };

        // Create G-Buffer textures
        if (!CreateTexture(m_gBuffer.DiffuseRadianceHitDist, DXGI_FORMAT_R16G16B16A16_FLOAT, L"GBuffer_DiffuseRadianceHitDist"))
            return false;

        if (!CreateTexture(m_gBuffer.SpecularRadianceHitDist, DXGI_FORMAT_R16G16B16A16_FLOAT, L"GBuffer_SpecularRadianceHitDist"))
            return false;

        if (!CreateTexture(m_gBuffer.NormalRoughness, DXGI_FORMAT_R8G8B8A8_UNORM, L"GBuffer_NormalRoughness"))
            return false;

        if (!CreateTexture(m_gBuffer.ViewZ, DXGI_FORMAT_R32_FLOAT, L"GBuffer_ViewZ"))
            return false;

        if (!CreateTexture(m_gBuffer.MotionVectors, DXGI_FORMAT_R16G16_FLOAT, L"GBuffer_MotionVectors"))
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
            return true;
        };

        if (!CreateTexture(m_output.DiffuseRadiance, DXGI_FORMAT_R16G16B16A16_FLOAT, L"Output_DiffuseRadiance"))
            return false;

        if (!CreateTexture(m_output.SpecularRadiance, DXGI_FORMAT_R16G16B16A16_FLOAT, L"Output_SpecularRadiance"))
            return false;

        // Create UAV descriptors for output (starting at index 5)
        auto device2 = m_dxContext->GetDevice();
        CD3DX12_CPU_DESCRIPTOR_HANDLE cpuHandle(m_descriptorHeap->GetCPUDescriptorHandleForHeapStart());
        cpuHandle.Offset(5, m_descriptorSize);

        D3D12_UNORDERED_ACCESS_VIEW_DESC uavDesc = {};
        uavDesc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        uavDesc.Texture2D.MipSlice = 0;
        uavDesc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;

        device2->CreateUnorderedAccessView(m_output.DiffuseRadiance.Get(), nullptr, &uavDesc, cpuHandle);
        cpuHandle.Offset(m_descriptorSize);

        device2->CreateUnorderedAccessView(m_output.SpecularRadiance.Get(), nullptr, &uavDesc, cpuHandle);

        return true;
    }

#if NRD_ENABLED
    bool NRDDenoiser::CreateNRDResources()
    {
        if (!m_nrdInstance)
            return false;

        auto device = m_dxContext->GetDevice();
        const nrd::InstanceDesc& instanceDesc = nrd::GetInstanceDesc(*m_nrdInstance);

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
        }

        // Create NRD descriptor heap for dispatch resources
        // Each dispatch needs SRVs + UAVs, and we may have multiple dispatches per frame
        uint32_t maxDispatches = 32;  // Reasonable upper bound
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

        OutputDebugStringW(L"NRD: Pipelines created successfully\n");
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
            return;

#if NRD_ENABLED
        if (!m_nrdInstance || m_pipelineStates.empty())
        {
            OutputDebugStringW(L"NRD: Denoise called but not ready\n");
            return;
        }

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
        commonSettings.accumulationMode = settings.IsFirstFrame ? nrd::AccumulationMode::CLEAR_AND_RESTART : nrd::AccumulationMode::CONTINUE;
        commonSettings.enableValidation = settings.EnableValidation;

        nrd::SetCommonSettings(*m_nrdInstance, commonSettings);

        // Set REBLUR-specific settings
        nrd::ReblurSettings reblurSettings = {};
        reblurSettings.hitDistanceReconstructionMode = nrd::HitDistanceReconstructionMode::AREA_3X3;
        reblurSettings.enableAntiFirefly = true;
        reblurSettings.maxBlurRadius = 30.0f;
        reblurSettings.minBlurRadius = 1.0f;
        
        nrd::SetDenoiserSettings(*m_nrdInstance, m_reblurIdentifier, &reblurSettings);

        // Get compute dispatches
        const nrd::DispatchDesc* dispatchDescs = nullptr;
        uint32_t dispatchDescsNum = 0;
        
        nrd::Identifier identifiers[] = { m_reblurIdentifier };
        nrd::Result result = nrd::GetComputeDispatches(*m_nrdInstance, identifiers, 1, dispatchDescs, dispatchDescsNum);
        
        if (result != nrd::Result::SUCCESS || dispatchDescsNum == 0)
        {
            OutputDebugStringW(L"NRD: GetComputeDispatches failed\n");
            return;
        }

        // Set up descriptor heap
        ID3D12DescriptorHeap* heaps[] = { m_nrdDescriptorHeap.Get() };
        cmdList->SetDescriptorHeaps(1, heaps);
        cmdList->SetComputeRootSignature(m_nrdRootSignature.Get());

        // Execute each dispatch
        for (uint32_t i = 0; i < dispatchDescsNum; i++)
        {
            const nrd::DispatchDesc& dispatch = dispatchDescs[i];
            
            // Find PSO for this pipeline
            auto psoIt = m_pipelineStates.find(dispatch.pipelineIndex);
            if (psoIt == m_pipelineStates.end())
            {
                wchar_t msg[128];
                swprintf_s(msg, L"NRD: Missing PSO for pipeline %d\n", dispatch.pipelineIndex);
                OutputDebugStringW(msg);
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

            // Bind resources
            // First, create descriptors for this dispatch's resources
            const nrd::PipelineDesc& pipelineDesc = instanceDesc.pipelines[dispatch.pipelineIndex];
            
            UINT srvCount = 0;
            UINT uavCount = 0;
            
            // Count SRVs and UAVs based on resource ranges
            for (uint32_t r = 0; r < pipelineDesc.resourceRangesNum; r++)
            {
                const nrd::ResourceRangeDesc& range = pipelineDesc.resourceRanges[r];
                if (range.descriptorType == nrd::DescriptorType::TEXTURE)
                    srvCount += range.descriptorsNum;
                else if (range.descriptorType == nrd::DescriptorType::STORAGE_TEXTURE)
                    uavCount += range.descriptorsNum;
            }

            // Create descriptors for this dispatch
            UINT descriptorOffset = i * (instanceDesc.descriptorPoolDesc.perSetTexturesMaxNum + 
                                         instanceDesc.descriptorPoolDesc.perSetStorageTexturesMaxNum);
            
            CD3DX12_CPU_DESCRIPTOR_HANDLE cpuHandle(m_nrdDescriptorHeap->GetCPUDescriptorHandleForHeapStart());
            cpuHandle.Offset(descriptorOffset, m_descriptorSize);

            UINT resourceIdx = 0;
            for (uint32_t r = 0; r < pipelineDesc.resourceRangesNum; r++)
            {
                const nrd::ResourceRangeDesc& range = pipelineDesc.resourceRanges[r];
                
                for (uint32_t d = 0; d < range.descriptorsNum; d++)
                {
                    if (resourceIdx >= dispatch.resourcesNum)
                        break;
                        
                    const nrd::ResourceDesc& resource = dispatch.resources[resourceIdx++];
                    ID3D12Resource* d3dResource = GetResourceForNRD(resource, instanceDesc);
                    
                    if (!d3dResource)
                        continue;

                    if (range.descriptorType == nrd::DescriptorType::TEXTURE)
                    {
                        // Create SRV
                        D3D12_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
                        srvDesc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
                        srvDesc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
                        srvDesc.Texture2D.MipLevels = 1;
                        srvDesc.Format = d3dResource->GetDesc().Format;
                        
                        m_dxContext->GetDevice()->CreateShaderResourceView(d3dResource, &srvDesc, cpuHandle);
                    }
                    else
                    {
                        // Create UAV
                        D3D12_UNORDERED_ACCESS_VIEW_DESC uavDesc = {};
                        uavDesc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
                        uavDesc.Texture2D.MipSlice = 0;
                        uavDesc.Format = d3dResource->GetDesc().Format;
                        
                        m_dxContext->GetDevice()->CreateUnorderedAccessView(d3dResource, nullptr, &uavDesc, cpuHandle);
                    }
                    
                    cpuHandle.Offset(m_descriptorSize);
                }
            }

            // Set descriptor tables
            CD3DX12_GPU_DESCRIPTOR_HANDLE gpuHandle(m_nrdDescriptorHeap->GetGPUDescriptorHandleForHeapStart());
            gpuHandle.Offset(descriptorOffset, m_descriptorSize);
            
            UINT rootParamIndex = 1;  // 0 is CBV
            if (srvCount > 0)
            {
                cmdList->SetComputeRootDescriptorTable(rootParamIndex++, gpuHandle);
                gpuHandle.Offset(instanceDesc.descriptorPoolDesc.perSetTexturesMaxNum, m_descriptorSize);
            }
            if (uavCount > 0)
            {
                cmdList->SetComputeRootDescriptorTable(rootParamIndex, gpuHandle);
            }

            // Dispatch
            cmdList->Dispatch(dispatch.gridWidth, dispatch.gridHeight, 1);

            // UAV barrier between dispatches
            D3D12_RESOURCE_BARRIER barrier = {};
            barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
            barrier.UAV.pResource = nullptr;  // All UAVs
            cmdList->ResourceBarrier(1, &barrier);
        }

        OutputDebugStringW(L"NRD: Denoise completed\n");
        return;
#endif

        // Stub: Just copy input to output for now (pass-through)
        OutputDebugStringW(L"NRD: Denoise called (stub mode - no NRD)\n");
    }

#if NRD_ENABLED
    ID3D12Resource* NRDDenoiser::GetResourceForNRD(const nrd::ResourceDesc& resource, const nrd::InstanceDesc& instanceDesc)
    {
        switch (resource.type)
        {
        case nrd::ResourceType::IN_MV:
            return m_gBuffer.MotionVectors.Get();
        case nrd::ResourceType::IN_NORMAL_ROUGHNESS:
            return m_gBuffer.NormalRoughness.Get();
        case nrd::ResourceType::IN_VIEWZ:
            return m_gBuffer.ViewZ.Get();
        case nrd::ResourceType::IN_DIFF_RADIANCE_HITDIST:
            return m_gBuffer.DiffuseRadianceHitDist.Get();
        case nrd::ResourceType::IN_SPEC_RADIANCE_HITDIST:
            return m_gBuffer.SpecularRadianceHitDist.Get();
        case nrd::ResourceType::OUT_DIFF_RADIANCE_HITDIST:
            return m_output.DiffuseRadiance.Get();
        case nrd::ResourceType::OUT_SPEC_RADIANCE_HITDIST:
            return m_output.SpecularRadiance.Get();
        case nrd::ResourceType::TRANSIENT_POOL:
            if (resource.indexInPool < instanceDesc.transientPoolSize)
                return m_nrdTextures[instanceDesc.permanentPoolSize + resource.indexInPool].Get();
            break;
        case nrd::ResourceType::PERMANENT_POOL:
            if (resource.indexInPool < instanceDesc.permanentPoolSize)
                return m_nrdTextures[resource.indexInPool].Get();
            break;
        default:
            break;
        }
        return nullptr;
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
        m_gBuffer.NormalRoughness.Reset();
        m_gBuffer.ViewZ.Reset();
        m_gBuffer.MotionVectors.Reset();

        m_output.DiffuseRadiance.Reset();
        m_output.SpecularRadiance.Reset();

        m_initialized = false;
    }
}

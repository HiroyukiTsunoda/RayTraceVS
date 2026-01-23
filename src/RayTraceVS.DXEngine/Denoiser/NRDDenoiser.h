#pragma once

#include <d3d12.h>
#include <wrl/client.h>
#include <DirectXMath.h>
#include <vector>
#include <memory>
#include <unordered_map>

// NRD integration - enabled by default since NRD SDK is built
// To disable, define NRD_ENABLED=0 in preprocessor
#ifndef NRD_ENABLED
#define NRD_ENABLED 1
#endif

#if NRD_ENABLED
#include "NRD.h"
#endif

using Microsoft::WRL::ComPtr;
using namespace DirectX;

namespace RayTraceVS::DXEngine
{
    class DXContext;

    // G-Buffer structure for denoising
    struct GBuffer
    {
        ComPtr<ID3D12Resource> DiffuseRadianceHitDist;   // RGBA16F: RGB = diffuse radiance, A = hit distance
        ComPtr<ID3D12Resource> SpecularRadianceHitDist; // RGBA16F: RGB = specular radiance, A = hit distance
        ComPtr<ID3D12Resource> NormalRoughness;          // RGBA8: XYZ = normal (oct encoded), W = roughness
        ComPtr<ID3D12Resource> ViewZ;                    // R32F: linear view depth
        ComPtr<ID3D12Resource> MotionVectors;            // RG16F: 2D screen-space motion vectors
        ComPtr<ID3D12Resource> Albedo;                   // RGBA8: albedo color
        // SIGMA shadow inputs
        ComPtr<ID3D12Resource> ShadowData;               // RG16F: R = shadow visibility (0-1), G = penumbra
        ComPtr<ID3D12Resource> ShadowTranslucency;       // RGBA16F: shadow translucency for SIGMA
        // Raw specular backup (copy before NRD corrupts the original)
        ComPtr<ID3D12Resource> RawSpecularBackup;        // RGBA16F: copy of SpecularRadianceHitDist before NRD
        // Raw diffuse backup (copy before NRD corrupts the original)
        ComPtr<ID3D12Resource> RawDiffuseBackup;         // RGBA16F: copy of DiffuseRadianceHitDist before NRD
        // Object ID for custom shadow denoiser
        ComPtr<ID3D12Resource> ObjectID;                 // R32UI: object type + index
    };

    // Denoised output structure
    struct DenoisedOutput
    {
        ComPtr<ID3D12Resource> DiffuseRadiance;   // RGBA16F: denoised diffuse
        ComPtr<ID3D12Resource> SpecularRadiance;  // RGBA16F: denoised specular
        // SIGMA shadow output
        ComPtr<ID3D12Resource> DenoisedShadow;    // RG16F: denoised shadow visibility
    };

    // Common settings for NRD (per-frame)
    struct DenoiserFrameSettings
    {
        XMFLOAT4X4 ViewMatrix;
        XMFLOAT4X4 ProjMatrix;
        XMFLOAT4X4 ViewMatrixPrev;
        XMFLOAT4X4 ProjMatrixPrev;
        XMFLOAT4X4 WorldToViewMatrix;
        XMFLOAT4X4 WorldToViewMatrixPrev;
        XMFLOAT2 JitterOffset;
        XMFLOAT2 JitterOffsetPrev;
        XMFLOAT2 MotionVectorScale;
        float CameraNear;
        float CameraFar;
        bool IsFirstFrame;
        bool EnableValidation;
        float DenoiserStabilization;
    };

    // NRD Denoiser wrapper class for D3D12
    class NRDDenoiser
    {
    public:
        NRDDenoiser(DXContext* context);
        ~NRDDenoiser();

        // Initialize the denoiser
        bool Initialize(UINT width, UINT height);

        // Resize resources when window size changes
        bool Resize(UINT width, UINT height);

        // Get G-Buffer resources for ray tracing output
        GBuffer& GetGBuffer() { return m_gBuffer; }
        const GBuffer& GetGBuffer() const { return m_gBuffer; }

        // Get denoised output resources
        DenoisedOutput& GetOutput() { return m_output; }
        const DenoisedOutput& GetOutput() const { return m_output; }

        // Perform denoising
        void Denoise(ID3D12GraphicsCommandList* cmdList, const DenoiserFrameSettings& settings);

        // Track external resource state changes
        void NotifyResourceState(ID3D12Resource* resource, D3D12_RESOURCE_STATES state);
        void EnsureResourceState(ID3D12GraphicsCommandList* cmdList,
                                 ID3D12Resource* resource,
                                 D3D12_RESOURCE_STATES desiredState);

        // Check if denoiser is ready
        bool IsReady() const { return m_initialized; }
        
        // Get dimensions
        UINT GetWidth() const { return m_width; }
        UINT GetHeight() const { return m_height; }

        // Get descriptor heap for G-Buffer UAVs
        ID3D12DescriptorHeap* GetDescriptorHeap() const { return m_descriptorHeap.Get(); }
        
        // Get UAV GPU handles for shader binding
        D3D12_GPU_DESCRIPTOR_HANDLE GetDiffuseRadianceUAV() const;
        D3D12_GPU_DESCRIPTOR_HANDLE GetSpecularRadianceUAV() const;
        D3D12_GPU_DESCRIPTOR_HANDLE GetNormalRoughnessUAV() const;
        D3D12_GPU_DESCRIPTOR_HANDLE GetViewZUAV() const;
        D3D12_GPU_DESCRIPTOR_HANDLE GetMotionVectorsUAV() const;
        
        // SIGMA shadow UAVs
        D3D12_GPU_DESCRIPTOR_HANDLE GetShadowDataUAV() const;
        D3D12_GPU_DESCRIPTOR_HANDLE GetShadowTranslucencyUAV() const;
        D3D12_GPU_DESCRIPTOR_HANDLE GetDenoisedShadowUAV() const;
        
        // Check if SIGMA is enabled
        bool IsSigmaEnabled() const { return m_sigmaEnabled; }
        void SetSigmaEnabled(bool enabled) { m_sigmaEnabled = enabled; }

    private:
        DXContext* m_dxContext;
        bool m_initialized = false;
        UINT m_width = 0;
        UINT m_height = 0;

        bool m_sigmaEnabled = true;  // Re-enabled with logging to diagnose issues

#if NRD_ENABLED
        // NRD Instance (only when NRD is enabled)
        nrd::Instance* m_nrdInstance = nullptr;
        nrd::Identifier m_reblurIdentifier = 0;
        nrd::Identifier m_sigmaIdentifier = 1;  // SIGMA shadow denoiser identifier
        uint32_t m_frameIndex = 0;
        
        // NRD internal textures
        std::vector<ComPtr<ID3D12Resource>> m_nrdTextures;
        
        // Compute pipeline for NRD dispatches
        std::unordered_map<uint32_t, ComPtr<ID3D12PipelineState>> m_pipelineStates;
        ComPtr<ID3D12RootSignature> m_nrdRootSignature;
        
        // NRD descriptor heap (separate from G-Buffer heap)
        ComPtr<ID3D12DescriptorHeap> m_nrdDescriptorHeap;
        
        // Constant buffer for NRD
        ComPtr<ID3D12Resource> m_constantBuffer;
        void* m_constantBufferMapped = nullptr;
        
        // Sampler heap
        ComPtr<ID3D12DescriptorHeap> m_samplerHeap;

        // Resource state tracking for NRD inputs/outputs
        std::unordered_map<ID3D12Resource*, D3D12_RESOURCE_STATES> m_resourceStates;
        
        // Helper functions
        bool CreateNRDResources();
        bool CreateNRDPipelines();
        bool CreateConstantBuffer();
        bool CreateSamplers();
        DXGI_FORMAT GetDXGIFormat(nrd::Format format);
        ID3D12Resource* GetResourceForNRD(const nrd::ResourceDesc& resource, const nrd::InstanceDesc& instanceDesc);
#endif

        // G-Buffer resources
        GBuffer m_gBuffer;
        DenoisedOutput m_output;

        // Previous frame data (for temporal accumulation)
        GBuffer m_gBufferPrev;
        
        // Descriptor heap for G-Buffer UAVs/SRVs
        ComPtr<ID3D12DescriptorHeap> m_descriptorHeap;
        UINT m_descriptorSize = 0;

        // Helper functions
        bool CreateGBufferResources();
        bool CreateOutputResources();
        bool CreateDescriptorHeaps();
        
        void DestroyResources();
    };
}

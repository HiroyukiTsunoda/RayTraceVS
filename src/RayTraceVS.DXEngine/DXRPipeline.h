#pragma once

#include <d3d12.h>
#include "d3dx12.h"
#include <wrl/client.h>
#include <memory>
#include <vector>
#include <DirectXMath.h>
#include <string>

using Microsoft::WRL::ComPtr;
using namespace DirectX;

namespace RayTraceVS::DXEngine
{
    class DXContext;
    class AccelerationStructure;
    class Scene;
    class RenderTarget;
    class NRDDenoiser;

    // Scene constants for compute shader
    struct alignas(256) SceneConstants
    {
        XMFLOAT3 CameraPosition;
        float CameraPadding1;
        XMFLOAT3 CameraForward;
        float CameraPadding2;
        XMFLOAT3 CameraRight;
        float CameraPadding3;
        XMFLOAT3 CameraUp;
        float CameraPadding4;
        XMFLOAT3 LightPosition;
        float LightIntensity;
        XMFLOAT4 LightColor;
        UINT NumSpheres;
        UINT NumPlanes;
        UINT NumBoxes;
        UINT NumLights;
        UINT ScreenWidth;
        UINT ScreenHeight;
        float AspectRatio;
        float TanHalfFov;
        UINT SamplesPerPixel;
        UINT MaxBounces;
        // Photon mapping parameters
        UINT NumPhotons;            // Number of photons to emit
        UINT PhotonMapSize;         // Current photon map size
        float PhotonRadius;         // Search radius for gathering
        float CausticIntensity;     // Intensity multiplier
        // DoF (Depth of Field) parameters
        float ApertureSize;         // 0.0 = DoF disabled, larger = stronger bokeh
        float FocusDistance;        // Distance to the focal plane
    };

    // Photon structure for caustics (must match HLSL)
    struct alignas(16) GPUPhoton
    {
        XMFLOAT3 Position;      // Hit position on diffuse surface
        float Power;            // Photon power/energy
        XMFLOAT3 Direction;     // Incoming direction
        UINT Flags;             // Flags: 0=empty, 1=valid caustic photon
        XMFLOAT3 Color;         // Photon color
        float Padding;
    };

    // ============================================
    // AoS (Array of Structures) - Original format for Compute Shader fallback
    // ============================================
    
    // GPU sphere data (with PBR material)
    struct alignas(16) GPUSphere
    {
        XMFLOAT3 Center;
        float Radius;
        XMFLOAT4 Color;
        float Metallic;
        float Roughness;
        float Transmission;
        float IOR;
    };

    // GPU plane data (with PBR material)
    struct alignas(16) GPUPlane
    {
        XMFLOAT3 Position;
        float Metallic;
        XMFLOAT3 Normal;
        float Roughness;
        XMFLOAT4 Color;
        float Transmission;
        float IOR;
        float Padding1;
        float Padding2;
    };

    // GPU box data (with PBR material)
    struct alignas(16) GPUBox
    {
        XMFLOAT3 Center;
        float Padding1;
        XMFLOAT3 Size;       // half-extents
        float Padding2;
        XMFLOAT4 Color;
        float Metallic;
        float Roughness;
        float Transmission;
        float IOR;
    };

    // ============================================
    // SoA (Structure of Arrays) - Optimized for DXR intersection
    // Separates geometry data from material data for better cache efficiency
    // ============================================

    // Geometry-only data for intersection tests (minimal size = faster intersection)
    struct alignas(16) SphereGeometry
    {
        XMFLOAT3 Center;
        float Radius;
    };

    struct alignas(16) PlaneGeometry
    {
        XMFLOAT3 Position;
        float Padding;
        XMFLOAT3 Normal;
        float Padding2;
    };

    struct alignas(16) BoxGeometry
    {
        XMFLOAT3 Center;
        float Padding1;
        XMFLOAT3 Size;
        float Padding2;
    };

    // Material data (only read after intersection is confirmed)
    struct alignas(16) ObjectMaterial
    {
        XMFLOAT4 Color;
        float Metallic;
        float Roughness;
        float Transmission;
        float IOR;
    };

    // Light type enum (must match shader)
    enum GPULightType : UINT
    {
        GPULightType_Ambient = 0,
        GPULightType_Point = 1,
        GPULightType_Directional = 2
    };

    // GPU light data
    struct alignas(16) GPULight
    {
        XMFLOAT3 Position;    // Position (Point) or Direction (Directional)
        float Intensity;
        XMFLOAT4 Color;
        UINT Type;            // 0=Ambient, 1=Point, 2=Directional
        XMFLOAT3 Padding;
    };

    class DXRPipeline
    {
    public:
        DXRPipeline(DXContext* context);
        ~DXRPipeline();

        bool Initialize();
        void BuildPipeline();
        void CreateRootSignatures();
        void CreatePipelineStateObject();
        void CreateShaderTables();

        void DispatchRays(UINT width, UINT height);
        
        // DXR ray tracing with hardware BVH
        void RenderWithDXR(RenderTarget* renderTarget, Scene* scene);
        
        // GPU Compute Shader ray tracing (fallback)
        void RenderWithComputeShader(RenderTarget* renderTarget, Scene* scene);
        
        // Main render function (auto-selects DXR or Compute)
        void Render(RenderTarget* renderTarget, Scene* scene);
        
        // Check if DXR pipeline is ready
        bool IsDXRReady() const { return dxrPipelineReady; }
        
        // Check if denoiser is enabled and ready
        bool IsDenoiserReady() const { return denoiserEnabled && denoiser != nullptr; }
        
        // Enable/disable denoiser
        void SetDenoiserEnabled(bool enabled) { denoiserEnabled = enabled; }
        bool GetDenoiserEnabled() const { return denoiserEnabled; }
        
        // Get denoiser for direct access (if needed)
        NRDDenoiser* GetDenoiser() const { return denoiser.get(); }

    private:
        DXContext* dxContext;
        bool dxrPipelineReady = false;

        // Compute shader pipeline (fallback)
        ComPtr<ID3D12RootSignature> computeRootSignature;
        ComPtr<ID3D12PipelineState> computePipelineState;
        ComPtr<ID3DBlob> computeShader;
        
        // Descriptor heap for compute shader
        ComPtr<ID3D12DescriptorHeap> computeSrvUavHeap;
        UINT srvUavDescriptorSize = 0;

        // Constant buffer
        ComPtr<ID3D12Resource> constantBuffer;
        SceneConstants* mappedConstantData = nullptr;

        // AoS Object buffers (for Compute Shader fallback)
        ComPtr<ID3D12Resource> sphereBuffer;
        ComPtr<ID3D12Resource> planeBuffer;
        ComPtr<ID3D12Resource> boxBuffer;
        ComPtr<ID3D12Resource> lightBuffer;

        // Upload buffers (for dynamic updates)
        ComPtr<ID3D12Resource> sphereUploadBuffer;
        ComPtr<ID3D12Resource> planeUploadBuffer;
        ComPtr<ID3D12Resource> boxUploadBuffer;
        ComPtr<ID3D12Resource> lightUploadBuffer;

        // ============================================
        // SoA Buffers (for DXR - optimized memory access)
        // ============================================
        
        // Geometry buffers (minimal data for intersection tests)
        ComPtr<ID3D12Resource> sphereGeometryBuffer;
        ComPtr<ID3D12Resource> planeGeometryBuffer;
        ComPtr<ID3D12Resource> boxGeometryBuffer;
        
        // Material buffer (shared by all object types)
        ComPtr<ID3D12Resource> materialBuffer;
        
        // Upload buffers for SoA
        ComPtr<ID3D12Resource> sphereGeometryUploadBuffer;
        ComPtr<ID3D12Resource> planeGeometryUploadBuffer;
        ComPtr<ID3D12Resource> boxGeometryUploadBuffer;
        ComPtr<ID3D12Resource> materialUploadBuffer;
        
        bool useSoABuffers = false;

        // ============================================
        // DXR Pipeline Resources
        // ============================================
        
        // Root signatures
        ComPtr<ID3D12RootSignature> globalRootSignature;
        ComPtr<ID3D12RootSignature> localRootSignature;
        
        // State object (RTPSO)
        ComPtr<ID3D12StateObject> stateObject;
        ComPtr<ID3D12StateObjectProperties> stateObjectProperties;
        
        // Shader tables
        ComPtr<ID3D12Resource> rayGenShaderTable;
        ComPtr<ID3D12Resource> missShaderTable;
        ComPtr<ID3D12Resource> hitGroupShaderTable;
        
        UINT shaderTableRecordSize = 0;
        
        // Acceleration structure
        std::unique_ptr<AccelerationStructure> accelerationStructure;
        
        // DXR descriptor heap
        ComPtr<ID3D12DescriptorHeap> dxrSrvUavHeap;
        UINT dxrDescriptorSize = 0;

        // Shader bytecode
        ComPtr<ID3DBlob> rayGenShader;
        ComPtr<ID3DBlob> closestHitShader;
        ComPtr<ID3DBlob> missShader;
        ComPtr<ID3DBlob> intersectionShader;
        
        // ============================================
        // Photon Mapping Resources (for Caustics)
        // ============================================
        
        // Photon map buffer
        ComPtr<ID3D12Resource> photonMapBuffer;
        ComPtr<ID3D12Resource> photonCounterBuffer;
        ComPtr<ID3D12Resource> photonCounterResetBuffer;  // For resetting counter to 0
        
        // Photon mapping shaders
        ComPtr<ID3DBlob> photonEmitShader;
        ComPtr<ID3DBlob> photonTraceClosestHitShader;
        ComPtr<ID3DBlob> photonTraceMissShader;
        
        // Photon tracing state object and tables
        ComPtr<ID3D12StateObject> photonStateObject;
        ComPtr<ID3D12StateObjectProperties> photonStateObjectProperties;
        ComPtr<ID3D12Resource> photonRayGenShaderTable;
        ComPtr<ID3D12Resource> photonMissShaderTable;
        ComPtr<ID3D12Resource> photonHitGroupShaderTable;
        
        // Photon descriptor heap (separate from main rendering)
        ComPtr<ID3D12DescriptorHeap> photonSrvUavHeap;
        
        // Photon mapping parameters
        UINT maxPhotons = 262144;   // 256K photons
        float photonRadius = 0.5f;
        float causticIntensity = 2.0f;
        UINT photonsPerLight = 65536;
        bool causticsEnabled = true;
        
        // Cached scene pointer for acceleration structure rebuild
        Scene* lastScene = nullptr;
        bool needsAccelerationStructureRebuild = true;

        // ============================================
        // Denoiser (NRD Integration)
        // ============================================
        
        std::unique_ptr<NRDDenoiser> denoiser;
        bool denoiserEnabled = false;  // NRD denoiser disabled until G-Buffer output is enabled in shaders
        
        // Frame tracking for motion vectors
        UINT frameIndex = 0;
        XMFLOAT4X4 prevViewMatrix;
        XMFLOAT4X4 prevProjMatrix;
        bool isFirstFrame = true;
        
        // Composite shader for combining denoised output
        ComPtr<ID3D12PipelineState> compositePipelineState;
        ComPtr<ID3D12RootSignature> compositeRootSignature;
        ComPtr<ID3D12DescriptorHeap> compositeDescriptorHeap;

        // ============================================
        // Helper Functions
        // ============================================
        
        bool LoadShader(const wchar_t* filename, ID3DBlob** shader);
        bool LoadPrecompiledShader(const std::wstring& filename, ID3DBlob** shader);
        bool CompileShaderFromFile(const std::wstring& filename, const char* entryPoint, const char* target, ID3DBlob** shader);  // Deprecated
        
        // Compute pipeline
        bool CreateComputePipeline();
        bool CreateBuffers(UINT width, UINT height);
        void UpdateSceneData(Scene* scene, UINT width, UINT height);
        void RenderErrorPattern(RenderTarget* renderTarget);
        
        // DXR pipeline
        bool CreateDXRPipeline();
        bool CreateGlobalRootSignature();
        bool CreateLocalRootSignature();
        bool CreateDXRStateObject();
        bool CreateDXRShaderTables();
        bool CreateDXRDescriptorHeap();
        bool BuildAccelerationStructures(Scene* scene);
        void UpdateDXRDescriptors(RenderTarget* renderTarget);
        
        // Photon mapping (for caustics)
        bool CreatePhotonMappingResources();
        bool CreatePhotonStateObject();
        bool CreatePhotonShaderTables();
        void EmitPhotons(Scene* scene);
        void UpdatePhotonDescriptors();
        void ClearPhotonMap();
        
        // Denoiser (NRD)
        bool InitializeDenoiser(UINT width, UINT height);
        void ApplyDenoising(RenderTarget* renderTarget, Scene* scene);
        bool CreateCompositePipeline();
        void CompositeOutput(RenderTarget* renderTarget);
    };
}

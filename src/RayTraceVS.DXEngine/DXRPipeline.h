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
    class ShaderCache;

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
        UINT PhotonDebugMode;       // 0 = off, 1+ = debug visualization
        float PhotonDebugScale;     // Debug intensity scale
        float PhotonDebugPadding[2];
        // DoF (Depth of Field) parameters
        float ApertureSize;         // 0.0 = DoF disabled, larger = stronger bokeh
        float FocusDistance;        // Distance to the focal plane
        // Shadow parameters
        float ShadowStrength;       // 0.0 = no shadow, 1.0 = normal, >1.0 = darker
        UINT FrameIndex;            // Frame counter for temporal noise variation
        // Mesh instance count
        UINT NumMeshInstances;      // Number of FBX mesh instances
        UINT MeshPadding[3];        // Padding for 16-byte alignment
        // Matrices for motion vectors (column-major for HLSL)
        XMFLOAT4X4 ViewProjection;
        XMFLOAT4X4 PrevViewProjection;
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
    // Spatial Hash for Photon Gathering
    // ============================================
    
    static constexpr UINT PHOTON_HASH_TABLE_SIZE = 65536;   // 2^16 hash buckets
    static constexpr UINT MAX_PHOTONS_PER_CELL = 64;        // Max photons per cell
    
    // Photon hash cell structure (must match HLSL)
    struct PhotonHashCell
    {
        UINT Count;
        UINT PhotonIndices[MAX_PHOTONS_PER_CELL];
    };
    
    // Constants for photon hash compute shader
    struct alignas(16) PhotonHashConstants
    {
        UINT PhotonCount;
        float CellSize;
        float Padding[2];
    };

    // ============================================
    // AoS (Array of Structures) - Original format for Compute Shader fallback
    // ============================================
    
    // GPU sphere data (with PBR material) - 80 bytes, 16-byte aligned
    struct alignas(16) GPUSphere
    {
        XMFLOAT3 Center;        // 12
        float Radius;           // 4  -> 16
        XMFLOAT4 Color;         // 16 -> 32
        float Metallic;         // 4
        float Roughness;        // 4
        float Transmission;     // 4
        float IOR;              // 4  -> 48
        float Specular;         // 4
        float Padding1;         // 4
        float Padding2;         // 4
        float Padding3;         // 4  -> 64
        XMFLOAT3 Emission;      // 12
        float Padding4;         // 4  -> 80
        XMFLOAT3 Absorption;    // 12 (sigmaA)
        float Padding5;         // 4  -> 96
    };

    // GPU plane data (with PBR material) - 80 bytes, 16-byte aligned
    struct alignas(16) GPUPlane
    {
        XMFLOAT3 Position;      // 12
        float Metallic;         // 4  -> 16
        XMFLOAT3 Normal;        // 12
        float Roughness;        // 4  -> 32
        XMFLOAT4 Color;         // 16 -> 48
        float Transmission;     // 4
        float IOR;              // 4
        float Specular;         // 4
        float Padding1;         // 4  -> 64
        XMFLOAT3 Emission;      // 12
        float Padding2;         // 4  -> 80
        XMFLOAT3 Absorption;    // 12 (sigmaA)
        float Padding3;         // 4  -> 96
    };

    // GPU box data (with PBR material and rotation) - 144 bytes, 16-byte aligned
    // OBB (Oriented Bounding Box) support via local axes
    struct alignas(16) GPUBox
    {
        XMFLOAT3 Center;        // 12
        float Padding1;         // 4  -> 16
        XMFLOAT3 Size;          // 12 (half-extents)
        float Padding2;         // 4  -> 32
        // Local axes (rotation matrix columns) - for OBB
        XMFLOAT3 AxisX;         // 12 (local X axis in world space)
        float Padding3;         // 4  -> 48
        XMFLOAT3 AxisY;         // 12 (local Y axis in world space)
        float Padding4;         // 4  -> 64
        XMFLOAT3 AxisZ;         // 12 (local Z axis in world space)
        float Padding5;         // 4  -> 80
        XMFLOAT4 Color;         // 16 -> 96
        float Metallic;         // 4
        float Roughness;        // 4
        float Transmission;     // 4
        float IOR;              // 4  -> 112
        float Specular;         // 4
        float Padding6;         // 4
        float Padding7;         // 4
        float Padding8;         // 4  -> 128
        XMFLOAT3 Emission;      // 12
        float Padding9;         // 4  -> 144
        XMFLOAT3 Absorption;    // 12 (sigmaA)
        float Padding10;        // 4  -> 160
    };

    // ============================================
    // GPU Mesh Data Structures (for FBX triangle meshes)
    // ============================================

    // GPU mesh vertex - 32 bytes (matches cache format)
    struct alignas(16) GPUMeshVertex
    {
        XMFLOAT3 Position;      // 12
        float Padding1;         // 4  -> 16
        XMFLOAT3 Normal;        // 12
        float Padding2;         // 4  -> 32
    };

    // GPU mesh info - 16 bytes (offset info per mesh type)
    struct alignas(16) GPUMeshInfo
    {
        UINT VertexOffset;      // 4 - offset in global vertex buffer
        UINT IndexOffset;       // 4 - offset in global index buffer
        UINT VertexCount;       // 4 - vertex count for this mesh type
        UINT IndexCount;        // 4 - index count for this mesh type -> 16
    };

    // GPU mesh material - 64 bytes (per instance)
    struct alignas(16) GPUMeshMaterial
    {
        XMFLOAT4 Color;         // 16 -> 16
        float Metallic;         // 4
        float Roughness;        // 4
        float Transmission;     // 4
        float IOR;              // 4  -> 32
        float Specular;         // 4
        XMFLOAT3 Emission;      // 12 -> 48
        float Padding1;         // 4
        float Padding2;         // 4
        float Padding3;         // 4
        float Padding4;         // 4  -> 64
        XMFLOAT3 Absorption;    // 12 (sigmaA)
        float Padding5;         // 4  -> 80
    };

    // GPU mesh instance info - 8 bytes (maps TLAS instance to mesh/material)
    struct GPUMeshInstanceInfo
    {
        UINT MeshTypeIndex;     // 4 - index into MeshInfos (which mesh type)
        UINT MaterialIndex;     // 4 - index into MeshMaterials -> 8
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
        float Radius;         // Area light radius (0 = point light, hard shadows)
        float SoftShadowSamples; // Number of shadow samples (1-16)
        float Padding;
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

        // Shader paths (fixed locations)
        // - shaderSourcePath: C:\git\RayTraceVS\src\Shader\ (for .hlsl source files)
        // - shaderBasePath:   C:\git\RayTraceVS\src\Shader\Cache\ (for .cso compiled files)
        std::wstring shaderBasePath;      // Cache directory for .cso files
        std::wstring shaderSourcePath;    // Source directory for .hlsl files
        bool InitializeShaderPath();
        std::wstring GetShaderPath(const std::wstring& filename) const { return shaderBasePath + filename; }
        std::wstring GetShaderSourcePath(const std::wstring& filename) const { return shaderSourcePath + filename; }

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

        // Per-frame UI parameters
        float exposure = 1.0f;
        int toneMapOperator = 2;
        float denoiserStabilization = 1.0f;
        float shadowStrength = 1.0f;
        float gamma = 1.0f;

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
        // Mesh Buffers (for FBX triangle meshes)
        // ============================================
        
        ComPtr<ID3D12Resource> meshVertexBuffer;      // t5 - Combined vertex data for all mesh types
        ComPtr<ID3D12Resource> meshIndexBuffer;       // t6 - Combined index data for all mesh types
        ComPtr<ID3D12Resource> meshMaterialBuffer;    // t7 - Material per instance
        ComPtr<ID3D12Resource> meshInfoBuffer;        // t8 - MeshInfo per mesh type
        ComPtr<ID3D12Resource> meshInstanceBuffer;    // t9 - MeshInstanceInfo per instance

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
        ComPtr<ID3DBlob> closestHitTriangleShader;  // For triangle meshes
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
        UINT maxPhotons = 262144;   // 256K photons (TDR safety)
        float photonRadius = 0.5f;
        float causticIntensity = 3.0f;
        UINT photonsPerLight = 32768;
        bool causticsEnabled = false;
        
        // ============================================
        // Photon Hash Table Resources (Spatial Hash)
        // ============================================
        
        // Hash table buffer
        ComPtr<ID3D12Resource> photonHashTableBuffer;
        
        // Constants buffer for hash compute shaders
        ComPtr<ID3D12Resource> photonHashConstantBuffer;
        PhotonHashConstants* mappedPhotonHashConstants = nullptr;
        
        // Compute pipeline for hash table construction
        ComPtr<ID3D12RootSignature> photonHashRootSignature;
        ComPtr<ID3D12PipelineState> photonHashClearPipeline;
        ComPtr<ID3D12PipelineState> photonHashBuildPipeline;
        ComPtr<ID3DBlob> photonHashClearShader;
        ComPtr<ID3DBlob> photonHashBuildShader;
        
        // Descriptor heap for hash compute shaders
        ComPtr<ID3D12DescriptorHeap> photonHashDescriptorHeap;
        
        // Cached scene pointer for acceleration structure rebuild
        Scene* lastScene = nullptr;
        bool needsAccelerationStructureRebuild = true;

        // Trace recursion depth (DXR pipeline config)
        UINT maxTraceRecursionDepth = 2;
        UINT currentTraceRecursionDepth = 2;
        
        // Cached object counts for detecting scene changes
        UINT lastSphereCount = 0;
        UINT lastPlaneCount = 0;
        UINT lastBoxCount = 0;
        UINT lastMeshInstanceCount = 0;

        // ============================================
        // Shader Cache System
        // ============================================
        
        std::unique_ptr<ShaderCache> shaderCache;

        // ============================================
        // Denoiser (NRD Integration)
        // ============================================
        
        std::unique_ptr<NRDDenoiser> denoiser;
        bool denoiserEnabled = true;  // NRD denoiser enabled - G-Buffer output is ready
        
        // Frame tracking for motion vectors
        UINT frameIndex = 0;
        XMFLOAT4X4 prevViewMatrix;
        XMFLOAT4X4 prevProjMatrix;
        bool isFirstFrame = true;
        
        // Composite shader for combining denoised output
        ComPtr<ID3D12PipelineState> compositePipelineState;
        ComPtr<ID3D12RootSignature> compositeRootSignature;
        ComPtr<ID3D12DescriptorHeap> compositeDescriptorHeap;
        ComPtr<ID3D12DescriptorHeap> compositeUavCpuHeap;
        ComPtr<ID3D12DescriptorHeap> computeUavCpuHeap;
        
        // ============================================
        // Custom Shadow Denoiser (replaces SIGMA)
        // ============================================
        
        ComPtr<ID3D12PipelineState> shadowDenoisePipelineState;
        ComPtr<ID3D12RootSignature> shadowDenoiseRootSignature;
        ComPtr<ID3D12DescriptorHeap> shadowDenoiseDescriptorHeap;
        ComPtr<ID3D12Resource> shadowDenoiseConstantBuffer;
        bool useCustomShadowDenoiser = true;  // Use custom denoiser instead of SIGMA
        
        bool CreateShadowDenoisePipeline();
        void ApplyCustomShadowDenoising();

        // ============================================
        // Helper Functions
        // ============================================
        
        bool LoadShader(const wchar_t* filename, ID3DBlob** shader);
        bool LoadPrecompiledShader(const std::wstring& filename, ID3DBlob** shader);
        bool CompileShaderFromFile(const std::wstring& filename, const char* entryPoint, const char* target, ID3DBlob** shader);  // Deprecated
        std::wstring ResolveDXRShaderSourcePath(const std::wstring& shaderName) const;
        bool CompileDXRShaderFromSource(const std::wstring& shaderName, ID3DBlob** shader);  // Compile DXIL library from source
        bool LoadOrCompileDXRShader(const std::wstring& shaderName, ID3DBlob** shader);  // Try source first, fall back to .cso
        
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
        
        // Photon hash table (spatial hash for O(1) lookup)
        bool CreatePhotonHashResources();
        void BuildPhotonHashTable();
        
        // Denoiser (NRD)
        bool InitializeDenoiser(UINT width, UINT height);
        void ApplyDenoising(RenderTarget* renderTarget, Scene* scene);
        bool CreateCompositePipeline();
        void CompositeOutput(RenderTarget* renderTarget);
    };
}

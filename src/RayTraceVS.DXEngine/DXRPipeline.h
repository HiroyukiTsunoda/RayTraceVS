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
        UINT NumCylinders;
        UINT NumLights;
        UINT ScreenWidth;
        UINT ScreenHeight;
        float AspectRatio;
        float TanHalfFov;
        UINT SamplesPerPixel;
        UINT MaxBounces;
        float Padding1;
        float Padding2;
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

    // GPU cylinder data (with PBR material)
    struct alignas(16) GPUCylinder
    {
        XMFLOAT3 Position;
        float Radius;
        XMFLOAT3 Axis;
        float Height;
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

    struct alignas(16) CylinderGeometry
    {
        XMFLOAT3 Position;
        float Radius;
        XMFLOAT3 Axis;
        float Height;
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
        ComPtr<ID3D12Resource> cylinderBuffer;
        ComPtr<ID3D12Resource> lightBuffer;

        // Upload buffers (for dynamic updates)
        ComPtr<ID3D12Resource> sphereUploadBuffer;
        ComPtr<ID3D12Resource> planeUploadBuffer;
        ComPtr<ID3D12Resource> cylinderUploadBuffer;
        ComPtr<ID3D12Resource> lightUploadBuffer;

        // ============================================
        // SoA Buffers (for DXR - optimized memory access)
        // ============================================
        
        // Geometry buffers (minimal data for intersection tests)
        ComPtr<ID3D12Resource> sphereGeometryBuffer;
        ComPtr<ID3D12Resource> planeGeometryBuffer;
        ComPtr<ID3D12Resource> cylinderGeometryBuffer;
        
        // Material buffer (shared by all object types)
        ComPtr<ID3D12Resource> materialBuffer;
        
        // Upload buffers for SoA
        ComPtr<ID3D12Resource> sphereGeometryUploadBuffer;
        ComPtr<ID3D12Resource> planeGeometryUploadBuffer;
        ComPtr<ID3D12Resource> cylinderGeometryUploadBuffer;
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
        
        // Cached scene pointer for acceleration structure rebuild
        Scene* lastScene = nullptr;
        bool needsAccelerationStructureRebuild = true;

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
    };
}

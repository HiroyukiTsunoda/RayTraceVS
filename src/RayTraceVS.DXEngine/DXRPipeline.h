#pragma once

#include <d3d12.h>
#include "d3dx12.h"
#include <wrl/client.h>
#include <memory>
#include <vector>
#include <DirectXMath.h>

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
        
        // GPU Compute Shader ray tracing
        void RenderWithComputeShader(RenderTarget* renderTarget, Scene* scene);

    private:
        DXContext* dxContext;

        // Compute shader pipeline
        ComPtr<ID3D12RootSignature> computeRootSignature;
        ComPtr<ID3D12PipelineState> computePipelineState;
        ComPtr<ID3DBlob> computeShader;
        
        // Descriptor heap for compute shader
        ComPtr<ID3D12DescriptorHeap> computeSrvUavHeap;
        UINT srvUavDescriptorSize;

        // Constant buffer
        ComPtr<ID3D12Resource> constantBuffer;
        SceneConstants* mappedConstantData;

        // Object buffers
        ComPtr<ID3D12Resource> sphereBuffer;
        ComPtr<ID3D12Resource> planeBuffer;
        ComPtr<ID3D12Resource> cylinderBuffer;
        ComPtr<ID3D12Resource> lightBuffer;

        // Upload buffers (for dynamic updates)
        ComPtr<ID3D12Resource> sphereUploadBuffer;
        ComPtr<ID3D12Resource> planeUploadBuffer;
        ComPtr<ID3D12Resource> cylinderUploadBuffer;
        ComPtr<ID3D12Resource> lightUploadBuffer;

        // DXR (future)
        ComPtr<ID3D12RootSignature> globalRootSignature;
        ComPtr<ID3D12RootSignature> localRootSignature;
        ComPtr<ID3D12StateObject> stateObject;
        ComPtr<ID3D12Resource> shaderTable;

        // Shader bytecode
        ComPtr<ID3DBlob> rayGenShader;
        ComPtr<ID3DBlob> closestHitShader;
        ComPtr<ID3DBlob> missShader;
        ComPtr<ID3DBlob> intersectionShader;

        bool LoadShader(const wchar_t* filename, ID3DBlob** shader);
        bool CreateComputePipeline();
        bool CreateBuffers(UINT width, UINT height);
        void UpdateSceneData(Scene* scene, UINT width, UINT height);
        void RenderErrorPattern(RenderTarget* renderTarget);
    };
}

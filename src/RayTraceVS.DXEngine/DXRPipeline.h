#pragma once

#include <d3d12.h>
#include "d3dx12.h"
#include <wrl/client.h>
#include <memory>

using Microsoft::WRL::ComPtr;

namespace RayTraceVS::DXEngine
{
    class DXContext;
    class AccelerationStructure;

    class RenderTarget;

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
        
        // Test pattern rendering (for verification before DXR implementation)
        void RenderTestPattern(RenderTarget* renderTarget);

    private:
        DXContext* dxContext;

        ComPtr<ID3D12RootSignature> globalRootSignature;
        ComPtr<ID3D12RootSignature> localRootSignature;
        ComPtr<ID3D12StateObject> stateObject;
        ComPtr<ID3D12Resource> shaderTable;

        // Shader bytecode
        ComPtr<ID3DBlob> rayGenShader;
        ComPtr<ID3DBlob> closestHitShader;
        ComPtr<ID3DBlob> missShader;
        ComPtr<ID3DBlob> intersectionShader;
        
        // Upload buffer (keep until GPU finishes using it)
        ComPtr<ID3D12Resource> uploadBuffer;

        bool LoadShader(const wchar_t* filename, ID3DBlob** shader);
    };
}

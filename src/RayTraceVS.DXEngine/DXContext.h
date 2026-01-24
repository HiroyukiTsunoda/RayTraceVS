#pragma once

#include <d3d12.h>
#include <dxgi1_6.h>
#include "d3dx12.h"
#include <wrl/client.h>
#include <stdexcept>
#include <memory>

using Microsoft::WRL::ComPtr;

namespace RayTraceVS::DXEngine
{
    class DXContext
    {
    public:
        DXContext();
        ~DXContext();

        bool Initialize(HWND hwnd, int width, int height);
        void Shutdown();

        ID3D12Device5* GetDevice() const;
        ID3D12GraphicsCommandList4* GetCommandList() const;
        ID3D12CommandQueue* GetCommandQueue() const;

        bool CheckDXRSupport();
        bool IsDXRSupported() const { return isDXRSupported; }
        D3D12_RAYTRACING_TIER GetRaytracingTier() const { return raytracingTier; }
        
        // Get adapter for driver information (used by ShaderCache)
        IDXGIAdapter1* GetAdapter() const { return adapter.Get(); }
        
        void CreateCommandQueue();
        void CreateCommandAllocatorAndList();
        void CreateSwapChain(HWND hwnd, int width, int height);
        void CreateFence();

        void WaitForGPU();
        void MoveToNextFrame();
        void ResetCommandList();
        void MarkCommandListClosed();
        ID3D12CommandAllocator* GetCommandAllocator() const;

    private:
        ComPtr<IDXGIFactory4> dxgiFactory;
        ComPtr<IDXGIAdapter1> adapter;
        ComPtr<ID3D12Device5> device;

        ComPtr<ID3D12CommandQueue> commandQueue;
        ComPtr<ID3D12CommandAllocator> commandAllocator;
        ComPtr<ID3D12GraphicsCommandList4> commandList;
        bool commandListOpen = false;

        ComPtr<IDXGISwapChain3> swapChain;
        static const UINT frameCount = 2;
        UINT currentFrameIndex = 0;

        ComPtr<ID3D12Fence> fence;
        UINT64 fenceValue = 0;
        HANDLE fenceEvent = nullptr;

        ComPtr<ID3D12DescriptorHeap> rtvHeap;
        UINT rtvDescriptorSize = 0;

        ComPtr<ID3D12Resource> renderTargets[frameCount];

        bool isDXRSupported = false;
        D3D12_RAYTRACING_TIER raytracingTier = D3D12_RAYTRACING_TIER_NOT_SUPPORTED;
    };

    inline ID3D12Device5* DXContext::GetDevice() const { return device.Get(); }
    inline ID3D12GraphicsCommandList4* DXContext::GetCommandList() const { return commandList.Get(); }
    inline ID3D12CommandQueue* DXContext::GetCommandQueue() const { return commandQueue.Get(); }
    inline ID3D12CommandAllocator* DXContext::GetCommandAllocator() const { return commandAllocator.Get(); }
}

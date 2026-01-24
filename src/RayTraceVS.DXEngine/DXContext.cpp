#include "DXContext.h"
#include <d3d12.h>
#include <d3d12sdklayers.h>
#include <dxgi1_6.h>
#include <stdexcept>
#include <stdio.h>

#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")

namespace RayTraceVS::DXEngine
{
    DXContext::DXContext()
    {
    }

    DXContext::~DXContext()
    {
        Shutdown();
    }

    bool DXContext::Initialize(HWND hwnd, int width, int height)
    {
        try
        {
            // Debug layer (Debug build only)
#if defined(_DEBUG)
            ComPtr<ID3D12Debug> debugController;
            if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&debugController))))
            {
                debugController->EnableDebugLayer();

                // Enable GPU-based validation for deeper debug output
                ComPtr<ID3D12Debug1> debugController1;
                if (SUCCEEDED(debugController.As(&debugController1)))
                {
                    debugController1->SetEnableGPUBasedValidation(TRUE);
                    debugController1->SetEnableSynchronizedCommandQueueValidation(TRUE);
                    OutputDebugStringA("D3D12 debug layer: GPU-based validation enabled\n");
                }
            }

            ComPtr<ID3D12DeviceRemovedExtendedDataSettings> dredSettings;
            if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&dredSettings))))
            {
                dredSettings->SetAutoBreadcrumbsEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
                dredSettings->SetPageFaultEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
                dredSettings->SetWatsonDumpEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
                OutputDebugStringA("DRED enabled\n");
            }
#endif

            // Create DXGI factory
            if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&dxgiFactory))))
            {
                throw std::runtime_error("Failed to create DXGI factory");
            }

            // Enumerate adapters
            for (UINT i = 0; dxgiFactory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; ++i)
            {
                DXGI_ADAPTER_DESC1 desc;
                adapter->GetDesc1(&desc);

                // Skip software adapter
                if (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)
                    continue;

                // Try to create D3D12 device
                if (SUCCEEDED(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_1, IID_PPV_ARGS(&device))))
                    break;
            }

            if (!device)
            {
                throw std::runtime_error("Failed to create D3D12 device");
            }

#if defined(_DEBUG)
            // Break on severe validation issues to surface exact failing API call
            ComPtr<ID3D12InfoQueue> infoQueue;
            if (SUCCEEDED(device.As(&infoQueue)))
            {
                infoQueue->SetBreakOnSeverity(D3D12_MESSAGE_SEVERITY_CORRUPTION, TRUE);
                infoQueue->SetBreakOnSeverity(D3D12_MESSAGE_SEVERITY_ERROR, TRUE);
                OutputDebugStringA("D3D12 InfoQueue: break on error/corruption enabled\n");
            }
#endif

            // Check DXR support (fallback to compute shader if not supported)
            CheckDXRSupport();
            if (!isDXRSupported)
            {
                OutputDebugStringA("DXR not supported - falling back to Compute Shader pipeline\n");
            }
            else
            {
                OutputDebugStringA("DXR supported - using hardware ray tracing\n");
            }

            // Create command queue
            CreateCommandQueue();

            // Create command allocator and list
            CreateCommandAllocatorAndList();

            // Create swap chain
            CreateSwapChain(hwnd, width, height);

            // Create fence
            CreateFence();

            return true;
        }
        catch (const std::exception& ex)
        {
            char buf[512];
            sprintf_s(buf, "DXContext::Initialize failed: %s\n", ex.what());
            OutputDebugStringA(buf);
            return false;
        }
    }

    void DXContext::Shutdown()
    {
        WaitForGPU();

        if (fenceEvent)
        {
            CloseHandle(fenceEvent);
            fenceEvent = nullptr;
        }
    }

    bool DXContext::CheckDXRSupport()
    {
        D3D12_FEATURE_DATA_D3D12_OPTIONS5 options5 = {};
        if (FAILED(device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS5, &options5, sizeof(options5))))
        {
            raytracingTier = D3D12_RAYTRACING_TIER_NOT_SUPPORTED;
            isDXRSupported = false;
            return false;
        }

        raytracingTier = options5.RaytracingTier;
        isDXRSupported = (raytracingTier >= D3D12_RAYTRACING_TIER_1_0);
        
        // Log raytracing tier
        char buf[128];
        sprintf_s(buf, "Raytracing Tier: %d (1.0=%d, 1.1=%d)\n", 
            raytracingTier, D3D12_RAYTRACING_TIER_1_0, D3D12_RAYTRACING_TIER_1_1);
        OutputDebugStringA(buf);
        
        return isDXRSupported;
    }

    void DXContext::CreateCommandQueue()
    {
        D3D12_COMMAND_QUEUE_DESC queueDesc = {};
        queueDesc.Flags = D3D12_COMMAND_QUEUE_FLAG_NONE;
        queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;

        if (FAILED(device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&commandQueue))))
        {
            throw std::runtime_error("Failed to create command queue");
        }
        commandQueue->SetName(L"MainCommandQueue");
    }

    void DXContext::CreateCommandAllocatorAndList()
    {
        if (FAILED(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&commandAllocator))))
        {
            throw std::runtime_error("Failed to create command allocator");
        }
        commandAllocator->SetName(L"MainCommandAllocator");

        if (FAILED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, commandAllocator.Get(), nullptr, IID_PPV_ARGS(&commandList))))
        {
            throw std::runtime_error("Failed to create command list");
        }
        commandList->SetName(L"MainCommandList");

        commandList->Close();
        commandListOpen = false;
    }

    void DXContext::CreateSwapChain(HWND hwnd, int width, int height)
    {
        char buf[256];
        sprintf_s(buf, "CreateSwapChain: hwnd=%p, width=%d, height=%d\n", hwnd, width, height);
        OutputDebugStringA(buf);

        // hwndの検証
        if (!hwnd || !IsWindow(hwnd))
        {
            sprintf_s(buf, "CreateSwapChain: Invalid HWND (%p, IsWindow=%d)\n", hwnd, hwnd ? IsWindow(hwnd) : 0);
            OutputDebugStringA(buf);
            throw std::runtime_error("Invalid window handle");
        }

        // サイズの検証
        if (width <= 0 || height <= 0)
        {
            sprintf_s(buf, "CreateSwapChain: Invalid size (%dx%d)\n", width, height);
            OutputDebugStringA(buf);
            throw std::runtime_error("Invalid swap chain size");
        }

        DXGI_SWAP_CHAIN_DESC1 swapChainDesc = {};
        swapChainDesc.BufferCount = frameCount;
        swapChainDesc.Width = width;
        swapChainDesc.Height = height;
        swapChainDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        swapChainDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        swapChainDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        swapChainDesc.SampleDesc.Count = 1;

        ComPtr<IDXGISwapChain1> swapChain1;
        HRESULT hr = dxgiFactory->CreateSwapChainForHwnd(commandQueue.Get(), hwnd, &swapChainDesc, nullptr, nullptr, &swapChain1);
        if (FAILED(hr))
        {
            sprintf_s(buf, "CreateSwapChainForHwnd failed (HRESULT: 0x%08X)\n", hr);
            OutputDebugStringA(buf);
            throw std::runtime_error(buf);
        }
        OutputDebugStringA("CreateSwapChainForHwnd: Success\n");

        hr = swapChain1.As(&swapChain);
        if (FAILED(hr))
        {
            sprintf_s(buf, "SwapChain cast failed (HRESULT: 0x%08X)\n", hr);
            OutputDebugStringA(buf);
            throw std::runtime_error(buf);
        }
        OutputDebugStringA("SwapChain cast: Success\n");

        currentFrameIndex = swapChain->GetCurrentBackBufferIndex();
        sprintf_s(buf, "Initial frame index: %u\n", currentFrameIndex);
        OutputDebugStringA(buf);

        // Create RTV descriptor heap
        D3D12_DESCRIPTOR_HEAP_DESC rtvHeapDesc = {};
        rtvHeapDesc.NumDescriptors = frameCount;
        rtvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        rtvHeapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;

        if (FAILED(device->CreateDescriptorHeap(&rtvHeapDesc, IID_PPV_ARGS(&rtvHeap))))
        {
            throw std::runtime_error("Failed to create RTV descriptor heap");
        }

        rtvDescriptorSize = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);

        // Create render targets
        CD3DX12_CPU_DESCRIPTOR_HANDLE rtvHandle(rtvHeap->GetCPUDescriptorHandleForHeapStart());

        for (UINT i = 0; i < frameCount; i++)
        {
            HRESULT hr = swapChain->GetBuffer(i, IID_PPV_ARGS(&renderTargets[i]));
            if (FAILED(hr))
            {
                char buf[256];
                sprintf_s(buf, "Failed to get swap chain buffer %u (HRESULT: 0x%08X)\n", i, hr);
                OutputDebugStringA(buf);
                throw std::runtime_error(buf);
            }

            // GetBufferが成功してもポインタがnullの場合のチェック
            if (!renderTargets[i])
            {
                char buf[256];
                sprintf_s(buf, "GetBuffer succeeded but render target %u is null (HRESULT was: 0x%08X)\n", i, hr);
                OutputDebugStringA(buf);
                throw std::runtime_error(buf);
            }
            
            device->CreateRenderTargetView(renderTargets[i].Get(), nullptr, rtvHandle);
            rtvHandle.Offset(1, rtvDescriptorSize);
        }
    }

    void DXContext::CreateFence()
    {
        if (FAILED(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence))))
        {
            throw std::runtime_error("Failed to create fence");
        }

        fenceValue = 1;

        fenceEvent = CreateEvent(nullptr, FALSE, FALSE, nullptr);
        if (fenceEvent == nullptr)
        {
            throw std::runtime_error("Failed to create fence event");
        }
    }

    void DXContext::WaitForGPU()
    {
        const UINT64 currentFenceValue = fenceValue;
        if (FAILED(commandQueue->Signal(fence.Get(), currentFenceValue)))
        {
            throw std::runtime_error("Failed to signal fence");
        }

        fenceValue++;

        if (fence->GetCompletedValue() < currentFenceValue)
        {
            if (FAILED(fence->SetEventOnCompletion(currentFenceValue, fenceEvent)))
            {
                throw std::runtime_error("Failed to set fence event");
            }

            WaitForSingleObject(fenceEvent, INFINITE);
        }
    }

    void DXContext::MoveToNextFrame()
    {
        const UINT64 currentFenceValue = fenceValue;
        if (FAILED(commandQueue->Signal(fence.Get(), currentFenceValue)))
        {
            throw std::runtime_error("Failed to signal fence");
        }

        currentFrameIndex = swapChain->GetCurrentBackBufferIndex();

        if (fence->GetCompletedValue() < fenceValue)
        {
            if (FAILED(fence->SetEventOnCompletion(fenceValue, fenceEvent)))
            {
                throw std::runtime_error("Failed to set fence event");
            }

            WaitForSingleObject(fenceEvent, INFINITE);
        }

        fenceValue = currentFenceValue + 1;
    }

    void DXContext::ResetCommandList()
    {
        try
        {
            if (!commandAllocator || !commandList)
                return;

            if (commandListOpen)
            {
                commandList->Close();
                commandListOpen = false;
            }

            HRESULT hr = commandAllocator->Reset();
            if (FAILED(hr))
                throw std::runtime_error("Failed to reset command allocator");

            hr = commandList->Reset(commandAllocator.Get(), nullptr);
            if (FAILED(hr))
                throw std::runtime_error("Failed to reset command list");

            commandListOpen = true;
        }
        catch (const std::exception&)
        {
            throw;
        }
    }

    void DXContext::MarkCommandListClosed()
    {
        commandListOpen = false;
    }
}

#include "DXContext.h"
#include "DebugLog.h"
#include <d3d12.h>
#include <d3d12sdklayers.h>
#include <dxgi1_6.h>
#include <stdexcept>
#include <stdio.h>
#include <vector>
#include <string>
#include <algorithm>

#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")

namespace RayTraceVS::DXEngine
{
    namespace
    {
        const char* BreadcrumbOperationName(D3D12_AUTO_BREADCRUMB_OP operation)
        {
            switch (operation)
            {
            case D3D12_AUTO_BREADCRUMB_OP_BUILDRAYTRACINGACCELERATIONSTRUCTURE: return "BuildRaytracingAccelerationStructure";
            case D3D12_AUTO_BREADCRUMB_OP_DISPATCHRAYS: return "DispatchRays";
            case D3D12_AUTO_BREADCRUMB_OP_DISPATCH: return "Dispatch";
            case D3D12_AUTO_BREADCRUMB_OP_COPYBUFFERREGION: return "CopyBufferRegion";
            case D3D12_AUTO_BREADCRUMB_OP_COPYTEXTUREREGION: return "CopyTextureRegion";
            case D3D12_AUTO_BREADCRUMB_OP_COPYRESOURCE: return "CopyResource";
            case D3D12_AUTO_BREADCRUMB_OP_RESOURCEBARRIER: return "ResourceBarrier";
            case D3D12_AUTO_BREADCRUMB_OP_SETPIPELINESTATE1: return "SetPipelineState1";
            default: return "Other";
            }
        }

        std::string DredObjectName(const char* ascii, const wchar_t* wide)
        {
            if (ascii) return ascii;
            if (!wide) return "(unnamed)";
            const int size = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
            if (size <= 0) return "(unnamed)";
            std::string name(static_cast<size_t>(size), '\0');
            WideCharToMultiByte(CP_UTF8, 0, wide, -1, name.data(), size, nullptr, nullptr);
            name.pop_back();
            return name;
        }

        std::string CollectDredFailure(ID3D12Device* device)
        {
            if (!device || SUCCEEDED(device->GetDeviceRemovedReason())) return {};
            ComPtr<ID3D12DeviceRemovedExtendedData> dred;
            HRESULT hr = device->QueryInterface(IID_PPV_ARGS(&dred));
            if (FAILED(hr))
            {
                LOG_ERROR_HR("DRED interface unavailable", hr);
                return "DRED interface unavailable";
            }

            std::string summary;
            D3D12_DRED_AUTO_BREADCRUMBS_OUTPUT breadcrumbs = {};
            hr = dred->GetAutoBreadcrumbsOutput(&breadcrumbs);
            if (SUCCEEDED(hr))
            {
                if (!breadcrumbs.pHeadAutoBreadcrumbNode) LOG_ERROR("DRED: no breadcrumb nodes");
                unsigned int nodeCount = 0;
                for (auto* node = breadcrumbs.pHeadAutoBreadcrumbNode; node && nodeCount++ < 32; node = node->pNext)
                {
                    const UINT completed = node->pLastBreadcrumbValue ? *node->pLastBreadcrumbValue : 0;
                    const std::string listName = DredObjectName(node->pCommandListDebugNameA, node->pCommandListDebugNameW);
                    char line[512];
                    sprintf_s(line, "DRED list=%s completed=%u/%u", listName.c_str(), completed, node->BreadcrumbCount);
                    LOG_ERROR(line);
                    if (!node->pCommandHistory || node->BreadcrumbCount == 0) continue;
                    const UINT center = (std::min)(completed, node->BreadcrumbCount - 1);
                    const UINT start = center > 3 ? center - 3 : 0;
                    const UINT end = (std::min)(center + 4, node->BreadcrumbCount);
                    for (UINT index = start; index < end; ++index)
                    {
                        const auto operation = node->pCommandHistory[index];
                        sprintf_s(line, "DRED op[%u]=%s (%u)%s", index, BreadcrumbOperationName(operation),
                            static_cast<unsigned int>(operation), index == completed ? " <- first incomplete" : "");
                        LOG_ERROR(line);
                    }
                    if (summary.empty() && completed < node->BreadcrumbCount)
                    {
                        summary = "DRED first incomplete: " + std::string(BreadcrumbOperationName(node->pCommandHistory[completed])) +
                            " (" + std::to_string(completed) + "/" + std::to_string(node->BreadcrumbCount) + ")";
                    }
                }
            }
            else LOG_ERROR_HR("DRED breadcrumb retrieval failed", hr);

            D3D12_DRED_PAGE_FAULT_OUTPUT pageFault = {};
            hr = dred->GetPageFaultAllocationOutput(&pageFault);
            if (SUCCEEDED(hr))
            {
                char line[256];
                sprintf_s(line, "DRED page fault address=0x%llX", pageFault.PageFaultVA);
                LOG_ERROR(line);
                auto LogAllocations = [](const D3D12_DRED_ALLOCATION_NODE* node, const char* category) {
                    unsigned int count = 0;
                    for (; node && count++ < 32; node = node->pNext)
                    {
                        const std::string name = DredObjectName(node->ObjectNameA, node->ObjectNameW);
                        const std::string description = std::string("DRED ") + category + " allocation: " + name +
                            " type=" + std::to_string(static_cast<unsigned int>(node->AllocationType));
                        LOG_ERROR(description.c_str());
                    }
                };
                LogAllocations(pageFault.pHeadExistingAllocationNode, "existing");
                LogAllocations(pageFault.pHeadRecentFreedAllocationNode, "recently freed");
            }
            else LOG_ERROR_HR("DRED page fault retrieval failed", hr);
            return summary;
        }

        std::string CollectValidationErrors(ID3D12Device* device)
        {
            ComPtr<ID3D12InfoQueue> infoQueue;
            if (!device || FAILED(device->QueryInterface(IID_PPV_ARGS(&infoQueue))))
                return {};

            std::string firstError;
            const UINT64 count = infoQueue->GetNumStoredMessagesAllowedByRetrievalFilter();
            for (UINT64 index = 0; index < count; ++index)
            {
                SIZE_T bytes = 0;
                if (FAILED(infoQueue->GetMessage(index, nullptr, &bytes)) || bytes == 0)
                    continue;
                std::vector<unsigned char> storage(bytes);
                auto* message = reinterpret_cast<D3D12_MESSAGE*>(storage.data());
                if (FAILED(infoQueue->GetMessage(index, message, &bytes)))
                    continue;
                if (message->Severity == D3D12_MESSAGE_SEVERITY_ERROR ||
                    message->Severity == D3D12_MESSAGE_SEVERITY_CORRUPTION)
                {
                    LOG_ERROR(message->pDescription);
                    if (firstError.empty()) firstError = message->pDescription;
                }
            }
            infoQueue->ClearStoredMessages();
            return firstError;
        }

        std::runtime_error GraphicsFailure(ID3D12Device* device, const char* operation, HRESULT hr)
        {
            const std::string validationError = CollectValidationErrors(device);
            const std::string dredFailure = CollectDredFailure(device);
            char details[256];
            sprintf_s(details, "%s (HRESULT 0x%08X, device reason 0x%08X)", operation,
                static_cast<unsigned int>(hr), static_cast<unsigned int>(device ? device->GetDeviceRemovedReason() : E_POINTER));
            std::string message = details;
            if (!validationError.empty()) message += ": " + validationError;
            if (!dredFailure.empty()) message += "; " + dredFailure;
            LOG_ERROR(message.c_str());
            return std::runtime_error(message);
        }
    }

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

#endif
            // DRED is independent of the debug/validation layer and is needed
            // for diagnosing device removal in ordinary Release rendering too.
            ComPtr<ID3D12DeviceRemovedExtendedDataSettings> dredSettings;
            if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&dredSettings))))
            {
                dredSettings->SetAutoBreadcrumbsEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
                dredSettings->SetPageFaultEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
                dredSettings->SetWatsonDumpEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
                OutputDebugStringA("DRED enabled\n");
            }

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
                // A breakpoint without a debugger becomes an unhandled SEH
                // exception in the managed host. Errors are still collected,
                // logged and propagated at command completion in either mode.
                const BOOL breakOnError = IsDebuggerPresent();
                infoQueue->SetBreakOnSeverity(D3D12_MESSAGE_SEVERITY_CORRUPTION, breakOnError);
                infoQueue->SetBreakOnSeverity(D3D12_MESSAGE_SEVERITY_ERROR, breakOnError);
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

    void DXContext::Shutdown() noexcept
    {
        // Destruction must be safe after any failed initialization step and
        // after device removal. Repeated shutdown must not signal a dead event.
        if (commandQueue && fence && fenceEvent)
        {
            try
            {
                WaitForGPU();
            }
            catch (const std::exception& ex)
            {
                LOG_ERROR(ex.what());
            }
        }

        if (fenceEvent)
        {
            CloseHandle(fenceEvent);
            fenceEvent = nullptr;
        }
        fence.Reset();
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
        if (!commandQueue || !fence || !fenceEvent)
        {
            throw std::runtime_error("GPU synchronization is not initialized");
        }

        const UINT64 currentFenceValue = fenceValue;
        HRESULT hr = commandQueue->Signal(fence.Get(), currentFenceValue);
        if (FAILED(hr))
        {
            throw GraphicsFailure(device.Get(), "Failed to signal fence", hr);
        }

        fenceValue++;

        if (fence->GetCompletedValue() < currentFenceValue)
        {
            hr = fence->SetEventOnCompletion(currentFenceValue, fenceEvent);
            if (FAILED(hr))
            {
                throw GraphicsFailure(device.Get(), "Failed to set fence event", hr);
            }

            if (WaitForSingleObject(fenceEvent, INFINITE) != WAIT_OBJECT_0)
            {
                throw GraphicsFailure(device.Get(), "Failed to wait for GPU fence", HRESULT_FROM_WIN32(GetLastError()));
            }
        }

        if (fence->GetCompletedValue() == UINT64_MAX)
        {
            throw GraphicsFailure(device.Get(), "Device removed while waiting for GPU fence", device->GetDeviceRemovedReason());
        }

        const std::string validationError = CollectValidationErrors(device.Get());
        if (!validationError.empty())
            throw std::runtime_error("Direct3D validation failed: " + validationError);
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

    void DXContext::ExecuteCommandList()
    {
        if (!commandList || !commandQueue || !commandListOpen)
        {
            throw std::runtime_error("No open command list to execute");
        }
        const HRESULT hr = commandList->Close();
        if (FAILED(hr))
        {
            throw GraphicsFailure(device.Get(), "Failed to close command list", hr);
        }
        commandListOpen = false;
        ID3D12CommandList* lists[] = { commandList.Get() };
        commandQueue->ExecuteCommandLists(1, lists);
    }
}

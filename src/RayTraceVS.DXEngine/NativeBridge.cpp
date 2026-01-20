// This file is native code only - CLR compilation disabled
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <stdio.h>
#include <fstream>
#include "DXContext.h"
#include "DXRPipeline.h"
#include "RenderTarget.h"
#include "Scene/Scene.h"
#include "Scene/Objects/Sphere.h"
#include "Scene/Objects/Plane.h"
#include "Scene/Objects/Box.h"
#include "Scene/Camera.h"
#include "Scene/Light.h"
#include "NativeBridge.h"

using namespace DirectX;

static void LogToFile(const char* message)
{
    std::ofstream log("C:\\git\\RayTraceVS\\debug_log.txt", std::ios::app);
    if (log.is_open())
    {
        log << message << std::endl;
        log.close();
    }
}

namespace RayTraceVS::Interop::Bridge
{
    // Helper functions: conversion
    static XMFLOAT3 ToXMFLOAT3(const Vector3Native& v)
    {
        return XMFLOAT3(v.x, v.y, v.z);
    }

    static XMFLOAT4 ToXMFLOAT4(const ColorNative& c)
    {
        return XMFLOAT4(c.r, c.g, c.b, c.a);
    }

    static RayTraceVS::DXEngine::Material ToMaterial(const MaterialNative& m)
    {
        RayTraceVS::DXEngine::Material material;
        material.color = ToXMFLOAT4(m.color);
        material.metallic = m.metallic;
        material.roughness = m.roughness;
        material.transmission = m.transmission;
        material.ior = m.ior;
        material.specular = m.specular;
        material.emission = ToXMFLOAT3(m.emission);
        return material;
    }

    // DXContext functions
    RayTraceVS::DXEngine::DXContext* CreateDXContext()
    {
        return new RayTraceVS::DXEngine::DXContext();
    }

    bool InitializeDXContext(RayTraceVS::DXEngine::DXContext* context, void* hwnd, int width, int height)
    {
        return context->Initialize(static_cast<HWND>(hwnd), width, height);
    }

    void ShutdownDXContext(RayTraceVS::DXEngine::DXContext* context)
    {
        context->Shutdown();
    }

    void DestroyDXContext(RayTraceVS::DXEngine::DXContext* context)
    {
        delete context;
    }

    void ResetCommandList(RayTraceVS::DXEngine::DXContext* context)
    {
        context->ResetCommandList();
    }

    // DXRPipeline functions
    RayTraceVS::DXEngine::DXRPipeline* CreateDXRPipeline(RayTraceVS::DXEngine::DXContext* context)
    {
        return new RayTraceVS::DXEngine::DXRPipeline(context);
    }

    bool InitializeDXRPipeline(RayTraceVS::DXEngine::DXRPipeline* pipeline)
    {
        return pipeline->Initialize();
    }

    void DestroyDXRPipeline(RayTraceVS::DXEngine::DXRPipeline* pipeline)
    {
        delete pipeline;
    }

    void DispatchRays(RayTraceVS::DXEngine::DXRPipeline* pipeline, int width, int height)
    {
        pipeline->DispatchRays(width, height);
    }

    // Scene functions
    RayTraceVS::DXEngine::Scene* CreateScene()
    {
        return new RayTraceVS::DXEngine::Scene();
    }

    void DestroyScene(RayTraceVS::DXEngine::Scene* scene)
    {
        delete scene;
    }

    void ClearScene(RayTraceVS::DXEngine::Scene* scene)
    {
        scene->Clear();
    }

    void SetCamera(RayTraceVS::DXEngine::Scene* scene, const CameraDataNative& camera)
    {
        RayTraceVS::DXEngine::Camera nativeCamera(
            ToXMFLOAT3(camera.position),
            ToXMFLOAT3(camera.lookAt),
            ToXMFLOAT3(camera.up),
            camera.fov
        );
        nativeCamera.SetApertureSize(camera.apertureSize);
        nativeCamera.SetFocusDistance(camera.focusDistance);
        scene->SetCamera(nativeCamera);
    }

    void SetRenderSettings(RayTraceVS::DXEngine::Scene* scene, int samplesPerPixel, int maxBounces, float exposure, int toneMapOperator, float denoiserStabilization, float shadowStrength, bool enableDenoiser)
    {
        char buf[256];
        sprintf_s(buf, "NativeBridge::SetRenderSettings: shadowStrength=%.2f, enableDenoiser=%d", shadowStrength, enableDenoiser ? 1 : 0);
        LogToFile(buf);
        scene->SetRenderSettings(samplesPerPixel, maxBounces, exposure, toneMapOperator, denoiserStabilization, shadowStrength, enableDenoiser);
    }

    void AddSphere(RayTraceVS::DXEngine::Scene* scene, const SphereDataNative& sphere)
    {
        auto nativeSphere = std::make_shared<RayTraceVS::DXEngine::Sphere>(
            ToXMFLOAT3(sphere.center),
            sphere.radius
        );
        nativeSphere->SetMaterial(ToMaterial(sphere.material));
        scene->AddObject(nativeSphere);
    }

    void AddPlane(RayTraceVS::DXEngine::Scene* scene, const PlaneDataNative& plane)
    {
        auto nativePlane = std::make_shared<RayTraceVS::DXEngine::Plane>(
            ToXMFLOAT3(plane.position),
            ToXMFLOAT3(plane.normal)
        );
        nativePlane->SetMaterial(ToMaterial(plane.material));
        scene->AddObject(nativePlane);
    }

    void AddBox(RayTraceVS::DXEngine::Scene* scene, const BoxDataNative& box)
    {
        auto nativeBox = std::make_shared<RayTraceVS::DXEngine::Box>(
            ToXMFLOAT3(box.center),
            ToXMFLOAT3(box.size)
        );
        nativeBox->SetMaterial(ToMaterial(box.material));
        scene->AddObject(nativeBox);
    }

    void AddLight(RayTraceVS::DXEngine::Scene* scene, const LightDataNative& light)
    {
        RayTraceVS::DXEngine::Light nativeLight(
            ToXMFLOAT3(light.position),
            ToXMFLOAT4(light.color),
            light.intensity
        );
        nativeLight.SetType(static_cast<RayTraceVS::DXEngine::LightType>(light.type));
        nativeLight.SetRadius(light.radius);
        nativeLight.SetSoftShadowSamples(light.softShadowSamples);
        scene->AddLight(nativeLight);
    }

    // RenderTarget functions
    RayTraceVS::DXEngine::RenderTarget* CreateRenderTarget(RayTraceVS::DXEngine::DXContext* context)
    {
        return new RayTraceVS::DXEngine::RenderTarget(context);
    }

    void DestroyRenderTarget(RayTraceVS::DXEngine::RenderTarget* target)
    {
        delete target;
    }

    bool InitializeRenderTarget(RayTraceVS::DXEngine::RenderTarget* target, int width, int height)
    {
        return target->Create(width, height);
    }

    void RenderTestPattern(RayTraceVS::DXEngine::DXRPipeline* pipeline, RayTraceVS::DXEngine::RenderTarget* target, RayTraceVS::DXEngine::Scene* scene)
    {
        OutputDebugStringA("Bridge::Render called\n");
        if (!pipeline)
        {
            OutputDebugStringA("Bridge::Render: Null pipeline\n");
            return;
        }
        if (!target)
        {
            OutputDebugStringA("Bridge::Render: Null target\n");
            return;
        }
        if (!scene)
        {
            OutputDebugStringA("Bridge::Render: Null scene\n");
            return;
        }
        pipeline->Render(target, scene);
        OutputDebugStringA("Bridge::Render returned\n");
    }

    bool CopyRenderTargetToReadback(RayTraceVS::DXEngine::RenderTarget* target, RayTraceVS::DXEngine::DXContext* context)
    {
        return target->CopyToReadback(context->GetCommandList());
    }

    bool ReadRenderTargetPixels(RayTraceVS::DXEngine::RenderTarget* target, unsigned char* outData, int dataSize)
    {
        try
        {
            if (!target || !outData || dataSize <= 0)
            {
                return false;
            }
            
            std::vector<unsigned char> pixels;
            if (!target->ReadPixels(pixels))
            {
                // ReadPixels failed - fill with green
                for (int j = 0; j < dataSize; j += 4)
                {
                    outData[j + 0] = 0;
                    outData[j + 1] = 255;
                    outData[j + 2] = 0;
                    outData[j + 3] = 255;
                }
                return true;
            }

            if (pixels.size() == 0)
            {
                // Zero size - fill with red
                for (int j = 0; j < dataSize; j += 4)
                {
                    outData[j + 0] = 255;
                    outData[j + 1] = 0;
                    outData[j + 2] = 0;
                    outData[j + 3] = 255;
                }
                return true;
            }

            if (dataSize < static_cast<int>(pixels.size()))
            {
                // Buffer too small - fill with yellow
                for (int j = 0; j < dataSize; j += 4)
                {
                    outData[j + 0] = 255;
                    outData[j + 1] = 255;
                    outData[j + 2] = 0;
                    outData[j + 3] = 255;
                }
                return true;
            }

            // Check if all zeros
            bool allZero = true;
            size_t checkSize = pixels.size() < 1000 ? pixels.size() : 1000;
            for (size_t k = 0; k < checkSize; ++k)
            {
                if (pixels[k] != 0)
                {
                    allZero = false;
                    break;
                }
            }
            
            if (allZero)
            {
                // All zeros - fill with orange
                for (int j = 0; j < dataSize; j += 4)
                {
                    outData[j + 0] = 255;
                    outData[j + 1] = 128;
                    outData[j + 2] = 0;
                    outData[j + 3] = 255;
                }
                return true;
            }

            // Success: copy actual pixel data
            memcpy(outData, pixels.data(), pixels.size());
            return true;
        }
        catch (...)
        {
            // Exception - fill with magenta
            if (outData && dataSize > 0)
            {
                for (int j = 0; j < dataSize; j += 4)
                {
                    outData[j + 0] = 255;
                    outData[j + 1] = 0;
                    outData[j + 2] = 255;
                    outData[j + 3] = 255;
                }
            }
            return true;
        }
    }

    void ExecuteCommandList(RayTraceVS::DXEngine::DXContext* context)
    {
        try
        {
            if (!context)
            {
                OutputDebugStringA("ExecuteCommandList: Null context\n");
                return;
            }
            
            auto commandList = context->GetCommandList();
            auto commandQueue = context->GetCommandQueue();
            
            if (!commandList || !commandQueue)
            {
                OutputDebugStringA("ExecuteCommandList: Null command list or queue\n");
                return;
            }
            
            HRESULT hr = commandList->Close();
            if (FAILED(hr))
            {
                char buffer[256];
                sprintf_s(buffer, "ExecuteCommandList: Failed to close: 0x%08X\n", hr);
                OutputDebugStringA(buffer);
                return;
            }
            
            ID3D12CommandList* commandLists[] = { commandList };
            commandQueue->ExecuteCommandLists(1, commandLists);
            
            OutputDebugStringA("ExecuteCommandList: Success\n");
        }
        catch (...)
        {
            OutputDebugStringA("ExecuteCommandList: Exception\n");
        }
    }

    void WaitForGPU(RayTraceVS::DXEngine::DXContext* context)
    {
        try
        {
            if (!context)
            {
                OutputDebugStringA("WaitForGPU: Null context\n");
                return;
            }
            
            context->WaitForGPU();
            OutputDebugStringA("WaitForGPU: Success\n");
        }
        catch (...)
        {
            OutputDebugStringA("WaitForGPU: Exception\n");
        }
    }
}

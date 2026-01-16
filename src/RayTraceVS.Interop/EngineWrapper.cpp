// Include bridge headers
#include "EngineWrapper.h"
#include "Marshalling.h"
#include "NativeBridge.h"

namespace RayTraceVS::Interop
{
    EngineWrapper::EngineWrapper(System::IntPtr windowHandle, int width, int height)
        : isInitialized(false)
        , renderWidth(width)
        , renderHeight(height)
        , nativeRenderTarget(nullptr)
    {
        try
        {
            // Create native context
            nativeContext = Bridge::CreateDXContext();
            
            void* hwnd = windowHandle.ToPointer();
            if (!Bridge::InitializeDXContext(nativeContext, hwnd, width, height))
            {
                throw gcnew System::Exception("Failed to initialize DirectX context");
            }

            // Create DXR pipeline
            nativePipeline = Bridge::CreateDXRPipeline(nativeContext);
            if (!Bridge::InitializeDXRPipeline(nativePipeline))
            {
                throw gcnew System::Exception("Failed to initialize DXR pipeline");
            }

            // Create scene
            nativeScene = Bridge::CreateScene();
            
            // Create render target
            nativeRenderTarget = Bridge::CreateRenderTarget(nativeContext);
            if (!Bridge::InitializeRenderTarget(nativeRenderTarget, width, height))
            {
                throw gcnew System::Exception("Failed to initialize render target");
            }

            isInitialized = true;
        }
        catch (...)
        {
            throw gcnew System::Exception("Native initialization failed");
        }
    }

    EngineWrapper::~EngineWrapper()
    {
        this->!EngineWrapper();
    }

    EngineWrapper::!EngineWrapper()
    {
        if (nativeRenderTarget)
        {
            Bridge::DestroyRenderTarget(nativeRenderTarget);
            nativeRenderTarget = nullptr;
        }

        if (nativeScene)
        {
            Bridge::DestroyScene(nativeScene);
            nativeScene = nullptr;
        }

        if (nativePipeline)
        {
            Bridge::DestroyDXRPipeline(nativePipeline);
            nativePipeline = nullptr;
        }

        if (nativeContext)
        {
            Bridge::ShutdownDXContext(nativeContext);
            Bridge::DestroyDXContext(nativeContext);
            nativeContext = nullptr;
        }
    }

    void EngineWrapper::UpdateScene(
        array<SphereData>^ spheres,
        array<PlaneData>^ planes,
        array<CylinderData>^ cylinders,
        CameraData camera,
        array<LightData>^ lights)
    {
        if (!isInitialized || !nativeScene)
            return;

        // Clear scene
        Bridge::ClearScene(nativeScene);

        // Set camera
        auto nativeCamera = Marshalling::ToNativeCamera(camera);
        Bridge::SetCamera(nativeScene, nativeCamera);

        // Add spheres
        if (spheres != nullptr)
        {
            for each (SphereData sphere in spheres)
            {
                auto nativeSphere = Marshalling::ToNativeSphere(sphere);
                Bridge::AddSphere(nativeScene, nativeSphere);
            }
        }

        // Add planes
        if (planes != nullptr)
        {
            for each (PlaneData plane in planes)
            {
                auto nativePlane = Marshalling::ToNativePlane(plane);
                Bridge::AddPlane(nativeScene, nativePlane);
            }
        }

        // Add cylinders
        if (cylinders != nullptr)
        {
            for each (CylinderData cylinder in cylinders)
            {
                auto nativeCylinder = Marshalling::ToNativeCylinder(cylinder);
                Bridge::AddCylinder(nativeScene, nativeCylinder);
            }
        }

        // Add lights
        if (lights != nullptr)
        {
            for each (LightData light in lights)
            {
                auto nativeLight = Marshalling::ToNativeLight(light);
                Bridge::AddLight(nativeScene, nativeLight);
            }
        }
    }

    void EngineWrapper::Render()
    {
        if (!isInitialized || !nativePipeline || !nativeRenderTarget || !nativeContext)
            return;

        try
        {
            // Reset command list
            Bridge::ResetCommandList(nativeContext);
            
            // Render test pattern
            Bridge::RenderTestPattern(nativePipeline, nativeRenderTarget, nativeScene);
            
            // TODO: Actual ray tracing rendering
            // Bridge::DispatchRays(nativePipeline, renderWidth, renderHeight);
            
            // Execute command list
            Bridge::ExecuteCommandList(nativeContext);
            
            // Wait for GPU completion
            Bridge::WaitForGPU(nativeContext);
            
            // Copy to readback buffer
            Bridge::ResetCommandList(nativeContext);
            Bridge::CopyRenderTargetToReadback(nativeRenderTarget, nativeContext);
            Bridge::ExecuteCommandList(nativeContext);
            Bridge::WaitForGPU(nativeContext);
        }
        catch (System::Exception^)
        {
            throw;
        }
        catch (...)
        {
            throw gcnew System::Exception("Native rendering error");
        }
    }

    System::IntPtr EngineWrapper::GetRenderTargetTexture()
    {
        if (!isInitialized)
            return System::IntPtr::Zero;

        // Return render target pointer
        // TODO: Implementation
        return System::IntPtr::Zero;
    }
    
    array<System::Byte>^ EngineWrapper::GetPixelData()
    {
        if (!isInitialized)
            return nullptr;
            
        if (!nativeRenderTarget)
            return nullptr;
            
        // Calculate pixel data size
        int dataSize = renderWidth * renderHeight * 4; // RGBA
        
        // Create managed array
        array<System::Byte>^ pixelData = gcnew array<System::Byte>(dataSize);
        
        // Pin and get native pointer
        pin_ptr<System::Byte> pinnedData = &pixelData[0];
        
        // Read pixel data in native code
        bool result = false;
        try
        {
            result = Bridge::ReadRenderTargetPixels(nativeRenderTarget, pinnedData, dataSize);
        }
        catch (System::Exception^)
        {
            return nullptr;
        }
        catch (...)
        {
            return nullptr;
        }
        
        if (!result)
            return nullptr;
        
        return pixelData;
    }
}

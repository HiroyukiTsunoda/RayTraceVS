#pragma once

#include "SceneData.h"
#include "NativeBridge.h"
#include <string>

namespace RayTraceVS::Interop
{
    public ref class Marshalling
    {
    public:
        // Convert from managed to native bridge structures
        static Bridge::CameraDataNative ToNativeCamera(CameraData managedCamera);
        static Bridge::LightDataNative ToNativeLight(LightData managedLight);
        static Bridge::SphereDataNative ToNativeSphere(SphereData managedSphere);
        static Bridge::PlaneDataNative ToNativePlane(PlaneData managedPlane);
        static Bridge::BoxDataNative ToNativeBox(BoxData managedBox);
        
        // Helper to convert managed string to native string
        static std::string ToNativeString(System::String^ managedString);
    };
}

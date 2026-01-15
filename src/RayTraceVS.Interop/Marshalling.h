#pragma once

#include "SceneData.h"
#include "NativeBridge.h"

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
        static Bridge::CylinderDataNative ToNativeCylinder(CylinderData managedCylinder);
    };
}

#include "Marshalling.h"

namespace RayTraceVS::Interop
{
    Bridge::CameraDataNative Marshalling::ToNativeCamera(CameraData managedCamera)
    {
        Bridge::CameraDataNative native;
        native.position = { managedCamera.Position.X, managedCamera.Position.Y, managedCamera.Position.Z };
        native.lookAt = { managedCamera.LookAt.X, managedCamera.LookAt.Y, managedCamera.LookAt.Z };
        native.up = { managedCamera.Up.X, managedCamera.Up.Y, managedCamera.Up.Z };
        native.fov = managedCamera.FieldOfView;
        native.aspectRatio = managedCamera.AspectRatio;
        native.apertureSize = managedCamera.ApertureSize;
        native.focusDistance = managedCamera.FocusDistance;
        return native;
    }

    Bridge::LightDataNative Marshalling::ToNativeLight(LightData managedLight)
    {
        Bridge::LightDataNative native;
        native.position = { managedLight.Position.X, managedLight.Position.Y, managedLight.Position.Z };
        native.color = { managedLight.Color.X, managedLight.Color.Y, managedLight.Color.Z, managedLight.Color.W };
        native.intensity = managedLight.Intensity;
        native.type = static_cast<int>(managedLight.Type);
        return native;
    }

    Bridge::SphereDataNative Marshalling::ToNativeSphere(SphereData managedSphere)
    {
        Bridge::SphereDataNative native;
        native.center = { managedSphere.Position.X, managedSphere.Position.Y, managedSphere.Position.Z };
        native.radius = managedSphere.Radius;
        native.material.color = { managedSphere.Color.X, managedSphere.Color.Y, managedSphere.Color.Z, managedSphere.Color.W };
        native.material.metallic = managedSphere.Metallic;
        native.material.roughness = managedSphere.Roughness;
        native.material.transmission = managedSphere.Transmission;
        native.material.ior = managedSphere.IOR;
        return native;
    }

    Bridge::PlaneDataNative Marshalling::ToNativePlane(PlaneData managedPlane)
    {
        Bridge::PlaneDataNative native;
        native.position = { managedPlane.Position.X, managedPlane.Position.Y, managedPlane.Position.Z };
        native.normal = { managedPlane.Normal.X, managedPlane.Normal.Y, managedPlane.Normal.Z };
        native.material.color = { managedPlane.Color.X, managedPlane.Color.Y, managedPlane.Color.Z, managedPlane.Color.W };
        native.material.metallic = managedPlane.Metallic;
        native.material.roughness = managedPlane.Roughness;
        native.material.transmission = managedPlane.Transmission;
        native.material.ior = managedPlane.IOR;
        return native;
    }

    Bridge::BoxDataNative Marshalling::ToNativeBox(BoxData managedBox)
    {
        Bridge::BoxDataNative native;
        native.center = { managedBox.Center.X, managedBox.Center.Y, managedBox.Center.Z };
        native.size = { managedBox.Size.X, managedBox.Size.Y, managedBox.Size.Z };
        native.material.color = { managedBox.Color.X, managedBox.Color.Y, managedBox.Color.Z, managedBox.Color.W };
        native.material.metallic = managedBox.Metallic;
        native.material.roughness = managedBox.Roughness;
        native.material.transmission = managedBox.Transmission;
        native.material.ior = managedBox.IOR;
        return native;
    }
}

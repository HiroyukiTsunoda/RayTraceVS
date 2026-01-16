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

    Bridge::CylinderDataNative Marshalling::ToNativeCylinder(CylinderData managedCylinder)
    {
        Bridge::CylinderDataNative native;
        native.center = { managedCylinder.Position.X, managedCylinder.Position.Y, managedCylinder.Position.Z };
        native.axis = { managedCylinder.Axis.X, managedCylinder.Axis.Y, managedCylinder.Axis.Z };
        native.radius = managedCylinder.Radius;
        native.height = managedCylinder.Height;
        native.material.color = { managedCylinder.Color.X, managedCylinder.Color.Y, managedCylinder.Color.Z, managedCylinder.Color.W };
        native.material.metallic = managedCylinder.Metallic;
        native.material.roughness = managedCylinder.Roughness;
        native.material.transmission = managedCylinder.Transmission;
        native.material.ior = managedCylinder.IOR;
        return native;
    }
}

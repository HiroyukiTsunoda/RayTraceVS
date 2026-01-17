#pragma once

using namespace System;
using namespace System::Runtime::InteropServices;

namespace RayTraceVS::Interop
{
    // Vector3 structure
    [StructLayout(LayoutKind::Sequential)]
    public value struct Vector3
    {
        float X;
        float Y;
        float Z;

        Vector3(float x, float y, float z) : X(x), Y(y), Z(z) {}
    };

    // Vector4 structure
    [StructLayout(LayoutKind::Sequential)]
    public value struct Vector4
    {
        float X;
        float Y;
        float Z;
        float W;

        Vector4(float x, float y, float z, float w) : X(x), Y(y), Z(z), W(w) {}
    };

    // Sphere data (with PBR material)
    [StructLayout(LayoutKind::Sequential)]
    public value struct SphereData
    {
        Vector3 Position;
        float Radius;
        Vector4 Color;
        float Metallic;     // 0.0 = dielectric, 1.0 = metal
        float Roughness;    // 0.0 = smooth, 1.0 = rough
        float Transmission; // 0.0 = opaque, 1.0 = transparent (glass)
        float IOR;          // Index of Refraction (1.5 for glass)
    };

    // Plane data (with PBR material)
    [StructLayout(LayoutKind::Sequential)]
    public value struct PlaneData
    {
        Vector3 Position;
        Vector3 Normal;
        Vector4 Color;
        float Metallic;
        float Roughness;
        float Transmission;
        float IOR;
    };

    // Box data (with PBR material)
    [StructLayout(LayoutKind::Sequential)]
    public value struct BoxData
    {
        Vector3 Center;
        Vector3 Size;      // half-extents (width/2, height/2, depth/2)
        Vector4 Color;
        float Metallic;
        float Roughness;
        float Transmission;
        float IOR;
    };

    // Camera data
    [StructLayout(LayoutKind::Sequential)]
    public value struct CameraData
    {
        Vector3 Position;
        Vector3 LookAt;
        Vector3 Up;
        float FieldOfView;
        float AspectRatio;
        float Near;
        float Far;
        float ApertureSize;   // DoF: 0.0 = disabled, larger = stronger bokeh
        float FocusDistance;  // DoF: distance to the focal plane
    };

    // Light type enumeration
    public enum class LightType
    {
        Ambient = 0,
        Point = 1,
        Directional = 2
    };

    // Light data
    [StructLayout(LayoutKind::Sequential)]
    public value struct LightData
    {
        Vector3 Position;
        Vector4 Color;
        float Intensity;
        LightType Type;
    };
}

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

    // Sphere data (with PBR material) - 80 bytes, 16-byte aligned
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
        float Specular;     // Specular intensity (0.0 = none, 1.0 = full)
        float Padding1;
        float Padding2;
        float Padding3;
        Vector3 Emission;   // Emissive color (self-illumination)
        float Padding4;
    };

    // Plane data (with PBR material)
    [StructLayout(LayoutKind::Sequential)]
    public value struct PlaneData
    {
        Vector3 Position;
        float Metallic;
        Vector3 Normal;
        float Roughness;
        Vector4 Color;
        float Transmission;
        float IOR;
        float Specular;     // Specular intensity (0.0 = none, 1.0 = full)
        float Padding1;     // Padding for 16-byte alignment
        Vector3 Emission;   // Emissive color (self-illumination)
        float Padding2;
    };

    // Box data (with PBR material) - 96 bytes, 16-byte aligned
    [StructLayout(LayoutKind::Sequential)]
    public value struct BoxData
    {
        Vector3 Center;
        float Padding1;
        Vector3 Size;       // half-extents (width/2, height/2, depth/2)
        float Padding2;
        Vector4 Color;
        float Metallic;
        float Roughness;
        float Transmission;
        float IOR;
        float Specular;     // Specular intensity (0.0 = none, 1.0 = full)
        float Padding3;
        float Padding4;
        float Padding5;
        Vector3 Emission;   // Emissive color (self-illumination)
        float Padding6;
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

    // Render settings (managed side to native)
    [StructLayout(LayoutKind::Sequential)]
    public value struct RenderSettings
    {
        int SamplesPerPixel;
        int MaxBounces;
        float Exposure;
        int ToneMapOperator;
        float DenoiserStabilization;
        float ShadowStrength;
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
        float Radius;            // Area light radius (0 = point light, hard shadows)
        float SoftShadowSamples; // Number of shadow samples (1-16)
    };
}

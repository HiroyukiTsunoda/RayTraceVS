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

    // Sphere data
    [StructLayout(LayoutKind::Sequential)]
    public value struct SphereData
    {
        Vector3 Position;
        float Radius;
        Vector4 Color;
        float Reflectivity;
        float Transparency;
        float IOR;
    };

    // Plane data
    [StructLayout(LayoutKind::Sequential)]
    public value struct PlaneData
    {
        Vector3 Position;
        Vector3 Normal;
        Vector4 Color;
        float Reflectivity;
    };

    // Cylinder data
    [StructLayout(LayoutKind::Sequential)]
    public value struct CylinderData
    {
        Vector3 Position;
        Vector3 Axis;
        float Radius;
        float Height;
        Vector4 Color;
        float Reflectivity;
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

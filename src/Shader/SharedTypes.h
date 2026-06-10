// ============================================================================
// SharedTypes.h
// GPU structure definitions shared between C++ (engine) and HLSL (shaders):
//   - HLSL: included from Common.hlsli (DXR shader family)
//   - C++ : included from DXRPipeline.h (engine keeps GPU* aliases below)
// A new geometry/material/light field has to be added only once here.
// Note: RayTraceCompute.hlsl (non-DXR compute fallback) keeps its own copies.
// ============================================================================

#ifndef SHARED_TYPES_H
#define SHARED_TYPES_H

#ifdef __cplusplus
// ---- C++ view -------------------------------------------------------------
// Structs live in a nested SharedGpu namespace so that plain HLSL names
// (MeshMaterial etc.) cannot collide with engine-internal types (Scene.h has
// its own MeshMaterial). The engine uses the GPU* aliases exported below.
#include <DirectXMath.h>

#define SHARED_FLOAT2   DirectX::XMFLOAT2
#define SHARED_FLOAT3   DirectX::XMFLOAT3
#define SHARED_FLOAT4   DirectX::XMFLOAT4
#define SHARED_FLOAT4X4 DirectX::XMFLOAT4X4
#define SHARED_UINT     unsigned int
#define SHARED_UINT3    DirectX::XMUINT3
#define SHARED_ALIGN(x) alignas(x)

namespace RayTraceVS::DXEngine::SharedGpu
{
#else
// ---- HLSL view ------------------------------------------------------------
#define SHARED_FLOAT2   float2
#define SHARED_FLOAT3   float3
#define SHARED_FLOAT4   float4
#define SHARED_FLOAT4X4 float4x4
#define SHARED_UINT     uint
#define SHARED_UINT3    uint3
#define SHARED_ALIGN(x)
#endif

// Scene constant buffer (b0)
struct SHARED_ALIGN(256) SceneConstantBuffer
{
    SHARED_FLOAT3 CameraPosition;
    float CameraPadding1;
    SHARED_FLOAT3 CameraForward;
    float CameraPadding2;
    SHARED_FLOAT3 CameraRight;
    float CameraPadding3;
    SHARED_FLOAT3 CameraUp;
    float CameraPadding4;
    SHARED_FLOAT3 LightPosition;
    float LightIntensity;
    SHARED_FLOAT4 LightColor;
    SHARED_UINT NumSpheres;
    SHARED_UINT NumPlanes;
    SHARED_UINT NumBoxes;
    SHARED_UINT NumLights;
    SHARED_UINT ScreenWidth;
    SHARED_UINT ScreenHeight;
    float AspectRatio;
    float TanHalfFov;
    SHARED_UINT SamplesPerPixel;
    SHARED_UINT MaxBounces;
    // Photon mapping parameters
    SHARED_UINT NumPhotons;     // Number of photons to emit
    SHARED_UINT PhotonMapSize;  // Current photon map size
    float PhotonRadius;         // Search radius for gathering
    float CausticIntensity;     // Intensity multiplier
    SHARED_UINT PhotonDebugMode; // 0 = off, 1+ = debug visualization
    float PhotonDebugScale;     // Debug intensity scale
    SHARED_FLOAT2 PhotonDebugPadding;
    // DoF (Depth of Field) parameters
    float ApertureSize;         // 0.0 = DoF disabled, larger = stronger bokeh
    float FocusDistance;        // Distance to the focal plane
    // Shadow parameters
    float ShadowStrength;       // 0.0 = no shadow, 1.0 = normal, >1.0 = darker
    float ShadowAbsorptionScale; // Beer absorption scale for colored transparent shadows
    SHARED_UINT FrameIndex;     // Frame counter for temporal noise variation
    SHARED_UINT ShadowPadding;  // Padding for 16-byte alignment
    // Light attenuation parameters (physical-based)
    float LightAttenuationConstant;   // Constant term (usually 1.0)
    float LightAttenuationLinear;     // Linear term (distance proportional)
    float LightAttenuationQuadratic;  // Quadratic term (physical: 1.0, artistic: 0.01)
    SHARED_UINT MaxShadowLights;      // Maximum lights for shadow calculation (optimization)
    // Mesh instance count
    SHARED_UINT NumMeshInstances;     // Number of FBX mesh instances
    SHARED_UINT3 MeshPadding;         // Padding for 16-byte alignment
    // Matrices for motion vectors (column-major for HLSL)
    SHARED_FLOAT4X4 ViewProjection;
    SHARED_FLOAT4X4 PrevViewProjection;
};

// Sphere data (with PBR material) - 96 bytes, 16-byte aligned
struct SHARED_ALIGN(16) SphereData
{
    SHARED_FLOAT3 center;   // 12
    float radius;           // 4  -> 16
    SHARED_FLOAT4 color;    // 16 -> 32
    float metallic;         // 4
    float roughness;        // 4
    float transmission;     // 4
    float ior;              // 4  -> 48
    float specular;         // 4
    float padding1;         // 4
    float padding2;         // 4
    float padding3;         // 4  -> 64
    SHARED_FLOAT3 emission; // 12
    float padding4;         // 4  -> 80
    SHARED_FLOAT3 absorption; // 12 (sigmaA)
    float padding5;         // 4  -> 96
};

// Plane data (with PBR material) - 96 bytes, 16-byte aligned
struct SHARED_ALIGN(16) PlaneData
{
    SHARED_FLOAT3 position; // 12
    float metallic;         // 4  -> 16
    SHARED_FLOAT3 normal;   // 12
    float roughness;        // 4  -> 32
    SHARED_FLOAT4 color;    // 16 -> 48
    float transmission;     // 4
    float ior;              // 4
    float specular;         // 4
    float padding1;         // 4  -> 64
    SHARED_FLOAT3 emission; // 12
    float padding2;         // 4  -> 80
    SHARED_FLOAT3 absorption; // 12 (sigmaA)
    float padding3;         // 4  -> 96
};

// Box data (with PBR material and rotation) - 160 bytes, 16-byte aligned
// OBB (Oriented Bounding Box) support via local axes
struct SHARED_ALIGN(16) BoxData
{
    SHARED_FLOAT3 center;   // 12
    float padding1;         // 4  -> 16
    SHARED_FLOAT3 size;     // 12 (half-extents)
    float padding2;         // 4  -> 32
    // Local axes (rotation matrix columns) - for OBB
    SHARED_FLOAT3 axisX;    // 12 (local X axis in world space)
    float padding3;         // 4  -> 48
    SHARED_FLOAT3 axisY;    // 12 (local Y axis in world space)
    float padding4;         // 4  -> 64
    SHARED_FLOAT3 axisZ;    // 12 (local Z axis in world space)
    float padding5;         // 4  -> 80
    SHARED_FLOAT4 color;    // 16 -> 96
    float metallic;         // 4
    float roughness;        // 4
    float transmission;     // 4
    float ior;              // 4  -> 112
    float specular;         // 4
    float padding6;         // 4
    float padding7;         // 4
    float padding8;         // 4  -> 128
    SHARED_FLOAT3 emission; // 12
    float padding9;         // 4  -> 144
    SHARED_FLOAT3 absorption; // 12 (sigmaA)
    float padding10;        // 4  -> 160
};

// Light data - 48 bytes, 16-byte aligned
struct SHARED_ALIGN(16) LightData
{
    SHARED_FLOAT3 position; // Position (Point) or Direction (Directional)
    float intensity;
    SHARED_FLOAT4 color;
    SHARED_UINT type;       // 0=Ambient, 1=Point, 2=Directional (LIGHT_TYPE_*)
    float radius;           // Area light radius (0 = point light, hard shadows)
    float softShadowSamples; // Number of shadow samples (1-16)
    float padding;
};

// ============================================
// Mesh data (for FBX triangle meshes)
// ============================================

// Mesh vertex (Position + Normal interleaved) - 32 bytes
struct SHARED_ALIGN(16) MeshVertex
{
    SHARED_FLOAT3 position; // 12
    float padding1;         // 4  -> 16
    SHARED_FLOAT3 normal;   // 12
    float padding2;         // 4  -> 32
};

// Mesh info (vertex/index offsets per mesh type) - 16 bytes
struct SHARED_ALIGN(16) MeshInfo
{
    SHARED_UINT vertexOffset;  // Start index into the combined vertex buffer
    SHARED_UINT indexOffset;   // Start index into the combined index buffer
    SHARED_UINT vertexCount;   // Vertex count for this mesh type
    SHARED_UINT indexCount;    // Index count for this mesh type
};

// Mesh material (per instance) - 80 bytes
struct SHARED_ALIGN(16) MeshMaterial
{
    SHARED_FLOAT4 color;    // 16 -> 16
    float metallic;         // 4
    float roughness;        // 4
    float transmission;     // 4
    float ior;              // 4  -> 32
    float specular;         // 4
    SHARED_FLOAT3 emission; // 12 -> 48
    float padding1;         // 4
    float padding2;         // 4
    float padding3;         // 4
    float padding4;         // 4  -> 64
    SHARED_FLOAT3 absorption; // 12 (sigmaA)
    float padding5;         // 4  -> 80
};

// Mesh instance info (maps TLAS instance to mesh/material) - 8 bytes
struct MeshInstanceInfo
{
    SHARED_UINT meshTypeIndex;  // Index into MeshInfos (which mesh type)
    SHARED_UINT materialIndex;  // Index into MeshMaterials
};

#ifdef __cplusplus
    // Layout guards: StructuredBuffer strides in the shaders and the upload
    // code assume exactly these sizes. A mismatch silently corrupts rendering.
    static_assert(sizeof(SceneConstantBuffer) == 512, "SceneConstantBuffer size mismatch with HLSL cbuffer layout");
    static_assert(sizeof(SphereData) == 96, "SphereData size mismatch with HLSL");
    static_assert(sizeof(PlaneData) == 96, "PlaneData size mismatch with HLSL");
    static_assert(sizeof(BoxData) == 160, "BoxData size mismatch with HLSL");
    static_assert(sizeof(LightData) == 48, "LightData size mismatch with HLSL");
    static_assert(sizeof(MeshVertex) == 32, "MeshVertex size mismatch with HLSL");
    static_assert(sizeof(MeshInfo) == 16, "MeshInfo size mismatch with HLSL");
    static_assert(sizeof(MeshMaterial) == 80, "MeshMaterial size mismatch with HLSL");
    static_assert(sizeof(MeshInstanceInfo) == 8, "MeshInstanceInfo size mismatch with HLSL");
} // namespace RayTraceVS::DXEngine::SharedGpu

namespace RayTraceVS::DXEngine
{
    // Engine-side aliases: the C++ code historically used the GPU* names.
    using SceneConstants = SharedGpu::SceneConstantBuffer;
    using GPUSphere = SharedGpu::SphereData;
    using GPUPlane = SharedGpu::PlaneData;
    using GPUBox = SharedGpu::BoxData;
    using GPULight = SharedGpu::LightData;
    using GPUMeshVertex = SharedGpu::MeshVertex;
    using GPUMeshInfo = SharedGpu::MeshInfo;
    using GPUMeshMaterial = SharedGpu::MeshMaterial;
    using GPUMeshInstanceInfo = SharedGpu::MeshInstanceInfo;
} // namespace RayTraceVS::DXEngine
#endif

#endif // SHARED_TYPES_H

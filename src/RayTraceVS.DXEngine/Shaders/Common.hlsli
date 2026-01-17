// 共通定義

#define MAX_RECURSION_DEPTH 5

// Object type constants
#define OBJECT_TYPE_SPHERE 0
#define OBJECT_TYPE_PLANE 1
#define OBJECT_TYPE_CYLINDER 2

// Light type constants
#define LIGHT_TYPE_AMBIENT 0
#define LIGHT_TYPE_POINT 1
#define LIGHT_TYPE_DIRECTIONAL 2

// レイペイロード
struct RayPayload
{
    float3 color;
    uint depth;
    bool hit;
};

// シャドウレイ用ペイロード
struct ShadowPayload
{
    bool inShadow;
};

// Procedural geometry attributes (normal from intersection shader)
struct ProceduralAttributes
{
    float3 normal;
    uint objectType;    // OBJECT_TYPE_*
    uint objectIndex;   // Index into the type-specific buffer
};

// 球の属性 (legacy, kept for compatibility)
struct SphereAttributes
{
    float3 normal;
};

// シーン定数バッファ (must match C++ SceneConstants)
struct SceneConstantBuffer
{
    float3 CameraPosition;
    float CameraPadding1;
    float3 CameraForward;
    float CameraPadding2;
    float3 CameraRight;
    float CameraPadding3;
    float3 CameraUp;
    float CameraPadding4;
    float3 LightPosition;
    float LightIntensity;
    float4 LightColor;
    uint NumSpheres;
    uint NumPlanes;
    uint NumCylinders;
    uint NumLights;
    uint ScreenWidth;
    uint ScreenHeight;
    float AspectRatio;
    float TanHalfFov;
    uint SamplesPerPixel;
    uint MaxBounces;
    float Padding1;
    float Padding2;
};

// 球データ (with PBR material, must match C++ GPUSphere)
struct SphereData
{
    float3 center;
    float radius;
    float4 color;
    float metallic;
    float roughness;
    float transmission;
    float ior;
};

// 平面データ (with PBR material, must match C++ GPUPlane)
struct PlaneData
{
    float3 position;
    float metallic;
    float3 normal;
    float roughness;
    float4 color;
    float transmission;
    float ior;
    float padding1;
    float padding2;
};

// 円柱データ (with PBR material, must match C++ GPUCylinder)
struct CylinderData
{
    float3 position;
    float radius;
    float3 axis;
    float height;
    float4 color;
    float metallic;
    float roughness;
    float transmission;
    float ior;
};

// ライトデータ (must match C++ GPULight)
struct LightData
{
    float3 position;    // Position (Point) or Direction (Directional)
    float intensity;
    float4 color;
    uint type;          // LIGHT_TYPE_*
    float3 padding;
};

// ============================================
// SoA (Structure of Arrays) - Optimized structures
// ============================================

// Geometry-only data for intersection tests (minimal size)
struct SphereGeometry
{
    float3 center;
    float radius;
};

struct PlaneGeometry
{
    float3 position;
    float padding;
    float3 normal;
    float padding2;
};

struct CylinderGeometry
{
    float3 position;
    float radius;
    float3 axis;
    float height;
};

// Material data (only read after intersection is confirmed)
struct ObjectMaterial
{
    float4 color;
    float metallic;
    float roughness;
    float transmission;
    float ior;
};

// レイトレーシングリソース
RaytracingAccelerationStructure SceneBVH : register(t0);
RWTexture2D<float4> RenderTarget : register(u0);
ConstantBuffer<SceneConstantBuffer> Scene : register(b0);

// Object data buffers
StructuredBuffer<SphereData> Spheres : register(t1);
StructuredBuffer<PlaneData> Planes : register(t2);
StructuredBuffer<CylinderData> Cylinders : register(t3);
StructuredBuffer<LightData> Lights : register(t4);

// ユーティリティ関数
float3 CreateCameraRay(float2 ndc, float3 cameraPos, float4x4 invViewProj)
{
    float4 worldPos = mul(invViewProj, float4(ndc, 0.0, 1.0));
    worldPos /= worldPos.w;
    return normalize(worldPos.xyz - cameraPos);
}

// ランベルト拡散反射
float3 CalculateDiffuse(float3 normal, float3 lightDir, float3 lightColor, float3 objectColor)
{
    float ndotl = max(0.0, dot(normal, lightDir));
    return objectColor * lightColor * ndotl;
}

// スペキュラー反射
float3 CalculateSpecular(float3 normal, float3 lightDir, float3 viewDir, float3 lightColor, float shininess)
{
    float3 reflectDir = reflect(-lightDir, normal);
    float spec = pow(max(0.0, dot(viewDir, reflectDir)), shininess);
    return lightColor * spec;
}

// Fresnel-Schlick approximation
float FresnelSchlick(float cosTheta, float f0)
{
    return f0 + (1.0 - f0) * pow(1.0 - cosTheta, 5.0);
}

// Get sky color for background
float3 GetSkyColor(float3 direction)
{
    float t = 0.5 * (direction.y + 1.0);
    return lerp(float3(1.0, 1.0, 1.0), float3(0.5, 0.7, 1.0), t);
}

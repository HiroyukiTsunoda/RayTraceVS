// 共通定義

#define MAX_RECURSION_DEPTH 5

// Include NRD encoding helpers
#include "NRDEncoding.hlsli"

// Object type constants
#define OBJECT_TYPE_SPHERE 0
#define OBJECT_TYPE_PLANE 1
#define OBJECT_TYPE_BOX 2

// Light type constants
#define LIGHT_TYPE_AMBIENT 0
#define LIGHT_TYPE_POINT 1
#define LIGHT_TYPE_DIRECTIONAL 2

// ============================================
// Photon Mapping for Caustics
// ============================================
#define MAX_PHOTONS 262144          // 256K photons
#define PHOTON_SEARCH_RADIUS 0.5    // Search radius for photon gathering
#define MAX_PHOTON_BOUNCES 8        // Max bounces for photon tracing
#define CAUSTIC_INTENSITY 2.0       // Intensity multiplier for caustics

// レイペイロード (with NRD fields for denoising)
struct RayPayload
{
    // Basic fields
    float3 color;           // Total radiance (12 bytes)
    uint depth;             // Recursion depth (4 bytes)
    uint hit;               // Did ray hit something? (4 bytes)
    float padding;          // Padding (4 bytes)
    
    // NRD denoiser fields
    float3 diffuseRadiance;   // Diffuse lighting component
    float hitDistance;        // Distance to hit point
    float3 specularRadiance;  // Specular/reflection component
    float roughness;          // Surface roughness
    float3 worldNormal;       // World-space normal at hit
    float viewZ;              // Linear view depth
    float3 worldPosition;     // World-space hit position
    float metallic;           // Metallic value
    float3 albedo;            // Surface albedo
    float padding2;           // Padding for alignment
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
    uint NumBoxes;
    uint NumLights;
    uint ScreenWidth;
    uint ScreenHeight;
    float AspectRatio;
    float TanHalfFov;
    uint SamplesPerPixel;
    uint MaxBounces;
    // Photon mapping parameters
    uint NumPhotons;            // Number of photons to emit
    uint PhotonMapSize;         // Current photon map size
    float PhotonRadius;         // Search radius for gathering
    float CausticIntensity;     // Intensity multiplier
    // DoF (Depth of Field) parameters
    float ApertureSize;         // 0.0 = DoF disabled, larger = stronger bokeh
    float FocusDistance;        // Distance to the focal plane
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

// ボックスデータ (with PBR material, must match C++ GPUBox)
struct BoxData
{
    float3 center;
    float padding1;
    float3 size;       // half-extents (width/2, height/2, depth/2)
    float padding2;
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
// Photon Structure for Caustics
// ============================================
struct Photon
{
    float3 position;    // Hit position on diffuse surface
    float power;        // Photon power/energy
    float3 direction;   // Incoming direction
    uint flags;         // Flags: 0=empty, 1=valid caustic photon
    float3 color;       // Photon color (from light and surface interactions)
    float padding;
};

// Photon ray payload
struct PhotonPayload
{
    float3 color;       // Accumulated color/power
    float power;        // Remaining power
    uint depth;         // Current bounce depth
    bool isCaustic;     // Has passed through specular surface
    bool terminated;    // Photon has been absorbed or stored
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

struct BoxGeometry
{
    float3 center;
    float padding1;
    float3 size;
    float padding2;
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
StructuredBuffer<BoxData> Boxes : register(t3);
StructuredBuffer<LightData> Lights : register(t4);

// Photon map buffer (for caustics)
RWStructuredBuffer<Photon> PhotonMap : register(u1);
RWStructuredBuffer<uint> PhotonCounter : register(u2);  // Atomic counter for photon index

// ============================================
// G-Buffer Outputs for NRD Denoiser
// ============================================
// These are optional - only used when denoising is enabled
#ifdef ENABLE_NRD_GBUFFER
RWTexture2D<float4> GBuffer_DiffuseRadianceHitDist : register(u3);   // RGBA16F: Diffuse radiance + hit dist
RWTexture2D<float4> GBuffer_SpecularRadianceHitDist : register(u4); // RGBA16F: Specular radiance + hit dist
RWTexture2D<float4> GBuffer_NormalRoughness : register(u5);          // RGBA8: Normal (oct) + roughness
RWTexture2D<float>  GBuffer_ViewZ : register(u6);                    // R32F: Linear view depth
RWTexture2D<float2> GBuffer_MotionVectors : register(u7);            // RG16F: Screen-space motion
#endif

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

// Get sky color for background (simple sky gradient)
float3 GetSkyColor(float3 direction)
{
    float3 dir = normalize(direction);
    
    // Simple sky gradient based on Y direction
    float t = 0.5 * (dir.y + 1.0);  // Map from [-1,1] to [0,1]
    
    // Lerp between horizon color and sky color
    float3 horizonColor = float3(0.8, 0.85, 0.9);  // Light gray/white horizon
    float3 skyColor = float3(0.4, 0.6, 0.9);       // Blue sky
    
    return lerp(horizonColor, skyColor, t);
}

// ============================================
// Photon Mapping Utility Functions
// ============================================

// Hash function for pseudo-random numbers
uint WangHash(uint seed)
{
    seed = (seed ^ 61) ^ (seed >> 16);
    seed *= 9;
    seed = seed ^ (seed >> 4);
    seed *= 0x27d4eb2d;
    seed = seed ^ (seed >> 15);
    return seed;
}

// Generate random float [0, 1) from seed
float RandomFloat(inout uint seed)
{
    seed = WangHash(seed);
    return float(seed) / 4294967296.0;
}

// Generate random direction on unit sphere
float3 RandomOnSphere(inout uint seed)
{
    float z = RandomFloat(seed) * 2.0 - 1.0;
    float phi = RandomFloat(seed) * 6.28318530718;
    float r = sqrt(max(0.0, 1.0 - z * z));
    return float3(r * cos(phi), r * sin(phi), z);
}

// Generate random direction in hemisphere around normal
float3 RandomInHemisphere(float3 normal, inout uint seed)
{
    float3 dir = RandomOnSphere(seed);
    return dot(dir, normal) > 0.0 ? dir : -dir;
}

// Cosine-weighted hemisphere sampling
float3 CosineSampleHemisphere(float3 normal, inout uint seed)
{
    float u1 = RandomFloat(seed);
    float u2 = RandomFloat(seed);
    
    float r = sqrt(u1);
    float theta = 6.28318530718 * u2;
    
    float x = r * cos(theta);
    float y = r * sin(theta);
    float z = sqrt(max(0.0, 1.0 - u1));
    
    // Create tangent space basis
    float3 up = abs(normal.y) < 0.999 ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 tangent = normalize(cross(up, normal));
    float3 bitangent = cross(normal, tangent);
    
    return normalize(tangent * x + bitangent * y + normal * z);
}

// Gather photons within radius (brute force for now)
float3 GatherPhotons(float3 position, float3 normal, float radius)
{
    float3 causticColor = float3(0, 0, 0);
    float totalWeight = 0.0;
    float radiusSq = radius * radius;
    
    uint photonCount = min(Scene.PhotonMapSize, MAX_PHOTONS);
    
    for (uint i = 0; i < photonCount; i++)
    {
        Photon p = PhotonMap[i];
        
        // Skip invalid photons
        if (p.flags == 0)
            continue;
        
        // Distance check
        float3 diff = position - p.position;
        float distSq = dot(diff, diff);
        
        if (distSq < radiusSq)
        {
            // Check if photon is on the same side of surface
            float dotN = dot(-p.direction, normal);
            if (dotN > 0.0)
            {
                // Gaussian kernel weight
                float weight = exp(-distSq / (2.0 * radiusSq * 0.5)) * dotN;
                causticColor += p.color * p.power * weight;
                totalWeight += weight;
            }
        }
    }
    
    // Normalize by area
    if (totalWeight > 0.0)
    {
        float area = 3.14159265 * radiusSq;
        causticColor /= area;
    }
    
    return causticColor * Scene.CausticIntensity;
}

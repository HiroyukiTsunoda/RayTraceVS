// 共通定義

#define MAX_RECURSION_DEPTH 4
#define SHADOW_RAY_DEPTH 100  // Marker for shadow rays (depth >= this value)

// Include NRD encoding helpers
#include "NRDEncoding.hlsli"

// Object type constants
#define OBJECT_TYPE_SPHERE 0
#define OBJECT_TYPE_PLANE 1
#define OBJECT_TYPE_BOX 2
#define OBJECT_TYPE_MESH 3

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

// ============================================
// Spatial Hash for Photon Gathering (O(1) lookup)
// ============================================
#define PHOTON_HASH_TABLE_SIZE 65536    // 2^16 hash buckets
#define MAX_PHOTONS_PER_CELL 64         // Max photons per hash cell
#define PHOTON_HASH_CELL_SIZE 1.0       // Default cell size (will be radius * 2)

// Spatial hash cell structure
struct PhotonHashCell
{
    uint count;                                     // Number of photons in this cell
    uint photonIndices[MAX_PHOTONS_PER_CELL];       // Indices into PhotonMap
};

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
    float hitDistance;        // Distance to hit point (primary ray)
    float3 specularRadiance;  // Specular/reflection component
    float specularHitDistance;// Distance for specular (reflection ray or primary)
    float roughness;          // Surface roughness
    float3 worldNormal;       // World-space normal at hit
    float viewZ;              // Linear view depth
    float3 worldPosition;     // World-space hit position
    float metallic;           // Metallic value
    float3 albedo;            // Surface albedo
    float shadowVisibility;   // Shadow visibility for SIGMA (0-1)
    float shadowPenumbra;     // Packed penumbra radius for SIGMA
    float shadowDistance;     // Distance to occluder (or NRD_FP16_MAX)
    float padding2;           // Padding for alignment
    
    // Thickness query for refractive objects
    uint targetObjectType;      // Object to skip (input)
    uint targetObjectIndex;
    uint thicknessQuery;        // 1 = thickness ray, 0 = normal
    uint hitObjectType;         // Object that was hit (output)
    
    // Colored shadow accumulation (for translucent objects)
    float3 shadowColorAccum;       // Accumulated shadow color tint
    float shadowTransmissionAccum; // Accumulated transmission (visibility)
    
    uint hitObjectIndex;        // Index of hit object (output)
    
    // Loop-based ray tracing
    float4 loopRayOrigin;       // xyz = nextRayOrigin, w = continueTrace
    float4 loopRayDirection;    // xyz = nextRayDirection, w = nextRayTMin
    float4 loopThroughput;      // xyz = throughput, w = unused
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
    // Shadow parameters
    float ShadowStrength;       // 0.0 = no shadow, 1.0 = normal, >1.0 = darker
    uint FrameIndex;            // Frame counter for temporal noise variation
    // Matrices for motion vectors
    float4x4 ViewProjection;
    float4x4 PrevViewProjection;
};

// 球データ (with PBR material, must match C++ GPUSphere) - 80 bytes
struct SphereData
{
    float3 center;      // 12
    float radius;       // 4  -> 16
    float4 color;       // 16 -> 32
    float metallic;     // 4
    float roughness;    // 4
    float transmission; // 4
    float ior;          // 4  -> 48
    float specular;     // 4
    float padding1;     // 4
    float padding2;     // 4
    float padding3;     // 4  -> 64
    float3 emission;    // 12
    float padding4;     // 4  -> 80
};

// 平面データ (with PBR material, must match C++ GPUPlane) - 80 bytes
struct PlaneData
{
    float3 position;    // 12
    float metallic;     // 4  -> 16
    float3 normal;      // 12
    float roughness;    // 4  -> 32
    float4 color;       // 16 -> 48
    float transmission; // 4
    float ior;          // 4
    float specular;     // 4
    float padding1;     // 4  -> 64
    float3 emission;    // 12
    float padding2;     // 4  -> 80
};

// ボックスデータ (with PBR material and rotation, must match C++ GPUBox) - 144 bytes
// OBB (Oriented Bounding Box) support via local axes
struct BoxData
{
    float3 center;      // 12
    float padding1;     // 4  -> 16
    float3 size;        // 12 (half-extents)
    float padding2;     // 4  -> 32
    // Local axes (rotation matrix columns) - for OBB
    float3 axisX;       // 12 (local X axis in world space)
    float padding3;     // 4  -> 48
    float3 axisY;       // 12 (local Y axis in world space)
    float padding4;     // 4  -> 64
    float3 axisZ;       // 12 (local Z axis in world space)
    float padding5;     // 4  -> 80
    float4 color;       // 16 -> 96
    float metallic;     // 4
    float roughness;    // 4
    float transmission; // 4
    float ior;          // 4  -> 112
    float specular;     // 4
    float padding6;     // 4
    float padding7;     // 4
    float padding8;     // 4  -> 128
    float3 emission;    // 12
    float padding9;     // 4  -> 144
};

// ライトデータ (must match C++ GPULight)
struct LightData
{
    float3 position;    // Position (Point) or Direction (Directional)
    float intensity;
    float4 color;
    uint type;          // LIGHT_TYPE_*
    float radius;       // Area light radius (0 = point light, hard shadows)
    float softShadowSamples; // Number of shadow samples (1-16)
    float padding;
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

// ============================================
// Mesh Data Buffers (for FBX triangle meshes)
// ============================================

// メッシュ頂点データ（Position + Normal インターリーブ）- 32 bytes
struct MeshVertex
{
    float3 position;    // 12 bytes
    float padding1;     // 4 bytes -> 16
    float3 normal;      // 12 bytes
    float padding2;     // 4 bytes -> 32
};

// メッシュ情報（各メッシュ種類の頂点/インデックスオフセット）- 16 bytes
struct MeshInfo
{
    uint vertexOffset;  // MeshVertices内の開始インデックス
    uint indexOffset;   // MeshIndices内の開始インデックス
    uint vertexCount;   // このメッシュの頂点数
    uint indexCount;    // このメッシュのインデックス数
};

// メッシュマテリアル（インスタンスごと）- 64 bytes
struct MeshMaterial
{
    float4 color;       // 16 bytes -> 16
    float metallic;     // 4
    float roughness;    // 4
    float transmission; // 4
    float ior;          // 4 -> 32
    float specular;     // 4
    float3 emission;    // 12 -> 48
    float padding1;     // 4
    float padding2;     // 4
    float padding3;     // 4
    float padding4;     // 4 -> 64
};

// メッシュインスタンス情報（TLASインスタンスごと）- 8 bytes
struct MeshInstanceInfo
{
    uint meshTypeIndex;     // MeshInfos内のインデックス（どのメッシュ種類か）
    uint materialIndex;     // MeshMaterials内のインデックス
};

// Mesh buffers (SRV)
StructuredBuffer<MeshVertex> MeshVertices : register(t5);           // 全メッシュの頂点を統合
StructuredBuffer<uint> MeshIndices : register(t6);                  // 全メッシュのインデックスを統合
StructuredBuffer<MeshMaterial> MeshMaterials : register(t7);        // インスタンスごとのマテリアル
StructuredBuffer<MeshInfo> MeshInfos : register(t8);                // メッシュ種類ごとのオフセット情報
StructuredBuffer<MeshInstanceInfo> MeshInstances : register(t9);    // インスタンスごとの参照情報

// Photon map buffer (for caustics)
RWStructuredBuffer<Photon> PhotonMap : register(u1);
RWStructuredBuffer<uint> PhotonCounter : register(u2);  // Atomic counter for photon index

// Spatial hash table for efficient photon gathering (O(1) lookup instead of O(N))
// Note: This buffer is populated by BuildPhotonHash compute shader after photon emission
RWStructuredBuffer<PhotonHashCell> PhotonHashTable : register(u11);

// ============================================
// G-Buffer Outputs for NRD Denoiser
// ============================================
// Always enabled - UAV declarations needed for ray tracing output
RWTexture2D<float4> GBuffer_DiffuseRadianceHitDist : register(u3);   // RGBA16F: Diffuse radiance + hit dist
RWTexture2D<float4> GBuffer_SpecularRadianceHitDist : register(u4); // RGBA16F: Specular radiance + hit dist
RWTexture2D<float4> GBuffer_NormalRoughness : register(u5);          // RGBA8: Normal (oct) + roughness
RWTexture2D<float>  GBuffer_ViewZ : register(u6);                    // R32F: Linear view depth
RWTexture2D<float2> GBuffer_MotionVectors : register(u7);            // RG16F: Screen-space motion
RWTexture2D<float4> GBuffer_Albedo : register(u8);                   // RGBA8: Albedo color
RWTexture2D<float2> GBuffer_ShadowData : register(u9);               // RG16F: X = penumbra, Y = visibility
RWTexture2D<float4> GBuffer_ShadowTranslucency : register(u10);      // RGBA16F: packed translucency

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

// Luminance helper (Rec.709)
float Luminance(float3 color)
{
    return dot(color, float3(0.2126, 0.7152, 0.0722));
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

// ============================================
// Universal PBR BRDF Functions
// ============================================
#define PI 3.14159265359

// GGX Normal Distribution Function (Trowbridge-Reitz)
float GGX_D(float NdotH, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * denom * denom + 0.0001);
}

// Smith Geometry Function (Schlick-GGX) for direct lighting
float Smith_G1(float NdotV, float k)
{
    return NdotV / (NdotV * (1.0 - k) + k);
}

float Smith_G(float NdotV, float NdotL, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;  // Remapping for direct lighting
    return Smith_G1(NdotV, k) * Smith_G1(NdotL, k);
}

// Fresnel-Schlick approximation (float3 version for PBR)
float3 Fresnel_Schlick3(float VdotH, float3 F0)
{
    return F0 + (1.0 - F0) * pow(saturate(1.0 - VdotH), 5.0);
}

// Cook-Torrance Specular BRDF
// Returns specular contribution for a single light
float3 CookTorranceSpecular(float3 N, float3 V, float3 L, float3 F0, float roughness)
{
    float3 H = normalize(V + L);
    
    float NdotL = max(dot(N, L), 0.001);
    float NdotV = max(dot(N, V), 0.001);
    float NdotH = max(dot(N, H), 0.0);
    float VdotH = max(dot(V, H), 0.0);
    
    // Distribution
    float D = GGX_D(NdotH, roughness);
    
    // Geometry
    float G = Smith_G(NdotV, NdotL, roughness);
    
    // Fresnel
    float3 F = Fresnel_Schlick3(VdotH, F0);
    
    // Cook-Torrance specular BRDF
    float3 specular = (D * G * F) / (4.0 * NdotV * NdotL + 0.001);
    
    return specular;
}

// Lambert Diffuse BRDF (energy conserving with Fresnel)
float3 LambertDiffuse(float3 diffuseColor)
{
    return diffuseColor / PI;
}

// Get sky color for background (realistic atmospheric gradient)
float3 GetSkyColor(float3 direction)
{
    float3 dir = normalize(direction);
    
    // Vertical gradient factor (0 = horizon, 1 = zenith)
    float elevation = dir.y;
    float t = saturate(elevation);  // 0 at horizon, 1 at zenith
    float tBelow = saturate(-elevation);  // For below horizon
    
    // Sky colors at different elevations
    float3 zenithColor = float3(0.15, 0.35, 0.75);     // Deep blue at zenith
    float3 skyMidColor = float3(0.35, 0.55, 0.90);     // Mid sky blue
    float3 horizonColor = float3(0.70, 0.80, 0.95);    // Light blue-white at horizon
    float3 horizonGlow = float3(0.95, 0.85, 0.70);     // Warm glow near horizon (atmospheric scattering)
    float3 groundColor = float3(0.25, 0.28, 0.35);     // Dark blue-gray below horizon
    
    float3 skyColor;
    
    if (elevation >= 0.0)
    {
        // Above horizon - blend from horizon to zenith
        // Use smoothstep for more natural gradient
        float horizonFade = smoothstep(0.0, 0.15, t);    // Horizon to low sky
        float midFade = smoothstep(0.1, 0.5, t);         // Low sky to mid sky
        float zenithFade = smoothstep(0.4, 1.0, t);      // Mid sky to zenith
        
        // Layer the gradients
        skyColor = horizonColor;
        
        // Add warm horizon glow (strongest at horizon, fades quickly)
        float glowIntensity = 1.0 - smoothstep(0.0, 0.08, t);
        skyColor = lerp(skyColor, horizonGlow, glowIntensity * 0.4);
        
        // Blend to mid sky
        skyColor = lerp(skyColor, skyMidColor, horizonFade);
        
        // Blend to zenith
        skyColor = lerp(skyColor, zenithColor, zenithFade);
        
        // Add subtle atmospheric haze near horizon (exponential falloff)
        float hazeAmount = exp(-t * 8.0) * 0.3;
        skyColor = lerp(skyColor, horizonColor, hazeAmount);
    }
    else
    {
        // Below horizon - blend from horizon to ground
        float groundFade = smoothstep(0.0, 0.3, tBelow);
        skyColor = lerp(horizonColor, groundColor, groundFade);
        
        // Dim the below-horizon area
        skyColor *= lerp(0.8, 0.4, groundFade);
    }
    
    // Reduce sky intensity to avoid blown-out highlights under exposure
    return skyColor * 0.7;
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

// ============================================
// Soft Shadow Sampling Functions
// ============================================

// Generate random point on a disk (for area light sampling)
float2 RandomOnDisk(inout uint seed)
{
    float r = sqrt(RandomFloat(seed));
    float theta = RandomFloat(seed) * 6.28318530718;
    return float2(r * cos(theta), r * sin(theta));
}

// Build orthonormal basis from direction vector
void BuildOrthonormalBasis(float3 dir, out float3 tangent, out float3 bitangent)
{
    float3 up = abs(dir.y) < 0.999 ? float3(0, 1, 0) : float3(1, 0, 0);
    tangent = normalize(cross(up, dir));
    bitangent = cross(dir, tangent);
}

// Sample point on spherical light source
float3 SampleSphericalLight(float3 lightCenter, float lightRadius, float3 hitPos, inout uint seed)
{
    // Sample random direction on unit sphere
    float2 diskSample = RandomOnDisk(seed);
    float z = sqrt(max(0.0, 1.0 - dot(diskSample, diskSample)));
    
    // Build tangent space toward light center
    float3 toLight = normalize(lightCenter - hitPos);
    float3 tangent, bitangent;
    BuildOrthonormalBasis(toLight, tangent, bitangent);
    
    // Transform disk sample to world space, scaled by light radius
    float3 sampleOffset = (tangent * diskSample.x + bitangent * diskSample.y) * lightRadius;
    return lightCenter + sampleOffset;
}

// Sample point on directional light disk (perpendicular to light direction)
float3 SampleDirectionalLightDisk(float3 lightDir, float lightRadius, float3 hitPos, inout uint seed)
{
    // Build tangent space perpendicular to light direction
    float3 tangent, bitangent;
    BuildOrthonormalBasis(-lightDir, tangent, bitangent);
    
    // Random offset on disk
    float2 diskSample = RandomOnDisk(seed);
    float3 offset = (tangent * diskSample.x + bitangent * diskSample.y) * lightRadius;
    
    // Return perturbed direction (not position, since directional lights are at infinity)
    return normalize(-lightDir + offset * 0.1);  // Small perturbation for directional
}

struct SoftShadowResult
{
    float visibility;
    float penumbra;
    float occluderDistance;
    float3 shadowColor;    // Color tint from translucent objects (white = no tint)
};

// Trace a single shadow ray and return visibility (0 = blocked, 1 = visible)
// Also returns shadowColor: white = no tint, colored = light filtered through translucent objects
// Loops through translucent objects to accumulate color tint
float TraceSingleShadowRay(float3 rayOrigin, float3 rayDir, float maxDist, out float occluderDistance, out float3 shadowColor)
{
    float3 currentOrigin = rayOrigin;
    float remainingDist = maxDist;
    float accumulatedVisibility = 1.0;
    float3 accumulatedColor = float3(1, 1, 1);
    occluderDistance = NRD_FP16_MAX;
    bool firstHit = true;
    
    // Loop through potentially multiple translucent objects
    const int MAX_TRANSLUCENT_LAYERS = 4;
    for (int layer = 0; layer < MAX_TRANSLUCENT_LAYERS; layer++)
    {
        RayDesc shadowRay;
        shadowRay.Origin = currentOrigin;
        shadowRay.Direction = rayDir;
        shadowRay.TMin = 0.001;
        shadowRay.TMax = remainingDist;
        
        RayPayload shadowPayload;
        shadowPayload.color = float3(0, 0, 0);
        shadowPayload.depth = SHADOW_RAY_DEPTH;  // Mark as shadow ray
        shadowPayload.hit = 0;
        shadowPayload.padding = 0.0;
        shadowPayload.diffuseRadiance = float3(0, 0, 0);
        shadowPayload.specularRadiance = float3(0, 0, 0);
        shadowPayload.hitDistance = NRD_FP16_MAX;
        shadowPayload.worldNormal = float3(0, 1, 0);
        shadowPayload.roughness = 1.0;
        shadowPayload.worldPosition = float3(0, 0, 0);
        shadowPayload.viewZ = 10000.0;
        shadowPayload.metallic = 0.0;
        shadowPayload.albedo = float3(0, 0, 0);
        shadowPayload.shadowVisibility = 1.0;
        shadowPayload.shadowPenumbra = 0.0;
        shadowPayload.shadowDistance = NRD_FP16_MAX;
        shadowPayload.targetObjectType = 0;
        shadowPayload.targetObjectIndex = 0;
        shadowPayload.thicknessQuery = 0;
        shadowPayload.shadowColorAccum = float3(1, 1, 1);
        shadowPayload.shadowTransmissionAccum = 0.0;  // Will be set by ClosestHit
        shadowPayload.loopRayOrigin = float4(0, 0, 0, 0);
        shadowPayload.loopRayDirection = float4(0, 0, 0, 0);
        shadowPayload.loopThroughput = float4(1, 1, 1, 0);
        
        TraceRay(SceneBVH, 
                 RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH,
                 0xFF, 0, 0, 0, shadowRay, shadowPayload);
        
        if (!shadowPayload.hit)
        {
            // No hit - light reaches through
            break;
        }
        
        // Record first occluder distance
        if (firstHit)
        {
            occluderDistance = shadowPayload.hitDistance;
            firstHit = false;
        }
        
        // Get transmission from payload (set by ClosestHit)
        float transmission = shadowPayload.shadowTransmissionAccum;
        float3 objectColor = shadowPayload.shadowColorAccum;
        
        if (transmission < 0.01)
        {
            // Opaque object - full shadow, no color
            shadowColor = float3(0, 0, 0);
            return 0.0;
        }
        
        // Translucent object - accumulate color and continue
        // Color tint: less transparent = more color influence
        float3 tintColor = lerp(objectColor, float3(1, 1, 1), transmission);
        accumulatedColor *= tintColor;
        accumulatedVisibility *= transmission;
        
        // If too little light remaining, stop
        if (accumulatedVisibility < 0.01)
        {
            shadowColor = accumulatedColor;
            return 0.0;
        }
        
        // Move origin past the hit object and continue
        currentOrigin = currentOrigin + rayDir * (shadowPayload.hitDistance + 0.01);
        remainingDist -= shadowPayload.hitDistance + 0.01;
        
        if (remainingDist <= 0.0)
            break;
    }
    
    shadowColor = accumulatedColor;
    return accumulatedVisibility;
}

// Calculate soft shadow visibility for a point light (area light)
// Returns: visibility value between 0 (fully shadowed) and 1 (fully lit)
// Also returns shadowColor for colored shadows from translucent objects
SoftShadowResult CalculateSoftShadowPoint(float3 hitPos, float3 normal, LightData light, inout uint seed)
{
    SoftShadowResult result;
    result.shadowColor = float3(1, 1, 1);  // Initialize to white (no tint)
    
    // If radius is 0 or very small, use hard shadow
    if (light.radius <= 0.001)
    {
        float3 lightDir = normalize(light.position - hitPos);
        float lightDist = length(light.position - hitPos);
        float occluderDistance;
        float3 sampleShadowColor;
        result.visibility = TraceSingleShadowRay(hitPos + normal * 0.001, lightDir, lightDist, occluderDistance, sampleShadowColor);
        result.occluderDistance = result.visibility < 0.99 ? occluderDistance : NRD_FP16_MAX;
        result.penumbra = 0.0;
        result.shadowColor = sampleShadowColor;
        return result;
    }
    
    // Soft shadow with multiple samples
    float visibility = 0.0;
    float penumbraSum = 0.0;
    float minOccluderDistance = NRD_FP16_MAX;
    int occludedCount = 0;
    int numSamples = clamp((int)light.softShadowSamples, 1, 16);
    float lightDistToCenter = length(light.position - hitPos);
    float lightSize = light.radius * 2.0;
    float3 colorSum = float3(0, 0, 0);
    int validSamples = 0;
    
    for (int i = 0; i < numSamples; i++)
    {
        // Sample point on spherical light
        float3 samplePos = SampleSphericalLight(light.position, light.radius, hitPos, seed);
        float3 sampleDir = normalize(samplePos - hitPos);
        float sampleDist = length(samplePos - hitPos);
        
        // Check if sample is above the surface
        if (dot(sampleDir, normal) > 0.0)
        {
            float occluderDistance;
            float3 sampleShadowColor;
            float sampleVisibility = TraceSingleShadowRay(hitPos + normal * 0.001, sampleDir, sampleDist, occluderDistance, sampleShadowColor);
            visibility += sampleVisibility;
            colorSum += sampleShadowColor * sampleVisibility;  // Weight by visibility
            validSamples++;
            
            if (sampleVisibility < 0.99)
            {
                occludedCount++;
                minOccluderDistance = min(minOccluderDistance, occluderDistance);
                penumbraSum += SIGMA_FrontEnd_PackPenumbra(occluderDistance, lightDistToCenter, lightSize);
            }
        }
    }
    
    result.visibility = validSamples > 0 ? (visibility / float(validSamples)) : 1.0;
    result.occluderDistance = occludedCount > 0 ? minOccluderDistance : NRD_FP16_MAX;
    result.penumbra = occludedCount > 0 ? (penumbraSum / float(occludedCount)) : 0.0;
    // Average the shadow color, weighted by visibility
    result.shadowColor = (visibility > 0.01) ? (colorSum / visibility) : float3(0, 0, 0);
    return result;
}

// Calculate soft shadow visibility for a directional light
// Returns: visibility value between 0 (fully shadowed) and 1 (fully lit)
// Also returns shadowColor for colored shadows from translucent objects
SoftShadowResult CalculateSoftShadowDirectional(float3 hitPos, float3 normal, LightData light, inout uint seed)
{
    SoftShadowResult result;
    result.shadowColor = float3(1, 1, 1);  // Initialize to white (no tint)
    float3 lightDir = normalize(-light.position);  // Direction stored in position for directional lights
    
    // If radius is 0 or very small, use hard shadow
    if (light.radius <= 0.001)
    {
        float occluderDistance;
        float3 sampleShadowColor;
        result.visibility = TraceSingleShadowRay(hitPos + normal * 0.001, lightDir, 10000.0, occluderDistance, sampleShadowColor);
        result.occluderDistance = result.visibility < 0.99 ? occluderDistance : NRD_FP16_MAX;
        result.penumbra = 0.0;
        result.shadowColor = sampleShadowColor;
        return result;
    }
    
    // Soft shadow with multiple samples
    float visibility = 0.0;
    float penumbraSum = 0.0;
    float minOccluderDistance = NRD_FP16_MAX;
    int occludedCount = 0;
    int numSamples = clamp((int)light.softShadowSamples, 1, 16);
    float tanAngularRadius = tan(light.radius);
    float3 colorSum = float3(0, 0, 0);
    int validSamples = 0;
    
    // Build tangent space perpendicular to light direction
    float3 tangent, bitangent;
    BuildOrthonormalBasis(lightDir, tangent, bitangent);
    
    for (int i = 0; i < numSamples; i++)
    {
        // Perturb light direction within cone angle based on radius (angular radius in radians)
        float2 diskSample = RandomOnDisk(seed);
        float3 perturbedDir = normalize(lightDir + 
            (tangent * diskSample.x + bitangent * diskSample.y) * light.radius);
        
        // Check if sample is above the surface
        if (dot(perturbedDir, normal) > 0.0)
        {
            float occluderDistance;
            float3 sampleShadowColor;
            float sampleVisibility = TraceSingleShadowRay(hitPos + normal * 0.001, perturbedDir, 10000.0, occluderDistance, sampleShadowColor);
            visibility += sampleVisibility;
            colorSum += sampleShadowColor * sampleVisibility;  // Weight by visibility
            validSamples++;
            
            if (sampleVisibility < 0.99)
            {
                occludedCount++;
                minOccluderDistance = min(minOccluderDistance, occluderDistance);
                penumbraSum += SIGMA_FrontEnd_PackPenumbra(occluderDistance, tanAngularRadius);
            }
        }
    }
    
    result.visibility = validSamples > 0 ? (visibility / float(validSamples)) : 1.0;
    result.occluderDistance = occludedCount > 0 ? minOccluderDistance : NRD_FP16_MAX;
    result.penumbra = occludedCount > 0 ? (penumbraSum / float(occludedCount)) : 0.0;
    // Average the shadow color, weighted by visibility
    result.shadowColor = (visibility > 0.01) ? (colorSum / visibility) : float3(0, 0, 0);
    return result;
}

// Unified soft shadow calculation for any light type
// Returns: visibility value between 0 (fully shadowed) and 1 (fully lit)
// Also returns shadowColor for colored shadows from translucent objects
SoftShadowResult CalculateSoftShadow(float3 hitPos, float3 normal, LightData light, inout uint seed)
{
    if (light.type == LIGHT_TYPE_AMBIENT)
    {
        SoftShadowResult result;
        result.visibility = 1.0;  // Ambient lights don't cast shadows
        result.penumbra = 0.0;
        result.occluderDistance = NRD_FP16_MAX;
        result.shadowColor = float3(1, 1, 1);  // No tint
        return result;
    }
    else if (light.type == LIGHT_TYPE_DIRECTIONAL)
    {
        return CalculateSoftShadowDirectional(hitPos, normal, light, seed);
    }
    else // LIGHT_TYPE_POINT
    {
        return CalculateSoftShadowPoint(hitPos, normal, light, seed);
    }
}

// Select a primary light for SIGMA shadow denoising
bool GetPrimaryShadowForSigma(float3 hitPos, float3 normal, inout uint seed, out SoftShadowResult result)
{
    if (Scene.NumLights > 0)
    {
        bool found = false;
        float bestWeight = -1.0;
        LightData bestLight;
        SoftShadowResult bestShadow;
        
        [loop]
        for (uint i = 0; i < Scene.NumLights; i++)
        {
            LightData light = Lights[i];
            if (light.type == LIGHT_TYPE_AMBIENT)
                continue;
            
            float3 lightDir;
            float ndotl = 0.0;
            float attenuation = 1.0;
            
            if (light.type == LIGHT_TYPE_DIRECTIONAL)
            {
                lightDir = normalize(-light.position);
                ndotl = dot(normal, lightDir);
                if (ndotl <= 0.0)
                    continue;
            }
            else
            {
                float3 toLight = light.position - hitPos;
                float lightDist = length(toLight);
                lightDir = toLight / max(lightDist, 1e-4);
                ndotl = dot(normal, lightDir);
                if (ndotl <= 0.0)
                    continue;
                attenuation = 1.0 / (1.0 + lightDist * lightDist * 0.01);
            }
            
            SoftShadowResult shadow = CalculateSoftShadow(hitPos, normal, light, seed);
            float shadowStrength = 1.0 - shadow.visibility;
            float weight = shadowStrength * ndotl * attenuation * light.intensity * Luminance(light.color.rgb);
            
            if (!found || weight > bestWeight)
            {
                found = true;
                bestWeight = weight;
                bestLight = light;
                bestShadow = shadow;
            }
        }
        
        if (found)
        {
            result = bestShadow;
            return true;
        }
    }
    
    // Fallback to legacy single light if no explicit lights are provided
    LightData fallbackLight;
    fallbackLight.position = Scene.LightPosition;
    fallbackLight.intensity = Scene.LightIntensity;
    fallbackLight.color = Scene.LightColor;
    fallbackLight.type = LIGHT_TYPE_POINT;
    fallbackLight.radius = 0.0;
    fallbackLight.softShadowSamples = 1.0;
    fallbackLight.padding = 0.0;
    result = CalculateSoftShadow(hitPos, normal, fallbackLight, seed);
    return true;
}

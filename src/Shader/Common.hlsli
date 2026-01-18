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
    float shadowVisibility;   // Shadow visibility for SIGMA (0-1)
    float shadowPenumbra;     // Packed penumbra radius for SIGMA
    float shadowDistance;     // Distance to occluder (or NRD_FP16_MAX)
    float padding2;           // Padding for alignment
    
    // Thickness query for refractive objects
    uint targetObjectType;
    uint targetObjectIndex;
    uint thicknessQuery;      // 1 = thickness ray, 0 = normal
    float padding3;
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
    // Matrices for motion vectors
    float4x4 ViewProjection;
    float4x4 PrevViewProjection;
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
RWTexture2D<float4> GBuffer_Albedo : register(u8);                   // RGBA8: Albedo color
RWTexture2D<float2> GBuffer_ShadowData : register(u9);               // RG16F: X = penumbra, Y = visibility
RWTexture2D<float4> GBuffer_ShadowTranslucency : register(u10);      // RGBA16F: packed translucency
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
};

// Trace a single shadow ray and return visibility (0 = blocked, 1 = visible)
float TraceSingleShadowRay(float3 rayOrigin, float3 rayDir, float maxDist, out float occluderDistance)
{
    RayDesc shadowRay;
    shadowRay.Origin = rayOrigin;
    shadowRay.Direction = rayDir;
    shadowRay.TMin = 0.001;
    shadowRay.TMax = maxDist;
    
    RayPayload shadowPayload;
    shadowPayload.color = float3(0, 0, 0);
    shadowPayload.depth = MAX_RECURSION_DEPTH;
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
    
    // Use primary hit group (index 0) with ClosestHit
    // Note: This works because ClosestHit sets payload.hit = 1
    // TODO: Add transparent object handling later
    TraceRay(SceneBVH, 
             RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH,
             0xFF, 0, 0, 0, shadowRay, shadowPayload);
    occluderDistance = shadowPayload.hit ? shadowPayload.hitDistance : NRD_FP16_MAX;
    return shadowPayload.hit ? 0.0 : 1.0;
}

// Calculate soft shadow visibility for a point light (area light)
// Returns: visibility value between 0 (fully shadowed) and 1 (fully lit)
SoftShadowResult CalculateSoftShadowPoint(float3 hitPos, float3 normal, LightData light, inout uint seed)
{
    SoftShadowResult result;
    // If radius is 0 or very small, use hard shadow
    if (light.radius <= 0.001)
    {
        float3 lightDir = normalize(light.position - hitPos);
        float lightDist = length(light.position - hitPos);
        float occluderDistance;
        result.visibility = TraceSingleShadowRay(hitPos + normal * 0.001, lightDir, lightDist, occluderDistance);
        result.occluderDistance = result.visibility < 0.5 ? occluderDistance : NRD_FP16_MAX;
        result.penumbra = 0.0;
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
            float sampleVisibility = TraceSingleShadowRay(hitPos + normal * 0.001, sampleDir, sampleDist, occluderDistance);
            visibility += sampleVisibility;
            if (sampleVisibility < 0.5)
            {
                occludedCount++;
                minOccluderDistance = min(minOccluderDistance, occluderDistance);
                penumbraSum += SIGMA_FrontEnd_PackPenumbra(occluderDistance, lightDistToCenter, lightSize);
            }
        }
    }
    
    result.visibility = visibility / float(numSamples);
    result.occluderDistance = occludedCount > 0 ? minOccluderDistance : NRD_FP16_MAX;
    result.penumbra = occludedCount > 0 ? (penumbraSum / float(occludedCount)) : 0.0;
    return result;
}

// Calculate soft shadow visibility for a directional light
// Returns: visibility value between 0 (fully shadowed) and 1 (fully lit)
SoftShadowResult CalculateSoftShadowDirectional(float3 hitPos, float3 normal, LightData light, inout uint seed)
{
    SoftShadowResult result;
    float3 lightDir = normalize(-light.position);  // Direction stored in position for directional lights
    
    // If radius is 0 or very small, use hard shadow
    if (light.radius <= 0.001)
    {
        float occluderDistance;
        result.visibility = TraceSingleShadowRay(hitPos + normal * 0.001, lightDir, 10000.0, occluderDistance);
        result.occluderDistance = result.visibility < 0.5 ? occluderDistance : NRD_FP16_MAX;
        result.penumbra = 0.0;
        return result;
    }
    
    // Soft shadow with multiple samples
    float visibility = 0.0;
    float penumbraSum = 0.0;
    float minOccluderDistance = NRD_FP16_MAX;
    int occludedCount = 0;
    int numSamples = clamp((int)light.softShadowSamples, 1, 16);
    float tanAngularRadius = tan(light.radius);
    
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
            float sampleVisibility = TraceSingleShadowRay(hitPos + normal * 0.001, perturbedDir, 10000.0, occluderDistance);
            visibility += sampleVisibility;
            if (sampleVisibility < 0.5)
            {
                occludedCount++;
                minOccluderDistance = min(minOccluderDistance, occluderDistance);
                penumbraSum += SIGMA_FrontEnd_PackPenumbra(occluderDistance, tanAngularRadius);
            }
        }
    }
    
    result.visibility = visibility / float(numSamples);
    result.occluderDistance = occludedCount > 0 ? minOccluderDistance : NRD_FP16_MAX;
    result.penumbra = occludedCount > 0 ? (penumbraSum / float(occludedCount)) : 0.0;
    return result;
}

// Unified soft shadow calculation for any light type
// Returns: visibility value between 0 (fully shadowed) and 1 (fully lit)
SoftShadowResult CalculateSoftShadow(float3 hitPos, float3 normal, LightData light, inout uint seed)
{
    if (light.type == LIGHT_TYPE_AMBIENT)
    {
        SoftShadowResult result;
        result.visibility = 1.0;  // Ambient lights don't cast shadows
        result.penumbra = 0.0;
        result.occluderDistance = NRD_FP16_MAX;
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

// Calculate a SINGLE stochastic shadow ray for SIGMA denoising
// SIGMA expects noisy single-sample input, NOT averaged samples!
// This traces ONE ray toward a random point on the light source
SoftShadowResult CalculateSingleShadowRayForSigma(float3 hitPos, float3 normal, LightData light, inout uint seed)
{
    SoftShadowResult result;
    result.visibility = 1.0;
    result.penumbra = 0.0;
    result.occluderDistance = NRD_FP16_MAX;
    
    float3 lightDir;
    float lightDist = 10000.0;
    float lightRadius = max(light.radius, 0.001);
    
    if (light.type == LIGHT_TYPE_DIRECTIONAL)
    {
        // Directional light: perturb direction based on angular radius
        lightDir = normalize(-light.position);
        
        // Build tangent space
        float3 tangent, bitangent;
        BuildOrthonormalBasis(lightDir, tangent, bitangent);
        
        // Random sample on disk -> perturbed direction
        float2 diskSample = RandomOnDisk(seed);
        lightDir = normalize(lightDir + (tangent * diskSample.x + bitangent * diskSample.y) * lightRadius);
    }
    else if (light.type == LIGHT_TYPE_POINT)
    {
        // Point light: sample random point on light sphere
        float3 toLight = light.position - hitPos;
        lightDist = length(toLight);
        float3 baseLightDir = toLight / max(lightDist, 1e-4);
        
        // Build tangent space perpendicular to light direction
        float3 tangent, bitangent;
        BuildOrthonormalBasis(baseLightDir, tangent, bitangent);
        
        // Random point on light surface (sphere)
        float2 diskSample = RandomOnDisk(seed);
        float3 lightSamplePos = light.position + 
            lightRadius * (tangent * diskSample.x + bitangent * diskSample.y);
        
        lightDir = normalize(lightSamplePos - hitPos);
        lightDist = length(lightSamplePos - hitPos);
    }
    else
    {
        // Ambient light - no shadow
        return result;
    }
    
    // Check if light is above surface
    float ndotl = dot(normal, lightDir);
    if (ndotl <= 0.0)
    {
        // Light is below horizon - fully shadowed
        result.visibility = 0.0;
        result.occluderDistance = 0.001;  // Very close occluder for SIGMA
        result.penumbra = 0.0;  // Hard shadow at grazing angles
        return result;
    }
    
    // Trace single shadow ray with proper bias
    // Bias too large causes contact light leaks (white halo).
    // Keep it small to preserve contact shadows.
    float bias = 0.001;
    float occluderDistance;
    result.visibility = TraceSingleShadowRay(hitPos + normal * bias, lightDir, lightDist, occluderDistance);
    
    if (result.visibility < 0.5)
    {
        result.occluderDistance = occluderDistance;
        
        // Calculate penumbra based on geometry
        // Larger penumbra = softer edge, better SIGMA filtering
        // penumbra = (lightRadius / occluderDistance) for point lights
        float basePenumbra;
        if (light.type == LIGHT_TYPE_POINT)
        {
            // Point light: penumbra depends on light size and occluder distance
            float tanPenumbra = lightRadius / max(occluderDistance, 0.01);
            basePenumbra = SIGMA_FrontEnd_PackPenumbra(occluderDistance, lightDist, lightRadius * 2.0);
        }
        else
        {
            // Directional: use angular radius (in radians)
            float tanAngularRadius = tan(lightRadius);
            basePenumbra = SIGMA_FrontEnd_PackPenumbra(occluderDistance, tanAngularRadius);
        }
        
        // Ensure minimum penumbra for SIGMA to work effectively
        // This prevents hard pixel edges from noisy sampling
        result.penumbra = max(basePenumbra, 0.5);
    }
    else
    {
        // No occlusion - lit area
        result.occluderDistance = NRD_FP16_MAX;
        result.penumbra = 0.0;  // No penumbra needed for lit areas
    }
    
    return result;
}

// Select a primary light for SIGMA shadow denoising
bool GetPrimaryShadowForSigma(float3 hitPos, float3 normal, inout uint seed, out SoftShadowResult result)
{
    if (Scene.NumLights > 0)
    {
        bool found = false;
        float bestWeight = -1.0;
        LightData bestLight;
        
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
            
            // Calculate weight based on light contribution (not shadow yet)
            float weight = ndotl * attenuation * light.intensity * Luminance(light.color.rgb);
            
            if (!found || weight > bestWeight)
            {
                found = true;
                bestWeight = weight;
                bestLight = light;
            }
        }
        
        if (found)
        {
            // Use single stochastic shadow ray for SIGMA
            // SIGMA will temporally reconstruct from noisy single-sample input
            result = CalculateSingleShadowRayForSigma(hitPos, normal, bestLight, seed);
            return true;
        }
    }
    
    // Fallback to legacy single light if no explicit lights are provided
    LightData fallbackLight;
    fallbackLight.position = Scene.LightPosition;
    fallbackLight.intensity = Scene.LightIntensity;
    fallbackLight.color = Scene.LightColor;
    fallbackLight.type = LIGHT_TYPE_POINT;
    fallbackLight.radius = 0.5;  // Give it some radius for soft shadow
    fallbackLight.softShadowSamples = 1.0;
    fallbackLight.padding = 0.0;
    result = CalculateSingleShadowRayForSigma(hitPos, normal, fallbackLight, seed);
    return true;
}

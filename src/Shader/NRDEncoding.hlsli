// NRD Encoding/Decoding Helpers
// Based on NVIDIA NRD SDK
// These functions help pack/unpack data for NRD denoiser input

#ifndef NRD_ENCODING_HLSLI
#define NRD_ENCODING_HLSLI

// ============================================
// NRD Configuration
// ============================================
// Minimal NRD constants (from NRD.hlsli)
#ifndef NRD_FP16_MAX
#define NRD_FP16_MAX 65504.0
#endif
#ifndef NRD_EPS
#define NRD_EPS 1e-6
#endif
// NRD_NORMAL_ENCODING: 
//   1 = RGBA8_UNORM with simple encoding
//   2 = RGBA8_UNORM with octahedron encoding (recommended)
#ifndef NRD_NORMAL_ENCODING
#define NRD_NORMAL_ENCODING 2
#endif

// NRD_ROUGHNESS_ENCODING:
//   0 = Linear roughness
//   1 = sqrt(linear roughness) (recommended)
#ifndef NRD_ROUGHNESS_ENCODING
#define NRD_ROUGHNESS_ENCODING 1
#endif

// ============================================================
// NRD/SIGMA CONSTANTS TABLE
// ============================================================
// PURPOSE: Centralized constants for NRD denoiser integration
// RATIONALE: These values are tuned for NRD's internal algorithms
// ============================================================

// --- SIGMA Penumbra Constants ---
// SIGMA expects penumbra in world units, but has internal limits
#define SIGMA_PENUMBRA_ABSOLUTE_MAX    32768.0  // NRD internal limit (half of FP16_MAX range)
#define SIGMA_PENUMBRA_PRACTICAL_MAX   100.0    // Above this, SIGMA treats as "almost no shadow"
#define SIGMA_PENUMBRA_MIN             0.1      // Minimum visible penumbra

// --- ViewZ Constants ---
// ViewZ: camera-space Z (positive = in front of camera)
#define VIEWZ_MIN                      0.01     // Avoid zero depth for NRD accumulation
#define VIEWZ_SKY                      10000.0  // Far distance for sky/miss pixels (SIGMA edge handling)

// --- Motion Vector Constants ---
// Motion vectors in pixel space, clamped to prevent extreme values
#define MOTION_VECTOR_CLAMP            64.0     // Max pixels per frame (reasonable at 60fps)

// --- Roughness Thresholds ---
// Mirror-like surfaces bypass NRD (causes artifacts on low roughness)
#define MIRROR_BYPASS_ROUGHNESS        0.05     // Below this, use raw specular without NRD

// --- Shadow Constants ---
#define SHADOW_FULLY_LIT_THRESHOLD     0.99     // Above this, considered fully lit (no shadow)

// ============================================
// Math Helpers
// ============================================

float2 OctWrap(float2 v)
{
    // Equivalent to: select(v.xy >= 0.0, float2(1.0, 1.0), float2(-1.0, -1.0))
    float2 sign_v = float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
    return (1.0 - abs(v.yx)) * sign_v;
}

// Encode normalized direction to octahedron [0, 1]
float2 EncodeUnitVector(float3 n)
{
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    n.xy = n.z >= 0.0 ? n.xy : OctWrap(n.xy);
    n.xy = n.xy * 0.5 + 0.5;
    return n.xy;
}

// Decode octahedron [0, 1] to normalized direction
float3 DecodeUnitVector(float2 p)
{
    p = p * 2.0 - 1.0;
    float3 n = float3(p.xy, 1.0 - abs(p.x) - abs(p.y));
    float t = saturate(-n.z);
    // Equivalent to: select(n.xy >= 0.0, float2(-t, -t), float2(t, t))
    n.x += (n.x >= 0.0) ? -t : t;
    n.y += (n.y >= 0.0) ? -t : t;
    return normalize(n);
}

// ============================================
// Normal + Roughness Encoding (RGBA8)
// ============================================

// Pack normal and roughness into RGBA8 format for NRD
float4 NRD_FrontEnd_PackNormalAndRoughness(float3 normal, float roughness)
{
    float4 result;
    
#if NRD_NORMAL_ENCODING == 2
    // Octahedron encoding (better precision for normals)
    result.xy = EncodeUnitVector(normal);
#else
    // Simple encoding
    result.xy = normal.xy * 0.5 + 0.5;
#endif

    // Encode Z sign in the third component
    result.z = normal.z >= 0.0 ? 1.0 : 0.0;
    
#if NRD_ROUGHNESS_ENCODING == 1
    // sqrt encoding for better precision in smooth surfaces
    result.w = sqrt(saturate(roughness));
#else
    result.w = saturate(roughness);
#endif

    return result;
}

// Unpack normal and roughness from RGBA8 format
void NRD_FrontEnd_UnpackNormalAndRoughness(float4 packed, out float3 normal, out float roughness)
{
#if NRD_NORMAL_ENCODING == 2
    normal = DecodeUnitVector(packed.xy);
#else
    normal.xy = packed.xy * 2.0 - 1.0;
    normal.z = sqrt(max(0.0, 1.0 - dot(normal.xy, normal.xy)));
#endif

    // Apply Z sign
    if (packed.z < 0.5)
        normal.z = -normal.z;
    
#if NRD_ROUGHNESS_ENCODING == 1
    roughness = packed.w * packed.w;
#else
    roughness = packed.w;
#endif
}

// ============================================
// Radiance + HitDist Encoding (RGBA16F)
// ============================================

// REBLUR: Pack radiance and normalized hit distance
float4 REBLUR_FrontEnd_PackRadianceAndNormHitDist(float3 radiance, float normHitDist)
{
    return float4(radiance, normHitDist);
}

// REBLUR: Get normalized hit distance
// normHitDist = hitDist / denoisingRange
float REBLUR_FrontEnd_GetNormHitDist(float hitDist, float viewZ, float denoisingRange)
{
    // Normalize hit distance relative to view Z and denoising range
    // This helps NRD handle varying hit distances better
    float normHitDist = hitDist / (denoisingRange + 1e-6);
    return saturate(normHitDist);
}

// RELAX: Pack radiance and hit distance (RELAX uses different encoding)
float4 RELAX_FrontEnd_PackRadianceAndHitDist(float3 radiance, float hitDist)
{
    return float4(radiance, hitDist);
}

// ============================================
// SIGMA Shadow Encoding
// ============================================

// SIGMA single light
// Infinite (directional) light source
// X => IN_PENUMBRA
float SIGMA_FrontEnd_PackPenumbra(float distanceToOccluder, float tanOfLightAngularRadius)
{
    float penumbraSize = distanceToOccluder * tanOfLightAngularRadius;
    float penumbraRadius = penumbraSize * 0.5;

    return distanceToOccluder >= NRD_FP16_MAX ? NRD_FP16_MAX : min(penumbraRadius, SIGMA_PENUMBRA_ABSOLUTE_MAX);
}

// Local light source
// X => IN_PENUMBRA
// "lightSize" must be an acceptable projection to the plane perpendicular to the light direction
float SIGMA_FrontEnd_PackPenumbra(float distanceToOccluder, float distanceToLight, float lightSize)
{
    float penumbraSize = lightSize * distanceToOccluder / max(distanceToLight - distanceToOccluder, NRD_EPS);
    float penumbraRadius = penumbraSize * 0.5;

    return distanceToOccluder >= NRD_FP16_MAX ? NRD_FP16_MAX : min(penumbraRadius, SIGMA_PENUMBRA_ABSOLUTE_MAX);
}

// X => IN_TRANSLUCENCY
float4 SIGMA_FrontEnd_PackTranslucency(float distanceToOccluder, float3 translucency)
{
    float4 r;
    r.x = float(distanceToOccluder >= NRD_FP16_MAX);
    r.yzw = saturate(translucency);

    return r;
}

// OUT_SHADOW_TRANSLUCENCY => X (and YZW for translucency)
// Note: Removed the squaring (shadow * shadow) that was causing contrast differences
// between denoised and non-denoised paths. The shadow value is used directly.
float4 SIGMA_BackEnd_UnpackShadow(float4 shadow)
{
    return shadow;
}

// ============================================
// Motion Vector Encoding
// ============================================

// Encode screen-space motion vector (in pixels)
// prevUV = UV of current pixel in previous frame
// currUV = UV of current pixel in current frame
// Motion = prevUV - currUV (NRD convention)
float2 NRD_EncodeMotionVector(float2 prevUV, float2 currUV, float2 screenSize)
{
    float2 motion = prevUV - currUV;
    return motion * screenSize; // Convert to pixel space
}

// ============================================
// View Z Encoding
// ============================================

// Get linear view Z from world position and view matrix
float NRD_GetViewZ(float3 worldPos, float4x4 viewMatrix)
{
    float4 viewPos = mul(viewMatrix, float4(worldPos, 1.0));
    return viewPos.z;
}

// Alternative: get view Z from ray hit distance and direction
float NRD_GetViewZ_FromHit(float3 cameraPos, float3 cameraForward, float3 hitPos)
{
    float3 toHit = hitPos - cameraPos;
    return dot(toHit, cameraForward);
}

// ============================================
// Material Classification
// ============================================

// Separate diffuse and specular radiance based on material properties
void NRD_SeparateDiffuseSpecular(
    float3 radiance,
    float3 albedo,
    float metallic,
    out float3 diffuseRadiance,
    out float3 specularRadiance)
{
    // For metals: no diffuse, all specular
    // For dielectrics: diffuse + specular
    float3 diffuseAlbedo = albedo * (1.0 - metallic);
    float3 specularF0 = lerp(float3(0.04, 0.04, 0.04), albedo, metallic);
    
    // Approximate separation (simplified)
    diffuseRadiance = radiance * diffuseAlbedo / (diffuseAlbedo + specularF0 + 0.001);
    specularRadiance = radiance - diffuseRadiance;
}

// ============================================
// NRD Input Builder
// ============================================
// Centralized coordinate system conversion for NRD inputs.
// THIS IS THE AUTHORITATIVE SOURCE for NRD coordinate conventions.
//
// Coordinate System:
//   View Space: X = right, Y = up, Z = forward (into screen)
//   ViewZ: Positive distance along camera forward direction
//   Motion Vector: Screen-space pixel displacement (current - previous)

struct NRDInputs
{
    float3 viewSpaceNormal;     // Normal in view space
    float  viewZ;               // Linear depth (positive, distance along forward)
    float2 motionVector;        // Screen-space motion in pixels
    float  albedoAlpha;         // 1.0 for hits, 0.0 for misses (sky detection)
};

// Build NRD inputs with correct coordinate system transformations
// All NRD-related coordinate conversions should use this function.
//
// Parameters:
//   worldNormal     - Surface normal in world space
//   worldHitPos     - Hit position in world space
//   cameraPos       - Camera position in world space
//   cameraRight     - Camera right vector (normalized)
//   cameraUp        - Camera up vector (normalized)
//   cameraForward   - Camera forward vector (normalized, points into scene)
//   currViewProj    - Current frame's view-projection matrix
//   prevViewProj    - Previous frame's view-projection matrix
//   screenSize      - Screen dimensions (width, height)
//   anyHit          - True if ray hit geometry, false for sky/miss
//
NRDInputs NRD_BuildInputs(
    float3 worldNormal,
    float3 worldHitPos,
    float3 cameraPos,
    float3 cameraRight,
    float3 cameraUp,
    float3 cameraForward,
    float4x4 currViewProj,
    float4x4 prevViewProj,
    float2 screenSize,
    bool anyHit)
{
    NRDInputs result;
    
    // ========================================
    // View Space Normal
    // ========================================
    // Transform world-space normal to view space using camera basis vectors.
    // View space convention: X=right, Y=up, Z=forward (into screen)
    result.viewSpaceNormal.x = dot(worldNormal, cameraRight);
    result.viewSpaceNormal.y = dot(worldNormal, cameraUp);
    result.viewSpaceNormal.z = dot(worldNormal, cameraForward);
    result.viewSpaceNormal = normalize(result.viewSpaceNormal);
    
    // ========================================
    // ViewZ (Linear Depth)
    // ========================================
    // NRD/SIGMA expects POSITIVE view depth (distance along camera forward).
    // For miss pixels, use a large value (not 0) so SIGMA can properly
    // handle edge filtering. Zero viewZ causes discontinuities.
    if (anyHit)
    {
        float3 hitOffset = worldHitPos - cameraPos;
        result.viewZ = dot(hitOffset, cameraForward);  // Positive distance along forward
        result.viewZ = max(result.viewZ, VIEWZ_MIN);   // Minimum positive depth (avoid zero)
        result.albedoAlpha = 1.0;                      // Mark as hit
    }
    else
    {
        // Use large depth for misses - helps SIGMA handle edges properly
        result.viewZ = VIEWZ_SKY;                      // Far distance for sky/miss pixels
        result.albedoAlpha = 0.0;                      // Mark as miss for Composite shader
    }
    
    // ========================================
    // Motion Vector
    // ========================================
    // Screen-space pixel delta: (current - previous) position
    // This is the correct, unmodified motion vector for NRD.
    // Do NOT apply any damping here - use NRD settings if stabilization is needed.
    if (anyHit)
    {
        float4 currClip = mul(float4(worldHitPos, 1.0), currViewProj);
        float4 prevClip = mul(float4(worldHitPos, 1.0), prevViewProj);
        
        float2 currNdc = currClip.xy / currClip.w;
        float2 prevNdc = prevClip.xy / prevClip.w;
        
        // NDC delta: range [-2, 2]
        float2 ndcDelta = currNdc - prevNdc;
        
        // Convert to pixel space: NDC * (screenSize / 2)
        float2 pixelScale = screenSize * 0.5;
        result.motionVector = ndcDelta * pixelScale;
        
        // Clamp to reasonable range to prevent extreme values
        result.motionVector = clamp(result.motionVector, float2(-MOTION_VECTOR_CLAMP, -MOTION_VECTOR_CLAMP), float2(MOTION_VECTOR_CLAMP, MOTION_VECTOR_CLAMP));
    }
    else
    {
        result.motionVector = float2(0, 0);
    }
    
    return result;
}

// Convenience function to compute only ViewZ
// Use when you only need depth without full NRD input building
float NRD_ComputeViewZ(float3 worldHitPos, float3 cameraPos, float3 cameraForward, bool anyHit)
{
    if (anyHit)
    {
        float3 hitOffset = worldHitPos - cameraPos;
        float viewZ = dot(hitOffset, cameraForward);
        return max(viewZ, VIEWZ_MIN);
    }
    return VIEWZ_SKY;
}

// Convenience function to compute only view-space normal
// Use when you only need the normal transformation
float3 NRD_ComputeViewSpaceNormal(float3 worldNormal, float3 cameraRight, float3 cameraUp, float3 cameraForward)
{
    float3 viewNormal;
    viewNormal.x = dot(worldNormal, cameraRight);
    viewNormal.y = dot(worldNormal, cameraUp);
    viewNormal.z = dot(worldNormal, cameraForward);
    return normalize(viewNormal);
}

// Convenience function to compute only motion vector
// Use when you only need motion without full NRD input building
float2 NRD_ComputeMotionVector(
    float3 worldHitPos,
    float4x4 currViewProj,
    float4x4 prevViewProj,
    float2 screenSize,
    bool anyHit)
{
    if (!anyHit)
        return float2(0, 0);
    
    float4 currClip = mul(float4(worldHitPos, 1.0), currViewProj);
    float4 prevClip = mul(float4(worldHitPos, 1.0), prevViewProj);
    
    float2 currNdc = currClip.xy / currClip.w;
    float2 prevNdc = prevClip.xy / prevClip.w;
    
    float2 ndcDelta = currNdc - prevNdc;
    float2 pixelScale = screenSize * 0.5;
    float2 motion = ndcDelta * pixelScale;
    
    return clamp(motion, float2(-MOTION_VECTOR_CLAMP, -MOTION_VECTOR_CLAMP), float2(MOTION_VECTOR_CLAMP, MOTION_VECTOR_CLAMP));
}

#endif // NRD_ENCODING_HLSLI

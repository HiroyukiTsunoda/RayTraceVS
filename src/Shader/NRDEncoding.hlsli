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

    return distanceToOccluder >= NRD_FP16_MAX ? NRD_FP16_MAX : min(penumbraRadius, 32768.0);
}

// Local light source
// X => IN_PENUMBRA
// "lightSize" must be an acceptable projection to the plane perpendicular to the light direction
float SIGMA_FrontEnd_PackPenumbra(float distanceToOccluder, float distanceToLight, float lightSize)
{
    float penumbraSize = lightSize * distanceToOccluder / max(distanceToLight - distanceToOccluder, NRD_EPS);
    float penumbraRadius = penumbraSize * 0.5;

    return distanceToOccluder >= NRD_FP16_MAX ? NRD_FP16_MAX : min(penumbraRadius, 32768.0);
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
float4 SIGMA_BackEnd_UnpackShadow(float4 shadow)
{
    return shadow * shadow;
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

#endif // NRD_ENCODING_HLSLI

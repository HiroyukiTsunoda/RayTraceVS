// Composite Shader
// Combines denoised diffuse and specular radiance into final output
// This is used after NRD denoising to produce the final image
// Now also supports SIGMA-denoised shadows

// ============================================================
// COLOR SPACE RULES
// ============================================================
// 1. All intermediate calculations are in LINEAR space
// 2. sRGB conversion happens ONLY at final output (CSMain line ~410)
// 3. Debug modes must NOT apply sRGB individually - use finalOutput path
// 4. Exposure is applied in LINEAR space BEFORE tonemapping
// ============================================================

// ============================================================
// GAMMA CONSTANTS
// ============================================================
#define GAMMA_SRGB_STANDARD 2.2
#define GAMMA_SRGB_TOLERANCE 0.01

#include "NRDEncoding.hlsli"

// Output render target
RWTexture2D<float4> OutputTexture : register(u0);

// Denoised inputs from NRD
Texture2D<float4> DenoisedDiffuse : register(t0);     // Denoised diffuse radiance (REBLUR output)
Texture2D<float4> DenoisedSpecular : register(t1);    // Denoised specular radiance (REBLUR output)
Texture2D<float4> AlbedoTexture : register(t2);       // Optional: albedo for modulation
Texture2D<float4> DenoisedShadow : register(t3);      // Denoised shadow visibility (SIGMA output)

// G-Buffer inputs (for debug visualization)
Texture2D<float4> GBuffer_DiffuseIn : register(t4);   // Input diffuse before denoising
Texture2D<float4> GBuffer_SpecularIn : register(t5); // Input specular before denoising
Texture2D<float4> GBuffer_NormalRoughness : register(t6);
Texture2D<float> GBuffer_ViewZ : register(t7);
Texture2D<float2> GBuffer_MotionVectors : register(t8);
Texture2D<float2> GBuffer_ShadowData : register(t9);  // Input shadow before denoising
Texture2D<float4> RawSpecularBackup : register(t10);  // Raw specular before NRD (for mirror bypass)

// Samplers
SamplerState LinearSampler : register(s0);
SamplerState PointSampler : register(s1);

// Constants
cbuffer CompositeConstants : register(b0)
{
    uint2 OutputSize;
    float ExposureValue;
    float ToneMapOperator;  // 0 = Reinhard, 1 = ACES, 2 = None
    uint DebugMode;         // 0 = off, 1 = show debug tiles
    float DebugTileScale;   // Size of debug tiles (0.15 = 15% of screen height)
    uint UseDenoisedShadow; // 1 = apply SIGMA-denoised shadow
    float ShadowStrength;   // Multiplier for shadow contrast (1.0 = normal)
    float GammaValue;       // Gamma correction value (2.2 = standard sRGB, 1.0 = linear)
    uint PhotonMapSize;     // Current photon count (for overlay)
    uint MaxPhotons;        // Max photon capacity (for overlay)
};

// ============================================
// Tone Mapping Functions
// ============================================

// Simple Reinhard tone mapping
float3 ReinhardToneMap(float3 color)
{
    return color / (1.0 + color);
}

// ACES Filmic Tone Mapping
// Attempt to approximate the look of ACES
float3 ACESFilm(float3 x)
{
    float a = 2.51f;
    float b = 0.03f;
    float c = 2.43f;
    float d = 0.59f;
    float e = 0.14f;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}

// Gamma correction (standard sRGB)
float3 LinearToSRGB(float3 color)
{
    // Component-wise sRGB conversion (compatible with SM 5.1)
    float3 srgb;
    srgb.r = (color.r < 0.0031308) ? (12.92 * color.r) : (1.055 * pow(color.r, 1.0 / 2.4) - 0.055);
    srgb.g = (color.g < 0.0031308) ? (12.92 * color.g) : (1.055 * pow(color.g, 1.0 / 2.4) - 0.055);
    srgb.b = (color.b < 0.0031308) ? (12.92 * color.b) : (1.055 * pow(color.b, 1.0 / 2.4) - 0.055);
    return srgb;
}

// Custom gamma correction
float3 ApplyGamma(float3 color, float gamma)
{
    return pow(max(color, 0.0), 1.0 / gamma);
}

float Luminance(float3 color)
{
    return dot(color, float3(0.2126, 0.7152, 0.0722));
}

// Simple heatmap for debug visualization
float3 Heatmap(float t)
{
    t = saturate(t);
    float3 c1 = float3(0.0, 0.0, 0.2);
    float3 c2 = float3(0.0, 0.4, 1.0);
    float3 c3 = float3(0.0, 1.0, 0.2);
    float3 c4 = float3(1.0, 1.0, 0.0);
    float3 c5 = float3(1.0, 0.2, 0.0);
    if (t < 0.25)
        return lerp(c1, c2, t / 0.25);
    if (t < 0.5)
        return lerp(c2, c3, (t - 0.25) / 0.25);
    if (t < 0.75)
        return lerp(c3, c4, (t - 0.5) / 0.25);
    return lerp(c4, c5, (t - 0.75) / 0.25);
}

// ============================================
// Debug Visualization Helpers
// ============================================

// Visualize depth (ViewZ) with color coding
float3 VisualizeDepth(float viewZ)
{
    // Map depth to color: near=blue, mid=green, far=red
    float normalizedDepth = saturate(viewZ / 100.0); // Assume max depth 100
    
    if (normalizedDepth < 0.5)
    {
        // Blue to Green
        float t = normalizedDepth * 2.0;
        return lerp(float3(0, 0, 1), float3(0, 1, 0), t);
    }
    else
    {
        // Green to Red  
        float t = (normalizedDepth - 0.5) * 2.0;
        return lerp(float3(0, 1, 0), float3(1, 0, 0), t);
    }
}

// Visualize motion vectors
float3 VisualizeMotionVectors(float2 mv)
{
    // Scale for visibility and map to color
    float2 scaled = mv * 10.0; // Scale up for visibility
    return float3(abs(scaled.x), abs(scaled.y), 0.5);
}

// Visualize normal (from oct-encoded)
float3 VisualizeNormal(float4 normalRoughness)
{
    // Normal is in XYZ, roughness in W
    // Assume normal is already in [-1,1] range or oct-encoded
    float3 n = normalRoughness.xyz * 2.0 - 1.0;
    return n * 0.5 + 0.5; // Map to [0,1] for display
}

// ============================================
// Main Compute Shader
// ============================================

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 pixelCoord = dispatchThreadId.xy;
    
    // Bounds check
    if (pixelCoord.x >= OutputSize.x || pixelCoord.y >= OutputSize.y)
        return;
    
    // Calculate UV for texture sampling
    float2 uv = (float2(pixelCoord) + 0.5) / float2(OutputSize);
    
    float3 finalColor;
    
    // ========================================
    // Debug Modes 2-10: Unified output path
    // All debug outputs are computed in LINEAR space and converted to sRGB once at the end
    // ========================================
    float3 debugOutput = float3(0, 0, 0);
    bool useDebugOutput = false;
    
    // Debug Mode 2: full-screen input shadow visibility
    if (DebugMode == 2)
    {
        float2 shadowIn = GBuffer_ShadowData.SampleLevel(LinearSampler, uv, 0);
        float shadowVis = shadowIn.y;  // Y = visibility (0 = shadow, 1 = lit)
        debugOutput = float3(shadowVis, shadowVis, shadowVis);
        useDebugOutput = true;
    }
    // Debug Mode 3: full-screen denoised shadow visibility
    else if (DebugMode == 3)
    {
        float shadowOut = SIGMA_BackEnd_UnpackShadow(DenoisedShadow.SampleLevel(LinearSampler, uv, 0)).x;
        debugOutput = float3(shadowOut, shadowOut, shadowOut);
        useDebugOutput = true;
    }
    // Debug Mode 4: split-screen input vs denoised shadow
    else if (DebugMode == 4)
    {
        if (uv.x < 0.5)
        {
            float2 shadowIn = GBuffer_ShadowData.SampleLevel(LinearSampler, uv, 0);
            float shadowVis = shadowIn.y;
            debugOutput = float3(shadowVis, shadowVis, shadowVis);
        }
        else
        {
            float shadowOut = SIGMA_BackEnd_UnpackShadow(DenoisedShadow.SampleLevel(LinearSampler, uv, 0)).x;
            debugOutput = float3(shadowOut, shadowOut, shadowOut);
        }
        useDebugOutput = true;
    }
    // Debug Mode 5: solid magenta (composite sanity check) - already in sRGB
    else if (DebugMode == 5)
    {
        OutputTexture[pixelCoord] = float4(1.0, 0.0, 1.0, 1.0);
        return;
    }
    // Debug Mode 6: Show denoised diffuse only (no albedo, no shadow)
    else if (DebugMode == 6)
    {
        float3 diffOnly = DenoisedDiffuse.SampleLevel(LinearSampler, uv, 0).rgb;
        diffOnly *= ExposureValue;
        debugOutput = ACESFilm(diffOnly);
        useDebugOutput = true;
    }
    // Debug Mode 7: Show diffuse * albedo (no shadow)
    else if (DebugMode == 7)
    {
        float3 diff = DenoisedDiffuse.SampleLevel(LinearSampler, uv, 0).rgb;
        float3 alb = AlbedoTexture.SampleLevel(LinearSampler, uv, 0).rgb;
        float3 result = diff * alb * ExposureValue;
        debugOutput = ACESFilm(result);
        useDebugOutput = true;
    }
    // Debug Mode 8: Show GBuffer_DiffuseIn (raw input before NRD)
    else if (DebugMode == 8)
    {
        float3 rawDiff = GBuffer_DiffuseIn.SampleLevel(LinearSampler, uv, 0).rgb;
        rawDiff *= ExposureValue;
        debugOutput = ACESFilm(rawDiff);
        useDebugOutput = true;
    }
    // Debug Mode 9: Photon contribution (linear)
    else if (DebugMode == 9)
    {
        float3 photonOnly = GBuffer_DiffuseIn.SampleLevel(LinearSampler, uv, 0).rgb;
        photonOnly *= ExposureValue;
        debugOutput = ACESFilm(photonOnly);
        useDebugOutput = true;
    }
    // Debug Mode 10: Photon contribution heatmap
    else if (DebugMode == 10)
    {
        float3 photonOnly = GBuffer_DiffuseIn.SampleLevel(LinearSampler, uv, 0).rgb;
        float intensity = Luminance(photonOnly);
        float mapped = log2(1.0 + intensity * 4.0) / 4.0;
        debugOutput = Heatmap(mapped);
        useDebugOutput = true;
    }
    
    // Unified debug output path - sRGB conversion happens ONLY here
    if (useDebugOutput)
    {
        OutputTexture[pixelCoord] = float4(LinearToSRGB(debugOutput), 1.0);
        return;
    }
    
    // ========================================
    // Debug Mode: Show tiles at bottom of screen
    // Only enabled when DebugMode == 1 explicitly
    // ========================================
    if (DebugMode == 1)
    {
        float tileHeight = OutputSize.y * DebugTileScale;
        float tileWidth = tileHeight; // Square tiles
        float debugAreaY = OutputSize.y - tileHeight - 10; // 10px margin from bottom
        
        // Check if we're in the debug tile area
        if (pixelCoord.y > debugAreaY)
        {
            // Calculate which tile we're in
            float tileX = float(pixelCoord.x) / tileWidth;
            int tileIndex = int(tileX);
            
            // Calculate UV within the tile
            float localX = frac(tileX);
            float localY = (float(pixelCoord.y) - debugAreaY) / tileHeight;
            float2 tileUV = float2(localX, localY);
            
            float3 tileColor = float3(0, 0, 0);
            
            // Tile 0: Input Diffuse (before denoise)
            if (tileIndex == 0)
            {
                tileColor = GBuffer_DiffuseIn.SampleLevel(LinearSampler, tileUV, 0).rgb;
                tileColor = saturate(tileColor); // Clamp for display
            }
            // Tile 1: Input Specular (before denoise)
            else if (tileIndex == 1)
            {
                tileColor = GBuffer_SpecularIn.SampleLevel(LinearSampler, tileUV, 0).rgb;
                tileColor = saturate(tileColor);
            }
            // Tile 2: Denoised Diffuse (output)
            else if (tileIndex == 2)
            {
                tileColor = DenoisedDiffuse.SampleLevel(LinearSampler, tileUV, 0).rgb;
                tileColor = saturate(tileColor);
            }
            // Tile 3: Denoised Specular (output)
            else if (tileIndex == 3)
            {
                tileColor = DenoisedSpecular.SampleLevel(LinearSampler, tileUV, 0).rgb;
                tileColor = saturate(tileColor);
            }
            // Tile 4: Normal + Roughness
            else if (tileIndex == 4)
            {
                float4 nr = GBuffer_NormalRoughness.SampleLevel(LinearSampler, tileUV, 0);
                tileColor = VisualizeNormal(nr);
            }
            // Tile 5: ViewZ (Depth)
            else if (tileIndex == 5)
            {
                float viewZ = GBuffer_ViewZ.SampleLevel(LinearSampler, tileUV, 0);
                tileColor = VisualizeDepth(viewZ);
            }
            // Tile 6: Motion Vectors
            else if (tileIndex == 6)
            {
                float2 mv = GBuffer_MotionVectors.SampleLevel(LinearSampler, tileUV, 0);
                tileColor = VisualizeMotionVectors(mv);
            }
            // Tile 7: Input Shadow (before SIGMA denoising)
            else if (tileIndex == 7)
            {
                float2 shadowIn = GBuffer_ShadowData.SampleLevel(LinearSampler, tileUV, 0);
                // X = penumbra, Y = visibility (0=shadow, 1=lit)
                // Show visibility as grayscale (shadow silhouette)
                tileColor = float3(shadowIn.y, shadowIn.y, shadowIn.y);
            }
            // Tile 8: Denoised Shadow (SIGMA output)
            else if (tileIndex == 8)
            {
                float4 rawSigma = DenoisedShadow.SampleLevel(LinearSampler, tileUV, 0);
                // R = shadow visibility (0=shadow, 1=lit)
                tileColor = float3(rawSigma.x, rawSigma.x, rawSigma.x);
            }
            
            // Draw tile border (1px white line)
            if (localX < 0.01 || localX > 0.99 || localY < 0.01 || localY > 0.99)
            {
                tileColor = float3(1, 1, 1);
            }
            
            // Apply gamma for display
            finalColor = LinearToSRGB(tileColor);
            OutputTexture[pixelCoord] = float4(finalColor, 1.0);
            return;
        }
    }
    
    // ========================================
    // Normal Rendering
    // ========================================
    
    // Read G-Buffer data for mirror bypass decision
    float4 normalRoughnessPacked = GBuffer_NormalRoughness.SampleLevel(LinearSampler, uv, 0);
    float roughness = normalRoughnessPacked.w * normalRoughnessPacked.w;  // sqrt encoding -> linear
    float viewZ = GBuffer_ViewZ.SampleLevel(LinearSampler, uv, 0);
    
    // ========================================
    // NRD REBLUR Compositing
    // ========================================
    // Material type encoded in albedo.alpha:
    //   0.0 = sky/miss - use raw diffuse directly
    //   0.5 = specular-dominant (glass/metal) - BYPASS NRD, use raw buffers
    //   1.0 = diffuse surface - use NRD denoised, remodulate with albedo
    //
    // Specular-dominant materials bypass NRD because:
    //   - Reflections/refractions have incorrect motion vectors
    //   - Temporal filtering causes ghosting and blur on these surfaces
    // ========================================
    
    // Read albedo (use PointSampler to avoid blending alpha at edges)
    float4 albedoData = AlbedoTexture.SampleLevel(PointSampler, uv, 0);
    float3 albedo = albedoData.rgb;
    float materialAlpha = albedoData.a;
    
    // Material classification from alpha
    bool isSky = materialAlpha < 0.25;                    // 0.0
    bool isSpecularDominant = materialAlpha > 0.25 && materialAlpha < 0.75;  // 0.5
    bool isDiffuseSurface = materialAlpha > 0.75;         // 1.0
    
    float3 inputColor;
    
    if (isSky)
    {
        // Sky/miss: use raw diffuse directly (contains sky color)
        inputColor = GBuffer_DiffuseIn.SampleLevel(PointSampler, uv, 0).rgb;
    }
    else if (isSpecularDominant)
    {
        // Glass/Metal: BYPASS NRD completely, use raw specular
        // This avoids temporal filtering artifacts on reflections/refractions
        inputColor = RawSpecularBackup.SampleLevel(PointSampler, uv, 0).rgb;
    }
    else
    {
        // Diffuse surface: Distance-based NRD bypass
        // 
        // Near surfaces (viewZ < threshold): Use NRD denoised (clean shadows)
        // Far surfaces (viewZ >= threshold): Use raw buffers (clean checkerboard)
        // This avoids checkerboard artifacts from NRD temporal filtering at distance
        
        float viewZ = GBuffer_ViewZ.SampleLevel(PointSampler, uv, 0);
        float bypassThreshold = 8.0;  // Distance threshold for NRD bypass
        float blendRange = 2.0;       // Smooth transition range
        
        float3 diffuseDemodRaw = GBuffer_DiffuseIn.SampleLevel(PointSampler, uv, 0).rgb;
        float3 specularRaw = RawSpecularBackup.SampleLevel(PointSampler, uv, 0).rgb;
        float3 rawDiffuse = diffuseDemodRaw * albedo;
        float3 rawColor = rawDiffuse + specularRaw;
        
        if (UseDenoisedShadow != 0 && viewZ < bypassThreshold + blendRange)
        {
            // Use NRD denoised for near surfaces
            float3 diffuseDemodNRD = DenoisedDiffuse.SampleLevel(LinearSampler, uv, 0).rgb;
            float3 specularNRD = DenoisedSpecular.SampleLevel(LinearSampler, uv, 0).rgb;
            float3 nrdDiffuse = diffuseDemodNRD * albedo;
            float3 nrdColor = nrdDiffuse + specularNRD;
            
            // Blend between NRD and raw based on distance
            float blendFactor = saturate((viewZ - bypassThreshold) / blendRange);
            inputColor = lerp(nrdColor, rawColor, blendFactor);
        }
        else
        {
            // Far surfaces or denoiser OFF: use raw
            inputColor = rawColor;
        }
    }
    
    // Apply exposure
    inputColor *= ExposureValue;
    
    // Apply tone mapping based on ToneMapOperator
    float3 tonemapped;
    if (ToneMapOperator < 0.5)
    {
        // 0 = Reinhard
        tonemapped = ReinhardToneMap(inputColor);
    }
    else if (ToneMapOperator < 1.5)
    {
        // 1 = ACES
        tonemapped = ACESFilm(inputColor);
    }
    else
    {
        // 2 = None
        tonemapped = inputColor;
    }
    
    // Apply gamma correction and output
    // Use accurate sRGB curve when gamma is standard (2.2), otherwise use power function
    if (abs(GammaValue - GAMMA_SRGB_STANDARD) < GAMMA_SRGB_TOLERANCE)
    {
        finalColor = LinearToSRGB(saturate(tonemapped));
    }
    else
    {
        finalColor = ApplyGamma(saturate(tonemapped), GammaValue);
    }
    // Photon map usage overlay (top-left bar) - only show when DebugMode is enabled
    if (DebugMode > 0 && MaxPhotons > 0)
    {
        uint barWidth = max(64u, OutputSize.x / 5u);
        uint barHeight = 8u;
        if (pixelCoord.x < barWidth && pixelCoord.y < barHeight)
        {
            float ratio = saturate((float)PhotonMapSize / (float)MaxPhotons);
            uint filled = (uint)round(ratio * barWidth);
            if (pixelCoord.x < filled)
            {
                // Green -> Red as it fills
                finalColor = lerp(float3(0.1, 0.9, 0.1), float3(0.9, 0.1, 0.1), ratio);
            }
            else
            {
                finalColor = float3(0.05, 0.05, 0.05);
            }
        }
    }

    OutputTexture[pixelCoord] = float4(finalColor, 1.0);
}

// ============================================
// Simple Pass-Through for Direct Output
// (Used when denoising is disabled)
// ============================================

[numthreads(8, 8, 1)]
void CSPassThrough(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 pixelCoord = dispatchThreadId.xy;
    
    if (pixelCoord.x >= OutputSize.x || pixelCoord.y >= OutputSize.y)
        return;
    
    float2 uv = (float2(pixelCoord) + 0.5) / float2(OutputSize);
    
    // Just sample diffuse and output directly (for testing)
    float3 color = DenoisedDiffuse.SampleLevel(LinearSampler, uv, 0).rgb;
    
    OutputTexture[pixelCoord] = float4(color, 1.0);
}

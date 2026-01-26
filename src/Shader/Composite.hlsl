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
Texture2D<float4> PreDenoiseColor : register(t11);    // Copy of RayGen output before denoise (true "denoiser off" image)

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

// Forward declarations (HLSL requires functions be declared before use).
float3 LinearToSRGB(float3 color);
float3 ApplyGamma(float3 color, float gamma);

// Composite-local helper: apply exposure + tonemap + gamma to a linear RGB value.
float3 CompositePostProcess(float3 linearColor, float exposure, float toneMapOperator, float gammaValue)
{
    float3 c = linearColor * exposure;

    float3 tonemapped;
    if (toneMapOperator < 0.5)
        tonemapped = ReinhardToneMap(c);
    else if (toneMapOperator < 1.5)
        tonemapped = ACESFilm(c);
    else
        tonemapped = c;

    tonemapped = saturate(tonemapped);

    if (abs(gammaValue - GAMMA_SRGB_STANDARD) < GAMMA_SRGB_TOLERANCE)
        return LinearToSRGB(tonemapped);
    return ApplyGamma(tonemapped, gammaValue);
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

float FilterShadowVisibility3x3(float2 uv, float2 texelSize)
{
    float sum = 0.0;
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float2 offset = float2((float)x, (float)y) * texelSize;
            float2 shadowIn = GBuffer_ShadowData.SampleLevel(PointSampler, uv + offset, 0);
            sum += shadowIn.y;
        }
    }
    return sum * (1.0 / 9.0);
}

float3 FilterLighting3x3(float2 uv, float2 texelSize)
{
    float3 sum = 0.0;
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float2 offset = float2((float)x, (float)y) * texelSize;
            float3 diff = DenoisedDiffuse.SampleLevel(LinearSampler, uv + offset, 0).rgb;
            float3 spec = DenoisedSpecular.SampleLevel(LinearSampler, uv + offset, 0).rgb;
            sum += diff + spec;
        }
    }
    return sum * (1.0 / 9.0);
}

float3 FilterLighting3x3Raw(float2 uv, float2 texelSize)
{
    float3 sum = 0.0;
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float2 offset = float2((float)x, (float)y) * texelSize;
            float3 diff = GBuffer_DiffuseIn.SampleLevel(LinearSampler, uv + offset, 0).rgb;
            float3 spec = RawSpecularBackup.SampleLevel(LinearSampler, uv + offset, 0).rgb;
            sum += diff + spec;
        }
    }
    return sum * (1.0 / 9.0);
}

float ShadowFilterMask(float rawVis)
{
    // 1.0 in shadow, 0.0 in lit area
    return smoothstep(0.0, 0.25, 1.0 - rawVis);
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

    // DebugMode 12: visualize ViewZ (log scale)
    if (DebugMode == 12)
    {
        float vz = GBuffer_ViewZ.SampleLevel(PointSampler, uv, 0);
        float mapped = log2(1.0 + vz) / log2(1.0 + 150.0);
        OutputTexture[pixelCoord] = float4(LinearToSRGB(saturate(mapped).xxx), 1.0);
        return;
    }

    // DebugMode 15/16: ViewZ debugging (FULL-SCREEN).
    // NOTE: These must run regardless of hit/miss, so they live here (before hitMask branches).
    if (DebugMode == 15 || DebugMode == 16)
    {
        float vz = abs(GBuffer_ViewZ.SampleLevel(PointSampler, uv, 0));

        // Use the user-measured reference range for debugging.
        const float zStartDbg = 12.0;
        const float zEndDbg = 500.0;

        if (DebugMode == 15)
        {
            float m = (vz >= zStartDbg && vz <= zEndDbg) ? 1.0 : 0.0;
            OutputTexture[pixelCoord] = float4(0.0, m, 0.0, 1.0);
            return;
        }

        // DebugMode 16: linear-scale ViewZ (clamped to 0..zEndDbg).
        float mapped = saturate(vz / zEndDbg);
        OutputTexture[pixelCoord] = float4(LinearToSRGB(mapped.xxx), 1.0);
        return;
    }

    // DebugMode 13: show the captured pre-denoise color (true "denoiser off" image) full-screen.
    if (DebugMode == 13)
    {
        // IMPORTANT: Use PointSampler to avoid blending across the hit/miss horizon.
        float3 pre = PreDenoiseColor.SampleLevel(PointSampler, uv, 0).rgb;
        float3 outColor = CompositePostProcess(pre, ExposureValue, ToneMapOperator, GammaValue);
        OutputTexture[pixelCoord] = float4(outColor, 1.0);
        return;
    }
    
    // ========================================
    // Sky/Miss pixels: viewZ == 0 means no hit, use raw input directly
    // NRD cannot properly denoise sky pixels, so bypass denoising
    // ========================================
    // Sky bypass disabled - handled by main path below
    // (GBuffer_DiffuseIn now contains the complete finalColor from RayGen for all pixels)
    
    // Primary lighting inputs:
    // Use REBLUR outputs when denoiser is enabled, otherwise fall back to raw buffers.
    // Albedo/alpha is a G-Buffer attribute, not a filtered texture.
    // Using LinearSampler here blends hit/miss alpha across the horizon and creates bright fringes.
    // Always sample it with PointSampler.
    float4 albedoSample = AlbedoTexture.SampleLevel(PointSampler, uv, 0);
    float3 albedo = albedoSample.rgb;
    // Alpha encoding from RayGen:
    //   0.0 = miss/sky
    //   1.0 = hit, diffuse is DEMODULATED and needs re-modulation by albedo
    //   0.5 = hit, diffuse is residual and must NOT be re-modulated (metal/glass)
    float hitMask = (albedoSample.a > 0.25) ? 1.0 : 0.0;
    float remodulateMask = (albedoSample.a > 0.75) ? 1.0 : 0.0;
    // Specular-dominant surfaces (metal/glass) can't provide correct motion vectors for the refracted/reflected
    // signal (MV is based on the primary hit). NRD temporal filtering will therefore smear/distort the image
    // seen through refraction. For these pixels, bypass NRD specular and use the raw pre-NRD buffer.
    bool bypassSpecularDenoise = (hitMask > 0.5) && (remodulateMask < 0.5);

    // True pre-denoise final color (RayGen output copy). Use PointSampler to avoid horizon blending.
    float3 preDenoise = PreDenoiseColor.SampleLevel(PointSampler, uv, 0).rgb;
    
    // Diffuse buffers are stored DEMODULATED (diffuse / albedo) for hit pixels.
    // Re-modulate here to preserve high-frequency textures (checkerboard, etc.).
    // IMPORTANT: Use PointSampler for 1:1 full-res buffers to avoid horizon fringes.
    float3 rawDiffuseDemod = GBuffer_DiffuseIn.SampleLevel(PointSampler, uv, 0).rgb;
    float3 rawSpecular = RawSpecularBackup.SampleLevel(PointSampler, uv, 0).rgb;
    float3 rawDiffuse = rawDiffuseDemod * lerp(1.0.xxx, albedo, hitMask * remodulateMask);
    float3 rawLighting = rawDiffuse + rawSpecular;

    float3 denoisedDiffuseDemod = DenoisedDiffuse.SampleLevel(LinearSampler, uv, 0).rgb;
    float3 denoisedSpecular = DenoisedSpecular.SampleLevel(LinearSampler, uv, 0).rgb;
    if (bypassSpecularDenoise)
    {
        denoisedSpecular = rawSpecular;
    }
    float3 denoisedDiffuse = denoisedDiffuseDemod * lerp(1.0.xxx, albedo, hitMask * remodulateMask);
    float3 denoisedLighting = denoisedDiffuse + denoisedSpecular;

    // Sky/miss pixels: NEVER use NRD outputs.
    // NRD temporal filtering can leak geometry history into sky and create a bright/white horizon seam.
    // For miss pixels (albedo alpha == 0), always use raw lighting.
    float3 inputColor;
    if (hitMask < 0.5)
    {
        // Miss/sky: prefer the exact RayGen output to avoid any reconstruction mismatch near the horizon.
        inputColor = preDenoise;
    }
    else
    {
        // At the hit/miss boundary (horizon), NRD can still produce bright fringing on the HIT side
        // due to history clamping and neighborhood sampling across discontinuities. Detect if any
        // immediate neighbor is a miss pixel and, if so, prefer raw for this pixel too.
        bool boundaryToSky = false;
        float2 texel = 1.0 / float2(OutputSize);
        [unroll]
        for (int oy = -3; oy <= 3; oy++)
        {
            [unroll]
            for (int ox = -3; ox <= 3; ox++)
            {
                float a = AlbedoTexture.SampleLevel(PointSampler, uv + float2(ox, oy) * texel, 0).a;
                if (a < 0.25)
                {
                    boundaryToSky = true;
                }
            }
        }

        if (boundaryToSky)
        {
            // Horizon seam fix: force exact RayGen output on the HIT side near sky/miss neighbors.
            // This matches the denoiser-off image and eliminates the 1px white fringe on the ground.
            inputColor = preDenoise;
        }
        else
        {
            inputColor = (UseDenoisedShadow != 0) ? denoisedLighting : rawLighting;
        }
    }

    // ------------------------------------------------------------------
    // Far-field fallback to RAW (pre-denoise) for aliasing (VIEWZ-based).
    // You already measured that typical ground ViewZ spans roughly ~12..500 in this scene.
    // Use that same metric here so the "switch range" doesn't drift.
    // ------------------------------------------------------------------
    if (hitMask > 0.5 && (UseDenoisedShadow != 0))
    {
        float vz = abs(GBuffer_ViewZ.SampleLevel(PointSampler, uv, 0));

        // Tune these in VIEWZ units (same units as GBuffer_ViewZ).
        float zStart = 12.0;
        float zEnd = 500.0;

        // Use the SAME condition as DebugMode 15 (Mode 9): ViewZ in [12..500] is the desired override band.
        // This avoids any mismatch caused by curve shaping / thresholds.
        float rawT = (vz >= zStart && vz <= zEnd) ? 1.0 : 0.0;

        // DebugMode 11: visualize raw fallback blend factor (0=denoised, 1=raw)
        if (DebugMode == 11)
        {
            OutputTexture[pixelCoord] = float4(LinearToSRGB(rawT.xxx), 1.0);
            return;
        }

        // DebugMode 14: visualize which pixels would switch to PreDenoiseColor.
        if (DebugMode == 14)
        {
            float m = rawT;
            OutputTexture[pixelCoord] = float4(0.0, m, 0.0, 1.0);
            return;
        }

        // Hard cut to RAW in the far field (user-requested behavior).
        if (rawT > 0.5)
            inputColor = PreDenoiseColor.SampleLevel(PointSampler, uv, 0).rgb;
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

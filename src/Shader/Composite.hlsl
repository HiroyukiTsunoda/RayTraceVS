// Composite Shader
// Combines denoised diffuse and specular radiance into final output
// This is used after NRD denoising to produce the final image
// Now also supports SIGMA-denoised shadows

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

    // Debug Mode 2: full-screen input shadow visibility
    if (DebugMode == 2)
    {
        float2 shadowIn = GBuffer_ShadowData.SampleLevel(LinearSampler, uv, 0);
        float shadowVis = shadowIn.y;  // Y = visibility (0 = shadow, 1 = lit)
        finalColor = float3(shadowVis, shadowVis, shadowVis);
        OutputTexture[pixelCoord] = float4(LinearToSRGB(finalColor), 1.0);
        return;
    }
    // Debug Mode 3: full-screen denoised shadow visibility
    if (DebugMode == 3)
    {
        float shadowOut = SIGMA_BackEnd_UnpackShadow(DenoisedShadow.SampleLevel(LinearSampler, uv, 0)).x;
        finalColor = float3(shadowOut, shadowOut, shadowOut);
        OutputTexture[pixelCoord] = float4(LinearToSRGB(finalColor), 1.0);
        return;
    }
    // Debug Mode 4: split-screen input vs denoised shadow
    if (DebugMode == 4)
    {
        if (uv.x < 0.5)
        {
            float2 shadowIn = GBuffer_ShadowData.SampleLevel(LinearSampler, uv, 0);
            float shadowVis = shadowIn.y;
            finalColor = float3(shadowVis, shadowVis, shadowVis);
        }
        else
        {
            float shadowOut = SIGMA_BackEnd_UnpackShadow(DenoisedShadow.SampleLevel(LinearSampler, uv, 0)).x;
            finalColor = float3(shadowOut, shadowOut, shadowOut);
        }
        OutputTexture[pixelCoord] = float4(LinearToSRGB(finalColor), 1.0);
        return;
    }
    // Debug Mode 5: solid magenta (composite sanity check)
    if (DebugMode == 5)
    {
        OutputTexture[pixelCoord] = float4(1.0, 0.0, 1.0, 1.0);
        return;
    }
    // Debug Mode 6: Show denoised diffuse only (no albedo, no shadow)
    if (DebugMode == 6)
    {
        float3 diffOnly = DenoisedDiffuse.SampleLevel(LinearSampler, uv, 0).rgb;
        // Apply exposure and tone mapping for visibility
        diffOnly *= ExposureValue;
        float3 tonemapped = ACESFilm(diffOnly);
        OutputTexture[pixelCoord] = float4(LinearToSRGB(tonemapped), 1.0);
        return;
    }
    // Debug Mode 7: Show diffuse * albedo (no shadow)
    if (DebugMode == 7)
    {
        float3 diff = DenoisedDiffuse.SampleLevel(LinearSampler, uv, 0).rgb;
        float3 alb = AlbedoTexture.SampleLevel(LinearSampler, uv, 0).rgb;
        float3 result = diff * alb * ExposureValue;
        float3 tonemapped = ACESFilm(result);
        OutputTexture[pixelCoord] = float4(LinearToSRGB(tonemapped), 1.0);
        return;
    }
    // Debug Mode 8: Show GBuffer_DiffuseIn (raw input before NRD)
    if (DebugMode == 8)
    {
        float3 rawDiff = GBuffer_DiffuseIn.SampleLevel(LinearSampler, uv, 0).rgb;
        rawDiff *= ExposureValue;
        float3 tonemapped = ACESFilm(rawDiff);
        OutputTexture[pixelCoord] = float4(LinearToSRGB(tonemapped), 1.0);
        return;
    }
    // Debug Mode 9: Photon contribution (linear)
    if (DebugMode == 9)
    {
        float3 photonOnly = GBuffer_DiffuseIn.SampleLevel(LinearSampler, uv, 0).rgb;
        photonOnly *= ExposureValue;
        float3 tonemapped = ACESFilm(photonOnly);
        OutputTexture[pixelCoord] = float4(LinearToSRGB(tonemapped), 1.0);
        return;
    }
    // Debug Mode 10: Photon contribution heatmap
    if (DebugMode == 10)
    {
        float3 photonOnly = GBuffer_DiffuseIn.SampleLevel(LinearSampler, uv, 0).rgb;
        float intensity = Luminance(photonOnly);
        float mapped = log2(1.0 + intensity * 4.0) / 4.0;
        float3 heat = Heatmap(mapped);
        OutputTexture[pixelCoord] = float4(LinearToSRGB(heat), 1.0);
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
    // Sky/Miss pixels: viewZ == 0 means no hit, use raw input directly
    // NRD cannot properly denoise sky pixels, so bypass denoising
    // ========================================
    // Sky bypass disabled - handled by main path below
    // (GBuffer_DiffuseIn now contains the complete finalColor from RayGen for all pixels)
    
    // Correct SIGMA workflow:
    // GBuffer_DiffuseIn = diffuse radiance (ambient + directDiffuse + reflections + emission)
    // RawSpecularBackup = specular radiance (directSpecular)
    // Combine both for complete lighting
    
    float3 inputColor = GBuffer_DiffuseIn.SampleLevel(LinearSampler, uv, 0).rgb;
    float3 inputSpecular = RawSpecularBackup.SampleLevel(LinearSampler, uv, 0).rgb;
    inputColor += inputSpecular;  // Add specular component for point light highlights
    
    // For hit pixels, apply SIGMA shadow
    // Metal/Glass set shadowVisibility=1.0 in their G-Buffer output, so SIGMA won't darken them
    // Sky pixels have albedo.a == 0, so we skip shadow application for them
    float4 albedoData = AlbedoTexture.SampleLevel(LinearSampler, uv, 0);
    bool isHitPixel = albedoData.a > 0.5;  // Alpha = 1.0 for hits, 0.0 for misses
    
    // Shadow is now baked into diffuseRadiance in ClosestHit (same as denoiser-disabled path)
    // No need to apply shadow here - this avoids white artifacts at object edges
    // UseDenoisedShadow flag is ignored since shadow is already applied
    
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
    // Use custom gamma if not 2.2, otherwise use accurate sRGB curve
    if (abs(GammaValue - 2.2) < 0.01)
    {
        finalColor = LinearToSRGB(saturate(tonemapped));
    }
    else
    {
        finalColor = ApplyGamma(saturate(tonemapped), GammaValue);
    }
    // Photon map usage overlay (top-left bar)
    if (MaxPhotons > 0)
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

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

// Sampler
SamplerState LinearSampler : register(s0);

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

// Gamma correction
float3 LinearToSRGB(float3 color)
{
    float3 srgb;
    srgb = color < 0.0031308 
        ? 12.92 * color 
        : 1.055 * pow(color, 1.0 / 2.4) - 0.055;
    return srgb;
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

    // Debug Mode 2: full-screen input shadow visibility (Y channel)
    if (DebugMode == 2)
    {
        float2 shadowIn = GBuffer_ShadowData.SampleLevel(LinearSampler, uv, 0);
        float shadowVis = shadowIn.y;  // Y = visibility (0 = shadow, 1 = lit)
        finalColor = float3(shadowVis, shadowVis, shadowVis);
        OutputTexture[pixelCoord] = float4(LinearToSRGB(finalColor), 1.0);
        return;
    }
    // Debug Mode 6: Show raw penumbra (X channel) - should be occluder info
    if (DebugMode == 6)
    {
        float2 shadowIn = GBuffer_ShadowData.SampleLevel(LinearSampler, uv, 0);
        float penumbra = shadowIn.x;  // X = penumbra (packed)
        finalColor = float3(penumbra, penumbra, penumbra);
        OutputTexture[pixelCoord] = float4(finalColor, 1.0);
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
    
    // ========================================
    // Debug Mode: Show tiles at bottom of screen
    // ========================================
    if (DebugMode > 0)
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
                tileColor = float3(shadowIn.y, shadowIn.y, shadowIn.y);
            }
            // Tile 8: Denoised Shadow (SIGMA output)
            else if (tileIndex == 8)
            {
                // Show the same visibility used in final composite
                float shadowOut = SIGMA_BackEnd_UnpackShadow(DenoisedShadow.SampleLevel(LinearSampler, tileUV, 0)).x;
                float visibility = 1.0 - saturate(shadowOut); // invert if output is occlusion-like
                tileColor = float3(visibility, visibility, visibility);
            }
            // Tile 9: Albedo texture
            else if (tileIndex == 9)
            {
                tileColor = AlbedoTexture.SampleLevel(LinearSampler, tileUV, 0).rgb;
            }
            // Tile 10: Direct lighting (diffuse * albedo, no shadow)
            else if (tileIndex == 10)
            {
                float3 diff = DenoisedDiffuse.SampleLevel(LinearSampler, tileUV, 0).rgb;
                float3 alb = AlbedoTexture.SampleLevel(LinearSampler, tileUV, 0).rgb;
                tileColor = saturate(diff * alb);
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
    
    // GBuffer_DiffuseIn contains the ray traced scene
    // For diffuse: unshadowed lighting * albedo + specular
    // For metal/glass: full reflection/refraction result
    float3 sceneColor = GBuffer_DiffuseIn.SampleLevel(LinearSampler, uv, 0).rgb;
    
    // ========================================
    // Shadow Application (SIGMA denoised)
    // ========================================
    // IMPORTANT: SIGMA outputs OCCLUSION (0=no occlusion, 1=full occlusion)
    // We need TRANSMITTANCE for compositing (1=lit, 0=shadow)
    
    float transmittance = 1.0;  // Default: fully lit (no shadow)
    
    if (UseDenoisedShadow > 0)
    {
        // Step 1: Get SIGMA output (occlusion) and CLAMP to 0-1
        // SIGMA can overshoot causing white halos if not clamped!
        float occlusion = SIGMA_BackEnd_UnpackShadow(DenoisedShadow.SampleLevel(LinearSampler, uv, 0)).x;
        occlusion = saturate(occlusion);  // CRITICAL: prevent overshoot
        
        // Step 2: Convert occlusion to transmittance
        transmittance = saturate(1.0 - occlusion);  // Double saturate for safety
    }
    
    // Step 3: Apply shadow directly (no ambient floor for now)
    float3 finalRadiance = sceneColor * transmittance;
    
    // Apply exposure
    finalRadiance *= ExposureValue;
    
    // Tone mapping
    float3 tonemapped;
    if (ToneMapOperator < 0.5)
    {
        // Reinhard
        tonemapped = ReinhardToneMap(finalRadiance);
    }
    else if (ToneMapOperator < 1.5)
    {
        // ACES
        tonemapped = ACESFilm(finalRadiance);
    }
    else
    {
        // No tone mapping
        tonemapped = saturate(finalRadiance);
    }
    
    // Gamma correction (linear to sRGB)
    finalColor = LinearToSRGB(tonemapped);
    
    // Write output
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

// Custom Shadow Denoiser
// Spatial filtering that respects object boundaries using Object ID buffer
// This avoids the white artifact issue that SIGMA has at object edges

// Input textures
Texture2D<float2> InputShadow : register(t0);      // X = penumbra, Y = visibility
Texture2D<uint> ObjectID : register(t1);            // Packed object ID (type << 24 | index)
Texture2D<float> ViewZ : register(t2);              // Linear view depth
Texture2D<float4> NormalRoughness : register(t3);   // Packed normal + roughness

// Output texture
RWTexture2D<float2> OutputShadow : register(u0);    // Denoised shadow

// Samplers
SamplerState PointSampler : register(s0);

// Constants
cbuffer DenoiseConstants : register(b0)
{
    uint2 ScreenSize;
    float DepthThreshold;      // Depth difference threshold for edge detection
    float NormalThreshold;     // Normal difference threshold for edge detection
    uint FilterRadius;         // Filter kernel radius (1-3)
    float ShadowSoftness;      // Controls blur amount (0-1)
    float2 Padding;
};

// Decode normal from packed format (simplified - assumes octahedron encoding)
float3 DecodeNormal(float4 packed)
{
    float2 p = packed.xy * 2.0 - 1.0;
    float3 n = float3(p.xy, 1.0 - abs(p.x) - abs(p.y));
    float t = saturate(-n.z);
    n.x += (n.x >= 0.0) ? -t : t;
    n.y += (n.y >= 0.0) ? -t : t;
    return normalize(n);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 pixelCoord = dispatchThreadId.xy;
    
    // Bounds check
    if (pixelCoord.x >= ScreenSize.x || pixelCoord.y >= ScreenSize.y)
        return;
    
    // Get center pixel data
    float2 centerShadow = InputShadow[pixelCoord];
    uint centerObjectID = ObjectID[pixelCoord];
    float centerDepth = ViewZ[pixelCoord];
    float4 centerNormalPacked = NormalRoughness[pixelCoord];
    float3 centerNormal = DecodeNormal(centerNormalPacked);
    
    // Skip sky pixels (no object hit)
    if (centerObjectID == 0xFFFFFFFF)
    {
        OutputShadow[pixelCoord] = centerShadow;
        return;
    }
    
    // Accumulate weighted samples
    float weightSum = 0.0;
    float visibilitySum = 0.0;
    float penumbraSum = 0.0;
    
    // Fixed kernel size based on FilterRadius
    int radius = (int)FilterRadius;
    
    for (int dy = -radius; dy <= radius; dy++)
    {
        for (int dx = -radius; dx <= radius; dx++)
        {
            int2 sampleCoord = int2(pixelCoord) + int2(dx, dy);
            
            // Bounds check for sample
            if (sampleCoord.x < 0 || sampleCoord.x >= (int)ScreenSize.x ||
                sampleCoord.y < 0 || sampleCoord.y >= (int)ScreenSize.y)
                continue;
            
            uint2 sampleCoordU = uint2(sampleCoord);
            
            // Get sample data
            float2 sampleShadow = InputShadow[sampleCoordU];
            uint sampleObjectID = ObjectID[sampleCoordU];
            float sampleDepth = ViewZ[sampleCoordU];
            float4 sampleNormalPacked = NormalRoughness[sampleCoordU];
            float3 sampleNormal = DecodeNormal(sampleNormalPacked);
            
            // === Edge-stopping weights ===
            
            // 1. Object ID must match exactly (most important!)
            if (sampleObjectID != centerObjectID)
                continue;
            
            // 2. Depth similarity weight
            float depthDiff = abs(centerDepth - sampleDepth);
            float depthWeight = exp(-depthDiff / max(DepthThreshold * centerDepth, 0.001));
            
            // 3. Normal similarity weight
            float normalDot = max(0.0, dot(centerNormal, sampleNormal));
            float normalWeight = pow(normalDot, 8.0); // Sharp falloff for different normals
            
            // 4. Spatial weight (Gaussian-like)
            float dist = sqrt(float(dx * dx + dy * dy));
            float spatialWeight = exp(-dist * dist / (2.0 * ShadowSoftness * ShadowSoftness + 0.01));
            
            // Combined weight
            float weight = depthWeight * normalWeight * spatialWeight;
            
            // Accumulate
            visibilitySum += sampleShadow.y * weight;
            penumbraSum += sampleShadow.x * weight;
            weightSum += weight;
        }
    }
    
    // Normalize and output
    if (weightSum > 0.001)
    {
        float2 denoisedShadow;
        denoisedShadow.x = penumbraSum / weightSum;
        denoisedShadow.y = visibilitySum / weightSum;
        OutputShadow[pixelCoord] = denoisedShadow;
    }
    else
    {
        // Fallback to center pixel if no valid samples
        OutputShadow[pixelCoord] = centerShadow;
    }
}

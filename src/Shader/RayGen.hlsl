// Full RayGen shader with multi-sampling and DoF
#include "Common.hlsli"

// Simple hash function for random numbers
float Hash(uint seed)
{
    seed = (seed ^ 61) ^ (seed >> 16);
    seed *= 9;
    seed = seed ^ (seed >> 4);
    seed *= 0x27d4eb2d;
    seed = seed ^ (seed >> 15);
    return float(seed) / 4294967295.0;
}

// Generate random offset for anti-aliasing
float2 RandomInPixel(uint2 pixel, uint sampleIndex)
{
    uint seed = pixel.x * 1973 + pixel.y * 9277 + sampleIndex * 26699;
    float x = Hash(seed);
    float y = Hash(seed + 1);
    return float2(x, y);
}

// Generate random point on disk for DoF
float2 RandomOnDisk(uint2 pixel, uint sampleIndex)
{
    uint seed = pixel.x * 7919 + pixel.y * 6271 + sampleIndex * 1009;
    float r = sqrt(Hash(seed));
    float theta = Hash(seed + 1) * 6.28318530718;
    return float2(r * cos(theta), r * sin(theta));
}

[shader("raygeneration")]
void RayGen()
{
    // ピクセルインデックス取得
    uint2 launchIndex = DispatchRaysIndex().xy;
    uint2 launchDim = DispatchRaysDimensions().xy;
    
    // カメラ情報をシーン定数バッファから取得
    float3 cameraPos = Scene.CameraPosition;
    float3 cameraForward = Scene.CameraForward;
    float3 cameraRight = Scene.CameraRight;
    float3 cameraUp = Scene.CameraUp;
    float aspectRatio = Scene.AspectRatio;
    float tanHalfFov = Scene.TanHalfFov;
    
    // DoFパラメータ
    float apertureSize = Scene.ApertureSize;
    float focusDistance = Scene.FocusDistance;
    bool dofEnabled = apertureSize > 0.001;
    
    // サンプル数を取得（最小1、最大64）
    uint sampleCount = clamp(Scene.SamplesPerPixel, 1, 64);
    
    // 累積カラーとNRDデータ
    float3 accumulatedColor = float3(0, 0, 0);
    float3 accumulatedDiffuse = float3(0, 0, 0);
    float3 accumulatedSpecular = float3(0, 0, 0);
    float accumulatedHitDist = 0.0;
    float3 primaryNormal = float3(0, 1, 0);
    float primaryRoughness = 1.0;
    float3 primaryPosition = float3(0, 0, 0);
    float primaryViewZ = 10000.0;
    float primaryMetallic = 0.0;
    float3 primaryAlbedo = float3(0, 0, 0);
    bool anyHit = false;
    
    // SIGMA expects RAW single-sample shadow data, NOT averaged!
    // Store primary sample shadow for SIGMA (no averaging)
    float primaryShadowVisibility = 1.0;
    float primaryShadowPenumbra = 0.0;
    float primaryShadowDistance = NRD_FP16_MAX;
    
    // For display/fallback only (averaged)
    float accumulatedShadowVisibility = 0.0;
    float accumulatedShadowPenumbra = 0.0;
    float minShadowDistance = NRD_FP16_MAX;
    int occludedSampleCount = 0;
    
    // Get max bounces from scene settings
    uint maxBounces = (Scene.MaxBounces > 0) ? min(Scene.MaxBounces, 32) : 8;
    
    for (uint s = 0; s < sampleCount; s++)
    {
        // ピクセル内のランダムオフセット（アンチエイリアシング）
        float2 offset = (sampleCount > 1) ? RandomInPixel(launchIndex, s) : float2(0.5, 0.5);
        
        // NDC座標計算（-1 to 1）
        float2 pixelCenter = (float2)launchIndex + offset;
        float2 ndc = pixelCenter / (float2)launchDim * 2.0 - 1.0;
        ndc.y = -ndc.y; // Y軸反転
        
        // レイ方向を計算（カメラ基底ベクトルを使用）
        float3 rayDir = cameraForward 
                      + cameraRight * (ndc.x * tanHalfFov * aspectRatio)
                      + cameraUp * (ndc.y * tanHalfFov);
        rayDir = normalize(rayDir);
        
        // レイの原点とフォーカス点
        float3 rayOrigin = cameraPos;
        
        // DoF: レンズのアパーチャをシミュレート
        if (dofEnabled)
        {
            // フォーカス平面上の点を計算
            float3 focusPoint = cameraPos + rayDir * focusDistance;
            
            // アパーチャ上のランダムな点
            float2 diskOffset = RandomOnDisk(launchIndex, s) * apertureSize;
            rayOrigin = cameraPos + cameraRight * diskOffset.x + cameraUp * diskOffset.y;
            
            // 新しいレイ方向（フォーカス点に向かう）
            rayDir = normalize(focusPoint - rayOrigin);
        }
        
        // ============================================
        // Loop-based ray tracing (no recursion in ClosestHit)
        // ============================================
        float3 throughput = float3(1, 1, 1);  // Accumulated color attenuation
        float3 sampleColor = float3(0, 0, 0); // Final color for this sample
        float rayTMin = 0.001;
        
        // Store primary ray NRD data
        bool primaryHitRecorded = false;
        
        for (uint bounce = 0; bounce < maxBounces; bounce++)
        {
            // レイディスクリプタ
            RayDesc ray;
            ray.Origin = rayOrigin;
            ray.Direction = rayDir;
            ray.TMin = rayTMin;
            ray.TMax = 10000.0;
            
            // ペイロード初期化
            RayPayload payload;
            payload.color = float3(0, 0, 0);
            payload.depth = bounce;  // Use bounce count as depth
            payload.hit = 0;
            payload.padding = 0.0;
            // Initialize NRD fields
            payload.diffuseRadiance = float3(0, 0, 0);
            payload.specularRadiance = float3(0, 0, 0);
            payload.hitDistance = 10000.0;
            payload.worldNormal = float3(0, 1, 0);
            payload.roughness = 1.0;
            payload.worldPosition = float3(0, 0, 0);
            payload.viewZ = 10000.0;
            payload.metallic = 0.0;
            payload.albedo = float3(0, 0, 0);
            payload.shadowVisibility = 1.0;
            payload.shadowPenumbra = 0.0;
            payload.shadowDistance = NRD_FP16_MAX;
            payload.targetObjectType = 0;
            payload.targetObjectIndex = 0;
            payload.thicknessQuery = 0;
            payload.shadowColorAccum = float3(1, 1, 1);
            payload.shadowTransmissionAccum = 1.0;
            // Initialize loop-based fields
            payload.nextRayOrigin = float3(0, 0, 0);
            payload.nextRayDirection = float3(0, 0, 0);
            payload.throughput = float3(1, 1, 1);
            payload.continueTrace = 0.0;
            payload.nextRayTMin = 0.001;
            payload.throughputPad = 0.0;
            
            // レイトレーシング実行
            TraceRay(
                SceneBVH,                           // アクセラレーション構造
                RAY_FLAG_NONE,                      // レイフラグ
                0xFF,                               // インスタンスマスク
                0,                                  // RayContributionToHitGroupIndex
                0,                                  // MultiplierForGeometryContributionToHitGroupIndex
                0,                                  // MissShaderIndex
                ray,                                // レイ
                payload                             // ペイロード
            );
            
            // Accumulate color with throughput
            sampleColor += throughput * payload.color;
            
            // Record primary ray NRD data (first bounce only, first hit)
            if (bounce == 0 && payload.hit && !primaryHitRecorded)
            {
                accumulatedDiffuse += payload.diffuseRadiance;
                accumulatedSpecular += payload.specularRadiance;
                accumulatedHitDist += payload.hitDistance;
                accumulatedShadowVisibility += payload.shadowVisibility;
                accumulatedShadowPenumbra += payload.shadowPenumbra;
                if (payload.shadowDistance < NRD_FP16_MAX)
                {
                    occludedSampleCount++;
                    minShadowDistance = min(minShadowDistance, payload.shadowDistance);
                }
                
                // Record first hit data for NRD
                if (!anyHit)
                {
                    primaryNormal = payload.worldNormal;
                    primaryRoughness = payload.roughness;
                    primaryPosition = payload.worldPosition;
                    primaryViewZ = payload.viewZ;
                    primaryMetallic = payload.metallic;
                    primaryAlbedo = payload.albedo;
                    
                    // SIGMA: Store raw shadow from PRIMARY sample only (no averaging!)
                    primaryShadowVisibility = payload.shadowVisibility;
                    primaryShadowPenumbra = payload.shadowPenumbra;
                    primaryShadowDistance = payload.shadowDistance;
                    
                    anyHit = true;
                }
                primaryHitRecorded = true;
            }
            
            // Check if we should continue tracing
            if (payload.continueTrace < 0.5)
            {
                // Miss or terminal hit - add sky color if miss
                if (!payload.hit)
                {
                    sampleColor += throughput * GetSkyColor(rayDir);
                }
                break;
            }
            
            // Update throughput for next bounce
            throughput *= payload.throughput;
            
            // Russian roulette for path termination (after a few bounces)
            if (bounce > 3)
            {
                float maxThroughput = max(throughput.r, max(throughput.g, throughput.b));
                if (maxThroughput < 0.1)
                {
                    // Use pixel-based random for Russian roulette
                    uint rrSeed = launchIndex.x * 1973 + launchIndex.y * 9277 + s * 26699 + bounce * 12345;
                    float rrRand = Hash(rrSeed);
                    if (rrRand > maxThroughput * 10.0)
                    {
                        break;  // Terminate path
                    }
                    // Boost throughput to maintain unbiased result
                    throughput /= maxThroughput * 10.0;
                }
            }
            
            // Setup next ray from payload
            rayOrigin = payload.nextRayOrigin;
            rayDir = payload.nextRayDirection;
            rayTMin = payload.nextRayTMin;
        }
        
        accumulatedColor += sampleColor;
    }
    
    // 平均を取って結果を出力
    float invSampleCount = 1.0 / float(sampleCount);
    float3 finalColor = accumulatedColor * invSampleCount;
    RenderTarget[launchIndex] = float4(finalColor, 1.0);
    
    // Output NRD G-Buffer data for SIGMA workflow
    // diffuseRadiance = lighting WITHOUT shadow (albedo handling depends on material shader)
    // Shadow will be applied from SIGMA denoiser in Composite.hlsl
    float3 diffuseForNRD = accumulatedDiffuse * invSampleCount;
    GBuffer_DiffuseRadianceHitDist[launchIndex] = float4(diffuseForNRD, accumulatedHitDist * invSampleCount);
    GBuffer_SpecularRadianceHitDist[launchIndex] = float4(accumulatedSpecular * invSampleCount, accumulatedHitDist * invSampleCount);
    
    // For primary normal/roughness/albedo, use hit data if available, else defaults
    float3 worldNormal = anyHit ? primaryNormal : float3(0, 1, 0);
    float outRoughness = anyHit ? primaryRoughness : 1.0;
    float3 outAlbedo = anyHit ? primaryAlbedo : float3(1.0, 1.0, 1.0);
    
    // Convert world space normal to view space for NRD
    // View space: X=right, Y=up, Z=forward (into screen)
    float3 viewSpaceNormal;
    viewSpaceNormal.x = dot(worldNormal, cameraRight);
    viewSpaceNormal.y = dot(worldNormal, cameraUp);
    viewSpaceNormal.z = dot(worldNormal, cameraForward);
    viewSpaceNormal = normalize(viewSpaceNormal);
    
    // Calculate correct linear view depth (ViewZ)
    // NRD/SIGMA expects POSITIVE view depth (distance along camera forward)
    // IMPORTANT: For miss pixels, use a large value (not 0) so SIGMA can properly
    // handle edge filtering. Zero viewZ causes discontinuities that confuse SIGMA.
    float outViewZ;
    float outAlbedoAlpha;  // 1.0 for hits, 0.0 for misses (used in Composite for sky detection)
    if (anyHit)
    {
        float3 hitOffset = primaryPosition - cameraPos;
        outViewZ = dot(hitOffset, cameraForward);  // Positive distance along forward
        outViewZ = max(outViewZ, 0.01);  // Minimum positive depth (avoid zero)
        outAlbedoAlpha = 1.0;
    }
    else
    {
        // Use large depth for misses - this helps SIGMA handle edges properly
        // by avoiding sharp depth discontinuities (0 vs large value)
        outViewZ = 10000.0;  // Far distance for sky/miss pixels
        outAlbedoAlpha = 0.0;  // Mark as miss for Composite shader
    }
    
    // Pack normal and roughness for NRD (using view space normal)
    GBuffer_NormalRoughness[launchIndex] = NRD_FrontEnd_PackNormalAndRoughness(viewSpaceNormal, outRoughness);
    GBuffer_ViewZ[launchIndex] = outViewZ;
    GBuffer_Albedo[launchIndex] = float4(outAlbedo, outAlbedoAlpha);
    
    // SIGMA shadow inputs - Use RAW PRIMARY SAMPLE data, NOT averaged!
    // SIGMA expects noisy single-sample input for temporal reconstruction
    // Averaging before SIGMA destroys the temporal coherence SIGMA needs
    
    // SIGMA penumbra encoding:
    // - NRD_FP16_MAX (65504) = fully lit (no shadow, no occluder)
    // - Small value (0.1 to ~100) = shadow with penumbra size in world units
    // IMPORTANT: SIGMA expects penumbra to be much smaller than NRD_FP16_MAX
    // Values > ~1000 are treated as "almost no shadow"
    float sigmaPenumbra;
    if (primaryShadowVisibility > 0.99)
    {
        sigmaPenumbra = NRD_FP16_MAX;  // Fully lit - no shadow
    }
    else
    {
        // Shadow area - clamp penumbra to reasonable range for SIGMA
        // primaryShadowPenumbra can be very large due to light size calculations
        // Clamp to max 100 to ensure SIGMA recognizes it as shadow
        sigmaPenumbra = clamp(primaryShadowPenumbra, 0.1, 100.0);
    }
    
    // Sanitize shadow values before SIGMA - NaN/Inf or out-of-range values cause white sparkles
    primaryShadowVisibility = saturate(primaryShadowVisibility);
    primaryShadowVisibility = isfinite(primaryShadowVisibility) ? primaryShadowVisibility : 1.0;
    sigmaPenumbra = isfinite(sigmaPenumbra) ? sigmaPenumbra : NRD_FP16_MAX;
    
    GBuffer_ShadowData[launchIndex] = float2(sigmaPenumbra, primaryShadowVisibility);
    
    // Pack translucency using NRD format:
    // X = 1.0 if no hit (lit), 0.0 if hit (shadow) - this is the binary signal
    // SIGMA uses this combined with penumbra for temporal reconstruction
    float4 packedTranslucency = SIGMA_FrontEnd_PackTranslucency(
        primaryShadowDistance,  // distanceToOccluder (NRD_FP16_MAX if no hit)
        float3(0, 0, 0)         // translucency (opaque shadow)
    );
    GBuffer_ShadowTranslucency[launchIndex] = packedTranslucency;
    
    // Motion vectors: screen-space pixel delta (current - previous)
    float2 motion = float2(0, 0);
    if (anyHit)
    {
        float4 currClip = mul(float4(primaryPosition, 1.0), Scene.ViewProjection);
        float4 prevClip = mul(float4(primaryPosition, 1.0), Scene.PrevViewProjection);
        float2 currNdc = currClip.xy / currClip.w;
        float2 prevNdc = prevClip.xy / prevClip.w;
        float2 ndcDelta = currNdc - prevNdc;
        // NDC -1..1 to pixel delta (scaled down to avoid overshoot)
        float2 pixelScale = float2(Scene.ScreenWidth, Scene.ScreenHeight) * 0.5;
        motion = ndcDelta * pixelScale * 0.1; // dampen motion for stability
        // Clamp to reasonable range
        motion = clamp(motion, float2(-8, -8), float2(8, 8));
    }
    GBuffer_MotionVectors[launchIndex] = motion;
}

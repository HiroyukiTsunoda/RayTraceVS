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
        
        // レイディスクリプタ
        RayDesc ray;
        ray.Origin = rayOrigin;
        ray.Direction = rayDir;
        ray.TMin = 0.001;
        ray.TMax = 10000.0;
        
        // ペイロード初期化
        RayPayload payload;
        payload.color = GetSkyColor(rayDir);  // 背景色（空のグラデーション）
        payload.depth = 0;
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
        
        accumulatedColor += payload.color;
        accumulatedDiffuse += payload.diffuseRadiance;
        accumulatedSpecular += payload.specularRadiance;
        accumulatedHitDist += payload.hitDistance;
        
        // 最初のヒットからNRDデータを取得（プライマリレイの情報）
        if (payload.hit && !anyHit)
        {
            primaryNormal = payload.worldNormal;
            primaryRoughness = payload.roughness;
            primaryPosition = payload.worldPosition;
            primaryViewZ = payload.viewZ;
            primaryMetallic = payload.metallic;
            primaryAlbedo = payload.albedo;
            anyHit = true;
        }
    }
    
    // 平均を取って結果を出力
    float invSampleCount = 1.0 / float(sampleCount);
    float3 finalColor = accumulatedColor * invSampleCount;
    RenderTarget[launchIndex] = float4(finalColor, 1.0);
    
    // Output NRD G-Buffer data (when enabled)
#ifdef ENABLE_NRD_GBUFFER
    if (anyHit)
    {
        GBuffer_DiffuseRadianceHitDist[launchIndex] = float4(accumulatedDiffuse * invSampleCount, accumulatedHitDist * invSampleCount);
        GBuffer_SpecularRadianceHitDist[launchIndex] = float4(accumulatedSpecular * invSampleCount, accumulatedHitDist * invSampleCount);
        GBuffer_NormalRoughness[launchIndex] = NRD_FrontEnd_PackNormalAndRoughness(primaryNormal, primaryRoughness);
        GBuffer_ViewZ[launchIndex] = primaryViewZ;
        GBuffer_MotionVectors[launchIndex] = float2(0, 0);  // TODO: Implement motion vectors
    }
#endif
}

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
    
    // サンプル数を取得（最小1、最大64）
    uint sampleCount = clamp(Scene.SamplesPerPixel, 1, 64);
    
    // 累積カラー
    float3 accumulatedColor = float3(0, 0, 0);
    
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
        
        // レイディスクリプタ
        RayDesc ray;
        ray.Origin = cameraPos;
        ray.Direction = rayDir;
        ray.TMin = 0.001;
        ray.TMax = 10000.0;
        
        // ペイロード初期化
        RayPayload payload;
        payload.color = GetSkyColor(rayDir);  // 背景色（空のグラデーション）
        payload.depth = 0;
        payload.hit = false;
        
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
    }
    
    // 平均を取って結果を出力
    float3 finalColor = accumulatedColor / float(sampleCount);
    RenderTarget[launchIndex] = float4(finalColor, 1.0);
}

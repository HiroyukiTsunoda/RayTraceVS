#include "Common.hlsli"

[shader("raygeneration")]
void RayGen()
{
    // ピクセルインデックス取得
    uint2 launchIndex = DispatchRaysIndex().xy;
    uint2 launchDim = DispatchRaysDimensions().xy;
    
    // NDC座標計算（-1 to 1）
    float2 pixelCenter = (float2)launchIndex + 0.5;
    float2 ndc = pixelCenter / (float2)launchDim * 2.0 - 1.0;
    ndc.y = -ndc.y; // Y軸反転
    
    // カメラからレイ生成
    float aspectRatio = (float)launchDim.x / (float)launchDim.y;
    float tanHalfFov = tan(radians(60.0 / 2.0));
    
    float3 cameraPos = Scene.cameraPosition;
    float3 rayDir = normalize(float3(ndc.x * aspectRatio * tanHalfFov, 
                                     ndc.y * tanHalfFov, 
                                     1.0));
    
    // レイディスクリプタ
    RayDesc ray;
    ray.Origin = cameraPos;
    ray.Direction = rayDir;
    ray.TMin = 0.001;
    ray.TMax = 10000.0;
    
    // ペイロード初期化
    RayPayload payload;
    payload.color = float3(0.2, 0.3, 0.4); // 背景色（空の色）
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
    
    // デバッグ：グラデーションパターンを表示
    float3 debugColor = float3(
        (float)launchIndex.x / (float)launchDim.x,
        (float)launchIndex.y / (float)launchDim.y,
        0.5
    );
    
    // 結果を出力（デバッグカラーとペイロードカラーをブレンド）
    RenderTarget[launchIndex] = float4(lerp(debugColor, payload.color, 0.5), 1.0);
}

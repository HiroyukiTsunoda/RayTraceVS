#include "Common.hlsli"

[shader("miss")]
void Miss(inout RayPayload payload)
{
    // 背景色（グラデーション空）
    float3 rayDir = WorldRayDirection();
    float t = 0.5 * (rayDir.y + 1.0);
    
    float3 skyColorTop = float3(0.5, 0.7, 1.0);    // 明るい青
    float3 skyColorBottom = float3(1.0, 1.0, 1.0); // 白
    
    payload.color = lerp(skyColorBottom, skyColorTop, t);
    payload.hit = false;
}

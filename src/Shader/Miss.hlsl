// Miss shader - realistic atmospheric sky gradient
#include "Common.hlsli"

[shader("miss")]
void Miss(inout RayPayload payload)
{
    float3 rayDir = normalize(WorldRayDirection());
    
    // Use the shared GetSkyColor function for consistent sky rendering
    float3 sky = GetSkyColor(rayDir);
    
    payload.color = sky;
    payload.diffuseRadiance = sky;  // NRD用：空の色をDiffuseに書き込む
    payload.specularRadiance = float3(0, 0, 0);
    payload.shadowVisibility = 1.0;
    payload.shadowPenumbra = 0.0;
    payload.shadowDistance = NRD_FP16_MAX;
    payload.hit = 0;  // 明示的にヒットなしを設定
    payload.targetObjectType = 0;
    payload.targetObjectIndex = 0;
    payload.thicknessQuery = 0;
    
    // Loop-based: terminate ray trace loop
    payload.loopRayOrigin.w = 0.0;
    payload.loopThroughput.xyz = float3(0, 0, 0);
}

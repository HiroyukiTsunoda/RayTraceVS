// Miss shader - simple sky gradient
#include "Common.hlsli"

[shader("miss")]
void Miss(inout RayPayload payload)
{
    float3 rayDir = normalize(WorldRayDirection());
    
    // Simple sky gradient based on Y direction
    float t = 0.5 * (rayDir.y + 1.0);  // Map from [-1,1] to [0,1]
    
    // Lerp between horizon color and sky color
    float3 horizonColor = float3(0.8, 0.85, 0.9);  // Light gray/white horizon
    float3 skyColor = float3(0.4, 0.6, 0.9);       // Blue sky
    
    float3 sky = lerp(horizonColor, skyColor, t);
    
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
}

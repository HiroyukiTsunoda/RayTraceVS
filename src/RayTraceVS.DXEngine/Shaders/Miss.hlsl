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
    
    payload.color = lerp(horizonColor, skyColor, t);
}

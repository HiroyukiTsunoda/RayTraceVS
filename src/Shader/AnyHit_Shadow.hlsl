// Shadow Ray Any-Hit Shader
// Enables early termination for shadow rays
// Significantly improves performance for shadow calculations

#include "Common.hlsli"

// Any-hit shader for shadow rays
// Called when a ray hits any geometry during shadow testing
// Uses RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH for early termination

[shader("anyhit")]
void AnyHit_Shadow(inout RayPayload payload, in ProceduralAttributes attribs)
{
    uint objectType = attribs.objectType;
    uint objectIndex = attribs.objectIndex;
    
    // Get material properties (transmission and color)
    float transmission = 0.0;
    float3 objectColor = float3(1, 1, 1);
    
    if (objectType == OBJECT_TYPE_SPHERE)
    {
        transmission = Spheres[objectIndex].transmission;
        objectColor = Spheres[objectIndex].color.rgb;
    }
    else if (objectType == OBJECT_TYPE_PLANE)
    {
        transmission = Planes[objectIndex].transmission;
        objectColor = Planes[objectIndex].color.rgb;
    }
    else // OBJECT_TYPE_BOX
    {
        transmission = Boxes[objectIndex].transmission;
        objectColor = Boxes[objectIndex].color.rgb;
    }
    
    // Handle translucent objects: accumulate color and transmission
    if (transmission > 0.01)
    {
        // Calculate the tint color: more transparent = less tint
        // When transmission is high (e.g., 0.9), object color has less influence
        // When transmission is low (e.g., 0.3), object color has more influence
        float3 tintColor = lerp(objectColor, float3(1, 1, 1), transmission);
        
        // Accumulate the shadow color and transmission
        payload.shadowColorAccum *= tintColor;
        payload.shadowTransmissionAccum *= transmission;
        
        // If there's still significant light passing through, continue tracing
        if (payload.shadowTransmissionAccum > 0.01)
        {
            // Record the first occluder distance if not already set
            if (payload.hitDistance >= 10000.0)
            {
                payload.hitDistance = RayTCurrent();
            }
            
            // Ignore this hit and continue searching for more occluders
            IgnoreHit();
            return;
        }
    }
    
    // Opaque object or accumulated transmission too low - full shadow
    payload.hit = true;
    payload.hitDistance = RayTCurrent();
    payload.shadowColorAccum = float3(0, 0, 0);
    payload.shadowTransmissionAccum = 0.0;
    
    // AcceptHitAndEndSearch() is implicit when using
    // RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH flag
}

// Specialized shadow payload for minimal memory usage
// Note: This shader uses the standard RayPayload for compatibility
// but could be optimized with a dedicated ShadowPayload:
//
// struct ShadowPayload
// {
//     bool inShadow;
// };
//
// [shader("anyhit")]
// void AnyHit_Shadow_Optimized(inout ShadowPayload payload, in ProceduralAttributes attribs)
// {
//     // ... same transparency check ...
//     payload.inShadow = true;
// }

// ============================================
// Usage in ClosestHit shaders:
// ============================================
//
// To use this any-hit shader for shadow rays, trace with these flags:
//
// TraceRay(
//     SceneBVH,
//     RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH |  // Stop at first hit
//     RAY_FLAG_SKIP_CLOSEST_HIT_SHADER,           // Don't run closest hit
//     0xFF,
//     1,    // Hit group index 1 for shadow rays (if using separate hit groups)
//     0,
//     1,    // Miss shader index 1 for shadow miss (optional)
//     shadowRay,
//     shadowPayload
// );
//
// bool inShadow = shadowPayload.hit;

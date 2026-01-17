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
    
    // Check if the hit object is transparent (glass)
    float transmission = 0.0;
    
    if (objectType == OBJECT_TYPE_SPHERE)
    {
        transmission = Spheres[objectIndex].transmission;
    }
    else if (objectType == OBJECT_TYPE_PLANE)
    {
        transmission = Planes[objectIndex].transmission;
    }
    else // OBJECT_TYPE_CYLINDER
    {
        transmission = Cylinders[objectIndex].transmission;
    }
    
    // If the object is highly transparent (glass), ignore it for shadow
    // This allows light to pass through glass objects
    if (transmission > 0.5)
    {
        // Ignore this hit and continue searching
        IgnoreHit();
        return;
    }
    
    // Opaque or semi-transparent object blocks light
    // Mark as in shadow and accept hit (will terminate due to ray flags)
    payload.hit = true;
    
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

// Shadow Ray Any-Hit Shader
// Enables early termination for shadow rays
// Significantly improves performance for shadow calculations

#include "Common.hlsli"

// Any-hit shader for shadow rays
// Called when a ray hits any geometry during shadow testing
// Uses RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH for early termination

[shader("anyhit")]
void AnyHit_Shadow(inout ShadowPayload payload, in ProceduralAttributes attribs)
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
    
    // Report the first occluder and its transmission/color.
    payload.hit = 1;
    payload.hitDistance = RayTCurrent();
    payload.shadowColorAccum = objectColor;
    payload.shadowTransmissionAccum = transmission;
    
    // AcceptHitAndEndSearch() is implicit when using
    // RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH flag
}

// Triangle mesh shadow any-hit (uses material data from mesh instance)
[shader("anyhit")]
void AnyHit_Shadow_Triangle(inout ShadowPayload payload, in BuiltInTriangleIntersectionAttributes attribs)
{
    uint instanceIndex = InstanceID();
    MeshInstanceInfo instInfo = MeshInstances[instanceIndex];
    MeshMaterial mat = MeshMaterials[instInfo.materialIndex];
    
    payload.hit = 1;
    payload.hitDistance = RayTCurrent();
    payload.shadowColorAccum = mat.color.rgb;
    payload.shadowTransmissionAccum = mat.transmission;
}

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

// Shadow Ray Any-Hit Shader
// Enables early termination for shadow rays
// Significantly improves performance for shadow calculations

#include "Common.hlsli"

// Any-hit shader for shadow rays
// Shadow rays are traced with RAYKIND_SHADOW and dedicated TraceRay flags.

[shader("anyhit")]
void AnyHit_Shadow(inout ShadowPayload payload, in ProceduralAttributes attribs)
{
    uint objectType = attribs.objectType;
    uint objectIndex = attribs.objectIndex;
    
    // Get material properties
    float transmission = 0.0;
    float3 sigmaA = float3(0, 0, 0);
    
    if (objectType == OBJECT_TYPE_SPHERE)
    {
        transmission = Spheres[objectIndex].transmission;
        sigmaA = Spheres[objectIndex].absorption;
    }
    else if (objectType == OBJECT_TYPE_PLANE)
    {
        transmission = Planes[objectIndex].transmission;
        sigmaA = Planes[objectIndex].absorption;
    }
    else // OBJECT_TYPE_BOX
    {
        transmission = Boxes[objectIndex].transmission;
        sigmaA = Boxes[objectIndex].absorption;
    }
    
    if (payload.hit == 0)
    {
        payload.hit = 1;
        payload.hitDistance = RayTCurrent();
        payload.hitObjectType = objectType;
        payload.hitObjectIndex = objectIndex;
    }
    
    if (transmission < 0.01)
    {
        payload.shadowTransmissionAccum = 0.0;
        payload.shadowColorAccum = float3(0, 0, 0);
        AcceptHitAndEndSearch();
        return;
    }
    
    float thickness = SHADOW_ABSORPTION_THICKNESS;
    float3 beer = any(sigmaA > 0.0) ? exp(-sigmaA * thickness * Scene.ShadowAbsorptionScale) : float3(1, 1, 1);
    payload.shadowColorAccum *= beer;
    payload.shadowTransmissionAccum *= transmission;
    IgnoreHit();
}

// Triangle mesh shadow any-hit (uses material data from mesh instance)
[shader("anyhit")]
void AnyHit_Shadow_Triangle(inout ShadowPayload payload, in BuiltInTriangleIntersectionAttributes attribs)
{
    uint instanceIndex = InstanceID();
    MeshInstanceInfo instInfo = MeshInstances[instanceIndex];
    MeshMaterial mat = MeshMaterials[instInfo.materialIndex];
    
    if (payload.hit == 0)
    {
        payload.hit = 1;
        payload.hitDistance = RayTCurrent();
        payload.hitObjectType = OBJECT_TYPE_MESH;
        payload.hitObjectIndex = instanceIndex;
    }
    
    if (mat.transmission < 0.01)
    {
        payload.shadowTransmissionAccum = 0.0;
        payload.shadowColorAccum = float3(0, 0, 0);
        AcceptHitAndEndSearch();
        return;
    }
    
    float thickness = SHADOW_ABSORPTION_THICKNESS;
    float3 beer = any(mat.absorption > 0.0) ? exp(-mat.absorption * thickness * Scene.ShadowAbsorptionScale) : float3(1, 1, 1);
    payload.shadowColorAccum *= beer;
    payload.shadowTransmissionAccum *= mat.transmission;
    IgnoreHit();
}

// Thickness ray any-hit (procedural)
[shader("anyhit")]
void AnyHit_Thickness(inout ThicknessPayload payload, in ProceduralAttributes attribs)
{
    if (payload.objectType != OBJECT_TYPE_INVALID)
    {
        if (payload.objectType != attribs.objectType || payload.objectIndex != attribs.objectIndex)
        {
            IgnoreHit();
            return;
        }
    }
    
    payload.hit = 1;
    payload.hitT = RayTCurrent();
    payload.objectType = attribs.objectType;
    payload.objectIndex = attribs.objectIndex;
    AcceptHitAndEndSearch();
}

// Thickness ray any-hit (triangle)
[shader("anyhit")]
void AnyHit_Thickness_Triangle(inout ThicknessPayload payload, in BuiltInTriangleIntersectionAttributes attribs)
{
    uint instanceIndex = InstanceID();
    if (payload.objectType != OBJECT_TYPE_INVALID)
    {
        if (payload.objectType != OBJECT_TYPE_MESH || payload.objectIndex != instanceIndex)
        {
            IgnoreHit();
            return;
        }
    }
    
    payload.hit = 1;
    payload.hitT = RayTCurrent();
    payload.objectType = OBJECT_TYPE_MESH;
    payload.objectIndex = instanceIndex;
    AcceptHitAndEndSearch();
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

// ClosestHit shader - Sphere, Plane, Box
#include "Common.hlsli"

// Perturb reflection direction based on roughness (GGX-like approximation)
float3 PerturbReflection(float3 reflectDir, float3 normal, float roughness, inout RNG rng)
{
    if (roughness < 0.01)
        return reflectDir;
    
    // Generate random values
    float r1 = rng_next(rng);
    float r2 = rng_next(rng);
    
    // Build tangent frame
    float3 tangent = abs(normal.x) > 0.9 ? float3(0, 1, 0) : float3(1, 0, 0);
    tangent = normalize(cross(normal, tangent));
    float3 bitangent = cross(normal, tangent);
    
    // Random offset scaled by roughness^2 (perceptually linear response)
    float angle = r1 * 6.28318;
    float radius = roughness * roughness * r2;
    
    float3 offset = (cos(angle) * tangent + sin(angle) * bitangent) * radius;
    float3 perturbed = normalize(reflectDir + offset);
    
    // Ensure the perturbed direction is in the hemisphere
    if (dot(perturbed, normal) < 0.0)
        perturbed = reflect(perturbed, normal);
    
    return perturbed;
}

[shader("closesthit")]
void ClosestHit(inout RadiancePayload payload, in ProceduralAttributes attribs)
{
    payload.hit = 1;
    payload.hitDistance = RayTCurrent();
    payload.hitObjectType = attribs.objectType;
    payload.hitObjectIndex = attribs.objectIndex;
    
    // Use scene-specified max bounces (glass needs more for entry/internal/exit)
    uint maxBounces = (Scene.MaxBounces > 0) ? min(Scene.MaxBounces, 32) : 10;
    if (payload.depth >= maxBounces)
    {
        // Max depth reached - return approximate color instead of black
        // Use sky color in the ray direction with a gentle tint from surface
        float3 rayDir = WorldRayDirection();
        float3 skyFallback = GetSkyColor(rayDir);
        
        // Get basic material color for tinting
        float4 matColor = float4(0.5, 0.5, 0.5, 1.0);
        if (attribs.objectType == OBJECT_TYPE_SPHERE)
            matColor = Spheres[attribs.objectIndex].color;
        else if (attribs.objectType == OBJECT_TYPE_BOX)
            matColor = Boxes[attribs.objectIndex].color;
        
        // Blend sky with material tint
        payload.color = skyFallback * lerp(float3(1, 1, 1), matColor.rgb, 0.3);
        return;
    }
    
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
    float3 rayDir = WorldRayDirection();
    
    // Normal from Intersection shader (outward)
    float3 normal = normalize(attribs.normal);
    // If normal is already pointing against ray, frontFace should be true for entering
    // But Intersection always returns normal against ray, so we need to check original geometry
    // For now, use the sign of the normal to determine entering/exiting
    
    // Get material properties
    float4 color;
    float metallic = 0.0;
    float roughness = 0.5;  // Default roughness
    float transmission = 0.0;
    float ior = 1.5;
    float specular = 0.5;   // Specular intensity
    float3 emission = float3(0.0, 0.0, 0.0);  // Emissive color
    float3 absorption = float3(0.0, 0.0, 0.0); // Beer-Lambert sigmaA
    
    if (attribs.objectType == OBJECT_TYPE_SPHERE)
    {
        SphereData s = Spheres[attribs.objectIndex];
        color = s.color;
        metallic = s.metallic;
        roughness = s.roughness;
        transmission = s.transmission;
        ior = s.ior;
        specular = s.specular;
        emission = s.emission;
        absorption = s.absorption;
    }
    else if (attribs.objectType == OBJECT_TYPE_PLANE)
    {
        PlaneData p = Planes[attribs.objectIndex];
        color = p.color;
        metallic = p.metallic;
        roughness = p.roughness;
        transmission = 0.0;
        specular = p.specular;
        emission = p.emission;
        absorption = p.absorption;
        
        // Checkerboard pattern for floor (world space coordinates)
        // Use hitPosition.xz directly for horizontal floor
        float2 uv = hitPosition.xz;
        
        // Use bitwise AND for correct handling of negative coordinates
        // fmod doesn't work correctly with negative numbers
        int ix = (int)floor(uv.x);
        int iy = (int)floor(uv.y);
        int checker = (ix + iy) & 1;
        color.rgb = checker ? float3(0.9, 0.9, 0.9) : float3(0.1, 0.1, 0.1);
    }
    else // OBJECT_TYPE_BOX
    {
        BoxData b = Boxes[attribs.objectIndex];
        color = b.color;
        metallic = b.metallic;
        roughness = b.roughness;
        transmission = b.transmission;
        ior = b.ior;
        specular = b.specular;
        emission = b.emission;
        absorption = b.absorption;

        // Recompute box normal from hit position (local space face)
        float3 ax = normalize(b.axisX);
        float3 ay = normalize(b.axisY);
        float3 az = normalize(b.axisZ);
        float3 localHit = float3(dot(hitPosition - b.center, ax),
                                 dot(hitPosition - b.center, ay),
                                 dot(hitPosition - b.center, az));
        float3 scaled = abs(localHit / max(b.size, 1e-4));
        float3 localNormal;
        if (scaled.x >= scaled.y && scaled.x >= scaled.z)
            localNormal = float3((localHit.x >= 0.0) ? 1.0 : -1.0, 0, 0);
        else if (scaled.y >= scaled.z)
            localNormal = float3(0, (localHit.y >= 0.0) ? 1.0 : -1.0, 0);
        else
            localNormal = float3(0, 0, (localHit.z >= 0.0) ? 1.0 : -1.0);
        normal = normalize(ax * localNormal.x + ay * localNormal.y + az * localNormal.z);
    }

    // Final shading normal (ensure it faces the ray)
    float3 N = (dot(rayDir, normal) < 0.0) ? normal : -normal;

    // Store hit/material data for RayGen shading (packed)
    payload.packedNormal = PackNormalOctahedron(N);
    payload.packedMaterial0 = PackHalf2(float2(roughness, metallic));
    payload.packedMaterial1 = PackHalf2(float2(specular, transmission));
    payload.packedMaterial2 = PackHalf2(float2(ior, 0.0));
    payload.albedo = color.rgb;
    payload.emission = emission;
    payload.absorption = absorption;

    // Debug: visualize material values as grayscale
    if (payload.depth == 0 && Scene.PhotonDebugMode == 3)
    {
        float t = saturate(transmission);
        payload.color = t.xxx;
        payload.diffuseRadiance = t.xxx;
        payload.specularRadiance = float3(0, 0, 0);
        return;
    }
    if (payload.depth == 0 && Scene.PhotonDebugMode == 4)
    {
        float m = saturate(metallic);
        payload.color = m.xxx;
        payload.diffuseRadiance = m.xxx;
        payload.specularRadiance = float3(0, 0, 0);
        return;
    }
    
    // Treat transmission as glass regardless of metallic to avoid parameter lock
    bool isGlass = (transmission > 0.01);
    if (isGlass && (attribs.objectType == OBJECT_TYPE_SPHERE || attribs.objectType == OBJECT_TYPE_BOX))
    {
        payload.color = float3(0, 0, 0);
        return;
    }
    
    // Shading and secondary rays are handled in RayGen; return material only.
    payload.color = float3(0, 0, 0);
    return;
}

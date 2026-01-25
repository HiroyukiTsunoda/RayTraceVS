// ClosestHit shader - Sphere, Plane, Box
#include "Common.hlsli"

// Hash function for roughness perturbation
float Hash(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// Perturb reflection direction based on roughness (GGX-like approximation)
float3 PerturbReflection(float3 reflectDir, float3 normal, float roughness, float2 seed)
{
    if (roughness < 0.01)
        return reflectDir;
    
    // Generate random values
    float r1 = Hash(seed);
    float r2 = Hash(seed + float2(17.3, 31.7));
    
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
    payload.loopRayOrigin.w = 0.0;  // Default: terminate loop unless overridden
    payload.childCount = 0;
    
    // Store hit object info for caller to check self-intersection
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
    
    // Generate random seed for soft shadow sampling
    uint seed = asuint(hitPosition.x * 1000.0) ^ asuint(hitPosition.y * 2000.0) ^ asuint(hitPosition.z * 3000.0);
    seed = WangHash(seed + payload.depth * 7919);
    
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

    // Debug: visualize material values as grayscale
    if (payload.depth == 0 && Scene.PhotonDebugMode == 3)
    {
        float t = saturate(transmission);
        payload.color = t.xxx;
        payload.diffuseRadiance = t.xxx;
        payload.specularRadiance = float3(0, 0, 0);
        payload.loopRayOrigin.w = 0.0;
        return;
    }
    if (payload.depth == 0 && Scene.PhotonDebugMode == 4)
    {
        float m = saturate(metallic);
        payload.color = m.xxx;
        payload.diffuseRadiance = m.xxx;
        payload.specularRadiance = float3(0, 0, 0);
        payload.loopRayOrigin.w = 0.0;
        return;
    }
    
    // Treat transmission as glass regardless of metallic to avoid parameter lock
    bool isGlass = (transmission > 0.01);
    
    // Final shading normal (ensure it faces the ray)
    float3 N = (dot(rayDir, normal) < 0.0) ? normal : -normal;

    // Store hit/material data for RayGen shading
    payload.worldNormal = N;
    payload.worldPosition = hitPosition;
    payload.roughness = roughness;
    payload.metallic = metallic;
    payload.albedo = color.rgb;
    payload.transmission = transmission;
    payload.ior = ior;
    payload.specular = specular;
    payload.emission = emission;
    payload.viewZ = RayTCurrent();
    
    // frontFace for glass refraction: check if ray is entering or exiting
    bool frontFace = dot(rayDir, N) < 0;
    bool isInside = (payload.pathFlags & PATH_FLAG_INSIDE) != 0;
    
    // Glass (sphere or box) with Fresnel - Loop-based stochastic selection
    if (isGlass && (attribs.objectType == OBJECT_TYPE_SPHERE || attribs.objectType == OBJECT_TYPE_BOX))
    {
        // Use current path flags to determine entering/exiting
        bool entering = !isInside;
        float eta = entering ? (1.0 / ior) : ior; // n1/n2
        
        // Fresnel term (Schlick)
        float cosTheta = saturate(dot(-rayDir, N));
        float f0 = pow((ior - 1.0) / (ior + 1.0), 2.0);
        float fresnel = FresnelSchlick(cosTheta, f0);
        
        float3 reflectDir = normalize(reflect(rayDir, N));
        float3 refractDir = refract(rayDir, N, eta);
        bool tir = dot(refractDir, refractDir) < 1e-6;
        if (!tir)
        {
            refractDir = normalize(refractDir);
        }
        // NaN/Inf guard (avoid GPU hang)
        if (any(!isfinite(N)) || any(!isfinite(reflectDir)) || any(!isfinite(refractDir)))
        {
            payload.color = GetSkyColor(rayDir);
            payload.loopRayOrigin.w = 0.0;
            return;
        }
        
        // Apply roughness perturbation for frosted glass effect
        // (Skip for secondary rays to reduce grain)
        if (roughness > 0.01 && payload.depth == 0)
        {
            float2 roughSeed = hitPosition.xy * 1000.0 + float2(payload.depth, payload.depth * 0.5);
            reflectDir = PerturbReflection(reflectDir, N, roughness, roughSeed);
            if (!tir)
            {
                float2 refractSeed = roughSeed + float2(123.456, 789.012);
                refractDir = PerturbReflection(refractDir, -N, roughness, refractSeed);
            }
        }
        
        // Total internal reflection forces reflection
        if (tir)
        {
            fresnel = 1.0;
        }
        
        // Deterministic split for loop-based tracing (reflection + refraction)
        float3 reflectThroughput = fresnel.xxx;
        float transmittance = saturate(transmission);
        float tintStrength = (payload.depth == 0) ? 1.0 : 0.7;
        float3 refractThroughput = (1.0 - fresnel) * transmittance * lerp(float3(1, 1, 1), color.rgb, tintStrength);
        reflectThroughput = clamp(reflectThroughput, 0.0, 1.0);
        refractThroughput = clamp(refractThroughput, 0.0, 1.0);
        
        uint childCount = 0;
        // Reflection child (always when glass)
        PathState reflectChild;
        reflectChild.origin = hitPosition + N * 0.002;
        reflectChild.tMin = (attribs.objectType == OBJECT_TYPE_BOX) ? 2.5 : 0.001;
        reflectChild.direction = reflectDir;
        reflectChild.depth = payload.depth + 1;
        reflectChild.throughput = reflectThroughput;
        reflectChild.flags = payload.pathFlags | PATH_FLAG_SPECULAR;
        reflectChild.absorption = payload.pathAbsorption;
        reflectChild.pathType = PATH_TYPE_RADIANCE;
        reflectChild.skyBoost = SKY_BOOST_GLASS;
        reflectChild.padding2 = float3(0, 0, 0);
        payload.childPaths[childCount++] = reflectChild;
        
        // Refraction child (skip when TIR)
        if (!tir)
        {
            PathState refractChild;
            refractChild.origin = hitPosition + refractDir * 0.002;
            refractChild.tMin = (attribs.objectType == OBJECT_TYPE_BOX) ? 3.0 : 0.001;
            refractChild.direction = refractDir;
            refractChild.depth = payload.depth + 1;
            refractChild.throughput = refractThroughput;
            if (entering)
            {
                refractChild.flags = payload.pathFlags | PATH_FLAG_INSIDE | PATH_FLAG_SPECULAR;
                refractChild.absorption = absorption;
            }
            else
            {
                refractChild.flags = (payload.pathFlags & ~PATH_FLAG_INSIDE) | PATH_FLAG_SPECULAR;
                refractChild.absorption = float3(0, 0, 0);
            }
            refractChild.pathType = PATH_TYPE_RADIANCE;
            refractChild.skyBoost = SKY_BOOST_GLASS;
            refractChild.padding2 = float3(0, 0, 0);
            payload.childPaths[childCount++] = refractChild;
        }
        payload.childCount = childCount;
        
        // Shading is handled in RayGen; return material + child paths only.
        payload.color = float3(0, 0, 0);
        
        return;
    }
    
    // ============================================
    // Universal PBR Shading (Metallic-Roughness Workflow)
    // ============================================
    // F0: 非金属 = 0.04, 金属 = baseColor, 連続補間
    // diffuseColor = baseColor * (1 - metallic)
    // ============================================
    
    float3 V = -rayDir;
    
    // Universal PBR: F0 interpolation based on metallic
    // Non-metal: F0 = 0.04 (dielectric)
    // Metal: F0 = baseColor
    float3 F0 = lerp(0.04.xxx, color.rgb, metallic);
    
    // Diffuse color (metals have no diffuse)
    float3 diffuseColor = color.rgb * (1.0 - metallic);
    
    // Reflection for metallic surfaces (scaled by metallic value)
    float3 reflectColor = float3(0, 0, 0);
    if (metallic > 0.1 && payload.depth < maxBounces)
    {
        float3 reflectDir = reflect(rayDir, N);
        float2 reflectSeed = hitPosition.xy * 1000.0 + float2(payload.depth, payload.depth * 0.5);
        float3 perturbedDir = PerturbReflection(reflectDir, N, roughness, reflectSeed);
        
        // Queue-based reflection: trace in RayGen, keep local contribution minimal
        float NdotV = saturate(dot(N, V));
        float3 F = Fresnel_Schlick3(NdotV, F0);
        float reflectScale = (1.0 - roughness * 0.5);
        // Boost secondary metal reflections a bit to avoid overly dark chains
        float boost = (payload.depth > 0) ? 1.5 : 1.0;
        
        PathState reflectChild;
        reflectChild.origin = hitPosition + N * 0.002;
        reflectChild.tMin = 0.001;
        reflectChild.direction = perturbedDir;
        reflectChild.depth = payload.depth + 1;
        reflectChild.throughput = F * reflectScale * boost;
        reflectChild.flags = payload.pathFlags | PATH_FLAG_SPECULAR;
        reflectChild.absorption = payload.pathAbsorption;
        reflectChild.pathType = PATH_TYPE_RADIANCE;
        reflectChild.skyBoost = SKY_BOOST_METAL;
        reflectChild.padding2 = float3(0, 0, 0);
        payload.childPaths[0] = reflectChild;
        payload.childCount = 1;
    }
    
    // Shading is handled in RayGen; return material + child paths only.
    payload.color = float3(0, 0, 0);
    return;
}

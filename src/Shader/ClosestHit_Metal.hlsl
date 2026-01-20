// Metal Material Closest Hit Shader
// Optimized for metallic, reflective surfaces
// Reduces warp divergence by separating material types

#include "Common.hlsli"

// Hash function for roughness perturbation
float Hash(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// Perturb reflection direction based on roughness
float3 PerturbReflection(float3 reflectDir, float3 normal, float roughness, float2 seed)
{
    if (roughness < 0.01)
        return reflectDir;
    
    float r1 = Hash(seed);
    float r2 = Hash(seed + float2(17.3, 31.7));
    
    float3 tangent = abs(normal.x) > 0.9 ? float3(0, 1, 0) : float3(1, 0, 0);
    tangent = normalize(cross(normal, tangent));
    float3 bitangent = cross(normal, tangent);
    
    float angle = r1 * 6.28318;
    float radius = roughness * roughness * r2;
    
    float3 offset = (cos(angle) * tangent + sin(angle) * bitangent) * radius;
    float3 perturbed = normalize(reflectDir + offset);
    
    if (dot(perturbed, normal) < 0.0)
        perturbed = reflect(perturbed, normal);
    
    return perturbed;
}

// Calculate basic lighting for roughness blend
float3 CalculateMetalLighting(float3 hitPosition, float3 normal, float3 objectColor)
{
    float3 finalColor = float3(0, 0, 0);
    finalColor += objectColor * 0.1;  // Ambient
    
    for (uint i = 0; i < Scene.NumLights; i++)
    {
        LightData light = Lights[i];
        
        if (light.type == LIGHT_TYPE_AMBIENT)
        {
            finalColor += objectColor * light.color.rgb * light.intensity;
        }
        else
        {
            float3 lightDir;
            float attenuation = 1.0;
            
            if (light.type == LIGHT_TYPE_DIRECTIONAL)
            {
                lightDir = normalize(-light.position);
            }
            else
            {
                lightDir = normalize(light.position - hitPosition);
                float lightDist = length(light.position - hitPosition);
                attenuation = 1.0 / (1.0 + lightDist * lightDist * 0.01);
            }
            
            float diff = max(0.0, dot(normal, lightDir));
            finalColor += objectColor * light.color.rgb * light.intensity * diff * attenuation;
        }
    }
    
    return finalColor;
}

// Force recompile: hitDistance fix for secondary rays 2026-01-19

[shader("closesthit")]
void ClosestHit_Metal(inout RayPayload payload, in ProceduralAttributes attribs)
{
    // CRITICAL: Always set hitDistance for ALL rays (primary AND secondary)
    // NRD needs the hit distance from reflection rays to properly denoise specular
    payload.hit = true;
    payload.hitDistance = RayTCurrent();
    
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
    float hitDistance = RayTCurrent();
    float3 normal = attribs.normal;
    uint objectType = attribs.objectType;
    uint objectIndex = attribs.objectIndex;
    
    // Get material properties
    float4 color;
    float roughness;
    
    if (objectType == OBJECT_TYPE_SPHERE)
    {
        SphereData sphere = Spheres[objectIndex];
        color = sphere.color;
        roughness = sphere.roughness;
    }
    else if (objectType == OBJECT_TYPE_PLANE)
    {
        PlaneData plane = Planes[objectIndex];
        color = plane.color;
        roughness = plane.roughness;
    }
    else
    {
        BoxData box = Boxes[objectIndex];
        color = box.color;
        roughness = box.roughness;
    }
    
    float3 objectColor = color.rgb;
    float3 finalColor = float3(0, 0, 0);
    float3 specularColor = float3(0, 0, 0);
    float3 diffuseLighting = CalculateMetalLighting(hitPosition, normal, float3(1.0, 1.0, 1.0));
    float3 diffuseColor = float3(0, 0, 0);
    
    // Variables for NRD specular output (need to be in outer scope)
    float3 fresnel = float3(0, 0, 0);
    RayPayload reflectPayload;
    reflectPayload.diffuseRadiance = float3(0, 0, 0);
    reflectPayload.specularRadiance = float3(0, 0, 0);
    reflectPayload.albedo = float3(0, 0, 0);
    
    if (payload.depth < MAX_RECURSION_DEPTH)
    {
        float3 viewDir = -WorldRayDirection();
        float cosTheta = max(0.0, dot(viewDir, normal));
        
        // Metal F0 is the base color
        float3 f0 = objectColor;
        fresnel = f0 + (1.0 - f0) * pow(1.0 - cosTheta, 5.0);
        
        // Reflection with roughness perturbation
        float3 reflectDir = reflect(WorldRayDirection(), normal);
        float2 seed = hitPosition.xy * 1000.0 + float2(payload.depth, payload.depth * 0.5);
        float3 perturbedDir = PerturbReflection(reflectDir, normal, roughness, seed);
        
        RayDesc reflectRay;
        reflectRay.Origin = hitPosition + normal * 0.001;
        reflectRay.Direction = perturbedDir;
        reflectRay.TMin = 0.001;
        reflectRay.TMax = 10000.0;
        
        reflectPayload.color = float3(0, 0, 0);
        reflectPayload.depth = payload.depth + 1;
        reflectPayload.hit = false;
        reflectPayload.padding = 0.0;
        // Initialize NRD fields
        reflectPayload.diffuseRadiance = float3(0, 0, 0);
        reflectPayload.specularRadiance = float3(0, 0, 0);
        reflectPayload.hitDistance = 10000.0;
        reflectPayload.specularHitDistance = 10000.0;
        reflectPayload.worldNormal = float3(0, 1, 0);
        reflectPayload.roughness = 1.0;
        reflectPayload.worldPosition = float3(0, 0, 0);
        reflectPayload.viewZ = 10000.0;
        reflectPayload.metallic = 0.0;
        reflectPayload.albedo = float3(0, 0, 0);
        reflectPayload.shadowVisibility = 1.0;
        reflectPayload.shadowPenumbra = 0.0;
        reflectPayload.shadowDistance = NRD_FP16_MAX;
        reflectPayload.targetObjectType = 0;
        reflectPayload.targetObjectIndex = 0;
        reflectPayload.thicknessQuery = 0;
        
        TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, reflectRay, reflectPayload);
        
        // If reflection ray missed (hit sky), use sky color explicitly
        float3 reflectedColor;
        if (reflectPayload.hit)
        {
            reflectedColor = reflectPayload.color;
        }
        else
        {
            reflectedColor = GetSkyColor(perturbedDir);
        }
        
        // Metal reflection tinted by base color
        specularColor = reflectedColor * fresnel;
        finalColor = specularColor;
        
        // Blend with diffuse for rough metals
        if (roughness > 0.1)
        {
            diffuseColor = diffuseLighting * objectColor;
            float diffuseBlend = roughness * roughness;
            finalColor = lerp(finalColor, diffuseColor, diffuseBlend * 0.5);
        }
    }
    else
    {
        // Max depth reached - use approximate reflection with environment
        // Instead of just diffuse, blend with sky color in reflection direction
        // This provides a more natural fallback for deep reflections
        float3 viewDir = -WorldRayDirection();
        float3 reflectDir = reflect(WorldRayDirection(), normal);
        float3 skyReflection = GetSkyColor(reflectDir);
        
        // Metal F0 (Fresnel at normal incidence) is the base color
        float cosTheta = max(0.0, dot(viewDir, normal));
        float3 f0 = objectColor;
        fresnel = f0 + (1.0 - f0) * pow(1.0 - cosTheta, 5.0);
        
        // Combine diffuse lighting with sky reflection, tinted by metal color
        diffuseColor = diffuseLighting * objectColor;
        float3 approxReflection = skyReflection * fresnel;
        
        // Blend based on roughness - smoother metals reflect more sky
        float reflectWeight = saturate(1.0 - roughness * roughness);
        finalColor = lerp(diffuseColor, approxReflection, reflectWeight * 0.7);
    }
    
    payload.color = saturate(finalColor);
    payload.hit = true;
    
    // NRD-specific outputs (only for primary rays)
    if (payload.depth == 0)
    {
        // Store RADIANCE ONLY (no albedo) for NRD denoising
        // For metals: diffuseLighting is pure lighting
        // Albedo is stored separately and multiplied AFTER denoising in Composite shader
        payload.diffuseRadiance = diffuseLighting;  // Pure radiance
        
        // For specular: use reflected RADIANCE, not saturated color
        // reflectPayload.color is saturated/clamped, which destroys HDR
        float3 specularRadiance = float3(0, 0, 0);
        if (any(fresnel > 0.01))
        {
            float3 reflectedRadiance = reflectPayload.diffuseRadiance * reflectPayload.albedo 
                                     + reflectPayload.specularRadiance;
            specularRadiance = reflectedRadiance * fresnel;
        }
        // NOTE: ×4 boost is now applied in RayGen for easier tuning
        payload.specularRadiance = specularRadiance;
        payload.hitDistance = hitDistance;
        // Metal: specular comes from reflection ray, use its hit distance
        payload.specularHitDistance = reflectPayload.hit ? reflectPayload.hitDistance : hitDistance;
        payload.worldNormal = normal;
        payload.roughness = roughness;
        payload.worldPosition = hitPosition;
        payload.viewZ = hitDistance;
        payload.metallic = 1.0;  // Metal surfaces
        payload.albedo = objectColor;
        
        // SIGMA shadow input (single primary light)
        SoftShadowResult sigmaShadow;
        uint seed = asuint(hitPosition.x * 1000.0) ^ asuint(hitPosition.y * 2000.0) ^ asuint(hitPosition.z * 3000.0);
        seed = WangHash(seed + payload.depth * 7919);
        if (GetPrimaryShadowForSigma(hitPosition, normal, seed, sigmaShadow))
        {
            payload.shadowVisibility = sigmaShadow.visibility;
            payload.shadowPenumbra = sigmaShadow.penumbra;
            payload.shadowDistance = sigmaShadow.occluderDistance;
        }
        else
        {
            payload.shadowVisibility = 1.0;
            payload.shadowPenumbra = 0.0;
            payload.shadowDistance = NRD_FP16_MAX;
        }
    }
}

// ClosestHit shader - Sphere, Plane, Box
#include "Common.hlsli"

#define SHADOW_RAY_DEPTH 100

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
void ClosestHit(inout RayPayload payload, in ProceduralAttributes attribs)
{
    payload.hit = 1;
    
    if (payload.depth >= SHADOW_RAY_DEPTH)
    {
        return;
    }
    
    // Use scene-specified max bounces (defaults to 5 if unset)
    uint maxBounces = (Scene.MaxBounces > 0) ? Scene.MaxBounces : 5;
    if (payload.depth >= maxBounces)
    {
        payload.color = float3(0, 0, 0);
        return;
    }
    
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
    float3 normal = attribs.normal;
    float3 rayDir = WorldRayDirection();
    
    // Get material properties
    float4 color;
    float metallic = 0.0;
    float roughness = 0.5;  // Default roughness
    float transmission = 0.0;
    float ior = 1.5;
    
    if (attribs.objectType == OBJECT_TYPE_SPHERE)
    {
        SphereData s = Spheres[attribs.objectIndex];
        color = s.color;
        metallic = s.metallic;
        roughness = s.roughness;
        transmission = s.transmission;
        ior = s.ior;
    }
    else if (attribs.objectType == OBJECT_TYPE_PLANE)
    {
        PlaneData p = Planes[attribs.objectIndex];
        color = p.color;
        metallic = p.metallic;
        roughness = p.roughness;
        transmission = 0.0;
        
        // Checkerboard pattern for planes
        float scale = 1.0;
        int checkX = (int)floor(hitPosition.x * scale);
        int checkZ = (int)floor(hitPosition.z * scale);
        bool isWhite = ((checkX + checkZ) & 1) == 0;
        color.rgb = isWhite ? float3(0.9, 0.9, 0.9) : float3(0.1, 0.1, 0.1);
    }
    else // OBJECT_TYPE_BOX
    {
        BoxData b = Boxes[attribs.objectIndex];
        color = b.color;
        metallic = b.metallic;
        roughness = b.roughness;
        transmission = b.transmission;
        ior = b.ior;
    }
    
    bool isGlass = transmission > 0.01;
    bool isMetal = metallic > 0.5;
    
    // Glass (sphere or box) with Fresnel and reflection/refraction
    if (isGlass && (attribs.objectType == OBJECT_TYPE_SPHERE || attribs.objectType == OBJECT_TYPE_BOX))
    {
        bool entering = dot(rayDir, normal) < 0;
        float3 N = entering ? normal : -normal;   // N always points against incoming ray
        float eta = entering ? (1.0 / ior) : ior; // n1/n2
        
        // Fresnel term (Schlick)
        float cosTheta = saturate(dot(-rayDir, N));
        float f0 = pow((ior - 1.0) / (ior + 1.0), 2.0);
        
        float3 reflectDir = normalize(reflect(rayDir, N));
        float3 refractDir = refract(rayDir, N, eta);
        bool tir = dot(refractDir, refractDir) < 1e-6;
        if (!tir)
        {
            refractDir = normalize(refractDir);
        }
        
        float3 reflectColor = float3(0, 0, 0);
        float3 refractColor = float3(0, 0, 0);
        
        // If we are at the recursion budget, shoot1本だけで早期終了
        bool nearLimit = (payload.depth + 1) >= maxBounces;
        
        // Trace reflection (always allowed)
        {
            RayDesc reflectRay;
            reflectRay.Origin = hitPosition + reflectDir * 0.002;   // offset along outgoing direction
            reflectRay.Direction = reflectDir;
            reflectRay.TMin = 0.001;
            reflectRay.TMax = 10000.0;
            
            RayPayload reflPayload;
            reflPayload.color = GetSkyColor(reflectRay.Direction);
            reflPayload.depth = payload.depth + 1;
            reflPayload.hit = 0;
            reflPayload.padding = 0.0;
            // Initialize NRD fields
            reflPayload.diffuseRadiance = float3(0, 0, 0);
            reflPayload.specularRadiance = float3(0, 0, 0);
            reflPayload.hitDistance = 10000.0;
            reflPayload.worldNormal = float3(0, 1, 0);
            reflPayload.roughness = 1.0;
            reflPayload.worldPosition = float3(0, 0, 0);
            reflPayload.viewZ = 10000.0;
            reflPayload.metallic = 0.0;
            reflPayload.albedo = float3(0, 0, 0);
            reflPayload.targetObjectType = 0;
            reflPayload.targetObjectIndex = 0;
            reflPayload.thicknessQuery = 0;
            
            TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, reflectRay, reflPayload);
            reflectColor = reflPayload.color;
        }
        
        // Trace refraction if not total internal reflection and we have budget
        if (!tir && !nearLimit)
        {
            RayDesc refractRay;
            refractRay.Origin = hitPosition + refractDir * 0.002; // push along transmitted direction
            refractRay.Direction = refractDir;
            refractRay.TMin = 0.001;
            refractRay.TMax = 10000.0;
            
            RayPayload refrPayload;
            refrPayload.color = GetSkyColor(refractRay.Direction);
            refrPayload.depth = payload.depth + 1;
            refrPayload.hit = 0;
            refrPayload.padding = 0.0;
            // Initialize NRD fields
            refrPayload.diffuseRadiance = float3(0, 0, 0);
            refrPayload.specularRadiance = float3(0, 0, 0);
            refrPayload.hitDistance = 10000.0;
            refrPayload.worldNormal = float3(0, 1, 0);
            refrPayload.roughness = 1.0;
            refrPayload.worldPosition = float3(0, 0, 0);
            refrPayload.viewZ = 10000.0;
            refrPayload.metallic = 0.0;
            refrPayload.albedo = float3(0, 0, 0);
            refrPayload.targetObjectType = 0;
            refrPayload.targetObjectIndex = 0;
            refrPayload.thicknessQuery = 0;
            
            TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, refractRay, refrPayload);
            refractColor = refrPayload.color;
        }
        
        float fresnel = tir ? 1.0 : FresnelSchlick(cosTheta, f0);
        
        // Apply simple tint from material color to transmitted component
        float3 tintedRefract = refractColor * color.rgb;
        payload.color = lerp(tintedRefract, reflectColor, fresnel);
        
        // NRD outputs for glass (primary rays only)
        if (payload.depth == 0)
        {
            float hitDistance = RayTCurrent();
            payload.diffuseRadiance = tintedRefract;
            payload.specularRadiance = reflectColor * fresnel;
            payload.hitDistance = hitDistance;
            payload.worldNormal = normal;
            payload.roughness = roughness;
            payload.worldPosition = hitPosition;
            payload.viewZ = hitDistance;  // Simplified - should compute proper view Z
            payload.metallic = 0.0;
            payload.albedo = color.rgb;
        }
        return;
    }
    
    // Metal material with roughness-based reflection blur
    if (isMetal && payload.depth < maxBounces)
    {
        float3 reflectDir = reflect(rayDir, normal);
        
        // Apply roughness perturbation
        float2 seed = hitPosition.xy * 1000.0 + float2(payload.depth, payload.depth * 0.5);
        float3 perturbedDir = PerturbReflection(reflectDir, normal, roughness, seed);
        
        RayDesc reflectRay;
        reflectRay.Origin = hitPosition + normal * 0.01;
        reflectRay.Direction = perturbedDir;
        reflectRay.TMin = 0.001;
        reflectRay.TMax = 10000.0;
        
        RayPayload reflectPayload;
        reflectPayload.color = float3(0, 0, 0);
        reflectPayload.depth = payload.depth + 1;
        reflectPayload.hit = 0;
        reflectPayload.padding = 0.0;
        // Initialize NRD fields
        reflectPayload.diffuseRadiance = float3(0, 0, 0);
        reflectPayload.specularRadiance = float3(0, 0, 0);
        reflectPayload.hitDistance = 10000.0;
        reflectPayload.worldNormal = float3(0, 1, 0);
        reflectPayload.roughness = 1.0;
        reflectPayload.worldPosition = float3(0, 0, 0);
        reflectPayload.viewZ = 10000.0;
        reflectPayload.metallic = 0.0;
        reflectPayload.albedo = float3(0, 0, 0);
        reflectPayload.targetObjectType = 0;
        reflectPayload.targetObjectIndex = 0;
        reflectPayload.thicknessQuery = 0;
        
        TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, reflectRay, reflectPayload);
        
        float3 reflectColor = reflectPayload.color * color.rgb;
        float3 diffuseColor = float3(0, 0, 0);
        
        // For rough metals, blend with a diffuse-like component
        if (roughness > 0.1)
        {
            // Simple diffuse lighting for rough metal blend
            float3 lightDir = normalize(Scene.LightPosition - hitPosition);
            float diff = max(0.0, dot(normal, lightDir));
            diffuseColor = color.rgb * diff * 0.5 + color.rgb * 0.1;  // diffuse + ambient
            
            float diffuseBlend = roughness * roughness;  // Perceptually linear
            reflectColor = lerp(reflectColor, diffuseColor, diffuseBlend * 0.5);
        }
        
        payload.color = reflectColor;
        
        // NRD outputs for metal (primary rays only)
        if (payload.depth == 0)
        {
            float hitDistance = RayTCurrent();
            float3 safeAlbedo = max(color.rgb, float3(0.001, 0.001, 0.001));
            payload.diffuseRadiance = diffuseColor / safeAlbedo;
            payload.specularRadiance = reflectPayload.color * color.rgb;
            payload.hitDistance = hitDistance;
            payload.worldNormal = normal;
            payload.roughness = roughness;
            payload.worldPosition = hitPosition;
            payload.viewZ = hitDistance;
            payload.metallic = 1.0;
            payload.albedo = color.rgb;
        }
        return;
    }
    
    // Diffuse material with specular highlight
    float3 ambient = color.rgb * 0.2;
    float3 diffuse = float3(0, 0, 0);
    float3 specular = float3(0, 0, 0);
    
    float3 lightDir = normalize(Scene.LightPosition - hitPosition);
    float lightDist = length(Scene.LightPosition - hitPosition);
    
    RayDesc shadowRay;
    shadowRay.Origin = hitPosition + normal * 0.01;
    shadowRay.Direction = lightDir;
    shadowRay.TMin = 0.001;
    shadowRay.TMax = lightDist;
    
    RayPayload shadowPayload;
    shadowPayload.color = float3(0, 0, 0);
    shadowPayload.depth = SHADOW_RAY_DEPTH;
    shadowPayload.hit = 0;
    shadowPayload.padding = 0.0;
    // Initialize NRD fields for shadow ray
    shadowPayload.diffuseRadiance = float3(0, 0, 0);
    shadowPayload.specularRadiance = float3(0, 0, 0);
    shadowPayload.hitDistance = 10000.0;
    shadowPayload.worldNormal = float3(0, 1, 0);
    shadowPayload.roughness = 1.0;
    shadowPayload.worldPosition = float3(0, 0, 0);
    shadowPayload.viewZ = 10000.0;
    shadowPayload.metallic = 0.0;
    shadowPayload.albedo = float3(0, 0, 0);
    shadowPayload.targetObjectType = 0;
    shadowPayload.targetObjectIndex = 0;
    shadowPayload.thicknessQuery = 0;
    
    TraceRay(SceneBVH, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH,
             0xFF, 0, 0, 0, shadowRay, shadowPayload);
    
    bool inShadow = (shadowPayload.hit != 0);
    
    if (!inShadow)
    {
        // Diffuse component
        float diff = max(0.0, dot(normal, lightDir));
        diffuse = color.rgb * diff * 0.8;
        
        // Specular component (Blinn-Phong, affected by roughness)
        float3 viewDir = -rayDir;
        float3 halfDir = normalize(lightDir + viewDir);
        float shininess = max(1.0, 128.0 * (1.0 - roughness));  // Higher roughness = lower shininess
        float spec = pow(max(0.0, dot(normal, halfDir)), shininess);
        specular = Scene.LightColor.rgb * Scene.LightIntensity * spec * (1.0 - roughness) * 0.3;
    }
    
    payload.color = saturate(ambient + diffuse + specular);
    
    // NRD outputs for diffuse (primary rays only)
    if (payload.depth == 0)
    {
        float hitDistance = RayTCurrent();
        float3 safeAlbedo = max(color.rgb, float3(0.001, 0.001, 0.001));
        payload.diffuseRadiance = (ambient + diffuse) / safeAlbedo;
        payload.specularRadiance = specular;
        payload.hitDistance = hitDistance;
        payload.worldNormal = normal;
        payload.roughness = roughness;
        payload.worldPosition = hitPosition;
        payload.viewZ = hitDistance;
        payload.metallic = metallic;
        payload.albedo = color.rgb;
    }
}

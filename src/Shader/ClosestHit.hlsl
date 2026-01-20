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
    float3 normal = attribs.normal;
    float3 rayDir = WorldRayDirection();
    
    // Generate random seed for soft shadow sampling
    uint seed = asuint(hitPosition.x * 1000.0) ^ asuint(hitPosition.y * 2000.0) ^ asuint(hitPosition.z * 3000.0);
    seed = WangHash(seed + payload.depth * 7919);
    
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
    }
    
    bool isGlass = transmission > 0.01;
    bool isMetal = metallic > 0.5;
    
    // Glass (sphere or box) with Fresnel and reflection/refraction
    if (isGlass && (attribs.objectType == OBJECT_TYPE_SPHERE || attribs.objectType == OBJECT_TYPE_BOX))
    {
        bool entering = dot(rayDir, normal) < 0;
        float3 N = entering ? normal : -normal;   // N always points against incoming ray
        float eta = entering ? (1.0 / ior) : ior; // n1/n2 (restored original IOR)
        
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
        
        // Trace reflection
        // For box: use larger TMin to skip self-intersection on other faces
        {
            RayDesc reflectRay;
            reflectRay.Origin = hitPosition + N * 0.002;
            reflectRay.Direction = reflectDir;
            // Box: TMin large enough to escape the box (typical size ~1.0, so TMin=2.0 escapes)
            reflectRay.TMin = (attribs.objectType == OBJECT_TYPE_BOX) ? 2.5 : 0.001;
            reflectRay.TMax = 10000.0;
            
            RayPayload reflPayload;
            reflPayload.color = GetSkyColor(reflectRay.Direction);
            reflPayload.depth = payload.depth + 1;
            reflPayload.hit = 0;
            reflPayload.padding = 0.0;
            reflPayload.diffuseRadiance = float3(0, 0, 0);
            reflPayload.specularRadiance = float3(0, 0, 0);
            reflPayload.hitDistance = 10000.0;
            reflPayload.worldNormal = float3(0, 1, 0);
            reflPayload.roughness = 1.0;
            reflPayload.worldPosition = float3(0, 0, 0);
            reflPayload.viewZ = 10000.0;
            reflPayload.metallic = 0.0;
            reflPayload.albedo = float3(0, 0, 0);
            reflPayload.shadowVisibility = 1.0;
            reflPayload.shadowPenumbra = 0.0;
            reflPayload.shadowDistance = NRD_FP16_MAX;
            // Pass current object info to skip self-intersection
            reflPayload.targetObjectType = attribs.objectType;
            reflPayload.targetObjectIndex = attribs.objectIndex;
            reflPayload.thicknessQuery = 0;
            
            TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, reflectRay, reflPayload);
            reflectColor = reflPayload.color;
        }
        
        // Trace refraction if not total internal reflection
        if (!tir)
        {
            // Sky color fallback for refraction direction
            float3 skyFallback = GetSkyColor(refractDir);
            
            if (!nearLimit)
            {
                // We have recursion budget - trace the refraction ray
                RayDesc refractRay;
                refractRay.Origin = hitPosition + refractDir * 0.002; // push along transmitted direction
                refractRay.Direction = refractDir;
                // For boxes: use larger TMin to skip the opposite face of the same box
                // This allows the ray to exit the box and hit other objects
                // Box typical size ~1.0, so TMin=3.0 ensures we escape the box
                refractRay.TMin = (attribs.objectType == OBJECT_TYPE_BOX) ? 3.0 : 0.001;
                refractRay.TMax = 10000.0;
                
                RayPayload refrPayload;
                refrPayload.color = skyFallback;  // Initialize with sky color
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
                refrPayload.shadowVisibility = 1.0;
                refrPayload.shadowPenumbra = 0.0;
                refrPayload.shadowDistance = NRD_FP16_MAX;
                refrPayload.targetObjectType = 0;
                refrPayload.targetObjectIndex = 0;
                refrPayload.thicknessQuery = 0;
                
                TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, refractRay, refrPayload);
                refractColor = refrPayload.color;
                
                // If refraction result is too dark (possible deep recursion artifact),
                // blend with sky color to prevent black artifacts
                float refractLuminance = dot(refractColor, float3(0.299, 0.587, 0.114));
                if (refractLuminance < 0.05)
                {
                    // Blend with sky to prevent complete blackout
                    refractColor = lerp(skyFallback, refractColor, refractLuminance / 0.05);
                }
            }
            else
            {
                // Near recursion limit - use approximate refraction with sky color
                // This prevents black artifacts when viewing glass through reflections
                refractColor = skyFallback * lerp(float3(1, 1, 1), color.rgb, 0.5);
            }
        }
        else
        {
            // Total internal reflection - use sky color as fallback
            refractColor = GetSkyColor(rayDir);
        }
        
        float fresnel = tir ? 1.0 : FresnelSchlick(cosTheta, f0);
        
        // Apply tint from material color to transmitted component
        // For primary rays (direct view): use full color tint for proper glass appearance
        // For secondary rays (reflections): use gentler tint to prevent black artifacts
        float tintStrength = (payload.depth == 0) ? 1.0 : 0.4;
        float3 tintedRefract = refractColor * lerp(float3(1, 1, 1), color.rgb, tintStrength);
        
        // Blend refraction and reflection using Fresnel
        payload.color = lerp(tintedRefract, reflectColor, fresnel);
        
        // For secondary rays only: ensure minimum brightness to prevent black artifacts
        if (payload.depth > 0)
        {
            float finalLuminance = dot(payload.color, float3(0.299, 0.587, 0.114));
            if (finalLuminance < 0.05)
            {
                // If result is too dark, blend with sky
                float3 skyContrib = GetSkyColor(refractDir) * 0.2;
                payload.color = lerp(skyContrib, payload.color, finalLuminance / 0.05);
            }
        }
        
        // NRD outputs for glass (primary rays only)
        if (payload.depth == 0)
        {
            float hitDistance = RayTCurrent();
            // For glass, store the FULL appearance (refraction + reflection blend)
            payload.diffuseRadiance = payload.color;  // Same as lerp(tintedRefract, reflectColor, fresnel)
            payload.specularRadiance = float3(0, 0, 0);  // Already included above
            payload.hitDistance = hitDistance;
            payload.worldNormal = normal;
            payload.roughness = roughness;
            payload.worldPosition = hitPosition;
            payload.viewZ = hitDistance;
            payload.metallic = 0.0;
            payload.albedo = color.rgb;
            
            // Glass is transparent, so it doesn't receive shadows in the traditional sense
            // Set visibility to 1.0 (no shadow) for SIGMA
            payload.shadowVisibility = 1.0;
            payload.shadowPenumbra = 0.0;
            payload.shadowDistance = NRD_FP16_MAX;
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
        reflectPayload.shadowVisibility = 1.0;
        reflectPayload.shadowPenumbra = 0.0;
        reflectPayload.shadowDistance = NRD_FP16_MAX;
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
            // For metal, diffuseRadiance contains the FULL reflection (already colored)
            // This is what we see - metallic reflection with the metal's color
            payload.diffuseRadiance = reflectColor;  // Full metal appearance
            payload.specularRadiance = float3(0, 0, 0);  // Already included above
            payload.hitDistance = hitDistance;
            payload.worldNormal = normal;
            payload.roughness = roughness;
            payload.worldPosition = hitPosition;
            payload.viewZ = hitDistance;
            payload.metallic = 1.0;
            payload.albedo = color.rgb;
            
            // Calculate shadow for metal surface
            uint shadowSeed = uint(hitPosition.x * 12345.0 + hitPosition.y * 67890.0);
            SoftShadowResult sigmaShadow;
            if (GetPrimaryShadowForSigma(hitPosition, normal, shadowSeed, sigmaShadow))
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
        return;
    }
    
    // Diffuse material with specular highlight and soft shadows
    float3 ambient = color.rgb * 0.2;
    float3 diffuse = float3(0, 0, 0);
    float3 diffuseNoShadow = float3(0, 0, 0);
    float3 specular = float3(0, 0, 0);
    
    // Process all lights with soft shadows
    if (Scene.NumLights > 0)
    {
        for (uint li = 0; li < Scene.NumLights; li++)
        {
            LightData light = Lights[li];
            
            if (light.type == LIGHT_TYPE_AMBIENT)
            {
                ambient += color.rgb * light.color.rgb * light.intensity;
            }
            else
            {
                // Calculate soft shadow visibility
                SoftShadowResult shadow = CalculateSoftShadow(hitPosition, normal, light, seed);
                
                if (shadow.visibility > 0.0)
                {
                    float3 lightDir;
                    float attenuation = 1.0;
                    
                    if (light.type == LIGHT_TYPE_DIRECTIONAL)
                    {
                        lightDir = normalize(-light.position);
                    }
                    else // LIGHT_TYPE_POINT
                    {
                        lightDir = normalize(light.position - hitPosition);
                        float lightDist = length(light.position - hitPosition);
                        attenuation = 1.0 / (1.0 + lightDist * lightDist * 0.01);
                    }
                    
                    // Diffuse component
                    float diff = max(0.0, dot(normal, lightDir));
                    diffuse += color.rgb * light.color.rgb * light.intensity * diff * attenuation * shadow.visibility * 0.8;
                    diffuseNoShadow += color.rgb * light.color.rgb * light.intensity * diff * attenuation * 0.8;
                    
                    // Specular component (Blinn-Phong)
                    float3 viewDir = -rayDir;
                    float3 halfDir = normalize(lightDir + viewDir);
                    float shininess = max(1.0, 128.0 * (1.0 - roughness));
                    float spec = pow(max(0.0, dot(normal, halfDir)), shininess);
                    specular += light.color.rgb * light.intensity * spec * (1.0 - roughness) * 0.3 * attenuation * shadow.visibility;
                }
            }
        }
    }
    else
    {
        // Fallback: use Scene.LightPosition for backward compatibility
        float3 lightDir = normalize(Scene.LightPosition - hitPosition);
        float lightDist = length(Scene.LightPosition - hitPosition);
        
        // Create temporary light data for soft shadow calculation
        LightData fallbackLight;
        fallbackLight.position = Scene.LightPosition;
        fallbackLight.intensity = Scene.LightIntensity;
        fallbackLight.color = Scene.LightColor;
        fallbackLight.type = LIGHT_TYPE_POINT;
        fallbackLight.radius = 0.0;  // Hard shadow for fallback
        fallbackLight.softShadowSamples = 1.0;
        fallbackLight.padding = 0.0;
        
        SoftShadowResult shadow = CalculateSoftShadow(hitPosition, normal, fallbackLight, seed);
        
        if (shadow.visibility > 0.0)
        {
            float diff = max(0.0, dot(normal, lightDir));
            diffuse = color.rgb * diff * 0.8 * shadow.visibility;
            diffuseNoShadow = color.rgb * diff * 0.8;
            
            float3 viewDir = -rayDir;
            float3 halfDir = normalize(lightDir + viewDir);
            float shininess = max(1.0, 128.0 * (1.0 - roughness));
            float spec = pow(max(0.0, dot(normal, halfDir)), shininess);
            specular = Scene.LightColor.rgb * Scene.LightIntensity * spec * (1.0 - roughness) * 0.3 * shadow.visibility;
        }
    }
    
    payload.color = saturate(ambient + diffuse + specular);
    
    // NRD outputs for diffuse (primary rays only)
    if (payload.depth == 0)
    {
        float hitDistance = RayTCurrent();
        // Output UNSHADOWED full lighting (diffuse + specular) WITH albedo
        // This will be used as base image, SIGMA shadow will be applied in Composite
        // Include specular so metal surfaces look correct
        payload.diffuseRadiance = (ambient + diffuseNoShadow) * color.rgb + specular;
        payload.specularRadiance = specular;
        payload.hitDistance = hitDistance;
        payload.worldNormal = normal;
        payload.roughness = roughness;
        payload.worldPosition = hitPosition;
        payload.viewZ = hitDistance;
        payload.metallic = metallic;
        payload.albedo = color.rgb;
        
        // SIGMA shadow input (single primary light)
        SoftShadowResult sigmaShadow;
        if (GetPrimaryShadowForSigma(hitPosition, normal, seed, sigmaShadow))
        {
            // Use direct shadow visibility from ray trace
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

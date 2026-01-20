// Diffuse Material Closest Hit Shader
// Optimized for opaque, non-metallic surfaces
// Reduces warp divergence by separating material types
// Now includes caustic rendering via photon mapping

#include "Common.hlsli"

// Calculate caustics from photon map
float3 CalculateCaustics(float3 hitPosition, float3 normal)
{
    // Only calculate if photon map has photons
    if (Scene.PhotonMapSize == 0)
        return float3(0, 0, 0);
    
    return GatherPhotons(hitPosition, normal, Scene.PhotonRadius);
}

struct DiffuseLightingResult
{
    float3 shadowed;
    float3 unshadowed;
};

// Calculate lighting for diffuse material with soft shadows
DiffuseLightingResult CalculateDiffuseLighting(float3 hitPosition, float3 normal, float3 objectColor, float roughness, inout uint seed)
{
    DiffuseLightingResult result;
    result.shadowed = float3(0, 0, 0);
    result.unshadowed = float3(0, 0, 0);
    
    // Base ambient
    result.shadowed += objectColor * 0.1;
    result.unshadowed += objectColor * 0.1;
    
    // Process all lights
    for (uint i = 0; i < Scene.NumLights; i++)
    {
        LightData light = Lights[i];
        
        if (light.type == LIGHT_TYPE_AMBIENT)
        {
            result.shadowed += objectColor * light.color.rgb * light.intensity;
            result.unshadowed += objectColor * light.color.rgb * light.intensity;
        }
        else if (light.type == LIGHT_TYPE_DIRECTIONAL)
        {
            float3 lightDir = normalize(-light.position);
            
            // Calculate soft shadow visibility
            SoftShadowResult shadow = CalculateSoftShadow(hitPosition, normal, light, seed);
            
            if (shadow.visibility > 0.0)
            {
                float diff = max(0.0, dot(normal, lightDir));
                result.shadowed += objectColor * light.color.rgb * light.intensity * diff * shadow.visibility;
                
                float3 viewDir = normalize(Scene.CameraPosition - hitPosition);
                float3 reflectDir = reflect(-lightDir, normal);
                float spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);
                result.shadowed += light.color.rgb * light.intensity * spec * 0.3 * (1.0 - roughness) * shadow.visibility;
            }
            
            // Unshadowed lighting for SIGMA input
            float diffNoShadow = max(0.0, dot(normal, lightDir));
            result.unshadowed += objectColor * light.color.rgb * light.intensity * diffNoShadow;
            
            float3 viewDirNoShadow = normalize(Scene.CameraPosition - hitPosition);
            float3 reflectDirNoShadow = reflect(-lightDir, normal);
            float specNoShadow = pow(max(0.0, dot(viewDirNoShadow, reflectDirNoShadow)), 32.0);
            result.unshadowed += light.color.rgb * light.intensity * specNoShadow * 0.3 * (1.0 - roughness);
        }
        else // LIGHT_TYPE_POINT
        {
            float3 lightDir = normalize(light.position - hitPosition);
            float lightDist = length(light.position - hitPosition);
            
            // Calculate soft shadow visibility
            SoftShadowResult shadow = CalculateSoftShadow(hitPosition, normal, light, seed);
            
            if (shadow.visibility > 0.0)
            {
                float attenuation = 1.0 / (1.0 + lightDist * lightDist * 0.01);
                float diff = max(0.0, dot(normal, lightDir));
                result.shadowed += objectColor * light.color.rgb * light.intensity * diff * attenuation * shadow.visibility;
                
                float3 viewDir = normalize(Scene.CameraPosition - hitPosition);
                float3 reflectDir = reflect(-lightDir, normal);
                float spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);
                result.shadowed += light.color.rgb * light.intensity * spec * 0.3 * attenuation * (1.0 - roughness) * shadow.visibility;
            }
            
            // Unshadowed lighting for SIGMA input
            float attenuationNoShadow = 1.0 / (1.0 + lightDist * lightDist * 0.01);
            float diffNoShadow = max(0.0, dot(normal, lightDir));
            result.unshadowed += objectColor * light.color.rgb * light.intensity * diffNoShadow * attenuationNoShadow;
            
            float3 viewDirNoShadow = normalize(Scene.CameraPosition - hitPosition);
            float3 reflectDirNoShadow = reflect(-lightDir, normal);
            float specNoShadow = pow(max(0.0, dot(viewDirNoShadow, reflectDirNoShadow)), 32.0);
            result.unshadowed += light.color.rgb * light.intensity * specNoShadow * 0.3 * attenuationNoShadow * (1.0 - roughness);
        }
    }
    
    // Fallback lighting
    if (Scene.NumLights == 0)
    {
        float3 lightDir = normalize(Scene.LightPosition - hitPosition);
        float diff = max(0.0, dot(normal, lightDir));
        result.shadowed += objectColor * Scene.LightColor.rgb * Scene.LightIntensity * diff;
        result.unshadowed += objectColor * Scene.LightColor.rgb * Scene.LightIntensity * diff;
    }
    
    return result;
}

// Calculate lighting with caustics for diffuse material
DiffuseLightingResult CalculateDiffuseLightingWithCaustics(float3 hitPosition, float3 normal, float3 objectColor, float roughness, inout uint seed)
{
    // Standard diffuse lighting with soft shadows
    DiffuseLightingResult result = CalculateDiffuseLighting(hitPosition, normal, objectColor, roughness, seed);
    
    // Add caustics from photon map
    float3 caustics = CalculateCaustics(hitPosition, normal);
    result.shadowed += objectColor * caustics;
    result.unshadowed += objectColor * caustics;
    
    return result;
}

[shader("closesthit")]
void ClosestHit_Diffuse(inout RayPayload payload, in ProceduralAttributes attribs)
{
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
    float hitDistance = RayTCurrent();
    float3 normal = attribs.normal;
    uint objectType = attribs.objectType;
    uint objectIndex = attribs.objectIndex;
    
    // Generate random seed for soft shadow sampling
    // Use hit position and ray direction to create unique seed per pixel/ray
    uint seed = asuint(hitPosition.x * 1000.0) ^ asuint(hitPosition.y * 2000.0) ^ asuint(hitPosition.z * 3000.0);
    seed = WangHash(seed + payload.depth * 7919);
    
    // Get material properties
    float4 color;
    float metallic = 0.0;
    float roughness = 0.5;
    float transmission = 0.0;
    float ior = 1.5;
    
    if (objectType == OBJECT_TYPE_SPHERE)
    {
        SphereData sphere = Spheres[objectIndex];
        color = sphere.color;
        metallic = sphere.metallic;
        roughness = sphere.roughness;
    }
    else if (objectType == OBJECT_TYPE_PLANE)
    {
        PlaneData plane = Planes[objectIndex];
        color = plane.color;
        metallic = plane.metallic;
        roughness = plane.roughness;

        // Checkerboard pattern for floor (world space coordinates)
        // Use hitPosition.xz directly for horizontal floor
        float2 uv = hitPosition.xz;
        
        // Use bitwise AND for correct handling of negative coordinates
        int ix = (int)floor(uv.x);
        int iy = (int)floor(uv.y);
        float checker = (float)((ix + iy) & 1);
        
        // Distance-based contrast reduction to reduce aliasing (pseudo-MIP filtering)
        // Far away checkers fade to gray, reducing high-frequency aliasing
        float fadeStart = 10.0;   // Distance where fading begins
        float fadeEnd = 100.0;    // Distance where contrast is minimum
        float distFactor = saturate((hitDistance - fadeStart) / (fadeEnd - fadeStart));
        float contrast = lerp(1.0, 0.2, distFactor);  // 1.0 = full contrast, 0.2 = low contrast at distance
        
        // Apply contrast: lerp between gray (0.5) and checker pattern
        float checkerValue = lerp(0.5, checker, contrast);
        
        // Map checker value to color range (0.1 to 0.9)
        float colorValue = lerp(0.1, 0.9, checkerValue);
        color.rgb = float3(colorValue, colorValue, colorValue);
    }
    else
    {
        BoxData box = Boxes[objectIndex];
        color = box.color;
        metallic = box.metallic;
        roughness = box.roughness;
    }
    
    // Calculate diffuse lighting with caustics and soft shadows
    // IMPORTANT: Pass white (1,1,1) as objectColor to get RADIANCE ONLY (no albedo)
    // This is required for proper NRD denoising - albedo is multiplied AFTER denoising
    DiffuseLightingResult lighting = CalculateDiffuseLightingWithCaustics(hitPosition, normal, float3(1.0, 1.0, 1.0), roughness, seed);
    float3 diffuseLighting = lighting.shadowed;           // Radiance with shadows
    float3 diffuseLightingNoShadow = lighting.unshadowed; // Radiance without shadows (for NRD)
    float3 diffuseColor = diffuseLighting * color.rgb;    // Final color = radiance * albedo
    float3 specularColor = float3(0, 0, 0);
    
    // Variables for NRD specular output (need to be in outer scope)
    float fresnel = 0.0;
    RayPayload reflectPayload;
    reflectPayload.diffuseRadiance = float3(0, 0, 0);
    reflectPayload.specularRadiance = float3(0, 0, 0);
    reflectPayload.albedo = float3(0, 0, 0);
    
    // Subtle Fresnel reflection for dielectrics
    if (payload.depth < MAX_RECURSION_DEPTH)
    {
        float f0 = 0.04;
        float3 viewDir = -WorldRayDirection();
        float cosTheta = max(0.0, dot(viewDir, normal));
        fresnel = FresnelSchlick(cosTheta, f0);
        
        if (fresnel > 0.05)
        {
            float3 reflectDir = reflect(WorldRayDirection(), normal);
            RayDesc reflectRay;
            reflectRay.Origin = hitPosition + normal * 0.001;
            reflectRay.Direction = reflectDir;
            reflectRay.TMin = 0.001;
            reflectRay.TMax = 10000.0;
            
            reflectPayload.color = float3(0, 0, 0);
            reflectPayload.depth = payload.depth + 1;
            reflectPayload.hit = false;
            reflectPayload.padding = 0.0;
            // Initialize NRD fields for recursive calls
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
            
            // Specular contribution from reflection (for display)
            specularColor = reflectPayload.color * fresnel * (1.0 - roughness);
        }
    }
    
    float3 finalColor = diffuseColor + specularColor;
    
    // Set payload outputs
    payload.color = saturate(finalColor);
    payload.hit = true;
    
    // NRD-specific outputs (only for primary rays, depth == 0)
    if (payload.depth == 0)
    {
        // Store RADIANCE ONLY (no albedo) for NRD denoising
        // Albedo will be multiplied AFTER denoising in Composite shader
        // This is critical for NRD quality - never denoise albedo-multiplied color
        payload.diffuseRadiance = diffuseLightingNoShadow;  // Pure radiance, no albedo
        
        // ========================================
        // Direct Light Specular (Blinn-Phong) for NRD
        // This is ESSENTIAL - without it, t1 (DenoisedSpecular) will be black!
        // ========================================
        float3 directSpecularRadiance = float3(0, 0, 0);
        float3 V = normalize(-WorldRayDirection());
        
        // Dielectric F0
        float3 F0 = float3(0.04, 0.04, 0.04);
        float VdotN = saturate(dot(V, normal));
        float3 F = F0 + (1.0 - F0) * pow(1.0 - VdotN, 5.0);
        
        // roughness -> shininess
        float shininess = lerp(256.0, 8.0, roughness);
        
        // Calculate specular from all lights
        for (uint i = 0; i < Scene.NumLights; i++)
        {
            LightData light = Lights[i];
            
            if (light.type == LIGHT_TYPE_DIRECTIONAL)
            {
                float3 L = normalize(-light.position);
                float3 H = normalize(L + V);
                
                float NdotL = saturate(dot(normal, L));
                float NdotH = saturate(dot(normal, H));
                
                float specTerm = pow(NdotH, shininess) * NdotL;
                directSpecularRadiance += light.color.rgb * light.intensity * specTerm * F;
            }
            else if (light.type == LIGHT_TYPE_POINT)
            {
                float3 toLight = light.position - hitPosition;
                float lightDist = length(toLight);
                float3 L = toLight / lightDist;
                float3 H = normalize(L + V);
                
                float NdotL = saturate(dot(normal, L));
                float NdotH = saturate(dot(normal, H));
                float attenuation = 1.0 / (1.0 + lightDist * lightDist * 0.01);
                
                float specTerm = pow(NdotH, shininess) * NdotL * attenuation;
                directSpecularRadiance += light.color.rgb * light.intensity * specTerm * F;
            }
        }
        
        // Fallback light if no lights defined
        if (Scene.NumLights == 0)
        {
            float3 toLight = Scene.LightPosition - hitPosition;
            float lightDist = length(toLight);
            float3 L = toLight / lightDist;
            float3 H = normalize(L + V);
            
            float NdotL = saturate(dot(normal, L));
            float NdotH = saturate(dot(normal, H));
            
            float specTerm = pow(NdotH, shininess) * NdotL;
            directSpecularRadiance += Scene.LightColor.rgb * Scene.LightIntensity * specTerm * F;
        }
        
        // ========================================
        // Reflection-based Specular (environment reflection)
        // ========================================
        float3 reflectionSpecularRadiance = float3(0, 0, 0);
        if (fresnel > 0.01 && roughness < 0.9)
        {
            // Use reflected radiance (not color!) to preserve HDR and avoid albedo mixing
            float3 reflectedRadiance = reflectPayload.diffuseRadiance * reflectPayload.albedo 
                                     + reflectPayload.specularRadiance;
            reflectionSpecularRadiance = reflectedRadiance * fresnel * (1.0 - roughness);
        }
        
        // Combine direct + reflection specular
        // NOTE: ×4 boost is now applied in RayGen for easier tuning
        payload.specularRadiance = directSpecularRadiance + reflectionSpecularRadiance;
        payload.hitDistance = hitDistance;
        
        // Specular hit distance: use reflection ray distance if available, else primary
        // NRD needs the hit distance of the specular source (reflection ray)
        if (fresnel > 0.01 && roughness < 0.9 && reflectPayload.hit)
        {
            payload.specularHitDistance = reflectPayload.hitDistance;
        }
        else
        {
            // Direct specular only - use primary ray hit distance
            payload.specularHitDistance = hitDistance;
        }
        
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
            // Use lighting ratio to ensure visible shadow signal
            float shadowVisibilityFromLighting = 1.0;
            float denom = max(0.001, Luminance(diffuseLightingNoShadow));
            shadowVisibilityFromLighting = saturate(Luminance(diffuseLighting) / denom);
            payload.shadowVisibility = shadowVisibilityFromLighting;
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

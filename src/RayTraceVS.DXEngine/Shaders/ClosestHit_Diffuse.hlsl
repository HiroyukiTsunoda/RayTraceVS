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

// Calculate lighting for diffuse material
float3 CalculateDiffuseLighting(float3 hitPosition, float3 normal, float3 objectColor, float roughness)
{
    float3 finalColor = float3(0, 0, 0);
    
    // Base ambient
    finalColor += objectColor * 0.1;
    
    // Process all lights
    for (uint i = 0; i < Scene.NumLights; i++)
    {
        LightData light = Lights[i];
        
        if (light.type == LIGHT_TYPE_AMBIENT)
        {
            finalColor += objectColor * light.color.rgb * light.intensity;
        }
        else if (light.type == LIGHT_TYPE_DIRECTIONAL)
        {
            float3 lightDir = normalize(-light.position);
            
            // Shadow ray
            RayDesc shadowRay;
            shadowRay.Origin = hitPosition + normal * 0.001;
            shadowRay.Direction = lightDir;
            shadowRay.TMin = 0.001;
            shadowRay.TMax = 10000.0;
            
            RayPayload shadowPayload;
            shadowPayload.color = float3(0, 0, 0);
            shadowPayload.depth = MAX_RECURSION_DEPTH;
            shadowPayload.hit = false;
            
            TraceRay(SceneBVH, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER,
                     0xFF, 0, 0, 0, shadowRay, shadowPayload);
            
            if (!shadowPayload.hit)
            {
                float diff = max(0.0, dot(normal, lightDir));
                finalColor += objectColor * light.color.rgb * light.intensity * diff;
                
                float3 viewDir = normalize(Scene.CameraPosition - hitPosition);
                float3 reflectDir = reflect(-lightDir, normal);
                float spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);
                finalColor += light.color.rgb * light.intensity * spec * 0.3 * (1.0 - roughness);
            }
        }
        else // LIGHT_TYPE_POINT
        {
            float3 lightDir = normalize(light.position - hitPosition);
            float lightDist = length(light.position - hitPosition);
            
            RayDesc shadowRay;
            shadowRay.Origin = hitPosition + normal * 0.001;
            shadowRay.Direction = lightDir;
            shadowRay.TMin = 0.001;
            shadowRay.TMax = lightDist;
            
            RayPayload shadowPayload;
            shadowPayload.color = float3(0, 0, 0);
            shadowPayload.depth = MAX_RECURSION_DEPTH;
            shadowPayload.hit = false;
            
            TraceRay(SceneBVH, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER,
                     0xFF, 0, 0, 0, shadowRay, shadowPayload);
            
            if (!shadowPayload.hit)
            {
                float attenuation = 1.0 / (1.0 + lightDist * lightDist * 0.01);
                float diff = max(0.0, dot(normal, lightDir));
                finalColor += objectColor * light.color.rgb * light.intensity * diff * attenuation;
                
                float3 viewDir = normalize(Scene.CameraPosition - hitPosition);
                float3 reflectDir = reflect(-lightDir, normal);
                float spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);
                finalColor += light.color.rgb * light.intensity * spec * 0.3 * attenuation * (1.0 - roughness);
            }
        }
    }
    
    // Fallback lighting
    if (Scene.NumLights == 0)
    {
        float3 lightDir = normalize(Scene.LightPosition - hitPosition);
        float diff = max(0.0, dot(normal, lightDir));
        finalColor += objectColor * Scene.LightColor.rgb * Scene.LightIntensity * diff;
    }
    
    return finalColor;
}

// Calculate lighting with caustics for diffuse material
float3 CalculateDiffuseLightingWithCaustics(float3 hitPosition, float3 normal, float3 objectColor, float roughness)
{
    // Standard diffuse lighting
    float3 finalColor = CalculateDiffuseLighting(hitPosition, normal, objectColor, roughness);
    
    // Add caustics from photon map
    float3 caustics = CalculateCaustics(hitPosition, normal);
    finalColor += objectColor * caustics;
    
    return finalColor;
}

[shader("closesthit")]
void ClosestHit_Diffuse(inout RayPayload payload, in ProceduralAttributes attribs)
{
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
    float hitDistance = RayTCurrent();
    float3 normal = attribs.normal;
    uint objectType = attribs.objectType;
    uint objectIndex = attribs.objectIndex;
    
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
    }
    else
    {
        BoxData box = Boxes[objectIndex];
        color = box.color;
        metallic = box.metallic;
        roughness = box.roughness;
    }
    
    // Calculate diffuse lighting with caustics
    float3 diffuseColor = CalculateDiffuseLightingWithCaustics(hitPosition, normal, color.rgb, roughness);
    float3 specularColor = float3(0, 0, 0);
    
    // Subtle Fresnel reflection for dielectrics
    if (payload.depth < MAX_RECURSION_DEPTH)
    {
        float f0 = 0.04;
        float3 viewDir = -WorldRayDirection();
        float cosTheta = max(0.0, dot(viewDir, normal));
        float fresnel = FresnelSchlick(cosTheta, f0);
        
        if (fresnel > 0.05)
        {
            float3 reflectDir = reflect(WorldRayDirection(), normal);
            RayDesc reflectRay;
            reflectRay.Origin = hitPosition + normal * 0.001;
            reflectRay.Direction = reflectDir;
            reflectRay.TMin = 0.001;
            reflectRay.TMax = 10000.0;
            
            RayPayload reflectPayload;
            reflectPayload.color = float3(0, 0, 0);
            reflectPayload.depth = payload.depth + 1;
            reflectPayload.hit = false;
            reflectPayload.padding = 0.0;
            // Initialize NRD fields for recursive calls
            reflectPayload.diffuseRadiance = float3(0, 0, 0);
            reflectPayload.specularRadiance = float3(0, 0, 0);
            reflectPayload.hitDistance = 10000.0;
            reflectPayload.worldNormal = float3(0, 1, 0);
            reflectPayload.roughness = 1.0;
            reflectPayload.worldPosition = float3(0, 0, 0);
            reflectPayload.viewZ = 10000.0;
            reflectPayload.metallic = 0.0;
            reflectPayload.albedo = float3(0, 0, 0);
            
            TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, reflectRay, reflectPayload);
            
            // Specular contribution from reflection
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
        payload.diffuseRadiance = diffuseColor;
        payload.specularRadiance = specularColor;
        payload.hitDistance = hitDistance;
        payload.worldNormal = normal;
        payload.roughness = roughness;
        payload.worldPosition = hitPosition;
        payload.viewZ = hitDistance;
        payload.metallic = metallic;
        payload.albedo = color.rgb;
    }
}

// Diffuse Material Closest Hit Shader
// Optimized for opaque, non-metallic surfaces
// Reduces warp divergence by separating material types

#include "Common.hlsli"

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

[shader("closesthit")]
void ClosestHit_Diffuse(inout RayPayload payload, in ProceduralAttributes attribs)
{
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
    float3 normal = attribs.normal;
    uint objectType = attribs.objectType;
    uint objectIndex = attribs.objectIndex;
    
    // Get material properties
    float4 color;
    float metallic, roughness, transmission, ior;
    
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
        CylinderData cyl = Cylinders[objectIndex];
        color = cyl.color;
        roughness = cyl.roughness;
    }
    
    // Calculate diffuse lighting
    float3 finalColor = CalculateDiffuseLighting(hitPosition, normal, color.rgb, roughness);
    
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
            
            TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, reflectRay, reflectPayload);
            
            finalColor = lerp(finalColor, reflectPayload.color, fresnel * (1.0 - roughness));
        }
    }
    
    payload.color = saturate(finalColor);
    payload.hit = true;
}

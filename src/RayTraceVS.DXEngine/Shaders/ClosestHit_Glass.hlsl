// Glass Material Closest Hit Shader
// Optimized for transparent, refractive surfaces
// Reduces warp divergence by separating material types

#include "Common.hlsli"

// Refract ray using Snell's law
float3 RefractRay(float3 incident, float3 normal, float eta)
{
    float cosI = -dot(incident, normal);
    float sin2T = eta * eta * (1.0 - cosI * cosI);
    
    if (sin2T > 1.0)
        return float3(0, 0, 0);  // Total internal reflection
    
    float cosT = sqrt(1.0 - sin2T);
    return eta * incident + (eta * cosI - cosT) * normal;
}

// Calculate basic surface lighting
float3 CalculateGlassLighting(float3 hitPosition, float3 normal, float3 objectColor)
{
    float3 finalColor = objectColor * 0.1;  // Ambient
    
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
            finalColor += objectColor * light.color.rgb * light.intensity * diff * attenuation * 0.3;
            
            // Strong specular for glass
            float3 viewDir = normalize(Scene.CameraPosition - hitPosition);
            float3 reflectDir = reflect(-lightDir, normal);
            float spec = pow(max(0.0, dot(viewDir, reflectDir)), 64.0);
            finalColor += light.color.rgb * light.intensity * spec * 0.5 * attenuation;
        }
    }
    
    return finalColor;
}

[shader("closesthit")]
void ClosestHit_Glass(inout RayPayload payload, in ProceduralAttributes attribs)
{
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
    float3 normal = attribs.normal;
    uint objectType = attribs.objectType;
    uint objectIndex = attribs.objectIndex;
    
    // Get material properties
    float4 color;
    float transmission, ior;
    
    if (objectType == OBJECT_TYPE_SPHERE)
    {
        SphereData sphere = Spheres[objectIndex];
        color = sphere.color;
        transmission = sphere.transmission;
        ior = sphere.ior;
    }
    else if (objectType == OBJECT_TYPE_PLANE)
    {
        PlaneData plane = Planes[objectIndex];
        color = plane.color;
        transmission = plane.transmission;
        ior = plane.ior;
    }
    else
    {
        CylinderData cyl = Cylinders[objectIndex];
        color = cyl.color;
        transmission = cyl.transmission;
        ior = cyl.ior;
    }
    
    float3 objectColor = color.rgb;
    float3 finalColor = float3(0, 0, 0);
    
    if (payload.depth < MAX_RECURSION_DEPTH)
    {
        float3 V = -WorldRayDirection();
        bool frontFace = dot(V, normal) > 0;
        float3 outwardNormal = frontFace ? normal : -normal;
        
        // ============================================
        // Refraction (transmitted color)
        // ============================================
        float3 transmittedColor = float3(0, 0, 0);
        
        float eta = frontFace ? (1.0 / ior) : ior;
        float3 refractDir = RefractRay(WorldRayDirection(), outwardNormal, eta);
        
        if (length(refractDir) > 0.001)
        {
            RayDesc refractRay;
            refractRay.Origin = hitPosition - outwardNormal * 0.01;
            refractRay.Direction = normalize(refractDir);
            refractRay.TMin = 0.001;
            refractRay.TMax = 10000.0;
            
            RayPayload refractPayload;
            refractPayload.color = float3(0, 0, 0);
            refractPayload.depth = payload.depth + 1;
            refractPayload.hit = false;
            
            TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, refractRay, refractPayload);
            transmittedColor = refractPayload.color;
        }
        else
        {
            // Total internal reflection - get sky color
            transmittedColor = GetSkyColor(WorldRayDirection());
        }
        
        // ============================================
        // Surface color (opaque contribution)
        // ============================================
        float3 opaqueColor = CalculateGlassLighting(hitPosition, normal, objectColor);
        
        // ============================================
        // Blend based on transparency
        // ============================================
        finalColor = lerp(opaqueColor, transmittedColor, transmission);
        
        // ============================================
        // Fresnel reflection
        // ============================================
        if (ior > 1.01)
        {
            float f0 = pow((1.0 - ior) / (1.0 + ior), 2.0);
            float cosTheta = abs(dot(V, outwardNormal));
            float fresnel = FresnelSchlick(cosTheta, f0);
            
            if (fresnel > 0.01)
            {
                float3 reflectDir = reflect(WorldRayDirection(), outwardNormal);
                
                RayDesc reflectRay;
                reflectRay.Origin = hitPosition + outwardNormal * 0.01;
                reflectRay.Direction = reflectDir;
                reflectRay.TMin = 0.001;
                reflectRay.TMax = 10000.0;
                
                RayPayload reflectPayload;
                reflectPayload.color = float3(0, 0, 0);
                reflectPayload.depth = payload.depth + 1;
                reflectPayload.hit = false;
                
                TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, reflectRay, reflectPayload);
                
                // Blend reflection based on Fresnel and transparency
                float reflectBlend = fresnel * (1.0 - transmission * 0.5);
                finalColor = lerp(finalColor, reflectPayload.color, reflectBlend);
            }
        }
    }
    else
    {
        // Max depth reached - use surface lighting only
        finalColor = CalculateGlassLighting(hitPosition, normal, objectColor);
    }
    
    payload.color = saturate(finalColor);
    payload.hit = true;
}

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

[shader("closesthit")]
void ClosestHit_Metal(inout RayPayload payload, in ProceduralAttributes attribs)
{
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
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
        CylinderData cyl = Cylinders[objectIndex];
        color = cyl.color;
        roughness = cyl.roughness;
    }
    
    float3 objectColor = color.rgb;
    float3 finalColor = float3(0, 0, 0);
    
    if (payload.depth < MAX_RECURSION_DEPTH)
    {
        float3 viewDir = -WorldRayDirection();
        float cosTheta = max(0.0, dot(viewDir, normal));
        
        // Metal F0 is the base color
        float3 f0 = objectColor;
        float3 fresnel = f0 + (1.0 - f0) * pow(1.0 - cosTheta, 5.0);
        
        // Reflection with roughness perturbation
        float3 reflectDir = reflect(WorldRayDirection(), normal);
        float2 seed = hitPosition.xy * 1000.0 + float2(payload.depth, payload.depth * 0.5);
        float3 perturbedDir = PerturbReflection(reflectDir, normal, roughness, seed);
        
        RayDesc reflectRay;
        reflectRay.Origin = hitPosition + normal * 0.001;
        reflectRay.Direction = perturbedDir;
        reflectRay.TMin = 0.001;
        reflectRay.TMax = 10000.0;
        
        RayPayload reflectPayload;
        reflectPayload.color = float3(0, 0, 0);
        reflectPayload.depth = payload.depth + 1;
        reflectPayload.hit = false;
        
        TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, reflectRay, reflectPayload);
        
        // Metal reflection tinted by base color
        finalColor = reflectPayload.color * fresnel;
        
        // Blend with diffuse for rough metals
        if (roughness > 0.1)
        {
            float3 diffuse = CalculateMetalLighting(hitPosition, normal, objectColor);
            float diffuseBlend = roughness * roughness;
            finalColor = lerp(finalColor, diffuse * objectColor, diffuseBlend * 0.5);
        }
    }
    else
    {
        // Max depth reached - use diffuse lighting
        finalColor = CalculateMetalLighting(hitPosition, normal, objectColor);
    }
    
    payload.color = saturate(finalColor);
    payload.hit = true;
}

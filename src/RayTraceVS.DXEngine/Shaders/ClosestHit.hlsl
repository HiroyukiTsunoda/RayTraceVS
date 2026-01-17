#include "Common.hlsli"

// Get material properties from the appropriate buffer based on object type
void GetMaterialProperties(uint objectType, uint objectIndex, out float4 color, out float metallic, 
                           out float roughness, out float transmission, out float ior)
{
    if (objectType == OBJECT_TYPE_SPHERE)
    {
        SphereData sphere = Spheres[objectIndex];
        color = sphere.color;
        metallic = sphere.metallic;
        roughness = sphere.roughness;
        transmission = sphere.transmission;
        ior = sphere.ior;
    }
    else if (objectType == OBJECT_TYPE_PLANE)
    {
        PlaneData plane = Planes[objectIndex];
        color = plane.color;
        metallic = plane.metallic;
        roughness = plane.roughness;
        transmission = plane.transmission;
        ior = plane.ior;
    }
    else // OBJECT_TYPE_CYLINDER
    {
        CylinderData cyl = Cylinders[objectIndex];
        color = cyl.color;
        metallic = cyl.metallic;
        roughness = cyl.roughness;
        transmission = cyl.transmission;
        ior = cyl.ior;
    }
}

// Shadow ray marker - use a large value to distinguish from material recursion
#define SHADOW_RAY_DEPTH 100

// Hash function for random numbers
float HashFloat(float2 p)
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
    
    // Generate random values
    float r1 = HashFloat(seed);
    float r2 = HashFloat(seed + float2(17.3, 31.7));
    
    // Create tangent space basis
    float3 tangent = abs(normal.x) > 0.9 ? float3(0, 1, 0) : float3(1, 0, 0);
    tangent = normalize(cross(normal, tangent));
    float3 bitangent = cross(normal, tangent);
    
    // Random offset scaled by roughness (GGX-like distribution approximation)
    float angle = r1 * 6.28318;
    float radius = roughness * roughness * r2;
    
    float3 offset = (cos(angle) * tangent + sin(angle) * bitangent) * radius;
    
    // Perturb reflection direction
    float3 perturbed = normalize(reflectDir + offset);
    
    // Ensure perturbed direction is above surface
    if (dot(perturbed, normal) < 0.0)
        perturbed = reflect(perturbed, normal);
    
    return perturbed;
}

// Cast shadow ray and return true if in shadow
bool IsInShadow(float3 hitPosition, float3 normal, float3 lightDir, float maxDist)
{
    RayDesc shadowRay;
    shadowRay.Origin = hitPosition + normal * 0.01;  // Offset to avoid self-intersection
    shadowRay.Direction = lightDir;
    shadowRay.TMin = 0.001;
    shadowRay.TMax = maxDist;
    
    RayPayload shadowPayload;
    shadowPayload.color = float3(0, 0, 0);
    shadowPayload.depth = SHADOW_RAY_DEPTH;  // Mark as shadow ray
    shadowPayload.hit = false;
    
    // Don't use RAY_FLAG_SKIP_CLOSEST_HIT_SHADER - we need ClosestHit to set the hit flag
    TraceRay(SceneBVH, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH,
             0xFF, 0, 0, 0, shadowRay, shadowPayload);
    
    return shadowPayload.hit;
}

// Calculate lighting for a hit point
float3 CalculateLighting(float3 hitPosition, float3 normal, float3 objectColor)
{
    float3 finalColor = float3(0, 0, 0);
    
    // Base ambient
    float3 ambient = objectColor * 0.1;
    finalColor += ambient;
    
    // Process all lights
    for (uint i = 0; i < Scene.NumLights; i++)
    {
        LightData light = Lights[i];
        
        if (light.type == LIGHT_TYPE_AMBIENT)
        {
            // Ambient light
            finalColor += objectColor * light.color.rgb * light.intensity;
        }
        else if (light.type == LIGHT_TYPE_DIRECTIONAL)
        {
            // Directional light
            float3 lightDir = normalize(-light.position);
            
            // Shadow check
            if (!IsInShadow(hitPosition, normal, lightDir, 10000.0))
            {
                // Diffuse
                float diff = max(0.0, dot(normal, lightDir));
                finalColor += objectColor * light.color.rgb * light.intensity * diff;
                
                // Specular
                float3 viewDir = normalize(Scene.CameraPosition - hitPosition);
                float3 reflectDir = reflect(-lightDir, normal);
                float spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);
                finalColor += light.color.rgb * light.intensity * spec * 0.3;
            }
        }
        else // LIGHT_TYPE_POINT
        {
            // Point light
            float3 lightDir = normalize(light.position - hitPosition);
            float lightDist = length(light.position - hitPosition);
            
            // Shadow check
            if (!IsInShadow(hitPosition, normal, lightDir, lightDist))
            {
                float attenuation = 1.0 / (1.0 + lightDist * lightDist * 0.01);
                
                // Diffuse
                float diff = max(0.0, dot(normal, lightDir));
                finalColor += objectColor * light.color.rgb * light.intensity * diff * attenuation;
                
                // Specular
                float3 viewDir = normalize(Scene.CameraPosition - hitPosition);
                float3 reflectDir = reflect(-lightDir, normal);
                float spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);
                finalColor += light.color.rgb * light.intensity * spec * 0.3 * attenuation;
            }
        }
    }
    
    // Fallback if no lights - use default light with shadow
    if (Scene.NumLights == 0)
    {
        float3 lightDir = normalize(Scene.LightPosition - hitPosition);
        float lightDist = length(Scene.LightPosition - hitPosition);
        
        if (!IsInShadow(hitPosition, normal, lightDir, lightDist))
        {
            float diff = max(0.0, dot(normal, lightDir));
            finalColor += objectColor * Scene.LightColor.rgb * Scene.LightIntensity * diff;
        }
    }
    
    return finalColor;
}

[shader("closesthit")]
void ClosestHit(inout RayPayload payload, in ProceduralAttributes attribs)
{
    // Mark as hit immediately
    payload.hit = true;
    
    // If this is a shadow ray (depth >= SHADOW_RAY_DEPTH), just return
    // We only care about the hit flag for shadow rays
    if (payload.depth >= SHADOW_RAY_DEPTH)
    {
        payload.color = float3(0, 0, 0);  // Shadow ray hit - doesn't matter
        return;
    }
    
    // If recursion depth exceeded, return black
    // Use Scene.MaxBounces if available, otherwise use MAX_RECURSION_DEPTH
    uint maxBounces = (Scene.MaxBounces > 0) ? Scene.MaxBounces : MAX_RECURSION_DEPTH;
    if (payload.depth >= maxBounces)
    {
        payload.color = float3(0, 0, 0);
        return;
    }
    
    // Get hit information
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
    float3 normal = attribs.normal;
    uint objectType = attribs.objectType;
    uint objectIndex = attribs.objectIndex;
    
    // Get material properties
    float4 color;
    float metallic, roughness, transmission, ior;
    GetMaterialProperties(objectType, objectIndex, color, metallic, roughness, transmission, ior);
    
    float3 objectColor = color.rgb;
    
    // Apply checkerboard pattern for planes (white/black like Compute Shader)
    if (objectType == OBJECT_TYPE_PLANE)
    {
        float scale = 1.0;  // Size of checker squares
        int checkX = (int)floor(hitPosition.x * scale);
        int checkZ = (int)floor(hitPosition.z * scale);
        bool isWhite = ((checkX + checkZ) & 1) == 0;
        objectColor = isWhite ? float3(0.9, 0.9, 0.9) : float3(0.1, 0.1, 0.1);
    }
    
    float3 finalColor = float3(0, 0, 0);
    
    // Determine material type
    bool isGlass = transmission > 0.01;
    bool isMetal = metallic > 0.5;
    
    if (isGlass)
    {
        // === GLASS MATERIAL (Compute-style loop-based refraction) ===
        float3 V = -WorldRayDirection();
        float3 N = normal;
        bool frontFace = dot(V, N) > 0;
        float3 outwardNormal = frontFace ? N : -N;
        
        float3 transmittedColor = float3(0, 0, 0);
        
        if (payload.depth < maxBounces)
        {
            // Initial refraction direction
            float3 currentOrigin = hitPosition;
            float3 currentDir = WorldRayDirection();
            
            // Apply initial refraction if entering glass
            if (ior > 1.01)
            {
                float eta = frontFace ? (1.0 / ior) : ior;
                float3 refracted = refract(currentDir, outwardNormal, eta);
                if (length(refracted) > 0.001)
                {
                    currentDir = normalize(refracted);
                }
            }
            
            // Trace through glass surfaces (loop like Compute shader)
            for (uint bounce = 0; bounce < maxBounces && bounce < 8; bounce++)
            {
                // Offset origin along current direction
                currentOrigin = currentOrigin + currentDir * 0.02;
                
                RayDesc nextRay;
                nextRay.Origin = currentOrigin;
                nextRay.Direction = currentDir;
                nextRay.TMin = 0.001;
                nextRay.TMax = 10000.0;
                
                RayPayload nextPayload;
                nextPayload.color = float3(0, 0, 0);
                nextPayload.depth = payload.depth + 1 + bounce;
                nextPayload.hit = false;
                
                TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, nextRay, nextPayload);
                
                if (!nextPayload.hit)
                {
                    // Hit nothing - use sky color
                    transmittedColor = GetSkyColor(currentDir);
                    break;
                }
                else
                {
                    // Hit something - check if it's glass or opaque
                    // For simplicity, use the color from the recursive call
                    // The recursive call will handle glass/opaque distinction
                    transmittedColor = nextPayload.color;
                    break;
                }
            }
        }
        else
        {
            transmittedColor = GetSkyColor(WorldRayDirection());
        }
        
        // Surface color (as if opaque)
        float3 opaqueColor = CalculateLighting(hitPosition, normal, objectColor);
        
        // Blend based on transmission
        finalColor = lerp(opaqueColor, transmittedColor, transmission);
        
        // Fresnel reflection
        if (ior > 1.01 && payload.depth < maxBounces - 1)
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
                
                finalColor = lerp(finalColor, reflectPayload.color, fresnel * (1.0 - transmission * 0.5));
            }
        }
        
        payload.color = saturate(finalColor);
    }
    else if (isMetal && payload.depth < maxBounces)
    {
        // === METAL MATERIAL ===
        float3 viewDir = -WorldRayDirection();
        float cosTheta = max(0.0, dot(viewDir, normal));
        
        // Metal F0 is the base color
        float3 f0 = objectColor;
        float3 fresnel = f0 + (1.0 - f0) * pow(1.0 - cosTheta, 5.0);
        
        // Reflection with roughness perturbation
        float3 reflectDir = reflect(WorldRayDirection(), normal);
        
        // Perturb reflection based on roughness
        float2 seed = hitPosition.xy * 1000.0 + hitPosition.z * 100.0;
        float3 perturbedDir = PerturbReflection(reflectDir, normal, roughness, seed);
        
        RayDesc reflectRay;
        reflectRay.Origin = hitPosition + normal * 0.01;
        reflectRay.Direction = perturbedDir;
        reflectRay.TMin = 0.001;
        reflectRay.TMax = 10000.0;
        
        RayPayload reflectPayload;
        reflectPayload.color = float3(0, 0, 0);
        reflectPayload.depth = payload.depth + 1;
        reflectPayload.hit = false;
        
        TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, reflectRay, reflectPayload);
        
        finalColor = reflectPayload.color * fresnel;
        
        // Blend with diffuse for rough metals (like Compute Shader)
        if (roughness > 0.1)
        {
            float3 diffuse = CalculateLighting(hitPosition, normal, objectColor);
            float diffuseBlend = roughness * roughness;
            finalColor = lerp(finalColor, diffuse * objectColor, diffuseBlend * 0.5);
        }
        
        payload.color = saturate(finalColor);
    }
    else
    {
        // === DIFFUSE MATERIAL ===
        payload.color = saturate(CalculateLighting(hitPosition, normal, objectColor));
    }
}

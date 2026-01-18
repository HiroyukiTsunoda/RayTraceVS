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
        int checker = (ix + iy) & 1;
        color.rgb = checker ? float3(0.9, 0.9, 0.9) : float3(0.1, 0.1, 0.1);
    }
    else
    {
        BoxData box = Boxes[objectIndex];
        color = box.color;
        metallic = box.metallic;
        roughness = box.roughness;
    }
    
    // Calculate diffuse lighting with caustics and soft shadows (separate lighting from albedo)
    DiffuseLightingResult lighting = CalculateDiffuseLightingWithCaustics(hitPosition, normal, float3(1.0, 1.0, 1.0), roughness, seed);
    float3 diffuseLighting = lighting.shadowed;
    float3 diffuseLightingNoShadow = lighting.unshadowed;
    float3 diffuseColor = diffuseLighting * color.rgb;
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
            reflectPayload.shadowVisibility = 1.0;
            reflectPayload.shadowPenumbra = 0.0;
            reflectPayload.shadowDistance = NRD_FP16_MAX;
            reflectPayload.targetObjectType = 0;
            reflectPayload.targetObjectIndex = 0;
            reflectPayload.thicknessQuery = 0;
            
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
        // Store the FULL rendered color (with shadows) for composite
        // This is the same as payload.color - the complete scene render
        payload.diffuseRadiance = finalColor;
        payload.specularRadiance = float3(0, 0, 0);  // Already included in diffuseRadiance
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

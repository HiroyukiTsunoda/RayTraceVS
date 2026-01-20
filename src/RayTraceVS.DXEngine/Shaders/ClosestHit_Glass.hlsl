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

// Helper to initialize NRD fields for recursive payloads
void InitializeNRDPayload(inout RayPayload p)
{
    p.diffuseRadiance = float3(0, 0, 0);
    p.specularRadiance = float3(0, 0, 0);
    p.hitDistance = 10000.0;
    p.worldNormal = float3(0, 1, 0);
    p.roughness = 1.0;
    p.worldPosition = float3(0, 0, 0);
    p.viewZ = 10000.0;
    p.metallic = 0.0;
    p.albedo = float3(0, 0, 0);
    p.shadowVisibility = 1.0;
    p.shadowPenumbra = 0.0;
    p.shadowDistance = NRD_FP16_MAX;
    p.targetObjectType = 0;
    p.targetObjectIndex = 0;
    p.thicknessQuery = 0;
}

[shader("anyhit")]
void AnyHit_Glass(inout RayPayload payload, in ProceduralAttributes attribs)
{
    if (payload.thicknessQuery == 0)
    {
        return;
    }
    
    if (attribs.objectType != payload.targetObjectType || attribs.objectIndex != payload.targetObjectIndex)
    {
        IgnoreHit();
    }
}

[shader("closesthit")]
void ClosestHit_Glass(inout RayPayload payload, in ProceduralAttributes attribs)
{
    if (payload.thicknessQuery != 0)
    {
        payload.hit = true;
        payload.hitDistance = RayTCurrent();
        return;
    }
    
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
    float hitDistance = RayTCurrent();
    float3 normal = attribs.normal;
    uint objectType = attribs.objectType;
    uint objectIndex = attribs.objectIndex;
    
    // Get material properties
    float4 color;
    float transmission, ior;
    float roughness = 0.0;
    
    if (objectType == OBJECT_TYPE_SPHERE)
    {
        SphereData sphere = Spheres[objectIndex];
        color = sphere.color;
        transmission = sphere.transmission;
        ior = sphere.ior;
        roughness = sphere.roughness;
    }
    else if (objectType == OBJECT_TYPE_PLANE)
    {
        PlaneData plane = Planes[objectIndex];
        color = plane.color;
        transmission = plane.transmission;
        ior = plane.ior;
        roughness = plane.roughness;
    }
    else
    {
        BoxData box = Boxes[objectIndex];
        color = box.color;
        transmission = box.transmission;
        ior = box.ior;
        roughness = box.roughness;
    }
    
    float3 objectColor = color.rgb;
    float3 finalColor = float3(0, 0, 0);
    float3 specularColor = float3(0, 0, 0);
    float3 transmittedColor = float3(0, 0, 0);  // Moved to function scope for NRD access
    float3 diffuseLighting = CalculateGlassLighting(hitPosition, normal, float3(1.0, 1.0, 1.0));
    float3 diffuseColor = float3(0, 0, 0);
    
    if (payload.depth < MAX_RECURSION_DEPTH)
    {
        float3 V = -WorldRayDirection();
        bool frontFace = dot(V, normal) > 0;
        float3 outwardNormal = frontFace ? normal : -normal;
        
        // ============================================
        // Refraction (transmitted color)
        // ============================================
        
        float eta = frontFace ? (1.0 / ior) : ior;
        float3 refractDir = RefractRay(WorldRayDirection(), outwardNormal, eta);
        
        if (length(refractDir) > 0.001)
        {
            RayDesc refractRay;
            // Use ray direction for offset instead of normal to avoid edge issues
            // At edges, normal-based offset can push the origin to the wrong side
            float3 refractDirNorm = normalize(refractDir);
            refractRay.Origin = hitPosition + refractDirNorm * 0.01;
            refractRay.Direction = refractDirNorm;
            refractRay.TMin = 0.001;
            refractRay.TMax = 10000.0;
            
            RayPayload refractPayload;
            refractPayload.color = float3(0, 0, 0);
            refractPayload.depth = payload.depth + 1;
            refractPayload.hit = false;
            refractPayload.padding = 0.0;
            InitializeNRDPayload(refractPayload);
            
            TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, refractRay, refractPayload);
            
            // If refraction ray missed (hit sky), use sky color explicitly
            // This ensures we never get black from uninitialized payload
            if (refractPayload.hit)
            {
                transmittedColor = refractPayload.color;
                
                // Only apply absorption if ray hit something inside the glass
                RayPayload thicknessPayload;
                thicknessPayload.color = float3(0, 0, 0);
                thicknessPayload.depth = payload.depth + 1;
                thicknessPayload.hit = false;
                thicknessPayload.padding = 0.0;
                InitializeNRDPayload(thicknessPayload);
                thicknessPayload.targetObjectType = objectType;
                thicknessPayload.targetObjectIndex = objectIndex;
                thicknessPayload.thicknessQuery = 1;
                
                TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, refractRay, thicknessPayload);
                
                if (thicknessPayload.hit)
                {
                    float thickness = thicknessPayload.hitDistance;
                    // Use inverted color for absorption (blue glass absorbs red/green, not blue)
                    // Significantly reduced absorption strength to prevent overly dark results
                    // when viewing glass through reflections
                    float3 absorption = (float3(1.0, 1.0, 1.0) - objectColor) * 0.15;
                    float3 transmittance = exp(-absorption * thickness);
                    
                    // Ensure minimum transmittance to prevent complete blackout
                    transmittance = max(transmittance, float3(0.1, 0.1, 0.1));
                    
                    // Apply gentle color tinting based on glass color
                    float3 tint = lerp(float3(1, 1, 1), objectColor, saturate(thickness * 0.1));
                    transmittedColor *= transmittance * tint;
                }
            }
            else
            {
                // Ray missed - use sky color in refraction direction (no absorption for sky)
                transmittedColor = GetSkyColor(refractRay.Direction);
            }
        }
        else
        {
            // Total internal reflection - get sky color
            transmittedColor = GetSkyColor(WorldRayDirection());
        }
        
        // ============================================
        // Surface color (opaque contribution)
        // ============================================
        float3 opaqueColor = diffuseLighting * objectColor;
        diffuseColor = opaqueColor * (1.0 - transmission);
        
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
                // Use reflection direction for offset to avoid edge issues
                reflectRay.Origin = hitPosition + reflectDir * 0.01;
                reflectRay.Direction = reflectDir;
                reflectRay.TMin = 0.001;
                reflectRay.TMax = 10000.0;
                
                RayPayload reflectPayload;
                reflectPayload.color = float3(0, 0, 0);
                reflectPayload.depth = payload.depth + 1;
                reflectPayload.hit = false;
                reflectPayload.padding = 0.0;
                InitializeNRDPayload(reflectPayload);
                
                TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, reflectRay, reflectPayload);
                
                // If reflection ray missed (hit sky), use sky color explicitly
                float3 reflectedColor;
                if (reflectPayload.hit)
                {
                    reflectedColor = reflectPayload.color;
                }
                else
                {
                    reflectedColor = GetSkyColor(reflectDir);
                }
                
                // Blend reflection based on Fresnel and transparency
                float reflectBlend = fresnel * (1.0 - transmission * 0.5);
                specularColor = reflectedColor * reflectBlend;
                finalColor = lerp(finalColor, reflectedColor, reflectBlend);
            }
        }
    }
    else
    {
        // Max depth reached - use approximate transparency with better fallback
        // For transparent glass, approximate both refraction and reflection
        float3 V = -WorldRayDirection();
        bool frontFace = dot(V, normal) > 0;
        float3 outwardNormal = frontFace ? normal : -normal;
        
        // Approximate refraction direction for sky lookup
        float eta = frontFace ? (1.0 / ior) : ior;
        float3 approxRefractDir = RefractRay(WorldRayDirection(), outwardNormal, eta);
        if (length(approxRefractDir) < 0.001)
        {
            approxRefractDir = WorldRayDirection();  // Total internal reflection fallback
        }
        
        // Approximate reflection direction
        float3 reflectDir = reflect(WorldRayDirection(), outwardNormal);
        
        // Get sky colors for approximation
        float3 skyRefraction = GetSkyColor(approxRefractDir);
        float3 skyReflection = GetSkyColor(reflectDir);
        
        // Calculate Fresnel for reflection blend
        float f0 = pow((1.0 - ior) / (1.0 + ior), 2.0);
        float cosTheta = abs(dot(V, outwardNormal));
        float fresnel = FresnelSchlick(cosTheta, f0);
        
        // Apply glass tint to transmitted light (but lighter than full absorption)
        float3 tintedTransmission = skyRefraction * lerp(float3(1, 1, 1), objectColor, 0.5);
        
        // Combine: transmitted light + Fresnel reflection
        float3 approximateTransmitted = tintedTransmission * transmission;
        float3 approximateReflected = skyReflection * fresnel * 0.5;
        
        // Surface lighting contribution (reduced by transmission)
        float3 opaqueColor = diffuseLighting * objectColor * (1.0 - transmission);
        
        // Final blend
        finalColor = opaqueColor + approximateTransmitted + approximateReflected;
        diffuseColor = opaqueColor;
        transmittedColor = approximateTransmitted;
        specularColor = approximateReflected;
    }
    
    payload.color = saturate(finalColor);
    payload.hit = true;
    
    // NRD-specific outputs (only for primary rays)
    // For glass: transmitted color goes to specular (both are view-dependent effects)
    // Reflection (specularColor) + Refraction (transmittedColor * transmission)
    if (payload.depth == 0)
    {
        // Store RADIANCE ONLY (no albedo) for NRD denoising
        // For glass: combine reflection and transmission into specular channel
        // Both are view-dependent effects that NRD should denoise together
        payload.diffuseRadiance = diffuseLighting * (1.0 - transmission);  // Surface lighting (reduced by transmission)
        payload.specularRadiance = specularColor + transmittedColor * transmission;  // Reflection + Refraction
        payload.hitDistance = hitDistance;
        payload.worldNormal = normal;
        payload.roughness = roughness;
        payload.worldPosition = hitPosition;
        payload.viewZ = hitDistance;
        payload.metallic = 0.0;
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

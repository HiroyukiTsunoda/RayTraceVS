// ClosestHit shader - Sphere, Plane, Box
#include "Common.hlsli"

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
    payload.hitDistance = RayTCurrent();
    
    // Store hit object info for caller to check self-intersection
    payload.hitObjectType = attribs.objectType;
    payload.hitObjectIndex = attribs.objectIndex;
    
    // Shadow ray: return material info for colored shadows
    if (payload.depth >= SHADOW_RAY_DEPTH)
    {
        // Get transmission and color for the hit object
        float transmission = 0.0;
        float3 objectColor = float3(1, 1, 1);
        
        if (attribs.objectType == OBJECT_TYPE_SPHERE)
        {
            transmission = Spheres[attribs.objectIndex].transmission;
            objectColor = Spheres[attribs.objectIndex].color.rgb;
        }
        else if (attribs.objectType == OBJECT_TYPE_PLANE)
        {
            transmission = Planes[attribs.objectIndex].transmission;
            objectColor = Planes[attribs.objectIndex].color.rgb;
        }
        else // OBJECT_TYPE_BOX
        {
            transmission = Boxes[attribs.objectIndex].transmission;
            objectColor = Boxes[attribs.objectIndex].color.rgb;
        }
        
        // Store in payload for TraceSingleShadowRay to read
        payload.shadowTransmissionAccum = transmission;
        payload.shadowColorAccum = objectColor;
        return;
    }
    
    // Use scene-specified max bounces (glass needs more for entry/internal/exit)
    uint maxBounces = (Scene.MaxBounces > 0) ? min(Scene.MaxBounces, 8) : 8;
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
    float3 rayDir = WorldRayDirection();
    
    // Normal from Intersection shader (outward)
    float3 normal = normalize(attribs.normal);
    // If normal is already pointing against ray, frontFace should be true for entering
    // But Intersection always returns normal against ray, so we need to check original geometry
    // For now, use the sign of the normal to determine entering/exiting
    
    // Generate random seed for soft shadow sampling
    uint seed = asuint(hitPosition.x * 1000.0) ^ asuint(hitPosition.y * 2000.0) ^ asuint(hitPosition.z * 3000.0);
    seed = WangHash(seed + payload.depth * 7919);
    
    // Get material properties
    float4 color;
    float metallic = 0.0;
    float roughness = 0.5;  // Default roughness
    float transmission = 0.0;
    float ior = 1.5;
    float specular = 0.5;   // Specular intensity
    float3 emission = float3(0.0, 0.0, 0.0);  // Emissive color
    
    if (attribs.objectType == OBJECT_TYPE_SPHERE)
    {
        SphereData s = Spheres[attribs.objectIndex];
        color = s.color;
        metallic = s.metallic;
        roughness = s.roughness;
        transmission = s.transmission;
        ior = s.ior;
        specular = s.specular;
        emission = s.emission;
    }
    else if (attribs.objectType == OBJECT_TYPE_PLANE)
    {
        PlaneData p = Planes[attribs.objectIndex];
        color = p.color;
        metallic = p.metallic;
        roughness = p.roughness;
        transmission = 0.0;
        specular = p.specular;
        emission = p.emission;
        
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
        specular = b.specular;
        emission = b.emission;

        // Recompute box normal from hit position (local space face)
        float3 ax = normalize(b.axisX);
        float3 ay = normalize(b.axisY);
        float3 az = normalize(b.axisZ);
        float3 localHit = float3(dot(hitPosition - b.center, ax),
                                 dot(hitPosition - b.center, ay),
                                 dot(hitPosition - b.center, az));
        float3 d = abs(abs(localHit) - b.size);
        float signX = (localHit.x >= 0.0) ? 1.0 : -1.0;
        float signY = (localHit.y >= 0.0) ? 1.0 : -1.0;
        float signZ = (localHit.z >= 0.0) ? 1.0 : -1.0;
        float3 localNormal;
        if (d.x < d.y && d.x < d.z)
            localNormal = float3(signX, 0, 0);
        else if (d.y < d.z)
            localNormal = float3(0, signY, 0);
        else
            localNormal = float3(0, 0, signZ);
        normal = normalize(ax * localNormal.x + ay * localNormal.y + az * localNormal.z);
    }
    
    // Enforce mutual exclusivity: metals are opaque (no transmission)
    if (metallic >= 0.5)
    {
        transmission = 0.0;
    }
    
    // Treat metal as opaque even if transmission is non-zero
    bool isGlass = (transmission > 0.01) && (metallic < 0.5);
    
    // Final shading normal (ensure it faces the ray)
    float3 N = (dot(rayDir, normal) < 0.0) ? normal : -normal;
    
    // frontFace for glass refraction: check if ray is entering or exiting
    bool frontFace = dot(rayDir, N) < 0;
    
    // Glass (sphere or box) with Fresnel and reflection/refraction
    // ============================================
    // LOOP-BASED: No recursive TraceRay - return next ray info instead
    // ============================================
    if (isGlass && (attribs.objectType == OBJECT_TYPE_SPHERE || attribs.objectType == OBJECT_TYPE_BOX))
    {
        // Use frontFace to determine entering/exiting
        // frontFace = true means ray is entering the object (coming from outside)
        bool entering = frontFace;
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
        
        // Apply roughness perturbation for frosted glass effect
        if (roughness > 0.01)
        {
            float2 roughSeed = hitPosition.xy * 1000.0 + float2(payload.depth, payload.depth * 0.5);
            
            // Perturb reflection direction
            reflectDir = PerturbReflection(reflectDir, N, roughness, roughSeed);
            
            // Perturb refraction direction (use negative normal since refraction goes through)
            if (!tir)
            {
                float2 refractSeed = roughSeed + float2(123.456, 789.012);
                refractDir = PerturbReflection(refractDir, -N, roughness, refractSeed);
            }
        }
        
        float fresnel = tir ? 1.0 : FresnelSchlick(cosTheta, f0);
        
        // ★ Specular highlight for glass surface (computed immediately, not via recursion)
        float3 specularHighlight = float3(0, 0, 0);
        if (specular > 0.01)
        {
            float3 viewDir = -rayDir;
            
            for (uint li = 0; li < Scene.NumLights; li++)
            {
                LightData light = Lights[li];
                if (light.type == LIGHT_TYPE_AMBIENT)
                    continue;
                
                float3 lightDir;
                float attenuation = 1.0;
                
                if (light.type == LIGHT_TYPE_DIRECTIONAL)
                {
                    lightDir = normalize(-light.position);
                }
                else // POINT
                {
                    lightDir = normalize(light.position - hitPosition);
                    float lightDist = length(light.position - hitPosition);
                    attenuation = 1.0 / (1.0 + lightDist * lightDist * 0.01);
                }
                
                float ndotl = max(0.0, dot(N, lightDir));
                if (ndotl > 0.0)
                {
                    float3 halfDir = normalize(lightDir + viewDir);
                    // Glass has high specular exponent (smooth surface)
                    float shininess = max(64.0, 512.0 * (1.0 - roughness));
                    float spec = pow(max(0.0, dot(N, halfDir)), shininess);
                    
                    // Fresnel for specular
                    float specFresnel = FresnelSchlick(max(0.0, dot(halfDir, viewDir)), f0);
                    specularHighlight += light.color.rgb * light.intensity * spec * specFresnel * attenuation;
                }
            }
        }
        
        // Output specular highlight immediately
        payload.color = specularHighlight * specular * (1.0 - roughness);
        
        // ============================================
        // LOOP-BASED: Probabilistically choose reflection OR refraction
        // This eliminates the exponential branching of recursive tracing
        // ============================================
        float randChoice = RandomFloat(seed);
        
        if (tir || randChoice < fresnel)
        {
            // Choose REFLECTION
            payload.nextRayOrigin = hitPosition + N * 0.002;
            payload.nextRayDirection = reflectDir;
            // Box: TMin large enough to escape the box
            payload.nextRayTMin = (attribs.objectType == OBJECT_TYPE_BOX) ? 2.5 : 0.001;
            payload.throughput = float3(1, 1, 1);  // Reflection doesn't tint (white throughput)
            payload.continueTrace = 1.0;
        }
        else
        {
            // Choose REFRACTION
            payload.nextRayOrigin = hitPosition + refractDir * 0.002;
            payload.nextRayDirection = refractDir;
            // Box: TMin large enough to escape
            payload.nextRayTMin = (attribs.objectType == OBJECT_TYPE_BOX) ? 3.0 : 0.001;
            // Tint by glass color (color filter effect)
            float tintStrength = (payload.depth == 0) ? 1.0 : 0.4;
            payload.throughput = lerp(float3(1, 1, 1), color.rgb, tintStrength);
            payload.continueTrace = 1.0;
        }
        
        // NRD outputs for glass (primary rays only)
        if (payload.depth == 0)
        {
            float hitDistance = RayTCurrent();
            payload.diffuseRadiance = payload.color;
            payload.specularRadiance = float3(0, 0, 0);
            payload.hitDistance = hitDistance;
            payload.worldNormal = normal;
            payload.roughness = roughness;
            payload.worldPosition = hitPosition;
            payload.viewZ = hitDistance;
            payload.metallic = 0.0;
            payload.albedo = color.rgb;
            
            // Glass is transparent, so it doesn't receive shadows
            payload.shadowVisibility = 1.0;
            payload.shadowPenumbra = 0.0;
            payload.shadowDistance = NRD_FP16_MAX;
        }
        return;
    }
    
    // ============================================
    // Universal PBR Shading (Metallic-Roughness Workflow)
    // ============================================
    // F0: 非金属 = 0.04, 金属 = baseColor, 連続補間
    // diffuseColor = baseColor * (1 - metallic)
    // ============================================
    
    float3 V = -rayDir;
    
    // Universal PBR: F0 interpolation based on metallic
    // Non-metal: F0 = 0.04 (dielectric)
    // Metal: F0 = baseColor
    float3 F0 = lerp(0.04.xxx, color.rgb, metallic);
    
    // Diffuse color (metals have no diffuse)
    float3 diffuseColor = color.rgb * (1.0 - metallic);
    
    // ============================================
    // LOOP-BASED: Metallic reflection - return next ray instead of recursive trace
    // ============================================
    // For highly metallic surfaces, we need to continue tracing the reflection
    if (metallic > 0.5)
    {
        float3 reflectDir = reflect(rayDir, N);
        
        // Apply roughness perturbation for blurry reflections
        float2 reflectSeed = hitPosition.xy * 1000.0 + float2(payload.depth, payload.depth * 0.5);
        float3 perturbedDir = PerturbReflection(reflectDir, N, roughness, reflectSeed);
        
        // Calculate TMin to avoid self-intersection
        float reflectTMin = 0.01;
        
        // For boxes, compute exit distance and start after it to avoid self-hit
        if (attribs.objectType == OBJECT_TYPE_BOX)
        {
            BoxData b = Boxes[attribs.objectIndex];
            float3 ax = normalize(b.axisX);
            float3 ay = normalize(b.axisY);
            float3 az = normalize(b.axisZ);
            float3 reflectOrigin = hitPosition + N * 0.01;
            float3 localOrigin = float3(dot(reflectOrigin - b.center, ax),
                                        dot(reflectOrigin - b.center, ay),
                                        dot(reflectOrigin - b.center, az));
            float3 localDir = float3(dot(perturbedDir, ax),
                                     dot(perturbedDir, ay),
                                     dot(perturbedDir, az));
            
            float3 t0 = (-b.size - localOrigin) / localDir;
            float3 t1 = ( b.size - localOrigin) / localDir;
            float3 tMinVec = min(t0, t1);
            float3 tMax = max(t0, t1);
            float tFar = min(min(tMax.x, tMax.y), tMax.z);
            if (tFar > 0.0)
            {
                reflectTMin = max(tFar + 0.01, 0.001);
            }
        }
        
        // Setup next ray for loop continuation
        payload.nextRayOrigin = hitPosition + N * 0.01;
        payload.nextRayDirection = perturbedDir;
        payload.nextRayTMin = reflectTMin;
        // Metal reflection: tint by material color
        payload.throughput = color.rgb;
        payload.continueTrace = 1.0;
        
        // No direct lighting contribution for pure metals - all comes from reflection
        payload.color = emission;
        
        // NRD outputs (primary rays only)
        if (payload.depth == 0)
        {
            float hitDistance = RayTCurrent();
            payload.diffuseRadiance = emission;
            payload.specularRadiance = float3(0, 0, 0);
            payload.hitDistance = hitDistance;
            payload.worldNormal = N;
            payload.roughness = roughness;
            payload.worldPosition = hitPosition;
            payload.viewZ = hitDistance;
            payload.metallic = metallic;
            payload.albedo = color.rgb;
            // Metals don't receive traditional shadows
            payload.shadowVisibility = 1.0;
            payload.shadowPenumbra = 0.0;
            payload.shadowDistance = NRD_FP16_MAX;
        }
        return;
    }
    
    // ============================================
    // Non-metallic, non-glass: Direct lighting only (terminal)
    // For partially metallic surfaces (0.1 < metallic <= 0.5), we compute direct lighting
    // but don't trace reflections (approximation for performance)
    // ============================================
    
    // Direct lighting accumulation
    float3 ambient = float3(0, 0, 0);
    float3 directDiffuse = float3(0, 0, 0);
    float3 directSpecular = float3(0, 0, 0);
    
    // Track the strongest shadow for SIGMA denoiser
    SoftShadowResult bestShadowForSigma;
    bestShadowForSigma.visibility = 1.0;
    bestShadowForSigma.penumbra = 0.0;
    bestShadowForSigma.occluderDistance = NRD_FP16_MAX;
    bestShadowForSigma.shadowColor = float3(1, 1, 1);  // No tint by default
    float bestShadowWeight = -1.0;
    
    // Process all lights with Universal PBR BRDF
    if (Scene.NumLights > 0)
    {
        for (uint li = 0; li < Scene.NumLights; li++)
        {
            LightData light = Lights[li];
            
            if (light.type == LIGHT_TYPE_AMBIENT)
            {
                // Ambient light affects both diffuse and metallic surfaces
                ambient += light.color.rgb * light.intensity * lerp(diffuseColor, color.rgb * 0.3, metallic);
            }
            else
            {
                float3 L;
                float attenuation = 1.0;
                
                if (light.type == LIGHT_TYPE_DIRECTIONAL)
                {
                    L = normalize(-light.position);
                }
                else // LIGHT_TYPE_POINT
                {
                    L = normalize(light.position - hitPosition);
                    float lightDist = length(light.position - hitPosition);
                    attenuation = 1.0 / (1.0 + lightDist * lightDist * 0.01);
                }
                
                float NdotL = max(dot(N, L), 0.0);
                
                if (NdotL > 0.0)
                {
                    // Calculate soft shadow
                    SoftShadowResult shadow = CalculateSoftShadow(hitPosition, normal, light, seed);
                    
                    // Track strongest shadow for SIGMA
                    float weight = NdotL * attenuation * light.intensity;
                    if (weight > bestShadowWeight)
                    {
                        bestShadowWeight = weight;
                        bestShadowForSigma = shadow;
                    }
                    
                    // Apply shadow strength: 0 = no shadow, 1 = normal, >1 = darker
                    float shadowAmount = 1.0 - shadow.visibility;
                    shadowAmount *= Scene.ShadowStrength;
                    shadowAmount = saturate(shadowAmount);
                    float adjustedVisibility = 1.0 - shadowAmount;
                    
                    // Apply colored shadow: multiply by shadow color for translucent objects
                    // shadow.shadowColor is white (1,1,1) for opaque shadows, colored for translucent
                    float3 radiance = light.color.rgb * light.intensity * attenuation * adjustedVisibility * shadow.shadowColor;
                    
                    // Half vector
                    float3 H = normalize(V + L);
                    float NdotV = max(dot(N, V), 0.001);
                    float NdotH = max(dot(N, H), 0.0);
                    float VdotH = max(dot(V, H), 0.0);
                    
                    // Fresnel
                    float3 F = Fresnel_Schlick3(VdotH, F0);
                    
                    // Cook-Torrance Specular BRDF
                    float D = GGX_D(NdotH, max(roughness, 0.04));  // Clamp roughness to avoid division issues
                    float G = Smith_G(NdotV, NdotL, roughness);
                    float3 specBRDF = (D * G * F) / (4.0 * NdotV * NdotL + 0.001);
                    
                    // Diffuse BRDF (energy conserving)
                    // kD = (1 - F) * (1 - metallic) ensures energy conservation
                    float3 kD = (1.0 - F) * (1.0 - metallic);
                    float3 diffBRDF = kD * diffuseColor / PI;
                    
                    // Accumulate lighting
                    directDiffuse += diffBRDF * radiance * NdotL;
                    directSpecular += specBRDF * radiance * NdotL;
                }
            }
        }
    }
    else
    {
        // Fallback: use Scene.LightPosition for backward compatibility
        float3 L = normalize(Scene.LightPosition - hitPosition);
        float lightDist = length(Scene.LightPosition - hitPosition);
        float attenuation = 1.0 / (1.0 + lightDist * lightDist * 0.01);
        
        float NdotL = max(dot(N, L), 0.0);
        
        // Create temporary light data for shadow calculation
        LightData fallbackLight;
        fallbackLight.position = Scene.LightPosition;
        fallbackLight.intensity = Scene.LightIntensity;
        fallbackLight.color = Scene.LightColor;
        fallbackLight.type = LIGHT_TYPE_POINT;
        fallbackLight.radius = 0.0;
        fallbackLight.softShadowSamples = 1.0;
        fallbackLight.padding = 0.0;
        
        SoftShadowResult shadow = CalculateSoftShadow(hitPosition, normal, fallbackLight, seed);
        bestShadowForSigma = shadow;
        
        if (NdotL > 0.0)
        {
            // Apply shadow strength: 0 = no shadow, 1 = normal, >1 = darker
            float shadowAmount = 1.0 - shadow.visibility;
            shadowAmount *= Scene.ShadowStrength;
            shadowAmount = saturate(shadowAmount);
            float adjustedVisibility = 1.0 - shadowAmount;
            
            // Apply colored shadow: multiply by shadow color for translucent objects
            float3 radiance = Scene.LightColor.rgb * Scene.LightIntensity * attenuation * adjustedVisibility * shadow.shadowColor;
            
            float3 H = normalize(V + L);
            float NdotV = max(dot(N, V), 0.001);
            float NdotH = max(dot(N, H), 0.0);
            float VdotH = max(dot(V, H), 0.0);
            
            float3 F = Fresnel_Schlick3(VdotH, F0);
            float D = GGX_D(NdotH, max(roughness, 0.04));
            float G = Smith_G(NdotV, NdotL, roughness);
            float3 specBRDF = (D * G * F) / (4.0 * NdotV * NdotL + 0.001);
            
            float3 kD = (1.0 - F) * (1.0 - metallic);
            float3 diffBRDF = kD * diffuseColor / PI;
            
            directDiffuse = diffBRDF * radiance * NdotL;
            directSpecular = specBRDF * radiance * NdotL;
        }
        
        // Simple ambient
        ambient = lerp(diffuseColor, color.rgb * 0.3, metallic) * 0.2;
    }
    
    // Combine direct lighting (no recursive reflection for diffuse surfaces)
    float3 finalColor = ambient + directDiffuse + directSpecular + emission;
    
    payload.color = saturate(finalColor);
    
    // ============================================
    // LOOP-BASED: Diffuse surfaces terminate the ray (no continuation)
    // ============================================
    payload.continueTrace = 0.0;
    payload.nextRayOrigin = float3(0, 0, 0);
    payload.nextRayDirection = float3(0, 0, 0);
    payload.throughput = float3(1, 1, 1);
    
    // NRD outputs (primary rays only)
    if (payload.depth == 0)
    {
        float hitDistance = RayTCurrent();
        payload.diffuseRadiance = ambient + directDiffuse + emission;
        payload.specularRadiance = directSpecular;
        // SIGMA shadow input
        payload.shadowVisibility = bestShadowForSigma.visibility;
        payload.shadowPenumbra = bestShadowForSigma.penumbra;
        payload.shadowDistance = bestShadowForSigma.occluderDistance;
        payload.hitDistance = hitDistance;
        payload.worldNormal = N;
        payload.roughness = roughness;
        payload.worldPosition = hitPosition;
        payload.viewZ = hitDistance;
        payload.metallic = metallic;
        payload.albedo = color.rgb;
    }
}

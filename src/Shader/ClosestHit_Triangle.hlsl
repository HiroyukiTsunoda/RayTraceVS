// ClosestHit shader for Triangle Meshes (FBX)
// Recursive ray tracing version
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

[shader("closesthit")]
void ClosestHit_Triangle(inout RayPayload payload, in BuiltInTriangleIntersectionAttributes attribs)
{
    payload.hit = 1;
    payload.hitDistance = RayTCurrent();
    
    // InstanceID() でインスタンス情報を取得
    uint instanceIndex = InstanceID();
    MeshInstanceInfo instInfo = MeshInstances[instanceIndex];
    
    // メッシュ種類の情報を取得（頂点/インデックスオフセット）
    MeshInfo meshInfo = MeshInfos[instInfo.meshTypeIndex];
    
    // プリミティブインデックスはBLAS内ローカル（0から始まる）
    // グローバルインデックスに変換するためオフセットを加算
    uint primitiveIndex = PrimitiveIndex();
    uint globalIndexBase = meshInfo.indexOffset + primitiveIndex * 3;
    
    // インデックスバッファから頂点インデックスを取得
    uint i0 = MeshIndices[globalIndexBase + 0];
    uint i1 = MeshIndices[globalIndexBase + 1];
    uint i2 = MeshIndices[globalIndexBase + 2];
    
    // 頂点オフセットを加算してグローバル頂点インデックスに
    uint v0 = meshInfo.vertexOffset + i0;
    uint v1 = meshInfo.vertexOffset + i1;
    uint v2 = meshInfo.vertexOffset + i2;
    
    // バリセントリック座標から頂点補間
    float3 bary = float3(1.0 - attribs.barycentrics.x - attribs.barycentrics.y,
                         attribs.barycentrics.x,
                         attribs.barycentrics.y);
    
    // 頂点法線を補間（スムーズシェーディング）
    float3 n0 = MeshVertices[v0].normal;
    float3 n1 = MeshVertices[v1].normal;
    float3 n2 = MeshVertices[v2].normal;
    float3 localNormal = normalize(n0 * bary.x + n1 * bary.y + n2 * bary.z);
    
    // 面法線を計算（frontFace判定用 - 薄いメッシュでも正しく動作）
    float3 p0 = MeshVertices[v0].position;
    float3 p1 = MeshVertices[v1].position;
    float3 p2 = MeshVertices[v2].position;
    float3 localFaceNormal = normalize(cross(p1 - p0, p2 - p0));
    
    // ワールド空間に変換（インスタンストランスフォーム適用）
    float3 normal = normalize(mul((float3x3)ObjectToWorld3x4(), localNormal));
    float3 faceNormal = normalize(mul((float3x3)ObjectToWorld3x4(), localFaceNormal));
    
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
    float3 rayDir = WorldRayDirection();
    
    // Generate random seed for soft shadows
    uint seed = asuint(hitPosition.x * 1000.0) ^ asuint(hitPosition.y * 2000.0) ^ asuint(hitPosition.z * 3000.0);
    seed = WangHash(seed + payload.depth * 7919);
    
    // Store hit object info
    payload.hitObjectType = OBJECT_TYPE_MESH;
    payload.hitObjectIndex = instanceIndex;
    
    // マテリアル取得（インスタンスごとのマテリアルインデックス）
    MeshMaterial mat = MeshMaterials[instInfo.materialIndex];
    
    // Shadow ray: return material info for colored shadows
    if (payload.depth >= SHADOW_RAY_DEPTH)
    {
        payload.shadowTransmissionAccum = mat.transmission;
        payload.shadowColorAccum = mat.color.rgb;
        return;
    }
    
    // Max depth check (glass needs more bounces for entry/internal/exit)
    uint maxBounces = (Scene.MaxBounces > 0) ? min(Scene.MaxBounces, 8) : 8;
    if (payload.depth >= maxBounces)
    {
        float3 skyFallback = GetSkyColor(rayDir);
        payload.color = skyFallback * lerp(float3(1, 1, 1), mat.color.rgb, 0.3);
        return;
    }
    
    // Extract material properties
    float4 color = mat.color;
    float metallic = mat.metallic;
    float roughness = mat.roughness;
    float transmission = mat.transmission;
    float ior = mat.ior;
    float specular = mat.specular;
    float3 emission = mat.emission;
    
    // Enforce mutual exclusivity: metals are opaque
    if (metallic >= 0.5)
    {
        transmission = 0.0;
    }
    
    bool isGlass = (transmission > 0.01) && (metallic < 0.5);
    
    // Determine if ray is entering or exiting using FACE normal (works for thin shells)
    bool frontFace = dot(rayDir, faceNormal) < 0.0;
    // Final shading normal (ensure it faces the ray)
    float3 N = frontFace ? normal : -normal;
    
    // Glass with Fresnel - Loop-based stochastic selection
    if (isGlass)
    {
        bool entering = frontFace;
        float eta = entering ? (1.0 / ior) : ior;
        
        float cosTheta = saturate(dot(-rayDir, N));
        float f0 = pow((ior - 1.0) / (ior + 1.0), 2.0);
        float fresnel = FresnelSchlick(cosTheta, f0);
        
        float3 reflectDir = normalize(reflect(rayDir, N));
        float3 refractDir = refract(rayDir, N, eta);
        bool tir = dot(refractDir, refractDir) < 1e-6;
        if (!tir)
        {
            refractDir = normalize(refractDir);
        }
        
        // Apply roughness perturbation
        // (Skip for secondary rays to reduce grain)
        if (roughness > 0.01 && payload.depth == 0)
        {
            float2 roughSeed = hitPosition.xy * 1000.0 + float2(payload.depth, payload.depth * 0.5);
            reflectDir = PerturbReflection(reflectDir, N, roughness, roughSeed);
            if (!tir)
            {
                float2 refractSeed = roughSeed + float2(123.456, 789.012);
                refractDir = PerturbReflection(refractDir, -N, roughness, refractSeed);
            }
        }
        
        // Total internal reflection forces reflection
        if (tir)
        {
            fresnel = 1.0;
        }
        
        // Stochastic selection: reflect or refract based on Fresnel probability
        float rand = Hash(hitPosition.xy * 1000.0 + float2(float(seed) * 0.001, payload.depth * 17.3));
        bool chooseReflect = (rand < fresnel);
        if (payload.depth > 0)
        {
            // Deterministic choice for secondary rays to reduce noise
            chooseReflect = (fresnel >= 0.5);
        }
        
        // Primary ray test: remove stochastic noise by mixing reflect+refract
        if (payload.depth == 0)
        {
            float3 reflectColor = float3(0, 0, 0);
            float3 refractColor = float3(0, 0, 0);

            // Reflection trace
            RayDesc reflectRay;
            reflectRay.Origin = hitPosition + N * 0.002;
            reflectRay.Direction = reflectDir;
            reflectRay.TMin = 0.001;
            reflectRay.TMax = 10000.0;

            RayPayload reflectPayload;
            reflectPayload.color = float3(0, 0, 0);
            reflectPayload.depth = payload.depth + 1;
            reflectPayload.hit = 0;
            reflectPayload.padding = 0.0;
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
            reflectPayload.hitObjectType = 0;
            reflectPayload.hitObjectIndex = 0;
            reflectPayload.shadowColorAccum = float3(1, 1, 1);
            reflectPayload.shadowTransmissionAccum = 1.0;
            reflectPayload.loopRayOrigin = float4(0, 0, 0, 0);
            reflectPayload.loopRayDirection = float4(0, 0, 0, 0.001);
            reflectPayload.loopThroughput = float4(1, 1, 1, 0);

            TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, reflectRay, reflectPayload);
            reflectColor = reflectPayload.hit ? reflectPayload.color : GetSkyColor(reflectDir);

            // Refraction trace (skip if TIR)
            if (!tir)
            {
                RayDesc refractRay;
                refractRay.Origin = hitPosition - N * 0.002;
                refractRay.Direction = refractDir;
                refractRay.TMin = 0.001;
                refractRay.TMax = 10000.0;

                RayPayload refractPayload = reflectPayload;
                refractPayload.color = float3(0, 0, 0);
                refractPayload.depth = payload.depth + 1;
                refractPayload.hit = 0;

                TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, refractRay, refractPayload);
                refractColor = refractPayload.hit ? refractPayload.color : GetSkyColor(refractDir);

                // Tint refraction
                float tintStrength = 1.0;
                refractColor *= lerp(float3(1, 1, 1), color.rgb, tintStrength);
            }
            else
            {
                fresnel = 1.0;
            }

            // Specular highlight (direct lighting on glass surface)
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
                    else
                    {
                        lightDir = normalize(light.position - hitPosition);
                        float lightDist = length(light.position - hitPosition);
                        attenuation = 1.0 / (1.0 + lightDist * lightDist * 0.01);
                    }

                    float NdotL = max(dot(N, lightDir), 0.0);
                    if (NdotL > 0.0)
                    {
                        float3 halfDir = normalize(lightDir + viewDir);
                        float shininess = max(64.0, 512.0 * (1.0 - roughness));
                        float spec = pow(max(0.0, dot(N, halfDir)), shininess);
                        float specFresnel = FresnelSchlick(max(0.0, dot(halfDir, viewDir)), f0);
                        specularHighlight += light.color.rgb * light.intensity * spec * specFresnel * attenuation;
                    }
                }
                specularHighlight *= specular * (1.0 - roughness);
            }

            payload.color = specularHighlight + lerp(refractColor, reflectColor, fresnel);
            payload.loopRayOrigin.w = 0.0;

            // NRD outputs for glass (primary rays only)
            if (payload.depth == 0)
            {
                payload.diffuseRadiance = float3(0, 0, 0);  // Glass has no diffuse
                payload.specularRadiance = specularHighlight;
                payload.hitDistance = RayTCurrent();
                payload.worldNormal = normal;
                payload.roughness = roughness;
                payload.worldPosition = hitPosition;
                payload.viewZ = RayTCurrent();
                payload.metallic = 0.0;
                payload.albedo = color.rgb;
                payload.shadowVisibility = 1.0;
                payload.shadowPenumbra = 0.0;
                payload.shadowDistance = NRD_FP16_MAX;
            }
            return;
        }

        // Set up next ray for loop continuation
        if (chooseReflect)
        {
            // Reflection path
            payload.loopRayOrigin.xyz = hitPosition + N * 0.002;
            payload.loopRayDirection.xyz = reflectDir;
            payload.loopRayDirection.w = 0.001;
            payload.loopThroughput.xyz = float3(1, 1, 1);
        }
        else
        {
            // Refraction path - offset in opposite direction of normal
            payload.loopRayOrigin.xyz = hitPosition - N * 0.002;
            payload.loopRayDirection.xyz = refractDir;
            payload.loopRayDirection.w = 0.001;
            float tintStrength = (payload.depth == 0) ? 1.0 : 0.4;
            payload.loopThroughput.xyz = lerp(float3(1, 1, 1), color.rgb, tintStrength);
        }
        
        // Specular highlight (direct lighting on glass surface)
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
                else
                {
                    lightDir = normalize(light.position - hitPosition);
                    float lightDist = length(light.position - hitPosition);
                    attenuation = 1.0 / (1.0 + lightDist * lightDist * 0.01);
                }
                
                float NdotL = max(dot(N, lightDir), 0.0);
                if (NdotL > 0.0)
                {
                    float3 halfDir = normalize(lightDir + viewDir);
                    float shininess = max(64.0, 512.0 * (1.0 - roughness));
                    float spec = pow(max(0.0, dot(N, halfDir)), shininess);
                    float specFresnel = FresnelSchlick(max(0.0, dot(halfDir, viewDir)), f0);
                    specularHighlight += light.color.rgb * light.intensity * spec * specFresnel * attenuation;
                }
            }
            specularHighlight *= specular * (1.0 - roughness);
        }
        
        // Secondary rays (reflections) can't use the loop; do a single-step TraceRay.
        if (payload.depth > 0)
        {
            float3 nextDir = chooseReflect ? reflectDir : refractDir;

            RayDesc secondaryRay;
            secondaryRay.Origin = chooseReflect ? (hitPosition + N * 0.002) : (hitPosition - N * 0.002);
            secondaryRay.Direction = nextDir;
            secondaryRay.TMin = 0.001;
            secondaryRay.TMax = 10000.0;

            RayPayload secondaryPayload;
            secondaryPayload.color = float3(0, 0, 0);
            secondaryPayload.depth = payload.depth + 1;
            secondaryPayload.hit = 0;
            secondaryPayload.padding = 0.0;
            secondaryPayload.diffuseRadiance = float3(0, 0, 0);
            secondaryPayload.specularRadiance = float3(0, 0, 0);
            secondaryPayload.hitDistance = 10000.0;
            secondaryPayload.worldNormal = float3(0, 1, 0);
            secondaryPayload.roughness = 1.0;
            secondaryPayload.worldPosition = float3(0, 0, 0);
            secondaryPayload.viewZ = 10000.0;
            secondaryPayload.metallic = 0.0;
            secondaryPayload.albedo = float3(0, 0, 0);
            secondaryPayload.shadowVisibility = 1.0;
            secondaryPayload.shadowPenumbra = 0.0;
            secondaryPayload.shadowDistance = NRD_FP16_MAX;
            secondaryPayload.targetObjectType = 0;
            secondaryPayload.targetObjectIndex = 0;
            secondaryPayload.thicknessQuery = 0;
            secondaryPayload.hitObjectType = 0;
            secondaryPayload.hitObjectIndex = 0;
            secondaryPayload.shadowColorAccum = float3(1, 1, 1);
            secondaryPayload.shadowTransmissionAccum = 1.0;
            secondaryPayload.loopRayOrigin = float4(0, 0, 0, 0);
            secondaryPayload.loopRayDirection = float4(0, 0, 0, 0.001);
            secondaryPayload.loopThroughput = float4(1, 1, 1, 0);

            TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, secondaryRay, secondaryPayload);

            float3 secondaryColor = secondaryPayload.hit ? secondaryPayload.color : GetSkyColor(nextDir);
            payload.color = specularHighlight + secondaryColor * payload.loopThroughput.xyz;
            payload.loopRayOrigin.w = 0.0;
            return;
        }

        // Primary ray: loop-based continuation
        payload.color = specularHighlight;
        payload.loopRayOrigin.w = 1.0;  // Continue tracing
        
        // NRD outputs for glass (primary rays only)
        if (payload.depth == 0)
        {
            payload.diffuseRadiance = float3(0, 0, 0);  // Glass has no diffuse
            payload.specularRadiance = specularHighlight;
            payload.hitDistance = RayTCurrent();
            payload.worldNormal = normal;
            payload.roughness = roughness;
            payload.worldPosition = hitPosition;
            payload.viewZ = RayTCurrent();
            payload.metallic = 0.0;
            payload.albedo = color.rgb;
            payload.shadowVisibility = 1.0;
            payload.shadowPenumbra = 0.0;
            payload.shadowDistance = NRD_FP16_MAX;
        }
        
        return;
    }
    
    // Universal PBR Shading
    float3 V = -rayDir;
    float3 F0 = lerp(0.04.xxx, color.rgb, metallic);
    float3 diffuseColor = color.rgb * (1.0 - metallic);
    
    // Reflection for metallic surfaces
    float3 reflectColor = float3(0, 0, 0);
    if (metallic > 0.1 && payload.depth < maxBounces)
    {
        float3 reflectDir = reflect(rayDir, N);
        float2 reflectSeed = hitPosition.xy * 1000.0 + float2(payload.depth, payload.depth * 0.5);
        float3 perturbedDir = PerturbReflection(reflectDir, N, roughness, reflectSeed);
        
        RayDesc reflectRay;
        reflectRay.Origin = hitPosition + N * 0.01;
        reflectRay.Direction = perturbedDir;
        reflectRay.TMin = 0.001;
        reflectRay.TMax = 10000.0;
        
        RayPayload reflectPayload;
        reflectPayload.color = float3(0, 0, 0);
        reflectPayload.depth = payload.depth + 1;
        reflectPayload.hit = 0;
        reflectPayload.padding = 0.0;
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
        reflectPayload.targetObjectType = OBJECT_TYPE_MESH;
        reflectPayload.targetObjectIndex = instanceIndex;
        reflectPayload.thicknessQuery = 0;
        reflectPayload.hitObjectType = 0;
        reflectPayload.hitObjectIndex = 0;
        reflectPayload.shadowColorAccum = float3(1, 1, 1);
        reflectPayload.shadowTransmissionAccum = 1.0;
        
        TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 2, 0, 0, reflectRay, reflectPayload);
        
        bool hitSameObject = (reflectPayload.hitObjectType == OBJECT_TYPE_MESH &&
                             reflectPayload.hitObjectIndex == instanceIndex);
        
        if (hitSameObject)
        {
            reflectColor = GetSkyColor(perturbedDir) * color.rgb;
        }
        else
        {
            reflectColor = reflectPayload.color * color.rgb;
        }
    }
    
    // Direct lighting
    float3 ambient = float3(0, 0, 0);
    float3 directDiffuse = float3(0, 0, 0);
    float3 directSpecular = float3(0, 0, 0);
    
    SoftShadowResult bestShadowForSigma;
    bestShadowForSigma.visibility = 1.0;
    bestShadowForSigma.penumbra = 0.0;
    bestShadowForSigma.occluderDistance = NRD_FP16_MAX;
    bestShadowForSigma.shadowColor = float3(1, 1, 1);
    float bestShadowWeight = -1.0;
    
    if (Scene.NumLights > 0)
    {
        for (uint li = 0; li < Scene.NumLights; li++)
        {
            LightData light = Lights[li];
            
            if (light.type == LIGHT_TYPE_AMBIENT)
            {
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
                else
                {
                    L = normalize(light.position - hitPosition);
                    float lightDist = length(light.position - hitPosition);
                    attenuation = 1.0 / (1.0 + lightDist * lightDist * 0.01);
                }
                
                float NdotL = max(dot(N, L), 0.0);
                
                if (NdotL > 0.0)
                {
                    SoftShadowResult shadow = CalculateSoftShadow(hitPosition, normal, light, seed);
                    
                    float weight = NdotL * attenuation * light.intensity;
                    if (weight > bestShadowWeight)
                    {
                        bestShadowWeight = weight;
                        bestShadowForSigma = shadow;
                    }
                    
                    float shadowAmount = 1.0 - shadow.visibility;
                    shadowAmount *= Scene.ShadowStrength;
                    shadowAmount = saturate(shadowAmount);
                    float adjustedVisibility = 1.0 - shadowAmount;
                    
                    float3 radiance = light.color.rgb * light.intensity * attenuation * adjustedVisibility * shadow.shadowColor;
                    
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
                    
                    directDiffuse += diffBRDF * radiance * NdotL;
                    directSpecular += specBRDF * radiance * NdotL;
                }
            }
        }
    }
    else
    {
        // Fallback lighting
        float3 L = normalize(Scene.LightPosition - hitPosition);
        float lightDist = length(Scene.LightPosition - hitPosition);
        float attenuation = 1.0 / (1.0 + lightDist * lightDist * 0.01);
        
        float NdotL = max(dot(N, L), 0.0);
        
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
            float shadowAmount = 1.0 - shadow.visibility;
            shadowAmount *= Scene.ShadowStrength;
            shadowAmount = saturate(shadowAmount);
            float adjustedVisibility = 1.0 - shadowAmount;
            
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
        
        ambient = lerp(diffuseColor, color.rgb * 0.3, metallic) * 0.2;
    }
    
    // Combine reflection and direct lighting
    float reflectionWeight = metallic * (1.0 - roughness * 0.5);
    float directWeight = 1.0 - reflectionWeight * 0.5;
    
    float3 finalColor;
    if (metallic > 0.99 && transmission <= 0.01)
    {
        finalColor = reflectColor + emission;
    }
    else
    {
        finalColor = ambient 
                   + directDiffuse * directWeight 
                   + directSpecular 
                   + reflectColor * reflectionWeight
                   + emission;
    }
    
    payload.color = saturate(finalColor);
    
    // NRD outputs (primary rays only)
    if (payload.depth == 0)
    {
        float hitDistance = RayTCurrent();
        bool isPureMetal = (metallic >= 0.5);
        if (isPureMetal)
        {
            payload.diffuseRadiance = reflectColor + emission;
            payload.specularRadiance = float3(0, 0, 0);
            payload.shadowVisibility = 1.0;
            payload.shadowPenumbra = 0.0;
            payload.shadowDistance = NRD_FP16_MAX;
        }
        else
        {
            payload.diffuseRadiance = ambient + directDiffuse * directWeight + reflectColor * reflectionWeight + emission;
            payload.specularRadiance = directSpecular;
            payload.shadowVisibility = bestShadowForSigma.visibility;
            payload.shadowPenumbra = bestShadowForSigma.penumbra;
            payload.shadowDistance = bestShadowForSigma.occluderDistance;
        }
        payload.hitDistance = hitDistance;
        payload.worldNormal = N;
        payload.roughness = roughness;
        payload.worldPosition = hitPosition;
        payload.viewZ = hitDistance;
        payload.metallic = metallic;
        payload.albedo = color.rgb;
    }
}

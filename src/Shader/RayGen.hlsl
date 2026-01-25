// Full RayGen shader with multi-sampling and DoF
#include "Common.hlsli"

// Simple hash function for random numbers
float Hash(uint seed)
{
    seed = (seed ^ 61) ^ (seed >> 16);
    seed *= 9;
    seed = seed ^ (seed >> 4);
    seed *= 0x27d4eb2d;
    seed = seed ^ (seed >> 15);
    return float(seed) / 4294967295.0;
}

// Generate random offset for anti-aliasing
float2 RandomInPixel(uint2 pixel, uint sampleIndex)
{
    uint seed = pixel.x * 1973 + pixel.y * 9277 + sampleIndex * 26699;
    float x = Hash(seed);
    float y = Hash(seed + 1);
    return float2(x, y);
}

// Generate random point on disk for DoF
float2 RandomOnDisk(uint2 pixel, uint sampleIndex)
{
    uint seed = pixel.x * 7919 + pixel.y * 6271 + sampleIndex * 1009;
    float r = sqrt(Hash(seed));
    float theta = Hash(seed + 1) * 6.28318530718;
    return float2(r * cos(theta), r * sin(theta));
}

// ------------------------------------------------------------
// NaN/Inf guard helpers (keep local to RayGen)
// ------------------------------------------------------------
bool HasNonFinite3(float3 v)
{
    return any(!isfinite(v));
}

bool HasNonFinite4(float4 v)
{
    return any(!isfinite(v));
}

[shader("raygeneration")]
void RayGen()
{
    // ピクセルインデックス取得
    uint2 launchIndex = DispatchRaysIndex().xy;
    uint2 launchDim = DispatchRaysDimensions().xy;
    
    // カメラ情報をシーン定数バッファから取得
    float3 cameraPos = Scene.CameraPosition;
    float3 cameraForward = Scene.CameraForward;
    float3 cameraRight = Scene.CameraRight;
    float3 cameraUp = Scene.CameraUp;
    float aspectRatio = Scene.AspectRatio;
    float tanHalfFov = Scene.TanHalfFov;
    
    // DoFパラメータ
    float apertureSize = Scene.ApertureSize;
    float focusDistance = Scene.FocusDistance;
    bool dofEnabled = apertureSize > 0.001;
    
    // サンプル数を取得（最小1、最大64）
    uint sampleCount = clamp(Scene.SamplesPerPixel, 1, 64);
        uint maxBounces = (Scene.MaxBounces > 0) ? min(Scene.MaxBounces, 32) : 8;

    // Ray budget safety: cap total rays per pixel to avoid TDR
    const uint maxRaysPerPixel = 128;
    if (sampleCount * maxBounces > maxRaysPerPixel)
    {
        sampleCount = max(1u, maxRaysPerPixel / maxBounces);
    }

    
    // 累積カラーとNRDデータ
    float3 accumulatedColor = float3(0, 0, 0);
    float3 accumulatedPrimaryColor = float3(0, 0, 0);
    float3 accumulatedDiffuse = float3(0, 0, 0);
    float3 accumulatedSpecular = float3(0, 0, 0);
    float accumulatedHitDist = 0.0;
    float accumulatedBounce = 0.0;
    float3 primaryNormal = float3(0, 1, 0);
    float primaryRoughness = 1.0;
    float3 primaryPosition = float3(0, 0, 0);
    float primaryViewZ = 10000.0;
    float primaryMetallic = 0.0;
    float3 primaryAlbedo = float3(0, 0, 0);
    bool anyHit = false;
    
    // SIGMA expects RAW single-sample shadow data, NOT averaged!
    // Store primary sample shadow for SIGMA (no averaging)
    float primaryShadowVisibility = 1.0;
    float primaryShadowPenumbra = 0.0;
    float primaryShadowDistance = NRD_FP16_MAX;
    
    // For display/fallback only (averaged)
    float accumulatedShadowVisibility = 0.0;
    float accumulatedShadowPenumbra = 0.0;
    float minShadowDistance = NRD_FP16_MAX;
    int occludedSampleCount = 0;
    
    for (uint s = 0; s < sampleCount; s++)
    {
        // ピクセル内のランダムオフセット（アンチエイリアシング）
        float2 offset = (sampleCount > 1) ? RandomInPixel(launchIndex, s) : float2(0.5, 0.5);
        
        // NDC座標計算（-1 to 1）
        float2 pixelCenter = (float2)launchIndex + offset;
        float2 ndc = pixelCenter / (float2)launchDim * 2.0 - 1.0;
        ndc.y = -ndc.y; // Y軸反転
        
        // レイ方向を計算（カメラ基底ベクトルを使用）
        float3 rayDir = cameraForward 
                      + cameraRight * (ndc.x * tanHalfFov * aspectRatio)
                      + cameraUp * (ndc.y * tanHalfFov);
        rayDir = normalize(rayDir);
        
        // レイの原点とフォーカス点
        float3 rayOrigin = cameraPos;
        
        // DoF: レンズのアパーチャをシミュレート
        if (dofEnabled)
        {
            // フォーカス平面上の点を計算
            float3 focusPoint = cameraPos + rayDir * focusDistance;
            
            // アパーチャ上のランダムな点
            float2 diskOffset = RandomOnDisk(launchIndex, s) * apertureSize;
            rayOrigin = cameraPos + cameraRight * diskOffset.x + cameraUp * diskOffset.y;
            
            // 新しいレイ方向（フォーカス点に向かう）
            rayDir = normalize(focusPoint - rayOrigin);
        }
        
        // ============================================
        // Queue-based ray tracing (RayGen-only TraceRay)
        // ============================================
        const uint maxQueueSize = 16;
        const float throughputThreshold = 0.01;
        PathState queue[maxQueueSize];
        uint queueCount = 0;
        uint processedRays = 0;
        float3 sampleColor = float3(0, 0, 0);
        float3 primaryContribution = float3(0, 0, 0);
        bool primaryHitRecorded = false;
        uint bounceCount = 0;
        
        PathState primaryState;
        primaryState.origin = rayOrigin;
        primaryState.tMin = 0.001;
        primaryState.direction = rayDir;
        primaryState.depth = 0;
        primaryState.throughput = float3(1, 1, 1);
        primaryState.flags = 0;
        primaryState.absorption = float3(0, 0, 0);
        primaryState.pathType = PATH_TYPE_RADIANCE;
        primaryState.skyBoost = 1.0;
        primaryState.padding2 = float3(0, 0, 0);
        queue[queueCount++] = primaryState;
        
        while (queueCount > 0)
        {
            PathState state = queue[--queueCount];
            if (processedRays >= maxRaysPerPixel && (state.flags & PATH_FLAG_SPECULAR) == 0)
            {
                continue;
            }
            processedRays++;
            bounceCount = max(bounceCount, state.depth + 1);
            
            if (state.depth >= maxBounces)
            {
                float3 fallbackSky = state.throughput * GetSkyColor(state.direction);
                sampleColor += fallbackSky;
                if (state.depth == 0)
                {
                    primaryContribution += fallbackSky;
                }
                continue;
            }
            
            float maxThroughput = max(state.throughput.r, max(state.throughput.g, state.throughput.b));
            if (maxThroughput < throughputThreshold && (state.flags & PATH_FLAG_SPECULAR) == 0)
            {
                continue;
            }
            
            // レイディスクリプタ
            RayDesc ray;
            ray.Origin = state.origin;
            ray.Direction = state.direction;
            ray.TMin = state.tMin;
            ray.TMax = 10000.0;
            
            // ペイロード初期化
            RadiancePayload payload;
            payload.color = float3(0, 0, 0);
            payload.depth = state.depth;
            payload.hit = 0;
            payload.padding = 0.0;
            payload.diffuseRadiance = float3(0, 0, 0);
            payload.specularRadiance = float3(0, 0, 0);
            payload.hitDistance = 10000.0;
            payload.worldNormal = float3(0, 1, 0);
            payload.roughness = 1.0;
            payload.worldPosition = float3(0, 0, 0);
            payload.viewZ = 10000.0;
            payload.metallic = 0.0;
            payload.albedo = float3(0, 0, 0);
            payload.shadowVisibility = 1.0;
            payload.shadowPenumbra = 0.0;
            payload.shadowDistance = NRD_FP16_MAX;
            payload.targetObjectType = 0;
            payload.targetObjectIndex = 0;
            payload.thicknessQuery = 0;
            payload.transmission = 0.0;
            payload.ior = 1.5;
            payload.specular = 0.5;
            payload.materialPadding = 0.0;
            payload.emission = float3(0, 0, 0);
            payload.emissionPadding = 0.0;
            payload.loopRayOrigin = float4(0, 0, 0, 0);
            payload.loopRayDirection = float4(0, 0, 0, 0.001);
            payload.loopThroughput = float4(1, 1, 1, 0);
            payload.pathFlags = state.flags;
            payload.pathAbsorption = state.absorption;
            payload.pathSkyBoost = state.skyBoost;
            payload.pathPadding = float3(0, 0, 0);
            payload.childCount = 0;
            
            // レイトレーシング実行
            TraceRay(
                SceneBVH,
                RAY_FLAG_NONE,
                0xFF,
                0, 0, 0,
                ray,
                payload
            );
            
            // NaN/Inf guard: if any critical payload field is invalid,
            // terminate the path and fall back to sky for this ray.
            bool invalidPayload =
                HasNonFinite3(payload.color) ||
                HasNonFinite4(payload.loopRayOrigin) ||
                HasNonFinite4(payload.loopRayDirection) ||
                HasNonFinite4(payload.loopThroughput);
            if (invalidPayload)
            {
                float3 skyFallback = state.throughput * GetSkyColor(state.direction);
                sampleColor += skyFallback;
                if (state.depth == 0)
                {
                    primaryContribution += skyFallback;
                }
                continue;
            }
            
            // Beer-Lambert absorption inside medium (use hit distance)
            if ((state.flags & PATH_FLAG_INSIDE) != 0 && payload.hit)
            {
                float3 absorption = exp(-state.absorption * payload.hitDistance);
                state.throughput *= absorption;
            }

            // Shade in RayGen to keep TraceRay calls centralized here.
            if (payload.hit && !(payload.depth == 0 && (Scene.PhotonDebugMode == 3 || Scene.PhotonDebugMode == 4)))
            {
                float3 hitPosition = payload.worldPosition;
                float3 N = normalize(payload.worldNormal);
                float3 V = -state.direction;
                float metallic = payload.metallic;
                float roughness = payload.roughness;
                float transmission = payload.transmission;
                float ior = payload.ior;
                float specular = payload.specular;
                float3 baseColor = payload.albedo;
                float3 emission = payload.emission;
                bool isGlass = (transmission > 0.01);

                // Seed for shadow sampling (matches ClosestHit)
                uint seed = asuint(hitPosition.x * 1000.0) ^ asuint(hitPosition.y * 2000.0) ^ asuint(hitPosition.z * 3000.0);
                seed = WangHash(seed + payload.depth * 7919);

                if (isGlass)
                {
                    float3 specularHighlight = float3(0, 0, 0);
                    if (specular > 0.01)
                    {
                        float3 viewDir = -state.direction;
                        float f0 = pow((ior - 1.0) / (ior + 1.0), 2.0);
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

                            float ndotl = max(0.0, dot(N, lightDir));
                            if (ndotl > 0.0)
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

                    payload.color = specularHighlight + emission;
                    if (payload.depth == 0)
                    {
                        payload.diffuseRadiance = float3(0, 0, 0);
                        payload.specularRadiance = specularHighlight;
                        payload.shadowVisibility = 1.0;
                        payload.shadowPenumbra = 0.0;
                        payload.shadowDistance = NRD_FP16_MAX;
                    }
                }
                else
                {
                    float3 F0 = lerp(0.04.xxx, baseColor, metallic);
                    float3 diffuseColor = baseColor * (1.0 - metallic);
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
                                ambient += light.color.rgb * light.intensity * lerp(diffuseColor, baseColor * 0.3, metallic);
                                continue;
                            }

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
                                SoftShadowResult shadow = CalculateSoftShadow(hitPosition, N, light, seed);
                                if (payload.depth == 0)
                                {
                                    float weight = NdotL * attenuation * light.intensity;
                                    if (weight > bestShadowWeight)
                                    {
                                        bestShadowWeight = weight;
                                        bestShadowForSigma = shadow;
                                    }
                                }

                                float shadowAmount = 1.0 - shadow.visibility;
                                shadowAmount *= Scene.ShadowStrength;
                                shadowAmount = saturate(shadowAmount);
                                float adjustedVisibility = 1.0 - shadowAmount;
                                float3 shadowColor = shadow.shadowColor;

                                float3 radiance = light.color.rgb * light.intensity * attenuation * adjustedVisibility * shadowColor;

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
                    else if (payload.depth == 0)
                    {
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

                        SoftShadowResult shadow = CalculateSoftShadow(hitPosition, N, fallbackLight, seed);
                        bestShadowForSigma = shadow;

                        if (NdotL > 0.0)
                        {
                            float shadowAmount = 1.0 - shadow.visibility;
                            shadowAmount *= Scene.ShadowStrength;
                            shadowAmount = saturate(shadowAmount);
                            float adjustedVisibility = 1.0 - shadowAmount;
                            float3 shadowColor = shadow.shadowColor;

                            float3 radiance = Scene.LightColor.rgb * Scene.LightIntensity * attenuation * adjustedVisibility * shadowColor;

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

                        ambient = lerp(diffuseColor, baseColor * 0.3, metallic) * 0.2;
                    }

                    float reflectionWeight = metallic * (1.0 - roughness * 0.5);
                    float directWeight = 1.0 - reflectionWeight * 0.5;

                    float3 photonCaustic = float3(0, 0, 0);
                    if (payload.depth == 0 && metallic < 0.5 && transmission <= 0.01 && Scene.PhotonMapSize > 0)
                    {
                        photonCaustic = GatherPhotons(hitPosition, N, Scene.PhotonRadius);
                        if (Scene.PhotonDebugMode > 0)
                        {
                            float3 debugColor = photonCaustic * Scene.PhotonDebugScale;
                            payload.color = debugColor;
                            payload.diffuseRadiance = debugColor;
                            payload.specularRadiance = float3(0, 0, 0);
                            payload.shadowVisibility = 1.0;
                            payload.shadowPenumbra = 0.0;
                            payload.shadowDistance = NRD_FP16_MAX;
                        }
                    }

                    if (Scene.PhotonDebugMode == 0)
                    {
                        float3 finalColor = ambient
                                          + directDiffuse * directWeight
                                          + directSpecular
                                          + photonCaustic
                                          + emission;
                        payload.color = saturate(finalColor);
                    }

                    if (payload.depth == 0)
                    {
                        payload.diffuseRadiance = ambient + directDiffuse * directWeight + photonCaustic + emission;
                        payload.specularRadiance = directSpecular;
                        payload.shadowVisibility = bestShadowForSigma.visibility;
                        payload.shadowPenumbra = bestShadowForSigma.penumbra;
                        payload.shadowDistance = bestShadowForSigma.occluderDistance;
                    }
                }
            }
            
            // Accumulate color with throughput
            float3 bounceColor = state.throughput * payload.color;
            sampleColor += bounceColor;
            if (state.depth == 0)
            {
                primaryContribution += bounceColor;
            }
            
            // Record NRD data from first bounce only
            if (state.depth == 0 && !primaryHitRecorded)
            {
                accumulatedDiffuse += payload.diffuseRadiance;
                accumulatedSpecular += payload.specularRadiance;
                accumulatedHitDist += payload.hitDistance;
                accumulatedShadowVisibility += payload.shadowVisibility;
                accumulatedShadowPenumbra += payload.shadowPenumbra;
                if (payload.shadowDistance < NRD_FP16_MAX)
                {
                    occludedSampleCount++;
                    minShadowDistance = min(minShadowDistance, payload.shadowDistance);
                }
                
                // Record first hit data for NRD
                if (payload.hit && !anyHit)
                {
                    primaryNormal = payload.worldNormal;
                    primaryRoughness = payload.roughness;
                    primaryPosition = payload.worldPosition;
                    primaryViewZ = payload.viewZ;
                    primaryMetallic = payload.metallic;
                    primaryAlbedo = payload.albedo;
                    primaryShadowVisibility = payload.shadowVisibility;
                    primaryShadowPenumbra = payload.shadowPenumbra;
                    primaryShadowDistance = payload.shadowDistance;
                    anyHit = true;
                }
                primaryHitRecorded = true;
            }
            
            // Enqueue children paths
            uint childCount = min(payload.childCount, MAX_CHILD_PATHS);
            [loop]
            for (uint i = 0; i < childCount; i++)
            {
                PathState child = payload.childPaths[i];
                child.throughput *= state.throughput;
                float maxChildThroughput = max(child.throughput.r, max(child.throughput.g, child.throughput.b));
                if (maxChildThroughput < throughputThreshold && (child.flags & PATH_FLAG_SPECULAR) == 0)
                {
                    continue;
                }
                if (queueCount < maxQueueSize)
                {
                    queue[queueCount++] = child;
                }
                else if ((child.flags & PATH_FLAG_SPECULAR) != 0)
                {
                    // Keep specular chain: replace a non-specular entry if possible.
                    // If all entries are specular, replace the weakest throughput.
                    bool replaced = false;
                    [loop]
                    for (uint qi = 0; qi < queueCount; qi++)
                    {
                        if ((queue[qi].flags & PATH_FLAG_SPECULAR) == 0)
                        {
                            queue[qi] = child;
                            replaced = true;
                            break;
                        }
                    }
                    if (!replaced)
                    {
                        uint replaceIndex = 0;
                        float minThroughput = max(queue[0].throughput.r, max(queue[0].throughput.g, queue[0].throughput.b));
                        [loop]
                        for (uint qi = 1; qi < queueCount; qi++)
                        {
                            float t = max(queue[qi].throughput.r, max(queue[qi].throughput.g, queue[qi].throughput.b));
                            if (t < minThroughput)
                            {
                                minThroughput = t;
                                replaceIndex = qi;
                            }
                        }
                        queue[replaceIndex] = child;
                    }
                }
            }
        }
        
        accumulatedColor += sampleColor;
        accumulatedPrimaryColor += primaryContribution;
        accumulatedBounce += (float)bounceCount;
    }
    
    // 平均を取って結果を出力
    float invSampleCount = 1.0 / float(sampleCount);
    float avgBounce = accumulatedBounce * invSampleCount;

    if (Scene.PhotonDebugMode == 2)
    {
        float bounceRatio = (maxBounces > 0) ? saturate(avgBounce / (float)maxBounces) : 0.0;
        float3 debugColor = bounceRatio.xxx;
        
        RenderTarget[launchIndex] = float4(debugColor, 1.0);
        GBuffer_DiffuseRadianceHitDist[launchIndex] = float4(debugColor, 0.0);
        GBuffer_SpecularRadianceHitDist[launchIndex] = float4(0, 0, 0, 0.0);
        GBuffer_NormalRoughness[launchIndex] = NRD_FrontEnd_PackNormalAndRoughness(float3(0, 1, 0), 1.0);
        GBuffer_ViewZ[launchIndex] = 10000.0;
        GBuffer_Albedo[launchIndex] = float4(1.0, 1.0, 1.0, 0.0);
        GBuffer_ShadowData[launchIndex] = float2(NRD_FP16_MAX, 1.0);
        GBuffer_ShadowTranslucency[launchIndex] = SIGMA_FrontEnd_PackTranslucency(NRD_FP16_MAX, float3(0, 0, 0));
        GBuffer_MotionVectors[launchIndex] = float2(0, 0);
        return;
    }
    
    if (Scene.PhotonDebugMode == 1)
    {
        float3 secondaryColor = (accumulatedColor - accumulatedPrimaryColor) * invSampleCount;
        secondaryColor = max(secondaryColor, 0.0);
        
        RenderTarget[launchIndex] = float4(secondaryColor, 1.0);
        GBuffer_DiffuseRadianceHitDist[launchIndex] = float4(secondaryColor, 0.0);
        GBuffer_SpecularRadianceHitDist[launchIndex] = float4(0, 0, 0, 0.0);
        GBuffer_NormalRoughness[launchIndex] = NRD_FrontEnd_PackNormalAndRoughness(float3(0, 1, 0), 1.0);
        GBuffer_ViewZ[launchIndex] = 10000.0;
        GBuffer_Albedo[launchIndex] = float4(1.0, 1.0, 1.0, 0.0);
        GBuffer_ShadowData[launchIndex] = float2(NRD_FP16_MAX, 1.0);
        GBuffer_ShadowTranslucency[launchIndex] = SIGMA_FrontEnd_PackTranslucency(NRD_FP16_MAX, float3(0, 0, 0));
        GBuffer_MotionVectors[launchIndex] = float2(0, 0);
        return;
    }

    float3 finalColor = accumulatedColor * invSampleCount;
    RenderTarget[launchIndex] = float4(finalColor, 1.0);
    
    // Output NRD G-Buffer data for SIGMA workflow
    // diffuseRadiance = lighting WITHOUT shadow (albedo handling depends on material shader)
    // Shadow will be applied from SIGMA denoiser in Composite.hlsl
    // Step B (minimal): keep Composite inputs consistent
    // diffuse + specular should reconstruct accumulatedColor
    float3 diffuseForNRD = (accumulatedColor - accumulatedSpecular) * invSampleCount;
    float3 specularForNRD = accumulatedSpecular * invSampleCount;
    GBuffer_DiffuseRadianceHitDist[launchIndex] = float4(diffuseForNRD, accumulatedHitDist * invSampleCount);
    GBuffer_SpecularRadianceHitDist[launchIndex] = float4(specularForNRD, accumulatedHitDist * invSampleCount);
    
    // For primary normal/roughness/albedo, use hit data if available, else defaults
    float3 worldNormal = anyHit ? primaryNormal : float3(0, 1, 0);
    float outRoughness = anyHit ? primaryRoughness : 1.0;
    float3 outAlbedo = anyHit ? primaryAlbedo : float3(1.0, 1.0, 1.0);
    
    // Convert world space normal to view space for NRD
    // View space: X=right, Y=up, Z=forward (into screen)
    float3 viewSpaceNormal;
    viewSpaceNormal.x = dot(worldNormal, cameraRight);
    viewSpaceNormal.y = dot(worldNormal, cameraUp);
    viewSpaceNormal.z = dot(worldNormal, cameraForward);
    viewSpaceNormal = normalize(viewSpaceNormal);
    
    // Calculate correct linear view depth (ViewZ)
    // NRD/SIGMA expects POSITIVE view depth (distance along camera forward)
    // IMPORTANT: For miss pixels, use a large value (not 0) so SIGMA can properly
    // handle edge filtering. Zero viewZ causes discontinuities that confuse SIGMA.
    float outViewZ;
    float outAlbedoAlpha;  // 1.0 for hits, 0.0 for misses (used in Composite for sky detection)
    if (anyHit)
    {
        float3 hitOffset = primaryPosition - cameraPos;
        outViewZ = dot(hitOffset, cameraForward);  // Positive distance along forward
        outViewZ = max(outViewZ, 0.01);  // Minimum positive depth (avoid zero)
        outAlbedoAlpha = 1.0;
    }
    else
    {
        // Use large depth for misses - this helps SIGMA handle edges properly
        // by avoiding sharp depth discontinuities (0 vs large value)
        outViewZ = 10000.0;  // Far distance for sky/miss pixels
        outAlbedoAlpha = 0.0;  // Mark as miss for Composite shader
    }
    
    // Pack normal and roughness for NRD (using view space normal)
    GBuffer_NormalRoughness[launchIndex] = NRD_FrontEnd_PackNormalAndRoughness(viewSpaceNormal, outRoughness);
    GBuffer_ViewZ[launchIndex] = outViewZ;
    GBuffer_Albedo[launchIndex] = float4(outAlbedo, outAlbedoAlpha);
    
    // SIGMA shadow inputs - Use RAW PRIMARY SAMPLE data, NOT averaged!
    // SIGMA expects noisy single-sample input for temporal reconstruction
    // Averaging before SIGMA destroys the temporal coherence SIGMA needs
    
    // SIGMA penumbra encoding:
    // - NRD_FP16_MAX (65504) = fully lit (no shadow, no occluder)
    // - Small value (0.1 to ~100) = shadow with penumbra size in world units
    // IMPORTANT: SIGMA expects penumbra to be much smaller than NRD_FP16_MAX
    // Values > ~1000 are treated as "almost no shadow"
    float sigmaPenumbra;
    if (primaryShadowVisibility > 0.99)
    {
        sigmaPenumbra = NRD_FP16_MAX;  // Fully lit - no shadow
    }
    else
    {
        // Shadow area - clamp penumbra to reasonable range for SIGMA
        // primaryShadowPenumbra can be very large due to light size calculations
        // Clamp to max 100 to ensure SIGMA recognizes it as shadow
        sigmaPenumbra = clamp(primaryShadowPenumbra, 0.1, 100.0);
    }
    
    // Sanitize shadow values before SIGMA - NaN/Inf or out-of-range values cause white sparkles
    primaryShadowVisibility = saturate(primaryShadowVisibility);
    primaryShadowVisibility = isfinite(primaryShadowVisibility) ? primaryShadowVisibility : 1.0;
    sigmaPenumbra = isfinite(sigmaPenumbra) ? sigmaPenumbra : NRD_FP16_MAX;
    
    GBuffer_ShadowData[launchIndex] = float2(sigmaPenumbra, primaryShadowVisibility);
    
    // Pack translucency using NRD format:
    // X = 1.0 if no hit (lit), 0.0 if hit (shadow) - this is the binary signal
    // SIGMA uses this combined with penumbra for temporal reconstruction
    float4 packedTranslucency = SIGMA_FrontEnd_PackTranslucency(
        primaryShadowDistance,  // distanceToOccluder (NRD_FP16_MAX if no hit)
        float3(0, 0, 0)         // translucency (opaque shadow)
    );
    GBuffer_ShadowTranslucency[launchIndex] = packedTranslucency;
    
    // Motion vectors: screen-space pixel delta (current - previous)
    float2 motion = float2(0, 0);
    if (anyHit)
    {
        float4 currClip = mul(float4(primaryPosition, 1.0), Scene.ViewProjection);
        float4 prevClip = mul(float4(primaryPosition, 1.0), Scene.PrevViewProjection);
        float2 currNdc = currClip.xy / currClip.w;
        float2 prevNdc = prevClip.xy / prevClip.w;
        float2 ndcDelta = currNdc - prevNdc;
        // NDC -1..1 to pixel delta (scaled down to avoid overshoot)
        float2 pixelScale = float2(Scene.ScreenWidth, Scene.ScreenHeight) * 0.5;
        motion = ndcDelta * pixelScale * 0.1; // dampen motion for stability
        // Clamp to reasonable range
        motion = clamp(motion, float2(-8, -8), float2(8, 8));
    }
    GBuffer_MotionVectors[launchIndex] = motion;
}

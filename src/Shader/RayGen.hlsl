// Full RayGen shader with multi-sampling and DoF
#include "Common.hlsli"

uint RngSampleIndex(uint sampleIndex, uint depth)
{
    return sampleIndex + depth * 4096u;
}

float4 SampleBlueNoise(uint2 pixel, uint frame, uint sampleIndex)
{
    uint blueNoiseFrame = frame;
    uint2 offset = uint2(blueNoiseFrame * 3u + sampleIndex * 11u, blueNoiseFrame * 5u + sampleIndex * 7u);
    uint2 p = (pixel + offset) & 15u; // 16x16 tile
    return BlueNoiseTex.Load(int3(p, 0));
}

// PerturbReflection is now in Common.hlsli (P1-3: code deduplication)

// Generate random offset for anti-aliasing
float2 RandomInPixel(uint2 pixel, uint sampleIndex)
{
    float4 bn = SampleBlueNoise(pixel, Scene.FrameIndex, sampleIndex);
    return bn.xy;
}

// Generate random point on disk for DoF
float2 RandomOnDisk(uint2 pixel, uint sampleIndex)
{
    float4 bn = SampleBlueNoise(pixel, Scene.FrameIndex, sampleIndex);
    float r = sqrt(bn.z);
    float theta = bn.w * 6.28318530718;
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
    float3 primaryAlbedo = float3(0, 0, 0);
    float primaryMetallic = 0.0;
    float primaryTransmission = 0.0;
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
        // WorkItem queue (per-pixel, stored in UAV)
        // ============================================
        const float throughputThreshold = 0.01;
        uint pixelIndex = launchIndex.y * launchDim.x + launchIndex.x;
        uint baseIndex = pixelIndex * WORK_QUEUE_STRIDE;
        uint queueCount = 0;
        WorkQueueCount[pixelIndex] = 0;
        uint processedRays = 0;
        float3 sampleColor = float3(0, 0, 0);
        float3 primaryContribution = float3(0, 0, 0);
        bool primaryHitRecorded = false;
        uint bounceCount = 0;
        
        WorkItem primaryState;
        primaryState.origin = rayOrigin;
        primaryState.tMin = 0.001;
        primaryState.direction = rayDir;
        primaryState.depth = 0;
        primaryState.throughput = float3(1, 1, 1);
        primaryState.pathFlags = 0;
        primaryState.absorption = float3(0, 0, 0);
        primaryState.rayKind = RAYKIND_RADIANCE;
        primaryState.skyBoost = 1.0;
        primaryState.specularDepth = 0;
        primaryState.diffuseDepth = 0;
        primaryState.kind = 0;
        primaryState.rayFlags = 0;
        primaryState.skipObjectType = OBJECT_TYPE_INVALID;
        primaryState.skipObjectIndex = 0;
        primaryState.mediumEta = 1.0; // Start in air (outside any medium)
        WorkQueue[baseIndex + queueCount++] = primaryState;
        WorkQueueCount[pixelIndex] = queueCount;
        
        while (queueCount > 0)
        {
            WorkItem state = WorkQueue[baseIndex + (--queueCount)];
            if (processedRays >= maxRaysPerPixel && (state.pathFlags & PATH_FLAG_SPECULAR) == 0)
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
            if (maxThroughput < throughputThreshold && (state.pathFlags & PATH_FLAG_SPECULAR) == 0)
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
            payload.hitDistance = 10000.0;
            payload.diffuseRadiance = float3(0, 0, 0);
            payload.specularHitDistance = NRD_FP16_MAX;
            payload.specularRadiance = float3(0, 0, 0);
            payload.packedNormal = PackNormalOctahedron(float3(0, 1, 0));
            payload.packedMaterial0 = PackHalf2(float2(1.0, 0.0)); // roughness, metallic
            payload.packedMaterial1 = PackHalf2(float2(0.5, 0.0)); // specular, transmission
            payload.packedMaterial2 = PackHalf2(float2(1.5, 0.0)); // ior, unused
            payload.albedo = float3(0, 0, 0);
            payload.shadowVisibility = 1.0;
            payload.shadowPenumbra = 0.0;
            payload.shadowDistance = NRD_FP16_MAX;
            payload.emission = float3(0, 0, 0);
            payload.absorption = float3(0, 0, 0);
            payload.pathFlags = state.pathFlags;
            payload.pathAbsorption = state.absorption;
            payload.pathSkyBoost = state.skyBoost;
            payload.rayFlags = state.rayFlags;
            payload.skipObjectType = state.skipObjectType;
            payload.skipObjectIndex = state.skipObjectIndex;
            payload.hitObjectType = OBJECT_TYPE_INVALID;
            payload.hitObjectIndex = 0;
            payload.frontFace = 0;
            
            // レイトレーシング実行
            uint rayContribution = ((state.rayFlags & RAYFLAG_SKIP_SELF) != 0) ? 2 : 0;
            TraceRay(
                SceneBVH,
                RAY_FLAG_NONE,
                0xFF,
                rayContribution, 0, 0,
                ray,
                payload
            );
            
            // NaN/Inf guard: if any critical payload field is invalid,
            // terminate the path and fall back to sky for this ray.
            bool invalidPayload = HasNonFinite3(payload.color);
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
            
            float3 hitPosition = state.origin + state.direction * payload.hitDistance;
            float3 N = UnpackNormalOctahedron(payload.packedNormal);
            float2 rm = UnpackHalf2(payload.packedMaterial0);
            float2 st = UnpackHalf2(payload.packedMaterial1);
            float2 io = UnpackHalf2(payload.packedMaterial2);
            float roughness = rm.x;
            float metallic = rm.y;
            float specular = st.x;
            float transmission = st.y;
            float ior = io.x;
            uint rngSampleIndex = RngSampleIndex(s, payload.depth);
            RNG shadowRng = rng_init(launchIndex, Scene.FrameIndex, rngSampleIndex, RNG_SALT_SHADOW);
            uint seed = shadowRng.state;
            
            // Shade in RayGen to keep TraceRay calls centralized here.
            if (payload.hit && !(payload.depth == 0 && (Scene.PhotonDebugMode == 3 || Scene.PhotonDebugMode == 4)))
            {
                float3 V = -state.direction;
                float3 baseColor = payload.albedo;
                float3 emission = payload.emission;
                bool isGlass = (transmission > 0.01);

                if (isGlass)
                {
                    float3 specularHighlight = float3(0, 0, 0);
                    if (specular > 0.01)
                    {
                        float3 viewDir = -state.direction;
                        float f0FromIor = pow((ior - 1.0) / (ior + 1.0), 2.0);
                        float specularBlend = saturate(specular);
                        float f0 = lerp(f0FromIor, specularBlend, specularBlend);
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
                                // P1-1: Physical-based attenuation
                                attenuation = ComputeAttenuationFromScene(lightDist);
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
                        // P0-1 Optimization: Select dominant lights for shadow calculation
                        // Only trace shadow rays for the most influential lights
                        LightInfo topLights[2];
                        uint topCount = SelectDominantLights(hitPosition, N, topLights);
                        
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
                                // P1-1: Physical-based attenuation
                                attenuation = ComputeAttenuationFromScene(lightDist);
                            }

                            float NdotL = max(dot(N, L), 0.0);
                            if (NdotL > 0.0)
                            {
                                // P0-1 Optimization: Only calculate shadows for dominant lights
                                SoftShadowResult shadow;
                                bool isTopLight = IsInTopLights(li, topLights, topCount);
                                
                                if (isTopLight)
                                {
                                    // Dominant light: calculate shadow with importance-based sample count
                                    if (light.radius > 0.001)
                                    {
                                        // Soft shadow with adaptive samples
                                        uint samples = ComputeShadowSamples(light, topLights, li);
                                        // Temporarily override softShadowSamples for this calculation
                                        LightData adjustedLight = light;
                                        adjustedLight.softShadowSamples = (float)samples;
                                        shadow = CalculateSoftShadow(hitPosition, N, adjustedLight, seed);
                                    }
                                    else
                                    {
                                        // Hard shadow (single ray)
                                        shadow = CalculateSoftShadow(hitPosition, N, light, seed);
                                    }
                                }
                                else
                                {
                                    // Non-dominant light: skip shadow calculation (direct light only)
                                    shadow.visibility = 1.0;
                                    shadow.penumbra = 0.0;
                                    shadow.occluderDistance = NRD_FP16_MAX;
                                    shadow.shadowColor = float3(1, 1, 1);
                                }
                                
                                if (payload.depth == 0)
                                {
                                    float weight = NdotL * attenuation * light.intensity;
                                    if (weight > bestShadowWeight)
                                    {
                                        bestShadowWeight = weight;
                                        bestShadowForSigma = shadow;
                                    }
                                }

                                // Apply shadow to radiance (shadow baked into lighting)
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
                        // P1-1: Physical-based attenuation
                        float attenuation = ComputeAttenuationFromScene(lightDist);
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
                            // Apply shadow to radiance (shadow baked into lighting)
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
                        payload.color = max(finalColor, 0.0);
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
            
#if DEBUG_SHADOW_VIS
            if (payload.depth == 0)
            {
                float v = saturate(payload.shadowVisibility);
                payload.color = v.xxx;
                payload.diffuseRadiance = v.xxx;
                payload.specularRadiance = float3(0, 0, 0);
            }
#endif

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
                    primaryNormal = N;
                    primaryRoughness = roughness;
                    primaryPosition = hitPosition;
                    primaryAlbedo = payload.albedo;
                    primaryMetallic = metallic;
                    primaryTransmission = transmission;
                    primaryShadowVisibility = payload.shadowVisibility;
                    primaryShadowPenumbra = payload.shadowPenumbra;
                    primaryShadowDistance = payload.shadowDistance;
                    anyHit = true;
                }
                primaryHitRecorded = true;
            }
            
            // Enqueue next paths (WorkItem queue)
            if (payload.hit && state.depth < maxBounces)
            {
                float3 baseColor = payload.albedo;
                bool isGlass = (transmission > 0.01);
                bool isMetal = (metallic > 0.1);
                bool useRR = (state.specularDepth > 6) && (state.diffuseDepth >= 2) && (Luminance(state.throughput) < 0.25);
                
                if (isGlass)
                {
                    // Use frontFace from payload for inside/outside state tracking
                    bool entering = (payload.frontFace != 0);
                    // N is always flipped to face the ray, so always use eta = 1/ior
                    // (maintains visual consistency with original behavior)
                    float eta = 1.0 / ior;
                    float3 reflectDir = normalize(reflect(state.direction, N));
                    float3 refractDir = refract(state.direction, N, eta);
                    bool tir = dot(refractDir, refractDir) < 1e-6;
                    if (!tir)
                    {
                        refractDir = normalize(refractDir);
                    }
                    
                    if (roughness > 0.01 && state.depth == 0)
                    {
                        uint localSampleIndex = RngSampleIndex(s, state.depth);
                        RNG reflectRng = rng_init(launchIndex, Scene.FrameIndex, localSampleIndex, RNG_SALT_REFLECT);
                        reflectDir = PerturbReflection(reflectDir, N, roughness, reflectRng);
                        if (!tir)
                        {
                            RNG refractRng = rng_init(launchIndex, Scene.FrameIndex, localSampleIndex, RNG_SALT_REFRACT);
                            refractDir = PerturbReflection(refractDir, -N, roughness, refractRng);
                        }
                    }
                    
                    float cosTheta = saturate(dot(-state.direction, N));
                    float f0FromIor = pow((ior - 1.0) / (ior + 1.0), 2.0);
                    float specularBlend = saturate(specular);
                    float f0 = lerp(f0FromIor, specularBlend, specularBlend);
                    float fresnel = FresnelSchlick(cosTheta, f0);
                    if (tir)
                    {
                        fresnel = 1.0;
                    }
                    
                    float3 reflectThroughput = fresnel.xxx;
                    float transmittance = saturate(transmission);
                    // P3-3: Use consistent tint strength regardless of ray depth
                    // This provides more predictable glass coloring at all bounce levels
                    float tintStrength = 0.85;  // Balanced value between full tint (1.0) and minimal (0.7)
                    float3 refractThroughput = (1.0 - fresnel) * transmittance
                        * lerp(float3(1, 1, 1), baseColor, tintStrength);
                    reflectThroughput = clamp(reflectThroughput, 0.0, 1.0);
                    refractThroughput = clamp(refractThroughput, 0.0, 1.0);
                    
                    float thickness = 0.0;
                    if (!tir)
                    {
                        RayDesc thicknessRay;
                        thicknessRay.Origin = hitPosition + refractDir * 0.002;
                        thicknessRay.Direction = refractDir;
                        thicknessRay.TMin = 0.001;
                        thicknessRay.TMax = NRD_FP16_MAX;
                        
                        ThicknessPayload thicknessPayload;
                        thicknessPayload.hit = 0;
                        thicknessPayload.hitT = NRD_FP16_MAX;
                        thicknessPayload.objectType = payload.hitObjectType;
                        thicknessPayload.objectIndex = payload.hitObjectIndex;
                        
                        TraceRay(SceneBVH,
                                 RAY_FLAG_SKIP_CLOSEST_HIT_SHADER,
                                 0xFF,
                                 3, 0, 2,
                                 thicknessRay,
                                 thicknessPayload);
                        
                        if (thicknessPayload.hit)
                        {
                            thickness = thicknessPayload.hitT;
                        }
                    }
                    
                    float absorptionScale = 0.6;
                    float3 refractionAbsorption = (!tir && thickness > 0.0)
                        ? exp(-payload.absorption * thickness * absorptionScale)
                        : float3(1, 1, 1);
                    float3 refractionPathScale = refractionAbsorption;
                    
                    float reflectWeight = max(reflectThroughput.r, max(reflectThroughput.g, reflectThroughput.b));
                    float refractWeight = (!tir)
                        ? max(refractThroughput.r, max(refractThroughput.g, refractThroughput.b))
                        : 0.0;
                    float weightSum = reflectWeight + refractWeight;
                    
                    if (useRR)
                    {
                        RNG rrRng = rng_init(launchIndex, Scene.FrameIndex, RngSampleIndex(s, state.depth), RNG_SALT_RR);
                        float rr = rng_next(rrRng);
                        bool chooseReflect = tir || (rr < (reflectWeight / max(weightSum, 1e-6)));
                        float chosenWeight = chooseReflect ? reflectWeight : refractWeight;
                        float3 nextThroughput = chooseReflect
                            ? reflectThroughput
                            : (refractThroughput * refractionPathScale);
                        nextThroughput *= weightSum / max(chosenWeight, 1e-6);
                        
                        WorkItem child;
                        child.origin = chooseReflect ? (hitPosition + N * 0.002) : (hitPosition + refractDir * 0.002);
                        child.tMin = 0.001;
                        child.direction = chooseReflect ? reflectDir : refractDir;
                        child.depth = state.depth + 1;
                        child.throughput = nextThroughput * state.throughput;
                        // Toggle PATH_FLAG_INSIDE for refraction, keep same for reflection
                        if (chooseReflect)
                        {
                            child.pathFlags = state.pathFlags | PATH_FLAG_SPECULAR;
                            child.mediumEta = state.mediumEta;
                        }
                        else
                        {
                            // Refraction: toggle inside/outside state
                            if (entering)
                            {
                                child.pathFlags = (state.pathFlags | PATH_FLAG_SPECULAR) | PATH_FLAG_INSIDE;
                                child.mediumEta = ior;
                            }
                            else
                            {
                                child.pathFlags = (state.pathFlags | PATH_FLAG_SPECULAR) & ~PATH_FLAG_INSIDE;
                                child.mediumEta = 1.0;
                            }
                        }
                        child.absorption = state.absorption;
                        child.rayKind = RAYKIND_RADIANCE;
                        child.skyBoost = SKY_BOOST_GLASS;
                        child.specularDepth = state.specularDepth + 1;
                        child.diffuseDepth = state.diffuseDepth;
                        child.kind = chooseReflect ? 1 : 2;
                        child.rayFlags = chooseReflect ? RAYFLAG_SKIP_SELF : 0;
                        child.skipObjectType = chooseReflect ? payload.hitObjectType : OBJECT_TYPE_INVALID;
                        child.skipObjectIndex = chooseReflect ? payload.hitObjectIndex : 0;
                        
                        float maxChildThroughput = max(child.throughput.r, max(child.throughput.g, child.throughput.b));
                        if (maxChildThroughput >= throughputThreshold || (child.pathFlags & PATH_FLAG_SPECULAR) != 0)
                        {
                            if (queueCount < WORK_QUEUE_STRIDE)
                            {
                                WorkQueue[baseIndex + queueCount++] = child;
                                WorkQueueCount[pixelIndex] = queueCount;
                            }
                        }
                    }
                    else
                    {
                        WorkItem reflectChild;
                        reflectChild.origin = hitPosition + N * 0.002;
                        reflectChild.tMin = 0.001;
                        reflectChild.direction = reflectDir;
                        reflectChild.depth = state.depth + 1;
                        reflectChild.throughput = reflectThroughput * state.throughput;
                        reflectChild.pathFlags = state.pathFlags | PATH_FLAG_SPECULAR;
                        reflectChild.absorption = state.absorption;
                        reflectChild.rayKind = RAYKIND_RADIANCE;
                        reflectChild.skyBoost = SKY_BOOST_GLASS;
                        reflectChild.specularDepth = state.specularDepth + 1;
                        reflectChild.diffuseDepth = state.diffuseDepth;
                        reflectChild.kind = 1;
                        reflectChild.rayFlags = RAYFLAG_SKIP_SELF;
                        reflectChild.skipObjectType = payload.hitObjectType;
                        reflectChild.skipObjectIndex = payload.hitObjectIndex;
                        reflectChild.mediumEta = state.mediumEta; // Reflection stays in same medium
                        
                        if (queueCount < WORK_QUEUE_STRIDE)
                        {
                            WorkQueue[baseIndex + queueCount++] = reflectChild;
                            WorkQueueCount[pixelIndex] = queueCount;
                        }
                        
                        if (!tir)
                        {
                            WorkItem refractChild;
                            refractChild.origin = hitPosition + refractDir * 0.002;
                            refractChild.tMin = 0.001;
                            refractChild.direction = refractDir;
                            refractChild.depth = state.depth + 1;
                            refractChild.throughput = refractThroughput * refractionPathScale * state.throughput;
                            // Toggle PATH_FLAG_INSIDE for refraction
                            if (entering)
                            {
                                refractChild.pathFlags = (state.pathFlags | PATH_FLAG_SPECULAR) | PATH_FLAG_INSIDE;
                                refractChild.mediumEta = ior;
                            }
                            else
                            {
                                refractChild.pathFlags = (state.pathFlags | PATH_FLAG_SPECULAR) & ~PATH_FLAG_INSIDE;
                                refractChild.mediumEta = 1.0;
                            }
                            refractChild.absorption = state.absorption;
                            refractChild.rayKind = RAYKIND_RADIANCE;
                            refractChild.skyBoost = SKY_BOOST_GLASS;
                            refractChild.specularDepth = state.specularDepth + 1;
                            refractChild.diffuseDepth = state.diffuseDepth;
                            refractChild.kind = 2;
                            refractChild.rayFlags = 0;
                            refractChild.skipObjectType = OBJECT_TYPE_INVALID;
                            refractChild.skipObjectIndex = 0;
                            
                            if (queueCount < WORK_QUEUE_STRIDE)
                            {
                                WorkQueue[baseIndex + queueCount++] = refractChild;
                                WorkQueueCount[pixelIndex] = queueCount;
                            }
                        }
                    }
                }
                else if (isMetal)
                {
                    float3 F0 = lerp(0.04.xxx, baseColor, metallic);
                    float3 reflectDir = reflect(state.direction, N);
                    RNG reflectRng = rng_init(launchIndex, Scene.FrameIndex, RngSampleIndex(s, state.depth), RNG_SALT_REFLECT);
                    float3 perturbedDir = PerturbReflection(reflectDir, N, roughness, reflectRng);
                    
                    float NdotV = saturate(dot(N, -state.direction));
                    float3 F = Fresnel_Schlick3(NdotV, F0);
                    float reflectScale = (1.0 - roughness * 0.5);
                    float boost = (state.depth > 0) ? 1.5 : 1.0;
                    
                    WorkItem reflectChild;
                    reflectChild.origin = hitPosition + N * 0.002;
                    reflectChild.tMin = 0.001;
                    reflectChild.direction = perturbedDir;
                    reflectChild.depth = state.depth + 1;
                    reflectChild.throughput = (F * reflectScale * boost) * state.throughput;
                    reflectChild.pathFlags = state.pathFlags | PATH_FLAG_SPECULAR;
                    reflectChild.absorption = state.absorption;
                    reflectChild.rayKind = RAYKIND_RADIANCE;
                    reflectChild.skyBoost = SKY_BOOST_METAL;
                    reflectChild.specularDepth = state.specularDepth + 1;
                    reflectChild.diffuseDepth = state.diffuseDepth;
                    reflectChild.kind = 1;
                    bool reflectInside = (reflectChild.pathFlags & PATH_FLAG_INSIDE) != 0;
                    reflectChild.rayFlags = reflectInside ? 0 : RAYFLAG_SKIP_SELF;
                    reflectChild.skipObjectType = reflectInside ? OBJECT_TYPE_INVALID : payload.hitObjectType;
                    reflectChild.skipObjectIndex = reflectInside ? 0 : payload.hitObjectIndex;
                    reflectChild.mediumEta = state.mediumEta;
                    
                    float maxChildThroughput = max(reflectChild.throughput.r, max(reflectChild.throughput.g, reflectChild.throughput.b));
                    if (maxChildThroughput >= throughputThreshold || (reflectChild.pathFlags & PATH_FLAG_SPECULAR) != 0)
                    {
                        if (queueCount < WORK_QUEUE_STRIDE)
                        {
                            WorkQueue[baseIndex + queueCount++] = reflectChild;
                            WorkQueueCount[pixelIndex] = queueCount;
                        }
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
    
    // For primary normal/roughness/albedo, use hit data if available, else defaults
    float3 worldNormal = anyHit ? primaryNormal : float3(0, 1, 0);
    float outRoughness = anyHit ? primaryRoughness : 1.0;
    float3 outAlbedo = anyHit ? primaryAlbedo : float3(1.0, 1.0, 1.0);
    
    // Output NRD G-Buffer data for REBLUR workflow
    // ========================================
    // NRD REBLUR expects:
    //   - Diffuse: DEMODULATED radiance (diffuse / albedo) for opaque surfaces
    //   - Specular: radiance as-is (reflections, refractions)
    //
    // Material classification:
    //   - Glass/Metal (specular-dominant): ALL light goes to specular (no demodulation)
    //   - Opaque diffuse: diffuse is demodulated, specular is direct highlights
    //   - Sky/miss: sky color in diffuse
    // ========================================
    
    float3 diffuseForNRD;
    float3 specularForNRD;
    
    // P2-2: Smooth specular dominance using smoothstep instead of binary threshold
    // This provides gradual transition between specular and diffuse surfaces
    float specularDominance = max(primaryTransmission, primaryMetallic);
    float nrdBlendFactor = 1.0 - smoothstep(0.3, 0.7, specularDominance);
    bool isSpecularDominant = anyHit && (specularDominance > 0.5);
    
    if (!anyHit)
    {
        // Sky/miss: put sky color in diffuse, specular = 0
        diffuseForNRD = finalColor;
        specularForNRD = float3(0, 0, 0);
    }
    else if (specularDominance > 0.7)
    {
        // Highly specular (glass/metal > 0.7): ALL light goes to specular
        // No demodulation - these materials have no meaningful diffuse component
        diffuseForNRD = float3(0, 0, 0);
        specularForNRD = finalColor;
    }
    else if (specularDominance > 0.3)
    {
        // P2-2: Transition zone (0.3-0.7): blend between specular and diffuse treatment
        float3 diffuseModulated = accumulatedDiffuse * invSampleCount;
        float3 directSpecular = accumulatedSpecular * invSampleCount;
        float3 secondaryBounces = max(finalColor - diffuseModulated - directSpecular, 0.0);
        float3 safeAlbedo = max(outAlbedo, float3(0.04, 0.04, 0.04));
        
        // Blend between full specular and demodulated diffuse
        diffuseForNRD = (diffuseModulated / safeAlbedo) * nrdBlendFactor;
        specularForNRD = lerp(finalColor, directSpecular + secondaryBounces, nrdBlendFactor);
    }
    else
    {
        // Opaque diffuse surface (< 0.3): demodulate diffuse, keep specular separate
        float3 diffuseModulated = accumulatedDiffuse * invSampleCount;
        float3 directSpecular = accumulatedSpecular * invSampleCount;
        
        // Secondary bounces = total - (diffuse + direct specular)
        float3 secondaryBounces = max(finalColor - diffuseModulated - directSpecular, 0.0);
        
        // DEMODULATE: divide by albedo for NRD
        // Use minimum threshold to avoid division by zero on dark surfaces
        float3 safeAlbedo = max(outAlbedo, float3(0.04, 0.04, 0.04));
        diffuseForNRD = diffuseModulated / safeAlbedo;
        
        // Specular: direct specular + secondary bounces (reflections from other objects)
        specularForNRD = directSpecular + secondaryBounces;
    }
    
    GBuffer_DiffuseRadianceHitDist[launchIndex] = float4(diffuseForNRD, accumulatedHitDist * invSampleCount);
    GBuffer_SpecularRadianceHitDist[launchIndex] = float4(specularForNRD, accumulatedHitDist * invSampleCount);
    
    // Build NRD inputs using centralized coordinate system conversion
    // See NRDEncoding.hlsli for the authoritative coordinate convention documentation
    NRDInputs nrdInputs = NRD_BuildInputs(
        worldNormal,
        primaryPosition,
        cameraPos,
        cameraRight,
        cameraUp,
        cameraForward,
        Scene.ViewProjection,
        Scene.PrevViewProjection,
        float2(Scene.ScreenWidth, Scene.ScreenHeight),
        anyHit
    );
    
    // Pack normal and roughness for NRD (using view space normal from NRD_BuildInputs)
    GBuffer_NormalRoughness[launchIndex] = NRD_FrontEnd_PackNormalAndRoughness(nrdInputs.viewSpaceNormal, outRoughness);
    GBuffer_ViewZ[launchIndex] = nrdInputs.viewZ;
    
    // Encode material type in albedo.alpha for Composite shader:
    //   0.0 = sky/miss (use raw diffuse directly, no remodulation)
    //   0.5 = specular-dominant (glass/metal) - bypass NRD completely to avoid ghosting
    //   0.75-1.0 = semi-specular to diffuse - use NRD with P2-2 smoothstep blending
    // Note: Glass/metal MUST use discrete 0.5 to ensure complete NRD bypass
    float materialAlpha;
    if (!anyHit)
        materialAlpha = 0.0;
    else if (specularDominance > 0.5)
        materialAlpha = 0.5;  // Glass/metal: discrete value for complete NRD bypass
    else
        materialAlpha = lerp(0.75, 1.0, nrdBlendFactor);  // Semi-specular to diffuse: P2-2 smooth transition
    
    GBuffer_Albedo[launchIndex] = float4(outAlbedo, materialAlpha);
    
    // SIGMA shadow inputs - Use RAW PRIMARY SAMPLE data, NOT averaged!
    // SIGMA expects noisy single-sample input for temporal reconstruction
    // Averaging before SIGMA destroys the temporal coherence SIGMA needs
    
    // SIGMA penumbra encoding:
    // - NRD_FP16_MAX (65504) = fully lit (no shadow, no occluder)
    // - Small value (SIGMA_PENUMBRA_MIN to SIGMA_PENUMBRA_PRACTICAL_MAX) = shadow with penumbra size in world units
    // IMPORTANT: SIGMA expects penumbra to be much smaller than NRD_FP16_MAX
    // Values > ~1000 are treated as "almost no shadow"
    // See NRDEncoding.hlsli for constant definitions and rationale
    float sigmaPenumbra;
    if (primaryShadowVisibility > SHADOW_FULLY_LIT_THRESHOLD)
    {
        sigmaPenumbra = NRD_FP16_MAX;  // Fully lit - no shadow
    }
    else
    {
        // Shadow area - clamp penumbra to reasonable range for SIGMA
        // primaryShadowPenumbra can be very large due to light size calculations
        // Clamp to SIGMA_PENUMBRA_PRACTICAL_MAX to ensure SIGMA recognizes it as shadow
        sigmaPenumbra = clamp(primaryShadowPenumbra, SIGMA_PENUMBRA_MIN, SIGMA_PENUMBRA_PRACTICAL_MAX);
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
    
    // Motion vectors: use centralized calculation from NRD_BuildInputs
    // Note: No damping applied - correct pixel-space motion for NRD
    // If stabilization is needed, adjust NRD settings (maxAccumulatedFrameNum, etc.)
    GBuffer_MotionVectors[launchIndex] = nrdInputs.motionVector;
}

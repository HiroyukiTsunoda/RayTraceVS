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
    payload.loopRayOrigin.w = 0.0;  // Default: terminate loop unless overridden
    payload.childCount = 0;
    const bool debugSimplifyTriangle = false;
    
    // InstanceID() でインスタンス情報を取得
    uint instanceIndex = InstanceID();
    MeshInstanceInfo instInfo = MeshInstances[instanceIndex];
    
    // マテリアル取得（インスタンスごとのマテリアルインデックス）
    MeshMaterial mat = MeshMaterials[instInfo.materialIndex];
    
    // Shadow ray: return material info for colored shadows (avoid mesh buffer access)
    if (payload.depth >= SHADOW_RAY_DEPTH)
    {
        payload.shadowTransmissionAccum = mat.transmission;
        payload.shadowColorAccum = mat.color.rgb;
        return;
    }
    
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
    
    if (debugSimplifyTriangle)
    {
        payload.color = mat.color.rgb;
        payload.diffuseRadiance = mat.color.rgb;
        payload.specularRadiance = float3(0, 0, 0);
        payload.worldNormal = float3(0, 1, 0);
        payload.roughness = mat.roughness;
        payload.worldPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
        payload.viewZ = 10000.0;
        payload.metallic = mat.metallic;
        payload.albedo = mat.color.rgb;
        payload.shadowVisibility = 1.0;
        payload.shadowPenumbra = 0.0;
        payload.shadowDistance = NRD_FP16_MAX;
        return;
    }
    
    // Shadow ray: return material info for colored shadows
    if (payload.depth >= SHADOW_RAY_DEPTH)
    {
        payload.shadowTransmissionAccum = mat.transmission;
        payload.shadowColorAccum = mat.color.rgb;
        return;
    }
    
    // Max depth check (glass needs more bounces for entry/internal/exit)
    uint maxBounces = (Scene.MaxBounces > 0) ? min(Scene.MaxBounces, 32) : 10;
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
    float3 absorption = mat.absorption;

    // Debug: visualize material values as grayscale
    if (payload.depth == 0 && Scene.PhotonDebugMode == 3)
    {
        float t = saturate(transmission);
        payload.color = t.xxx;
        payload.diffuseRadiance = t.xxx;
        payload.specularRadiance = float3(0, 0, 0);
        payload.loopRayOrigin.w = 0.0;
        return;
    }
    if (payload.depth == 0 && Scene.PhotonDebugMode == 4)
    {
        float m = saturate(metallic);
        payload.color = m.xxx;
        payload.diffuseRadiance = m.xxx;
        payload.specularRadiance = float3(0, 0, 0);
        payload.loopRayOrigin.w = 0.0;
        return;
    }
    
    // Treat transmission as glass regardless of metallic to avoid parameter lock
    bool isGlass = (transmission > 0.01);
    
    // Determine if ray is entering or exiting using FACE normal (works for thin shells)
    bool frontFace = dot(rayDir, faceNormal) < 0.0;
    bool isInside = (payload.pathFlags & PATH_FLAG_INSIDE) != 0;
    // Final shading normal (ensure it faces the ray)
    float3 N = frontFace ? normal : -normal;

    // Store hit/material data for RayGen shading
    payload.worldNormal = N;
    payload.worldPosition = hitPosition;
    payload.roughness = roughness;
    payload.metallic = metallic;
    payload.albedo = color.rgb;
    payload.transmission = transmission;
    payload.ior = ior;
    payload.specular = specular;
    payload.emission = emission;
    payload.viewZ = RayTCurrent();
    
    // Glass with Fresnel - Loop-based stochastic selection
    if (isGlass)
    {
        bool entering = !isInside;
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
        // NaN/Inf guard (avoid GPU hang)
        if (any(!isfinite(N)) || any(!isfinite(reflectDir)) || any(!isfinite(refractDir)))
        {
            payload.color = GetSkyColor(rayDir);
            payload.loopRayOrigin.w = 0.0;
            return;
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
        
        // Deterministic split for loop-based tracing (reflection + refraction)
        float3 reflectThroughput = fresnel.xxx;
        float transmittance = saturate(transmission);
        float tintStrength = (payload.depth == 0) ? 1.0 : 0.7;
        float3 refractThroughput = (1.0 - fresnel) * transmittance * lerp(float3(1, 1, 1), color.rgb, tintStrength);
        reflectThroughput = clamp(reflectThroughput, 0.0, 1.0);
        refractThroughput = clamp(refractThroughput, 0.0, 1.0);
        
        uint childCount = 0;
        // Reflection child
        PathState reflectChild;
        reflectChild.origin = hitPosition + N * 0.002;
        reflectChild.tMin = 0.001;
        reflectChild.direction = reflectDir;
        reflectChild.depth = payload.depth + 1;
        reflectChild.throughput = reflectThroughput;
        reflectChild.flags = payload.pathFlags | PATH_FLAG_SPECULAR;
        reflectChild.absorption = payload.pathAbsorption;
        reflectChild.pathType = PATH_TYPE_RADIANCE;
        reflectChild.skyBoost = SKY_BOOST_GLASS;
        reflectChild.padding2 = float3(0, 0, 0);
        payload.childPaths[childCount++] = reflectChild;
        
        // Refraction child (skip when TIR)
        if (!tir)
        {
            PathState refractChild;
            refractChild.origin = hitPosition - N * 0.002;
            refractChild.tMin = 0.001;
            refractChild.direction = refractDir;
            refractChild.depth = payload.depth + 1;
            refractChild.throughput = refractThroughput;
            if (entering)
            {
                refractChild.flags = payload.pathFlags | PATH_FLAG_INSIDE | PATH_FLAG_SPECULAR;
                refractChild.absorption = absorption;
            }
            else
            {
                refractChild.flags = (payload.pathFlags & ~PATH_FLAG_INSIDE) | PATH_FLAG_SPECULAR;
                refractChild.absorption = float3(0, 0, 0);
            }
            refractChild.pathType = PATH_TYPE_RADIANCE;
            refractChild.skyBoost = SKY_BOOST_GLASS;
            refractChild.padding2 = float3(0, 0, 0);
            payload.childPaths[childCount++] = refractChild;
        }
        payload.childCount = childCount;
        
        // Shading is handled in RayGen; return material + child paths only.
        payload.color = float3(0, 0, 0);
        
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
        
        // Queue-based reflection: trace in RayGen, keep local contribution minimal
        float NdotV = saturate(dot(N, V));
        float3 F = Fresnel_Schlick3(NdotV, F0);
        float reflectScale = (1.0 - roughness * 0.5);
        // Boost secondary metal reflections a bit to avoid overly dark chains
        float boost = (payload.depth > 0) ? 1.5 : 1.0;
        
        PathState reflectChild;
        reflectChild.origin = hitPosition + N * 0.002;
        reflectChild.tMin = 0.001;
        reflectChild.direction = perturbedDir;
        reflectChild.depth = payload.depth + 1;
        reflectChild.throughput = F * reflectScale * boost;
        reflectChild.flags = payload.pathFlags | PATH_FLAG_SPECULAR;
        reflectChild.absorption = payload.pathAbsorption;
        reflectChild.pathType = PATH_TYPE_RADIANCE;
        reflectChild.skyBoost = SKY_BOOST_METAL;
        reflectChild.padding2 = float3(0, 0, 0);
        payload.childPaths[0] = reflectChild;
        payload.childCount = 1;
    }
    
    // Shading is handled in RayGen; return material + child paths only.
    payload.color = float3(0, 0, 0);
    return;
}

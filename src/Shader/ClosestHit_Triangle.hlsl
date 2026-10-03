// ClosestHit shader for Triangle Meshes (FBX)
// Recursive ray tracing version
#include "Common.hlsli"

// PerturbReflection is now in Common.hlsli (P1-3: code deduplication)

[shader("closesthit")]
void ClosestHit_Triangle(inout RadiancePayload payload, in BuiltInTriangleIntersectionAttributes attribs)
{
    payload.hit = 1;
    payload.hitDistance = RayTCurrent();
    const bool debugSimplifyTriangle = false;
    
    // InstanceID() でインスタンス情報を取得
    uint instanceIndex = InstanceID();
    MeshInstanceInfo instInfo = MeshInstances[instanceIndex];
    payload.hitObjectType = OBJECT_TYPE_MESH;
    payload.hitObjectIndex = instanceIndex;
    
    // マテリアル取得（インスタンスごとのマテリアルインデックス）
    MeshMaterial mat = MeshMaterials[instInfo.materialIndex];
    
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
    
    if (debugSimplifyTriangle)
    {
        payload.color = mat.color.rgb;
        payload.diffuseRadiance = mat.color.rgb;
        payload.specularRadiance = float3(0, 0, 0);
        payload.packedNormal = PackNormalOctahedron(float3(0, 1, 0));
        payload.packedMaterial0 = PackHalf2(float2(mat.roughness, mat.metallic));
        payload.packedMaterial1 = PackHalf2(float2(mat.specular, mat.transmission));
        payload.packedMaterial2 = PackHalf2(float2(mat.ior, 0.0));
        payload.albedo = mat.color.rgb;
        payload.emission = mat.emission;
        payload.absorption = mat.absorption;
        payload.shadowVisibility = 1.0;
        payload.shadowPenumbra = 0.0;
        payload.shadowDistance = NRD_FP16_MAX;
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
        return;
    }
    if (payload.depth == 0 && Scene.PhotonDebugMode == 4)
    {
        float m = saturate(metallic);
        payload.color = m.xxx;
        payload.diffuseRadiance = m.xxx;
        payload.specularRadiance = float3(0, 0, 0);
        return;
    }
    
    // Treat transmission as glass regardless of metallic to avoid parameter lock
    bool isGlass = (transmission > 0.01);
    
    // Determine if ray is entering or exiting using FACE normal (works for thin shells)
    bool frontFace = dot(rayDir, faceNormal) < 0.0;
    // Final shading normal (ensure it faces the ray)
    float3 N = frontFace ? normal : -normal;

    // Store hit/material data for RayGen shading (packed)
    payload.packedNormal = PackNormalOctahedron(N);
    payload.frontFace = frontFace ? 1 : 0;
    payload.packedMaterial0 = PackHalf2(float2(roughness, metallic));
    payload.packedMaterial1 = PackHalf2(float2(specular, transmission));
    payload.packedMaterial2 = PackHalf2(float2(ior, 0.0));
    payload.albedo = color.rgb;
    payload.emission = emission;
    payload.absorption = absorption;
    
    // Glass: handled in RayGen
    if (isGlass)
    {
        payload.color = float3(0, 0, 0);
        return;
    }
    
    // Shading and secondary rays are handled in RayGen; return material only.
    payload.color = float3(0, 0, 0);
    return;
}

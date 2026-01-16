#include "Common.hlsli"

// オブジェクトデータ（構造化バッファとして渡される想定）
StructuredBuffer<SphereData> Spheres : register(t1);
StructuredBuffer<PlaneData> Planes : register(t2);
StructuredBuffer<CylinderData> Cylinders : register(t3);
StructuredBuffer<LightData> Lights : register(t4);

[shader("closesthit")]
void ClosestHit(inout RayPayload payload, in SphereAttributes attribs)
{
    // 交差点情報取得
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
    float3 normal = attribs.normal;
    
    // オブジェクトデータ取得（PrimitiveIndexから）
    // オブジェクトタイプはHitGroupIndexから判定
    uint hitGroupIndex = HitKind();
    uint primitiveIndex = PrimitiveIndex();
    
    // 基本色
    float3 objectColor;
    float reflectivity;
    float transparency = 0.0;
    float ior = 1.0;
    
    // オブジェクトタイプごとの処理
    // 0=Sphere, 1=Plane, 2=Cylinder と仮定
    uint geometryIndex = InstanceID(); // ジオメトリタイプの識別に使用
    
    // とりあえず簡易的に全て球として処理し、後で改善
    // 実際のプロジェクトではヒットグループで分ける必要がある
    SphereData sphere = Spheres[primitiveIndex];
    objectColor = sphere.color.rgb;
    reflectivity = sphere.reflectivity;
    transparency = sphere.transparency;
    ior = sphere.ior;
    
    // TODO: 平面と円柱の処理を追加
    // 現時点では簡易実装として、全オブジェクトを球データから読み込む
    
    float3 finalColor = float3(0, 0, 0);
    
    // アンビエント項
    float3 ambient = objectColor * 0.1;
    finalColor += ambient;
    
    // 各ライトからの寄与を計算
    uint numLights = Scene.numLights;
    for (uint i = 0; i < numLights; i++)
    {
        LightData light = Lights[i];
        
        // ライト方向と距離
        float3 lightDir = light.position - hitPosition;
        float lightDistance = length(lightDir);
        lightDir = normalize(lightDir);
        
        // シャドウレイ
        RayDesc shadowRay;
        shadowRay.Origin = hitPosition + normal * 0.001; // バイアス
        shadowRay.Direction = lightDir;
        shadowRay.TMin = 0.001;
        shadowRay.TMax = lightDistance;
        
        RayPayload shadowPayload;
        shadowPayload.color = float3(0, 0, 0);
        shadowPayload.depth = payload.depth + 1;
        shadowPayload.hit = false;
        
        // シャドウレイをトレース（簡易版）
        // TraceRay(...); // 実際はここでシャドウ判定
        
        // 影がない場合のライティング計算
        float attenuation = 1.0 / (lightDistance * lightDistance);
        
        // 拡散反射
        float3 diffuse = CalculateDiffuse(normal, lightDir, light.color.rgb * light.intensity, objectColor);
        
        // スペキュラー反射
        float3 viewDir = normalize(Scene.cameraPosition - hitPosition);
        float3 specular = CalculateSpecular(normal, lightDir, viewDir, light.color.rgb * light.intensity, 32.0);
        
        finalColor += (diffuse + specular) * attenuation;
    }
    
    // 反射
    if (sphere.reflectivity > 0.0 && payload.depth < MAX_RECURSION_DEPTH)
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
        
        TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, reflectRay, reflectPayload);
        
        finalColor = lerp(finalColor, reflectPayload.color, sphere.reflectivity);
    }
    
    // 屈折（透明マテリアル）
    if (sphere.transparency > 0.0 && payload.depth < MAX_RECURSION_DEPTH)
    {
        float eta = 1.0 / sphere.ior;
        float3 refractDir = refract(WorldRayDirection(), normal, eta);
        
        if (length(refractDir) > 0.0)
        {
            RayDesc refractRay;
            refractRay.Origin = hitPosition - normal * 0.001;
            refractRay.Direction = refractDir;
            refractRay.TMin = 0.001;
            refractRay.TMax = 10000.0;
            
            RayPayload refractPayload;
            refractPayload.color = float3(0, 0, 0);
            refractPayload.depth = payload.depth + 1;
            refractPayload.hit = false;
            
            TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, refractRay, refractPayload);
            
            finalColor = lerp(finalColor, refractPayload.color, sphere.transparency);
        }
    }
    
    payload.color = saturate(finalColor);
    payload.hit = true;
}

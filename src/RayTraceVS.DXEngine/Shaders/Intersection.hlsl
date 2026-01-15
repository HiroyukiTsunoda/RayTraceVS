#include "Common.hlsli"

// 球の構造化バッファ
StructuredBuffer<SphereData> Spheres : register(t1);

[shader("intersection")]
void SphereIntersection()
{
    // プリミティブインデックスから球データ取得
    uint primitiveIndex = PrimitiveIndex();
    SphereData sphere = Spheres[primitiveIndex];
    
    // レイ情報
    float3 origin = WorldRayOrigin();
    float3 direction = WorldRayDirection();
    
    // レイと球の交差判定（2次方程式）
    float3 oc = origin - sphere.center;
    
    float a = dot(direction, direction);
    float b = 2.0 * dot(oc, direction);
    float c = dot(oc, oc) - sphere.radius * sphere.radius;
    
    float discriminant = b * b - 4 * a * c;
    
    // 交差判定
    if (discriminant >= 0.0)
    {
        float sqrtDiscriminant = sqrt(discriminant);
        float t1 = (-b - sqrtDiscriminant) / (2.0 * a);
        float t2 = (-b + sqrtDiscriminant) / (2.0 * a);
        
        float t = t1;
        if (t < RayTMin())
            t = t2;
        
        if (t >= RayTMin() && t <= RayTCurrent())
        {
            // 交差点での法線計算
            float3 hitPoint = origin + direction * t;
            float3 normal = normalize(hitPoint - sphere.center);
            
            // 属性設定
            SphereAttributes attribs;
            attribs.normal = normal;
            
            // ヒット報告
            ReportHit(t, 0, attribs);
        }
    }
}

[shader("intersection")]
void CylinderIntersection()
{
    // 円柱の交差判定
    // TODO: 実装
    // 無限円柱との交差 + キャップのチェック
}

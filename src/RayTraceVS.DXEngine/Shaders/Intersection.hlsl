#include "Common.hlsli"

// 球の構造化バッファ
StructuredBuffer<SphereData> Spheres : register(t1);
// 平面の構造化バッファ
StructuredBuffer<PlaneData> Planes : register(t2);
// 円柱の構造化バッファ
StructuredBuffer<CylinderData> Cylinders : register(t3);

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
void PlaneIntersection()
{
    // プリミティブインデックスから平面データ取得
    uint primitiveIndex = PrimitiveIndex();
    PlaneData plane = Planes[primitiveIndex];
    
    // レイ情報
    float3 origin = WorldRayOrigin();
    float3 direction = WorldRayDirection();
    
    // レイと平面の交差判定
    float denom = dot(plane.normal, direction);
    
    // 平面と平行でない場合
    if (abs(denom) > 0.0001)
    {
        float3 p0 = plane.position - origin;
        float t = dot(p0, plane.normal) / denom;
        
        if (t >= RayTMin() && t <= RayTCurrent())
        {
            // 属性設定
            SphereAttributes attribs;
            attribs.normal = plane.normal;
            
            // ヒット報告
            ReportHit(t, 0, attribs);
        }
    }
}

[shader("intersection")]
void CylinderIntersection()
{
    // プリミティブインデックスから円柱データ取得
    uint primitiveIndex = PrimitiveIndex();
    CylinderData cylinder = Cylinders[primitiveIndex];
    
    // レイ情報
    float3 origin = WorldRayOrigin();
    float3 direction = WorldRayDirection();
    
    // 円柱の軸を正規化
    float3 axis = normalize(cylinder.axis);
    float3 oc = origin - cylinder.position;
    
    // 側面との交差判定
    float3 dirCrossAxis = cross(direction, axis);
    float3 ocCrossAxis = cross(oc, axis);
    
    float a = dot(dirCrossAxis, dirCrossAxis);
    float b = 2.0 * dot(dirCrossAxis, ocCrossAxis);
    float c = dot(ocCrossAxis, ocCrossAxis) - cylinder.radius * cylinder.radius;
    
    float discriminant = b * b - 4 * a * c;
    
    if (discriminant >= 0.0 && a > 0.0001)
    {
        float sqrtDiscriminant = sqrt(discriminant);
        float t1 = (-b - sqrtDiscriminant) / (2.0 * a);
        float t2 = (-b + sqrtDiscriminant) / (2.0 * a);
        
        // 両方のヒット点をチェック
        float tValues[2] = { t1, t2 };
        
        for (int i = 0; i < 2; i++)
        {
            float t = tValues[i];
            
            if (t >= RayTMin() && t <= RayTCurrent())
            {
                float3 hitPoint = origin + direction * t;
                float3 localHitPoint = hitPoint - cylinder.position;
                float height = dot(localHitPoint, axis);
                
                // 高さの範囲内にあるかチェック
                if (height >= 0.0 && height <= cylinder.height)
                {
                    // 法線計算（側面）
                    float3 hitOnAxis = cylinder.position + axis * height;
                    float3 normal = normalize(hitPoint - hitOnAxis);
                    
                    // 属性設定
                    SphereAttributes attribs;
                    attribs.normal = normal;
                    
                    // ヒット報告
                    ReportHit(t, 0, attribs);
                    break;
                }
            }
        }
    }
    
    // キャップとの交差判定（上面と下面）
    // 下面
    float denom = dot(axis, direction);
    if (abs(denom) > 0.0001)
    {
        float3 p0 = cylinder.position - origin;
        float t = dot(p0, axis) / denom;
        
        if (t >= RayTMin() && t <= RayTCurrent())
        {
            float3 hitPoint = origin + direction * t;
            float3 localHitPoint = hitPoint - cylinder.position;
            float dist = length(localHitPoint - axis * dot(localHitPoint, axis));
            
            if (dist <= cylinder.radius)
            {
                SphereAttributes attribs;
                attribs.normal = -axis;
                ReportHit(t, 0, attribs);
            }
        }
    }
    
    // 上面
    if (abs(denom) > 0.0001)
    {
        float3 topCenter = cylinder.position + axis * cylinder.height;
        float3 p0 = topCenter - origin;
        float t = dot(p0, axis) / denom;
        
        if (t >= RayTMin() && t <= RayTCurrent())
        {
            float3 hitPoint = origin + direction * t;
            float3 localHitPoint = hitPoint - topCenter;
            float dist = length(localHitPoint - axis * dot(localHitPoint, axis));
            
            if (dist <= cylinder.radius)
            {
                SphereAttributes attribs;
                attribs.normal = axis;
                ReportHit(t, 0, attribs);
            }
        }
    }
}

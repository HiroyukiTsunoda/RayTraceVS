#include "Common.hlsli"

// Global index counters to track object indices across primitives
// These are set based on the scene layout in the BLAS

// Helper to determine object type and index from primitive index
void GetObjectInfo(uint primitiveIndex, out uint objectType, out uint objectIndex)
{
    // Objects are stored in order: Spheres, then Planes, then Cylinders
    // We need to determine which type based on the primitive index and counts
    
    uint sphereCount = Scene.NumSpheres;
    uint planeCount = Scene.NumPlanes;
    uint cylinderCount = Scene.NumCylinders;
    
    if (primitiveIndex < sphereCount)
    {
        objectType = OBJECT_TYPE_SPHERE;
        objectIndex = primitiveIndex;
    }
    else if (primitiveIndex < sphereCount + planeCount)
    {
        objectType = OBJECT_TYPE_PLANE;
        objectIndex = primitiveIndex - sphereCount;
    }
    else
    {
        objectType = OBJECT_TYPE_CYLINDER;
        objectIndex = primitiveIndex - sphereCount - planeCount;
    }
}

[shader("intersection")]
void SphereIntersection()
{
    uint primitiveIndex = PrimitiveIndex();
    
    // Determine object type and index
    uint objectType, objectIndex;
    GetObjectInfo(primitiveIndex, objectType, objectIndex);
    
    // レイ情報
    float3 origin = WorldRayOrigin();
    float3 direction = WorldRayDirection();
    
    // Handle based on object type
    if (objectType == OBJECT_TYPE_SPHERE)
    {
        SphereData sphere = Spheres[objectIndex];
        
        // レイと球の交差判定
        float3 oc = origin - sphere.center;
        
        float a = dot(direction, direction);
        float b = 2.0 * dot(oc, direction);
        float c = dot(oc, oc) - sphere.radius * sphere.radius;
        
        float discriminant = b * b - 4 * a * c;
        
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
                float3 hitPoint = origin + direction * t;
                float3 normal = normalize(hitPoint - sphere.center);
                
                ProceduralAttributes attribs;
                attribs.normal = normal;
                attribs.objectType = objectType;
                attribs.objectIndex = objectIndex;
                
                ReportHit(t, 0, attribs);
            }
        }
    }
    else if (objectType == OBJECT_TYPE_PLANE)
    {
        PlaneData plane = Planes[objectIndex];
        
        float3 n = normalize(plane.normal);
        float denom = dot(n, direction);
        
        if (abs(denom) > 0.0001)
        {
            float3 p0 = plane.position - origin;
            float t = dot(p0, n) / denom;
            
            if (t >= RayTMin() && t <= RayTCurrent())
            {
                ProceduralAttributes attribs;
                attribs.normal = n;
                attribs.objectType = objectType;
                attribs.objectIndex = objectIndex;
                
                ReportHit(t, 0, attribs);
            }
        }
    }
    else // OBJECT_TYPE_CYLINDER
    {
        CylinderData cyl = Cylinders[objectIndex];
        
        float3 axis = normalize(cyl.axis);
        float3 oc = origin - cyl.position;
        
        // 側面との交差判定
        float3 dirCrossAxis = cross(direction, axis);
        float3 ocCrossAxis = cross(oc, axis);
        
        float a = dot(dirCrossAxis, dirCrossAxis);
        float b = 2.0 * dot(dirCrossAxis, ocCrossAxis);
        float c = dot(ocCrossAxis, ocCrossAxis) - cyl.radius * cyl.radius;
        
        float discriminant = b * b - 4 * a * c;
        
        // 側面のチェック - すべての有効な交差をReportHitに報告
        // DXRが自動的に最も近いヒットを選択する
        if (discriminant >= 0.0 && a > 0.0001)
        {
            float sqrtDiscriminant = sqrt(discriminant);
            float t1 = (-b - sqrtDiscriminant) / (2.0 * a);
            float t2 = (-b + sqrtDiscriminant) / (2.0 * a);
            
            float tValues[2] = { t1, t2 };
            
            for (int i = 0; i < 2; i++)
            {
                float t = tValues[i];
                
                if (t >= RayTMin() && t <= RayTCurrent())
                {
                    float3 hitPoint = origin + direction * t;
                    float3 localHitPoint = hitPoint - cyl.position;
                    float height = dot(localHitPoint, axis);
                    
                    if (height >= 0.0 && height <= cyl.height)
                    {
                        float3 hitOnAxis = cyl.position + axis * height;
                        float3 normal = normalize(hitPoint - hitOnAxis);
                        
                        ProceduralAttributes attribs;
                        attribs.normal = normal;
                        attribs.objectType = objectType;
                        attribs.objectIndex = objectIndex;
                        
                        ReportHit(t, 0, attribs);
                    }
                }
            }
        }
        
        // キャップ判定 - 側面のヒット有無に関わらずチェック
        // DXRが最も近いヒットを自動選択
        float denom = dot(axis, direction);
        if (abs(denom) > 0.0001)
        {
            // 下面
            float3 p0 = cyl.position - origin;
            float tBottom = dot(p0, axis) / denom;
            
            if (tBottom >= RayTMin() && tBottom <= RayTCurrent())
            {
                float3 hitPoint = origin + direction * tBottom;
                float3 localHitPoint = hitPoint - cyl.position;
                float dist = length(localHitPoint - axis * dot(localHitPoint, axis));
                
                if (dist <= cyl.radius)
                {
                    ProceduralAttributes attribs;
                    attribs.normal = -axis;
                    attribs.objectType = objectType;
                    attribs.objectIndex = objectIndex;
                    
                    ReportHit(tBottom, 0, attribs);
                }
            }
            
            // 上面
            float3 topCenter = cyl.position + axis * cyl.height;
            p0 = topCenter - origin;
            float tTop = dot(p0, axis) / denom;
            
            if (tTop >= RayTMin() && tTop <= RayTCurrent())
            {
                float3 hitPoint = origin + direction * tTop;
                float3 localHitPoint = hitPoint - topCenter;
                float dist = length(localHitPoint - axis * dot(localHitPoint, axis));
                
                if (dist <= cyl.radius)
                {
                    ProceduralAttributes attribs;
                    attribs.normal = axis;
                    attribs.objectType = objectType;
                    attribs.objectIndex = objectIndex;
                    
                    ReportHit(tTop, 0, attribs);
                }
            }
        }
    }
}

// Sphere + Plane + Box intersection
#include "Common.hlsli"

[shader("intersection")]
void SphereIntersection()
{
    uint primitiveIndex = PrimitiveIndex();
    
    float3 origin = WorldRayOrigin();
    float3 direction = WorldRayDirection();
    
    uint sphereCount = Scene.NumSpheres;
    uint planeCount = Scene.NumPlanes;
    uint boxCount = Scene.NumBoxes;
    
    // Check if this primitive is a sphere
    if (primitiveIndex < sphereCount)
    {
        SphereData sphere = Spheres[primitiveIndex];
        
        float3 oc = origin - sphere.center;
        
        float a = dot(direction, direction);
        float b = 2.0 * dot(oc, direction);
        float c = dot(oc, oc) - sphere.radius * sphere.radius;
        
        float discriminant = b * b - 4.0 * a * c;
        
        if (discriminant >= 0.0)
        {
            float sqrtD = sqrt(discriminant);
            float t1 = (-b - sqrtD) / (2.0 * a);
            float t2 = (-b + sqrtD) / (2.0 * a);
            
            float t = t1;
            if (t < RayTMin())
                t = t2;
            
            if (t >= RayTMin() && t <= RayTCurrent())
            {
                float3 hitPoint = origin + direction * t;
                float3 normal = normalize(hitPoint - sphere.center);
                
                ProceduralAttributes attribs;
                attribs.normal = normal;
                attribs.objectType = OBJECT_TYPE_SPHERE;
                attribs.objectIndex = primitiveIndex;
                
                ReportHit(t, 0, attribs);
            }
        }
    }
    // Check if this primitive is a plane
    else if (primitiveIndex < sphereCount + planeCount)
    {
        uint planeIndex = primitiveIndex - sphereCount;
        PlaneData plane = Planes[planeIndex];
        
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
                attribs.objectType = OBJECT_TYPE_PLANE;
                attribs.objectIndex = planeIndex;
                
                ReportHit(t, 0, attribs);
            }
        }
    }
    // Check if this primitive is a box (AABB)
    else if (primitiveIndex < sphereCount + planeCount + boxCount)
    {
        uint boxIndex = primitiveIndex - sphereCount - planeCount;
        BoxData box = Boxes[boxIndex];
        
        // AABB ray intersection (slab method)
        float3 boxMin = box.center - box.size;
        float3 boxMax = box.center + box.size;
        
        float3 invDir = 1.0 / direction;
        
        float3 t0 = (boxMin - origin) * invDir;
        float3 t1 = (boxMax - origin) * invDir;
        
        float3 tMin = min(t0, t1);
        float3 tMax = max(t0, t1);
        
        float tNear = max(max(tMin.x, tMin.y), tMin.z);
        float tFar = min(min(tMax.x, tMax.y), tMax.z);
        
        if (tNear <= tFar && tFar >= RayTMin())
        {
            float t = tNear;
            bool isEntering = true;
            if (t < RayTMin())
            {
                t = tFar;
                isEntering = false;
            }
            
            if (t >= RayTMin() && t <= RayTCurrent())
            {
                // Calculate normal based on which face was hit
                // Use the axis that contributed most to tNear/tFar
                float3 normal;
                
                if (isEntering)
                {
                    // Entering: find which axis gave tNear (maximum of tMin values)
                    // Compare differences to find the closest match
                    float3 diff = abs(float3(tNear, tNear, tNear) - tMin);
                    
                    if (diff.x <= diff.y && diff.x <= diff.z)
                        normal = float3(-sign(direction.x), 0, 0);
                    else if (diff.y <= diff.z)
                        normal = float3(0, -sign(direction.y), 0);
                    else
                        normal = float3(0, 0, -sign(direction.z));
                }
                else
                {
                    // Exiting: find which axis gave tFar (minimum of tMax values)
                    float3 diff = abs(float3(tFar, tFar, tFar) - tMax);
                    
                    if (diff.x <= diff.y && diff.x <= diff.z)
                        normal = float3(sign(direction.x), 0, 0);
                    else if (diff.y <= diff.z)
                        normal = float3(0, sign(direction.y), 0);
                    else
                        normal = float3(0, 0, sign(direction.z));
                }
                
                // Handle edge case where direction component is exactly 0
                // (sign(0) = 0 would give zero normal)
                if (length(normal) < 0.5)
                {
                    // Fallback: use hit point method
                    float3 hitPoint = origin + direction * t;
                    float3 localHit = hitPoint - box.center;
                    float3 normalizedLocal = abs(localHit) / max(box.size, float3(0.0001, 0.0001, 0.0001));
                    
                    if (normalizedLocal.x > normalizedLocal.y && normalizedLocal.x > normalizedLocal.z)
                        normal = float3(localHit.x > 0 ? 1 : -1, 0, 0);
                    else if (normalizedLocal.y > normalizedLocal.z)
                        normal = float3(0, localHit.y > 0 ? 1 : -1, 0);
                    else
                        normal = float3(0, 0, localHit.z > 0 ? 1 : -1);
                }
                
                ProceduralAttributes attribs;
                attribs.normal = normal;
                attribs.objectType = OBJECT_TYPE_BOX;
                attribs.objectIndex = boxIndex;
                
                ReportHit(t, 0, attribs);
            }
        }
    }
}

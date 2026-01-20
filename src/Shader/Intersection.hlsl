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
                // Calculate normal using slab method - which axis determines tNear/tFar
                // This is more robust than hit point method for box intersection
                float3 normal;
                
                if (isEntering)
                {
                    // Entering: normal points outward from the face we hit
                    // The face we hit is determined by which component of tMin is largest
                    if (tMin.x > tMin.y && tMin.x > tMin.z)
                    {
                        normal = float3(direction.x > 0 ? -1 : 1, 0, 0);
                    }
                    else if (tMin.y > tMin.z)
                    {
                        normal = float3(0, direction.y > 0 ? -1 : 1, 0);
                    }
                    else
                    {
                        normal = float3(0, 0, direction.z > 0 ? -1 : 1);
                    }
                }
                else
                {
                    // Exiting: normal points outward from exit face
                    // The exit face is determined by which component of tMax is smallest
                    if (tMax.x < tMax.y && tMax.x < tMax.z)
                    {
                        normal = float3(direction.x > 0 ? 1 : -1, 0, 0);
                    }
                    else if (tMax.y < tMax.z)
                    {
                        normal = float3(0, direction.y > 0 ? 1 : -1, 0);
                    }
                    else
                    {
                        normal = float3(0, 0, direction.z > 0 ? 1 : -1);
                    }
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

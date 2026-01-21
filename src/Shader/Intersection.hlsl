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
    // Check if this primitive is a box (OBB - Oriented Bounding Box)
    else if (primitiveIndex < sphereCount + planeCount + boxCount)
    {
        uint boxIndex = primitiveIndex - sphereCount - planeCount;
        BoxData box = Boxes[boxIndex];
        
        // OBB ray intersection using local space transformation
        // Transform ray to box's local coordinate system
        float3 delta = origin - box.center;
        
        // Project ray origin and direction onto box's local axes
        float3 localOrigin = float3(
            dot(delta, box.axisX),
            dot(delta, box.axisY),
            dot(delta, box.axisZ)
        );
        
        float3 localDir = float3(
            dot(direction, box.axisX),
            dot(direction, box.axisY),
            dot(direction, box.axisZ)
        );
        
        // Slab method AABB intersection in local space (robust for near-parallel rays)
        const float INF = 1e20;
        const float EPS = 1e-6;
        
        float3 t0 = float3(0, 0, 0);
        float3 t1 = float3(0, 0, 0);
        
        // X slab
        if (abs(localDir.x) < EPS)
        {
            if (localOrigin.x < -box.size.x || localOrigin.x > box.size.x) return;
            t0.x = -INF; t1.x = INF;
        }
        else
        {
            float inv = 1.0 / localDir.x;
            t0.x = (-box.size.x - localOrigin.x) * inv;
            t1.x = ( box.size.x - localOrigin.x) * inv;
        }
        
        // Y slab
        if (abs(localDir.y) < EPS)
        {
            if (localOrigin.y < -box.size.y || localOrigin.y > box.size.y) return;
            t0.y = -INF; t1.y = INF;
        }
        else
        {
            float inv = 1.0 / localDir.y;
            t0.y = (-box.size.y - localOrigin.y) * inv;
            t1.y = ( box.size.y - localOrigin.y) * inv;
        }
        
        // Z slab
        if (abs(localDir.z) < EPS)
        {
            if (localOrigin.z < -box.size.z || localOrigin.z > box.size.z) return;
            t0.z = -INF; t1.z = INF;
        }
        else
        {
            float inv = 1.0 / localDir.z;
            t0.z = (-box.size.z - localOrigin.z) * inv;
            t1.z = ( box.size.z - localOrigin.z) * inv;
        }
        
        float3 tMin = min(t0, t1);
        float3 tMax = max(t0, t1);
        
        // Track which axis produced tNear / tFar
        float tNear = tMin.x;
        int nearAxis = 0;
        if (tMin.y > tNear) { tNear = tMin.y; nearAxis = 1; }
        if (tMin.z > tNear) { tNear = tMin.z; nearAxis = 2; }
        
        float tFar = tMax.x;
        int farAxis = 0;
        if (tMax.y < tFar) { tFar = tMax.y; farAxis = 1; }
        if (tMax.z < tFar) { tFar = tMax.z; farAxis = 2; }
        
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
                // Determine hit face based on which slab we entered/exited
                float3 localNormal = float3(0, 0, 0);
                int axis = isEntering ? nearAxis : farAxis;
                
                if (axis == 0)
                    localNormal = float3(localDir.x > 0 ? -1.0 : 1.0, 0, 0);
                else if (axis == 1)
                    localNormal = float3(0, localDir.y > 0 ? -1.0 : 1.0, 0);
                else
                    localNormal = float3(0, 0, localDir.z > 0 ? -1.0 : 1.0);
                
                // Transform normal back to world space
                float3 worldNormal = box.axisX * localNormal.x + 
                                     box.axisY * localNormal.y + 
                                     box.axisZ * localNormal.z;
                worldNormal = normalize(worldNormal);
                
                ProceduralAttributes attribs;
                attribs.normal = worldNormal;
                attribs.objectType = OBJECT_TYPE_BOX;
                attribs.objectIndex = boxIndex;
                
                ReportHit(t, 0, attribs);
            }
        }
    }
}

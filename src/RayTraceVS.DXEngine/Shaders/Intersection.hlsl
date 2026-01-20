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
                // Calculate normal using hit point method - most stable for all cases
                // This approach works consistently for edges and faces alike
                float3 hitPoint = origin + direction * t;
                float3 localHit = hitPoint - box.center;
                
                // Normalize by box size to handle non-uniform boxes correctly
                float3 safeSize = max(box.size, float3(0.0001, 0.0001, 0.0001));
                float3 normalizedLocal = localHit / safeSize;
                float3 absNormalized = abs(normalizedLocal);
                
                // The face we hit is the one where the normalized coordinate is closest to 1.0
                // Use a moderate bias to ensure consistent results at edges
                const float FACE_BIAS = 0.001;
                
                float3 normal;
                float maxComponent = max(max(absNormalized.x, absNormalized.y), absNormalized.z);
                
                // Determine which face(s) we're closest to
                // At edges, multiple components will be near maxComponent
                // We need a consistent tie-breaking rule: prioritize X > Y > Z
                if (absNormalized.x >= maxComponent - FACE_BIAS)
                {
                    // X face (or X edge)
                    normal = float3(normalizedLocal.x > 0 ? 1 : -1, 0, 0);
                }
                else if (absNormalized.y >= maxComponent - FACE_BIAS)
                {
                    // Y face (or Y edge without X)
                    normal = float3(0, normalizedLocal.y > 0 ? 1 : -1, 0);
                }
                else
                {
                    // Z face
                    normal = float3(0, 0, normalizedLocal.z > 0 ? 1 : -1);
                }
                
                // Safety check: ensure normal is valid
                if (length(normal) < 0.5)
                {
                    // Fallback: use outward direction from center
                    normal = normalize(localHit);
                    // Snap to axis
                    float3 absN = abs(normal);
                    if (absN.x >= absN.y && absN.x >= absN.z)
                        normal = float3(normal.x > 0 ? 1 : -1, 0, 0);
                    else if (absN.y >= absN.z)
                        normal = float3(0, normal.y > 0 ? 1 : -1, 0);
                    else
                        normal = float3(0, 0, normal.z > 0 ? 1 : -1);
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

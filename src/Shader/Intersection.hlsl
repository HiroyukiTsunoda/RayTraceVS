// Sphere + Plane + Box intersection
#include "Common.hlsli"
#include "BoxIntersection.hlsli"

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
        
        float hitT = 0;
        float3 normal = float3(0, 0, 0);
        if (IntersectOrientedBox(origin, direction, box, RayTMin(), RayTCurrent(), hitT, normal))
        {
            ProceduralAttributes attribs;
            attribs.normal = normal;
            attribs.objectType = OBJECT_TYPE_BOX;
            attribs.objectIndex = boxIndex;
            ReportHit(hitT, 0, attribs);
        }
    }
}

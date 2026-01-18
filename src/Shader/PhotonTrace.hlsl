// Photon Trace Closest Hit Shader
// Handles photon-surface interactions for caustic generation
// Stores photons on diffuse surfaces after passing through specular surfaces

#include "Common.hlsli"

// Refract ray using Snell's law (same as in ClosestHit_Glass.hlsl)
float3 RefractPhoton(float3 incident, float3 normal, float eta)
{
    float cosI = -dot(incident, normal);
    float sin2T = eta * eta * (1.0 - cosI * cosI);
    
    if (sin2T > 1.0)
        return float3(0, 0, 0);  // Total internal reflection
    
    float cosT = sqrt(1.0 - sin2T);
    return eta * incident + (eta * cosI - cosT) * normal;
}

// Store photon in the photon map
void StorePhoton(float3 position, float3 direction, float3 color, float power)
{
    // Atomic increment of photon counter
    uint index;
    InterlockedAdd(PhotonCounter[0], 1, index);
    
    // Check bounds
    if (index >= MAX_PHOTONS)
        return;
    
    // Store photon
    Photon p;
    p.position = position;
    p.power = power;
    p.direction = direction;
    p.flags = 1;  // Valid caustic photon
    p.color = color;
    p.padding = 0;
    
    PhotonMap[index] = p;
}

[shader("closesthit")]
void PhotonTraceClosestHit(inout PhotonPayload payload, in ProceduralAttributes attribs)
{
    // Already terminated
    if (payload.terminated)
        return;
    
    // Max depth reached
    if (payload.depth >= MAX_PHOTON_BOUNCES)
    {
        payload.terminated = true;
        return;
    }
    
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
    float3 normal = attribs.normal;
    float3 incomingDir = WorldRayDirection();
    uint objectType = attribs.objectType;
    uint objectIndex = attribs.objectIndex;
    
    // Get material properties
    float4 color;
    float metallic, roughness, transmission, ior;
    
    if (objectType == OBJECT_TYPE_SPHERE)
    {
        SphereData sphere = Spheres[objectIndex];
        color = sphere.color;
        metallic = sphere.metallic;
        roughness = sphere.roughness;
        transmission = sphere.transmission;
        ior = sphere.ior;
    }
    else if (objectType == OBJECT_TYPE_PLANE)
    {
        PlaneData plane = Planes[objectIndex];
        color = plane.color;
        metallic = plane.metallic;
        roughness = plane.roughness;
        transmission = plane.transmission;
        ior = plane.ior;
    }
    else
    {
        BoxData box = Boxes[objectIndex];
        color = box.color;
        metallic = box.metallic;
        roughness = box.roughness;
        transmission = box.transmission;
        ior = box.ior;
    }
    
    // Russian roulette for path termination
    uint seed = WangHash(asuint(hitPosition.x) ^ asuint(hitPosition.y) ^ asuint(hitPosition.z) ^ payload.depth);
    float survivalProb = max(color.r, max(color.g, color.b));
    survivalProb = clamp(survivalProb, 0.1, 0.95);
    
    if (RandomFloat(seed) > survivalProb)
    {
        payload.terminated = true;
        return;
    }
    
    // Boost power to account for Russian roulette
    payload.power /= survivalProb;
    
    // Determine surface type and handle accordingly
    bool isSpecular = (metallic > 0.5) || (transmission > 0.5);
    bool isDiffuse = !isSpecular;
    
    // Modulate photon color by surface color
    payload.color *= color.rgb;
    
    if (isDiffuse)
    {
        // Diffuse surface - store photon if it has passed through specular surface
        if (payload.isCaustic)
        {
            StorePhoton(hitPosition, incomingDir, payload.color, payload.power);
        }
        
        // Diffuse reflection (for global illumination, but we terminate for caustics)
        // For pure caustics, we stop here
        payload.terminated = true;
    }
    else if (transmission > 0.5)
    {
        // Glass/transparent surface - refract or reflect
        payload.isCaustic = true;  // Mark as caustic path
        
        float3 V = -incomingDir;
        bool frontFace = dot(V, normal) > 0;
        float3 outwardNormal = frontFace ? normal : -normal;
        
        // Calculate Fresnel
        float cosTheta = abs(dot(V, outwardNormal));
        float f0 = pow((1.0 - ior) / (1.0 + ior), 2.0);
        float fresnel = f0 + (1.0 - f0) * pow(1.0 - cosTheta, 5.0);
        
        float3 newDir;
        float3 newOrigin;
        
        // Probabilistic refraction vs reflection
        if (RandomFloat(seed) > fresnel)
        {
            // Refraction
            float eta = frontFace ? (1.0 / ior) : ior;
            newDir = RefractPhoton(incomingDir, outwardNormal, eta);
            
            if (length(newDir) < 0.001)
            {
                // Total internal reflection
                newDir = reflect(incomingDir, outwardNormal);
                newOrigin = hitPosition + outwardNormal * 0.01;
            }
            else
            {
                newDir = normalize(newDir);
                newOrigin = hitPosition - outwardNormal * 0.01;
            }
        }
        else
        {
            // Reflection
            newDir = reflect(incomingDir, outwardNormal);
            newOrigin = hitPosition + outwardNormal * 0.01;
        }
        
        // Continue tracing
        RayDesc ray;
        ray.Origin = newOrigin;
        ray.Direction = newDir;
        ray.TMin = 0.001;
        ray.TMax = 10000.0;
        
        payload.depth++;
        
        TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, ray, payload);
    }
    else if (metallic > 0.5)
    {
        // Metal surface - reflect
        payload.isCaustic = true;  // Mark as caustic path
        
        float3 reflectDir = reflect(incomingDir, normal);
        
        // Add roughness to reflection
        if (roughness > 0.01)
        {
            float3 randomDir = RandomInHemisphere(normal, seed);
            reflectDir = normalize(lerp(reflectDir, randomDir, roughness * roughness));
        }
        
        // Continue tracing
        RayDesc ray;
        ray.Origin = hitPosition + normal * 0.01;
        ray.Direction = reflectDir;
        ray.TMin = 0.001;
        ray.TMax = 10000.0;
        
        payload.depth++;
        
        TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, ray, payload);
    }
}

[shader("miss")]
void PhotonTraceMiss(inout PhotonPayload payload)
{
    // Photon escaped to sky - terminate
    payload.terminated = true;
}

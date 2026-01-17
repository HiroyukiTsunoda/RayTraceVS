// Photon Emission Ray Generation Shader
// Emits photons from light sources for caustic rendering
// Part of the 2-pass photon mapping algorithm

#include "Common.hlsli"

// Generate a random direction towards a hemisphere
float3 SampleLightDirection(float3 lightDir, float spreadAngle, inout uint seed)
{
    // For point lights, spread in all directions
    // For directional lights, emit in light direction with some spread
    
    float u1 = RandomFloat(seed);
    float u2 = RandomFloat(seed);
    
    // Cosine-weighted hemisphere sampling
    float r = sqrt(u1) * spreadAngle;
    float theta = 6.28318530718 * u2;
    
    float x = r * cos(theta);
    float y = r * sin(theta);
    float z = sqrt(max(0.0, 1.0 - r * r));
    
    // Create basis around light direction
    float3 up = abs(lightDir.y) < 0.999 ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 tangent = normalize(cross(up, lightDir));
    float3 bitangent = cross(lightDir, tangent);
    
    return normalize(tangent * x + bitangent * y + lightDir * z);
}

[shader("raygeneration")]
void PhotonEmit()
{
    // Get photon index from dispatch ID
    uint photonIndex = DispatchRaysIndex().x;
    uint totalPhotons = DispatchRaysDimensions().x;
    
    // Initialize random seed based on photon index
    uint seed = WangHash(photonIndex * 1973 + 9277);
    
    // Skip if no lights
    if (Scene.NumLights == 0)
        return;
    
    // Select light source (distribute photons among lights)
    uint lightCount = 0;
    for (uint l = 0; l < Scene.NumLights; l++)
    {
        if (Lights[l].type != LIGHT_TYPE_AMBIENT)
            lightCount++;
    }
    
    if (lightCount == 0)
        return;
    
    // Find which non-ambient light to use
    uint photonsPerLight = totalPhotons / lightCount;
    uint lightIndex = photonIndex / photonsPerLight;
    
    // Find the actual light index (skipping ambient lights)
    uint actualLightIndex = 0;
    uint nonAmbientCount = 0;
    for (uint li = 0; li < Scene.NumLights; li++)
    {
        if (Lights[li].type != LIGHT_TYPE_AMBIENT)
        {
            if (nonAmbientCount == lightIndex)
            {
                actualLightIndex = li;
                break;
            }
            nonAmbientCount++;
        }
    }
    
    LightData light = Lights[actualLightIndex];
    
    // Calculate photon origin and direction based on light type
    float3 photonOrigin;
    float3 photonDir;
    float3 photonColor = light.color.rgb * light.intensity;
    float photonPower = light.intensity / float(photonsPerLight);
    
    if (light.type == LIGHT_TYPE_POINT)
    {
        // Point light: emit from light position in random direction
        photonOrigin = light.position;
        photonDir = RandomOnSphere(seed);
        
        // Adjust power for spherical emission (4*pi steradians)
        photonPower *= 4.0 * 3.14159265;
    }
    else if (light.type == LIGHT_TYPE_DIRECTIONAL)
    {
        // Directional light: emit from a virtual plane
        // Create random position on a large virtual emitter plane
        float3 lightDir = normalize(-light.position);
        
        // Create a basis for the emitter plane
        float3 up = abs(lightDir.y) < 0.999 ? float3(0, 1, 0) : float3(1, 0, 0);
        float3 right = normalize(cross(up, lightDir));
        float3 realUp = cross(lightDir, right);
        
        // Random position on plane (spread around scene)
        float radius = 20.0;  // Large enough to cover scene
        float2 offset = float2(RandomFloat(seed), RandomFloat(seed)) * 2.0 - 1.0;
        offset *= radius;
        
        photonOrigin = right * offset.x + realUp * offset.y - lightDir * 50.0;
        photonDir = lightDir;
    }
    else
    {
        // Ambient light: don't emit photons
        return;
    }
    
    // Create photon ray
    RayDesc ray;
    ray.Origin = photonOrigin;
    ray.Direction = photonDir;
    ray.TMin = 0.001;
    ray.TMax = 10000.0;
    
    // Initialize photon payload
    PhotonPayload payload;
    payload.color = photonColor;
    payload.power = photonPower;
    payload.depth = 0;
    payload.isCaustic = false;
    payload.terminated = false;
    
    // Trace the photon through the scene
    // The ClosestHit shader will handle interactions and storage
    TraceRay(
        SceneBVH,
        RAY_FLAG_NONE,
        0xFF,
        0,  // Hit group index 0 (PhotonHitGroup)
        0,
        0,  // Miss shader index 0 (PhotonTraceMiss)
        ray,
        payload
    );
}

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
    if (totalPhotons == 0)
        return;
    
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
    if (photonsPerLight == 0)
        return;
    uint lightIndex = photonIndex / photonsPerLight;
    lightIndex = min(lightIndex, lightCount - 1);
    
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
    
    // Queue-based photon tracing (TraceRay only in RayGen)
    const uint maxPhotonQueueSize = 8;
    PhotonPathState queue[maxPhotonQueueSize];
    uint queueCount = 0;

    PhotonPathState primary;
    primary.origin = photonOrigin;
    primary.tMin = 0.001;
    primary.direction = photonDir;
    primary.depth = 0;
    primary.color = photonColor;
    primary.power = photonPower;
    primary.isCaustic = 0;
    primary.padding = 0;
    queue[queueCount++] = primary;

    while (queueCount > 0)
    {
        PhotonPathState state = queue[--queueCount];
        if (state.depth >= MAX_PHOTON_BOUNCES)
        {
            continue;
        }

        RayDesc ray;
        ray.Origin = state.origin;
        ray.Direction = state.direction;
        ray.TMin = state.tMin;
        ray.TMax = 10000.0;

        PhotonPayload payload;
        payload.color = state.color;
        payload.power = state.power;
        payload.depth = state.depth;
        payload.isCaustic = (state.isCaustic != 0);
        payload.terminated = false;
        payload.childCount = 0;
        payload.photonPadding = 0;
        payload.photonPadding2 = float2(0, 0);

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

        if (payload.terminated || payload.childCount == 0)
        {
            continue;
        }

        uint childCount = min(payload.childCount, 2u);
        [loop]
        for (uint i = 0; i < childCount; i++)
        {
            if (queueCount >= maxPhotonQueueSize)
                break;
            queue[queueCount++] = payload.childPaths[i];
        }
    }
}

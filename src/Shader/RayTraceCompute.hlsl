// GPU Ray Tracing Compute Shader
// Replaces CPU ray tracing with GPU computation
#include "SharedTypes.h"
#include "BoxIntersection.hlsli"

// Hash function for pseudo-random numbers
float Hash(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// Generate random direction on hemisphere around normal, biased by roughness
float3 PerturbReflection(float3 reflectDir, float3 normal, float roughness, float2 seed)
{
    if (roughness < 0.01)
        return reflectDir;
    
    // Generate random values
    float r1 = Hash(seed);
    float r2 = Hash(seed + float2(17.3, 31.7));
    
    // Create tangent space basis
    float3 tangent = abs(normal.x) > 0.9 ? float3(0, 1, 0) : float3(1, 0, 0);
    tangent = normalize(cross(normal, tangent));
    float3 bitangent = cross(normal, tangent);
    
    // Random offset scaled by roughness (GGX-like distribution approximation)
    float angle = r1 * 6.28318;
    float radius = roughness * roughness * r2;  // roughness^2 for perceptually linear response
    
    float3 offset = (cos(angle) * tangent + sin(angle) * bitangent) * radius;
    
    // Perturb reflection direction
    float3 perturbed = normalize(reflectDir + offset);
    
    // Ensure perturbed direction is above surface
    if (dot(perturbed, normal) < 0.0)
        perturbed = reflect(perturbed, normal);
    
    return perturbed;
}

#define MAX_SPHERES 32
#define MAX_PLANES 32
#define MAX_CYLINDERS 32
#define MAX_LIGHTS 8

// Output texture
RWTexture2D<float4> OutputTexture : register(u0);

// The fallback consumes exactly the same CPU upload layout as DXR.
cbuffer SceneConstants : register(b0)
{
    SceneConstantBuffer Scene;
};

#define LIGHT_TYPE_AMBIENT 0
#define LIGHT_TYPE_POINT 1
#define LIGHT_TYPE_DIRECTIONAL 2

StructuredBuffer<SphereData> Spheres : register(t0);
StructuredBuffer<PlaneData> Planes : register(t1);
StructuredBuffer<BoxData> Boxes : register(t2);
StructuredBuffer<LightData> Lights : register(t3);

// Ray structure
struct Ray
{
    float3 Origin;
    float3 Direction;
};

// Hit information (with PBR material)
struct HitInfo
{
    bool Hit;
    float T;
    float3 Position;
    float3 Normal;
    float4 Color;
    float Metallic;
    float Roughness;
    float Transmission;
    float IOR;
};

// Intersect ray with sphere
// Using inout instead of out to suppress X4000 warnings - caller must initialize
bool IntersectSphere(Ray ray, SphereData sphere, inout float t, inout float3 normal)
{
    
    float3 oc = ray.Origin - sphere.center;
    
    float a = dot(ray.Direction, ray.Direction);
    float b = 2.0 * dot(oc, ray.Direction);
    float c = dot(oc, oc) - sphere.radius * sphere.radius;
    float discriminant = b * b - 4.0 * a * c;
    
    if (discriminant < 0.0)
        return false;
    
    t = (-b - sqrt(discriminant)) / (2.0 * a);
    
    if (t < 0.001)
    {
        t = (-b + sqrt(discriminant)) / (2.0 * a);
        if (t < 0.001)
            return false;
    }
    
    float3 hitPos = ray.Origin + ray.Direction * t;
    normal = normalize(hitPos - sphere.center);
    
    return true;
}

// Intersect ray with plane
// Using inout instead of out to suppress X4000 warnings - caller must initialize
bool IntersectPlane(Ray ray, PlaneData plane, inout float t, inout float3 normal)
{
    
    float3 n = normalize(plane.normal);
    float denom = dot(n, ray.Direction);
    
    if (abs(denom) < 0.0001)
        return false;
    
    float3 p0 = plane.position - ray.Origin;
    t = dot(p0, n) / denom;
    
    if (t < 0.001)
        return false;
    
    normal = n;
    return true;
}

// The shared query supports rotated boxes and returns outward face normals.
bool IntersectBox(Ray ray, BoxData box, inout float t, inout float3 normal)
{
    return IntersectOrientedBox(ray.Origin, ray.Direction, box, 0.001, 1e30, t, normal);
}

// Find closest intersection
HitInfo TraceRay(Ray ray)
{
    HitInfo result = (HitInfo)0;
    result.Hit = false;
    result.T = 1e30;
    result.Position = float3(0, 0, 0);
    result.Normal = float3(0, 0, 0);
    result.Color = float4(0, 0, 0, 1);
    result.Metallic = 0;
    result.Roughness = 0.5;
    result.Transmission = 0;
    result.IOR = 1.5;
    
    // Initialize before passing to intersection functions
    float t = 1e30;
    float3 normal = float3(0, 0, 0);
    
    // Check spheres
    [loop]
    for (uint i = 0; i < Scene.NumSpheres; i++)
    {
        if (IntersectSphere(ray, Spheres[i], t, normal))
        {
            if (t < result.T)
            {
                result.Hit = true;
                result.T = t;
                result.Position = ray.Origin + ray.Direction * t;
                result.Normal = normal;
                result.Color = Spheres[i].color;
                result.Metallic = Spheres[i].metallic;
                result.Roughness = Spheres[i].roughness;
                result.Transmission = Spheres[i].transmission;
                result.IOR = Spheres[i].ior;
            }
        }
    }
    
    // Check planes
    [loop]
    for (uint j = 0; j < Scene.NumPlanes; j++)
    {
        if (IntersectPlane(ray, Planes[j], t, normal))
        {
            if (t < result.T)
            {
                result.Hit = true;
                result.T = t;
                result.Position = ray.Origin + ray.Direction * t;
                result.Normal = normal;
                
                // Checkerboard in plane space (works for any plane orientation)
                float3 n = normalize(normal);
                float3 axis = (abs(n.y) < 0.999) ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
                float3 tangent = normalize(cross(axis, n));
                float3 bitangent = cross(n, tangent);
                float2 uv = float2(dot(result.Position - Planes[j].position, tangent),
                                   dot(result.Position - Planes[j].position, bitangent));

                float checkerSize = 1.0;
                int ix = (int)floor(uv.x / checkerSize);
                int iz = (int)floor(uv.y / checkerSize);
                float checker = (float)(((ix + iz) & 1) == 0);
                
                // P2-1: Distance-based contrast reduction with exponential fade
                // Exponential fade provides more natural falloff than linear
                float hitDist = t;
                float fadeDistance = 50.0;  // P3-1: Matches CHECKER_FADE_DISTANCE in Common.hlsli
                float fadeExp = exp(-hitDist / fadeDistance);
                float contrast = lerp(0.2, 1.0, fadeExp);
                
                // Apply contrast: lerp between gray (0.5) and checker pattern
                float checkerValue = lerp(0.5, checker, contrast);
                
                // Map checker value to color range (0.1 to 0.9)
                float colorValue = lerp(0.1, 0.9, checkerValue);
                result.Color = float4(colorValue, colorValue, colorValue, 1.0);
                result.Metallic = Planes[j].metallic;
                result.Roughness = Planes[j].roughness;
                result.Transmission = Planes[j].transmission;
                result.IOR = Planes[j].ior;
            }
        }
    }
    
    // Check boxes
    [loop]
    for (uint k = 0; k < Scene.NumBoxes; k++)
    {
        if (IntersectBox(ray, Boxes[k], t, normal))
        {
            if (t < result.T)
            {
                result.Hit = true;
                result.T = t;
                result.Position = ray.Origin + ray.Direction * t;
                result.Normal = normal;
                result.Color = Boxes[k].color;
                result.Metallic = Boxes[k].metallic;
                result.Roughness = Boxes[k].roughness;
                result.Transmission = Boxes[k].transmission;
                result.IOR = Boxes[k].ior;
            }
        }
    }
    
    return result;
}

// Calculate lighting
float3 CalculateLighting(HitInfo hit, Ray ray)
{
    if (!hit.Hit)
    {
        // Sky gradient background
        float t = 0.5 * (ray.Direction.y + 1.0);
        return lerp(float3(1.0, 1.0, 1.0), float3(0.5, 0.7, 1.0), t);
    }
    
    float3 finalColor = float3(0, 0, 0);
    
    // Base ambient (will be enhanced by ambient lights)
    float baseAmbient = 0.1;
    finalColor = hit.Color.rgb * baseAmbient;
    
    // Process all lights from buffer
    [loop]
    for (uint i = 0; i < Scene.NumLights; i++)
    {
        LightData light = Lights[i];
        
        if (light.type == LIGHT_TYPE_AMBIENT)
        {
            // Ambient light: uniform lighting from all directions, no shadows
            finalColor += hit.Color.rgb * light.color.rgb * light.intensity;
        }
        else if (light.type == LIGHT_TYPE_DIRECTIONAL)
        {
            // Directional light: parallel rays from a direction, with shadows
            float3 lightDir = normalize(-light.position); // Position stores direction
            
            // Shadow ray (infinite distance)
            Ray shadowRay = (Ray)0;
            shadowRay.Origin = hit.Position + hit.Normal * 0.001;
            shadowRay.Direction = lightDir;
            
            HitInfo shadowHit = TraceRay(shadowRay);
            // Glass objects don't cast shadows
            bool inShadow = shadowHit.Hit && shadowHit.Transmission < 0.01;
            
            if (!inShadow)
            {
                // Diffuse
                float diff = max(0.0, dot(hit.Normal, lightDir));
                finalColor += hit.Color.rgb * light.color.rgb * light.intensity * diff;
                
                // Specular (directional lights have specular)
                float3 viewDir = normalize(Scene.CameraPosition - hit.Position);
                float3 reflectDir = reflect(-lightDir, hit.Normal);
                float spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);
                finalColor += light.color.rgb * light.intensity * spec * 0.3;
            }
        }
        else // LIGHT_TYPE_POINT
        {
            // Point light: position-based with attenuation and shadows
            float3 lightDir = normalize(light.position - hit.Position);
            float lightDist = length(light.position - hit.Position);
            
            // Shadow ray
            Ray shadowRay = (Ray)0;
            shadowRay.Origin = hit.Position + hit.Normal * 0.001;
            shadowRay.Direction = lightDir;
            
            HitInfo shadowHit = TraceRay(shadowRay);
            // Glass objects don't cast shadows
            bool inShadow = shadowHit.Hit && shadowHit.T < lightDist && shadowHit.Transmission < 0.01;
            
            if (!inShadow)
            {
                float attenuation = 1.0 / (1.0 + lightDist * lightDist * 0.01);
                
                // Diffuse
                float diff = max(0.0, dot(hit.Normal, lightDir));
                finalColor += hit.Color.rgb * light.color.rgb * light.intensity * diff * attenuation;
                
                // Specular
                float3 viewDir = normalize(Scene.CameraPosition - hit.Position);
                float3 reflectDir = reflect(-lightDir, hit.Normal);
                float spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);
                finalColor += light.color.rgb * light.intensity * spec * 0.3 * attenuation;
            }
        }
    }
    
    // Fallback: If no lights, use default lighting
    if (Scene.NumLights == 0)
    {
        float3 lightDir = normalize(Scene.LightPosition - hit.Position);
        float lightDist = length(Scene.LightPosition - hit.Position);
        
        Ray shadowRay = (Ray)0;
        shadowRay.Origin = hit.Position + hit.Normal * 0.001;
        shadowRay.Direction = lightDir;
        
        HitInfo shadowHit = TraceRay(shadowRay);
        bool inShadow = shadowHit.Hit && shadowHit.T < lightDist;
        
        if (!inShadow)
        {
            float diff = max(0.0, dot(hit.Normal, lightDir));
            finalColor += hit.Color.rgb * Scene.LightColor.rgb * Scene.LightIntensity * diff;
            
            float3 viewDir = normalize(Scene.CameraPosition - hit.Position);
            float3 reflectDir = reflect(-lightDir, hit.Normal);
            float spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);
            finalColor += Scene.LightColor.rgb * Scene.LightIntensity * spec * 0.5;
        }
    }
    
    return saturate(finalColor);
}

// Get sky color for background/environment
float3 GetSkyColor(float3 direction)
{
    float t = 0.5 * (direction.y + 1.0);
    return lerp(float3(1.0, 1.0, 1.0), float3(0.5, 0.7, 1.0), t);
}

// Fresnel-Schlick approximation
float FresnelSchlick(float cosTheta, float f0)
{
    return f0 + (1.0 - f0) * pow(1.0 - cosTheta, 5.0);
}

// Refract ray (Snell's law)
float3 Refract(float3 incident, float3 normal, float eta)
{
    float cosI = -dot(incident, normal);
    float sin2T = eta * eta * (1.0 - cosI * cosI);
    
    if (sin2T > 1.0)
        return float3(0, 0, 0); // Total internal reflection
    
    float cosT = sqrt(1.0 - sin2T);
    return eta * incident + (eta * cosI - cosT) * normal;
}

// Main compute shader
[numthreads(8, 8, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    if (DTid.x >= Scene.ScreenWidth || DTid.y >= Scene.ScreenHeight)
        return;
    
    float3 finalColor = float3(0, 0, 0);
    uint numSamples = max(1, Scene.SamplesPerPixel);
    
    [loop]
    for (uint sampleIdx = 0; sampleIdx < numSamples; sampleIdx++)
    {
        // Sub-pixel jitter for anti-aliasing
        float2 jitter = float2(0.5, 0.5);
        if (numSamples > 1)
        {
            // Stratified sampling
            float r1 = Hash(float2(DTid.x, DTid.y) + float2(sampleIdx * 0.123, sampleIdx * 0.456));
            float r2 = Hash(float2(DTid.y, DTid.x) + float2(sampleIdx * 0.789, sampleIdx * 0.321));
            jitter = float2(r1, r2);
        }
        
        // Calculate normalized device coordinates with jitter
        float2 pixelCenter = float2(DTid.x, DTid.y) + jitter;
        float2 ndc = pixelCenter / float2(Scene.ScreenWidth, Scene.ScreenHeight) * 2.0 - 1.0;
        ndc.y = -ndc.y; // Flip Y
        
        // Generate ray using camera basis vectors
        Ray ray = (Ray)0;
        ray.Origin = Scene.CameraPosition;
        
        // Standard ray generation for ray tracing
        float3 rayDir = Scene.CameraForward
                      + Scene.CameraRight * (ndc.x * Scene.TanHalfFov * Scene.AspectRatio)
                      + Scene.CameraUp * (ndc.y * Scene.TanHalfFov);
        
        ray.Direction = normalize(rayDir);
        
        // Trace primary ray
        HitInfo hit = TraceRay(ray);
    
    float3 color = float3(0, 0, 0);
    
    if (!hit.Hit)
    {
        // Sky background
        color = GetSkyColor(ray.Direction);
    }
    else
    {
        // Determine material type and shade accordingly
        bool isGlass = (hit.Transmission > 0.01);
        bool isMetal = !isGlass && (hit.Metallic >= 0.5);
        
        if (isGlass)
        {
            // === GLASS/TRANSPARENT MATERIAL ===
            // Transmission = 1.0: fully transparent (invisible)
            // Transmission = 0.5: semi-transparent (blend surface and background)
            // Transmission = 0.0: opaque (handled by else branch, not here)
            
            float transparency = hit.Transmission; // 0.0 to 1.0
            float3 surfaceColor = hit.Color.rgb;
            float glassIOR = hit.IOR;
            float3 N = hit.Normal;
            float3 V = -ray.Direction;
            bool frontFace = dot(V, N) > 0;
            float3 outwardNormal = frontFace ? N : -N;
            
            // === 1. Get the color of what's behind (transmitted color) ===
            float3 transmittedColor = float3(0, 0, 0);
            {
                float3 currentOrigin = hit.Position;
                float3 currentDir = ray.Direction;
                
                // Apply refraction if IOR > 1
                if (glassIOR > 1.01)
                {
                    float eta = frontFace ? (1.0 / glassIOR) : glassIOR;
                    float3 refracted = Refract(ray.Direction, outwardNormal, eta);
                    if (length(refracted) > 0.001)
                    {
                        currentDir = normalize(refracted);
                    }
                }
                
                // Trace through glass surfaces (max 4 bounces)
                [loop]
                for (uint bounce = 0; bounce < Scene.MaxBounces; bounce++)
                {
                    currentOrigin = currentOrigin + currentDir * 0.01;
                    
                    Ray nextRay = (Ray)0;
                    nextRay.Origin = currentOrigin;
                    nextRay.Direction = currentDir;
                    HitInfo nextHit = TraceRay(nextRay);
                    
                    if (!nextHit.Hit)
                    {
                        transmittedColor = GetSkyColor(currentDir);
                        break;
                    }
                    else if (nextHit.Transmission < 0.01)
                    {
                        transmittedColor = CalculateLighting(nextHit, nextRay);
                        break;
                    }
                    else
                    {
                        // Another glass surface - apply refraction and continue
                        currentOrigin = nextHit.Position;
                        if (glassIOR > 1.01)
                        {
                            bool entering = dot(-currentDir, nextHit.Normal) > 0;
                            float3 refractNormal = entering ? nextHit.Normal : -nextHit.Normal;
                            float eta = entering ? (1.0 / glassIOR) : glassIOR;
                            float3 refracted = Refract(currentDir, refractNormal, eta);
                            if (length(refracted) > 0.001)
                            {
                                currentDir = normalize(refracted);
                            }
                        }
                    }
                }
            }
            
            // === 2. Get surface color (as if opaque) ===
            float3 opaqueColor = CalculateLighting(hit, ray);
            
            // === 3. Blend based on transparency ===
            // transparency = 1.0 -> fully transmitted (invisible surface)
            // transparency = 0.0 -> fully opaque
            color = lerp(opaqueColor, transmittedColor, transparency);
            
            // === 4. Add Fresnel reflection for IOR > 1 ===
            if (glassIOR > 1.01)
            {
                float f0 = pow((1.0 - glassIOR) / (1.0 + glassIOR), 2.0);
                float cosTheta = abs(dot(V, outwardNormal));
                float fresnel = FresnelSchlick(cosTheta, f0);
                
                if (fresnel > 0.01)
                {
                    float3 reflectDir = reflect(ray.Direction, outwardNormal);
                    Ray reflectRay = (Ray)0;
                    reflectRay.Origin = hit.Position + outwardNormal * 0.01;
                    reflectRay.Direction = reflectDir;
                    HitInfo reflectHit = TraceRay(reflectRay);
                    float3 reflectColor = reflectHit.Hit ? CalculateLighting(reflectHit, reflectRay) : GetSkyColor(reflectDir);
                    color = lerp(color, reflectColor, fresnel * (1.0 - transparency * 0.5));
                }
            }
        }
        else if (isMetal)
        {
            // === METAL MATERIAL ===
            // Metal: base color affects reflection color (colored reflection)
            float3 viewDir = -ray.Direction;
            float cosTheta = max(0.0, dot(viewDir, hit.Normal));
            
            // Metal F0 is the base color
            float3 f0 = hit.Color.rgb;
            
            // Fresnel (colored for metals)
            float3 fresnel = f0 + (1.0 - f0) * pow(1.0 - cosTheta, 5.0);
            
            // Reflection with roughness-based blur
            float3 reflectDir = reflect(ray.Direction, hit.Normal);
            
            // Perturb reflection based on roughness
            float2 seed = float2(DTid.x, DTid.y) + hit.Position.xy * 1000.0;
            float3 perturbedDir = PerturbReflection(reflectDir, hit.Normal, hit.Roughness, seed);
            
            Ray reflectRay = (Ray)0;
            reflectRay.Origin = hit.Position + hit.Normal * 0.001;
            reflectRay.Direction = perturbedDir;
            
            HitInfo reflectHit = TraceRay(reflectRay);
            float3 reflectColor = reflectHit.Hit ? CalculateLighting(reflectHit, reflectRay) : GetSkyColor(perturbedDir);
            
            // Metal reflection is tinted by base color (no diffuse component)
            color = reflectColor * fresnel;
        }
        else
        {
            // === DIFFUSE MATERIAL ===
            color = CalculateLighting(hit, ray);
            
            // Add subtle reflection based on Fresnel for dielectrics
            float f0 = 0.04; // Standard dielectric F0
            float3 viewDir = -ray.Direction;
            float cosTheta = max(0.0, dot(viewDir, hit.Normal));
            float fresnel = FresnelSchlick(cosTheta, f0);
            
            if (fresnel > 0.05)
            {
                float3 reflectDir = reflect(ray.Direction, hit.Normal);
                Ray reflectRay = (Ray)0;
                reflectRay.Origin = hit.Position + hit.Normal * 0.001;
                reflectRay.Direction = reflectDir;
                
                HitInfo reflectHit = TraceRay(reflectRay);
                float3 reflectColor = reflectHit.Hit ? CalculateLighting(reflectHit, reflectRay) : GetSkyColor(reflectDir);
                
                color = lerp(color, reflectColor, fresnel * (1.0 - hit.Roughness));
            }
        }
    }
    
        // Accumulate sample color
        finalColor += color;
    } // End of sample loop
    
    // Average all samples
    finalColor /= (float)numSamples;
    
    // Tone mapping / gamma (simple)
    finalColor = saturate(finalColor);
    
    // Write output (RGBA format)
    OutputTexture[DTid.xy] = float4(finalColor, 1.0);
}

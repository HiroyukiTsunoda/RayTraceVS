// GPU Ray Tracing Compute Shader
// Replaces CPU ray tracing with GPU computation

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

// Scene constants
cbuffer SceneConstants : register(b0)
{
    float3 CameraPosition;
    float CameraPadding1;
    float3 CameraForward;
    float CameraPadding2;
    float3 CameraRight;
    float CameraPadding3;
    float3 CameraUp;
    float CameraPadding4;
    float3 LightPosition;
    float LightIntensity;
    float4 LightColor;
    uint NumSpheres;
    uint NumPlanes;
    uint NumBoxes;
    uint NumLights;
    uint ScreenWidth;
    uint ScreenHeight;
    float AspectRatio;
    float TanHalfFov;
    uint SamplesPerPixel;
    uint MaxBounces;
    float Padding1;
    float Padding2;
};

// Sphere data (with PBR material) - match GPUSphere layout
struct Sphere
{
    float3 Center;
    float Radius;
    float4 Color;
    float Metallic;
    float Roughness;
    float Transmission;
    float IOR;
    float Specular;
    float Padding1;
    float Padding2;
    float Padding3;
    float3 Emission;
    float Padding4;
    float3 Absorption;
    float Padding5;
};

// Plane data (with PBR material) - match GPUPlane layout
struct Plane
{
    float3 Position;
    float Metallic;
    float3 Normal;
    float Roughness;
    float4 Color;
    float Transmission;
    float IOR;
    float Specular;
    float Padding1;
    float3 Emission;
    float Padding2;
    float3 Absorption;
    float Padding3;
};

// Box data (with PBR material) - match GPUBox layout
struct Box
{
    float3 Center;
    float Padding1;
    float3 Size;       // half-extents
    float Padding2;
    float4 Color;
    float Metallic;
    float Roughness;
    float Transmission;
    float IOR;
    float Specular;
    float Padding3;
    float Padding4;
    float Padding5;
    float3 Emission;
    float Padding6;
    float3 Absorption;
    float Padding7;
};

// Light type constants
#define LIGHT_TYPE_AMBIENT 0
#define LIGHT_TYPE_POINT 1
#define LIGHT_TYPE_DIRECTIONAL 2

// Light data
struct Light
{
    float3 Position;    // Position (Point) or Direction (Directional)
    float Intensity;
    float4 Color;
    uint Type;          // 0=Ambient, 1=Point, 2=Directional
    float3 Padding;
};

// Structured buffers
StructuredBuffer<Sphere> Spheres : register(t0);
StructuredBuffer<Plane> Planes : register(t1);
StructuredBuffer<Box> Boxes : register(t2);
StructuredBuffer<Light> Lights : register(t3);

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
bool IntersectSphere(Ray ray, Sphere sphere, inout float t, inout float3 normal)
{
    
    float3 oc = ray.Origin - sphere.Center;
    
    float a = dot(ray.Direction, ray.Direction);
    float b = 2.0 * dot(oc, ray.Direction);
    float c = dot(oc, oc) - sphere.Radius * sphere.Radius;
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
    normal = normalize(hitPos - sphere.Center);
    
    return true;
}

// Intersect ray with plane
// Using inout instead of out to suppress X4000 warnings - caller must initialize
bool IntersectPlane(Ray ray, Plane plane, inout float t, inout float3 normal)
{
    
    float3 n = normalize(plane.Normal);
    float denom = dot(n, ray.Direction);
    
    if (abs(denom) < 0.0001)
        return false;
    
    float3 p0 = plane.Position - ray.Origin;
    t = dot(p0, n) / denom;
    
    if (t < 0.001)
        return false;
    
    normal = n;
    return true;
}

// Intersect ray with box (AABB)
// Using inout instead of out to suppress X4000 warnings - caller must initialize
bool IntersectBox(Ray ray, Box box, inout float t, inout float3 normal)
{
    float3 boxMin = box.Center - box.Size;
    float3 boxMax = box.Center + box.Size;
    
    float3 invDir = 1.0 / ray.Direction;
    
    float3 t0 = (boxMin - ray.Origin) * invDir;
    float3 t1 = (boxMax - ray.Origin) * invDir;
    
    float3 tMin = min(t0, t1);
    float3 tMax = max(t0, t1);
    
    float tNear = max(max(tMin.x, tMin.y), tMin.z);
    float tFar = min(min(tMax.x, tMax.y), tMax.z);
    
    if (tNear <= tFar && tFar > 0.001)
    {
        float tHit = tNear > 0.001 ? tNear : tFar;
        if (tHit > 0.001)
        {
            t = tHit;
            
            // Calculate normal based on which face was hit
            float3 hitPoint = ray.Origin + ray.Direction * t;
            float3 localHit = hitPoint - box.Center;
            float3 absLocal = abs(localHit);
            
            // Find the dominant axis
            if (absLocal.x > absLocal.y && absLocal.x > absLocal.z)
            {
                normal = float3(sign(localHit.x), 0, 0);
            }
            else if (absLocal.y > absLocal.z)
            {
                normal = float3(0, sign(localHit.y), 0);
            }
            else
            {
                normal = float3(0, 0, sign(localHit.z));
            }
            return true;
        }
    }
    
    return false;
}

// Find closest intersection
HitInfo TraceRay(Ray ray)
{
    HitInfo result;
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
    for (uint i = 0; i < NumSpheres; i++)
    {
        if (IntersectSphere(ray, Spheres[i], t, normal))
        {
            if (t < result.T)
            {
                result.Hit = true;
                result.T = t;
                result.Position = ray.Origin + ray.Direction * t;
                result.Normal = normal;
                result.Color = Spheres[i].Color;
                result.Metallic = Spheres[i].Metallic;
                result.Roughness = Spheres[i].Roughness;
                result.Transmission = Spheres[i].Transmission;
                result.IOR = Spheres[i].IOR;
            }
        }
    }
    
    // Check planes
    for (uint j = 0; j < NumPlanes; j++)
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
                float2 uv = float2(dot(result.Position - Planes[j].Position, tangent),
                                   dot(result.Position - Planes[j].Position, bitangent));

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
                result.Metallic = Planes[j].Metallic;
                result.Roughness = Planes[j].Roughness;
                result.Transmission = Planes[j].Transmission;
                result.IOR = Planes[j].IOR;
            }
        }
    }
    
    // Check boxes
    for (uint k = 0; k < NumBoxes; k++)
    {
        if (IntersectBox(ray, Boxes[k], t, normal))
        {
            if (t < result.T)
            {
                result.Hit = true;
                result.T = t;
                result.Position = ray.Origin + ray.Direction * t;
                result.Normal = normal;
                result.Color = Boxes[k].Color;
                result.Metallic = Boxes[k].Metallic;
                result.Roughness = Boxes[k].Roughness;
                result.Transmission = Boxes[k].Transmission;
                result.IOR = Boxes[k].IOR;
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
    for (uint i = 0; i < NumLights; i++)
    {
        Light light = Lights[i];
        
        if (light.Type == LIGHT_TYPE_AMBIENT)
        {
            // Ambient light: uniform lighting from all directions, no shadows
            finalColor += hit.Color.rgb * light.Color.rgb * light.Intensity;
        }
        else if (light.Type == LIGHT_TYPE_DIRECTIONAL)
        {
            // Directional light: parallel rays from a direction, with shadows
            float3 lightDir = normalize(-light.Position); // Position stores direction
            
            // Shadow ray (infinite distance)
            Ray shadowRay;
            shadowRay.Origin = hit.Position + hit.Normal * 0.001;
            shadowRay.Direction = lightDir;
            
            HitInfo shadowHit = TraceRay(shadowRay);
            // Glass objects don't cast shadows
            bool inShadow = shadowHit.Hit && shadowHit.Transmission < 0.01;
            
            if (!inShadow)
            {
                // Diffuse
                float diff = max(0.0, dot(hit.Normal, lightDir));
                finalColor += hit.Color.rgb * light.Color.rgb * light.Intensity * diff;
                
                // Specular (directional lights have specular)
                float3 viewDir = normalize(CameraPosition - hit.Position);
                float3 reflectDir = reflect(-lightDir, hit.Normal);
                float spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);
                finalColor += light.Color.rgb * light.Intensity * spec * 0.3;
            }
        }
        else // LIGHT_TYPE_POINT
        {
            // Point light: position-based with attenuation and shadows
            float3 lightDir = normalize(light.Position - hit.Position);
            float lightDist = length(light.Position - hit.Position);
            
            // Shadow ray
            Ray shadowRay;
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
                finalColor += hit.Color.rgb * light.Color.rgb * light.Intensity * diff * attenuation;
                
                // Specular
                float3 viewDir = normalize(CameraPosition - hit.Position);
                float3 reflectDir = reflect(-lightDir, hit.Normal);
                float spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);
                finalColor += light.Color.rgb * light.Intensity * spec * 0.3 * attenuation;
            }
        }
    }
    
    // Fallback: If no lights, use default lighting
    if (NumLights == 0)
    {
        float3 lightDir = normalize(LightPosition - hit.Position);
        float lightDist = length(LightPosition - hit.Position);
        
        Ray shadowRay;
        shadowRay.Origin = hit.Position + hit.Normal * 0.001;
        shadowRay.Direction = lightDir;
        
        HitInfo shadowHit = TraceRay(shadowRay);
        bool inShadow = shadowHit.Hit && shadowHit.T < lightDist;
        
        if (!inShadow)
        {
            float diff = max(0.0, dot(hit.Normal, lightDir));
            finalColor += hit.Color.rgb * LightColor.rgb * LightIntensity * diff;
            
            float3 viewDir = normalize(CameraPosition - hit.Position);
            float3 reflectDir = reflect(-lightDir, hit.Normal);
            float spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);
            finalColor += LightColor.rgb * LightIntensity * spec * 0.5;
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
    if (DTid.x >= ScreenWidth || DTid.y >= ScreenHeight)
        return;
    
    float3 finalColor = float3(0, 0, 0);
    uint numSamples = max(1, SamplesPerPixel);
    
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
        float2 ndc = pixelCenter / float2(ScreenWidth, ScreenHeight) * 2.0 - 1.0;
        ndc.y = -ndc.y; // Flip Y
        
        // Generate ray using camera basis vectors
        Ray ray;
        ray.Origin = CameraPosition;
        
        // Standard ray generation for ray tracing
        float3 rayDir = CameraForward 
                      + CameraRight * (ndc.x * TanHalfFov * AspectRatio)
                      + CameraUp * (ndc.y * TanHalfFov);
        
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
                for (uint bounce = 0; bounce < MaxBounces; bounce++)
                {
                    currentOrigin = currentOrigin + currentDir * 0.01;
                    
                    Ray nextRay;
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
                    Ray reflectRay;
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
            
            Ray reflectRay;
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
                Ray reflectRay;
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

// GPU Ray Tracing Compute Shader
// Replaces CPU ray tracing with GPU computation

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
    uint NumCylinders;
    uint NumLights;
    uint ScreenWidth;
    uint ScreenHeight;
    float AspectRatio;
    float TanHalfFov;
};

// Sphere data
struct Sphere
{
    float3 Center;
    float Radius;
    float4 Color;
    float Reflectivity;
    float3 Padding;
};

// Plane data
struct Plane
{
    float3 Position;
    float Padding1;
    float3 Normal;
    float Padding2;
    float4 Color;
    float Reflectivity;
    float3 Padding3;
};

// Cylinder data
struct Cylinder
{
    float3 Position;
    float Radius;
    float3 Axis;
    float Height;
    float4 Color;
    float Reflectivity;
    float3 Padding;
};

// Light data
struct Light
{
    float3 Position;
    float Intensity;
    float4 Color;
};

// Structured buffers
StructuredBuffer<Sphere> Spheres : register(t0);
StructuredBuffer<Plane> Planes : register(t1);
StructuredBuffer<Cylinder> Cylinders : register(t2);
StructuredBuffer<Light> Lights : register(t3);

// Ray structure
struct Ray
{
    float3 Origin;
    float3 Direction;
};

// Hit information
struct HitInfo
{
    bool Hit;
    float T;
    float3 Position;
    float3 Normal;
    float4 Color;
    float Reflectivity;
};

// Intersect ray with sphere
bool IntersectSphere(Ray ray, Sphere sphere, out float t, out float3 normal)
{
    // Initialize out parameters to avoid uninitialized variable warnings
    t = 1e30;
    normal = float3(0, 0, 0);
    
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
bool IntersectPlane(Ray ray, Plane plane, out float t, out float3 normal)
{
    // Initialize out parameters to avoid uninitialized variable warnings
    t = 1e30;
    normal = float3(0, 0, 0);
    
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

// Intersect ray with cylinder
bool IntersectCylinder(Ray ray, Cylinder cyl, out float t, out float3 normal)
{
    // Initialize out parameters to avoid uninitialized variable warnings
    t = 1e30;
    normal = float3(0, 0, 0);
    
    float3 axis = normalize(cyl.Axis);
    float3 oc = ray.Origin - cyl.Position;
    
    // Side surface intersection
    float3 dirCrossAxis = cross(ray.Direction, axis);
    float3 ocCrossAxis = cross(oc, axis);
    
    float a = dot(dirCrossAxis, dirCrossAxis);
    float b = 2.0 * dot(dirCrossAxis, ocCrossAxis);
    float c = dot(ocCrossAxis, ocCrossAxis) - cyl.Radius * cyl.Radius;
    
    float discriminant = b * b - 4.0 * a * c;
    
    if (discriminant >= 0.0 && a > 0.0001)
    {
        float sqrtD = sqrt(discriminant);
        float t1 = (-b - sqrtD) / (2.0 * a);
        float t2 = (-b + sqrtD) / (2.0 * a);
        
        float tVals[2] = { t1, t2 };
        
        for (int i = 0; i < 2; i++)
        {
            float tVal = tVals[i];
            
            if (tVal > 0.001)
            {
                float3 hitPos = ray.Origin + ray.Direction * tVal;
                float3 localHitPoint = hitPos - cyl.Position;
                float height = dot(localHitPoint, axis);
                
                if (height >= 0.0 && height <= cyl.Height)
                {
                    t = tVal;
                    float3 hitOnAxis = cyl.Position + axis * height;
                    normal = normalize(hitPos - hitOnAxis);
                    return true;
                }
            }
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
    result.Reflectivity = 0;
    
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
                result.Reflectivity = Spheres[i].Reflectivity;
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
                bool isWhite = ((ix + iz) & 1) == 0;
                result.Color = isWhite ? float4(0.9, 0.9, 0.9, 1.0) : float4(0.1, 0.1, 0.1, 1.0);
                result.Reflectivity = Planes[j].Reflectivity;
            }
        }
    }
    
    // Check cylinders
    for (uint k = 0; k < NumCylinders; k++)
    {
        if (IntersectCylinder(ray, Cylinders[k], t, normal))
        {
            if (t < result.T)
            {
                result.Hit = true;
                result.T = t;
                result.Position = ray.Origin + ray.Direction * t;
                result.Normal = normal;
                result.Color = Cylinders[k].Color;
                result.Reflectivity = Cylinders[k].Reflectivity;
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
    
    // Ambient
    float ambient = 0.2;
    finalColor = hit.Color.rgb * ambient;
    
    // Main light (from constants)
    {
        float3 lightDir = normalize(LightPosition - hit.Position);
        float lightDist = length(LightPosition - hit.Position);
        
        // Shadow ray
        Ray shadowRay;
        shadowRay.Origin = hit.Position + hit.Normal * 0.001;
        shadowRay.Direction = lightDir;
        
        HitInfo shadowHit = TraceRay(shadowRay);
        bool inShadow = shadowHit.Hit && shadowHit.T < lightDist;
        
        if (!inShadow)
        {
            // Diffuse
            float diff = max(0.0, dot(hit.Normal, lightDir));
            finalColor += hit.Color.rgb * LightColor.rgb * LightIntensity * diff;
            
            // Specular
            float3 viewDir = normalize(CameraPosition - hit.Position);
            float3 reflectDir = reflect(-lightDir, hit.Normal);
            float spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);
            finalColor += LightColor.rgb * LightIntensity * spec * 0.5;
        }
    }
    
    // Additional lights from buffer
    for (uint i = 0; i < NumLights; i++)
    {
        float3 lightDir = normalize(Lights[i].Position - hit.Position);
        float lightDist = length(Lights[i].Position - hit.Position);
        
        // Shadow ray
        Ray shadowRay;
        shadowRay.Origin = hit.Position + hit.Normal * 0.001;
        shadowRay.Direction = lightDir;
        
        HitInfo shadowHit = TraceRay(shadowRay);
        bool inShadow = shadowHit.Hit && shadowHit.T < lightDist;
        
        if (!inShadow)
        {
            float attenuation = 1.0 / (1.0 + lightDist * lightDist * 0.01);
            
            // Diffuse
            float diff = max(0.0, dot(hit.Normal, lightDir));
            finalColor += hit.Color.rgb * Lights[i].Color.rgb * Lights[i].Intensity * diff * attenuation;
            
            // Specular
            float3 viewDir = normalize(CameraPosition - hit.Position);
            float3 reflectDir = reflect(-lightDir, hit.Normal);
            float spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);
            finalColor += Lights[i].Color.rgb * Lights[i].Intensity * spec * 0.3 * attenuation;
        }
    }
    
    return saturate(finalColor);
}

// Main compute shader
[numthreads(8, 8, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    if (DTid.x >= ScreenWidth || DTid.y >= ScreenHeight)
        return;
    
    // Calculate normalized device coordinates
    float2 pixelCenter = float2(DTid.x, DTid.y) + 0.5;
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
    
    // Calculate lighting
    float3 color = CalculateLighting(hit, ray);
    
    // Simple reflection (1 bounce)
    if (hit.Hit && hit.Reflectivity > 0.0)
    {
        Ray reflectRay;
        reflectRay.Origin = hit.Position + hit.Normal * 0.001;
        reflectRay.Direction = reflect(ray.Direction, hit.Normal);
        
        HitInfo reflectHit = TraceRay(reflectRay);
        float3 reflectColor = CalculateLighting(reflectHit, reflectRay);
        
        color = lerp(color, reflectColor, hit.Reflectivity);
    }
    
    // Write output (RGBA format)
    OutputTexture[DTid.xy] = float4(color, 1.0);
}

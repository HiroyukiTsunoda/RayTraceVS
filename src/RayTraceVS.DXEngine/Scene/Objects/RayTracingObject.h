#pragma once

#include <DirectXMath.h>

using namespace DirectX;

namespace RayTraceVS::DXEngine
{
    enum class ObjectType
    {
        Sphere,
        Plane,
        Box
    };

    struct Material
    {
        XMFLOAT4 color;
        float metallic;     // 0.0 = dielectric, 1.0 = metal
        float roughness;    // 0.0 = smooth, 1.0 = rough
        float transmission; // 0.0 = opaque, 1.0 = fully transparent (glass)
        float ior;          // Index of Refraction (default 1.5 for glass)
    };

    class RayTracingObject
    {
    public:
        virtual ~RayTracingObject() = default;

        virtual ObjectType GetType() const = 0;
        virtual void SetMaterial(const Material& mat) { material = mat; }
        Material GetMaterial() const { return material; }

    protected:
        Material material;
    };
}

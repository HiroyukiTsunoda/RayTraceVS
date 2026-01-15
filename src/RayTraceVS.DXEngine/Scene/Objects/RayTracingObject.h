#pragma once

#include <DirectXMath.h>

using namespace DirectX;

namespace RayTraceVS::DXEngine
{
    enum class ObjectType
    {
        Sphere,
        Plane,
        Cylinder
    };

    struct Material
    {
        XMFLOAT4 color;
        float reflectivity;
        float transparency;
        float ior; // Index of Refraction
        float padding;
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

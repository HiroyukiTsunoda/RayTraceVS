#include "Cylinder.h"

namespace RayTraceVS::DXEngine
{
    Cylinder::Cylinder()
        : position(0.0f, 0.0f, 0.0f)
        , axis(0.0f, 1.0f, 0.0f)
        , radius(0.5f)
        , height(2.0f)
    {
        material.color = XMFLOAT4(1.0f, 1.0f, 1.0f, 1.0f);
        material.reflectivity = 0.0f;
        material.transparency = 0.0f;
        material.ior = 1.0f;
    }

    Cylinder::Cylinder(const XMFLOAT3& position, const XMFLOAT3& axis, float radius, float height)
        : position(position)
        , axis(axis)
        , radius(radius)
        , height(height)
    {
        material.color = XMFLOAT4(1.0f, 1.0f, 1.0f, 1.0f);
        material.reflectivity = 0.0f;
        material.transparency = 0.0f;
        material.ior = 1.0f;
    }
}

#include "Plane.h"

namespace RayTraceVS::DXEngine
{
    Plane::Plane()
        : position(0.0f, 0.0f, 0.0f)
        , normal(0.0f, 1.0f, 0.0f)
    {
        material.color = XMFLOAT4(0.8f, 0.8f, 0.8f, 1.0f);
        material.reflectivity = 0.0f;
        material.transparency = 0.0f;
        material.ior = 1.0f;
    }

    Plane::Plane(const XMFLOAT3& position, const XMFLOAT3& normal)
        : position(position)
        , normal(normal)
    {
        material.color = XMFLOAT4(0.8f, 0.8f, 0.8f, 1.0f);
        material.reflectivity = 0.0f;
        material.transparency = 0.0f;
        material.ior = 1.0f;
    }
}

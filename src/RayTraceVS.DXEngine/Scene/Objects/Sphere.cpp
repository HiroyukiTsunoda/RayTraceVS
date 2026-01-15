#include "Sphere.h"

namespace RayTraceVS::DXEngine
{
    Sphere::Sphere()
        : center(0.0f, 0.0f, 0.0f)
        , radius(1.0f)
    {
        material.color = XMFLOAT4(1.0f, 1.0f, 1.0f, 1.0f);
        material.reflectivity = 0.0f;
        material.transparency = 0.0f;
        material.ior = 1.5f;
    }

    Sphere::Sphere(const XMFLOAT3& center, float radius)
        : center(center)
        , radius(radius)
    {
        material.color = XMFLOAT4(1.0f, 1.0f, 1.0f, 1.0f);
        material.reflectivity = 0.0f;
        material.transparency = 0.0f;
        material.ior = 1.5f;
    }
}

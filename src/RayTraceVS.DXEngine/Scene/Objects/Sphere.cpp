#include "Sphere.h"

namespace RayTraceVS::DXEngine
{
    Sphere::Sphere()
        : center(0.0f, 0.0f, 0.0f)
        , radius(1.0f)
    {
        material.color = XMFLOAT4(1.0f, 1.0f, 1.0f, 1.0f);
        material.metallic = 0.0f;
        material.roughness = 0.5f;
        material.transmission = 0.0f;
        material.ior = 1.5f;
        material.specular = 0.5f;
        material.emission = XMFLOAT3(0.0f, 0.0f, 0.0f);
        material.absorption = XMFLOAT3(0.0f, 0.0f, 0.0f);
    }

    Sphere::Sphere(const XMFLOAT3& center, float radius)
        : center(center)
        , radius(radius)
    {
        material.color = XMFLOAT4(1.0f, 1.0f, 1.0f, 1.0f);
        material.metallic = 0.0f;
        material.roughness = 0.5f;
        material.transmission = 0.0f;
        material.ior = 1.5f;
        material.specular = 0.5f;
        material.emission = XMFLOAT3(0.0f, 0.0f, 0.0f);
        material.absorption = XMFLOAT3(0.0f, 0.0f, 0.0f);
    }
}

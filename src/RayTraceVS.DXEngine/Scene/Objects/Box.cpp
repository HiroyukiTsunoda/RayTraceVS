#include "Box.h"

namespace RayTraceVS::DXEngine
{
    Box::Box()
        : center(0.0f, 0.0f, 0.0f)
        , size(0.5f, 0.5f, 0.5f)
    {
        material.color = XMFLOAT4(1.0f, 1.0f, 1.0f, 1.0f);
        material.metallic = 0.0f;
        material.roughness = 0.5f;
        material.transmission = 0.0f;
        material.ior = 1.0f;
    }

    Box::Box(const XMFLOAT3& center, const XMFLOAT3& size)
        : center(center)
        , size(size)
    {
        material.color = XMFLOAT4(1.0f, 1.0f, 1.0f, 1.0f);
        material.metallic = 0.0f;
        material.roughness = 0.5f;
        material.transmission = 0.0f;
        material.ior = 1.0f;
    }
}

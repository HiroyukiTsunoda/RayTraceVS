#include "Box.h"

namespace RayTraceVS::DXEngine
{
    Box::Box()
        : center(0.0f, 0.0f, 0.0f)
        , size(0.5f, 0.5f, 0.5f)
        , axisX(1.0f, 0.0f, 0.0f)
        , axisY(0.0f, 1.0f, 0.0f)
        , axisZ(0.0f, 0.0f, 1.0f)
    {
        material.color = XMFLOAT4(1.0f, 1.0f, 1.0f, 1.0f);
        material.metallic = 0.0f;
        material.roughness = 0.5f;
        material.transmission = 0.0f;
        material.ior = 1.0f;
        material.specular = 0.5f;
        material.emission = XMFLOAT3(0.0f, 0.0f, 0.0f);
        material.absorption = XMFLOAT3(0.0f, 0.0f, 0.0f);
    }

    Box::Box(const XMFLOAT3& center, const XMFLOAT3& size)
        : center(center)
        , size(size)
        , axisX(1.0f, 0.0f, 0.0f)
        , axisY(0.0f, 1.0f, 0.0f)
        , axisZ(0.0f, 0.0f, 1.0f)
    {
        material.color = XMFLOAT4(1.0f, 1.0f, 1.0f, 1.0f);
        material.metallic = 0.0f;
        material.roughness = 0.5f;
        material.transmission = 0.0f;
        material.ior = 1.0f;
        material.specular = 0.5f;
        material.emission = XMFLOAT3(0.0f, 0.0f, 0.0f);
        material.absorption = XMFLOAT3(0.0f, 0.0f, 0.0f);
    }

    Box::Box(const XMFLOAT3& center, const XMFLOAT3& size,
             const XMFLOAT3& axisX, const XMFLOAT3& axisY, const XMFLOAT3& axisZ)
        : center(center)
        , size(size)
        , axisX(axisX)
        , axisY(axisY)
        , axisZ(axisZ)
    {
        material.color = XMFLOAT4(1.0f, 1.0f, 1.0f, 1.0f);
        material.metallic = 0.0f;
        material.roughness = 0.5f;
        material.transmission = 0.0f;
        material.ior = 1.0f;
        material.specular = 0.5f;
        material.emission = XMFLOAT3(0.0f, 0.0f, 0.0f);
        material.absorption = XMFLOAT3(0.0f, 0.0f, 0.0f);
    }

    void Box::SetAxes(const XMFLOAT3& axisX, const XMFLOAT3& axisY, const XMFLOAT3& axisZ)
    {
        this->axisX = axisX;
        this->axisY = axisY;
        this->axisZ = axisZ;
    }
}

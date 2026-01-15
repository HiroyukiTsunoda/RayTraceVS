#include "Light.h"

namespace RayTraceVS::DXEngine
{
    Light::Light()
        : position(5.0f, 5.0f, -5.0f)
        , color(1.0f, 1.0f, 1.0f, 1.0f)
        , intensity(1.0f)
        , lightType(LightType::Point)
    {
    }

    Light::Light(const XMFLOAT3& pos, const XMFLOAT4& col, float intensity)
        : position(pos)
        , color(col)
        , intensity(intensity)
        , lightType(LightType::Point)
    {
    }
}

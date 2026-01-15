#pragma once

#include "RayTracingObject.h"

namespace RayTraceVS::DXEngine
{
    class Sphere : public RayTracingObject
    {
    public:
        Sphere();
        Sphere(const XMFLOAT3& center, float radius);

        ObjectType GetType() const override { return ObjectType::Sphere; }

        void SetCenter(const XMFLOAT3& center) { this->center = center; }
        void SetRadius(float radius) { this->radius = radius; }

        XMFLOAT3 GetCenter() const { return center; }
        float GetRadius() const { return radius; }

    private:
        XMFLOAT3 center;
        float radius;
    };
}

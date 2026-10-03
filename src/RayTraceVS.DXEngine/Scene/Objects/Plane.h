#pragma once

#include "RayTracingObject.h"

namespace RayTraceVS::DXEngine
{
    class Plane : public RayTracingObject
    {
    public:
        Plane();
        Plane(const XMFLOAT3& position, const XMFLOAT3& normal);

        ObjectType GetType() const override { return ObjectType::Plane; }

        void SetPosition(const XMFLOAT3& position) { this->position = position; }
        void SetNormal(const XMFLOAT3& normal) { this->normal = normal; }

        XMFLOAT3 GetPosition() const { return position; }
        XMFLOAT3 GetNormal() const { return normal; }

    private:
        XMFLOAT3 position;
        XMFLOAT3 normal;
    };
}

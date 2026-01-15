#pragma once

#include "RayTracingObject.h"

namespace RayTraceVS::DXEngine
{
    class Cylinder : public RayTracingObject
    {
    public:
        Cylinder();
        Cylinder(const XMFLOAT3& position, const XMFLOAT3& axis, float radius, float height);

        ObjectType GetType() const override { return ObjectType::Cylinder; }

        void SetPosition(const XMFLOAT3& position) { this->position = position; }
        void SetAxis(const XMFLOAT3& axis) { this->axis = axis; }
        void SetRadius(float radius) { this->radius = radius; }
        void SetHeight(float height) { this->height = height; }

        XMFLOAT3 GetPosition() const { return position; }
        XMFLOAT3 GetAxis() const { return axis; }
        float GetRadius() const { return radius; }
        float GetHeight() const { return height; }

    private:
        XMFLOAT3 position;
        XMFLOAT3 axis;
        float radius;
        float height;
    };
}

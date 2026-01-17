#pragma once

#include "RayTracingObject.h"

namespace RayTraceVS::DXEngine
{
    class Box : public RayTracingObject
    {
    public:
        Box();
        Box(const XMFLOAT3& center, const XMFLOAT3& size);

        ObjectType GetType() const override { return ObjectType::Box; }

        void SetCenter(const XMFLOAT3& center) { this->center = center; }
        void SetSize(const XMFLOAT3& size) { this->size = size; }

        XMFLOAT3 GetCenter() const { return center; }
        XMFLOAT3 GetSize() const { return size; }

    private:
        XMFLOAT3 center;
        XMFLOAT3 size;  // half-extents
    };
}

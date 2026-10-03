#pragma once

#include "RayTracingObject.h"

namespace RayTraceVS::DXEngine
{
    class Box : public RayTracingObject
    {
    public:
        Box();
        Box(const XMFLOAT3& center, const XMFLOAT3& size);
        Box(const XMFLOAT3& center, const XMFLOAT3& size, 
            const XMFLOAT3& axisX, const XMFLOAT3& axisY, const XMFLOAT3& axisZ);

        ObjectType GetType() const override { return ObjectType::Box; }

        void SetCenter(const XMFLOAT3& center) { this->center = center; }
        void SetSize(const XMFLOAT3& size) { this->size = size; }
        void SetAxes(const XMFLOAT3& axisX, const XMFLOAT3& axisY, const XMFLOAT3& axisZ);

        XMFLOAT3 GetCenter() const { return center; }
        XMFLOAT3 GetSize() const { return size; }
        XMFLOAT3 GetAxisX() const { return axisX; }
        XMFLOAT3 GetAxisY() const { return axisY; }
        XMFLOAT3 GetAxisZ() const { return axisZ; }

    private:
        XMFLOAT3 center;
        XMFLOAT3 size;  // half-extents
        // Local axes (rotation matrix columns) for OBB
        XMFLOAT3 axisX = XMFLOAT3(1.0f, 0.0f, 0.0f);
        XMFLOAT3 axisY = XMFLOAT3(0.0f, 1.0f, 0.0f);
        XMFLOAT3 axisZ = XMFLOAT3(0.0f, 0.0f, 1.0f);
    };
}

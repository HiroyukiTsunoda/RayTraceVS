#include "Camera.h"

namespace RayTraceVS::DXEngine
{
    Camera::Camera()
        : position(0.0f, 2.0f, -5.0f)
        , lookAt(0.0f, 0.0f, 0.0f)
        , upVector(0.0f, 1.0f, 0.0f)
        , fieldOfView(60.0f)
    {
    }

    Camera::Camera(const XMFLOAT3& pos, const XMFLOAT3& target, const XMFLOAT3& up, float fov)
        : position(pos)
        , lookAt(target)
        , upVector(up)
        , fieldOfView(fov)
    {
    }

    XMMATRIX Camera::GetViewMatrix() const
    {
        XMVECTOR pos = XMLoadFloat3(&position);
        XMVECTOR target = XMLoadFloat3(&lookAt);
        XMVECTOR up = XMLoadFloat3(&upVector);
        
        return XMMatrixLookAtLH(pos, target, up);
    }

    XMMATRIX Camera::GetProjectionMatrix(float aspectRatio) const
    {
        float fovRadians = XMConvertToRadians(fieldOfView);
        return XMMatrixPerspectiveFovLH(fovRadians, aspectRatio, 0.1f, 1000.0f);
    }
}

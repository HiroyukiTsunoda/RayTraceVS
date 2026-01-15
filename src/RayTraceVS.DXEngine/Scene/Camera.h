#pragma once

#include <DirectXMath.h>

using namespace DirectX;

namespace RayTraceVS::DXEngine
{
    class Camera
    {
    public:
        Camera();
        Camera(const XMFLOAT3& pos, const XMFLOAT3& target, const XMFLOAT3& up, float fov);

        void SetPosition(const XMFLOAT3& pos) { position = pos; }
        void SetLookAt(const XMFLOAT3& target) { lookAt = target; }
        void SetUp(const XMFLOAT3& up) { upVector = up; }
        void SetFieldOfView(float fov) { fieldOfView = fov; }

        XMFLOAT3 GetPosition() const { return position; }
        XMFLOAT3 GetLookAt() const { return lookAt; }
        XMFLOAT3 GetUp() const { return upVector; }
        float GetFieldOfView() const { return fieldOfView; }

        XMMATRIX GetViewMatrix() const;
        XMMATRIX GetProjectionMatrix(float aspectRatio) const;

    private:
        XMFLOAT3 position;
        XMFLOAT3 lookAt;
        XMFLOAT3 upVector;
        float fieldOfView;
    };
}

#pragma once

#include <DirectXMath.h>

using namespace DirectX;

namespace RayTraceVS::DXEngine
{
    enum class LightType
    {
        Ambient = 0,
        Point = 1,
        Directional = 2,
        Spot = 3
    };

    class Light
    {
    public:
        Light();
        Light(const XMFLOAT3& pos, const XMFLOAT4& col, float intensity);

        void SetPosition(const XMFLOAT3& pos) { position = pos; }
        void SetColor(const XMFLOAT4& col) { color = col; }
        void SetIntensity(float intensity) { this->intensity = intensity; }
        void SetType(LightType type) { lightType = type; }
        void SetRadius(float r) { radius = r; }
        void SetSoftShadowSamples(float samples) { softShadowSamples = samples; }

        XMFLOAT3 GetPosition() const { return position; }
        XMFLOAT4 GetColor() const { return color; }
        float GetIntensity() const { return intensity; }
        LightType GetType() const { return lightType; }
        float GetRadius() const { return radius; }
        float GetSoftShadowSamples() const { return softShadowSamples; }

    private:
        XMFLOAT3 position;
        XMFLOAT4 color;
        float intensity;
        LightType lightType;
        float radius = 0.0f;           // Area light radius (0 = point light)
        float softShadowSamples = 1.0f; // Number of shadow samples
    };
}

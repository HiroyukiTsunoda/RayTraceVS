#pragma once

#include <vector>
#include <memory>
#include "Camera.h"
#include "Light.h"
#include "Objects/RayTracingObject.h"

namespace RayTraceVS::DXEngine
{
    class Scene
    {
    public:
        Scene();
        ~Scene();

        void SetCamera(const Camera& cam) { camera = cam; }
        Camera& GetCamera() { return camera; }
        const Camera& GetCamera() const { return camera; }

        void SetRenderSettings(int samples, int bounces, float exp = 1.0f, int tone = 2, float stab = 1.0f, float shadow = 1.0f, bool denoiser = true, float gam = 2.2f)
        {
            samplesPerPixel = samples;
            maxBounces = bounces;
            exposure = exp;
            toneMapOperator = tone;
            denoiserStabilization = stab;
            shadowStrength = shadow;
            enableDenoiser = denoiser;
            gamma = gam;
        }
        int GetSamplesPerPixel() const { return samplesPerPixel; }
        int GetMaxBounces() const { return maxBounces; }
        float GetExposure() const { return exposure; }
        int GetToneMapOperator() const { return toneMapOperator; }
        float GetDenoiserStabilization() const { return denoiserStabilization; }
        float GetShadowStrength() const { return shadowStrength; }
        bool GetEnableDenoiser() const { return enableDenoiser; }
        float GetGamma() const { return gamma; }

        void AddObject(std::shared_ptr<RayTracingObject> obj);
        void AddLight(const Light& light);

        void Clear();

        const std::vector<std::shared_ptr<RayTracingObject>>& GetObjects() const { return objects; }
        const std::vector<Light>& GetLights() const { return lights; }

    private:
        Camera camera;
        std::vector<std::shared_ptr<RayTracingObject>> objects;
        std::vector<Light> lights;
        int samplesPerPixel = 1;
        int maxBounces = 6;
        float exposure = 1.0f;
        int toneMapOperator = 2;
        float denoiserStabilization = 1.0f;
        float shadowStrength = 1.0f;
        bool enableDenoiser = true;
        float gamma = 1.0f;
    };
}

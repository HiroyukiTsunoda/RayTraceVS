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

        void SetRenderSettings(int samples, int bounces) { samplesPerPixel = samples; maxBounces = bounces; }
        int GetSamplesPerPixel() const { return samplesPerPixel; }
        int GetMaxBounces() const { return maxBounces; }

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
        int maxBounces = 4;
    };
}

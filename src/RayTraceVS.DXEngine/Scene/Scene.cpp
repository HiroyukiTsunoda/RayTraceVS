#include "Scene.h"

namespace RayTraceVS::DXEngine
{
    Scene::Scene()
    {
    }

    Scene::~Scene()
    {
    }

    void Scene::AddObject(std::shared_ptr<RayTracingObject> obj)
    {
        objects.push_back(obj);
    }

    void Scene::AddLight(const Light& light)
    {
        lights.push_back(light);
    }

    void Scene::Clear()
    {
        objects.clear();
        lights.clear();
    }
}

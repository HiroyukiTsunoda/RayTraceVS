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

    void Scene::AddMeshCache(const MeshCacheEntry& cache)
    {
        // Store by name for lookup by instances
        meshCaches[cache.name] = cache;
    }

    void Scene::AddMeshInstance(const MeshInstance& instance)
    {
        meshInstances.push_back(instance);
    }

    void Scene::Clear()
    {
        objects.clear();
        lights.clear();
        // meshCaches is intentionally NOT cleared: mesh geometry is immutable at
        // runtime (loaded once from the FBX cache, identified by name) and is
        // re-added on every UpdateScene. Keeping it avoids re-copying the large
        // vertex/index arrays each frame. Unused entries persist until app exit,
        // which only costs a little memory.
        meshInstances.clear();
    }
}

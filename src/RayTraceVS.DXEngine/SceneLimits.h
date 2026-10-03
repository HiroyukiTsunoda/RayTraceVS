#pragma once

#include <cstddef>
#include <stdexcept>
#include <string>

namespace RayTraceVS::DXEngine::SceneLimits
{
    // These capacities define both upload allocations and GPU SRV ranges.
    inline constexpr unsigned int MaxSpheres = 32;
    inline constexpr unsigned int MaxPlanes = 32;
    inline constexpr unsigned int MaxBoxes = 32;
    inline constexpr unsigned int MaxLights = 8;

    inline void ValidateCount(size_t count, unsigned int capacity, const char* name)
    {
        if (count > capacity)
        {
            throw std::out_of_range(std::string(name) + " count " + std::to_string(count) +
                " exceeds renderer capacity " + std::to_string(capacity));
        }
    }

    inline void Validate(size_t spheres, size_t planes, size_t boxes, size_t lights)
    {
        ValidateCount(spheres, MaxSpheres, "Sphere");
        ValidateCount(planes, MaxPlanes, "Plane");
        ValidateCount(boxes, MaxBoxes, "Box");
        ValidateCount(lights, MaxLights, "Light");
    }
}

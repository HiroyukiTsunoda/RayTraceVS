#include <algorithm>
#include <cmath>
#include <iostream>
#include <stdexcept>
#include "../src/Shader/SharedTypes.h"
#include "../src/RayTraceVS.DXEngine/SceneLimits.h"

// Thin HLSL math adapters let the test execute the actual shader helper.
// The intersection algorithm itself is never duplicated in this harness.
using float3 = DirectX::XMFLOAT3;
using BoxData = RayTraceVS::DXEngine::SharedGpu::BoxData;
using std::abs;
using std::min;
using std::max;

float3 operator+(float3 a, float3 b) { return { a.x + b.x, a.y + b.y, a.z + b.z }; }
float3 operator-(float3 a, float3 b) { return { a.x - b.x, a.y - b.y, a.z - b.z }; }
float3 operator*(float3 a, float b) { return { a.x * b, a.y * b, a.z * b }; }
float dot(float3 a, float3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
float3 normalize(float3 v) { return v * (1.0f / std::sqrt(dot(v, v))); }

#include "../src/Shader/BoxIntersection.hlsli"

static int checks = 0;
static void Require(bool condition, const char* message)
{
    ++checks;
    if (!condition) throw std::runtime_error(message);
}

static bool Near(float a, float b) { return std::abs(a - b) < 0.0001f; }
static bool Near(float3 a, float3 b) { return Near(a.x, b.x) && Near(a.y, b.y) && Near(a.z, b.z); }

static void ExpectHit(BoxData box, float3 origin, float3 direction, float expectedT, float3 expectedNormal)
{
    float t = -1;
    float3 normal{};
    Require(IntersectOrientedBox(origin, direction, box, 0.001f, 100.0f, t, normal), "Expected a box hit");
    Require(Near(t, expectedT), "Incorrect hit distance");
    Require(Near(normal, expectedNormal), "Incorrect outward face normal");
}

int main()
{
    try
    {
        using namespace RayTraceVS::DXEngine;
        SceneLimits::Validate(0, 0, 0, 0);
        SceneLimits::Validate(32, 32, 32, 8);
        checks += 2;
        const size_t oversized[][4] = { {33,0,0,0}, {0,33,0,0}, {0,0,33,0}, {0,0,0,9} };
        for (const auto& counts : oversized)
        {
            bool rejected = false;
            try { SceneLimits::Validate(counts[0], counts[1], counts[2], counts[3]); }
            catch (const std::out_of_range&) { rejected = true; }
            Require(rejected, "Scene larger than its GPU allocation must be rejected");
        }

        BoxData box{};
        box.size = { 2, 1, 0.5f };
        box.axisX = { 1, 0, 0 };
        box.axisY = { 0, 1, 0 };
        box.axisZ = { 0, 0, 1 };

        // Every entry/exit face must have an outward normal, including rays
        // originating inside a transparent box (the original DXR regression).
        const float3 directions[] = { {1,0,0}, {-1,0,0}, {0,1,0}, {0,-1,0}, {0,0,1}, {0,0,-1} };
        const float extents[] = { 2, 2, 1, 1, 0.5f, 0.5f };
        for (int i = 0; i < 6; ++i)
        {
            ExpectHit(box, {0,0,0}, directions[i], extents[i], directions[i]);
            ExpectHit(box, directions[i] * -5.0f, directions[i], 5.0f - extents[i], directions[i] * -1.0f);
        }

        // A wide, shallow box must use the struck slab, not the largest
        // coordinate of the hit point, to choose its normal.
        ExpectHit(box, { 1.8f, 0, -5 }, { 0, 0, 1 }, 4.5f, { 0, 0, -1 });
        ExpectHit(box, { 2, 0, -5 }, { 0, 0, 1 }, 4.5f, { 0, 0, -1 });

        float t = -1;
        float3 normal{};
        Require(!IntersectOrientedBox({ 3, 0, -5 }, { 0, 0, 1 }, box, 0.001f, 100.0f, t, normal), "Parallel ray outside slab must miss");
        Require(!IntersectOrientedBox({ 0, 0, -5 }, { 0, 0, 1 }, box, 0.001f, 4.0f, t, normal), "Hit beyond maxT must be rejected");
        Require(!IntersectOrientedBox({ 0, 0, 0 }, { 0, 0, 0 }, box, 0.001f, 100.0f, t, normal), "Zero direction must miss");

        const float c = std::sqrt(0.5f);
        box.center = { 3, -2, 1 };
        box.axisX = { c, 0, -c };
        box.axisZ = { c, 0, c };
        ExpectHit(box, box.center - box.axisX * 5.0f, box.axisX, 3.0f, box.axisX * -1.0f);
        ExpectHit(box, box.center, box.axisZ, 0.5f, box.axisZ);

        std::cout << "Native GPU layout and OBB contract checks passed: " << checks << '\n';
        return 0;
    }
    catch (const std::exception& ex)
    {
        std::cerr << ex.what() << '\n';
        return 1;
    }
}

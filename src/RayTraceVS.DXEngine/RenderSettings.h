#pragma once

// Render settings passed from the Interop layer down to the Scene.
// Plain POD with no other dependencies so it can be safely included from
// both native code (Scene, NativeBridge) and C++/CLI code (EngineWrapper).
// Adding a new render parameter only requires adding a field here,
// in Interop SceneData.h (managed mirror) and using it in the engine.

namespace RayTraceVS::DXEngine
{
    struct RenderSettings
    {
        int samplesPerPixel = 1;
        int maxBounces = 6;
        int traceRecursionDepth = 2;
        float exposure = 1.0f;
        int toneMapOperator = 2;
        float denoiserStabilization = 1.0f;
        float shadowStrength = 1.0f;
        float shadowAbsorptionScale = 4.0f;
        bool enableDenoiser = true;
        float gamma = 1.0f;
        int photonDebugMode = 0;
        float photonDebugScale = 1.0f;

        // P1 optimization settings
        float lightAttenuationConstant = 1.0f;
        float lightAttenuationLinear = 0.0f;
        float lightAttenuationQuadratic = 0.01f;
        int maxShadowLights = 2;
        float nrdBypassDistanceThreshold = 8.0f;
        float nrdBypassBlendRange = 2.0f;
    };
}

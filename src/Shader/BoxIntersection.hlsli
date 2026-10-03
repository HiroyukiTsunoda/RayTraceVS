#ifndef BOX_INTERSECTION_HLSLI
#define BOX_INTERSECTION_HLSLI

// Keep this geometric query independent of ray-tracing intrinsics so both
// rendering paths (and the CPU contract harness) use the same slab test.
#ifdef __cplusplus
#define BOX_INOUT(type) type&
#else
#define BOX_INOUT(type) inout type
#endif

float BoxComponent(float3 value, int axis)
{
    return axis == 0 ? value.x : (axis == 1 ? value.y : value.z);
}

bool IntersectOrientedBox(float3 origin, float3 direction, BoxData box,
                          float minT, float maxT,
                          BOX_INOUT(float) hitT, BOX_INOUT(float3) normal)
{
    float3 delta = origin - box.center;
    float3 localOrigin = float3(dot(delta, box.axisX), dot(delta, box.axisY), dot(delta, box.axisZ));
    float3 localDir = float3(dot(direction, box.axisX), dot(direction, box.axisY), dot(direction, box.axisZ));

    float nearT = -1e30f;
    float farT = 1e30f;
    int nearAxis = -1;
    int farAxis = -1;

#ifndef __cplusplus
    [unroll]
#endif
    for (int axis = 0; axis < 3; ++axis)
    {
        float axisDir = BoxComponent(localDir, axis);
        float axisOrigin = BoxComponent(localOrigin, axis);
        float axisSize = BoxComponent(box.size, axis);
        if (abs(axisDir) < 1e-6f)
        {
            if (axisOrigin < -axisSize || axisOrigin > axisSize)
                return false;
            continue;
        }

        float t0 = (-axisSize - axisOrigin) / axisDir;
        float t1 = ( axisSize - axisOrigin) / axisDir;
        float slabNear = min(t0, t1);
        float slabFar = max(t0, t1);
        if (slabNear > nearT) { nearT = slabNear; nearAxis = axis; }
        if (slabFar < farT) { farT = slabFar; farAxis = axis; }
    }

    bool entering = nearT >= minT;
    float candidateT = entering ? nearT : farT;
    int hitAxis = entering ? nearAxis : farAxis;
    if (nearT > farT || candidateT < minT || candidateT > maxT || hitAxis < 0)
        return false;

    float axisDirection = hitAxis == 0 ? localDir.x : (hitAxis == 1 ? localDir.y : localDir.z);
    float entrySign = axisDirection > 0 ? -1.0f : 1.0f;
    // The exit face has the opposite outward normal. Closest-hit uses this
    // sign to distinguish entering and leaving a transparent solid.
    float normalSign = entering ? entrySign : -entrySign;
    float3 localNormal = hitAxis == 0 ? float3(normalSign, 0, 0) :
                        (hitAxis == 1 ? float3(0, normalSign, 0) : float3(0, 0, normalSign));
    normal = normalize(box.axisX * localNormal.x + box.axisY * localNormal.y + box.axisZ * localNormal.z);
    hitT = candidateT;
    return true;
}

#undef BOX_INOUT
#endif

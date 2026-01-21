// Any-Hit Shader to skip self-intersection
// Used for reflection rays to avoid hitting the same object's other faces

#include "Common.hlsli"

[shader("anyhit")]
void AnyHit_SkipSelf(inout RayPayload payload, in ProceduralAttributes attribs)
{
    // If targetObjectType is set (non-zero) and matches current hit, skip it
    if (payload.targetObjectType != 0 &&
        payload.targetObjectType == attribs.objectType &&
        payload.targetObjectIndex == attribs.objectIndex)
    {
        // Ignore this hit and continue searching for next intersection
        IgnoreHit();
        return;
    }
    
    // Accept this hit (default behavior)
}

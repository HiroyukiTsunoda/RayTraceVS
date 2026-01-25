// Any-Hit Shader to skip self-intersection
// Used for reflection rays to avoid hitting the same object's other faces

#include "Common.hlsli"

[shader("anyhit")]
void AnyHit_SkipSelf(inout RadiancePayload payload, in ProceduralAttributes attribs)
{
    // Self-skip disabled (no per-ray skip data in payload).
}

// Any-Hit Shader to skip self-intersection
// Used for reflection rays to avoid hitting the same object's other faces

#include "Common.hlsli"

[shader("anyhit")]
void AnyHit_SkipSelf(inout RadiancePayload payload, in ProceduralAttributes attribs)
{
    if ((payload.rayFlags & RAYFLAG_SKIP_SELF) == 0 || payload.skipObjectType == OBJECT_TYPE_INVALID)
        return;
    
    if (payload.skipObjectType == attribs.objectType && payload.skipObjectIndex == attribs.objectIndex)
    {
        IgnoreHit();
    }
}

[shader("anyhit")]
void AnyHit_SkipSelf_Triangle(inout RadiancePayload payload, in BuiltInTriangleIntersectionAttributes attribs)
{
    if ((payload.rayFlags & RAYFLAG_SKIP_SELF) == 0 || payload.skipObjectType != OBJECT_TYPE_MESH)
        return;
    
    if (payload.skipObjectIndex == InstanceID())
    {
        IgnoreHit();
    }
}

// BuildPhotonHash.hlsl
// Compute shader to build spatial hash table from photon map
// This enables O(1) photon lookup instead of O(N) brute force search

// ============================================
// Spatial Hash Constants (must match Common.hlsli)
// ============================================
#define PHOTON_HASH_TABLE_SIZE 65536
#define MAX_PHOTONS_PER_CELL 64
#define MAX_PHOTONS 262144

// ============================================
// Structures (must match Common.hlsli)
// ============================================
struct Photon
{
    float3 position;
    float power;
    float3 direction;
    uint flags;
    float3 color;
    float padding;
};

struct PhotonHashCell
{
    uint count;
    uint photonIndices[MAX_PHOTONS_PER_CELL];
};

// ============================================
// Resources
// ============================================
RWStructuredBuffer<Photon> PhotonMap : register(u0);
RWStructuredBuffer<PhotonHashCell> PhotonHashTable : register(u1);

cbuffer PhotonHashConstants : register(b0)
{
    uint PhotonCount;       // Number of valid photons
    float CellSize;         // Cell size = PhotonRadius * 2
    float2 Padding;
};

// ============================================
// Hash Function (must match Common.hlsli)
// ============================================
uint HashPhotonPosition(float3 pos, float cellSize)
{
    int3 cell = int3(floor(pos / cellSize));
    uint hash = (uint(cell.x) * 73856093u) ^ 
                (uint(cell.y) * 19349663u) ^ 
                (uint(cell.z) * 83492791u);
    return hash % PHOTON_HASH_TABLE_SIZE;
}

// ============================================
// Clear Hash Table (run before building)
// ============================================
[numthreads(256, 1, 1)]
void ClearPhotonHash(uint3 id : SV_DispatchThreadID)
{
    uint cellIndex = id.x;
    if (cellIndex >= PHOTON_HASH_TABLE_SIZE)
        return;
    
    // Reset cell count to 0
    PhotonHashTable[cellIndex].count = 0;
    
    // Optional: Clear indices (not strictly necessary since count=0)
    // for (uint i = 0; i < MAX_PHOTONS_PER_CELL; i++)
    //     PhotonHashTable[cellIndex].photonIndices[i] = 0;
}

// ============================================
// Build Hash Table (run after photon emission)
// ============================================
[numthreads(256, 1, 1)]
void BuildPhotonHash(uint3 id : SV_DispatchThreadID)
{
    uint photonIndex = id.x;
    if (photonIndex >= PhotonCount)
        return;
    
    // Read photon data
    Photon p = PhotonMap[photonIndex];
    
    // Skip invalid photons
    if (p.flags == 0)
        return;
    
    // Calculate hash cell for this photon's position
    uint cellHash = HashPhotonPosition(p.position, CellSize);
    
    // Atomically increment cell count and get slot index
    uint slotIndex;
    InterlockedAdd(PhotonHashTable[cellHash].count, 1, slotIndex);
    
    // If we have room in this cell, store the photon index
    if (slotIndex < MAX_PHOTONS_PER_CELL)
    {
        PhotonHashTable[cellHash].photonIndices[slotIndex] = photonIndex;
    }
    // Note: If slotIndex >= MAX_PHOTONS_PER_CELL, photon is dropped from this cell
    // This is acceptable as it only affects query accuracy slightly in high-density areas
}

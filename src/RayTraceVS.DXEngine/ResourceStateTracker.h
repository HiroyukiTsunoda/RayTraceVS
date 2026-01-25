#pragma once

#include <d3d12.h>
#include <unordered_map>
#include <vector>

namespace RayTraceVS::DXEngine
{
    class ResourceStateTracker
    {
    public:
        void RegisterResource(ID3D12Resource* resource, D3D12_RESOURCE_STATES initialState);
        void NotifyState(ID3D12Resource* resource, D3D12_RESOURCE_STATES state);
        D3D12_RESOURCE_STATES GetState(ID3D12Resource* resource,
                                       D3D12_RESOURCE_STATES fallbackState = D3D12_RESOURCE_STATE_COMMON) const;

        void Transition(ID3D12Resource* resource, D3D12_RESOURCE_STATES desiredState);
        void Transition(ID3D12Resource* resource,
                        D3D12_RESOURCE_STATES beforeState,
                        D3D12_RESOURCE_STATES afterState);

        void AddUavBarrier(ID3D12Resource* resource);
        void Flush(ID3D12GraphicsCommandList* cmdList);
        void Reset();

    private:
        std::unordered_map<ID3D12Resource*, D3D12_RESOURCE_STATES> m_currentStates;
        std::vector<D3D12_RESOURCE_BARRIER> m_pendingBarriers;
    };
}

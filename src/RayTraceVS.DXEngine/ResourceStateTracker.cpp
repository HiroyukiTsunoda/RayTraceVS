#include "ResourceStateTracker.h"

namespace RayTraceVS::DXEngine
{
    namespace
    {
        D3D12_RESOURCE_BARRIER MakeTransition(ID3D12Resource* resource,
                                              D3D12_RESOURCE_STATES beforeState,
                                              D3D12_RESOURCE_STATES afterState)
        {
            D3D12_RESOURCE_BARRIER barrier = {};
            barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            barrier.Transition.pResource = resource;
            barrier.Transition.StateBefore = beforeState;
            barrier.Transition.StateAfter = afterState;
            barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            return barrier;
        }
    }

    void ResourceStateTracker::RegisterResource(ID3D12Resource* resource, D3D12_RESOURCE_STATES initialState)
    {
        if (!resource)
            return;
        m_currentStates[resource] = initialState;
    }

    void ResourceStateTracker::NotifyState(ID3D12Resource* resource, D3D12_RESOURCE_STATES state)
    {
        if (!resource)
            return;
        m_currentStates[resource] = state;
    }

    D3D12_RESOURCE_STATES ResourceStateTracker::GetState(ID3D12Resource* resource,
                                                         D3D12_RESOURCE_STATES fallbackState) const
    {
        if (!resource)
            return fallbackState;
        auto stateIt = m_currentStates.find(resource);
        if (stateIt == m_currentStates.end())
            return fallbackState;
        return stateIt->second;
    }

    void ResourceStateTracker::Transition(ID3D12Resource* resource, D3D12_RESOURCE_STATES desiredState)
    {
        if (!resource)
            return;
        D3D12_RESOURCE_STATES currentState = GetState(resource, D3D12_RESOURCE_STATE_COMMON);
        if (currentState == desiredState)
            return;
        m_pendingBarriers.push_back(MakeTransition(resource, currentState, desiredState));
        m_currentStates[resource] = desiredState;
    }

    void ResourceStateTracker::Transition(ID3D12Resource* resource,
                                          D3D12_RESOURCE_STATES beforeState,
                                          D3D12_RESOURCE_STATES afterState)
    {
        if (!resource)
            return;
        if (beforeState == afterState)
        {
            m_currentStates[resource] = afterState;
            return;
        }
        m_pendingBarriers.push_back(MakeTransition(resource, beforeState, afterState));
        m_currentStates[resource] = afterState;
    }

    void ResourceStateTracker::AddUavBarrier(ID3D12Resource* resource)
    {
        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        barrier.UAV.pResource = resource;
        m_pendingBarriers.push_back(barrier);
    }

    void ResourceStateTracker::Flush(ID3D12GraphicsCommandList* cmdList)
    {
        if (!cmdList || m_pendingBarriers.empty())
            return;
        cmdList->ResourceBarrier(static_cast<UINT>(m_pendingBarriers.size()), m_pendingBarriers.data());
        m_pendingBarriers.clear();
    }

    void ResourceStateTracker::Reset()
    {
        m_currentStates.clear();
        m_pendingBarriers.clear();
    }
}

// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#include "d3d12_bridge.h"
#include <cstdio>
#include <vector>

#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dxguid.lib")

D3D12Bridge::D3D12Bridge() {
    QueryPerformanceFrequency(&m_perfFrequency);
}

D3D12Bridge::~D3D12Bridge() {
    Shutdown();
}

bool D3D12Bridge::InitializeFrom9On12(IDirect3DDevice9* pDevice9) {
    if (!pDevice9) {
        D3D12Log("ERROR: InitializeFrom9On12 called with null IDirect3DDevice9!");
        return false;
    }

    m_pD3D9Device = pDevice9;

    D3D12Log("================================================================================");
    D3D12Log("[D3D12_BRIDGE] Initializing Direct3D 12 extraction from IDirect3DDevice9 (0x%p)...", pDevice9);

    // 1. Query the IDirect3DDevice9On12 interface
    HRESULT hr = pDevice9->QueryInterface(IID_IDirect3DDevice9On12, (void**)&m_pDevice9On12);
    if (FAILED(hr) || !m_pDevice9On12) {
        D3D12Log("ERROR: pDevice9->QueryInterface(IID_IDirect3DDevice9On12) failed! hr=0x%08X", hr);
        D3D12Log("Make sure Direct3DCreate9On12 / Direct3DCreate9On12Ex was used with Enable9On12 = TRUE.");
        return false;
    }
    D3D12Log("[D3D12_BRIDGE] SUCCESS: IDirect3DDevice9On12 interface resolved: 0x%p", m_pDevice9On12);

    // 2. Extract the live native ID3D12Device
    hr = m_pDevice9On12->GetD3D12Device(IID_PPV_ARGS(&m_pD3D12Device));
    if (FAILED(hr) || !m_pD3D12Device) {
        D3D12Log("ERROR: m_pDevice9On12->GetD3D12Device() failed! hr=0x%08X", hr);
        return false;
    }
    D3D12Log("[D3D12_BRIDGE] SUCCESS: Live native ID3D12Device extracted: 0x%p", m_pD3D12Device);

    // 3. Match LUID via DXGI to inspect physical hardware adapter
    LUID adapterLuid = m_pD3D12Device->GetAdapterLuid();
    D3D12Log("[D3D12_BRIDGE] Adapter LUID: %08lX-%08lX", adapterLuid.HighPart, adapterLuid.LowPart);

    IDXGIFactory4* pDxgiFactory = nullptr;
    if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&pDxgiFactory)))) {
        IDXGIAdapter1* pAdapter = nullptr;
        for (UINT i = 0; pDxgiFactory->EnumAdapters1(i, &pAdapter) != DXGI_ERROR_NOT_FOUND; ++i) {
            DXGI_ADAPTER_DESC1 desc = {};
            pAdapter->GetDesc1(&desc);
            if (desc.AdapterLuid.LowPart == adapterLuid.LowPart && desc.AdapterLuid.HighPart == adapterLuid.HighPart) {
                char nameBuf[256] = {0};
                WideCharToMultiByte(CP_UTF8, 0, desc.Description, -1, nameBuf, sizeof(nameBuf), nullptr, nullptr);
                m_gpuName = nameBuf;
                m_dedicatedVram = desc.DedicatedVideoMemory;

                D3D12Log("[D3D12_BRIDGE] Physical GPU: %s", m_gpuName.c_str());
                D3D12Log("[D3D12_BRIDGE] Dedicated VRAM: %.2f GB (%llu bytes)", 
                         (double)m_dedicatedVram / (1024.0 * 1024.0 * 1024.0), m_dedicatedVram);
                pAdapter->Release();
                break;
            }
            pAdapter->Release();
        }
        pDxgiFactory->Release();
    }

    // 4. Query supported D3D12 Feature Levels
    D3D_FEATURE_LEVEL featureLevels[] = {
        D3D_FEATURE_LEVEL_12_2,
        D3D_FEATURE_LEVEL_12_1,
        D3D_FEATURE_LEVEL_12_0,
        D3D_FEATURE_LEVEL_11_1,
        D3D_FEATURE_LEVEL_11_0
    };
    D3D12_FEATURE_DATA_FEATURE_LEVELS flData = {
        _countof(featureLevels), featureLevels, D3D_FEATURE_LEVEL_11_0
    };
    if (SUCCEEDED(m_pD3D12Device->CheckFeatureSupport(D3D12_FEATURE_FEATURE_LEVELS, &flData, sizeof(flData)))) {
        switch (flData.MaxSupportedFeatureLevel) {
            case D3D_FEATURE_LEVEL_12_2: m_featureLevelStr = "12_2 (DirectX 12 Ultimate)"; break;
            case D3D_FEATURE_LEVEL_12_1: m_featureLevelStr = "12_1"; break;
            case D3D_FEATURE_LEVEL_12_0: m_featureLevelStr = "12_0"; break;
            case D3D_FEATURE_LEVEL_11_1: m_featureLevelStr = "11_1"; break;
            default:                     m_featureLevelStr = "11_0"; break;
        }
    }
    D3D12Log("[D3D12_BRIDGE] Maximum Hardware Feature Level: %s", m_featureLevelStr.c_str());

    // 5. Query Advanced D3D12 Hardware Capabilities
    D3D12_FEATURE_DATA_D3D12_OPTIONS5 opt5 = {};
    if (SUCCEEDED(m_pD3D12Device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS5, &opt5, sizeof(opt5)))) {
        D3D12Log("[D3D12_BRIDGE] DirectX Raytracing (DXR) Support: %s (Tier %u)", 
                 opt5.RaytracingTier != D3D12_RAYTRACING_TIER_NOT_SUPPORTED ? "YES" : "NO", 
                 (unsigned)opt5.RaytracingTier);
    }

    D3D12_FEATURE_DATA_D3D12_OPTIONS7 opt7 = {};
    if (SUCCEEDED(m_pD3D12Device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS7, &opt7, sizeof(opt7)))) {
        D3D12Log("[D3D12_BRIDGE] Mesh Shader Support: %s (Tier %u)", 
                 opt7.MeshShaderTier != D3D12_MESH_SHADER_TIER_NOT_SUPPORTED ? "YES" : "NO", 
                 (unsigned)opt7.MeshShaderTier);
    }

    m_bIsD3D12Active = true;
    D3D12Log("[D3D12_BRIDGE] DIRECTX 12 SUBSYSTEM FULLY OPERATIONAL!");
    D3D12Log("================================================================================");

    return true;
}

void D3D12Bridge::Shutdown() {
    if (m_bIsD3D12Active) {
        D3D12Log("[D3D12_BRIDGE] Shutting down D3D12 subsystem...");
        m_bIsD3D12Active = false;
    }
    if (m_pD3D12Device) {
        m_pD3D12Device->Release();
        m_pD3D12Device = nullptr;
    }
    if (m_pDevice9On12) {
        m_pDevice9On12->Release();
        m_pDevice9On12 = nullptr;
    }
    m_pD3D9Device = nullptr;
}

void D3D12Bridge::OnBeginFrame(uint64_t frameIndex) {
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    if (m_perfFrequency.QuadPart > 0 && m_lastFrameTime.QuadPart > 0) {
        m_frameDeltaMs = (double)(now.QuadPart - m_lastFrameTime.QuadPart) * 1000.0 / (double)m_perfFrequency.QuadPart;
        if (m_frameDeltaMs > 0.001) {
            m_instantFps = 1000.0 / m_frameDeltaMs;
        }
    }
    m_lastFrameTime = now;

    if (frameIndex <= 30 || (frameIndex % 300 == 0)) {
        D3D12Log("[D3D12_FRAME] BeginFrame #%llu | Delta: %.2f ms | Instant FPS: %.1f | API: DIRECTX 12 (%s)", 
                 frameIndex, m_frameDeltaMs, m_instantFps, m_featureLevelStr.c_str());
    }

    if (frameIndex == 1 || frameIndex == 30 || (frameIndex % 300 == 0)) {
        HWND hWnd = FindWindowA("K2_Direct3D", nullptr);
        if (!hWnd) hWnd = GetActiveWindow();
        if (hWnd) {
            char title[256];
            snprintf(title, sizeof(title), "Heroes of Newerth [DIRECTX 12 ACTIVE | %s | %s | FPS: %.0f]", 
                     m_gpuName.c_str(), m_featureLevelStr.c_str(), m_instantFps);
            SetWindowTextA(hWnd, title);
        }
    }
}

void D3D12Bridge::OnEndFrame(uint64_t frameIndex) {
    if (frameIndex <= 30 || (frameIndex % 300 == 0)) {
        D3D12Log("[D3D12_FRAME] EndFrame #%llu | Presented via D3D12 Command Queue on %s", 
                 frameIndex, m_gpuName.c_str());
    }
}

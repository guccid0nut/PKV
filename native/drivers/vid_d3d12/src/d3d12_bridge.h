// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <initguid.h>
#include <d3d9.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <d3d9on12.h>
#include <string>
#include <cstdint>
#include <atomic>

void D3D12Log(const char* format, ...);

class D3D12Bridge {
public:
    static D3D12Bridge& Get() {
        static D3D12Bridge instance;
        return instance;
    }

    bool InitializeFrom9On12(IDirect3DDevice9* pDevice9);
    void Shutdown();

    void OnBeginFrame(uint64_t frameIndex);
    void OnEndFrame(uint64_t frameIndex);

    bool IsD3D12Active() const { return m_bIsD3D12Active; }
    const std::string& GetGpuName() const { return m_gpuName; }
    const std::string& GetFeatureLevelString() const { return m_featureLevelStr; }
    uint64_t GetDedicatedVramBytes() const { return m_dedicatedVram; }

    ID3D12Device* GetD3D12Device() const { return m_pD3D12Device; }
    IDirect3DDevice9On12* GetDevice9On12() const { return m_pDevice9On12; }

private:
    D3D12Bridge();
    ~D3D12Bridge();

    D3D12Bridge(const D3D12Bridge&) = delete;
    D3D12Bridge& operator=(const D3D12Bridge&) = delete;

    std::atomic<bool>     m_bIsD3D12Active{false};
    IDirect3DDevice9*     m_pD3D9Device = nullptr;
    IDirect3DDevice9On12* m_pDevice9On12 = nullptr;
    ID3D12Device*         m_pD3D12Device = nullptr;

    std::string           m_gpuName = "Unknown GPU";
    std::string           m_featureLevelStr = "Unknown";
    uint64_t              m_dedicatedVram = 0;

    LARGE_INTEGER         m_perfFrequency = {0};
    LARGE_INTEGER         m_lastFrameTime = {0};
    double                m_frameDeltaMs = 0.0;
    double                m_instantFps = 0.0;
};

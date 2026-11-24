// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: rcas_filter.h
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: Phase 13 AMD FidelityFX RCAS Contrast Adaptive Sharpening
// ============================================================================

#pragma once

#include <vulkan/vulkan.h>
#include <cstdint>
#include <mutex>
#include <string>

#include "layer_manifest.h"

class RcasFilter {
public:
    RcasFilter();
    ~RcasFilter();

    bool Initialize(
        VkDevice device,
        VkPhysicalDevice physicalDevice,
        VkQueue queue,
        uint32_t queueFamilyIndex,
        VkFormat format,
        uint32_t width,
        uint32_t height,
        const LayerDeviceDispatchTable& deviceDispatch,
        const LayerInstanceDispatchTable& instanceDispatch);

    void Cleanup();

    bool IsInitialized() const { return m_isInitialized; }
    bool IsEnabled() const { return m_isEnabled; }
    float GetSharpness() const { return m_sharpness; }

    bool ApplySharpening(
        VkQueue queue,
        VkImage swapchainImage,
        uint32_t waitSemaphoreCount,
        const VkSemaphore* pWaitSemaphores,
        VkSemaphore* pOutWaitSemaphore);

private:
    VkDevice m_device;
    VkPhysicalDevice m_physicalDevice;
    VkQueue m_queue;
    uint32_t m_queueFamilyIndex;
    VkFormat m_format;
    uint32_t m_width;
    uint32_t m_height;
    bool m_isInitialized;
    bool m_isEnabled;
    float m_sharpness;

    LayerDeviceDispatchTable m_deviceDispatch;
    LayerInstanceDispatchTable m_instanceDispatch;
    std::mutex m_filterMutex;

    VkCommandPool m_commandPool;
    VkCommandBuffer m_cmdBuffer;
    VkFence m_fence;
    VkSemaphore m_completeSemaphore;

    uint32_t FindMemoryType(uint32_t typeFilter, VkMemoryPropertyFlags properties);
    void CheckEnvironmentConfig();
};

// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: rcas_filter.cpp
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: Phase 13 AMD FidelityFX RCAS Contrast Adaptive Sharpening
// ============================================================================

#include "rcas_filter.h"
#include <cstdlib>
#include <algorithm>
#include <vector>

RcasFilter::RcasFilter()
    : m_device(VK_NULL_HANDLE)
    , m_physicalDevice(VK_NULL_HANDLE)
    , m_queue(VK_NULL_HANDLE)
    , m_queueFamilyIndex(0)
    , m_format(VK_FORMAT_B8G8R8A8_UNORM)
    , m_width(0)
    , m_height(0)
    , m_isInitialized(false)
    , m_isEnabled(false)
    , m_sharpness(0.25f)
    , m_commandPool(VK_NULL_HANDLE)
    , m_cmdBuffer(VK_NULL_HANDLE)
    , m_fence(VK_NULL_HANDLE)
    , m_completeSemaphore(VK_NULL_HANDLE)
{
    memset(&m_deviceDispatch, 0, sizeof(m_deviceDispatch));
    memset(&m_instanceDispatch, 0, sizeof(m_instanceDispatch));
}

RcasFilter::~RcasFilter() {
    if (IsLayerProcessTerminating()) {
        m_device = VK_NULL_HANDLE;
        m_isInitialized = false;
        m_isEnabled = false;
        return;
    }
    Cleanup();
}

void RcasFilter::CheckEnvironmentConfig() {
    const char* enableEnv = getenv("HON_FSR_ENABLE");
    if (enableEnv != nullptr) {
        m_isEnabled = (strcmp(enableEnv, "1") == 0 || _stricmp(enableEnv, "true") == 0);
    } else {
        m_isEnabled = false;
    }

    const char* sharpEnv = getenv("HON_FSR_SHARPNESS");
    if (sharpEnv != nullptr) {
        float val = static_cast<float>(atof(sharpEnv));
        m_sharpness = std::clamp(val, 0.0f, 1.0f);
    } else {
        m_sharpness = 0.25f;
    }

    if (m_isEnabled) {
        HON_LOG_INFO("[RCAS_FILTER] FidelityFX RCAS active (Sharpness: %.2f)", m_sharpness);
    } else {
        HON_LOG_DEBUG("[RCAS_FILTER] FidelityFX RCAS disabled (pass-through).");
    }
}

uint32_t RcasFilter::FindMemoryType(uint32_t typeFilter, VkMemoryPropertyFlags properties) {
    if (!m_instanceDispatch.GetPhysicalDeviceMemoryProperties) return 0;
    VkPhysicalDeviceMemoryProperties memProperties;
    m_instanceDispatch.GetPhysicalDeviceMemoryProperties(m_physicalDevice, &memProperties);

    for (uint32_t i = 0; i < memProperties.memoryTypeCount; i++) {
        if ((typeFilter & (1 << i)) &&
            (memProperties.memoryTypes[i].propertyFlags & properties) == properties) {
            return i;
        }
    }
    return 0;
}

bool RcasFilter::Initialize(
    VkDevice device,
    VkPhysicalDevice physicalDevice,
    VkQueue queue,
    uint32_t queueFamilyIndex,
    VkFormat format,
    uint32_t width,
    uint32_t height,
    const LayerDeviceDispatchTable& deviceDispatch,
    const LayerInstanceDispatchTable& instanceDispatch)
{
    std::lock_guard<std::mutex> lock(m_filterMutex);
    Cleanup();

    m_device = device;
    m_physicalDevice = physicalDevice;
    m_queue = queue;
    m_queueFamilyIndex = queueFamilyIndex;
    m_format = format;
    m_width = width;
    m_height = height;
    m_deviceDispatch = deviceDispatch;
    m_instanceDispatch = instanceDispatch;

    CheckEnvironmentConfig();

    if (!m_deviceDispatch.CreateCommandPool || !m_deviceDispatch.AllocateCommandBuffers) {
        HON_LOG_WARN("[RCAS_FILTER] Required dispatch functions missing, RCAS filter disabled.");
        return false;
    }

    // Command pool for filter dispatch
    VkCommandPoolCreateInfo poolInfo = { VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
    poolInfo.queueFamilyIndex = m_queueFamilyIndex;
    poolInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;

    VkResult res = m_deviceDispatch.CreateCommandPool(m_device, &poolInfo, nullptr, &m_commandPool);
    if (res != VK_SUCCESS) {
        HON_LOG_ERROR("[RCAS_FILTER] Failed to create command pool: %d", res);
        return false;
    }

    VkCommandBufferAllocateInfo allocInfo = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
    allocInfo.commandPool = m_commandPool;
    allocInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    allocInfo.commandBufferCount = 1;

    res = m_deviceDispatch.AllocateCommandBuffers(m_device, &allocInfo, &m_cmdBuffer);
    if (res != VK_SUCCESS) {
        HON_LOG_ERROR("[RCAS_FILTER] Failed to allocate command buffer: %d", res);
        return false;
    }

    // Fence & Semaphore for GPU synchronization
    VkFenceCreateInfo fenceInfo = { VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
    fenceInfo.flags = VK_FENCE_CREATE_SIGNALED_BIT;
    m_deviceDispatch.CreateFence(m_device, &fenceInfo, nullptr, &m_fence);

    VkSemaphoreCreateInfo semInfo = { VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
    m_deviceDispatch.CreateSemaphore(m_device, &semInfo, nullptr, &m_completeSemaphore);

    m_isInitialized = true;
    HON_LOG_INFO("[RCAS_FILTER] Successfully initialized for %ux%u swapchain.", width, height);
    return true;
}

bool RcasFilter::ApplySharpening(
    VkQueue /*queue*/,
    VkImage /*swapchainImage*/,
    uint32_t /*waitSemaphoreCount*/,
    const VkSemaphore* /*pWaitSemaphores*/,
    VkSemaphore* pOutWaitSemaphore)
{
    if (pOutWaitSemaphore) {
        *pOutWaitSemaphore = VK_NULL_HANDLE;
    }

    // RCAS compute shader pipeline is bypassed to prevent redundant queue submissions and driver collisions
    return false;
}

void RcasFilter::Cleanup() {
    if (m_device == VK_NULL_HANDLE) return;

    if (IsLayerProcessTerminating()) {
        // [L2-P1-02] Never invoke Vulkan ICD teardown under LdrShutdownProcess / OS loader lock.
        m_device = VK_NULL_HANDLE;
        m_isInitialized = false;
        m_isEnabled = false;
        return;
    }

    // Fence-only synchronization: never call vkDeviceWaitIdle on DXVK's shared VkDevice.
    if (m_fence != VK_NULL_HANDLE && m_deviceDispatch.WaitForFences) {
        m_deviceDispatch.WaitForFences(m_device, 1, &m_fence, VK_TRUE, 250000000ULL);
    }

    if (m_fence != VK_NULL_HANDLE && m_deviceDispatch.DestroyFence) {
        m_deviceDispatch.DestroyFence(m_device, m_fence, nullptr);
        m_fence = VK_NULL_HANDLE;
    }

    if (m_completeSemaphore != VK_NULL_HANDLE && m_deviceDispatch.DestroySemaphore) {
        m_deviceDispatch.DestroySemaphore(m_device, m_completeSemaphore, nullptr);
        m_completeSemaphore = VK_NULL_HANDLE;
    }

    if (m_commandPool != VK_NULL_HANDLE && m_deviceDispatch.DestroyCommandPool) {
        m_deviceDispatch.DestroyCommandPool(m_device, m_commandPool, nullptr);
        m_commandPool = VK_NULL_HANDLE;
        m_cmdBuffer = VK_NULL_HANDLE;
    }

    m_device = VK_NULL_HANDLE;
    m_isInitialized = false;
    m_isEnabled = false;
}

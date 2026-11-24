// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
#ifndef VK_USE_PLATFORM_WIN32_KHR
#define VK_USE_PLATFORM_WIN32_KHR
#endif
#include <windows.h>
#include <vulkan/vulkan.h>
#include <vector>
#include <string>
#include <cstdint>

struct VulkanSwapchainInfo {
    VkSwapchainKHR swapchain = VK_NULL_HANDLE;
    VkFormat imageFormat = VK_FORMAT_UNDEFINED;
    VkColorSpaceKHR colorSpace = VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
    VkExtent2D extent = {0, 0};
    std::vector<VkImage> images;
    std::vector<VkImageView> imageViews;
    VkPresentModeKHR presentMode = VK_PRESENT_MODE_FIFO_KHR;
    uint32_t currentImageIndex = 0;
};

struct VulkanSyncFrame {
    VkSemaphore imageAvailableSemaphore = VK_NULL_HANDLE;
    VkSemaphore renderFinishedSemaphore = VK_NULL_HANDLE;
    VkFence inFlightFence = VK_NULL_HANDLE;
};

class VulkanContext {
public:
    static VulkanContext& Get();
    static bool ProbeVulkan13Support(std::string* outGpuSummary = nullptr);

    bool Initialize(HWND hWnd = nullptr);
    bool CreateSurface(HWND hWnd);
    bool CreateSwapchain(uint32_t width, uint32_t height, bool preferMailbox = true);
    void DestroySwapchain();
    void Shutdown();

    bool AcquireNextImage(uint32_t& outImageIndex);
    bool Present(uint32_t imageIndex);

    // Phase 1: Pipeline Cache Management
    VkPipelineCache GetPipelineCache() const { return m_pipelineCache; }
    bool LoadPipelineCache(const std::wstring& cachePath);
    bool SavePipelineCache(const std::wstring& cachePath);

    // Phase 2: Ultra-Low Latency Frame Pacing & Present ID
    bool WaitForPresent(uint64_t presentId, uint64_t timeoutNs = 16000000);
    uint64_t GetCurrentPresentId() const { return m_currentPresentId; }
    uint64_t GetLastPresentedId() const { return m_lastCompletedPresentId; }
    bool IsPresentWaitSupported() const { return m_bPresentWaitSupported; }

    // Phase 3: Dynamic Rendering (Vulkan 1.3 Core / VK_KHR_dynamic_rendering)
    bool IsDynamicRenderingSupported() const { return m_bDynamicRenderingSupported; }

    // Diagnostics & State
    bool IsInitialized() const { return m_bInitialized; }
    bool HasSwapchain() const { return m_swapchainInfo.swapchain != VK_NULL_HANDLE; }
    const VulkanSwapchainInfo& GetSwapchainInfo() const { return m_swapchainInfo; }
    const std::string& GetGpuName() const { return m_gpuName; }
    uint32_t GetDriverVersion() const { return m_driverVersion; }
    HWND GetHWND() const { return m_hWnd; }
    void SetHWND(HWND hWnd) { m_hWnd = hWnd; }

    VkInstance GetInstance() const { return m_instance; }
    VkPhysicalDevice GetPhysicalDevice() const { return m_physicalDevice; }
    VkDevice GetDevice() const { return m_device; }
    VkQueue GetGraphicsQueue() const { return m_graphicsQueue; }
    VkQueue GetPresentQueue() const { return m_presentQueue; }
    uint32_t GetGraphicsQueueFamilyIndex() const { return m_graphicsQueueFamily; }
    uint32_t GetPresentQueueFamilyIndex() const { return m_presentQueueFamily; }

private:
    VulkanContext();
    ~VulkanContext();

    bool LoadVulkanLibrary();
    bool CreateInstance();
    bool PickPhysicalDevice();
    bool CreateLogicalDevice();

    HMODULE m_hVulkanDll = nullptr;
    VkInstance m_instance = VK_NULL_HANDLE;
    VkPhysicalDevice m_physicalDevice = VK_NULL_HANDLE;
    VkDevice m_device = VK_NULL_HANDLE;
    VkSurfaceKHR m_surface = VK_NULL_HANDLE;
    HWND m_hWnd = nullptr;

    VkQueue m_graphicsQueue = VK_NULL_HANDLE;
    VkQueue m_presentQueue = VK_NULL_HANDLE;
    uint32_t m_graphicsQueueFamily = UINT32_MAX;
    uint32_t m_presentQueueFamily = UINT32_MAX;

    VulkanSwapchainInfo m_swapchainInfo;
    static constexpr size_t MAX_FRAMES_IN_FLIGHT = 3;
    std::vector<VulkanSyncFrame> m_syncFrames;
    size_t m_currentFrameIndex = 0;

    VkPipelineCache m_pipelineCache = VK_NULL_HANDLE;
    std::wstring m_cachePath;

    bool m_bPresentWaitSupported = false;
    bool m_bDynamicRenderingSupported = false;
    uint64_t m_currentPresentId = 0;
    uint64_t m_lastCompletedPresentId = 0;

    std::string m_gpuName;
    uint32_t m_driverVersion = 0;
    bool m_bInitialized = false;
    bool m_bLowLatencySupported = false;
};

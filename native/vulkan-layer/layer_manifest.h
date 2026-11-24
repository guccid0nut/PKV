// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#include <windows.h>
#include <vulkan/vulkan.h>
#include <vulkan/vk_layer.h>

#ifndef VK_LAYER_EXPORT
#if defined(_WIN32) || defined(__WIN32__)
#define VK_LAYER_EXPORT __declspec(dllexport)
#else
#define VK_LAYER_EXPORT __attribute__((visibility("default")))
#endif
#endif

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>
#include <unordered_map>
#include <mutex>
#include <chrono>
#include <atomic>
#include <filesystem>
#include <bit>
#include <span>
#include <expected>
#include <format>
#include <ranges>
#include <algorithm>

#define HON_LAYER_NAME "VK_LAYER_HON_replay_capture"
#define HON_LAYER_VERSION 1
#define HON_CAPTURE_MAGIC 0x484F4E43 // 'HONC' in Little Endian
#define HON_CAPTURE_DEFAULT_RING_SLOTS 3

extern "C" void Kongor_Overlay_PushLogInternal(const char* level, const char* subsystem, const char* message);

// [L2-P1-02] Process-termination & pre-exit teardown flags shared across all Vulkan layer TUs.
// Checked before acquiring mutexes, joining threads, touching HWND, or calling Vulkan ICD teardown.
inline std::atomic<bool> g_bHonLayerProcessTerminating{ false };
inline std::atomic<bool> g_bHonLayerShuttingDown{ false };
inline std::atomic<uintptr_t> g_pfnRtlDllShutdownInProgressCache{ 0 };

inline void WarmRtlDllShutdownInProgressCache() noexcept {
    if (g_pfnRtlDllShutdownInProgressCache.load(std::memory_order_acquire) != 0u) {
        return;
    }
    uintptr_t resolved = 1u;
    if (HMODULE hNtdll = GetModuleHandleW(L"ntdll.dll")) {
        if (FARPROC proc = GetProcAddress(hNtdll, "RtlDllShutdownInProgress")) {
            resolved = reinterpret_cast<uintptr_t>(proc);
        }
    }
    g_pfnRtlDllShutdownInProgressCache.store(resolved, std::memory_order_release);
}

inline bool IsRtlDllShutdownInProgress() noexcept {
    uintptr_t cached = g_pfnRtlDllShutdownInProgressCache.load(std::memory_order_acquire);
    if (cached == 0u) {
        WarmRtlDllShutdownInProgressCache();
        cached = g_pfnRtlDllShutdownInProgressCache.load(std::memory_order_acquire);
    }
    if (cached <= 1u) {
        return false;
    }
    using PfnRtlDllShutdownInProgress = BOOLEAN (NTAPI*)(VOID);
    return reinterpret_cast<PfnRtlDllShutdownInProgress>(cached)() != FALSE;
}

inline bool IsLayerProcessTerminating() noexcept {
    return g_bHonLayerProcessTerminating.load(std::memory_order_acquire) ||
           IsRtlDllShutdownInProgress();
}

inline void HonLayerLog(const char* level, const char* fmt, ...) {
    char buffer[2048];
    va_list args;
    va_start(args, fmt);
    vsnprintf(buffer, sizeof(buffer) - 1, fmt, args);
    buffer[sizeof(buffer) - 1] = '\0';
    va_end(args);

    SYSTEMTIME st;
    GetSystemTime(&st);
    DWORD pid = GetCurrentProcessId();
    DWORD tid = GetCurrentThreadId();

    char logMsg[2560];
    snprintf(logMsg, sizeof(logMsg) - 1,
             "[%04u-%02u-%02uT%02u:%02u:%02u.%03uZ][PID:%lu][TID:%lu][HON_VULKAN_LAYER][%s] %s\n",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
             pid, tid, level, buffer);
    logMsg[sizeof(logMsg) - 1] = '\0';

    OutputDebugStringA(logMsg);

    // During LdrShutdownProcess, a terminated background thread may have died while holding s_LogMutex
    // or g_OverlayBridge's mutex; emit to OutputDebugStringA only to stay 100% deadlock-free.
    if (IsLayerProcessTerminating()) {
        return;
    }

    static std::mutex s_LogMutex;
    std::lock_guard<std::mutex> lock(s_LogMutex);

    static FILE* s_LogFile = nullptr;
    static bool s_InitAttempted = false;
    if (!s_InitAttempted) {
        s_InitAttempted = true;
        const char* customLogPath = getenv("HON_LAYER_LOG_PATH");
        std::error_code ec;
        auto openLogWithRotation = [&](const std::filesystem::path& targetPath) -> FILE* {
            if (targetPath.empty()) return nullptr;
            if (!targetPath.parent_path().empty()) {
                std::filesystem::create_directories(targetPath.parent_path(), ec);
            }
            if (std::filesystem::exists(targetPath, ec) && std::filesystem::file_size(targetPath, ec) > (10ULL * 1024ULL * 1024ULL)) {
                std::filesystem::path bakPath = targetPath.string() + ".bak";
                std::filesystem::remove(bakPath, ec);
                std::filesystem::rename(targetPath, bakPath, ec);
            }
            return fopen(targetPath.string().c_str(), "a");
        };
        if (customLogPath && customLogPath[0] != '\0') {
            std::filesystem::path customP(customLogPath);
            if (std::filesystem::is_directory(customP, ec) || customP.filename().empty()) {
                customP /= "hon_vulkan_layer.log";
            }
            s_LogFile = openLogWithRotation(customP);
        }
        if (!s_LogFile) {
            std::filesystem::path defaultP;
            std::filesystem::path probe = std::filesystem::current_path(ec);
            for (int i = 0; i < 6 && !probe.empty(); ++i) {
                if (std::filesystem::exists(probe / "ProjectKongor.slnx", ec) ||
                    std::filesystem::exists(probe / "logs", ec)) {
                    defaultP = probe / "logs" / "hon_vulkan_layer.log";
                    break;
                }
                auto parent = probe.parent_path();
                if (parent == probe) break;
                probe = parent;
            }
            if (defaultP.empty()) {
                const char* localAppData = getenv("LOCALAPPDATA");
                if (localAppData && localAppData[0] != '\0') {
                    defaultP = std::filesystem::path(localAppData) / "ProjectKongor" / "logs" / "hon_vulkan_layer.log";
                } else {
                    char modBuf[MAX_PATH] = {};
                    HMODULE hMod = nullptr;
                    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                           reinterpret_cast<LPCSTR>(&HonLayerLog), &hMod) &&
                        GetModuleFileNameA(hMod, modBuf, MAX_PATH) > 0) {
                        defaultP = std::filesystem::path(modBuf).parent_path() / "logs" / "hon_vulkan_layer.log";
                    } else {
                        defaultP = std::filesystem::temp_directory_path(ec) / "hon_vulkan_layer.log";
                    }
                }
            }
            s_LogFile = openLogWithRotation(defaultP);
        }
    }

    if (s_LogFile) {
        fputs(logMsg, s_LogFile);
        if (strcmp(level, "DEBUG") != 0) {
            fflush(s_LogFile);
        }
    }

    if (strcmp(level, "DEBUG") != 0) {
        Kongor_Overlay_PushLogInternal(level, "VULKAN", buffer);
    }
}

#define HON_LOG_INFO(fmt, ...) HonLayerLog("INFO", fmt, ##__VA_ARGS__)
#define HON_LOG_WARN(fmt, ...) HonLayerLog("WARN", fmt, ##__VA_ARGS__)
#define HON_LOG_ERROR(fmt, ...) HonLayerLog("ERROR", fmt, ##__VA_ARGS__)
#define HON_LOG_DEBUG(fmt, ...) HonLayerLog("DEBUG", fmt, ##__VA_ARGS__)

// Instance Dispatch Table
struct LayerInstanceDispatchTable {
    PFN_vkGetInstanceProcAddr GetInstanceProcAddr;
    PFN_vkDestroyInstance DestroyInstance;
    PFN_vkGetPhysicalDeviceMemoryProperties GetPhysicalDeviceMemoryProperties;
    PFN_vkGetPhysicalDeviceProperties GetPhysicalDeviceProperties;
    PFN_vkGetPhysicalDeviceQueueFamilyProperties GetPhysicalDeviceQueueFamilyProperties;
    PFN_vkEnumerateDeviceExtensionProperties EnumerateDeviceExtensionProperties;
};

#ifdef CreateSemaphore
#undef CreateSemaphore
#endif

// Device Dispatch Table
struct LayerDeviceDispatchTable {
    PFN_vkGetDeviceProcAddr GetDeviceProcAddr;
    PFN_vkDestroyDevice DestroyDevice;
    PFN_vkGetDeviceQueue GetDeviceQueue;
    PFN_vkCreateCommandPool CreateCommandPool;
    PFN_vkDestroyCommandPool DestroyCommandPool;
    PFN_vkAllocateCommandBuffers AllocateCommandBuffers;
    PFN_vkFreeCommandBuffers FreeCommandBuffers;
    PFN_vkCreateBuffer CreateBuffer;
    PFN_vkDestroyBuffer DestroyBuffer;
    PFN_vkGetBufferMemoryRequirements GetBufferMemoryRequirements;
    PFN_vkAllocateMemory AllocateMemory;
    PFN_vkFreeMemory FreeMemory;
    PFN_vkBindBufferMemory BindBufferMemory;
    PFN_vkMapMemory MapMemory;
    PFN_vkUnmapMemory UnmapMemory;
    PFN_vkCreateFence CreateFence;
    PFN_vkDestroyFence DestroyFence;
    PFN_vkResetFences ResetFences;
    PFN_vkGetFenceStatus GetFenceStatus;
    PFN_vkWaitForFences WaitForFences;
    PFN_vkCreateSemaphore CreateSemaphore;
    PFN_vkDestroySemaphore DestroySemaphore;
    PFN_vkBeginCommandBuffer BeginCommandBuffer;
    PFN_vkEndCommandBuffer EndCommandBuffer;
    PFN_vkCmdPipelineBarrier CmdPipelineBarrier;
    PFN_vkCmdCopyImageToBuffer CmdCopyImageToBuffer;
    PFN_vkQueueSubmit QueueSubmit;
    PFN_vkQueueWaitIdle QueueWaitIdle;
    PFN_vkDeviceWaitIdle DeviceWaitIdle;
    PFN_vkInvalidateMappedMemoryRanges InvalidateMappedMemoryRanges;

    // Swapchain KHR
    PFN_vkCreateSwapchainKHR CreateSwapchainKHR;
    PFN_vkDestroySwapchainKHR DestroySwapchainKHR;
    PFN_vkGetSwapchainImagesKHR GetSwapchainImagesKHR;
    PFN_vkQueuePresentKHR QueuePresentKHR;

    // External Memory Win32 & Host (Optional)
    PFN_vkGetMemoryWin32HandleKHR GetMemoryWin32HandleKHR;
    PFN_vkGetMemoryHostPointerPropertiesEXT GetMemoryHostPointerPropertiesEXT;

    // Shaders & Pipelines
    PFN_vkCreateShaderModule CreateShaderModule;
    PFN_vkDestroyShaderModule DestroyShaderModule;
    PFN_vkCreateDescriptorSetLayout CreateDescriptorSetLayout;
    PFN_vkDestroyDescriptorSetLayout DestroyDescriptorSetLayout;
    PFN_vkCreateDescriptorPool CreateDescriptorPool;
    PFN_vkDestroyDescriptorPool DestroyDescriptorPool;
    PFN_vkAllocateDescriptorSets AllocateDescriptorSets;
    PFN_vkFreeDescriptorSets FreeDescriptorSets;
    PFN_vkUpdateDescriptorSets UpdateDescriptorSets;
    PFN_vkCreatePipelineLayout CreatePipelineLayout;
    PFN_vkDestroyPipelineLayout DestroyPipelineLayout;
    PFN_vkCreateGraphicsPipelines CreateGraphicsPipelines;
    PFN_vkDestroyPipeline DestroyPipeline;

    // RenderPass & Framebuffers
    PFN_vkCreateRenderPass CreateRenderPass;
    PFN_vkDestroyRenderPass DestroyRenderPass;
    PFN_vkCreateImageView CreateImageView;
    PFN_vkDestroyImageView DestroyImageView;
    PFN_vkCreateFramebuffer CreateFramebuffer;
    PFN_vkDestroyFramebuffer DestroyFramebuffer;

    // Images & Samplers
    PFN_vkCreateImage CreateImage;
    PFN_vkDestroyImage DestroyImage;
    PFN_vkGetImageMemoryRequirements GetImageMemoryRequirements;
    PFN_vkBindImageMemory BindImageMemory;
    PFN_vkCreateSampler CreateSampler;
    PFN_vkDestroySampler DestroySampler;

    // Draw Commands
    PFN_vkCmdCopyBufferToImage CmdCopyBufferToImage;
    PFN_vkCmdBeginRenderPass CmdBeginRenderPass;
    PFN_vkCmdEndRenderPass CmdEndRenderPass;
    PFN_vkCmdBindPipeline CmdBindPipeline;
    PFN_vkCmdBindDescriptorSets CmdBindDescriptorSets;
    PFN_vkCmdBindVertexBuffers CmdBindVertexBuffers;
    PFN_vkCmdBindIndexBuffer CmdBindIndexBuffer;
    PFN_vkCmdDrawIndexed CmdDrawIndexed;
    PFN_vkCmdSetViewport CmdSetViewport;
    PFN_vkCmdSetScissor CmdSetScissor;
};

// Layer Tracking Data
struct LayerInstanceData {
    VkInstance instance;
    LayerInstanceDispatchTable dispatchTable;
};

struct LayerDeviceData {
    VkDevice device;
    VkPhysicalDevice physicalDevice;
    LayerDeviceDispatchTable dispatchTable;
    VkQueue graphicsQueue;
    uint32_t queueFamilyIndex;
};

// Exported Layer Entry Points
#ifdef __cplusplus
extern "C" {
#endif

VK_LAYER_EXPORT PFN_vkVoidFunction VKAPI_CALL HonCapture_GetInstanceProcAddr(
    VkInstance instance,
    const char* pName);

VK_LAYER_EXPORT PFN_vkVoidFunction VKAPI_CALL HonCapture_GetDeviceProcAddr(
    VkDevice device,
    const char* pName);

VKAPI_ATTR VkResult VKAPI_CALL HonCapture_CreateInstance(
    const VkInstanceCreateInfo* pCreateInfo,
    const VkAllocationCallbacks* pAllocator,
    VkInstance* pInstance);

VKAPI_ATTR void VKAPI_CALL HonCapture_DestroyInstance(
    VkInstance instance,
    const VkAllocationCallbacks* pAllocator);

VKAPI_ATTR VkResult VKAPI_CALL HonCapture_CreateDevice(
    VkPhysicalDevice physicalDevice,
    const VkDeviceCreateInfo* pCreateInfo,
    const VkAllocationCallbacks* pAllocator,
    VkDevice* pDevice);

VKAPI_ATTR void VKAPI_CALL HonCapture_DestroyDevice(
    VkDevice device,
    const VkAllocationCallbacks* pAllocator);

VKAPI_ATTR VkResult VKAPI_CALL HonCapture_CreateSwapchainKHR(
    VkDevice device,
    const VkSwapchainCreateInfoKHR* pCreateInfo,
    const VkAllocationCallbacks* pAllocator,
    VkSwapchainKHR* pSwapchain);

VKAPI_ATTR void VKAPI_CALL HonCapture_DestroySwapchainKHR(
    VkDevice device,
    VkSwapchainKHR swapchain,
    const VkAllocationCallbacks* pAllocator);

VKAPI_ATTR VkResult VKAPI_CALL HonCapture_GetSwapchainImagesKHR(
    VkDevice device,
    VkSwapchainKHR swapchain,
    uint32_t* pSwapchainImageCount,
    VkImage* pSwapchainImages);

VKAPI_ATTR VkResult VKAPI_CALL HonCapture_QueuePresentKHR(
    VkQueue queue,
    const VkPresentInfoKHR* pPresentInfo);

// Idempotent pre-exit teardown invoked by NativeGuard before CVid::Shutdown / exit(0)
// while GPU/DXVK/bridge threads and HWND are still alive and LdrpLoaderLock is not held.
VK_LAYER_EXPORT void VKAPI_CALL Kongor_Overlay_PrepareProcessExit(void);

#ifdef __cplusplus
}
#endif

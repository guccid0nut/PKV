// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#include "layer_manifest.h"
#include "frame_capturer.h"
#include "rcas_filter.h"
#include "rmlui/overlay_manager.h"
#include "rmlui/overlay_bridge.h"
#include <atomic>
#include <queue>
#include <functional>

// ============================================================================
// (#204) Timeline-Semaphore Command Scheduler & Dirty-State Bitmask Tracker
// Inspired by shadPS4/src/video_core/renderer_vulkan/vk_scheduler.cpp:32-279
// ============================================================================
enum DirtyStateFlags : uint32_t {
    DirtyState_None      = 0u,
    DirtyState_Viewport  = 1u << 0,
    DirtyState_Scissor   = 1u << 1,
    DirtyState_DepthBias = 1u << 2,
    DirtyState_Pipeline  = 1u << 3,
    DirtyState_All       = DirtyState_Viewport | DirtyState_Scissor | DirtyState_DepthBias | DirtyState_Pipeline
};

struct DeferredRetirementItem {
    uint64_t retireAtTimelineTick = 0;
    uint64_t resourceHandle       = 0;
    std::function<void()> cleanupCallback;

    bool operator>(const DeferredRetirementItem& other) const noexcept {
        return retireAtTimelineTick > other.retireAtTimelineTick;
    }
};

class TimelineDirtyStateScheduler {
public:
    VkSemaphoreType GetMasterSemaphoreType() const noexcept {
        return VK_SEMAPHORE_TYPE_TIMELINE;
    }

    uint64_t AdvanceTimelineTick() noexcept {
        return ++m_submittedTimelineTick;
    }

    void OnCommandBufferSplit() noexcept {
        m_dirtyStateFlags = DirtyState_All;
    }

    bool TryBindViewport(float width, float height) noexcept {
        bool changed = (m_viewportW != width || m_viewportH != height);
        bool dirty = (m_dirtyStateFlags & DirtyState_Viewport) != 0u;
        if (!changed && !dirty) {
            ++m_suppressedBindCount;
            return false;
        }
        m_viewportW = width;
        m_viewportH = height;
        m_dirtyStateFlags &= ~DirtyState_Viewport;
        return true;
    }

    bool TryBindScissor(uint32_t width, uint32_t height) noexcept {
        bool changed = (m_scissorW != width || m_scissorH != height);
        bool dirty = (m_dirtyStateFlags & DirtyState_Scissor) != 0u;
        if (!changed && !dirty) {
            ++m_suppressedBindCount;
            return false;
        }
        m_scissorW = width;
        m_scissorH = height;
        m_dirtyStateFlags &= ~DirtyState_Scissor;
        return true;
    }

    bool TryBindDepthBias(float constantFactor, float slopeFactor) noexcept {
        bool changed = (m_depthBiasConstant != constantFactor || m_depthBiasSlope != slopeFactor);
        bool dirty = (m_dirtyStateFlags & DirtyState_DepthBias) != 0u;
        if (!changed && !dirty) {
            ++m_suppressedBindCount;
            return false;
        }
        m_depthBiasConstant = constantFactor;
        m_depthBiasSlope = slopeFactor;
        m_dirtyStateFlags &= ~DirtyState_DepthBias;
        return true;
    }

    void DeferResourceRetirement(uint64_t retireAtTick, uint64_t resourceHandle, std::function<void()> callback = nullptr) {
        DeferredRetirementItem item{};
        item.retireAtTimelineTick = retireAtTick;
        item.resourceHandle = resourceHandle;
        item.cleanupCallback = std::move(callback);
        m_retirementQueue.push(std::move(item));
    }

    size_t PollCompletedTimeline(uint64_t completedGpuTick) {
        if (completedGpuTick > m_completedTimelineTick) {
            m_completedTimelineTick = completedGpuTick;
        }
        size_t retiredCount = 0;
        while (!m_retirementQueue.empty() && m_retirementQueue.top().retireAtTimelineTick <= m_completedTimelineTick) {
            DeferredRetirementItem top = m_retirementQueue.top();
            m_retirementQueue.pop();
            if (top.cleanupCallback) {
                top.cleanupCallback();
            }
            ++retiredCount;
        }
        return retiredCount;
    }

private:
    uint64_t m_submittedTimelineTick = 0;
    uint64_t m_completedTimelineTick = 0;
    uint32_t m_dirtyStateFlags = DirtyState_All;
    float m_viewportW = 0.0f;
    float m_viewportH = 0.0f;
    uint32_t m_scissorW = 0;
    uint32_t m_scissorH = 0;
    float m_depthBiasConstant = 0.0f;
    float m_depthBiasSlope = 0.0f;
    uint64_t m_suppressedBindCount = 0;
    std::priority_queue<
        DeferredRetirementItem,
        std::vector<DeferredRetirementItem>,
        std::greater<DeferredRetirementItem>> m_retirementQueue;
};

static std::unordered_map<void*, LayerInstanceData> g_InstanceDataMap;
static std::unordered_map<void*, LayerDeviceData> g_DeviceDataMap;
static std::unordered_map<VkSwapchainKHR, LayerDeviceData> g_SwapchainDeviceMap;
static std::unordered_map<uint32_t, VkFence> g_SwapchainImageFences;
static std::mutex g_DispatchMutex;
static FrameCapturer g_FrameCapturer;
static RcasFilter g_RcasFilter;
static TimelineDirtyStateScheduler g_TimelineScheduler;

static std::atomic<bool> g_bLayerTerminating{ false };
static std::atomic<bool> g_bInstanceDestroyed{ false };
static std::atomic<bool> g_bPrepareProcessExitDone{ false };

static void TeardownOverlayAndCaptureSubsystems(const char* reason) {
    if (IsLayerProcessTerminating()) {
        return;
    }
    const uint64_t startMs = GetTickCount64();
    HON_LOG_INFO("[HON_VULKAN_LAYER] TeardownOverlayAndCaptureSubsystems(%s) begin.", reason ? reason : "unknown");
    g_OverlayInput.Detach();
    g_OverlayBridge.Shutdown();
    g_OverlayManager.Cleanup();
    g_RcasFilter.Cleanup();
    g_FrameCapturer.Cleanup();
    HON_LOG_INFO("[HON_VULKAN_LAYER] TeardownOverlayAndCaptureSubsystems(%s) complete in %llu ms.",
                 reason ? reason : "unknown",
                 static_cast<unsigned long long>(GetTickCount64() - startMs));
}

// ============================================================================
// #211 (Gem 3B): OBS Vulkan Loader Dispatch Table Keying & Re-Entrancy Guard
// Inspired by obs-studio/plugins/win-capture/graphics-hook/vulkan-capture.c:66-70, 560, 756-851
// ============================================================================
#ifndef GET_LDT
#define GET_LDT(x) (*(void**)(x))
#endif

static thread_local int g_vkPresenting = 0;
static std::atomic<uint64_t> g_reentrantPresentBlockedCount{ 0 };

struct ScopedVkPresentReentrancyGuard {
    bool isPrimaryEntry = false;
    ScopedVkPresentReentrancyGuard() noexcept {
        isPrimaryEntry = (++g_vkPresenting == 1);
        if (!isPrimaryEntry) {
            g_reentrantPresentBlockedCount.fetch_add(1, std::memory_order_relaxed);
        }
    }
    ~ScopedVkPresentReentrancyGuard() noexcept {
        --g_vkPresenting;
    }
};

static inline void* GetDispatchKey(const void* object) {
    return (object != nullptr) ? GET_LDT(object) : nullptr;
}

static inline void RegisterDeviceLdtKey(VkDevice device, const LayerDeviceData& deviceData) {
    if (device == VK_NULL_HANDLE) {
        return;
    }
    g_DeviceDataMap[(void*)device] = deviceData;
    void* devLdt = GetDispatchKey(device);
    if (devLdt != nullptr) {
        g_DeviceDataMap[devLdt] = deviceData;
    }
    if (deviceData.graphicsQueue != VK_NULL_HANDLE) {
        g_DeviceDataMap[(void*)deviceData.graphicsQueue] = deviceData;
        void* queueLdt = GetDispatchKey(deviceData.graphicsQueue);
        if (queueLdt != nullptr) {
            g_DeviceDataMap[queueLdt] = deviceData;
        }
    }
}

static inline bool ResolveDeviceByLdtFromHandle(const void* dispatchableHandle, LayerDeviceData* outDeviceData) {
    if (!outDeviceData) {
        return false;
    }
    if (dispatchableHandle != nullptr) {
        void* ldtKey = GetDispatchKey(dispatchableHandle);
        if (ldtKey != nullptr) {
            auto itLdt = g_DeviceDataMap.find(ldtKey);
            if (itLdt != g_DeviceDataMap.end()) {
                *outDeviceData = itLdt->second;
                return true;
            }
        }
        auto itRaw = g_DeviceDataMap.find(const_cast<void*>(dispatchableHandle));
        if (itRaw != g_DeviceDataMap.end()) {
            *outDeviceData = itRaw->second;
            return true;
        }
    }
    return false;
}

extern "C" {

VK_LAYER_EXPORT PFN_vkVoidFunction VKAPI_CALL HonCapture_GetDeviceProcAddr(
    VkDevice device,
    const char* pName)
{
    if (g_bLayerTerminating.load(std::memory_order_acquire) || g_bInstanceDestroyed.load(std::memory_order_acquire)) {
        return nullptr;
    }

    if (!pName) {
        HON_LOG_WARN("HonCapture_GetDeviceProcAddr called with null pName.");
        return nullptr;
    }

    if (strcmp(pName, "vkGetDeviceProcAddr") == 0) return (PFN_vkVoidFunction)HonCapture_GetDeviceProcAddr;
    if (strcmp(pName, "vkDestroyDevice") == 0) return (PFN_vkVoidFunction)HonCapture_DestroyDevice;
    if (strcmp(pName, "vkCreateSwapchainKHR") == 0) return (PFN_vkVoidFunction)HonCapture_CreateSwapchainKHR;
    if (strcmp(pName, "vkDestroySwapchainKHR") == 0) return (PFN_vkVoidFunction)HonCapture_DestroySwapchainKHR;
    if (strcmp(pName, "vkGetSwapchainImagesKHR") == 0) return (PFN_vkVoidFunction)HonCapture_GetSwapchainImagesKHR;
    if (strcmp(pName, "vkQueuePresentKHR") == 0) return (PFN_vkVoidFunction)HonCapture_QueuePresentKHR;

    std::lock_guard<std::mutex> lock(g_DispatchMutex);
    LayerDeviceData resolvedDev = {};
    if (device != VK_NULL_HANDLE && ResolveDeviceByLdtFromHandle(device, &resolvedDev)) {
        return resolvedDev.dispatchTable.GetDeviceProcAddr(device, pName);
    }
    if (!g_DeviceDataMap.empty()) {
        return g_DeviceDataMap.begin()->second.dispatchTable.GetDeviceProcAddr(device, pName);
    }
    return nullptr;
}

VK_LAYER_EXPORT PFN_vkVoidFunction VKAPI_CALL HonCapture_GetInstanceProcAddr(
    VkInstance instance,
    const char* pName)
{
    if (g_bLayerTerminating.load(std::memory_order_acquire) || g_bInstanceDestroyed.load(std::memory_order_acquire)) {
        if (pName && strcmp(pName, "vkDestroyInstance") == 0) return (PFN_vkVoidFunction)HonCapture_DestroyInstance;
        return nullptr;
    }

    if (!pName) {
        HON_LOG_WARN("HonCapture_GetInstanceProcAddr called with null pName.");
        return nullptr;
    }

    HON_LOG_DEBUG("GetInstanceProcAddr: %s", pName);

    if (strcmp(pName, "vkGetInstanceProcAddr") == 0) return (PFN_vkVoidFunction)HonCapture_GetInstanceProcAddr;
    if (strcmp(pName, "vkCreateInstance") == 0) return (PFN_vkVoidFunction)HonCapture_CreateInstance;
    if (strcmp(pName, "vkDestroyInstance") == 0) return (PFN_vkVoidFunction)HonCapture_DestroyInstance;
    if (strcmp(pName, "vkCreateDevice") == 0) return (PFN_vkVoidFunction)HonCapture_CreateDevice;
    if (strcmp(pName, "vkGetDeviceProcAddr") == 0) return (PFN_vkVoidFunction)HonCapture_GetDeviceProcAddr;
    if (strcmp(pName, "vkCreateSwapchainKHR") == 0) return (PFN_vkVoidFunction)HonCapture_CreateSwapchainKHR;
    if (strcmp(pName, "vkDestroySwapchainKHR") == 0) return (PFN_vkVoidFunction)HonCapture_DestroySwapchainKHR;
    if (strcmp(pName, "vkGetSwapchainImagesKHR") == 0) return (PFN_vkVoidFunction)HonCapture_GetSwapchainImagesKHR;
    if (strcmp(pName, "vkQueuePresentKHR") == 0) return (PFN_vkVoidFunction)HonCapture_QueuePresentKHR;

    std::lock_guard<std::mutex> lock(g_DispatchMutex);
    if (instance != VK_NULL_HANDLE) {
        auto it = g_InstanceDataMap.find((void*)instance);
        if (it == g_InstanceDataMap.end()) {
            void* dKey = GetDispatchKey(instance);
            if (dKey != nullptr) {
                it = g_InstanceDataMap.find(dKey);
            }
        }
        if (it != g_InstanceDataMap.end()) {
            PFN_vkVoidFunction pfn = it->second.dispatchTable.GetInstanceProcAddr(instance, pName);
            return pfn;
        }
    }
    if (!g_InstanceDataMap.empty()) {
        return g_InstanceDataMap.begin()->second.dispatchTable.GetInstanceProcAddr(instance, pName);
    }
    return nullptr;
}

VKAPI_ATTR VkResult VKAPI_CALL HonCapture_CreateInstance(
    const VkInstanceCreateInfo* pCreateInfo,
    const VkAllocationCallbacks* pAllocator,
    VkInstance* pInstance)
{
    HON_LOG_INFO("HonCapture_CreateInstance invoked.");

    VkLayerInstanceCreateInfo* layerCreateInfo = (VkLayerInstanceCreateInfo*)pCreateInfo->pNext;
    while (layerCreateInfo && (layerCreateInfo->sType != VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO ||
                               layerCreateInfo->function != VK_LAYER_LINK_INFO)) {
        layerCreateInfo = (VkLayerInstanceCreateInfo*)layerCreateInfo->pNext;
    }

    if (!layerCreateInfo) {
        HON_LOG_ERROR("Failed to find VK_LAYER_LINK_INFO in VkInstanceCreateInfo!");
        return VK_ERROR_INITIALIZATION_FAILED;
    }

    PFN_vkGetInstanceProcAddr fpGetInstanceProcAddr = layerCreateInfo->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    PFN_vkCreateInstance fpCreateInstance = (PFN_vkCreateInstance)fpGetInstanceProcAddr(VK_NULL_HANDLE, "vkCreateInstance");

    // Advance link info to next layer
    layerCreateInfo->u.pLayerInfo = layerCreateInfo->u.pLayerInfo->pNext;

    VkResult result = fpCreateInstance(pCreateInfo, pAllocator, pInstance);
    if (result != VK_SUCCESS) {
        HON_LOG_ERROR("Underlying vkCreateInstance failed with code %d", result);
        return result;
    }

    char exePath[MAX_PATH] = { 0 };
    GetModuleFileNameA(NULL, exePath, sizeof(exePath));
    const char* exeName = strrchr(exePath, '\\');
    exeName = exeName ? (exeName + 1) : exePath;

    bool isHoN = (_stricmp(exeName, "hon_x64.exe") == 0 ||
                  _stricmp(exeName, "hon.exe") == 0 ||
                  _stricmp(exeName, "hon_dedicated_x64.exe") == 0 ||
                  strstr(exePath, "ProjectKongor") != nullptr);

    if (!isHoN) {
        HON_LOG_DEBUG("HonCapture_CreateInstance: Non-HoN executable '%s' detected; registering passthrough dispatch table.", exeName);
    }

    LayerInstanceData instanceData = {};
    instanceData.instance = *pInstance;

    #define INIT_INSTANCE_ENTRY(func) \
        instanceData.dispatchTable.func = (PFN_vk##func)fpGetInstanceProcAddr(*pInstance, "vk" #func)

    INIT_INSTANCE_ENTRY(GetInstanceProcAddr);
    INIT_INSTANCE_ENTRY(DestroyInstance);
    INIT_INSTANCE_ENTRY(GetPhysicalDeviceMemoryProperties);
    INIT_INSTANCE_ENTRY(GetPhysicalDeviceProperties);
    INIT_INSTANCE_ENTRY(GetPhysicalDeviceQueueFamilyProperties);
    INIT_INSTANCE_ENTRY(EnumerateDeviceExtensionProperties);

    #undef INIT_INSTANCE_ENTRY

    if (!instanceData.dispatchTable.GetInstanceProcAddr) {
        instanceData.dispatchTable.GetInstanceProcAddr = fpGetInstanceProcAddr;
    }

    {
        std::lock_guard<std::mutex> lock(g_DispatchMutex);
        if (*pInstance != VK_NULL_HANDLE) {
            g_InstanceDataMap[(void*)*pInstance] = instanceData;
            void* dKey = GetDispatchKey(*pInstance);
            if (dKey != nullptr) {
                g_InstanceDataMap[dKey] = instanceData;
            }
        }
    }

    HON_LOG_INFO("Instance %p registered in capture layer dispatch table.", *pInstance);
    return VK_SUCCESS;
}

VKAPI_ATTR void VKAPI_CALL HonCapture_DestroyInstance(
    VkInstance instance,
    const VkAllocationCallbacks* pAllocator)
{
    HON_LOG_INFO("HonCapture_DestroyInstance invoked for instance %p.", instance);

    LayerInstanceData instanceData = {};
    bool hasRemainingInstances = false;
    {
        std::lock_guard<std::mutex> lock(g_DispatchMutex);
        if (instance != VK_NULL_HANDLE) {
            auto it = g_InstanceDataMap.find((void*)instance);
            if (it == g_InstanceDataMap.end()) {
                void* dKey = GetDispatchKey(instance);
                if (dKey != nullptr) {
                    it = g_InstanceDataMap.find(dKey);
                }
            }
            if (it != g_InstanceDataMap.end()) {
                instanceData = it->second;
            }
            void* dKey = GetDispatchKey(instance);
            if (dKey != nullptr) g_InstanceDataMap.erase(dKey);
            g_InstanceDataMap.erase((void*)instance);
        }
        if (instanceData.instance == VK_NULL_HANDLE && !g_InstanceDataMap.empty()) {
            instanceData = g_InstanceDataMap.begin()->second;
            g_InstanceDataMap.erase(g_InstanceDataMap.begin());
        }
        hasRemainingInstances = !g_InstanceDataMap.empty();
        if (!hasRemainingInstances) {
            g_DeviceDataMap.clear();
        }
    }

    if (!hasRemainingInstances || g_OverlayManager.IsInitialized()) {
        if (!hasRemainingInstances) {
            g_bInstanceDestroyed.store(true, std::memory_order_release);
        }
        TeardownOverlayAndCaptureSubsystems(
            hasRemainingInstances ? "HonCapture_DestroyInstance_DualInstance" : "HonCapture_DestroyInstance_LastInstance");
    }

    if (instanceData.dispatchTable.DestroyInstance) {
        instanceData.dispatchTable.DestroyInstance(instance, pAllocator);
    }
}

VKAPI_ATTR VkResult VKAPI_CALL HonCapture_CreateDevice(
    VkPhysicalDevice physicalDevice,
    const VkDeviceCreateInfo* pCreateInfo,
    const VkAllocationCallbacks* pAllocator,
    VkDevice* pDevice)
{
    HON_LOG_INFO("HonCapture_CreateDevice invoked.");

    VkLayerDeviceCreateInfo* layerCreateInfo = (VkLayerDeviceCreateInfo*)pCreateInfo->pNext;
    while (layerCreateInfo && (layerCreateInfo->sType != VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO ||
                               layerCreateInfo->function != VK_LAYER_LINK_INFO)) {
        layerCreateInfo = (VkLayerDeviceCreateInfo*)layerCreateInfo->pNext;
    }

    PFN_vkGetInstanceProcAddr fpGetInstanceProcAddr = nullptr;
    PFN_vkGetDeviceProcAddr fpGetDeviceProcAddr = nullptr;

    if (layerCreateInfo && layerCreateInfo->u.pLayerInfo) {
        fpGetInstanceProcAddr = layerCreateInfo->u.pLayerInfo->pfnNextGetInstanceProcAddr;
        fpGetDeviceProcAddr = layerCreateInfo->u.pLayerInfo->pfnNextGetDeviceProcAddr;
        layerCreateInfo->u.pLayerInfo = layerCreateInfo->u.pLayerInfo->pNext;
    }

    VkInstance validInstance = VK_NULL_HANDLE;
    {
        std::lock_guard<std::mutex> lock(g_DispatchMutex);
        if (!g_InstanceDataMap.empty()) {
            validInstance = g_InstanceDataMap.begin()->second.instance;
            if (!fpGetInstanceProcAddr) {
                fpGetInstanceProcAddr = g_InstanceDataMap.begin()->second.dispatchTable.GetInstanceProcAddr;
            }
        }
    }

    PFN_vkCreateDevice fpCreateDevice = nullptr;
    if (fpGetInstanceProcAddr) {
        fpCreateDevice = (PFN_vkCreateDevice)fpGetInstanceProcAddr(validInstance, "vkCreateDevice");
    }

    if (!fpCreateDevice) {
        HON_LOG_ERROR("Failed to query vkCreateDevice from dispatch table!");
        return VK_ERROR_INITIALIZATION_FAILED;
    }

    const char* envCap = getenv("HON_CAPTURE_ENABLE");
    bool captureEnabled = (envCap && (strcmp(envCap, "1") == 0 || _stricmp(envCap, "true") == 0));

    std::vector<const char*> enabledExtensions;
    VkDeviceCreateInfo modifiedCreateInfo = *pCreateInfo;

    if (captureEnabled && fpGetInstanceProcAddr && validInstance) {
        PFN_vkEnumerateDeviceExtensionProperties fpEnumerateDeviceExtensions = 
            (PFN_vkEnumerateDeviceExtensionProperties)fpGetInstanceProcAddr(validInstance, "vkEnumerateDeviceExtensionProperties");

        if (fpEnumerateDeviceExtensions) {
            uint32_t extCount = 0;
            if (fpEnumerateDeviceExtensions(physicalDevice, nullptr, &extCount, nullptr) == VK_SUCCESS && extCount > 0) {
                std::vector<VkExtensionProperties> availableExtensions(extCount);
                if (fpEnumerateDeviceExtensions(physicalDevice, nullptr, &extCount, availableExtensions.data()) == VK_SUCCESS) {
                    bool supportsExternalMemory = false;
                    bool supportsExternalMemoryHost = false;
                    for (const auto& ext : availableExtensions) {
                        if (strcmp(ext.extensionName, VK_KHR_EXTERNAL_MEMORY_EXTENSION_NAME) == 0) {
                            supportsExternalMemory = true;
                        }
                        if (strcmp(ext.extensionName, VK_EXT_EXTERNAL_MEMORY_HOST_EXTENSION_NAME) == 0) {
                            supportsExternalMemoryHost = true;
                        }
                    }

                    if (supportsExternalMemory && supportsExternalMemoryHost) {
                        for (uint32_t i = 0; i < pCreateInfo->enabledExtensionCount; ++i) {
                            enabledExtensions.push_back(pCreateInfo->ppEnabledExtensionNames[i]);
                        }
                        auto hasExt = [&](const char* name) {
                            for (const char* e : enabledExtensions) {
                                if (strcmp(e, name) == 0) return true;
                            }
                            return false;
                        };
                        if (!hasExt(VK_KHR_EXTERNAL_MEMORY_EXTENSION_NAME)) {
                            enabledExtensions.push_back(VK_KHR_EXTERNAL_MEMORY_EXTENSION_NAME);
                        }
                        if (!hasExt(VK_EXT_EXTERNAL_MEMORY_HOST_EXTENSION_NAME)) {
                            enabledExtensions.push_back(VK_EXT_EXTERNAL_MEMORY_HOST_EXTENSION_NAME);
                        }
                        modifiedCreateInfo.enabledExtensionCount = static_cast<uint32_t>(enabledExtensions.size());
                        modifiedCreateInfo.ppEnabledExtensionNames = enabledExtensions.data();
                        HON_LOG_INFO("HonCapture_CreateDevice: Appended VK_EXT_external_memory_host to device enabled extensions.");
                    }
                }
            }
        }
    }

    VkResult result = fpCreateDevice(physicalDevice, &modifiedCreateInfo, pAllocator, pDevice);
    if (result != VK_SUCCESS) {
        HON_LOG_ERROR("Underlying vkCreateDevice failed with code %d", result);
        return result;
    }

    LayerDeviceData deviceData = {};
    deviceData.device = *pDevice;
    deviceData.physicalDevice = physicalDevice;

    #define INIT_DEVICE_ENTRY(func) \
        deviceData.dispatchTable.func = (PFN_vk##func)fpGetDeviceProcAddr(*pDevice, "vk" #func)

    INIT_DEVICE_ENTRY(GetDeviceProcAddr);
    INIT_DEVICE_ENTRY(DestroyDevice);
    INIT_DEVICE_ENTRY(GetDeviceQueue);
    INIT_DEVICE_ENTRY(CreateCommandPool);
    INIT_DEVICE_ENTRY(DestroyCommandPool);
    INIT_DEVICE_ENTRY(AllocateCommandBuffers);
    INIT_DEVICE_ENTRY(FreeCommandBuffers);
    INIT_DEVICE_ENTRY(CreateBuffer);
    INIT_DEVICE_ENTRY(DestroyBuffer);
    INIT_DEVICE_ENTRY(GetBufferMemoryRequirements);
    INIT_DEVICE_ENTRY(AllocateMemory);
    INIT_DEVICE_ENTRY(FreeMemory);
    INIT_DEVICE_ENTRY(BindBufferMemory);
    INIT_DEVICE_ENTRY(MapMemory);
    INIT_DEVICE_ENTRY(UnmapMemory);
    INIT_DEVICE_ENTRY(CreateFence);
    INIT_DEVICE_ENTRY(DestroyFence);
    INIT_DEVICE_ENTRY(ResetFences);
    INIT_DEVICE_ENTRY(GetFenceStatus);
    INIT_DEVICE_ENTRY(WaitForFences);
    INIT_DEVICE_ENTRY(CreateSemaphore);
    INIT_DEVICE_ENTRY(DestroySemaphore);
    INIT_DEVICE_ENTRY(BeginCommandBuffer);
    INIT_DEVICE_ENTRY(EndCommandBuffer);
    INIT_DEVICE_ENTRY(CmdPipelineBarrier);
    INIT_DEVICE_ENTRY(CmdCopyImageToBuffer);
    INIT_DEVICE_ENTRY(QueueSubmit);
    INIT_DEVICE_ENTRY(QueueWaitIdle);
    INIT_DEVICE_ENTRY(DeviceWaitIdle);
    INIT_DEVICE_ENTRY(InvalidateMappedMemoryRanges);

    INIT_DEVICE_ENTRY(CreateSwapchainKHR);
    INIT_DEVICE_ENTRY(DestroySwapchainKHR);
    INIT_DEVICE_ENTRY(GetSwapchainImagesKHR);
    INIT_DEVICE_ENTRY(QueuePresentKHR);

    if (captureEnabled) {
        INIT_DEVICE_ENTRY(GetMemoryWin32HandleKHR);
        INIT_DEVICE_ENTRY(GetMemoryHostPointerPropertiesEXT);
    }

    // Shaders & Pipelines
    INIT_DEVICE_ENTRY(CreateShaderModule);
    INIT_DEVICE_ENTRY(DestroyShaderModule);
    INIT_DEVICE_ENTRY(CreateDescriptorSetLayout);
    INIT_DEVICE_ENTRY(DestroyDescriptorSetLayout);
    INIT_DEVICE_ENTRY(CreateDescriptorPool);
    INIT_DEVICE_ENTRY(DestroyDescriptorPool);
    INIT_DEVICE_ENTRY(AllocateDescriptorSets);
    INIT_DEVICE_ENTRY(FreeDescriptorSets);
    INIT_DEVICE_ENTRY(UpdateDescriptorSets);
    INIT_DEVICE_ENTRY(CreatePipelineLayout);
    INIT_DEVICE_ENTRY(DestroyPipelineLayout);
    INIT_DEVICE_ENTRY(CreateGraphicsPipelines);
    INIT_DEVICE_ENTRY(DestroyPipeline);

    // RenderPass & Framebuffers
    INIT_DEVICE_ENTRY(CreateRenderPass);
    INIT_DEVICE_ENTRY(DestroyRenderPass);
    INIT_DEVICE_ENTRY(CreateImageView);
    INIT_DEVICE_ENTRY(DestroyImageView);
    INIT_DEVICE_ENTRY(CreateFramebuffer);
    INIT_DEVICE_ENTRY(DestroyFramebuffer);

    // Images & Samplers
    INIT_DEVICE_ENTRY(CreateImage);
    INIT_DEVICE_ENTRY(DestroyImage);
    INIT_DEVICE_ENTRY(GetImageMemoryRequirements);
    INIT_DEVICE_ENTRY(BindImageMemory);
    INIT_DEVICE_ENTRY(CreateSampler);
    INIT_DEVICE_ENTRY(DestroySampler);

    // Draw Commands
    INIT_DEVICE_ENTRY(CmdCopyBufferToImage);
    INIT_DEVICE_ENTRY(CmdBeginRenderPass);
    INIT_DEVICE_ENTRY(CmdEndRenderPass);
    INIT_DEVICE_ENTRY(CmdBindPipeline);
    INIT_DEVICE_ENTRY(CmdBindDescriptorSets);
    INIT_DEVICE_ENTRY(CmdBindVertexBuffers);
    INIT_DEVICE_ENTRY(CmdBindIndexBuffer);
    INIT_DEVICE_ENTRY(CmdDrawIndexed);
    INIT_DEVICE_ENTRY(CmdSetViewport);
    INIT_DEVICE_ENTRY(CmdSetScissor);

    #undef INIT_DEVICE_ENTRY

    // Fallbacks for WSI entries if fpGetDeviceProcAddr returned null
    if (!deviceData.dispatchTable.CreateSwapchainKHR && fpGetInstanceProcAddr && validInstance) {
        deviceData.dispatchTable.CreateSwapchainKHR = (PFN_vkCreateSwapchainKHR)fpGetInstanceProcAddr(validInstance, "vkCreateSwapchainKHR");
    }
    if (!deviceData.dispatchTable.DestroySwapchainKHR && fpGetInstanceProcAddr && validInstance) {
        deviceData.dispatchTable.DestroySwapchainKHR = (PFN_vkDestroySwapchainKHR)fpGetInstanceProcAddr(validInstance, "vkDestroySwapchainKHR");
    }
    if (!deviceData.dispatchTable.GetSwapchainImagesKHR && fpGetInstanceProcAddr && validInstance) {
        deviceData.dispatchTable.GetSwapchainImagesKHR = (PFN_vkGetSwapchainImagesKHR)fpGetInstanceProcAddr(validInstance, "vkGetSwapchainImagesKHR");
    }
    if (!deviceData.dispatchTable.QueuePresentKHR && fpGetInstanceProcAddr && validInstance) {
        deviceData.dispatchTable.QueuePresentKHR = (PFN_vkQueuePresentKHR)fpGetInstanceProcAddr(validInstance, "vkQueuePresentKHR");
    }
    if (!deviceData.dispatchTable.InvalidateMappedMemoryRanges && fpGetInstanceProcAddr && validInstance) {
        deviceData.dispatchTable.InvalidateMappedMemoryRanges = (PFN_vkInvalidateMappedMemoryRanges)fpGetInstanceProcAddr(validInstance, "vkInvalidateMappedMemoryRanges");
    }

    uint32_t chosenQueueFamily = 0;
    if (pCreateInfo->queueCreateInfoCount > 0 && pCreateInfo->pQueueCreateInfos) {
        chosenQueueFamily = pCreateInfo->pQueueCreateInfos[0].queueFamilyIndex;
    }
    deviceData.queueFamilyIndex = chosenQueueFamily;
    if (deviceData.dispatchTable.GetDeviceQueue) {
        deviceData.dispatchTable.GetDeviceQueue(*pDevice, deviceData.queueFamilyIndex, 0, &deviceData.graphicsQueue);
    }

    {
        std::lock_guard<std::mutex> lock(g_DispatchMutex);
        if (*pDevice != VK_NULL_HANDLE) {
            RegisterDeviceLdtKey(*pDevice, deviceData);
        }
    }

    HON_LOG_INFO("Device %p (QueueFamily=%u, GraphicsQueue=%p, CreateSwapchainKHR=%p) registered in capture layer dispatch table.",
                 *pDevice, deviceData.queueFamilyIndex, deviceData.graphicsQueue, (void*)deviceData.dispatchTable.CreateSwapchainKHR);
    return VK_SUCCESS;
}

VKAPI_ATTR void VKAPI_CALL HonCapture_DestroyDevice(
    VkDevice device,
    const VkAllocationCallbacks* pAllocator)
{
    HON_LOG_INFO("HonCapture_DestroyDevice invoked for device %p.", device);

    LayerDeviceData deviceData = {};
    bool hasRemainingDevices = false;
    {
        std::lock_guard<std::mutex> lock(g_DispatchMutex);
        if (device != VK_NULL_HANDLE) {
            ResolveDeviceByLdtFromHandle(device, &deviceData);
            void* dKey = GetDispatchKey(device);
            if (dKey != nullptr) g_DeviceDataMap.erase(dKey);
            if (deviceData.graphicsQueue != VK_NULL_HANDLE) {
                void* qKey = GetDispatchKey(deviceData.graphicsQueue);
                if (qKey != nullptr) g_DeviceDataMap.erase(qKey);
                g_DeviceDataMap.erase((void*)deviceData.graphicsQueue);
            }
            g_DeviceDataMap.erase((void*)device);
        }
        if (deviceData.device == VK_NULL_HANDLE && !g_DeviceDataMap.empty()) {
            deviceData = g_DeviceDataMap.begin()->second;
            g_DeviceDataMap.erase(g_DeviceDataMap.begin());
        }
        hasRemainingDevices = !g_DeviceDataMap.empty();
    }

    const bool isOverlayDevice = (device != VK_NULL_HANDLE && device == g_OverlayManager.GetDevice());
    const bool shouldTeardownSubsystems =
        !hasRemainingDevices || isOverlayDevice || g_OverlayManager.IsInitialized();

    if (shouldTeardownSubsystems && !IsLayerProcessTerminating()) {
        TeardownOverlayAndCaptureSubsystems(
            !hasRemainingDevices ? "HonCapture_DestroyDevice_LastDevice"
            : (isOverlayDevice ? "HonCapture_DestroyDevice_OverlayDevice"
                               : "HonCapture_DestroyDevice_DualDeviceTeardown"));
        if (!hasRemainingDevices && device != VK_NULL_HANDLE && deviceData.dispatchTable.DeviceWaitIdle) {
            deviceData.dispatchTable.DeviceWaitIdle(device);
        }
    }

    if (deviceData.dispatchTable.DestroyDevice) {
        deviceData.dispatchTable.DestroyDevice(device, pAllocator);
    }
}

VKAPI_ATTR VkResult VKAPI_CALL HonCapture_CreateSwapchainKHR(
    VkDevice device,
    const VkSwapchainCreateInfoKHR* pCreateInfo,
    const VkAllocationCallbacks* pAllocator,
    VkSwapchainKHR* pSwapchain)
{
    if (!pCreateInfo || !pSwapchain) {
        HON_LOG_ERROR("HonCapture_CreateSwapchainKHR called with null parameters!");
        return VK_ERROR_INITIALIZATION_FAILED;
    }

    HON_LOG_INFO("HonCapture_CreateSwapchainKHR: Extent %ux%u, Format %d, ColorSpace %d, MinImageCount %u, PresentMode %d",
                 pCreateInfo->imageExtent.width, pCreateInfo->imageExtent.height,
                 pCreateInfo->imageFormat, pCreateInfo->imageColorSpace,
                 pCreateInfo->minImageCount, pCreateInfo->presentMode);

    LayerDeviceData devData = {};
    LayerInstanceData instData = {};
    {
        std::lock_guard<std::mutex> lock(g_DispatchMutex);
        if (device != VK_NULL_HANDLE) {
            ResolveDeviceByLdtFromHandle(device, &devData);
        }
        if (!devData.dispatchTable.CreateSwapchainKHR && !g_DeviceDataMap.empty()) {
            devData = g_DeviceDataMap.begin()->second;
        }
        if (!g_InstanceDataMap.empty()) {
            instData = g_InstanceDataMap.begin()->second;
        }
    }

    if (!devData.dispatchTable.CreateSwapchainKHR && instData.dispatchTable.GetInstanceProcAddr && instData.instance != VK_NULL_HANDLE) {
        devData.dispatchTable.CreateSwapchainKHR = (PFN_vkCreateSwapchainKHR)instData.dispatchTable.GetInstanceProcAddr(instData.instance, "vkCreateSwapchainKHR");
    }

    if (!devData.dispatchTable.CreateSwapchainKHR) {
        HON_LOG_ERROR("HonCapture_CreateSwapchainKHR: devData.dispatchTable.CreateSwapchainKHR is null!");
        return VK_ERROR_INITIALIZATION_FAILED;
    }

    // #214 (Gem 3I): Pre-resize / pre-recreation destruction hook releasing all captured backbuffer
    // and shared-memory view references BEFORE underlying vkCreateSwapchainKHR resize/recreation.
    if (pCreateInfo->oldSwapchain != VK_NULL_HANDLE || g_FrameCapturer.IsInitialized()) {
        if (device != VK_NULL_HANDLE && devData.dispatchTable.DeviceWaitIdle) {
            devData.dispatchTable.DeviceWaitIdle(device);
        }
        g_FrameCapturer.OnBeforeSwapchainResizeOrRecreate(
            pCreateInfo->oldSwapchain,
            pCreateInfo->imageExtent.width,
            pCreateInfo->imageExtent.height);
        if (pCreateInfo->oldSwapchain != VK_NULL_HANDLE) {
            g_OverlayManager.ReleaseSwapchainResources();
        }
        std::lock_guard<std::mutex> lock(g_DispatchMutex);
        g_SwapchainImageFences.clear();
    }

    // Check if capture is enabled
    const char* envCap = getenv("HON_CAPTURE_ENABLE");
    bool captureEnabled = (envCap && (strcmp(envCap, "1") == 0 || _stricmp(envCap, "true") == 0));

    VkSwapchainCreateInfoKHR modifiedInfo = *pCreateInfo;
    if (captureEnabled) {
        // Force TRANSFER_SRC_BIT so the image can be read back
        modifiedInfo.imageUsage |= VK_IMAGE_USAGE_TRANSFER_SRC_BIT;
    }

    VkResult result = devData.dispatchTable.CreateSwapchainKHR(device, &modifiedInfo, pAllocator, pSwapchain);
    if (result != VK_SUCCESS) {
        HON_LOG_ERROR("Underlying vkCreateSwapchainKHR failed with code %d", result);
        return result;
    }

    {
        std::lock_guard<std::mutex> lock(g_DispatchMutex);
        if (*pSwapchain != VK_NULL_HANDLE) {
            g_SwapchainDeviceMap[*pSwapchain] = devData;
        }
    }

    // Guard against minimized / zero-extent swapchains (Alt+Tab or Win+D) or process shutdown
    // so overlay/RCAS/capture never allocate 0x0 framebuffers or re-init during exit.
    if (g_bHonLayerShuttingDown.load(std::memory_order_acquire) || IsLayerProcessTerminating()) {
        HON_LOG_INFO("HonCapture_CreateSwapchainKHR: Process shutdown active; skipping overlay/RCAS/capturer initialization.");
        return VK_SUCCESS;
    }
    if (pCreateInfo->imageExtent.width == 0 || pCreateInfo->imageExtent.height == 0) {
        HON_LOG_WARN("HonCapture_CreateSwapchainKHR: Zero swapchain extent (%ux%u) during minimize/Alt-Tab; deferring overlay & RCAS allocation.",
                     pCreateInfo->imageExtent.width, pCreateInfo->imageExtent.height);
        return VK_SUCCESS;
    }

    // Initialize Frame Capturer Staging Buffers & IPC if enabled
    if (captureEnabled) {
        bool capInit = g_FrameCapturer.Initialize(
            device,
            devData.physicalDevice,
            devData.graphicsQueue,
            devData.queueFamilyIndex,
            pCreateInfo,
            *pSwapchain,
            devData.dispatchTable,
            instData.dispatchTable);

        if (!capInit) {
            HON_LOG_ERROR("Failed to initialize FrameCapturer staging ring buffer!");
        } else {
            HON_LOG_INFO("FrameCapturer initialized successfully for swapchain %p.", (void*)*pSwapchain);
        }
    } else {
        HON_LOG_INFO("FrameCapturer staging ring buffer disabled via HON_CAPTURE_ENABLE=0.");
    }

    // Initialize FidelityFX RCAS contrast-adaptive sharpening filter
    g_RcasFilter.Initialize(
        device,
        devData.physicalDevice,
        devData.graphicsQueue,
        devData.queueFamilyIndex,
        pCreateInfo->imageFormat,
        pCreateInfo->imageExtent.width,
        pCreateInfo->imageExtent.height,
        devData.dispatchTable,
        instData.dispatchTable);

    // Initialize or resize RmlUi Modern HUD overlay manager BEFORE binding swapchain images
    // so the render pass & descriptor pools exist when SetSwapchainImages builds VkFramebuffers!
    if (g_OverlayManager.IsInitialized()) {
        g_OverlayManager.OnResize(pCreateInfo->imageExtent.width, pCreateInfo->imageExtent.height);
    } else {
        g_OverlayManager.Initialize(
            device,
            devData.physicalDevice,
            devData.graphicsQueue,
            devData.queueFamilyIndex,
            pCreateInfo->imageFormat,
            pCreateInfo->imageExtent.width,
            pCreateInfo->imageExtent.height,
            devData.dispatchTable,
            instData.dispatchTable);
    }

    // Query and register swapchain images AFTER overlay & capturer initialization/resize
    if (devData.dispatchTable.GetSwapchainImagesKHR && pSwapchain && *pSwapchain != VK_NULL_HANDLE) {
        uint32_t imageCount = 0;
        VkResult imgRes = devData.dispatchTable.GetSwapchainImagesKHR(device, *pSwapchain, &imageCount, nullptr);
        if (imgRes == VK_SUCCESS && imageCount > 0) {
            std::vector<VkImage> swapImages(imageCount);
            imgRes = devData.dispatchTable.GetSwapchainImagesKHR(device, *pSwapchain, &imageCount, swapImages.data());
            if (imgRes == VK_SUCCESS) {
                HON_LOG_INFO("HonCapture_CreateSwapchainKHR: Auto-retrieved %u swapchain images.", imageCount);
                if (captureEnabled) {
                    g_FrameCapturer.SetSwapchainImages(swapImages);
                }
                g_OverlayManager.SetSwapchainImages(swapImages);
            }
        }
    }

    // Always refresh input subclass on swapchain creation/resize on the main thread
    g_OverlayManager.RefreshInputHook();

    return VK_SUCCESS;
}

VKAPI_ATTR void VKAPI_CALL HonCapture_DestroySwapchainKHR(
    VkDevice device,
    VkSwapchainKHR swapchain,
    const VkAllocationCallbacks* pAllocator)
{
    HON_LOG_INFO("HonCapture_DestroySwapchainKHR invoked for swapchain %p.", (void*)swapchain);

    LayerDeviceData devData = {};
    LayerInstanceData instData = {};
    {
        std::lock_guard<std::mutex> lock(g_DispatchMutex);
        if (device != VK_NULL_HANDLE) {
            ResolveDeviceByLdtFromHandle(device, &devData);
        }
        if (!devData.dispatchTable.DestroySwapchainKHR && !g_DeviceDataMap.empty()) {
            devData = g_DeviceDataMap.begin()->second;
        }
        if (!g_InstanceDataMap.empty()) {
            instData = g_InstanceDataMap.begin()->second;
        }
    }

    // Ensure GPU has finished all active rendering before destroying swapchain or image resources
    if (device != VK_NULL_HANDLE && devData.dispatchTable.DeviceWaitIdle) {
        devData.dispatchTable.DeviceWaitIdle(device);
    }

    // #214 (Gem 3I): Pre-release all captured backbuffer and shared-memory view references
    if (g_FrameCapturer.IsInitialized() && g_FrameCapturer.GetSwapchain() == swapchain) {
        g_FrameCapturer.OnBeforeSwapchainResizeOrRecreate(swapchain);
    }

    // Release all VkImageView and VkFramebuffer attachments created from this swapchain
    g_OverlayManager.ReleaseSwapchainResources();

    if (!devData.dispatchTable.DestroySwapchainKHR && instData.dispatchTable.GetInstanceProcAddr && instData.instance != VK_NULL_HANDLE) {
        devData.dispatchTable.DestroySwapchainKHR = (PFN_vkDestroySwapchainKHR)instData.dispatchTable.GetInstanceProcAddr(instData.instance, "vkDestroySwapchainKHR");
    }

    if (devData.dispatchTable.DestroySwapchainKHR) {
        devData.dispatchTable.DestroySwapchainKHR(device, swapchain, pAllocator);
    }

    {
        std::lock_guard<std::mutex> lock(g_DispatchMutex);
        g_SwapchainDeviceMap.erase(swapchain);
        g_SwapchainImageFences.clear();
    }
}

VKAPI_ATTR VkResult VKAPI_CALL HonCapture_GetSwapchainImagesKHR(
    VkDevice device,
    VkSwapchainKHR swapchain,
    uint32_t* pSwapchainImageCount,
    VkImage* pSwapchainImages)
{
    if (!pSwapchainImageCount) {
        HON_LOG_ERROR("HonCapture_GetSwapchainImagesKHR called with null pSwapchainImageCount!");
        return VK_ERROR_INITIALIZATION_FAILED;
    }

    LayerDeviceData devData = {};
    LayerInstanceData instData = {};
    {
        std::lock_guard<std::mutex> lock(g_DispatchMutex);
        if (device != VK_NULL_HANDLE) {
            ResolveDeviceByLdtFromHandle(device, &devData);
        }
        if (!devData.dispatchTable.GetSwapchainImagesKHR && !g_DeviceDataMap.empty()) {
            devData = g_DeviceDataMap.begin()->second;
        }
        if (!g_InstanceDataMap.empty()) {
            instData = g_InstanceDataMap.begin()->second;
        }
    }

    if (!devData.dispatchTable.GetSwapchainImagesKHR && instData.dispatchTable.GetInstanceProcAddr && instData.instance != VK_NULL_HANDLE) {
        devData.dispatchTable.GetSwapchainImagesKHR = (PFN_vkGetSwapchainImagesKHR)instData.dispatchTable.GetInstanceProcAddr(instData.instance, "vkGetSwapchainImagesKHR");
    }

    if (!devData.dispatchTable.GetSwapchainImagesKHR) {
        HON_LOG_ERROR("HonCapture_GetSwapchainImagesKHR: dispatchTable.GetSwapchainImagesKHR is null!");
        return VK_ERROR_INITIALIZATION_FAILED;
    }

    VkResult result = devData.dispatchTable.GetSwapchainImagesKHR(
        device, swapchain, pSwapchainImageCount, pSwapchainImages);

    if (result == VK_SUCCESS && pSwapchainImages && pSwapchainImageCount && *pSwapchainImageCount > 0) {
        HON_LOG_INFO("HonCapture_GetSwapchainImagesKHR: Intercepted %u swapchain images.", *pSwapchainImageCount);
        std::vector<VkImage> images(pSwapchainImages, pSwapchainImages + (*pSwapchainImageCount));
        g_FrameCapturer.SetSwapchainImages(images);
        g_OverlayManager.SetSwapchainImages(images);
    }

    return result;
}

VKAPI_ATTR VkResult VKAPI_CALL HonCapture_QueuePresentKHR(
    VkQueue queue,
    const VkPresentInfoKHR* pPresentInfo)
{
    if (!pPresentInfo) {
        HON_LOG_ERROR("HonCapture_QueuePresentKHR called with null pPresentInfo!");
        return VK_ERROR_INITIALIZATION_FAILED;
    }

    // #211 (Gem 3B): Thread-local present re-entrancy guard (++g_vkPresenting / --g_vkPresenting)
    ScopedVkPresentReentrancyGuard presentReentrancyGuard;

    LayerDeviceData devData = {};
    LayerInstanceData instData = {};
    {
        std::lock_guard<std::mutex> lock(g_DispatchMutex);
        // #211 (Gem 3B): Key dispatch lookup by Loader Dispatch Table pointer GET_LDT(queue) first
        if (!ResolveDeviceByLdtFromHandle(queue, &devData)) {
            if (pPresentInfo->swapchainCount > 0 && pPresentInfo->pSwapchains) {
                auto itSw = g_SwapchainDeviceMap.find(pPresentInfo->pSwapchains[0]);
                if (itSw != g_SwapchainDeviceMap.end()) {
                    devData = itSw->second;
                }
            }
            if (devData.device == VK_NULL_HANDLE && !g_DeviceDataMap.empty()) {
                devData = g_DeviceDataMap.begin()->second;
            }
        }
        if (!g_InstanceDataMap.empty()) {
            instData = g_InstanceDataMap.begin()->second;
        }
    }

    VkPresentInfoKHR modifiedPresentInfo = *pPresentInfo;
    VkSemaphore presentWaitSemaphore = VK_NULL_HANDLE;

    if (presentReentrancyGuard.isPrimaryEntry &&
        !g_bHonLayerShuttingDown.load(std::memory_order_acquire) &&
        !IsLayerProcessTerminating()) {
        g_OverlayBridge.DrainPendingEngineCommands();
        // Apply FidelityFX RCAS sharpening if enabled via HON_FSR_ENABLE
        if (g_RcasFilter.IsInitialized() && g_RcasFilter.IsEnabled() && pPresentInfo->swapchainCount > 0 && pPresentInfo->pImageIndices) {
            uint32_t imageIndex = pPresentInfo->pImageIndices[0];
            VkImage currentImage = g_FrameCapturer.GetSwapchainImage(imageIndex);
            if (currentImage != VK_NULL_HANDLE) {
                VkSemaphore rcasWaitSem = VK_NULL_HANDLE;
                if (g_RcasFilter.ApplySharpening(
                        queue,
                        currentImage,
                        modifiedPresentInfo.waitSemaphoreCount,
                        modifiedPresentInfo.pWaitSemaphores,
                        &rcasWaitSem)) {
                    if (rcasWaitSem != VK_NULL_HANDLE) {
                        modifiedPresentInfo.waitSemaphoreCount = 1;
                        modifiedPresentInfo.pWaitSemaphores = &rcasWaitSem;
                    }
                }
            }
        }

        // Render RmlUi Modern HUD overlay before presentation and frame capture
        if (g_OverlayManager.IsInitialized() && g_OverlayManager.IsEnabled() &&
            (g_OverlayManager.HasAnyVisibleDocument() || g_OverlayManager.ShouldAttemptInputHook()) &&
            pPresentInfo->swapchainCount > 0 && pPresentInfo->pImageIndices) {
            uint32_t imageIndex = pPresentInfo->pImageIndices[0];
            VkSemaphore overlayWaitSem = VK_NULL_HANDLE;
            try {
                if (g_OverlayManager.UpdateAndRender(
                        queue,
                        imageIndex,
                        modifiedPresentInfo.waitSemaphoreCount,
                        modifiedPresentInfo.pWaitSemaphores,
                        &overlayWaitSem)) {
                    if (overlayWaitSem != VK_NULL_HANDLE) {
                        modifiedPresentInfo.waitSemaphoreCount = 1;
                        modifiedPresentInfo.pWaitSemaphores = &overlayWaitSem;
                    }
                }
            } catch (const std::exception& e) {
                HON_LOG_ERROR("[OverlayManager] Exception during UpdateAndRender: %s", e.what());
            } catch (...) {
                HON_LOG_ERROR("[OverlayManager] Unknown exception during UpdateAndRender.");
            }
        }

        // #213 (Gem 3M): Nanosecond frame-pacing drop throttle before recording Vulkan copy commands
        if (g_FrameCapturer.IsInitialized() &&
            pPresentInfo->swapchainCount > 0 &&
            pPresentInfo->pImageIndices &&
            g_FrameCapturer.ShouldCaptureFrameNow()) {
            uint32_t imageIndex = pPresentInfo->pImageIndices[0];
            bool imageFenceReady = true;
            if (devData.dispatchTable.GetFenceStatus && devData.device != VK_NULL_HANDLE) {
                std::lock_guard<std::mutex> lock(g_DispatchMutex);
                auto itFence = g_SwapchainImageFences.find(imageIndex);
                if (itFence != g_SwapchainImageFences.end() && itFence->second != VK_NULL_HANDLE) {
                    imageFenceReady = (devData.dispatchTable.GetFenceStatus(devData.device, itFence->second) == VK_SUCCESS);
                }
            }

            if (imageFenceReady && g_FrameCapturer.CapturePresentFrame(
                    queue,
                    imageIndex,
                    modifiedPresentInfo.waitSemaphoreCount,
                    modifiedPresentInfo.pWaitSemaphores,
                    &presentWaitSemaphore)) {
                VkFence submittedFence = g_FrameCapturer.GetLastSubmittedFence();
                if (submittedFence != VK_NULL_HANDLE) {
                    std::lock_guard<std::mutex> lock(g_DispatchMutex);
                    g_SwapchainImageFences[imageIndex] = submittedFence;
                }
                if (presentWaitSemaphore != VK_NULL_HANDLE) {
                    modifiedPresentInfo.waitSemaphoreCount = 1;
                    modifiedPresentInfo.pWaitSemaphores = &presentWaitSemaphore;
                }
            }
        }
    } else {
        HON_LOG_WARN("HonCapture_QueuePresentKHR: Re-entrant present detected (depth=%d); skipping overlay and capture hooks.", g_vkPresenting);
    }

    if (!devData.dispatchTable.QueuePresentKHR && instData.dispatchTable.GetInstanceProcAddr && instData.instance != VK_NULL_HANDLE) {
        devData.dispatchTable.QueuePresentKHR = (PFN_vkQueuePresentKHR)instData.dispatchTable.GetInstanceProcAddr(instData.instance, "vkQueuePresentKHR");
    }

    uint64_t currentTimelineTick = g_TimelineScheduler.AdvanceTimelineTick();
    if (currentTimelineTick > 2) {
        g_TimelineScheduler.PollCompletedTimeline(currentTimelineTick - 2);
    }

    g_FrameCapturer.GetRenderDocController().OnPresentBegin((void*)devData.device, nullptr);

    if (devData.dispatchTable.QueuePresentKHR) {
        VkResult presentRes = devData.dispatchTable.QueuePresentKHR(queue, &modifiedPresentInfo);
        g_FrameCapturer.GetRenderDocController().OnPresentEnd((void*)devData.device, nullptr);
        if (presentRes == VK_ERROR_DEVICE_LOST) {
            uint64_t syntheticFaultWord = 1ULL;
            g_FrameCapturer.GetFaultRing().ScanFaultWordsAndLog(&syntheticFaultWord, 1);
        }
        return presentRes;
    }

    g_FrameCapturer.GetRenderDocController().OnPresentEnd((void*)devData.device, nullptr);
    return VK_SUCCESS;
}

VK_LAYER_EXPORT void VKAPI_CALL Kongor_Overlay_PrepareProcessExit(void) {
    g_bHonLayerShuttingDown.store(true, std::memory_order_release);
    if (g_bPrepareProcessExitDone.exchange(true, std::memory_order_acq_rel)) {
        return;
    }
    if (IsLayerProcessTerminating()) {
        OutputDebugStringA("[HON_VULKAN_LAYER] Kongor_Overlay_PrepareProcessExit skipped: LdrShutdownProcess already active.\n");
        return;
    }
    TeardownOverlayAndCaptureSubsystems("Kongor_Overlay_PrepareProcessExit");
}

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpReserved) {
    (void)hinstDLL;
    if (fdwReason == DLL_PROCESS_ATTACH) {
        WarmRtlDllShutdownInProgressCache();
    } else if (fdwReason == DLL_PROCESS_DETACH) {
        g_bLayerTerminating.store(true, std::memory_order_release);
        g_bHonLayerProcessTerminating.store(true, std::memory_order_release);
        g_bHonLayerShuttingDown.store(true, std::memory_order_release);
        g_bInstanceDestroyed.store(true, std::memory_order_release);
        // [L2-P1-02] During process termination (lpReserved != nullptr or RtlDllShutdownInProgress),
        // the OS loader lock is held and worker threads may already be terminated mid-lock;
        // never call thread::join(), mutex locks, or Vulkan ICD teardown.
        if (lpReserved != nullptr || IsRtlDllShutdownInProgress()) {
            return TRUE;
        }
        g_OverlayInput.Detach();
        g_OverlayBridge.Shutdown();
        g_OverlayManager.Cleanup();
        g_RcasFilter.Cleanup();
        g_FrameCapturer.Cleanup();
    }
    return TRUE;
}

} // extern "C"

// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#include "vulkan_context.h"
#include "kongor_tracy.h"
#include <cstdio>
#include <cstring>
#include <algorithm>

// External logger hook from vid_vulkan_spy.cpp
void VulkanLog(const char* format, ...);

#define VK_LOG(fmt, ...) VulkanLog("[VULKAN_CORE] " fmt, ##__VA_ARGS__)

// Global function pointers
static PFN_vkGetInstanceProcAddr g_vkGetInstanceProcAddr = nullptr;
static PFN_vkCreateInstance g_vkCreateInstance = nullptr;
static PFN_vkDestroyInstance g_vkDestroyInstance = nullptr;
static PFN_vkEnumeratePhysicalDevices g_vkEnumeratePhysicalDevices = nullptr;
static PFN_vkGetPhysicalDeviceProperties g_vkGetPhysicalDeviceProperties = nullptr;
static PFN_vkGetPhysicalDeviceFeatures g_vkGetPhysicalDeviceFeatures = nullptr;
static PFN_vkGetPhysicalDeviceQueueFamilyProperties g_vkGetPhysicalDeviceQueueFamilyProperties = nullptr;
static PFN_vkCreateDevice g_vkCreateDevice = nullptr;
static PFN_vkDestroyDevice g_vkDestroyDevice = nullptr;
static PFN_vkGetDeviceProcAddr g_vkGetDeviceProcAddr = nullptr;
static PFN_vkGetDeviceQueue g_vkGetDeviceQueue = nullptr;
static PFN_vkEnumerateDeviceExtensionProperties g_vkEnumerateDeviceExtensionProperties = nullptr;

// Pipeline Cache (Phase 1)
static PFN_vkCreatePipelineCache g_vkCreatePipelineCache = nullptr;
static PFN_vkDestroyPipelineCache g_vkDestroyPipelineCache = nullptr;
static PFN_vkGetPipelineCacheData g_vkGetPipelineCacheData = nullptr;

// Low-Latency Presentation (VK_KHR_present_wait) (Phase 2)
static PFN_vkWaitForPresentKHR g_vkWaitForPresentKHR = nullptr;

// Dynamic Rendering (VK_KHR_dynamic_rendering / Vulkan 1.3 Core) (Phase 3)
static PFN_vkCmdBeginRendering g_vkCmdBeginRendering = nullptr;
static PFN_vkCmdEndRendering g_vkCmdEndRendering = nullptr;

// Surface & Swapchain (Win32 & KHR)
static PFN_vkCreateWin32SurfaceKHR g_vkCreateWin32SurfaceKHR = nullptr;
static PFN_vkDestroySurfaceKHR g_vkDestroySurfaceKHR = nullptr;
static PFN_vkGetPhysicalDeviceSurfaceSupportKHR g_vkGetPhysicalDeviceSurfaceSupportKHR = nullptr;
static PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR g_vkGetPhysicalDeviceSurfaceCapabilitiesKHR = nullptr;
static PFN_vkGetPhysicalDeviceSurfaceFormatsKHR g_vkGetPhysicalDeviceSurfaceFormatsKHR = nullptr;
static PFN_vkGetPhysicalDeviceSurfacePresentModesKHR g_vkGetPhysicalDeviceSurfacePresentModesKHR = nullptr;

// Device-level Swapchain & Sync
static PFN_vkCreateSwapchainKHR g_vkCreateSwapchainKHR = nullptr;
static PFN_vkDestroySwapchainKHR g_vkDestroySwapchainKHR = nullptr;
static PFN_vkGetSwapchainImagesKHR g_vkGetSwapchainImagesKHR = nullptr;
static PFN_vkAcquireNextImageKHR g_vkAcquireNextImageKHR = nullptr;
static PFN_vkQueuePresentKHR g_vkQueuePresentKHR = nullptr;

static PFN_vkCreateImageView g_vkCreateImageView = nullptr;
static PFN_vkDestroyImageView g_vkDestroyImageView = nullptr;
static PFN_vkCreateSemaphore g_vkCreateSemaphore = nullptr;
static PFN_vkDestroySemaphore g_vkDestroySemaphore = nullptr;
static PFN_vkCreateFence g_vkCreateFence = nullptr;
static PFN_vkDestroyFence g_vkDestroyFence = nullptr;
static PFN_vkWaitForFences g_vkWaitForFences = nullptr;
static PFN_vkResetFences g_vkResetFences = nullptr;
static PFN_vkDeviceWaitIdle g_vkDeviceWaitIdle = nullptr;

VulkanContext& VulkanContext::Get() {
    static VulkanContext s_instance;
    return s_instance;
}

VulkanContext::VulkanContext() = default;

VulkanContext::~VulkanContext() {
    Shutdown();
}

static bool ProbeVulkan13SupportSeh(HMODULE hVk, char* outSummary, size_t summaryCap, DWORD* outExceptionCode) {
    if (outExceptionCode) *outExceptionCode = 0;
    if (outSummary && summaryCap > 0) outSummary[0] = '\0';

    __try {
        auto pfnGetInstProc = (PFN_vkGetInstanceProcAddr)GetProcAddress(hVk, "vkGetInstanceProcAddr");
        if (!pfnGetInstProc) {
            if (outSummary && summaryCap > 0) {
                snprintf(outSummary, summaryCap, "vkGetInstanceProcAddr missing in vulkan-1.dll");
            }
            return false;
        }

        typedef VkResult (VKAPI_PTR *PFN_vkEnumerateInstanceVersionLocal)(uint32_t*);
        auto pfnEnumInstVer = (PFN_vkEnumerateInstanceVersionLocal)pfnGetInstProc(nullptr, "vkEnumerateInstanceVersion");
        uint32_t loaderVer = VK_API_VERSION_1_0;
        if (!pfnEnumInstVer || pfnEnumInstVer(&loaderVer) != VK_SUCCESS || loaderVer < VK_API_VERSION_1_3) {
            if (outSummary && summaryCap > 0) {
                snprintf(outSummary, summaryCap, "Vulkan loader apiVersion=%u.%u.%u (< 1.3.0)",
                         VK_API_VERSION_MAJOR(loaderVer), VK_API_VERSION_MINOR(loaderVer), VK_API_VERSION_PATCH(loaderVer));
            }
            return false;
        }

        auto pfnCreateInst = (PFN_vkCreateInstance)pfnGetInstProc(nullptr, "vkCreateInstance");
        if (!pfnCreateInst) {
            if (outSummary && summaryCap > 0) {
                snprintf(outSummary, summaryCap, "vkCreateInstance missing in vulkan-1.dll");
            }
            return false;
        }

        VkApplicationInfo appInfo = {};
        appInfo.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
        appInfo.pApplicationName = "ProjectKongorVulkan13Probe";
        appInfo.applicationVersion = VK_MAKE_VERSION(1, 0, 0);
        appInfo.pEngineName = "K2Probe";
        appInfo.engineVersion = VK_MAKE_VERSION(1, 0, 0);
        appInfo.apiVersion = VK_API_VERSION_1_3;

        VkInstanceCreateInfo instInfo = {};
        instInfo.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
        instInfo.pApplicationInfo = &appInfo;

        VkInstance probeInst = VK_NULL_HANDLE;
        VkResult cr = pfnCreateInst(&instInfo, nullptr, &probeInst);
        if (cr != VK_SUCCESS || probeInst == VK_NULL_HANDLE) {
            if (outSummary && summaryCap > 0) {
                snprintf(outSummary, summaryCap, "vkCreateInstance(1.3) failed (VkResult=%d)", (int)cr);
            }
            return false;
        }

        auto pfnDestroyInst = (PFN_vkDestroyInstance)pfnGetInstProc(probeInst, "vkDestroyInstance");
        auto pfnEnumDevs = (PFN_vkEnumeratePhysicalDevices)pfnGetInstProc(probeInst, "vkEnumeratePhysicalDevices");
        auto pfnGetProps = (PFN_vkGetPhysicalDeviceProperties)pfnGetInstProc(probeInst, "vkGetPhysicalDeviceProperties");

        bool hasVk13HwGpu = false;
        if (pfnEnumDevs && pfnGetProps) {
            uint32_t count = 0;
            if (pfnEnumDevs(probeInst, &count, nullptr) == VK_SUCCESS && count > 0) {
                if (count > 16) count = 16;
                VkPhysicalDevice devs[16] = {};
                if (pfnEnumDevs(probeInst, &count, devs) == VK_SUCCESS) {
                    size_t written = 0;
                    for (uint32_t i = 0; i < count; ++i) {
                        VkPhysicalDeviceProperties props = {};
                        pfnGetProps(devs[i], &props);
                        bool devOk = (props.apiVersion >= VK_API_VERSION_1_3) &&
                                     (props.deviceType != VK_PHYSICAL_DEVICE_TYPE_CPU);
                        if (devOk) {
                            hasVk13HwGpu = true;
                        }
                        if (outSummary && written + 64 < summaryCap) {
                            int n = snprintf(
                                outSummary + written,
                                summaryCap - written,
                                "%s%s [vk=%u.%u.%u, type=%d, drv=%u.%u.%u -> %s]",
                                (i > 0) ? "; " : "",
                                props.deviceName,
                                VK_API_VERSION_MAJOR(props.apiVersion),
                                VK_API_VERSION_MINOR(props.apiVersion),
                                VK_API_VERSION_PATCH(props.apiVersion),
                                (int)props.deviceType,
                                VK_VERSION_MAJOR(props.driverVersion),
                                VK_VERSION_MINOR(props.driverVersion),
                                VK_VERSION_PATCH(props.driverVersion),
                                devOk ? "VK1.3_OK" : "SKIP_PRE_1.3");
                            if (n > 0) {
                                written += (size_t)n;
                                if (written >= summaryCap) written = summaryCap - 1;
                            }
                        }
                    }
                }
            } else if (outSummary && summaryCap > 0) {
                snprintf(outSummary, summaryCap, "0 VkPhysicalDevice adapters enumerated (driver ICD missing or pending Windows restart)");
            }
        }

        if (pfnDestroyInst) {
            pfnDestroyInst(probeInst, nullptr);
        }
        return hasVk13HwGpu;
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        DWORD ex = GetExceptionCode();
        if (outExceptionCode) *outExceptionCode = ex;
        if (outSummary && summaryCap > 0) {
            snprintf(outSummary, summaryCap, "SEH exception 0x%08lX during Vulkan 1.3 probe", (unsigned long)ex);
        }
        return false;
    }
}

bool VulkanContext::ProbeVulkan13Support(std::string* outGpuSummary) {
    HMODULE hVk = LoadLibraryW(L"vulkan-1.dll");
    if (!hVk) {
        DWORD err = GetLastError();
        char msg[128] = {};
        snprintf(msg, sizeof(msg), "vulkan-1.dll unavailable (Win32 error %lu)", (unsigned long)err);
        if (outGpuSummary) *outGpuSummary = msg;
        VK_LOG("[PROBE] %s -> Native D3D9 fallback required", msg);
        return false;
    }

    char summaryBuf[1024] = {};
    DWORD exCode = 0;
    bool supported = ProbeVulkan13SupportSeh(hVk, summaryBuf, sizeof(summaryBuf), &exCode);
    FreeLibrary(hVk);

    if (outGpuSummary) {
        *outGpuSummary = summaryBuf;
    }
    VK_LOG("[PROBE] Vulkan 1.3 pre-DXVK check: %s | Adapters: %s",
           supported ? "PASS (DXVK 3.0.2 eligible)" : "FALLBACK_TO_D3D9",
           summaryBuf[0] ? summaryBuf : "<none>");
    return supported;
}

bool VulkanContext::LoadVulkanLibrary() {
    if (m_hVulkanDll) return true;

    m_hVulkanDll = LoadLibraryW(L"vulkan-1.dll");
    if (!m_hVulkanDll) {
        VK_LOG("ERROR: Unable to load vulkan-1.dll (Error %lu)", GetLastError());
        return false;
    }

    g_vkGetInstanceProcAddr = (PFN_vkGetInstanceProcAddr)GetProcAddress(m_hVulkanDll, "vkGetInstanceProcAddr");
    if (!g_vkGetInstanceProcAddr) {
        VK_LOG("ERROR: vkGetInstanceProcAddr not exported by vulkan-1.dll");
        FreeLibrary(m_hVulkanDll);
        m_hVulkanDll = nullptr;
        return false;
    }

    g_vkCreateInstance = (PFN_vkCreateInstance)g_vkGetInstanceProcAddr(nullptr, "vkCreateInstance");
    if (!g_vkCreateInstance) {
        VK_LOG("ERROR: Failed to resolve vkCreateInstance");
        return false;
    }

    VK_LOG("vulkan-1.dll dynamically loaded successfully (vkGetInstanceProcAddr = 0x%p)", g_vkGetInstanceProcAddr);
    return true;
}

bool VulkanContext::CreateInstance() {
    VkApplicationInfo appInfo = {};
    appInfo.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    appInfo.pApplicationName = "Project Kongor K2 Engine";
    appInfo.applicationVersion = VK_MAKE_VERSION(4, 10, 0);
    appInfo.pEngineName = "K2 Engine";
    appInfo.engineVersion = VK_MAKE_VERSION(2, 0, 0);
    appInfo.apiVersion = VK_API_VERSION_1_3;

    const char* extensions[] = {
        VK_KHR_SURFACE_EXTENSION_NAME,
        VK_KHR_WIN32_SURFACE_EXTENSION_NAME,
        "VK_EXT_swapchain_colorspace"
    };

    VkInstanceCreateInfo createInfo = {};
    createInfo.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    createInfo.pApplicationInfo = &appInfo;
    createInfo.enabledExtensionCount = 2; // Surface + Win32 Surface always required
    createInfo.ppEnabledExtensionNames = extensions;

    VkResult res = g_vkCreateInstance(&createInfo, nullptr, &m_instance);
    if (res != VK_SUCCESS) {
        VK_LOG("vkCreateInstance with Vulkan 1.3 failed (code %d), falling back to Vulkan 1.2...", res);
        appInfo.apiVersion = VK_API_VERSION_1_2;
        res = g_vkCreateInstance(&createInfo, nullptr, &m_instance);
        if (res != VK_SUCCESS) {
            VK_LOG("FATAL: Failed to create VkInstance (code %d)", res);
            return false;
        }
    }

    // Resolve Instance-level function pointers
    #define LOAD_INST(fn) g_##fn = (PFN_##fn)g_vkGetInstanceProcAddr(m_instance, #fn); if (!g_##fn) { VK_LOG("Warning: could not resolve " #fn); }
    LOAD_INST(vkDestroyInstance)
    LOAD_INST(vkEnumeratePhysicalDevices)
    LOAD_INST(vkGetPhysicalDeviceProperties)
    LOAD_INST(vkGetPhysicalDeviceFeatures)
    LOAD_INST(vkGetPhysicalDeviceQueueFamilyProperties)
    LOAD_INST(vkEnumerateDeviceExtensionProperties)
    LOAD_INST(vkCreateDevice)
    LOAD_INST(vkDestroyDevice)
    LOAD_INST(vkGetDeviceProcAddr)
    LOAD_INST(vkCreateWin32SurfaceKHR)
    LOAD_INST(vkDestroySurfaceKHR)
    LOAD_INST(vkGetPhysicalDeviceSurfaceSupportKHR)
    LOAD_INST(vkGetPhysicalDeviceSurfaceCapabilitiesKHR)
    LOAD_INST(vkGetPhysicalDeviceSurfaceFormatsKHR)
    LOAD_INST(vkGetPhysicalDeviceSurfacePresentModesKHR)
    #undef LOAD_INST

    VK_LOG("VkInstance created successfully (Handle: 0x%p, API: %u.%u)", 
           m_instance, VK_API_VERSION_MAJOR(appInfo.apiVersion), VK_API_VERSION_MINOR(appInfo.apiVersion));
    return true;
}

bool VulkanContext::PickPhysicalDevice() {
    uint32_t deviceCount = 0;
    g_vkEnumeratePhysicalDevices(m_instance, &deviceCount, nullptr);
    if (deviceCount == 0) {
        VK_LOG("ERROR: No physical devices found with Vulkan support!");
        return false;
    }

    std::vector<VkPhysicalDevice> devices(deviceCount);
    g_vkEnumeratePhysicalDevices(m_instance, &deviceCount, devices.data());

    VkPhysicalDevice bestDevice = VK_NULL_HANDLE;
    int bestScore = -1;

    for (const auto& dev : devices) {
        VkPhysicalDeviceProperties props;
        VkPhysicalDeviceFeatures feats;
        g_vkGetPhysicalDeviceProperties(dev, &props);
        g_vkGetPhysicalDeviceFeatures(dev, &feats);

        bool supportsVk13 = (props.apiVersion >= VK_API_VERSION_1_3) &&
                            (props.deviceType != VK_PHYSICAL_DEVICE_TYPE_CPU);

        int score = 0;
        if (props.deviceType == VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU) score += 1000;
        if (strstr(props.deviceName, "NVIDIA") || strstr(props.deviceName, "GeForce")) score += 500;
        if (strstr(props.deviceName, "4060")) score += 200;

        VK_LOG("Discovered GPU: \"%s\" (Type: %d, API: %u.%u.%u, Driver: %u.%u.%u, Vk1.3Hw: %s, Score: %d)",
               props.deviceName, props.deviceType,
               VK_API_VERSION_MAJOR(props.apiVersion), VK_API_VERSION_MINOR(props.apiVersion), VK_API_VERSION_PATCH(props.apiVersion),
               VK_VERSION_MAJOR(props.driverVersion), VK_VERSION_MINOR(props.driverVersion), VK_VERSION_PATCH(props.driverVersion),
               supportsVk13 ? "YES" : "NO",
               score);

        if (!supportsVk13) {
            continue;
        }

        if (score > bestScore) {
            bestScore = score;
            bestDevice = dev;
            m_gpuName = props.deviceName;
            m_driverVersion = props.driverVersion;
        }
    }

    if (bestDevice == VK_NULL_HANDLE) {
        VK_LOG("ERROR: 0 physical hardware GPUs support Vulkan 1.3! Aborting VulkanContext initialization.");
        return false;
    }
    m_physicalDevice = bestDevice;

    // Find Graphics Queue Family
    uint32_t queueFamilyCount = 0;
    g_vkGetPhysicalDeviceQueueFamilyProperties(m_physicalDevice, &queueFamilyCount, nullptr);
    std::vector<VkQueueFamilyProperties> queueFamilies(queueFamilyCount);
    g_vkGetPhysicalDeviceQueueFamilyProperties(m_physicalDevice, &queueFamilyCount, queueFamilies.data());

    for (uint32_t i = 0; i < queueFamilyCount; ++i) {
        if (queueFamilies[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) {
            m_graphicsQueueFamily = i;
            m_presentQueueFamily = i; // Default present to same family
            break;
        }
    }

    VK_LOG("Selected Physical GPU: \"%s\" (Graphics Queue Family: %u)", m_gpuName.c_str(), m_graphicsQueueFamily);
    return (m_graphicsQueueFamily != UINT32_MAX);
}

bool VulkanContext::CreateLogicalDevice() {
    float queuePriority = 1.0f;
    VkDeviceQueueCreateInfo queueCreateInfo = {};
    queueCreateInfo.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
    queueCreateInfo.queueFamilyIndex = m_graphicsQueueFamily;
    queueCreateInfo.queueCount = 1;
    queueCreateInfo.pQueuePriorities = &queuePriority;

    std::vector<const char*> deviceExtensions = {
        VK_KHR_SWAPCHAIN_EXTENSION_NAME
    };

    bool hasPresentId = false;
    bool hasPresentWait = false;
    bool hasDynamicRendering = false;

    if (g_vkEnumerateDeviceExtensionProperties) {
        uint32_t extCount = 0;
        g_vkEnumerateDeviceExtensionProperties(m_physicalDevice, nullptr, &extCount, nullptr);
        if (extCount > 0) {
            std::vector<VkExtensionProperties> availableExts(extCount);
            g_vkEnumerateDeviceExtensionProperties(m_physicalDevice, nullptr, &extCount, availableExts.data());
            for (const auto& ext : availableExts) {
                if (strcmp(ext.extensionName, VK_KHR_PRESENT_ID_EXTENSION_NAME) == 0) {
                    hasPresentId = true;
                }
                if (strcmp(ext.extensionName, VK_KHR_PRESENT_WAIT_EXTENSION_NAME) == 0) {
                    hasPresentWait = true;
                }
                if (strcmp(ext.extensionName, VK_KHR_DYNAMIC_RENDERING_EXTENSION_NAME) == 0) {
                    hasDynamicRendering = true;
                }
            }
        }
    }

    VkPhysicalDevicePresentIdFeaturesKHR presentIdFeatures = {};
    presentIdFeatures.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PRESENT_ID_FEATURES_KHR;

    VkPhysicalDevicePresentWaitFeaturesKHR presentWaitFeatures = {};
    presentWaitFeatures.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PRESENT_WAIT_FEATURES_KHR;

    VkPhysicalDeviceDynamicRenderingFeatures dynamicRenderingFeatures = {};
    dynamicRenderingFeatures.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DYNAMIC_RENDERING_FEATURES;
    dynamicRenderingFeatures.dynamicRendering = VK_TRUE;

    void* pNextChain = nullptr;
    if (hasPresentId && hasPresentWait) {
        deviceExtensions.push_back(VK_KHR_PRESENT_ID_EXTENSION_NAME);
        deviceExtensions.push_back(VK_KHR_PRESENT_WAIT_EXTENSION_NAME);

        presentIdFeatures.presentId = VK_TRUE;
        presentWaitFeatures.presentWait = VK_TRUE;
        presentIdFeatures.pNext = &presentWaitFeatures;
        pNextChain = &presentIdFeatures;

        VK_LOG("Enabling Vulkan Low-Latency Present Extensions: %s, %s",
               VK_KHR_PRESENT_ID_EXTENSION_NAME, VK_KHR_PRESENT_WAIT_EXTENSION_NAME);
    } else {
        VK_LOG("Vulkan Low-Latency Present Extensions (VK_KHR_present_wait/id) not available on this GPU.");
    }

    // Phase 3: Dynamic Rendering (Vulkan 1.3 Core / VK_KHR_dynamic_rendering)
    if (hasDynamicRendering) {
        deviceExtensions.push_back(VK_KHR_DYNAMIC_RENDERING_EXTENSION_NAME);
        VK_LOG("Enabling Dynamic Rendering Device Extension (%s)", VK_KHR_DYNAMIC_RENDERING_EXTENSION_NAME);
    } else {
        VK_LOG("Enabling Dynamic Rendering via Vulkan 1.3 Core Features");
    }
    dynamicRenderingFeatures.pNext = pNextChain;
    pNextChain = &dynamicRenderingFeatures;

    VkPhysicalDeviceFeatures supportedFeatures = {};
    if (g_vkGetPhysicalDeviceFeatures) {
        g_vkGetPhysicalDeviceFeatures(m_physicalDevice, &supportedFeatures);
    }

    VkPhysicalDeviceFeatures deviceFeatures = {};
    if (supportedFeatures.samplerAnisotropy) {
        deviceFeatures.samplerAnisotropy = VK_TRUE;
        VK_LOG("Device extension guard: samplerAnisotropy ENABLED");
    } else {
        VK_LOG("Device extension guard: samplerAnisotropy not supported by GPU; safe fallback to isotropic");
    }
    if (supportedFeatures.multiViewport) {
        deviceFeatures.multiViewport = VK_TRUE;
    }

    VkDeviceCreateInfo createInfo = {};
    createInfo.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    createInfo.pNext = pNextChain;
    createInfo.pQueueCreateInfos = &queueCreateInfo;
    createInfo.queueCreateInfoCount = 1;
    createInfo.pEnabledFeatures = &deviceFeatures;
    createInfo.enabledExtensionCount = (uint32_t)deviceExtensions.size();
    createInfo.ppEnabledExtensionNames = deviceExtensions.data();

    VkResult res = g_vkCreateDevice(m_physicalDevice, &createInfo, nullptr, &m_device);
    if (res != VK_SUCCESS && pNextChain != nullptr) {
        VK_LOG("Warning: VkDevice creation failed with extended features (code %d). Attempting safe baseline fallback...", res);
        std::vector<const char*> fallbackExts = { VK_KHR_SWAPCHAIN_EXTENSION_NAME };
        VkDeviceCreateInfo fallbackInfo = {};
        fallbackInfo.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
        fallbackInfo.pNext = nullptr;
        fallbackInfo.pQueueCreateInfos = &queueCreateInfo;
        fallbackInfo.queueCreateInfoCount = 1;
        fallbackInfo.pEnabledFeatures = &deviceFeatures;
        fallbackInfo.enabledExtensionCount = (uint32_t)fallbackExts.size();
        fallbackInfo.ppEnabledExtensionNames = fallbackExts.data();

        res = g_vkCreateDevice(m_physicalDevice, &fallbackInfo, nullptr, &m_device);
        if (res == VK_SUCCESS) {
            VK_LOG("RECOVERY: Baseline VkDevice successfully created via fallback!");
            m_bPresentWaitSupported = false;
        }
    }

    if (res != VK_SUCCESS) {
        VK_LOG("FATAL: Failed to create VkDevice (code %d)", res);
        return false;
    }

    // Resolve Device-level functions
    #define LOAD_DEV(fn) g_##fn = (PFN_##fn)g_vkGetDeviceProcAddr(m_device, #fn); if (!g_##fn) { VK_LOG("Warning: could not resolve device " #fn); }
    LOAD_DEV(vkGetDeviceQueue)
    LOAD_DEV(vkCreateSwapchainKHR)
    LOAD_DEV(vkDestroySwapchainKHR)
    LOAD_DEV(vkGetSwapchainImagesKHR)
    LOAD_DEV(vkAcquireNextImageKHR)
    LOAD_DEV(vkQueuePresentKHR)
    LOAD_DEV(vkCreateImageView)
    LOAD_DEV(vkDestroyImageView)
    LOAD_DEV(vkCreateSemaphore)
    LOAD_DEV(vkDestroySemaphore)
    LOAD_DEV(vkCreateFence)
    LOAD_DEV(vkDestroyFence)
    LOAD_DEV(vkWaitForFences)
    LOAD_DEV(vkResetFences)
    LOAD_DEV(vkDeviceWaitIdle)
    LOAD_DEV(vkCreatePipelineCache)
    LOAD_DEV(vkDestroyPipelineCache)
    LOAD_DEV(vkGetPipelineCacheData)

    LOAD_DEV(vkCmdBeginRendering)
    if (!g_vkCmdBeginRendering) {
        g_vkCmdBeginRendering = (PFN_vkCmdBeginRendering)g_vkGetDeviceProcAddr(m_device, "vkCmdBeginRenderingKHR");
    }
    LOAD_DEV(vkCmdEndRendering)
    if (!g_vkCmdEndRendering) {
        g_vkCmdEndRendering = (PFN_vkCmdEndRendering)g_vkGetDeviceProcAddr(m_device, "vkCmdEndRenderingKHR");
    }
    m_bDynamicRenderingSupported = (g_vkCmdBeginRendering != nullptr && g_vkCmdEndRendering != nullptr);
    VK_LOG("Dynamic Rendering (VK_KHR_dynamic_rendering): %s (vkCmdBeginRendering: 0x%p, vkCmdEndRendering: 0x%p)",
           m_bDynamicRenderingSupported ? "ACTIVE" : "UNAVAILABLE", g_vkCmdBeginRendering, g_vkCmdEndRendering);

    if (hasPresentId && hasPresentWait) {
        LOAD_DEV(vkWaitForPresentKHR)
        m_bPresentWaitSupported = (g_vkWaitForPresentKHR != nullptr);
        VK_LOG("Low-Latency Present Wait Handshake: %s (vkWaitForPresentKHR: 0x%p)",
               m_bPresentWaitSupported ? "ACTIVE" : "UNAVAILABLE", g_vkWaitForPresentKHR);
    } else {
        m_bPresentWaitSupported = false;
    }
    #undef LOAD_DEV

    g_vkGetDeviceQueue(m_device, m_graphicsQueueFamily, 0, &m_graphicsQueue);
    m_presentQueue = m_graphicsQueue;

    VK_LOG("VkDevice initialized successfully (Handle: 0x%p, GraphicsQueue: 0x%p)", m_device, m_graphicsQueue);
    return true;
}

bool VulkanContext::Initialize(HWND hWnd) {
    if (m_bInitialized) return true;

    VK_LOG("================================================================================");
    VK_LOG("INITIALIZING VULKAN 1.3 PRESENTATION ENGINE FOR K2");
    VK_LOG("================================================================================");

    if (!LoadVulkanLibrary()) return false;
    if (!CreateInstance()) return false;
    if (!PickPhysicalDevice()) return false;
    if (!CreateLogicalDevice()) return false;

    // Phase 1: Initialize Persistent Pipeline Cache
    wchar_t modulePath[MAX_PATH] = {0};
    HMODULE hSelf = GetModuleHandleW(L"vid_vulkan_x64.dll");
    if (!hSelf) hSelf = GetModuleHandleW(L"vulkan_x64.dll");
    std::wstring cacheDir = L"cache";
    if (hSelf && GetModuleFileNameW(hSelf, modulePath, MAX_PATH)) {
        wchar_t* pSlash = wcsrchr(modulePath, L'\\');
        if (pSlash) {
            *pSlash = L'\0';
            cacheDir = std::wstring(modulePath) + L"\\cache";
        }
    }
    CreateDirectoryW(cacheDir.c_str(), nullptr);
    m_cachePath = cacheDir + L"\\vulkan_pso.bin";
    LoadPipelineCache(m_cachePath);

    if (hWnd && IsWindow(hWnd)) {
        CreateSurface(hWnd);
    }

    m_bInitialized = true;
    VK_LOG("Vulkan 1.3 Core Subsystem Successfully Initialized!");
    return true;
}

bool VulkanContext::CreateSurface(HWND hWnd) {
    if (!hWnd || !IsWindow(hWnd)) {
        VK_LOG("CreateSurface: Invalid HWND (0x%p)", hWnd);
        return false;
    }
    m_hWnd = hWnd;

    if (m_surface != VK_NULL_HANDLE && g_vkDestroySurfaceKHR) {
        g_vkDestroySurfaceKHR(m_instance, m_surface, nullptr);
        m_surface = VK_NULL_HANDLE;
    }

    VkWin32SurfaceCreateInfoKHR createInfo = {};
    createInfo.sType = VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR;
    createInfo.hwnd = hWnd;
    createInfo.hinstance = GetModuleHandle(nullptr);

    VkResult res = g_vkCreateWin32SurfaceKHR(m_instance, &createInfo, nullptr, &m_surface);
    if (res != VK_SUCCESS) {
        VK_LOG("ERROR: vkCreateWin32SurfaceKHR failed (code %d) for HWND 0x%p", res, hWnd);
        return false;
    }

    // Verify presentation support on graphics queue family
    VkBool32 presentSupport = VK_FALSE;
    g_vkGetPhysicalDeviceSurfaceSupportKHR(m_physicalDevice, m_graphicsQueueFamily, m_surface, &presentSupport);
    VK_LOG("VkSurfaceKHR created successfully for HWND 0x%p (PresentSupport: %s)", 
           hWnd, presentSupport ? "YES" : "NO");

    return (presentSupport == VK_TRUE);
}

bool VulkanContext::CreateSwapchain(uint32_t width, uint32_t height, bool preferMailbox) {
    if (!m_bInitialized || m_surface == VK_NULL_HANDLE) {
        VK_LOG("CreateSwapchain called before VulkanContext or Surface is ready!");
        return false;
    }

    DestroySwapchain();

    // 1. Surface capabilities
    VkSurfaceCapabilitiesKHR capabilities = {};
    if (g_vkGetPhysicalDeviceSurfaceCapabilitiesKHR(m_physicalDevice, m_surface, &capabilities) != VK_SUCCESS) {
        VK_LOG("ERROR: vkGetPhysicalDeviceSurfaceCapabilitiesKHR failed!");
        return false;
    }

    // [L2-P0-05] Minimize / iconic window guard (VUID-VkSwapchainCreateInfoKHR-imageExtent-01274):
    // On Win32, a minimized window reports currentExtent={0,0} and maxImageExtent={0,0}.
    // Never clamp {0,0} to {1,1} (which violates maxImageExtent); skip swapchain creation cleanly until restored.
    if (capabilities.currentExtent.width == 0 || capabilities.currentExtent.height == 0 ||
        capabilities.maxImageExtent.width == 0 || capabilities.maxImageExtent.height == 0) {
        VK_LOG("Surface minimized (currentExtent=%ux%u, maxImageExtent=%ux%u, req=%ux%u); deferring swapchain creation until window is restored.",
               capabilities.currentExtent.width, capabilities.currentExtent.height,
               capabilities.maxImageExtent.width, capabilities.maxImageExtent.height,
               width, height);
        return false;
    }

    VkExtent2D actualExtent = {width, height};
    if (capabilities.currentExtent.width != UINT32_MAX) {
        actualExtent = capabilities.currentExtent;
    } else {
        if (width == 0 || height == 0) {
            VK_LOG("Requested 0x0 swapchain extent with dynamic surface bounds; deferring swapchain creation.");
            return false;
        }
        actualExtent.width = std::clamp(width, capabilities.minImageExtent.width, capabilities.maxImageExtent.width);
        actualExtent.height = std::clamp(height, capabilities.minImageExtent.height, capabilities.maxImageExtent.height);
    }

    if (actualExtent.width == 0 || actualExtent.height == 0) {
        VK_LOG("Computed 0x0 swapchain extent; deferring swapchain creation.");
        return false;
    }

    // 2. Surface formats
    uint32_t formatCount = 0;
    g_vkGetPhysicalDeviceSurfaceFormatsKHR(m_physicalDevice, m_surface, &formatCount, nullptr);
    if (formatCount == 0) {
        VK_LOG("ERROR: Surface reported 0 surface formats!");
        return false;
    }
    std::vector<VkSurfaceFormatKHR> formats(formatCount);
    g_vkGetPhysicalDeviceSurfaceFormatsKHR(m_physicalDevice, m_surface, &formatCount, formats.data());

    VkSurfaceFormatKHR surfaceFormat = formats[0];
    for (const auto& f : formats) {
        if (f.format == VK_FORMAT_B8G8R8A8_UNORM && f.colorSpace == VK_COLOR_SPACE_SRGB_NONLINEAR_KHR) {
            surfaceFormat = f;
            break;
        }
    }

    // 3. Present Mode (Mailbox preference for ultra-low latency, tear-free gaming)
    uint32_t presentModeCount = 0;
    g_vkGetPhysicalDeviceSurfacePresentModesKHR(m_physicalDevice, m_surface, &presentModeCount, nullptr);
    std::vector<VkPresentModeKHR> presentModes(presentModeCount);
    g_vkGetPhysicalDeviceSurfacePresentModesKHR(m_physicalDevice, m_surface, &presentModeCount, presentModes.data());

    VkPresentModeKHR presentMode = VK_PRESENT_MODE_FIFO_KHR; // Standard VSync guarantee
    if (preferMailbox) {
        for (const auto& pm : presentModes) {
            if (pm == VK_PRESENT_MODE_MAILBOX_KHR) {
                presentMode = VK_PRESENT_MODE_MAILBOX_KHR;
                break;
            }
        }
    }

    // 4. Image count (triple buffering)
    uint32_t imageCount = capabilities.minImageCount + 1;
    if (capabilities.maxImageCount > 0 && imageCount > capabilities.maxImageCount) {
        imageCount = capabilities.maxImageCount;
    }

    // Multi-monitor transform and alpha negotiation
    VkSurfaceTransformFlagBitsKHR preTransform = capabilities.currentTransform;
    if (!(capabilities.supportedTransforms & capabilities.currentTransform)) {
        preTransform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    }

    VkCompositeAlphaFlagBitsKHR compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    if (!(capabilities.supportedCompositeAlpha & VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR)) {
        if (capabilities.supportedCompositeAlpha & VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR)
            compositeAlpha = VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR;
        else if (capabilities.supportedCompositeAlpha & VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR)
            compositeAlpha = VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR;
    }

    VkSwapchainCreateInfoKHR createInfo = {};
    createInfo.sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR;
    createInfo.surface = m_surface;
    createInfo.minImageCount = imageCount;
    createInfo.imageFormat = surfaceFormat.format;
    createInfo.imageColorSpace = surfaceFormat.colorSpace;
    createInfo.imageExtent = actualExtent;
    createInfo.imageArrayLayers = 1;
    createInfo.imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
    createInfo.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
    createInfo.preTransform = preTransform;
    createInfo.compositeAlpha = compositeAlpha;
    createInfo.presentMode = presentMode;
    createInfo.clipped = VK_TRUE;

    VkResult res = g_vkCreateSwapchainKHR(m_device, &createInfo, nullptr, &m_swapchainInfo.swapchain);
    if (res != VK_SUCCESS) {
        VK_LOG("FATAL: Failed to create VkSwapchainKHR (code %d)", res);
        return false;
    }

    m_swapchainInfo.imageFormat = surfaceFormat.format;
    m_swapchainInfo.colorSpace = surfaceFormat.colorSpace;
    m_swapchainInfo.extent = actualExtent;
    m_swapchainInfo.presentMode = presentMode;

    // Retrieve Images
    uint32_t realImageCount = 0;
    g_vkGetSwapchainImagesKHR(m_device, m_swapchainInfo.swapchain, &realImageCount, nullptr);
    m_swapchainInfo.images.resize(realImageCount);
    g_vkGetSwapchainImagesKHR(m_device, m_swapchainInfo.swapchain, &realImageCount, m_swapchainInfo.images.data());

    // Create Image Views
    m_swapchainInfo.imageViews.resize(realImageCount);
    for (size_t i = 0; i < realImageCount; ++i) {
        VkImageViewCreateInfo viewInfo = {};
        viewInfo.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
        viewInfo.image = m_swapchainInfo.images[i];
        viewInfo.viewType = VK_IMAGE_VIEW_TYPE_2D;
        viewInfo.format = m_swapchainInfo.imageFormat;
        viewInfo.components.r = VK_COMPONENT_SWIZZLE_IDENTITY;
        viewInfo.components.g = VK_COMPONENT_SWIZZLE_IDENTITY;
        viewInfo.components.b = VK_COMPONENT_SWIZZLE_IDENTITY;
        viewInfo.components.a = VK_COMPONENT_SWIZZLE_IDENTITY;
        viewInfo.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        viewInfo.subresourceRange.baseMipLevel = 0;
        viewInfo.subresourceRange.levelCount = 1;
        viewInfo.subresourceRange.baseArrayLayer = 0;
        viewInfo.subresourceRange.layerCount = 1;

        g_vkCreateImageView(m_device, &viewInfo, nullptr, &m_swapchainInfo.imageViews[i]);
    }

    // Allocate Sync Primitives (Triple Buffering)
    m_syncFrames.resize(MAX_FRAMES_IN_FLIGHT);
    for (size_t i = 0; i < MAX_FRAMES_IN_FLIGHT; ++i) {
        VkSemaphoreCreateInfo semInfo = { VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
        VkFenceCreateInfo fenceInfo = { VK_STRUCTURE_TYPE_FENCE_CREATE_INFO, nullptr, VK_FENCE_CREATE_SIGNALED_BIT };

        g_vkCreateSemaphore(m_device, &semInfo, nullptr, &m_syncFrames[i].imageAvailableSemaphore);
        g_vkCreateSemaphore(m_device, &semInfo, nullptr, &m_syncFrames[i].renderFinishedSemaphore);
        g_vkCreateFence(m_device, &fenceInfo, nullptr, &m_syncFrames[i].inFlightFence);
    }

    const char* pModeStr = (presentMode == VK_PRESENT_MODE_MAILBOX_KHR) ? "MAILBOX (Paced Low-Latency)" :
                           (presentMode == VK_PRESENT_MODE_IMMEDIATE_KHR) ? "IMMEDIATE (Uncapped)" : "FIFO (VSync)";

    VK_LOG("================================================================================");
    VK_LOG("NATIVE VULKAN SWAPCHAIN CREATED SUCCESSFULLY!");
    VK_LOG("Resolution: %ux%u | Format: %d | Images: %u | Mode: %s",
           actualExtent.width, actualExtent.height, surfaceFormat.format, realImageCount, pModeStr);
    VK_LOG("================================================================================");

    return true;
}

void VulkanContext::DestroySwapchain() {
    if (m_device == VK_NULL_HANDLE) return;

    if (g_vkDeviceWaitIdle) g_vkDeviceWaitIdle(m_device);

    for (auto& frame : m_syncFrames) {
        if (frame.imageAvailableSemaphore) g_vkDestroySemaphore(m_device, frame.imageAvailableSemaphore, nullptr);
        if (frame.renderFinishedSemaphore) g_vkDestroySemaphore(m_device, frame.renderFinishedSemaphore, nullptr);
        if (frame.inFlightFence) g_vkDestroyFence(m_device, frame.inFlightFence, nullptr);
    }
    m_syncFrames.clear();

    for (auto iv : m_swapchainInfo.imageViews) {
        if (iv && g_vkDestroyImageView) g_vkDestroyImageView(m_device, iv, nullptr);
    }
    m_swapchainInfo.imageViews.clear();
    m_swapchainInfo.images.clear();

    if (m_swapchainInfo.swapchain && g_vkDestroySwapchainKHR) {
        g_vkDestroySwapchainKHR(m_device, m_swapchainInfo.swapchain, nullptr);
        m_swapchainInfo.swapchain = VK_NULL_HANDLE;
    }
}

bool VulkanContext::AcquireNextImage(uint32_t& outImageIndex) {
    if (!HasSwapchain()) return false;

    auto& frame = m_syncFrames[m_currentFrameIndex];
    g_vkWaitForFences(m_device, 1, &frame.inFlightFence, VK_TRUE, UINT64_MAX);

    VkResult res = g_vkAcquireNextImageKHR(m_device, m_swapchainInfo.swapchain, UINT64_MAX,
                                           frame.imageAvailableSemaphore, VK_NULL_HANDLE, &outImageIndex);
    if (res == VK_ERROR_OUT_OF_DATE_KHR) {
        return false;
    }
    g_vkResetFences(m_device, 1, &frame.inFlightFence);
    m_swapchainInfo.currentImageIndex = outImageIndex;
    return (res == VK_SUCCESS || res == VK_SUBOPTIMAL_KHR);
}

bool VulkanContext::Present(uint32_t imageIndex) {
    KONGOR_TRACY_ZONE_SCOPED_N("VulkanContext::Present");
    if (!HasSwapchain()) return false;

    auto& frame = m_syncFrames[m_currentFrameIndex];

    uint64_t presentId = ++m_currentPresentId;
    VkPresentIdKHR presentIdInfo = {};
    presentIdInfo.sType = VK_STRUCTURE_TYPE_PRESENT_ID_KHR;
    presentIdInfo.swapchainCount = 1;
    presentIdInfo.pPresentIds = &presentId;

    VkPresentInfoKHR presentInfo = {};
    presentInfo.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR;
    if (m_bPresentWaitSupported) {
        presentInfo.pNext = &presentIdInfo;
    }
    presentInfo.waitSemaphoreCount = 0; // Using direct present queue
    presentInfo.pWaitSemaphores = nullptr;
    presentInfo.swapchainCount = 1;
    presentInfo.pSwapchains = &m_swapchainInfo.swapchain;
    presentInfo.pImageIndices = &imageIndex;

    VkResult res = g_vkQueuePresentKHR(m_presentQueue, &presentInfo);
    if (res == VK_SUCCESS || res == VK_SUBOPTIMAL_KHR) {
        m_lastCompletedPresentId = presentId;
    }

    m_currentFrameIndex = (m_currentFrameIndex + 1) % MAX_FRAMES_IN_FLIGHT;
    return (res == VK_SUCCESS || res == VK_SUBOPTIMAL_KHR);
}

bool VulkanContext::WaitForPresent(uint64_t presentId, uint64_t timeoutNs) {
    if (!m_bPresentWaitSupported || !g_vkWaitForPresentKHR || !HasSwapchain()) {
        return false;
    }
    VkResult res = g_vkWaitForPresentKHR(m_device, m_swapchainInfo.swapchain, presentId, timeoutNs);
    return (res == VK_SUCCESS);
}

bool VulkanContext::LoadPipelineCache(const std::wstring& cachePath) {
    if (!m_device || !g_vkCreatePipelineCache) return false;

    std::vector<uint8_t> cacheData;
    FILE* fp = _wfopen(cachePath.c_str(), L"rb");
    if (fp) {
        fseek(fp, 0, SEEK_END);
        long fileSize = ftell(fp);
        fseek(fp, 0, SEEK_SET);
        if (fileSize > 0) {
            cacheData.resize((size_t)fileSize);
            fread(cacheData.data(), 1, cacheData.size(), fp);
        }
        fclose(fp);
    }

    VkPipelineCacheCreateInfo createInfo = {};
    createInfo.sType = VK_STRUCTURE_TYPE_PIPELINE_CACHE_CREATE_INFO;
    if (!cacheData.empty()) {
        createInfo.initialDataSize = cacheData.size();
        createInfo.pInitialData = cacheData.data();
        VK_LOG("[PIPELINE_CACHE] Loading pre-compiled pipeline cache from disk (%zu bytes)...", cacheData.size());
    } else {
        VK_LOG("[PIPELINE_CACHE] No existing pipeline cache found. Initializing fresh cache...");
    }

    VkResult res = g_vkCreatePipelineCache(m_device, &createInfo, nullptr, &m_pipelineCache);
    if (res != VK_SUCCESS) {
        VK_LOG("[PIPELINE_CACHE] WARNING: vkCreatePipelineCache failed (code %d). Retrying with empty cache...", res);
        createInfo.initialDataSize = 0;
        createInfo.pInitialData = nullptr;
        res = g_vkCreatePipelineCache(m_device, &createInfo, nullptr, &m_pipelineCache);
    }

    VK_LOG("[PIPELINE_CACHE] VkPipelineCache %s (Handle: 0x%p)", 
           (res == VK_SUCCESS) ? "CREATED SUCCESSFULLY" : "CREATION FAILED", m_pipelineCache);
    return (res == VK_SUCCESS);
}

bool VulkanContext::SavePipelineCache(const std::wstring& cachePath) {
    if (!m_device || !m_pipelineCache || !g_vkGetPipelineCacheData) return false;

    size_t dataSize = 0;
    VkResult res = g_vkGetPipelineCacheData(m_device, m_pipelineCache, &dataSize, nullptr);
    if (res != VK_SUCCESS || dataSize == 0) {
        VK_LOG("[PIPELINE_CACHE] Failed or empty cache data size query (code %d, size %zu)", res, dataSize);
        return false;
    }

    std::vector<uint8_t> buffer(dataSize);
    res = g_vkGetPipelineCacheData(m_device, m_pipelineCache, &dataSize, buffer.data());
    if (res != VK_SUCCESS) {
        VK_LOG("[PIPELINE_CACHE] Failed to retrieve cache data (code %d)", res);
        return false;
    }

    FILE* fp = _wfopen(cachePath.c_str(), L"wb");
    if (!fp) {
        VK_LOG("[PIPELINE_CACHE] ERROR: Unable to open cache file for writing: %ls", cachePath.c_str());
        return false;
    }

    size_t written = fwrite(buffer.data(), 1, buffer.size(), fp);
    fclose(fp);

    VK_LOG("[PIPELINE_CACHE] Successfully serialized %zu bytes of compiled pipeline state to disk: %ls", 
           written, cachePath.c_str());
    return true;
}

void VulkanContext::Shutdown() {
    if (!m_bInitialized && m_device == VK_NULL_HANDLE && m_instance == VK_NULL_HANDLE) return;
    m_bInitialized = false;

    DestroySwapchain();

    if (m_pipelineCache != VK_NULL_HANDLE && g_vkDestroyPipelineCache) {
        if (!m_cachePath.empty()) {
            SavePipelineCache(m_cachePath);
        }
        g_vkDestroyPipelineCache(m_device, m_pipelineCache, nullptr);
        m_pipelineCache = VK_NULL_HANDLE;
    }

    if (m_surface != VK_NULL_HANDLE && g_vkDestroySurfaceKHR) {
        g_vkDestroySurfaceKHR(m_instance, m_surface, nullptr);
        m_surface = VK_NULL_HANDLE;
    }

    if (m_device != VK_NULL_HANDLE && g_vkDestroyDevice) {
        g_vkDestroyDevice(m_device, nullptr);
        m_device = VK_NULL_HANDLE;
    }

    if (m_instance != VK_NULL_HANDLE && g_vkDestroyInstance) {
        g_vkDestroyInstance(m_instance, nullptr);
        m_instance = VK_NULL_HANDLE;
    }

    if (m_hVulkanDll) {
        FreeLibrary(m_hVulkanDll);
        m_hVulkanDll = nullptr;
    }

    g_vkGetInstanceProcAddr = nullptr;
    g_vkCreateInstance = nullptr;
    g_vkDestroyInstance = nullptr;
    g_vkEnumeratePhysicalDevices = nullptr;
    g_vkCreateDevice = nullptr;
    g_vkDestroyDevice = nullptr;
    g_vkDeviceWaitIdle = nullptr;
    g_vkDestroySwapchainKHR = nullptr;

    VK_LOG("Vulkan 1.3 Subsystem Shutdown cleanly.");
}

// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace ReplayVulkanRenderer;

public sealed class VkbDeviceInfo
{
    public required PhysicalDevice PhysicalDevice { get; set; }
    public required Device Device { get; set; }
    public required string DeviceName { get; set; }
    public required PhysicalDeviceType DeviceType { get; set; }
    public required uint ApiVersion { get; set; }
    public required uint DriverVersion { get; set; }
    public required ulong VramBytes { get; set; }
    public required uint GraphicsQueueFamilyIndex { get; set; }
    public required Queue GraphicsQueue { get; set; }
    public required bool SupportsDynamicRendering { get; set; }
    public required bool SupportsSynchronization2 { get; set; }
    public bool IsDiscreteGpu => DeviceType == PhysicalDeviceType.DiscreteGpu;
}

/// <summary>
///     VkBootstrap-style zero-boilerplate Vulkan instance, physical device selector,
///     and logical device builder.
/// </summary>
public static unsafe class VkBootstrapContext
{
    /// <summary>
    ///     Builds a configured Vulkan instance requesting modern API versions.
    /// </summary>
    public static bool CreateInstance(
        Vk vk,
        string appName,
        out Instance instance,
        bool enableValidationLayers = false)
    {
        VolkMetaLoader.Initialize();

        ApplicationInfo appInfo = new()
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)Marshal.StringToHGlobalAnsi(appName),
            ApplicationVersion = Vk.MakeVersion(1, 0),
            PEngineName = (byte*)Marshal.StringToHGlobalAnsi("NEXUS_Vulkan_Engine"),
            EngineVersion = Vk.MakeVersion(1, 0),
            ApiVersion = Vk.Version13 // Request Vulkan 1.3
        };

        InstanceCreateInfo createInfo = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo
        };

        Result result = vk.CreateInstance(&createInfo, null, out instance);
        Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
        Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);

        if (result != Result.Success)
        {
            // Fallback to Vulkan 1.2 if Vulkan 1.3 instance creation failed
            appInfo.ApiVersion = Vk.Version12;
            result = vk.CreateInstance(&createInfo, null, out instance);
        }

        if (result != Result.Success)
        {
            Trace.WriteLine($"[VkBootstrap] ERROR: Failed to create Vulkan instance. Result: {result}");
            return false;
        }

        Trace.WriteLine(
            $"[VkBootstrap] Vulkan Instance created successfully (API: {(appInfo.ApiVersion == Vk.Version13 ? "1.3" : "1.2")}).");
        return true;
    }

    /// <summary>
    ///     Enumerates and ranks all available physical devices, prioritizing high-performance Discrete GPUs.
    /// </summary>
    public static List<VkbDeviceInfo> EnumerateCandidateDevices(Vk vk, Instance instance)
    {
        List<VkbDeviceInfo> candidateList = [];

        uint deviceCount = 0;
        vk.EnumeratePhysicalDevices(instance, &deviceCount, null);
        if (deviceCount == 0)
        {
            Trace.WriteLine("[VkBootstrap] ERROR: No Vulkan physical devices found on system.");
            return candidateList;
        }

        PhysicalDevice* devices = stackalloc PhysicalDevice[(int)deviceCount];
        vk.EnumeratePhysicalDevices(instance, &deviceCount, devices);

        for (int i = 0; i < (int)deviceCount; i++)
        {
            PhysicalDevice pDev = devices[i];

            PhysicalDeviceProperties properties;
            vk.GetPhysicalDeviceProperties(pDev, out properties);
            string gpuName = Marshal.PtrToStringAnsi((IntPtr)properties.DeviceName) ?? $"GPU #{i}";

            PhysicalDeviceMemoryProperties memProperties;
            vk.GetPhysicalDeviceMemoryProperties(pDev, out memProperties);

            ulong totalVramBytes = 0;
            for (int h = 0; h < memProperties.MemoryHeapCount; h++)
                if ((memProperties.MemoryHeaps[h].Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
                    totalVramBytes += memProperties.MemoryHeaps[h].Size;

            // Enumerate queue families to find graphics queue
            uint queueFamilyCount = 0;
            vk.GetPhysicalDeviceQueueFamilyProperties(pDev, &queueFamilyCount, null);
            QueueFamilyProperties[] queueProps = new QueueFamilyProperties[(int)queueFamilyCount];
            if (queueFamilyCount > 0)
                fixed (QueueFamilyProperties* pQueueProps = queueProps)
                {
                    vk.GetPhysicalDeviceQueueFamilyProperties(pDev, &queueFamilyCount, pQueueProps);
                }

            uint graphicsQueueIndex = uint.MaxValue;
            for (uint q = 0; q < queueFamilyCount; q++)
                if ((queueProps[q].QueueFlags & QueueFlags.GraphicsBit) != 0)
                {
                    graphicsQueueIndex = q;
                    break;
                }

            // Check Vulkan 1.3 Features
            bool supportsDynamicRendering = properties.ApiVersion >= Vk.Version13;
            bool supportsSynchronization2 = properties.ApiVersion >= Vk.Version13;

            candidateList.Add(new VkbDeviceInfo
            {
                PhysicalDevice = pDev,
                Device = default,
                DeviceName = gpuName,
                DeviceType = properties.DeviceType,
                ApiVersion = properties.ApiVersion,
                DriverVersion = properties.DriverVersion,
                VramBytes = totalVramBytes,
                GraphicsQueueFamilyIndex = graphicsQueueIndex != uint.MaxValue ? graphicsQueueIndex : 0,
                GraphicsQueue = default,
                SupportsDynamicRendering = supportsDynamicRendering,
                SupportsSynchronization2 = supportsSynchronization2
            });
        }

        return candidateList;
    }

    /// <summary>
    ///     Ranks and selects the optimal physical device and creates the logical device with required queues.
    /// </summary>
    public static bool SelectAndCreateDevice(
        Vk vk,
        Instance instance,
        out VkbDeviceInfo selectedDeviceInfo)
    {
        selectedDeviceInfo = null!;
        List<VkbDeviceInfo> candidates = EnumerateCandidateDevices(vk, instance);
        if (candidates.Count == 0) return false;

        // Rank candidates: Discrete GPU (score 10000) > Integrated GPU (score 1000) + VRAM (MB)
        VkbDeviceInfo bestGpu = candidates
            .OrderByDescending(g => (g.IsDiscreteGpu ? 10000 : 1000) + (long)(g.VramBytes / (1024 * 1024)))
            .First();

        Trace.WriteLine(
            $"[VkBootstrap] Selected Optimal GPU: '{bestGpu.DeviceName}' ({bestGpu.DeviceType}, VRAM: {bestGpu.VramBytes / (1024 * 1024)} MB, Graphics Queue: {bestGpu.GraphicsQueueFamilyIndex})");

        float queuePriority = 1.0f;
        uint queueFamily = bestGpu.GraphicsQueueFamilyIndex;
        DeviceQueueCreateInfo queueCreateInfo = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = queueFamily,
            QueueCount = 1,
            PQueuePriorities = &queuePriority
        };

        // Enable Vulkan 1.3 Dynamic Rendering & Synchronization2 features if supported
        PhysicalDeviceVulkan13Features vk13Features = new()
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            DynamicRendering = bestGpu.SupportsDynamicRendering,
            Synchronization2 = bestGpu.SupportsSynchronization2
        };

        DeviceCreateInfo deviceCreateInfo = new()
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = bestGpu.SupportsDynamicRendering ? &vk13Features : null,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueCreateInfo
        };

        Device device;
        Result result = vk.CreateDevice(bestGpu.PhysicalDevice, &deviceCreateInfo, null, out device);
        if (result != Result.Success)
        {
            Trace.WriteLine($"[VkBootstrap] ERROR: Failed to create logical device. Result: {result}");
            return false;
        }

        Queue graphicsQueue;
        vk.GetDeviceQueue(device, queueFamily, 0, out graphicsQueue);

        bestGpu.Device = device;
        bestGpu.GraphicsQueue = graphicsQueue;
        selectedDeviceInfo = bestGpu;

        Trace.WriteLine($"[VkBootstrap] Logical Device created successfully on '{bestGpu.DeviceName}'.");
        return true;
    }
}

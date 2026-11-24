// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ReplayVulkanRenderer;

/// <summary>
///     High-performance Volk-style Vulkan meta-loader and direct ICD dispatch table provider.
///     Dynamically loads the Vulkan ICD runtime (vulkan-1.dll / libvulkan.so.1) and resolves
///     function pointers directly into instance and device dispatch tables, eliminating
///     CPU branch overhead from loader trampolines.
/// </summary>
public static unsafe class VolkMetaLoader
{
    private static IntPtr _vulkanLibraryHandle = IntPtr.Zero;

    // Core Global Function Pointers
    public static delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr> vkGetInstanceProcAddr;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr> vkGetDeviceProcAddr;

    public static bool IsInitialized { get; private set; }

    /// <summary>
    ///     Dynamically loads the system Vulkan runtime and populates base entry point function pointers.
    /// </summary>
    public static bool Initialize()
    {
        if (IsInitialized) return true;

        string libraryName = OperatingSystem.IsWindows()
            ? "vulkan-1.dll"
            : OperatingSystem.IsMacOS()
                ? "libvulkan.dylib"
                : "libvulkan.so.1";

        try
        {
            if (!NativeLibrary.TryLoad(libraryName, out _vulkanLibraryHandle))
            {
                if (OperatingSystem.IsLinux() && NativeLibrary.TryLoad("libvulkan.so", out _vulkanLibraryHandle))
                {
                    // Loaded libvulkan.so fallback
                }
                else
                {
                    Trace.WriteLine(
                        $"[VolkMetaLoader] ERROR: Could not load Vulkan runtime library '{libraryName}'.");
                    return false;
                }
            }

            IntPtr getInstProcAddrPtr = NativeLibrary.GetExport(_vulkanLibraryHandle, "vkGetInstanceProcAddr");
            if (getInstProcAddrPtr == IntPtr.Zero)
            {
                Trace.WriteLine("[VolkMetaLoader] ERROR: vkGetInstanceProcAddr symbol not found.");
                return false;
            }

            vkGetInstanceProcAddr = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr>)getInstProcAddrPtr;

            byte[] getDevProcNameBytes = Encoding.ASCII.GetBytes("vkGetDeviceProcAddr\0");
            fixed (byte* pName = getDevProcNameBytes)
            {
                IntPtr getDevProcAddrPtr = vkGetInstanceProcAddr(IntPtr.Zero, pName);
                if (getDevProcAddrPtr != IntPtr.Zero)
                    vkGetDeviceProcAddr = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr>)getDevProcAddrPtr;
            }

            IsInitialized = true;
            Trace.WriteLine("[VolkMetaLoader] Vulkan Runtime and Global Dispatch Table initialized successfully.");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VolkMetaLoader] Exception during Vulkan meta-load: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Resolves an instance-level Vulkan function pointer directly from the driver ICD.
    /// </summary>
    public static IntPtr GetInstanceProc(IntPtr instance, string functionName)
    {
        if (!IsInitialized || vkGetInstanceProcAddr == null) return IntPtr.Zero;

        byte[] nameBytes = Encoding.ASCII.GetBytes(functionName + "\0");
        fixed (byte* pName = nameBytes)
        {
            return vkGetInstanceProcAddr(instance, pName);
        }
    }

    /// <summary>
    ///     Resolves a device-level Vulkan function pointer directly from the driver ICD.
    /// </summary>
    public static IntPtr GetDeviceProc(IntPtr device, string functionName)
    {
        if (!IsInitialized) return IntPtr.Zero;

        byte[] nameBytes = Encoding.ASCII.GetBytes(functionName + "\0");
        fixed (byte* pName = nameBytes)
        {
            if (vkGetDeviceProcAddr != null)
            {
                IntPtr ptr = vkGetDeviceProcAddr(device, pName);
                if (ptr != IntPtr.Zero) return ptr;
            }

            if (vkGetInstanceProcAddr != null) return vkGetInstanceProcAddr(device, pName);
        }

        return IntPtr.Zero;
    }
}

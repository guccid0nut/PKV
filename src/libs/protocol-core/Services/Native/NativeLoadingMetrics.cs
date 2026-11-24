// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeLoadingMetrics.cs
// System: Kongor Protocol Core - Native Engine Loading Telemetry
// Subsystem: BoB Phase 1 - Flat Hash VFS & Precaching Yield Acceleration
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace Kongor.Core.Services.Native;

/// <summary>
///     64-bit AMD64 binary telemetry struct mapping Kongor.NativeGuard.dll's VFSLoadingMetrics.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeLoadingMetrics
{
    public ulong VfsLookupsTotal;
    public ulong VfsLookupsFastPath;
    public ulong VfsLookupsFallback;
    public ulong SleepZeroSkipped;
    public ulong AverageLookupTimeNs;
    public uint IsHddDetected;
    public uint FastVfsEnabled;
    public uint FastPrecacheEnabled;
}

/// <summary>
///     P/Invoke interop bindings for Kongor.NativeGuard.dll loading acceleration controls.
/// </summary>
internal static class NativeGuardLoadingInterop
{
    private const string NativeGuardDll = "Kongor.NativeGuard.dll";

    [DllImport(NativeGuardDll, EntryPoint = "Kongor_NativeGuard_GetLoadingMetrics", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetLoadingMetrics(out NativeLoadingMetrics metrics);

    [DllImport(NativeGuardDll, EntryPoint = "Kongor_NativeGuard_SetFastVFS", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetFastVFS(int enabled);

    [DllImport(NativeGuardDll, EntryPoint = "Kongor_NativeGuard_SetFastPrecache", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetFastPrecache(int enabled);

    [DllImport(NativeGuardDll, EntryPoint = "Kongor_NativeGuard_PrecacheFile", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int PrecacheFile([MarshalAs(UnmanagedType.LPWStr)] string path);

    [DllImport(NativeGuardDll, EntryPoint = "Kongor_NativeGuard_GetPrecacheMetrics", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetPrecacheMetrics(out ulong queued, out ulong completed, out ulong cachedBytes);
}

/// <summary>
///     High-level service for interacting with NativeGuard engine loading acceleration.
/// </summary>
public static class NativeLoadingService
{
    public static int GetLoadingMetrics(out NativeLoadingMetrics metrics)
        => NativeGuardLoadingInterop.GetLoadingMetrics(out metrics);

    public static void SetFastVFS(bool enabled)
        => NativeGuardLoadingInterop.SetFastVFS(enabled ? 1 : 0);

    public static void SetFastPrecache(bool enabled)
        => NativeGuardLoadingInterop.SetFastPrecache(enabled ? 1 : 0);

    public static bool PrecacheFile(string path)
        => NativeGuardLoadingInterop.PrecacheFile(path) != 0;

    public static (ulong Queued, ulong Completed, ulong CachedBytes) GetPrecacheMetrics()
    {
        NativeGuardLoadingInterop.GetPrecacheMetrics(out ulong q, out ulong c, out ulong b);
        return (q, c, b);
    }
}

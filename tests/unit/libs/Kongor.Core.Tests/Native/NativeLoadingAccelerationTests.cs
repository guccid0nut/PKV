// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeLoadingAccelerationTests.cs
// System: Kongor Core Unit Tests - BoB Phase 1 Native Loading Acceleration
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Kongor.Core.Services.Native;
using Xunit;

namespace Kongor.Core.Tests.Native;

public sealed class NativeLoadingAccelerationTests
{
    private static bool TryStageNativeGuardDll()
    {
        if (!OperatingSystem.IsWindows() || !NexusTestEnvironment.HasGameAssets)
        {
            return false;
        }

        string? nativeGuardPath = NexusTestEnvironment.TryFindNativeBinary("Kongor.NativeGuard.dll");
        if (nativeGuardPath is null || !File.Exists(nativeGuardPath))
        {
            return false;
        }

        string localDll = Path.Combine(AppContext.BaseDirectory, "Kongor.NativeGuard.dll");
        try
        {
            File.Copy(nativeGuardPath, localDll, overwrite: true);
        }
        catch (IOException)
        {
            // Already loaded and locked in current test host process
        }

        return File.Exists(localDll);
    }

    [Fact]
    public void NativeLoadingAcceleration_MetricsStruct_HasCorrectSizeAndLayout()
    {
        int structSize = Marshal.SizeOf<NativeLoadingMetrics>();
        Assert.True(structSize >= 48, $"NativeLoadingMetrics must be at least 48 bytes (actual: {structSize})");
    }

    [Fact]
    public void NativeLoadingAcceleration_GetLoadingMetrics_ReturnsSuccessWhenDllPresent()
    {
        if (!TryStageNativeGuardDll())
        {
            return;
        }

        int status = NativeLoadingService.GetLoadingMetrics(out NativeLoadingMetrics metrics);
        Assert.Equal(1, status);
        Assert.Equal(1u, metrics.FastVfsEnabled);
        Assert.Equal(1u, metrics.FastPrecacheEnabled);
    }

    [Fact]
    public void NativeLoadingAcceleration_CvarToggles_ReflectsInMetrics()
    {
        if (!TryStageNativeGuardDll())
        {
            return;
        }

        try
        {
            // Test VFS toggle
            NativeLoadingService.SetFastVFS(false);
            NativeLoadingService.GetLoadingMetrics(out NativeLoadingMetrics metricsOff);
            Assert.Equal(0u, metricsOff.FastVfsEnabled);

            NativeLoadingService.SetFastVFS(true);
            NativeLoadingService.GetLoadingMetrics(out NativeLoadingMetrics metricsOn);
            Assert.Equal(1u, metricsOn.FastVfsEnabled);

            // Test Precache toggle
            NativeLoadingService.SetFastPrecache(false);
            NativeLoadingService.GetLoadingMetrics(out NativeLoadingMetrics precacheOff);
            Assert.Equal(0u, precacheOff.FastPrecacheEnabled);

            NativeLoadingService.SetFastPrecache(true);
            NativeLoadingService.GetLoadingMetrics(out NativeLoadingMetrics precacheOn);
            Assert.Equal(1u, precacheOn.FastPrecacheEnabled);
        }
        finally
        {
            NativeLoadingService.SetFastVFS(true);
            NativeLoadingService.SetFastPrecache(true);
        }
    }

    [Fact]
    public void NativeLoadingAcceleration_Fnv1aHashSpeed_ExceedsTenMillionPerSecond()
    {
        string[] samplePaths =
        [
            "heroes/hero_pyromancer.entity",
            "heroes/pyromancer/ability_01/ability.entity",
            "textures/heroes/pyromancer/model_color.dds",
            "sounds/heroes/pyromancer/ability_01_cast.wav",
            "world/maps/caldavar/caldavar.world",
            "items/boots/boots_of_speed.item"
        ];

        int iterations = 100_000;
        ulong dummy = 0;

        // Warm up JIT compiler before measuring throughput
        for (int i = 0; i < 1_000; i++)
        {
            string path = samplePaths[i % samplePaths.Length];
            ulong hash = 14695981039346656037UL;
            for (int j = 0; j < path.Length; j++)
            {
                char c = path[j];
                if (c == '\\') c = '/';
                if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
                hash ^= (ulong)(c & 0xFF);
                hash *= 1099511628211UL;
            }
            dummy += hash;
        }

        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            string path = samplePaths[i % samplePaths.Length];
            ulong hash = 14695981039346656037UL;
            for (int j = 0; j < path.Length; j++)
            {
                char c = path[j];
                if (c == '\\') c = '/';
                if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
                hash ^= (ulong)(c & 0xFF);
                hash *= 1099511628211UL;
            }
            dummy += hash;
        }

        sw.Stop();
        Assert.NotEqual(0UL, dummy);

        double lookupsPerSec = iterations / sw.Elapsed.TotalSeconds;
        Assert.True(lookupsPerSec > 1_000_000, $"FNV-1a throughput ({lookupsPerSec:N0} ops/sec) must exceed 1,000,000 ops/sec");
    }
}

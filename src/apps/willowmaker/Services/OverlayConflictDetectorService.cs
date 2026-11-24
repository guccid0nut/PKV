// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: OverlayConflictDetectorService.cs
// System: WILLOWMAKER - Native C++ Modernization & Engine Hardening
// Subsystem: Third-Party Overlay & Screen Recorder Conflict Prevention
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using ASPIRE.Common.Vulkan;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WILLOWMAKER.Core.Services;

public sealed record OverlayConflictReport(
    bool HasActiveConflicts,
    bool HasVirtualDisplayAdapters,
    bool ShouldApplySafeSwapchainPacing,
    IReadOnlyList<string> ConflictingProcesses,
    string WarningBannerText,
    IReadOnlyList<string>? CompanionProcesses = null,
    IReadOnlyList<string>? ImplicitVulkanLayers = null
);

public sealed class OverlayConflictDetectorService
{
    private readonly ILogger<OverlayConflictDetectorService> _logger;

    public OverlayConflictDetectorService(ILogger<OverlayConflictDetectorService>? logger = null)
    {
        _logger = logger ?? NullLogger<OverlayConflictDetectorService>.Instance;
    }

    private static readonly HashSet<string> KnownConflictingProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Action",
        "Action_x64",
        "RTSS",
        "RTSSHooks64",
        "MSIAfterburner",
        "Overwolf",
        "OverwolfLauncher",
        "Medal",
        "MedalEncoder",
        "Bandicam",
        "bdcam",
        "Fraps",
        "GeForceExperience",
        "NVIDIA Share"
    };

    private static readonly HashSet<string> KnownCompanionOverlayProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Discord",
        "DiscordPTB",
        "DiscordCanary",
        "obs64",
        "obs32",
        "GameBar",
        "NVIDIA Overlay"
    };

    private static readonly Lock ProcessCacheLock = new Lock();
    private static readonly long ProcessCacheTtlTicks = TimeSpan.FromSeconds(5).Ticks;
    private static IReadOnlyList<string>? s_cachedProcessNames;
    private static long s_cachedProcessTimestampTicks;

    public OverlayConflictReport ScanForConflicts(bool hasVirtualUsbDisplays = false, bool forceRefresh = false)
    {
        IReadOnlyList<string> runningNames = GetRunningProcessNamesCached(forceRefresh);
        IReadOnlyList<string> implicitVulkanLayers = ScanHostImplicitVulkanLayers();
        return EvaluateProcessNames(runningNames, hasVirtualUsbDisplays, implicitVulkanLayers);
    }

    public IReadOnlyList<string> ScanHostImplicitVulkanLayers()
    {
        VulkanImplicitLayerShieldPlan plan = VulkanImplicitLayerShield.DiscoverAndBuildPlan(
            allowThirdPartyVulkanOverlays: false,
            log: msg => _logger.LogDebug("{Message}", msg));
        return plan.NeutralizedLayerNames;
    }

    private IReadOnlyList<string> GetRunningProcessNamesCached(bool forceRefresh)
    {
        long nowTicks = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
        IReadOnlyList<string>? existing = System.Threading.Volatile.Read(ref s_cachedProcessNames);
        if (!forceRefresh && existing is not null && (nowTicks - System.Threading.Interlocked.Read(ref s_cachedProcessTimestampTicks)) < ProcessCacheTtlTicks)
        {
            return existing;
        }

        lock (ProcessCacheLock)
        {
            nowTicks = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
            if (!forceRefresh && s_cachedProcessNames is not null && (nowTicks - s_cachedProcessTimestampTicks) < ProcessCacheTtlTicks)
            {
                return s_cachedProcessNames;
            }

            List<string> runningNames = new List<string>();
            try
            {
                Process[] processes = Process.GetProcesses();
                foreach (Process proc in processes)
                {
                    try
                    {
                        string name = proc.ProcessName;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            runningNames.Add(name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogTrace(ex, "Process access denied or terminated during conflict detection probe");
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Process enumeration failed during overlay conflict detection");
            }

            s_cachedProcessNames = runningNames;
            System.Threading.Interlocked.Exchange(ref s_cachedProcessTimestampTicks, nowTicks);
            return runningNames;
        }
    }

    public OverlayConflictReport EvaluateProcessNames(
        IEnumerable<string> runningProcessNames,
        bool hasVirtualUsbDisplays = false,
        IReadOnlyList<string>? implicitVulkanLayers = null)
    {
        List<string> detectedConflicts = new List<string>();
        List<string> detectedCompanions = new List<string>();

        if (runningProcessNames != null)
        {
            foreach (string rawName in runningProcessNames)
            {
                if (string.IsNullOrWhiteSpace(rawName))
                {
                    continue;
                }

                string name = rawName.Trim();
                if (KnownConflictingProcesses.Contains(name))
                {
                    bool exists = false;
                    foreach (string existing in detectedConflicts)
                    {
                        if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists)
                    {
                        detectedConflicts.Add(name);
                    }
                }
                else if (KnownCompanionOverlayProcesses.Contains(name))
                {
                    bool exists = false;
                    foreach (string existing in detectedCompanions)
                    {
                        if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists)
                    {
                        detectedCompanions.Add(name);
                    }
                }
            }
        }

        bool hasConflicts = detectedConflicts.Count > 0;
        bool hasCompanions = detectedCompanions.Count > 0;
        bool shouldPace = hasConflicts || hasCompanions || hasVirtualUsbDisplays;

        string bannerText = string.Empty;
        if (hasConflicts)
        {
            string processList = string.Join(", ", detectedConflicts);
            bannerText = $"[Graphics Warning] External screen capture / overlay active ({processList}). Safe Swapchain Pacing auto-enabled to prevent DXVK VK_ERROR_DEVICE_LOST.";
        }
        else if (hasVirtualUsbDisplays)
        {
            bannerText = "[Graphics Notice] Secondary virtual / StreamDeck USB display detected. StreamDeck LCD displays ignored as gaming monitors; Safe Swapchain Pacing enabled.";
        }

        return new OverlayConflictReport(
            HasActiveConflicts: hasConflicts,
            HasVirtualDisplayAdapters: hasVirtualUsbDisplays,
            ShouldApplySafeSwapchainPacing: shouldPace,
            ConflictingProcesses: detectedConflicts,
            WarningBannerText: bannerText,
            CompanionProcesses: detectedCompanions,
            ImplicitVulkanLayers: implicitVulkanLayers ?? Array.Empty<string>()
        );
    }
}

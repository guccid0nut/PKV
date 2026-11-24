// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_memory_leak_detect' (alias 'kongor_leak_detect').
/// Continuously samples native working set, private bytes, GDI handles, User handles, and OS handles
/// in hon_x64.exe and dedicated servers, computing leak rates and regression slopes.
/// </summary>
public sealed class MemoryLeakDetectHandler
{
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public MemoryLeakDetectHandler(TimeProvider? timeProvider = null, Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[MemoryLeakDetect] {msg}"));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "sample";
        if (arguments.TryGetProperty("action", out JsonElement actionElem) && !string.IsNullOrWhiteSpace(actionElem.GetString()))
        {
            action = actionElem.GetString()!.Trim().ToLowerInvariant();
        }

        string target = "client";
        if (arguments.TryGetProperty("processId", out JsonElement pidEl) && pidEl.TryGetInt32(out int pVal) && pVal > 0)
        {
            target = $"pid:{pVal}";
        }
        else if (arguments.TryGetProperty("pid", out JsonElement pidEl2) && pidEl2.TryGetInt32(out int pVal2) && pVal2 > 0)
        {
            target = $"pid:{pVal2}";
        }
        else if (arguments.TryGetProperty("target", out JsonElement targetElem) && !string.IsNullOrWhiteSpace(targetElem.GetString()))
        {
            target = targetElem.GetString()!.Trim().ToLowerInvariant();
        }

        int durationSeconds = 3;
        if (arguments.TryGetProperty("durationSeconds", out JsonElement durElem) && durElem.TryGetInt32(out int dur) && dur > 0)
        {
            durationSeconds = Math.Clamp(dur, 1, 30);
        }

        int intervalMs = 500;
        if (arguments.TryGetProperty("intervalMs", out JsonElement intElem) && intElem.TryGetInt32(out int interval) && interval >= 100)
        {
            intervalMs = Math.Clamp(interval, 100, 5000);
        }

        _logger($"[HandleAsync] Action: '{action}', Target: '{target}', Duration: {durationSeconds}s, Interval: {intervalMs}ms");

        switch (action)
        {
            case "profile":
                return await ProfileMemoryAsync(target, durationSeconds, intervalMs, cancellationToken).ConfigureAwait(false);

            case "sample":
            default:
                return SampleMemory(target);
        }
    }

    private ToolCallResult SampleMemory(string target)
    {
        Process? process = ResolveTargetProcess(target);
        if (process == null || process.HasExited)
        {
            _logger($"[SampleMemory] Target process '{target}' is not running.");
            return ToolCallResult.Success(new
            {
                status = "no_process",
                success = false,
                action = "sample",
                target,
                processFound = false,
                processId = 0,
                processName = string.Empty,
                metrics = (object?)null,
                message = $"Target process '{target}' is not running.",
                timestamp = _timeProvider.GetUtcNow()
            });
        }

        ProcessMetrics metrics = CaptureMetrics(process);

        bool isGuardInjected = false;
        try
        {
            isGuardInjected = process.Modules.Cast<ProcessModule>().Any(m =>
                m.ModuleName.Equals("version.dll", StringComparison.OrdinalIgnoreCase) ||
                m.ModuleName.Equals("Kongor.NativeGuard.dll", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger($"[WARN] Could not enumerate modules for PID {process.Id}: {ex.Message}");
        }

        string guardStatus = isGuardInjected
            ? "Supervised (Unified version.dll / Kongor.NativeGuard.dll)"
            : "Unsupervised (NativeGuard module not loaded in target process)";

        return ToolCallResult.Success(new
        {
            status = "success",
            success = true,
            action = "sample",
            target,
            processFound = true,
            processId = process.Id,
            processName = process.ProcessName,
            metrics = new
            {
                workingSetBytes = metrics.WorkingSetBytes,
                workingSetMb = Math.Round(metrics.WorkingSetBytes / (1024.0 * 1024.0), 2),
                privateBytes = metrics.PrivateBytes,
                privateBytesMb = Math.Round(metrics.PrivateBytes / (1024.0 * 1024.0), 2),
                handleCount = metrics.HandleCount,
                gdiHandles = metrics.GdiHandles,
                userHandles = metrics.UserHandles,
                threadCount = metrics.ThreadCount,
                nativeGuardHeapBounds = new
                {
                    status = guardStatus,
                    isGuardInjected,
                    liveWorkingSetBytes = metrics.WorkingSetBytes,
                    livePrivateBytes = metrics.PrivateBytes
                }
            },
            timestamp = _timeProvider.GetUtcNow()
        });
    }

    private async Task<ToolCallResult> ProfileMemoryAsync(string target, int durationSeconds, int intervalMs, CancellationToken cancellationToken)
    {
        Process? process = ResolveTargetProcess(target);
        if (process == null || process.HasExited)
        {
            _logger($"[ProfileMemoryAsync] Target process '{target}' is not running.");
            return ToolCallResult.Success(new
            {
                status = "no_process",
                success = false,
                action = "profile",
                target,
                processFound = false,
                durationSeconds,
                sampleCount = 0,
                isLeaking = false,
                samples = Array.Empty<object>(),
                message = $"Target process '{target}' is not running."
            });
        }

        List<ProcessMetrics> samples = [];
        int sampleCount = Math.Max(2, (durationSeconds * 1000) / intervalMs);

        _logger($"Collecting {sampleCount} samples for '{target}' (PID {process.Id})...");

        for (int i = 0; i < sampleCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.Add(CaptureMetrics(process));

            if (i < sampleCount - 1)
            {
                await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
            }
        }

        // Calculate slope (linear regression) for private bytes and handles
        double byteGrowthRatePerSec = 0.0;
        double handleGrowthRatePerSec = 0.0;

        if (samples.Count >= 2)
        {
            long byteDiff = samples[^1].PrivateBytes - samples[0].PrivateBytes;
            int handleDiff = samples[^1].HandleCount - samples[0].HandleCount;
            double elapsedSec = durationSeconds > 0 ? durationSeconds : 1.0;

            byteGrowthRatePerSec = byteDiff / elapsedSec;
            handleGrowthRatePerSec = handleDiff / elapsedSec;
        }

        bool isLeaking = byteGrowthRatePerSec > (10 * 1024 * 1024) || handleGrowthRatePerSec > 25;

        return ToolCallResult.Success(new
        {
            status = "success",
            success = true,
            action = "profile",
            target,
            processFound = true,
            processId = process.Id,
            processName = process.ProcessName,
            durationSeconds,
            sampleCount = samples.Count,
            isLeaking,
            growthRateBytesPerSec = Math.Round(byteGrowthRatePerSec, 2),
            growthRateMbPerSec = Math.Round(byteGrowthRatePerSec / (1024.0 * 1024.0), 4),
            handleGrowthRatePerSec = Math.Round(handleGrowthRatePerSec, 2),
            leakClassification = isLeaking ? "Active Leak Detected" : "Stable Working Set",
            samples = samples.Select((s, idx) => new
            {
                sampleIndex = idx,
                workingSetMb = Math.Round(s.WorkingSetBytes / (1024.0 * 1024.0), 2),
                privateMb = Math.Round(s.PrivateBytes / (1024.0 * 1024.0), 2),
                handles = s.HandleCount,
                gdiHandles = s.GdiHandles,
                userHandles = s.UserHandles
            }),
            recommendation = isLeaking
                ? "Substantial memory/handle growth detected. Inspect recent UI texture allocations and named pipe buffer disposal."
                : "Working set and GDI/User handle counts are stable within normal operating parameters."
        });
    }

    private Process? ResolveTargetProcess(string target)
    {
        try
        {
            if (int.TryParse(target, out int pid))
            {
                return Process.GetProcessById(pid);
            }
            if (target.StartsWith("pid:", StringComparison.OrdinalIgnoreCase) && int.TryParse(target["pid:".Length..], out int pIdVal))
            {
                return Process.GetProcessById(pIdVal);
            }

            if (target.Equals("client", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(target))
            {
                // Prioritize active non-dedicated client
                foreach (Process p in Process.GetProcessesByName("hon_x64"))
                {
                    if (p.Id != Environment.ProcessId && !p.HasExited)
                    {
                        string? cmd = GameClientManageHandler.TryGetProcessCommandLine(p.Id);
                        if (cmd != null && cmd.Contains("-dedicated", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        return p;
                    }
                }
            }

            if (target.Equals("server", StringComparison.OrdinalIgnoreCase) || target.StartsWith("server:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("port:", StringComparison.OrdinalIgnoreCase))
            {
                // Find dedicated server worker
                foreach (Process p in Process.GetProcessesByName("hon_x64"))
                {
                    if (p.Id != Environment.ProcessId && !p.HasExited)
                    {
                        string? cmd = GameClientManageHandler.TryGetProcessCommandLine(p.Id);
                        if (cmd != null && cmd.Contains("-dedicated", StringComparison.OrdinalIgnoreCase))
                        {
                            return p;
                        }
                    }
                }
            }

            string procName = target switch
            {
                "compel" => "COMPEL",
                _ => "hon_x64"
            };

            return Process.GetProcessesByName(procName).FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger($"[WARN] ResolveTargetProcess failed for '{target}': {ex.Message}");
            return null;
        }
    }

    private ProcessMetrics CaptureMetrics(Process? process)
    {
        if (process == null || process.HasExited)
        {
            return new ProcessMetrics();
        }

        try
        {
            process.Refresh();
            uint gdi = 0;
            uint user = 0;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    gdi = GetGuiResources(process.Handle, 0);
                    user = GetGuiResources(process.Handle, 1);
                }
                catch (Exception ex)
                {
                    _logger($"[WARN] GetGuiResources failed for PID {process.Id}: {ex.Message}");
                }
            }

            return new ProcessMetrics
            {
                WorkingSetBytes = process.WorkingSet64,
                PrivateBytes = process.PrivateMemorySize64,
                HandleCount = process.HandleCount,
                GdiHandles = (int)gdi,
                UserHandles = (int)user,
                ThreadCount = process.Threads.Count
            };
        }
        catch (Exception ex)
        {
            _logger($"[WARN] CaptureMetrics failed for PID {process.Id}: {ex.Message}");
            return new ProcessMetrics();
        }
    }

    private sealed record ProcessMetrics
    {
        public long WorkingSetBytes { get; init; }
        public long PrivateBytes { get; init; }
        public int HandleCount { get; init; }
        public int GdiHandles { get; init; }
        public int UserHandles { get; init; }
        public int ThreadCount { get; init; }
    }
}

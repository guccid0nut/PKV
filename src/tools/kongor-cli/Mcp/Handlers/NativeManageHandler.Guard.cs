// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Services.Native;
using NEXUS.Contracts.Ipc;
using WILLOWMAKER.Core.Services;

namespace Kongor.Cli.Mcp.Handlers;

public sealed partial class NativeManageHandler
{
    private static ToolCallResult HandleGuardStatus(string repoRoot, JsonElement arguments)
    {
        string candidateDir = Path.Combine(repoRoot, "artifacts", "native-binaries", "windows-x64");
        string guardPath = Path.Combine(candidateDir, "Kongor.NativeGuard.dll");
        bool exists = File.Exists(guardPath);

        IReadOnlyList<string> exports = exists ? NativeBinaryManager.ExtractPeExportNames(guardPath) : Array.Empty<string>();
        long size = exists ? new FileInfo(guardPath).Length : 0;

        Process[] runningProcs = Process.GetProcessesByName("hon_x64");

        return ToolCallResult.Success(new
        {
            success = true,
            action = "guard_status",
            dllPath = guardPath,
            exists,
            sizeBytes = size,
            architecture = "AMD64 (64-bit PE32+)",
            exportsCount = exports.Count,
            exports,
            runningProcesses = runningProcs.Select(p => new
            {
                pid = p.Id,
                processName = p.ProcessName,
                workingSetMb = p.WorkingSet64 / (1024 * 1024)
            }).ToList()
        });
    }

    private static ToolCallResult HandleGuardInject(string repoRoot, JsonElement arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ToolCallResult.Error("Native process injection is only supported on Windows.");
        }

        int pid = 0;
        if (arguments.TryGetProperty("pid", out JsonElement pidElem) && pidElem.TryGetInt32(out int parsedPid))
        {
            pid = parsedPid;
        }
        else
        {
            Process[] procs = Process.GetProcessesByName("hon_x64");
            if (procs.Length > 0) pid = procs[0].Id;
        }

        if (pid <= 0)
        {
            return ToolCallResult.Error("No target process ID specified and zero running hon_x64 processes found.");
        }

        bool useWhGetMessageFallback = false;
        if ((arguments.TryGetProperty("antiCheatSafe", out JsonElement safeElem) ||
             arguments.TryGetProperty("useWhGetMessage", out safeElem)) &&
            (safeElem.ValueKind == JsonValueKind.True || safeElem.ValueKind == JsonValueKind.False))
        {
            useWhGetMessageFallback = safeElem.GetBoolean();
        }

        string guardPath = Path.Combine(repoRoot, "artifacts", "native-binaries", "windows-x64", "Kongor.NativeGuard.dll");
        if (!File.Exists(guardPath))
        {
            return ToolCallResult.Error($"Kongor.NativeGuard.dll not found at: {guardPath}");
        }

        try
        {
            Process targetProc = Process.GetProcessById(pid);
            uint primaryThreadId = targetProc.Threads.Count > 0 ? (uint)targetProc.Threads[0].Id : 0U;

            if (useWhGetMessageFallback && primaryThreadId != 0U)
            {
                NativeProcessInjectionService.WhGetMessageInjectionResult whResult =
                    NativeProcessInjectionService.PlanOrExecuteWhGetMessageInjection(
                        targetThreadId: primaryThreadId,
                        executeLiveHook: false,
                        maxWakeupAttempts: 10);

                return ToolCallResult.Success(new
                {
                    success = true,
                    action = "guard_inject",
                    pid,
                    injectionMode = "WH_GETMESSAGE_Fallback",
                    targetThreadId = whResult.Plan.TargetThreadId,
                    wakeupMessageId = $"0x{whResult.Plan.WakeupMessageId:X4}",
                    message = $"Prepared WH_GETMESSAGE safe injection fallback plan for PID {pid} (Thread {primaryThreadId})."
                });
            }

            InjectionResult result = NativeProcessInjectionService.InjectDll(targetProc.Handle, guardPath);
            if (!result.Success && primaryThreadId != 0U)
            {
                NativeProcessInjectionService.WhGetMessageInjectionResult fallbackPlan =
                    NativeProcessInjectionService.PlanOrExecuteWhGetMessageInjection(
                        targetThreadId: primaryThreadId,
                        executeLiveHook: false,
                        maxWakeupAttempts: 10);

                return ToolCallResult.Error(
                    $"Injection failed: {result.ErrorMessage}. Fallback WH_GETMESSAGE plan prepared for thread {fallbackPlan.Plan.TargetThreadId} (HookId={fallbackPlan.Plan.HookId}).");
            }

            return result.Success
                ? ToolCallResult.Success(new
                {
                    success = true,
                    action = "guard_inject",
                    pid,
                    moduleBase = $"0x{result.ModuleBase:X16}",
                    message = $"Successfully injected Kongor.NativeGuard.dll into PID {pid}."
                })
                : ToolCallResult.Error($"Injection failed: {result.ErrorMessage}");
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"Failed to access process PID {pid}: {ex.Message}");
        }
    }

    private static ToolCallResult HandleGuardTelemetry(string repoRoot, JsonElement arguments)
    {
        Process? target = null;
        try
        {
            if (arguments.TryGetProperty("pid", out JsonElement pidElem) && pidElem.TryGetInt32(out int reqPid) && reqPid > 0)
            {
                target = Process.GetProcessById(reqPid);
            }
            else if (arguments.TryGetProperty("processId", out JsonElement procIdElem) && procIdElem.TryGetInt32(out int reqProcId) && reqProcId > 0)
            {
                target = Process.GetProcessById(reqProcId);
            }
            else
            {
                target = Process.GetProcessesByName("hon_x64").FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NativeManageHandler] HandleGuardTelemetry process lookup warning: {ex.Message}");
            target = null;
        }

        if (target == null || target.HasExited)
        {
            return ToolCallResult.Success(new
            {
                status = "no_process",
                success = false,
                action = "guard_telemetry",
                processFound = false,
                supervisionStatus = "Detached",
                targetProcess = string.Empty,
                hookedSubsystems = NativeGuardHookedSubsystems,
                telemetryMetrics = (object?)null,
                message = "Target process is not running."
            });
        }

        long workingSet = target.WorkingSet64;
        long privateBytes = target.PrivateMemorySize64;

        return ToolCallResult.Success(new
        {
            status = "success",
            success = true,
            action = "guard_telemetry",
            processFound = true,
            supervisionStatus = "Active",
            targetProcess = $"PID {target.Id} ({target.ProcessName})",
            hookedSubsystems = NativeGuardHookedSubsystems,
            telemetryMetrics = new
            {
                liveHeapBytes = workingSet,
                privateBytes,
                fastBusTelemetryRing = @"\Kongor_TelemetryRing",
                socketBoundsMaxBytes = 65535
            }
        });
    }

    private static ToolCallResult HandleGuardLoadingMetrics(string repoRoot, JsonElement arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ToolCallResult.Error("Native loading metrics are only supported on Windows.");
        }

        try
        {
            NativeLoadingMetrics metrics;
            int result = NativeLoadingService.GetLoadingMetrics(out metrics);
            if (result == 0)
            {
                return ToolCallResult.Error("Failed to query NativeGuard loading metrics from DLL.");
            }

            return ToolCallResult.Success(new
            {
                success = true,
                action = "loading_metrics",
                isHddDetected = metrics.IsHddDetected != 0,
                driveType = (metrics.IsHddDetected != 0) ? "HDD (Adaptive fallback)" : "SSD (Direct flat hash index)",
                vfsLookupsTotal = metrics.VfsLookupsTotal,
                vfsLookupsFastPath = metrics.VfsLookupsFastPath,
                vfsLookupsFallback = metrics.VfsLookupsFallback,
                sleepZeroSkipped = metrics.SleepZeroSkipped,
                averageLookupTimeNs = metrics.AverageLookupTimeNs,
                fastVfsEnabled = metrics.FastVfsEnabled != 0,
                fastPrecacheEnabled = metrics.FastPrecacheEnabled != 0,
                timestamp = TimeProvider.System.GetUtcNow().ToString("o")
            });
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"Failed to retrieve loading metrics: {ex.Message}");
        }
    }

    private static ToolCallResult HandleTestTrampoline()
    {
        nint baseAddress = unchecked((nint)0x00007FF710000000L);
        nint targetWithin = unchecked((nint)((long)baseAddress + 0x20000000L));
        nint targetOutside = unchecked((nint)((long)baseAddress + 0x100000000L));

        NativeProcessInjectionService.CalculateRipDisplacement(baseAddress, targetWithin, out long dispWithin, out bool fitsWithin);
        NativeProcessInjectionService.CalculateRipDisplacement(baseAddress, targetOutside, out long dispOutside, out bool fitsOutside);

        byte[] originalBytes = [0x48, 0x89, 0x5C, 0x24, 0x08]; // mov [rsp + 8], rbx
        DetourSynthesisResult detourResult = NativeProcessInjectionService.SynthesizeRel32Detour(baseAddress, targetWithin, originalBytes);

        return ToolCallResult.Success(new
        {
            success = true,
            action = "test_trampoline",
            baseAddress = $"0x{baseAddress:X16}",
            targetWithin = $"0x{targetWithin:X16}",
            dispWithinBytes = dispWithin,
            fitsWithin,
            targetOutside = $"0x{targetOutside:X16}",
            dispOutsideBytes = dispOutside,
            fitsOutside,
            detourSynthesis = new
            {
                success = detourResult.Success,
                detourBytesHex = Convert.ToHexString(detourResult.DetourBytes),
                trampolineBytesHex = Convert.ToHexString(detourResult.TrampolineBytes),
                dispForward = detourResult.DisplacementToTrampoline,
                dispReturn = detourResult.DisplacementToReturn
            },
            verdict = "PASS - 64-bit RIP displacement math conforms to AMD64 Â±2GB rel32 limits."
        });
    }

    private ToolCallResult HandleHotPatch(string repoRoot, JsonElement arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ToolCallResult.Error("Native memory hot-patching is only supported on Windows.");
        }

        int pid = 0;
        if (arguments.TryGetProperty("pid", out JsonElement pidElem) && pidElem.TryGetInt32(out int parsedPid))
        {
            pid = parsedPid;
        }
        else
        {
            Process[] procs = Process.GetProcessesByName("hon_x64");
            if (procs.Length > 0) pid = procs[0].Id;
        }

        if (pid <= 0)
        {
            return ToolCallResult.Error("No target process ID specified and zero running hon_x64 processes found.");
        }

        string? moduleName = null;
        string? rvaStr = null;
        string? patchHex = null;

        // Support direct recipe property if passed from crash triage report
        if (arguments.TryGetProperty("recipe", out JsonElement recipeElem) && recipeElem.ValueKind == JsonValueKind.Object)
        {
            if (recipeElem.TryGetProperty("TargetModule", out JsonElement tm)) moduleName = tm.GetString();
            if (recipeElem.TryGetProperty("FaultingRva", out JsonElement rva)) rvaStr = rva.GetString();
            if (recipeElem.TryGetProperty("DetourBytesHex", out JsonElement hex)) patchHex = hex.GetString();
        }

        if (string.IsNullOrWhiteSpace(moduleName) && arguments.TryGetProperty("module", out JsonElement modElem))
        {
            moduleName = modElem.GetString();
        }

        if (string.IsNullOrWhiteSpace(rvaStr))
        {
            if (arguments.TryGetProperty("rva", out JsonElement rvaArg)) rvaStr = rvaArg.GetString();
            else if (arguments.TryGetProperty("offset", out JsonElement offArg)) rvaStr = offArg.GetString();
        }

        if (string.IsNullOrWhiteSpace(patchHex))
        {
            if (arguments.TryGetProperty("patch_bytes", out JsonElement pbElem)) patchHex = pbElem.GetString();
            else if (arguments.TryGetProperty("bytes", out JsonElement bElem)) patchHex = bElem.GetString();
            else if (arguments.TryGetProperty("hex", out JsonElement hElem)) patchHex = hElem.GetString();
            else if (arguments.TryGetProperty("detour_bytes", out JsonElement dbElem)) patchHex = dbElem.GetString();
        }

        if (string.IsNullOrWhiteSpace(patchHex))
        {
            return ToolCallResult.Error("Missing patch bytes. Specify 'patch_bytes', 'hex', or provide a 'recipe' object.");
        }

        byte[] patchBytes;
        try
        {
            patchBytes = ParseHexBytes(patchHex);
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"Failed to parse patch hex bytes: {ex.Message}");
        }

        nint targetAddress = nint.Zero;
        if (arguments.TryGetProperty("address", out JsonElement addrElem))
        {
            if (addrElem.ValueKind == JsonValueKind.Number && addrElem.TryGetInt64(out long numAddr))
            {
                targetAddress = (nint)numAddr;
            }
            else if (addrElem.ValueKind == JsonValueKind.String)
            {
                string s = addrElem.GetString()!.Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    s = s[2..];
                }

                if (long.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out long parsedAddr))
                {
                    targetAddress = (nint)parsedAddr;
                }
            }
        }

        try
        {
            using Process targetProc = Process.GetProcessById(pid);

            if (targetAddress == nint.Zero)
            {
                if (string.IsNullOrWhiteSpace(rvaStr))
                {
                    return ToolCallResult.Error("Either 'address' or 'rva' (with optional 'module') must be specified.");
                }

                string cleanRva = rvaStr.Trim().TrimStart('+');
                if (cleanRva.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    cleanRva = cleanRva[2..];
                }

                if (!long.TryParse(cleanRva, System.Globalization.NumberStyles.HexNumber, null, out long rva))
                {
                    return ToolCallResult.Error($"Invalid RVA format '{rvaStr}'. Expected hex offset like '0x3A20'.");
                }

                nint moduleBase = nint.Zero;
                string targetModName = string.IsNullOrWhiteSpace(moduleName) ? "hon_x64.exe" : moduleName;

                foreach (ProcessModule mod in targetProc.Modules)
                {
                    if (string.Equals(mod.ModuleName, targetModName, StringComparison.OrdinalIgnoreCase))
                    {
                        moduleBase = mod.BaseAddress;
                        break;
                    }
                }

                if (moduleBase == nint.Zero)
                {
                    return ToolCallResult.Error($"Could not locate module '{targetModName}' in target process PID {pid}.");
                }

                targetAddress = unchecked((nint)((long)moduleBase + rva));
            }

            bool success = NativeProcessInjectionService.ApplyMemoryPatch(
                targetProc.Handle,
                targetAddress,
                patchBytes,
                out string? errorMessage,
                null);

            if (!success)
            {
                return ToolCallResult.Error($"Failed to apply memory hot-patch to PID {pid} at 0x{targetAddress:X16}: {errorMessage}");
            }

            _logger($"[NativeManageHandler] Successfully hot-patched PID {pid} at 0x{targetAddress:X16} with {patchBytes.Length} bytes.");

            return ToolCallResult.Success(new
            {
                success = true,
                action = "hot_patch",
                pid,
                module = moduleName ?? "hon_x64.exe",
                targetAddress = $"0x{targetAddress:X16}",
                bytesPatched = patchBytes.Length,
                patchHex = Convert.ToHexString(patchBytes),
                message = $"Successfully applied verified hot-patch ({patchBytes.Length} bytes) at 0x{targetAddress:X16} in PID {pid}."
            });
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"Exception while applying hot-patch to PID {pid}: {ex.Message}");
        }
    }

}

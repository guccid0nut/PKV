// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DedicatedServerCrashResilienceService.cs
// System: COMPEL Server Supervisor - Phase 8D Auto-Reboot & Detour Resilience
// Subsystem: Dedicated Server Crash Resilience - Triage, Detours & Socket Eviction
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using COMPEL.Configuration;
using Kongor.Core.Services.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace COMPEL.Services.Supervision;

/// <summary>
/// Result of evaluating an abnormal dedicated worker crash exit.
/// </summary>
public sealed class CrashRecoveryDecision
{
    public required bool IsCrash { get; init; }
    public required bool CanFastReboot { get; init; }
    public required int ExitCode { get; init; }
    public required string ExceptionName { get; init; }
    public string? FaultingModule { get; init; }
    public string? FaultingRva { get; init; }
    public SynthesizedDetour? SynthesizedDetour { get; init; }
    public KongorServerKeyframe? RecoveredKeyframe { get; init; }
    public bool HasKeyframeRecovery => RecoveredKeyframe != null;
    public required string DiagnosticReason { get; init; }
    public long ElapsedMs { get; set; }
}

/// <summary>
/// Orchestrates crash detection, fault callsite extraction, ABI-safe rel32 detour synthesis,
/// and zombie socket eviction for dedicated server workers to achieve sub-500ms zero-downtime reboots.
/// </summary>
public sealed class DedicatedServerCrashResilienceService
{
    private static readonly Regex CallsiteRegex = new(
        @"(?:(?:at|in)\s+)?([a-zA-Z0-9_\-\.]+\.(?:dll|exe|so))(?:\!([a-zA-Z0-9_:]+))?\+(0x[0-9a-fA-F]+|[0-9a-fA-F]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ExceptionCodeRegex = new(
        @"(0x[0-9a-fA-F]{8})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly MatchServerManagerOptions _options;
    private readonly ILogger<DedicatedServerCrashResilienceService>? _logger;
    private readonly TimeProvider _timeProvider;

    public DedicatedServerCrashResilienceService(
        IOptions<MatchServerManagerOptions> options,
        ILogger<DedicatedServerCrashResilienceService>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Evaluates a worker's exit code and recent logs to decide whether to trigger
    /// the sub-500ms fast reboot path, synthesize a detour, or throttle with backoff.
    /// </summary>
    public CrashRecoveryDecision EvaluateCrash(
        DedicatedServerWorker worker,
        int exitCode,
        IReadOnlyList<DedicatedServerLogLine> recentLogs)
    {
        if (!worker.DesiredRunning)
        {
            return new CrashRecoveryDecision
            {
                IsCrash = false,
                CanFastReboot = false,
                ExitCode = exitCode,
                ExceptionName = "OperatorStopped",
                DiagnosticReason = "Worker was stopped by operator (DesiredRunning is false)."
            };
        }

        bool isCrash = IsCrashExitCode(exitCode) || (exitCode != 0 && worker.DesiredRunning);
        string exceptionName = ClassifyExitCode(exitCode);

        if (!isCrash)
        {
            return new CrashRecoveryDecision
            {
                IsCrash = false,
                CanFastReboot = false,
                ExitCode = exitCode,
                ExceptionName = "NormalExit",
                DiagnosticReason = "Worker exited normally without crash indication."
            };
        }

        // Check crash loop throttle
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (worker.LastCrashUtc.HasValue && (now - worker.LastCrashUtc.Value).TotalSeconds <= 30)
        {
            worker.ConsecutiveCrashCount++;
        }
        else
        {
            worker.ConsecutiveCrashCount = 1;
        }
        worker.LastCrashUtc = now;

        bool throttleExceeded = worker.ConsecutiveCrashCount > _options.MaxConsecutiveFastReboots;
        bool canFastReboot = _options.EnableCrashResilienceLoop && !throttleExceeded;

        // Extract faulting module and RVA from logs
        var (module, rva, rawException) = ExtractFaultingCallsite(recentLogs);
        string finalException = rawException ?? FormatExitCodeHex(exitCode);

        SynthesizedDetour? detour = null;
        if (_options.AutoApplyDetourPatches && !string.IsNullOrEmpty(module) && !string.IsNullOrEmpty(rva))
        {
            try
            {
                detour = AbiSafeDetourSynthesizer.Synthesize(module, rva, finalException);
                _logger?.LogInformation(
                    "[CrashResilience] Synthesized ABI-safe {Strategy} detour for {Module}+{Rva} (Worker Port {Port})",
                    detour.Strategy, module, rva, worker.Port);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[CrashResilience] Could not synthesize detour for {Module}+{Rva}", module, rva);
            }
        }

        // Milestone 4.5: Inspect shared memory for latest compact state keyframe
        bool hasKeyframe = ServerKeyframeSharedMemory.TryReadKeyframe(worker.Port, out KongorServerKeyframe? keyframe, logger: _logger);
        if (hasKeyframe && keyframe != null)
        {
            _logger?.LogInformation(
                "[CrashResilience] Discovered valid match keyframe for Port {Port} at Tick {Tick} (MatchId: {MatchId}, Payload: {Bytes} bytes)",
                worker.Port, keyframe.ServerTick, keyframe.MatchId, keyframe.StatePayload.Length);
        }

        string keyframeDetail = keyframe != null ? $" [Keyframe Restorable @ Tick {keyframe.ServerTick}, Match {keyframe.MatchId}]" : string.Empty;
        string reason = throttleExceeded
            ? $"Throttling: Worker on port {worker.Port} has crashed {worker.ConsecutiveCrashCount} times in <30s. Applying standard backoff.{keyframeDetail}"
            : $"Crash detected ({exceptionName}). Fast reboot path active (<{_options.FastRebootTargetMs}ms target).{keyframeDetail}";

        return new CrashRecoveryDecision
        {
            IsCrash = true,
            CanFastReboot = canFastReboot,
            ExitCode = exitCode,
            ExceptionName = exceptionName,
            FaultingModule = module,
            FaultingRva = rva,
            SynthesizedDetour = detour,
            RecoveredKeyframe = keyframe,
            DiagnosticReason = reason
        };
    }

    /// <summary>
    /// Scans recent log lines in reverse chronological order for faulting callsites.
    /// </summary>
    public static (string? module, string? rva, string? exceptionCode) ExtractFaultingCallsite(
        IReadOnlyList<DedicatedServerLogLine> logs)
    {
        string? foundModule = null;
        string? foundRva = null;
        string? foundException = null;

        for (int i = logs.Count - 1; i >= 0; i--)
        {
            string line = logs[i].Text;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (foundException == null)
            {
                var excMatch = ExceptionCodeRegex.Match(line);
                if (excMatch.Success)
                {
                    foundException = excMatch.Groups[1].Value;
                }
            }

            if (foundModule == null)
            {
                var match = CallsiteRegex.Match(line);
                if (match.Success)
                {
                    foundModule = match.Groups[1].Value;
                    string off = match.Groups[3].Value;
                    foundRva = off.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? off : $"0x{off}";
                }
            }

            if (foundModule != null && foundException != null)
            {
                break;
            }
        }

        return (foundModule, foundRva, foundException);
    }

    /// <summary>
    /// Checks whether an exit code represents a known Windows exception, Linux POSIX signal, or fatal crash.
    /// </summary>
    public static bool IsCrashExitCode(int exitCode)
    {
        return exitCode switch
        {
            -1073741819 or unchecked((int)0xC0000005) => true, // Access Violation
            -1073741676 or unchecked((int)0xC0000094) => true, // Integer Division by Zero
            -1073741571 or unchecked((int)0xC00000FD) => true, // Stack Overflow
            -1073741515 or unchecked((int)0xC0000135) => true, // Missing DLL
            -536870911 or unchecked((int)0xE0000001) => true,  // Fatal Engine Exception / SEH
            132 => true, // SIGILL (Illegal Instruction)
            134 => true, // SIGABRT (Abort / Assertion Failure)
            135 => true, // SIGBUS (Bus Error / Unaligned Access)
            136 => true, // SIGFPE (Floating Point Exception)
            137 => true, // SIGKILL (Linux OOM Killer)
            139 => true, // SIGSEGV (Segmentation Fault)
            143 => true, // SIGTERM (Termination Signal)
            _ => false
        };
    }

    /// <summary>
    /// Classifies an exit code into a human-readable exception name.
    /// </summary>
    public static string ClassifyExitCode(int exitCode)
    {
        return exitCode switch
        {
            0 => "Success",
            -1073741819 or unchecked((int)0xC0000005) => "STATUS_ACCESS_VIOLATION",
            -1073741676 or unchecked((int)0xC0000094) => "STATUS_INTEGER_DIVIDE_BY_ZERO",
            -1073741571 or unchecked((int)0xC00000FD) => "STATUS_STACK_OVERFLOW",
            -1073741515 or unchecked((int)0xC0000135) => "STATUS_DLL_NOT_FOUND",
            -1073741510 or unchecked((int)0xC000013A) => "STATUS_CONTROL_C_EXIT",
            -536870911 or unchecked((int)0xE0000001) => "STATUS_FATAL_ENGINE_EXCEPTION",
            132 => "SIGILL",
            134 => "SIGABRT",
            135 => "SIGBUS",
            136 => "SIGFPE",
            137 => "SIGKILL_OOM",
            139 => "SIGSEGV",
            143 => "SIGTERM",
            _ => $"EXIT_{FormatExitCodeHex(exitCode)}"
        };
    }

    private static string FormatExitCodeHex(int exitCode)
    {
        return $"0x{unchecked((uint)exitCode):X8}";
    }
}

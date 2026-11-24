// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DiagnosticBundleService.cs
// System: WILLOWMAKER - Diagnostics & Privacy Firewall
// Subsystem: Stream-Bounded 1-Click Discord Diagnostic .zip Packager (<= 8.0 MB)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Core.Diagnostics;
using WILLOWMAKER.Core.Services.Serialization;

namespace WILLOWMAKER.Core.Services.Diagnostics;

public sealed record DiagnosticBundleResult(
    bool Success,
    string ZipPath,
    long ZipSizeBytes,
    string MarkdownSnippet,
    RedactionAuditCounts RedactionCounts,
    string StatusMessage);

public sealed class DiagnosticManifestDto
{
    public string TimestampUtc { get; set; } = string.Empty;
    public string ReportType { get; set; } = "CrashReport";
    public string OperatingSystem { get; set; } = string.Empty;
    public string BuildId { get; set; } = string.Empty;
    public string FaultingTarget { get; set; } = string.Empty;
    public string ExceptionCode { get; set; } = string.Empty;
    public string ExceptionName { get; set; } = string.Empty;
    public string FaultType { get; set; } = string.Empty;
    public List<string> ActiveMods { get; set; } = new();
    public Dictionary<string, int> RedactionSummary { get; set; } = new();
    public List<string> PackagedEntries { get; set; } = new();
    public List<string> OmittedEntries { get; set; } = new();
    public bool HasLocalFullDump { get; set; }
}

public static class DiagnosticBundleService
{
    public const long HardZipSizeCapBytes = 8_000_000; // <= 8.0 MB strict Discord non-Nitro ceiling
    public const long DefaultCrashRetentionCapBytes = 1024L * 1024 * 1024; // 1.0 GB standalone local crash retention cap
    public const int DefaultMaxRetainedReportZips = 25; // Standalone retention cap for exported Kongor_Diag_*.zip bundles
    private const int MaxLogScanWindowBytes = 16 * 1024 * 1024; // 16 MB max scan bound for Err/Warn lines

    [DllImport("DbgHelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        SafeHandle hFile,
        uint dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    /// <summary>
    /// Resolves the canonical output directory for shareable Zero-PII diagnostic .zip bundles.
    /// Prefers project/game-local <c>&lt;clientRoot&gt;/logs/crash_reports</c> (separate from raw dumps in <c>logs/crash</c>)
    /// whenever writable and not inside a signed macOS .app bundle or Gatekeeper App Translocation mount,
    /// falling back to <see cref="GetOsFallbackReportDirectory"/> on read-only installs (e.g. Program Files).
    /// </summary>
    public static string GetReportOutputDirectory(string? clientRoot = null)
    {
        string? effectiveRoot = !string.IsNullOrWhiteSpace(clientRoot)
            ? clientRoot
            : LocationGuard.ResolveGameClientDirectory();

        if (!string.IsNullOrWhiteSpace(effectiveRoot) && !IsReadOnlyAppBundleOrTranslocatedPath(effectiveRoot))
        {
            string projectLocalDir = Path.Combine(effectiveRoot, "logs", "crash_reports");
            if (TryEnsureWritableDirectory(projectLocalDir))
            {
                return projectLocalDir;
            }

            System.Diagnostics.Trace.WriteLine(
                $"[DIAGNOSTICS] Project-local crash_reports directory '{projectLocalDir}' is not writable; falling back to OS directory.");
        }

        return GetOsFallbackReportDirectory(effectiveRoot);
    }

    /// <summary>
    /// Resolves the OS-level per-user fallback directory (<c>%LOCALAPPDATA%\Kongor\crash_reports</c>,
    /// <c>~/Library/Logs/Kongor/crash_reports</c>, or <c>$XDG_STATE_HOME/kongor/crash_reports</c>)
    /// when the game directory is read-only, inside a macOS .app bundle, or unavailable.
    /// </summary>
    public static string GetOsFallbackReportDirectory(string? clientRoot = null)
    {
        string? candidateDir = null;

        if (OperatingSystem.IsWindows())
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localApp))
            {
                candidateDir = Path.Combine(localApp, "Kongor", "crash_reports");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                candidateDir = Path.Combine(home, "Library", "Logs", "Kongor", "crash_reports");
            }
        }
        else // Linux & others
        {
            string? xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (!string.IsNullOrWhiteSpace(xdgState))
            {
                candidateDir = Path.Combine(xdgState, "kongor", "crash_reports");
            }
            else
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(home))
                {
                    candidateDir = Path.Combine(home, ".local", "share", "kongor", "crash_reports");
                }
            }
        }

        if (TryEnsureWritableDirectory(candidateDir))
        {
            return candidateDir!;
        }

        if (!string.IsNullOrWhiteSpace(clientRoot) && !IsReadOnlyAppBundleOrTranslocatedPath(clientRoot))
        {
            string userdataFallback = Path.Combine(clientRoot, "userdata", "crash_reports");
            if (TryEnsureWritableDirectory(userdataFallback))
            {
                return userdataFallback;
            }
        }

        string tempFallback = Path.Combine(Path.GetTempPath(), "Kongor", "crash_reports");
        if (TryEnsureWritableDirectory(tempFallback))
        {
            return tempFallback;
        }

        return Path.GetTempPath();
    }

    internal static bool IsReadOnlyAppBundleOrTranslocatedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalized = path.Replace('\\', '/');
        return normalized.Contains(".app/Contents", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/AppTranslocation/", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryEnsureWritableDirectory(string? dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(dirPath);
            string probePath = Path.Combine(dirPath, $".kongor_write_probe_{Guid.NewGuid():N}.tmp");
            using FileStream probeStream = new(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            probeStream.WriteByte(0x4B);
            probeStream.Flush();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Writability probe failed for '{dirPath}': {ex.Message}");
            return false;
        }
    }

    private static readonly string[] KnownThirdPartyGraphicsHooks =
    [
        "MirillisActionVulkanLayer.dll",
        "MirillisAction64.dll",
        "graphics-hook64.dll",
        "RTSSVkLayer64.dll",
        "RTSSHooks64.dll",
        "OWExplorer.dll",
        "ow-graphics-vulkan.dll",
        "DiscordHook64.dll",
        "medal-hook64.dll",
        "bdcam64.dll"
    ];

    /// <summary>
    /// Assembles, scrubs, bounds, and exports a shareable diagnostic .zip in one shot.
    /// </summary>
    public static async Task<DiagnosticBundleResult> ExportBundleAsync(
        string? clientRoot = null,
        bool isCrashTriggered = false,
        MinidumpTriageResult? triageResult = null,
        string? activeModString = null,
        string? masterServerHost = null,
        string? cdnHost = null,
        TimeProvider? timeProvider = null,
        string? outputDirectoryOverride = null,
        bool revealInFileManager = true,
        string? preferredDumpPath = null,
        DateTimeOffset? minDumpWriteTimeUtc = null,
        uint? processExitCode = null,
        string? capturedFatalStderr = null,
        string? activeLauncherLogPath = null,
        IReadOnlyList<string>? knownDiscreteGpusOverride = null,
        int? expectedProcessId = null,
        string? silentExitZeroReason = null,
        CancellationToken cancellationToken = default)
    {
        TimeProvider time = timeProvider ?? TimeProvider.System;
        return await Task.Run(() =>
        {
            string root = clientRoot ?? LocationGuard.ResolveGameClientDirectory() ?? Environment.CurrentDirectory;
            string outDir = !string.IsNullOrWhiteSpace(outputDirectoryOverride)
                ? outputDirectoryOverride
                : GetReportOutputDirectory(root);
            Directory.CreateDirectory(outDir);

            string? resolvedCrashDumpPath = (!string.IsNullOrWhiteSpace(preferredDumpPath) && File.Exists(preferredDumpPath))
                ? preferredDumpPath
                : null;
            if (isCrashTriggered && triageResult == null)
            {
                resolvedCrashDumpPath ??= LocateLatestCrashDumpPath(
                    root,
                    minDumpWriteTimeUtc,
                    includeWerFallback: minDumpWriteTimeUtc.HasValue);
                if (!string.IsNullOrEmpty(resolvedCrashDumpPath))
                {
                    triageResult = MinidumpReader.Inspect(resolvedCrashDumpPath);
                }
            }

            string? linuxSignalCrashPath = LocateLatestLinuxSignalCrashLog(root, minDumpWriteTimeUtc);
            string? resolvedConsoleLogPath = LocateActiveConsoleLog(root, activeModString);
            string dxvkLogPath = Path.Combine(root, "hon_x64_d3d9.log");
            string vidSpyLogPath = File.Exists(Path.Combine(root, "logs", "vid_vulkan_spy.log"))
                ? Path.Combine(root, "logs", "vid_vulkan_spy.log")
                : Path.Combine(root, "vid_vulkan_spy.log");

            string? syntheticRemediationNote = null;
            string? overrideFaultSlug = null;

            // 1. Auto-triage POSIX Exit 0x7F / ld.so missing shared library
            string? missingPosixLib = TryExtractMissingPosixSharedLibrary(processExitCode, capturedFatalStderr);
            if (!string.IsNullOrWhiteSpace(missingPosixLib))
            {
                isCrashTriggered = true;
                overrideFaultSlug = $"missing_{SanitizeFilename(missingPosixLib)}_0x7F";
                string distroHint = PlaytestPolicyService.GetLinuxPackageInstallHint(missingPosixLib);
                syntheticRemediationNote = $"POSIX dynamic linker exited with 0x7F (127): missing shared library `{missingPosixLib}`. Fix: `{distroHint}`";
                if (triageResult == null || !triageResult.IsValidDump)
                {
                    triageResult = new MinidumpTriageResult(
                        IsValidDump: true,
                        Status: "SYNTHETIC_POSIX_LINKER_TRIAGE",
                        Architecture: RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                        ExceptionCodeHex: "0x0000007F",
                        ExceptionName: "POSIX_DYNAMIC_LINKER_MISSING_LIB",
                        FaultingAddress: 0UL,
                        FaultingModule: "ld-linux-x86-64.so.2",
                        FaultingRva: "0x0",
                        TargetFormatted: $"hon-x86_64 (missing {missingPosixLib})",
                        FaultType: $"POSIX Dynamic Linker Missing Shared Library ({missingPosixLib})",
                        AccessViolationKind: null,
                        AccessViolationTargetHex: null,
                        CppExceptionTypeName: null,
                        CppExceptionWhatMessage: null,
                        Registers: new Dictionary<string, string>(),
                        LoadedModules: [],
                        StreamCount: 0U);
                }
            }

            // 2. Auto-triage Linux crash_*.log signal dumps when no Win32 MDMP exists
            if ((triageResult == null || !triageResult.IsValidDump) && !string.IsNullOrWhiteSpace(linuxSignalCrashPath) && File.Exists(linuxSignalCrashPath))
            {
                MinidumpTriageResult? parsedLinuxCrash = TryParseLinuxSignalCrashLog(linuxSignalCrashPath);
                if (parsedLinuxCrash != null)
                {
                    isCrashTriggered = true;
                    triageResult = parsedLinuxCrash;
                }
            }

            // 3. Auto-triage DXVK VK_ERROR_DEVICE_LOST and genuine "DXVK: No adapters found" / Vulkan 1.3 rejection
            bool hasRealExceptionMinidump = triageResult is { IsValidDump: true } &&
                !string.Equals(triageResult.Status, "LIVE_SNAPSHOT_NO_EXCEPTION", StringComparison.OrdinalIgnoreCase) &&
                !triageResult.Status.StartsWith("SYNTHETIC_", StringComparison.OrdinalIgnoreCase);

            string dxvkSnippet = File.Exists(dxvkLogPath)
                ? ReadBoundedLogText(dxvkLogPath, isUtf16Possible: false, maxTailLines: 500, maxTailBytes: 256 * 1024)
                : string.Empty;
            string spySnippet = File.Exists(vidSpyLogPath)
                ? ReadBoundedLogText(vidSpyLogPath, isUtf16Possible: false, maxTailLines: 500, maxTailBytes: 256 * 1024)
                : string.Empty;

            bool dxvkRelevantForTriage = IsLogRelevantForCrashTriage(dxvkLogPath, resolvedCrashDumpPath, minDumpWriteTimeUtc);
            bool spyRelevantForTriage = IsLogRelevantForCrashTriage(vidSpyLogPath, resolvedCrashDumpPath, minDumpWriteTimeUtc);

            bool hasDeviceLost = (dxvkRelevantForTriage && dxvkSnippet.Contains("VK_ERROR_DEVICE_LOST", StringComparison.OrdinalIgnoreCase)) ||
                                 (spyRelevantForTriage && spySnippet.Contains("VK_ERROR_DEVICE_LOST", StringComparison.OrdinalIgnoreCase));

            // Note: Windows 11 enumerates secondary Microsoft Direct3D12 / Basic Render Driver software Vulkan 1.2
            // adapters alongside discrete RTX/Radeon GPUs, causing DXVK to log "Skipping: Device does not support Vulkan 1.3"
            // right before "Creating device:" succeeds on the real GPU. Only treat as a failure if DXVK never created a device!
            bool isGenuineDxvkNoAdapterFailure = dxvkRelevantForTriage &&
                (dxvkSnippet.Contains("DXVK: No adapters found", StringComparison.OrdinalIgnoreCase) ||
                 (dxvkSnippet.Contains("Device does not support Vulkan 1.3", StringComparison.OrdinalIgnoreCase) &&
                  !dxvkSnippet.Contains("Creating device:", StringComparison.OrdinalIgnoreCase)));

            if (hasDeviceLost)
            {
                if (!hasRealExceptionMinidump)
                {
                    overrideFaultSlug ??= "dxvk_vk_error_device_lost";
                }
                syntheticRemediationNote ??=
                    "DXVK / Vulkan encountered `VK_ERROR_DEVICE_LOST` during swapchain/surface transition (common with third-party Vulkan overlay hooks like Mirillis Action!, Overwolf, RTSS, or aggressive NVIDIA Reflex driver states). Keep `Allow Third-Party Vulkan Overlays` OFF and `NVIDIA Reflex` OFF in WILLOWMAKER.";
                if (triageResult == null || !triageResult.IsValidDump)
                {
                    triageResult = new MinidumpTriageResult(
                        IsValidDump: true,
                        Status: "SYNTHETIC_DXVK_DEVICE_LOST_TRIAGE",
                        Architecture: "x64",
                        ExceptionCodeHex: "VK_ERROR_DEVICE_LOST",
                        ExceptionName: "VK_ERROR_DEVICE_LOST",
                        FaultingAddress: 0UL,
                        FaultingModule: "d3d9.dll",
                        FaultingRva: "0x0",
                        TargetFormatted: "d3d9.dll (DXVK v3.0.2 Swapchain Present/Reset)",
                        FaultType: "Vulkan Device Lost Hang (VK_ERROR_DEVICE_LOST during Swapchain/Surface Transition)",
                        AccessViolationKind: null,
                        AccessViolationTargetHex: null,
                        CppExceptionTypeName: null,
                        CppExceptionWhatMessage: null,
                        Registers: new Dictionary<string, string>(),
                        LoadedModules: [],
                        StreamCount: 0U);
                }
            }
            else if (!hasRealExceptionMinidump && isGenuineDxvkNoAdapterFailure)
            {
                overrideFaultSlug ??= "dxvk_no_vulkan13_adapter";
                IReadOnlyList<string> winGpus = knownDiscreteGpusOverride ?? PlaytestPolicyService.EnumerateWindowsDisplayAdapterNames();
                List<string> dxvkEnumeratedNames = ExtractDxvkEnumeratedAdapterNames(dxvkSnippet);
                string? hiddenDiscrete = PlaytestPolicyService.FindDiscreteGpuMissingFromVulkan(winGpus, dxvkEnumeratedNames);

                string classification = !string.IsNullOrWhiteSpace(hiddenDiscrete)
                    ? $"Hybrid Laptop Discrete GPU Pending Windows Restart / Optimus iGPU-Only ({hiddenDiscrete})"
                    : "Outdated Pre-Vulkan-1.3 Graphics Driver or Legacy GPU";
                syntheticRemediationNote ??= !string.IsNullOrWhiteSpace(hiddenDiscrete)
                    ? $"DXVK rejected adapters (`DXVK: No adapters found`), while Windows Display Adapters registers discrete GPU `{hiddenDiscrete}` absent from Vulkan enumeration. Reboot Windows to finalize pending GPU driver install, bind `hon_x64.exe` to High-Performance GPU in Windows Graphics Settings, or select Compatibility Native D3D9 in WILLOWMAKER."
                    : "DXVK v3.0.2 requires a Vulkan 1.3 driver (`Device does not support Vulkan 1.3`). Update your NVIDIA/AMD/Intel GPU driver or select Compatibility Native D3D9 in WILLOWMAKER.";

                if (triageResult == null || !triageResult.IsValidDump)
                {
                    triageResult = new MinidumpTriageResult(
                        IsValidDump: true,
                        Status: "SYNTHETIC_DXVK_NO_ADAPTER_TRIAGE",
                        Architecture: "x64",
                        ExceptionCodeHex: "DXVK_NO_VULKAN_1_3_ADAPTER",
                        ExceptionName: "DXVK_NO_ADAPTERS_FOUND",
                        FaultingAddress: 0UL,
                        FaultingModule: "d3d9.dll",
                        FaultingRva: "0x0",
                        TargetFormatted: $"d3d9.dll ({classification})",
                        FaultType: classification,
                        AccessViolationKind: null,
                        AccessViolationTargetHex: null,
                        CppExceptionTypeName: null,
                        CppExceptionWhatMessage: null,
                        Registers: new Dictionary<string, string>(),
                        LoadedModules: [],
                        StreamCount: 0U);
                }
            }
            else if (isCrashTriggered &&
                     !hasRealExceptionMinidump &&
                     IsShutdownHangOrIncompleteQuit(root, resolvedCrashDumpPath, processExitCode, expectedProcessId, out string? shutdownSentinelDetail))
            {
                // 3b. Auto-triage Shutdown Hang / Post-Quit Deadlock (out-of-process hang_*.dmp, ShutdownHangTimeout, or incomplete quit-initiated)
                overrideFaultSlug ??= "shutdown_hang";
                string sentinelTail = TryReadNativeGuardSentinelTail(root, maxLines: 10);
                string detailSuffix = !string.IsNullOrWhiteSpace(shutdownSentinelDetail)
                    ? $" Sentinel details: {shutdownSentinelDetail}."
                    : string.Empty;
                string tailSuffix = !string.IsNullOrWhiteSpace(sentinelTail)
                    ? $"\n\nLast NativeGuard sentinel lines:\n```text\n{sentinelTail}\n```"
                    : string.Empty;

                syntheticRemediationNote ??=
                    $"Client hung/deadlocked or crashed during shutdown after quit was initiated and did not complete clean exit within the shutdown watchdog window.{detailSuffix} Out-of-process thread snapshot captured when available.{tailSuffix}";

                triageResult = new MinidumpTriageResult(
                    IsValidDump: true,
                    Status: "SYNTHETIC_SHUTDOWN_HANG_TRIAGE",
                    Architecture: triageResult?.Architecture ?? "x64",
                    ExceptionCodeHex: processExitCode is not null and not 0U ? $"0x{processExitCode.Value:X8}" : "0xCFFFFFFF",
                    ExceptionName: "SHUTDOWN_HANG_TIMEOUT",
                    FaultingAddress: 0UL,
                    FaultingModule: "shutdown_hang",
                    FaultingRva: "0x0",
                    TargetFormatted: "hon_x64.exe (Shutdown Hang / Deadlock)",
                    FaultType: "Client Shutdown Hang / Deadlock (0xCFFFFFFF)",
                    AccessViolationKind: null,
                    AccessViolationTargetHex: null,
                    CppExceptionTypeName: null,
                    CppExceptionWhatMessage: null,
                    Registers: triageResult?.Registers ?? new Dictionary<string, string>(),
                    LoadedModules: triageResult?.LoadedModules ?? [],
                    StreamCount: triageResult?.StreamCount ?? 0U);
            }
            else if (isCrashTriggered && !hasRealExceptionMinidump && processExitCode is not 0U)
            {
                // 4. Auto-triage NativeGuard [EXIT_TELEMETRY] forced ExitProcess(non-zero) & abrupt non-zero/orphan exits (BUG-501-06 / Issue 7B-7C)
                // Only valid when processExitCode is unknown (null) or non-zero; ExitCode 0 never triggers non-zero ExitProcess telemetry.
                bool requireTimestampedEntry = expectedProcessId.HasValue || (!processExitCode.HasValue && minDumpWriteTimeUtc.HasValue);
                if (TryExtractLatestForcedExitTelemetry(
                        root,
                        resolvedCrashDumpPath,
                        minDumpWriteTimeUtc,
                        expectedProcessId,
                        requireTimestampedEntry,
                        out string exitTelemetryHex,
                        out string exitCallerModule,
                        out string exitCallerRva,
                        out string exitStackSummary))
                {
                    overrideFaultSlug ??= $"forced_exit_{SanitizeFilename(exitCallerModule)}_{SanitizeFilename(exitCallerRva)}";
                    string stackSuffix = string.IsNullOrWhiteSpace(exitStackSummary)
                        ? string.Empty
                        : $" (Stack: `{exitStackSummary}`)";
                    string callerHint = BuildCallerSpecificTriageHint(exitCallerModule, exitCallerRva);
                    syntheticRemediationNote ??=
                        $"Process terminated via forced non-zero `ExitProcess({exitTelemetryHex})` without an unhandled SEH minidump. Primary caller: `{exitCallerModule}+{exitCallerRva}`{stackSuffix}.{callerHint}";

                    if (triageResult == null || !triageResult.IsValidDump)
                    {
                        triageResult = new MinidumpTriageResult(
                            IsValidDump: true,
                            Status: "SYNTHETIC_FORCED_EXIT_TRIAGE",
                            Architecture: "x64",
                            ExceptionCodeHex: exitTelemetryHex,
                            ExceptionName: "FORCED_EXIT_PROCESS",
                            FaultingAddress: 0UL,
                            FaultingModule: exitCallerModule,
                            FaultingRva: exitCallerRva,
                            TargetFormatted: $"{exitCallerModule} + {exitCallerRva} (ExitProcess {exitTelemetryHex})",
                            FaultType: $"Forced Non-Zero ExitProcess ({exitTelemetryHex})",
                            AccessViolationKind: null,
                            AccessViolationTargetHex: null,
                            CppExceptionTypeName: null,
                            CppExceptionWhatMessage: null,
                            Registers: new Dictionary<string, string>(),
                            LoadedModules: [],
                            StreamCount: 0U);
                    }
                }
                else if ((triageResult == null || !triageResult.IsValidDump) && processExitCode is not null and not 0U)
                {
                    string exitHexFallback = $"0x{processExitCode.Value:X8}";
                    (string decodedName, string decodedDescription) = DescribeWindowsNtStatusExitCode(processExitCode.Value);
                    string sentinelTail = TryReadNativeGuardSentinelTail(root, maxLines: 10);
                    string tailSuffix = !string.IsNullOrWhiteSpace(sentinelTail)
                        ? $"\n\nLast NativeGuard sentinel lines:\n```text\n{sentinelTail}\n```"
                        : string.Empty;

                    overrideFaultSlug ??= $"forced_exit_{exitHexFallback}";
                    syntheticRemediationNote ??=
                        $"Process terminated abruptly with non-zero exit code `{exitHexFallback}` ({decodedDescription}) and no unhandled SEH minidump (e.g. kernel `__fastfail`, heap corruption, external `TerminateProcess`, or fatal driver abort).{tailSuffix}";
                    triageResult = new MinidumpTriageResult(
                        IsValidDump: true,
                        Status: "SYNTHETIC_FORCED_EXIT_TRIAGE",
                        Architecture: "x64",
                        ExceptionCodeHex: exitHexFallback,
                        ExceptionName: decodedName,
                        FaultingAddress: 0UL,
                        FaultingModule: "hon_x64.exe",
                        FaultingRva: "0x0",
                        TargetFormatted: $"hon_x64.exe ({decodedName} {exitHexFallback})",
                        FaultType: decodedDescription,
                        AccessViolationKind: null,
                        AccessViolationTargetHex: null,
                        CppExceptionTypeName: null,
                        CppExceptionWhatMessage: null,
                        Registers: new Dictionary<string, string>(),
                        LoadedModules: [],
                        StreamCount: 0U);
                }
                else if ((triageResult == null || !triageResult.IsValidDump) && processExitCode is null)
                {
                    overrideFaultSlug ??= "ungraceful_exit_no_dump";
                    string pidNote = expectedProcessId.HasValue ? $" (PID {expectedProcessId.Value})" : string.Empty;
                    syntheticRemediationNote ??=
                        $"Previous client session{pidNote} exited ungracefully without producing a minidump or forced-exit telemetry (e.g., external process termination, OOM, or fatal driver abort). Inspect `logs/console.tail.log`, `logs/hon_x64_d3d9.log`, and `logs/vid_vulkan_spy.log`.";
                    triageResult = new MinidumpTriageResult(
                        IsValidDump: true,
                        Status: "SYNTHETIC_UNGRACEFUL_EXIT_NO_DUMP",
                        Architecture: RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                        ExceptionCodeHex: "UNGRACEFUL_EXIT",
                        ExceptionName: "UNGRACEFUL_EXIT_NO_DUMP",
                        FaultingAddress: 0UL,
                        FaultingModule: "hon_x64.exe",
                        FaultingRva: "0x0",
                        TargetFormatted: $"hon_x64.exe (Ungraceful Exit{pidNote}, No Minidump)",
                        FaultType: $"Ungraceful Session Exit Without Minidump{pidNote}",
                        AccessViolationKind: null,
                        AccessViolationTargetHex: null,
                        CppExceptionTypeName: null,
                        CppExceptionWhatMessage: null,
                        Registers: new Dictionary<string, string>(),
                        LoadedModules: [],
                        StreamCount: 0U);
                    System.Diagnostics.Trace.WriteLine(
                        $"[DIAGNOSTICS] Classified dump-less orphan crash without telemetry as 'ungraceful_exit_no_dump'{pidNote}.");
                }
            }

            if (triageResult is { IsValidDump: true } &&
                string.IsNullOrWhiteSpace(syntheticRemediationNote))
            {
                string dumpCallerHint = BuildCallerSpecificTriageHint(triageResult.FaultingModule, triageResult.FaultingRva);
                if (!string.IsNullOrWhiteSpace(dumpCallerHint))
                {
                    syntheticRemediationNote = dumpCallerHint.Trim();
                }
            }

            string? finalConsoleErrorLine = isCrashTriggered && (!hasRealExceptionMinidump || triageResult?.Status == "SYNTHETIC_FORCED_EXIT_TRIAGE")
                ? TryExtractFinalConsoleFatalOrErrorLine(resolvedConsoleLogPath, resolvedCrashDumpPath, minDumpWriteTimeUtc)
                : null;

            List<string> detectedThirdPartyHooks = DetectThirdPartyGraphicsHooks(triageResult, dxvkSnippet, spySnippet);

            DateTimeOffset nowUtc = time.GetUtcNow();
            DateTimeOffset utcEntryTimestamp = new(nowUtc.UtcDateTime, TimeSpan.Zero);
            string timestamp = nowUtc.ToString("yyyyMMdd_HHmmss");
            string faultSlug = !string.IsNullOrWhiteSpace(overrideFaultSlug)
                ? overrideFaultSlug
                : isCrashTriggered
                    ? $"{SanitizeFilename(triageResult?.FaultingModule ?? "crash")}_{SanitizeFilename(triageResult?.FaultingRva ?? "0x0")}"
                    : "manual_report";

            string zipPath = Path.Combine(outDir, $"Kongor_Diag_{timestamp}_{faultSlug}.zip");

            int totalSecrets = 0;
            int totalUserIds = 0;
            int totalPeerIps = 0;
            int totalCanaryLinesRedacted = 0;
            List<string> packagedEntries = new();
            List<string> omittedEntries = new();
            List<string> canarySurgicalRedactions = new();
            string reportType = isCrashTriggered ? "CrashReport" : "ManualReport";

            // Build ephemeral per-bundle Zero-PII context (128-bit CSPRNG salt discarded when ExportBundleAsync completes)
            PrivacyScrubContext privacyContext = new(
                clientRoot: root,
                outputDir: outDir,
                faultingModuleName: triageResult?.FaultingModule,
                strictZeroPiiBundleMode: true);

            string? playerNickname = TryResolvePlayerNicknameForScrubbing(root);
            if (!string.IsNullOrWhiteSpace(playerNickname))
            {
                privacyContext.AddKnownPlayerName(playerNickname);
            }
            string? resolvedWillowmakerLogPath = LocateActiveWillowmakerLog(root, outDir, activeLauncherLogPath);
            PopulateSensitiveSessionContextFromConfigs(root, privacyContext, resolvedWillowmakerLogPath, resolvedConsoleLogPath);

            string? minidumpOmittedNotice = null;
            bool hasLocalFullDump = false;
            List<string> sanitizedActiveMods = SanitizeActiveModList(activeModString);

            // Harvest and scrub components
            using MemoryStream zipMs = new();
            using (ZipArchive zip = new(zipMs, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipArchiveEntry CreateUtcEntry(ZipArchive targetZip, string entryName)
                {
                    ZipArchiveEntry created = targetZip.CreateEntry(entryName, CompressionLevel.Optimal);
                    created.LastWriteTime = utcEntryTimestamp;
                    return created;
                }

                void WriteVerifiedTextEntry(ZipArchive targetZip, string entryName, string scrubbedText, bool isRequiredMetadata = false)
                {
                    byte[] utf8Bytes = Encoding.UTF8.GetBytes(scrubbedText);
                    string? violation = DiagnosticRedactor.FindZeroPiiCanaryViolation(
                        entryName,
                        utf8Bytes,
                        privacyContext,
                        isBinaryMinidump: false);

                    if (violation != null)
                    {
                        System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] [ZERO-PII-CANARY] Caught residual token in '{entryName}': {violation}");
                        bool isLogOrCrashText =
                            entryName.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) ||
                            entryName.StartsWith("crash/", StringComparison.OrdinalIgnoreCase);

                        if (isRequiredMetadata)
                        {
                            // Re-scrub required metadata aggressively so summary/manifest/readme remain guaranteed clean
                            (scrubbedText, _) = DiagnosticRedactor.ScrubText(
                                scrubbedText,
                                masterServerHost,
                                cdnHost,
                                playerNickname: playerNickname,
                                context: privacyContext);
                            utf8Bytes = Encoding.UTF8.GetBytes(scrubbedText);
                            violation = DiagnosticRedactor.FindZeroPiiCanaryViolation(
                                entryName,
                                utf8Bytes,
                                privacyContext,
                                isBinaryMinidump: false);
                        }

                        if (violation != null && (isLogOrCrashText || isRequiredMetadata))
                        {
                            (string surgicallyRedactedText, int redactedLineCount) = ApplyZeroPiiCanarySurgicalLineFallback(
                                entryName,
                                scrubbedText,
                                privacyContext);
                            byte[] surgicalBytes = Encoding.UTF8.GetBytes(surgicallyRedactedText);
                            string? postSurgicalViolation = DiagnosticRedactor.FindZeroPiiCanaryViolation(
                                entryName,
                                surgicalBytes,
                                privacyContext,
                                isBinaryMinidump: false);

                            if (postSurgicalViolation == null)
                            {
                                utf8Bytes = surgicalBytes;
                                if (!isRequiredMetadata && redactedLineCount > 0)
                                {
                                    totalCanaryLinesRedacted += redactedLineCount;
                                    totalUserIds += redactedLineCount;
                                    canarySurgicalRedactions.Add($"{entryName} ({redactedLineCount} line(s) surgically redacted)");
                                    System.Diagnostics.Trace.WriteLine(
                                        $"[DIAGNOSTICS] [ZERO-PII-CANARY] Surgically redacted {redactedLineCount} line(s) in '{entryName}' and verified clean.");
                                }
                            }
                            else if (!isRequiredMetadata)
                            {
                                string kind = DiagnosticRedactor.ClassifyZeroPiiViolationKind(postSurgicalViolation);
                                string omittedRecord = $"{entryName} (omitted by Zero-PII Canary: {kind})";
                                omittedEntries.Add(omittedRecord);
                                System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] [ZERO-PII-CANARY] Omitted '{entryName}' after surgical fallback failed: {postSurgicalViolation}");
                                return;
                            }
                            else
                            {
                                utf8Bytes = Encoding.UTF8.GetBytes($"[LINE_REDACTED_BY_ZERO_PII_CANARY: {DiagnosticRedactor.ClassifyZeroPiiViolationKind(postSurgicalViolation)}]");
                            }
                        }
                        else if (violation != null)
                        {
                            string kind = DiagnosticRedactor.ClassifyZeroPiiViolationKind(violation);
                            string omittedRecord = $"{entryName} (omitted by Zero-PII Canary: {kind})";
                            omittedEntries.Add(omittedRecord);
                            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] [ZERO-PII-CANARY] Omitted '{entryName}': {violation}");
                            return;
                        }
                    }

                    ZipArchiveEntry entry = CreateUtcEntry(targetZip, entryName);
                    using Stream s = entry.Open();
                    s.Write(utf8Bytes, 0, utf8Bytes.Length);
                    packagedEntries.Add(entryName);
                }

                // 1. Minidump (Tier-1 or Live Freeze Snapshot) — Allowlist Structural Sanitization + Fail-Closed Canary Gate
                byte[]? minidumpBytes = null;
                string dmpEntryName = $"dumps/minidump_{timestamp}.dmp";

                if (!isCrashTriggered && OperatingSystem.IsWindows())
                {
                    minidumpBytes = TryCaptureLiveProcessDump(root, expectedProcessId);
                    if (minidumpBytes != null) reportType = "FreezeReport";
                }

                DateTimeOffset? effectiveMinDumpWriteTimeUtc = minDumpWriteTimeUtc;
                if (!isCrashTriggered && string.IsNullOrWhiteSpace(preferredDumpPath))
                {
                    DateTimeOffset manualRecencyFloor = nowUtc.AddMinutes(-15);
                    if (!effectiveMinDumpWriteTimeUtc.HasValue || manualRecencyFloor > effectiveMinDumpWriteTimeUtc.Value)
                    {
                        effectiveMinDumpWriteTimeUtc = manualRecencyFloor;
                    }
                }

                string? emptyHangDumpNotice = DetectEmptyHangDumpNotice(
                    root,
                    effectiveMinDumpWriteTimeUtc ?? nowUtc.AddMinutes(-15),
                    expectedProcessId);

                if (minidumpBytes == null)
                {
                    (byte[]? bytes, string? tier1DumpPath, string? fullDump) = LocateAndReadLatestMinidump(
                        root,
                        preferredDumpPath,
                        effectiveMinDumpWriteTimeUtc);
                    minidumpBytes = bytes;
                    hasLocalFullDump = !string.IsNullOrWhiteSpace(fullDump);
                    if (!string.IsNullOrWhiteSpace(tier1DumpPath))
                    {
                        dmpEntryName = $"dumps/{FormatSafeOnDiskDumpEntryFileName(tier1DumpPath, nowUtc.UtcDateTime)}";
                    }
                }

                if (minidumpBytes != null &&
                    minidumpBytes.Length > 0 &&
                    minidumpBytes.Length <= 4 * 1024 * 1024 &&
                    !dmpEntryName.EndsWith(".rich.dmp", StringComparison.OrdinalIgnoreCase))
                {
                    List<string> binaryVocabulary = privacyContext.BuildBinaryScrubVocabulary();
                    MinidumpSanitizeResult dmpSanitize = MinidumpPrivacySanitizer.SanitizeInPlace(
                        minidumpBytes,
                        privacyContext,
                        binaryVocabulary);

                    if (!dmpSanitize.AllowedForExport)
                    {
                        minidumpOmittedNotice =
                            $"Binary minidump omitted by privacy allowlist ({dmpSanitize.StatusCode}: {dmpSanitize.Reason}); sanitized RVA triage preserved in CRASH_SUMMARY.md.";
                        omittedEntries.Add($"{dmpEntryName} ({dmpSanitize.StatusCode})");
                        System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] [ZERO-PII] {minidumpOmittedNotice}");
                    }
                    else
                    {
                        // Fast binary Zero-PII canary scan on the packaged Tier-1 minimal .dmp bytes right before zip write
                        // (NEVER touches local .rich.dmp files).
                        string? binaryCanaryViolation = FindBinaryMinidumpZeroPiiViolation(minidumpBytes);
                        string? dmpCanaryViolation = binaryCanaryViolation ?? DiagnosticRedactor.FindZeroPiiCanaryViolation(
                            dmpEntryName,
                            minidumpBytes,
                            privacyContext,
                            isBinaryMinidump: true);

                        if (dmpCanaryViolation != null)
                        {
                            string violationKind = DiagnosticRedactor.ClassifyZeroPiiViolationKind(dmpCanaryViolation);
                            minidumpOmittedNotice =
                                $"Binary minidump omitted by Zero-PII Canary ({dmpCanaryViolation}); sanitized RVA triage preserved in CRASH_SUMMARY.md.";
                            omittedEntries.Add($"{dmpEntryName} (ZERO_PII_CANARY: {violationKind} — {dmpCanaryViolation})");
                            canarySurgicalRedactions.Add($"{dmpEntryName} (omitted by binary Zero-PII Canary: {dmpCanaryViolation})");
                            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] [ZERO-PII-CANARY] WARNING: {minidumpOmittedNotice}");
                        }
                        else
                        {
                            totalUserIds += dmpSanitize.ModulesSanitized + dmpSanitize.BackgroundStacksCleared;
                            ZipArchiveEntry dmpEntry = CreateUtcEntry(zip, dmpEntryName);
                            using Stream entryStream = dmpEntry.Open();
                            entryStream.Write(minidumpBytes, 0, minidumpBytes.Length);
                            packagedEntries.Add(dmpEntryName);
                            System.Diagnostics.Trace.WriteLine(
                                $"[DIAGNOSTICS] [ZERO-PII-CANARY] Packaged Tier-1 minidump '{dmpEntryName}' ({minidumpBytes.Length} bytes) passed binary Zero-PII canary scan.");
                        }
                    }
                }

                // 2. Harvest Logs (social/chat lines dropped, local console clocks converted to [+HH:MM:SS], paths/UUIDs scrubbed)
                HarvestConsoleDiagnosticLog(zip, "logs/console.tail.log", resolvedConsoleLogPath);
                HarvestAndAddTextLog(zip, "logs/engine_crashes.log", Path.Combine(root, "logs", "crash", "engine_crashes.log"), maxTailBytes: 512 * 1024, relativizeBracketClocks: true);
                string? localLauncherCrashLog = LocateFirstExistingFile(
                    Path.Combine(root, "logs", "crash", "willowmaker_crashes.log"),
                    Path.Combine(AppContext.BaseDirectory, "logs", "crash", "willowmaker_crashes.log"),
                    Path.Combine(outDir, "willowmaker_crashes.log"),
                    Path.Combine(GetOsFallbackReportDirectory(root), "willowmaker_crashes.log"));
                HarvestAndAddTextLog(zip, "logs/willowmaker_crashes.log", localLauncherCrashLog, maxTailBytes: 512 * 1024);
                HarvestAndAddTextLog(zip, "logs/WILLOWMAKER.tail.log", resolvedWillowmakerLogPath, maxTailBytes: 1024 * 1024, relativizeBracketClocks: true);
                HarvestAndAddTextLog(zip, "logs/hon_x64_d3d9.log", dxvkLogPath, maxTailBytes: 256 * 1024);
                HarvestAndAddTextLog(zip, "logs/vid_vulkan_spy.log", vidSpyLogPath, maxTailBytes: 256 * 1024, relativizeBracketClocks: true);
                HarvestAndAddTextLog(zip, "logs/vid_vulkan_spy.prev.log", Path.Combine(root, "logs", "vid_vulkan_spy.prev.log"), maxTailBytes: 128 * 1024, relativizeBracketClocks: true);
                HarvestAndAddTextLog(zip, "logs/hon_vulkan_layer.log", LocateFirstExistingFile(Path.Combine(root, "logs", "hon_vulkan_layer.log"), Path.Combine(root, "hon_vulkan_layer.log")), maxTailBytes: 256 * 1024, relativizeBracketClocks: true);
                HarvestAndAddTextLog(zip, "logs/hon_vulkan_layer.log.bak", LocateFirstExistingFile(Path.Combine(root, "logs", "hon_vulkan_layer.log.bak"), Path.Combine(root, "hon_vulkan_layer.log.bak")), maxTailBytes: 128 * 1024, relativizeBracketClocks: true);
                HarvestAndAddTextLog(zip, "logs/client_lua_debug.log", Path.Combine(root, "client_lua_debug.log"), maxTailBytes: 256 * 1024, dropSocialChatLines: true, relativizeBracketClocks: true);
                HarvestAndAddTextLog(zip, "crash/macos_diagnostic_report.ips", LocateLatestMacIpsReport(root), maxTailBytes: 512 * 1024);
                HarvestAndAddTextLog(zip, "crash/linux_signal_crash.log", linuxSignalCrashPath, maxTailBytes: 512 * 1024);

                if (!string.IsNullOrWhiteSpace(capturedFatalStderr))
                {
                    (string scrubbedStderr, RedactionAuditCounts stderrCounts) = DiagnosticRedactor.ScrubText(
                        capturedFatalStderr,
                        masterServerHost,
                        cdnHost,
                        playerNickname: playerNickname,
                        context: privacyContext);
                    totalSecrets += stderrCounts.SecretsRedacted;
                    totalUserIds += stderrCounts.UserIdentifiersRedacted;
                    totalPeerIps += stderrCounts.PeerIpsRedacted;
                    WriteVerifiedTextEntry(zip, "logs/captured_stderr.log", scrubbedStderr);
                }

                // 3. Harvest strictly allowlisted configurations (name + value-type validation; zero binds/chat/nicknames)
                HarvestAllowlistedK2Cfg(zip, "config/startup.cfg", LocateActiveStartupCfg(root), isUtf16Possible: true);
                HarvestAllowlistedK2Cfg(
                    zip,
                    "config/autoexec.cfg",
                    LocateFirstExistingFile(
                        Path.Combine(root, "userdataKONGOR", "configuration", "autoexec.cfg"),
                        Path.Combine(root, "game", "autoexec.cfg"),
                        Path.Combine(root, "linux-x64", "game", "autoexec.cfg")),
                    isUtf16Possible: false);
                HarvestAndAddTextLog(
                    zip,
                    "config/dxvk.conf",
                    LocateFirstExistingFile(
                        Path.Combine(root, "userdataKONGOR", "dxvk.conf"),
                        Path.Combine(root, "dxvk.conf")));
                HarvestProjectedLauncherSettingsJson(
                    zip,
                    "config/launcher_settings.json",
                    LocateFirstExistingFile(
                        LauncherSettingsService.GetSettingsFilePath(Path.Combine(root, "userdataKONGOR")),
                        LauncherSettingsService.GetSettingsFilePath(root),
                        LauncherSettingsService.GetSettingsFilePath()));

                // 4. Human-readable triage report & manifest
                string buildId = TryReadBuildId(root);
                string crashSummary = GenerateCrashSummaryMarkdown(
                    triageResult,
                    reportType,
                    buildId,
                    hasLocalFullDump,
                    packagedEntries,
                    nowUtc,
                    processExitCode,
                    syntheticRemediationNote,
                    detectedThirdPartyHooks,
                    minidumpOmittedNotice,
                    finalConsoleErrorLine,
                    silentExitZeroReason,
                    emptyHangDumpNotice,
                    canarySurgicalRedactions,
                    omittedEntries);
                (string scrubbedSummary, RedactionAuditCounts summaryCounts) = DiagnosticRedactor.ScrubText(
                    crashSummary,
                    masterServerHost,
                    cdnHost,
                    playerNickname: playerNickname,
                    context: privacyContext);
                totalSecrets += summaryCounts.SecretsRedacted;
                totalUserIds += summaryCounts.UserIdentifiersRedacted;
                totalPeerIps += summaryCounts.PeerIpsRedacted;
                WriteVerifiedTextEntry(zip, "CRASH_SUMMARY.md", scrubbedSummary, isRequiredMetadata: true);

                // Diagnostic Manifest JSON
                (string scrubbedFaultType, _) = DiagnosticRedactor.ScrubText(
                    triageResult?.FaultType ?? (isCrashTriggered ? "Engine Crash" : "Manual Bug Capture"),
                    masterServerHost,
                    cdnHost,
                    playerNickname: playerNickname,
                    context: privacyContext);

                DiagnosticManifestDto manifestDto = new()
                {
                    TimestampUtc = nowUtc.ToString("O"),
                    ReportType = reportType,
                    OperatingSystem = RuntimeInformation.OSDescription,
                    BuildId = buildId,
                    FaultingTarget = triageResult?.TargetFormatted ?? (isCrashTriggered
                        ? (processExitCode.HasValue ? $"Crash (Exit 0x{processExitCode.Value:X8})" : "Crash (Process Exit)")
                        : "Live Telemetry"),
                    ExceptionCode = triageResult?.ExceptionCodeHex ?? (isCrashTriggered
                        ? (processExitCode.HasValue ? $"0x{processExitCode.Value:X8}" : "UNKNOWN")
                        : "NONE"),
                    ExceptionName = triageResult?.ExceptionName ?? (isCrashTriggered ? "PROCESS_ABNORMAL_EXIT" : "NONE"),
                    FaultType = scrubbedFaultType,
                    ActiveMods = sanitizedActiveMods,
                    RedactionSummary = new Dictionary<string, int>
                    {
                        ["SecretsRedacted"] = totalSecrets,
                        ["UserIdentifiersRedacted"] = totalUserIds,
                        ["PeerIpsRedacted"] = totalPeerIps,
                        ["ZeroPiiCanaryLinesRedacted"] = totalCanaryLinesRedacted,
                        ["OmittedEntriesCount"] = omittedEntries.Count
                    },
                    PackagedEntries = new List<string>(packagedEntries),
                    OmittedEntries = new List<string>(omittedEntries),
                    HasLocalFullDump = hasLocalFullDump
                };

                string manifestJson = JsonSerializer.Serialize(manifestDto, WillowmakerJsonSerializerContext.Default.DiagnosticManifestDto);
                (string scrubbedManifest, _) = DiagnosticRedactor.ScrubText(
                    manifestJson,
                    masterServerHost,
                    cdnHost,
                    playerNickname: playerNickname,
                    context: privacyContext);
                WriteVerifiedTextEntry(zip, "manifest.json", scrubbedManifest, isRequiredMetadata: true);

                // 5. Transparency Notice: README_WHAT_IS_SHARED.txt
                string rawReadmeText = GenerateTransparencyNotice(
                    totalSecrets,
                    totalUserIds,
                    totalPeerIps,
                    packagedEntries,
                    hasLocalFullDump,
                    minidumpOmittedNotice);
                (string readmeText, _) = DiagnosticRedactor.ScrubText(
                    rawReadmeText,
                    masterServerHost,
                    cdnHost,
                    playerNickname: playerNickname,
                    context: privacyContext);
                WriteVerifiedTextEntry(zip, "README_WHAT_IS_SHARED.txt", readmeText, isRequiredMetadata: true);

                void HarvestConsoleDiagnosticLog(ZipArchive targetZip, string entryName, string? filePath)
                {
                    if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

                    try
                    {
                        string text = ReadConsoleDiagnosticText(filePath, headerLines: 200, maxTailLines: 2500, maxScanBytes: MaxLogScanWindowBytes);
                        if (string.IsNullOrWhiteSpace(text)) return;

                        string prefiltered = FilterSocialLinesAndRelativizeClocks(text, dropSocialChatLines: true, relativizeBracketClocks: true);
                        (string scrubbed, RedactionAuditCounts counts) = DiagnosticRedactor.ScrubText(
                            prefiltered,
                            masterServerHost,
                            cdnHost,
                            playerNickname: playerNickname,
                            context: privacyContext);
                        totalSecrets += counts.SecretsRedacted;
                        totalUserIds += counts.UserIdentifiersRedacted;
                        totalPeerIps += counts.PeerIpsRedacted;

                        WriteVerifiedTextEntry(targetZip, entryName, scrubbed);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Console log harvest notice for '{entryName}': {ex.Message}");
                    }
                }

                void HarvestAndAddTextLog(
                    ZipArchive targetZip,
                    string entryName,
                    string? filePath,
                    bool isUtf16Possible = false,
                    int maxTailLines = 1500,
                    int maxTailBytes = 512 * 1024,
                    bool dropSocialChatLines = false,
                    bool relativizeBracketClocks = false)
                {
                    if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

                    try
                    {
                        string text = ReadBoundedLogText(filePath, isUtf16Possible, maxTailLines, maxTailBytes);
                        if (string.IsNullOrWhiteSpace(text)) return;

                        if (dropSocialChatLines || relativizeBracketClocks)
                        {
                            text = FilterSocialLinesAndRelativizeClocks(text, dropSocialChatLines, relativizeBracketClocks);
                        }

                        (string scrubbed, RedactionAuditCounts counts) = DiagnosticRedactor.ScrubText(
                            text,
                            masterServerHost,
                            cdnHost,
                            playerNickname: playerNickname,
                            context: privacyContext);
                        totalSecrets += counts.SecretsRedacted;
                        totalUserIds += counts.UserIdentifiersRedacted;
                        totalPeerIps += counts.PeerIpsRedacted;

                        WriteVerifiedTextEntry(targetZip, entryName, scrubbed);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Optional log entry '{entryName}' omitted: {ex.Message}");
                    }
                }

                void HarvestAllowlistedK2Cfg(ZipArchive targetZip, string entryName, string? filePath, bool isUtf16Possible)
                {
                    if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

                    try
                    {
                        string rawCfg = ReadBoundedLogText(filePath, isUtf16Possible, maxTailLines: 4000, maxTailBytes: 512 * 1024);
                        if (string.IsNullOrWhiteSpace(rawCfg)) return;

                        (string projected, int droppedCount) = ProjectSafeK2ConfigForDiagnostics(rawCfg, privacyContext);
                        totalUserIds += droppedCount;
                        if (string.IsNullOrWhiteSpace(projected)) return;

                        WriteVerifiedTextEntry(targetZip, entryName, projected);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Config allowlist projection notice for '{entryName}': {ex.Message}");
                    }
                }

                void HarvestProjectedLauncherSettingsJson(ZipArchive targetZip, string entryName, string? filePath)
                {
                    if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

                    try
                    {
                        string rawJson = File.ReadAllText(filePath);
                        string? projectedJson = ProjectSafeLauncherSettingsJson(rawJson, ref totalUserIds);
                        if (string.IsNullOrWhiteSpace(projectedJson)) return;

                        WriteVerifiedTextEntry(targetZip, entryName, projectedJson);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Launcher settings projection notice for '{entryName}': {ex.Message}");
                    }
                }
            }

            // Write to disk & verify <= 8.0 MB cap (preserving UTC zip entry timestamps)
            byte[] zipData = zipMs.ToArray();
            if (zipData.Length > HardZipSizeCapBytes)
            {
                // Fallback: trim to strictly stay under 8 MB
                zipData = TrimZipToCap(zipData, HardZipSizeCapBytes, utcEntryTimestamp);
            }

            File.WriteAllBytes(zipPath, zipData);
            _ = EnforceExportedReportZipRetention(outDir, root);

            // Format Discord Markdown Snippet (strictly allowlisted mods + basename-only module + Zero-PII redaction pass)
            string safeFaultModule = Path.GetFileName(triageResult?.FaultingModule ?? "unknown");
            string targetText = isCrashTriggered
                ? $"{safeFaultModule}+{triageResult?.FaultingRva ?? "0x0"} ({triageResult?.ExceptionCodeHex ?? "0xC0000005"})"
                : "Manual Diagnostics / Telemetry Snapshot";
            string safeActiveModSummary = sanitizedActiveMods.Count > 0
                ? string.Join(';', sanitizedActiveMods)
                : "default";

            string rawDiscordSnippet = new StringBuilder()
                .AppendLine($"**Project KONGOR Bug Report** ({nowUtc:yyyy-MM-dd HH:mm} UTC)")
                .AppendLine($"• **Type:** `{reportType}` | **Target:** `{targetText}`")
                .AppendLine($"• **OS:** `{RuntimeInformation.OSDescription}` ({RuntimeInformation.ProcessArchitecture})")
                .AppendLine($"• **Active Mods:** `{safeActiveModSummary}`")
                .AppendLine($"• **Bundle:** `{Path.GetFileName(zipPath)}` ({zipData.Length / 1024} KB)")
                .AppendLine($"• **Privacy:** Scrubbed {totalSecrets + totalUserIds + totalPeerIps} sensitive tokens (passwords, usernames, IPs)")
                .ToString();

            (string discordSnippet, _) = DiagnosticRedactor.ScrubText(
                rawDiscordSnippet,
                masterServerHost,
                cdnHost,
                playerNickname: playerNickname,
                context: privacyContext);

            // Reveal in OS File Manager (suppressed during headless automated tests)
            if (revealInFileManager)
            {
                RevealFileInExplorer(zipPath);
            }

            return new DiagnosticBundleResult(
                Success: true,
                ZipPath: zipPath,
                ZipSizeBytes: zipData.Length,
                MarkdownSnippet: discordSnippet,
                RedactionCounts: new RedactionAuditCounts(totalSecrets, totalUserIds, totalPeerIps),
                StatusMessage: $"Diagnostic bundle created ({zipData.Length / 1024} KB). Ready to drag into Discord!");
        }, cancellationToken);
    }

    /// <summary>
    /// Reveals and selects the created .zip in Windows Explorer, macOS Finder, or Linux file manager.
    /// </summary>
    public static void RevealFileInExplorer(string fullPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{fullPath}\"",
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", $"-R \"{fullPath}\"");
            }
            else if (OperatingSystem.IsLinux())
            {
                string? parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Process.Start("xdg-open", $"\"{parent}\"");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Diagnostic export notice: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the tail of a log file safely, handling UTF-16LE BOM and odd-byte seek alignment.
    /// </summary>
    public static string ReadBoundedLogText(string filePath, bool isUtf16Possible, int maxTailLines, int maxTailBytes)
    {
        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length == 0) return string.Empty;

        // Detect UTF-16LE BOM (FF FE)
        bool isUtf16Le = false;
        if (isUtf16Possible && fs.Length >= 2)
        {
            byte[] bom = new byte[2];
            int readBom = fs.Read(bom, 0, 2);
            isUtf16Le = readBom == 2 && bom[0] == 0xFF && bom[1] == 0xFE;
        }

        Encoding encoding = isUtf16Le ? Encoding.Unicode : Encoding.UTF8;

        if (fs.Length <= maxTailBytes)
        {
            fs.Seek(0, SeekOrigin.Begin);
            using StreamReader sr = new(fs, encoding, detectEncodingFromByteOrderMarks: true);
            string all = sr.ReadToEnd();
            if (maxTailLines <= 0) return all;

            string[] allLines = all.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries);
            if (allLines.Length <= maxTailLines) return all;

            return string.Join(Environment.NewLine, allLines.Skip(allLines.Length - maxTailLines));
        }

        // Seek bounded tail, aligning to even byte boundary if UTF-16LE
        long seekOffset = fs.Length - maxTailBytes;
        if (isUtf16Le && (seekOffset % 2 != 0))
        {
            seekOffset++; // Align to 16-bit code unit boundary
        }

        fs.Seek(seekOffset, SeekOrigin.Begin);
        using (StreamReader sr = new(fs, encoding, detectEncodingFromByteOrderMarks: false))
        {
            // Skip potential partial first line
            _ = sr.ReadLine();

            List<string> lines = new();
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                lines.Add(line);
            }

            int skipCount = Math.Max(0, lines.Count - maxTailLines);
            return string.Join(Environment.NewLine, lines.Skip(skipCount));
        }
    }

    /// <summary>
    /// Returns the bounded list of directories where NativeGuard or K2's CCrashReporter writes client .dmp files,
    /// avoiding unbounded recursive scans across massive userdata trees.
    /// </summary>
    public static IReadOnlyList<string> GetClientCrashDumpDirectories(string clientRoot)
    {
        List<string> dirs =
        [
            Path.Combine(clientRoot, "logs", "crash"),
            Path.Combine(clientRoot, "dumps"),
            Path.Combine(clientRoot, "linux-x64", "dumps"),
            Path.Combine(clientRoot, "userdata", "logs", "crash"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "override"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "game"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "base"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "editor"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "KONGOR", "extensions"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth", "override"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth", "game"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth x64", "dumps"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth x64", "override"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth x64", "game"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth x64"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth", "dumps"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth", "override"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth", "game"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth x64", "dumps"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth x64", "override"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth x64", "game"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth x64"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth", "dumps"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth", "override"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth", "game"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth"),
            Path.Combine(clientRoot, "macos-x64", "userdata", "Library", "Application Support", "Heroes of Newerth", "crash"),
            Path.Combine(clientRoot, "macos-x64", "userdata", "Library", "Application Support", "Heroes of Newerth", "override"),
            Path.Combine(clientRoot, "macos-x64", "userdata", "Library", "Application Support", "Heroes of Newerth", "game"),
            Path.Combine(clientRoot, "macos-x64", "userdata", "Library", "Application Support", "Heroes of Newerth"),
            Path.Combine(clientRoot, "userdata")
        ];

        string[] profileRoots =
        [
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth x64"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth x64"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth")
        ];

        foreach (string profileRoot in profileRoots)
        {
            if (!Directory.Exists(profileRoot))
            {
                continue;
            }

            try
            {
                foreach (string subDir in Directory.EnumerateDirectories(profileRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!dirs.Contains(subDir, StringComparer.OrdinalIgnoreCase))
                    {
                        dirs.Add(subDir);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Crash directory enumeration notice: {ex.Message}");
            }
        }

        return dirs;
    }

    /// <summary>
    /// Locates the globally newest crash minidump across NativeGuard (logs/crash), K2 CCrashReporter
    /// (userdata/Heroes of Newerth x64/&lt;mod&gt;/*.dmp), and optional Windows WER CrashDumps, optionally
    /// requiring LastWriteTimeUtc &gt;= minWriteTimeUtc so stale dumps from prior sessions are never selected.
    /// </summary>
    public static string? LocateLatestCrashDumpPath(
        string clientRoot,
        DateTimeOffset? minWriteTimeUtc = null,
        bool includeWerFallback = false)
    {
        if (string.IsNullOrWhiteSpace(clientRoot)) return null;

        DateTime? minUtc = minWriteTimeUtc?.UtcDateTime;
        FileInfo? newestTier1NativeGuard = null;
        FileInfo? newestNonRich = null;
        FileInfo? newestAny = null;

        void ConsiderCandidate(string path)
        {
            try
            {
                FileInfo fi = new(path);
                if (!fi.Exists || fi.Length <= 0) return;
                if (minUtc.HasValue && fi.LastWriteTimeUtc < minUtc.Value) return;

                if (newestAny == null || fi.LastWriteTimeUtc > newestAny.LastWriteTimeUtc)
                {
                    newestAny = fi;
                }

                if (!fi.Name.EndsWith(".rich.dmp", StringComparison.OrdinalIgnoreCase))
                {
                    if (newestNonRich == null || fi.LastWriteTimeUtc > newestNonRich.LastWriteTimeUtc)
                    {
                        newestNonRich = fi;
                    }

                    if (fi.Name.StartsWith("minidump_", StringComparison.OrdinalIgnoreCase) &&
                        fi.Length <= 4 * 1024 * 1024 &&
                        (newestTier1NativeGuard == null || fi.LastWriteTimeUtc > newestTier1NativeGuard.LastWriteTimeUtc))
                    {
                        newestTier1NativeGuard = fi;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Crash candidate stat notice for '{path}': {ex.Message}");
            }
        }

        foreach (string dir in GetClientCrashDumpDirectories(clientRoot))
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (string dmp in Directory.EnumerateFiles(dir, "*.dmp", SearchOption.TopDirectoryOnly))
                {
                    ConsiderCandidate(dmp);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Crash dir scan notice for '{dir}': {ex.Message}");
            }
        }

        if (includeWerFallback && OperatingSystem.IsWindows())
        {
            try
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string werDumpsDir = Path.Combine(localApp, "CrashDumps");
                if (!string.IsNullOrWhiteSpace(localApp) && Directory.Exists(werDumpsDir))
                {
                    foreach (string werDump in Directory.EnumerateFiles(werDumpsDir, "hon_x64*.dmp", SearchOption.TopDirectoryOnly))
                    {
                        ConsiderCandidate(werDump);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] WER CrashDumps fallback scan notice: {ex.Message}");
            }
        }

        if (newestTier1NativeGuard != null && newestAny != null &&
            (newestAny.LastWriteTimeUtc - newestTier1NativeGuard.LastWriteTimeUtc) <= TimeSpan.FromSeconds(30))
        {
            return newestTier1NativeGuard.FullName;
        }

        if (newestNonRich != null && newestAny != null &&
            (newestAny.LastWriteTimeUtc - newestNonRich.LastWriteTimeUtc) <= TimeSpan.FromSeconds(30))
        {
            return newestNonRich.FullName;
        }

        return (newestAny ?? newestNonRich)?.FullName;
    }

    private static (byte[]? MinidumpBytes, string? Tier1DumpPath, string? FullDumpPath) LocateAndReadLatestMinidump(
        string clientRoot,
        string? preferredDumpPath = null,
        DateTimeOffset? minWriteTimeUtc = null)
    {
        string? primaryDump = (!string.IsNullOrWhiteSpace(preferredDumpPath) && File.Exists(preferredDumpPath))
            ? preferredDumpPath
            : LocateLatestCrashDumpPath(clientRoot, minWriteTimeUtc, includeWerFallback: minWriteTimeUtc.HasValue);

        if (string.IsNullOrEmpty(primaryDump) || !File.Exists(primaryDump))
        {
            return (null, null, null);
        }

        string? tier1Path = null;
        string? richPath = null;
        FileInfo primaryInfo = new(primaryDump);
        DateTime eventTimeUtc = primaryInfo.LastWriteTimeUtc;
        bool primaryIsNativeGuardTier1 =
            primaryInfo.Name.StartsWith("minidump_", StringComparison.OrdinalIgnoreCase) &&
            !primaryInfo.Name.EndsWith(".rich.dmp", StringComparison.OrdinalIgnoreCase) &&
            primaryInfo.Length <= 4 * 1024 * 1024;

        if (primaryInfo.Length <= 4 * 1024 * 1024 &&
            !primaryInfo.Name.EndsWith(".rich.dmp", StringComparison.OrdinalIgnoreCase))
        {
            tier1Path = primaryInfo.FullName;
        }
        else
        {
            richPath = primaryInfo.FullName;
        }

        // Pair companion tier-1 or rich dump ONLY if written during the same crash event (within +-30s),
        // never mixing a stale minidump_*.dmp from hours earlier with a newer CCrashReporter <uuid>.dmp!
        // When both a NativeGuard Tier-1 minidump_*.dmp (~55-250 KB, Zero-PII scrubbed) and a K2 CCrashReporter
        // <uuid>.dmp (~3.7 MB data-seg dump) exist within +-30s of the same crash event, always prefer
        // the NativeGuard Tier-1 minidump_*.dmp for bundle packaging and record the K2 dump as richPath.
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        if (Directory.Exists(crashDir))
        {
            try
            {
                if (string.IsNullOrEmpty(tier1Path) || !primaryIsNativeGuardTier1)
                {
                    string? companionNativeGuardTier1 = Directory.GetFiles(crashDir, "minidump_*.dmp")
                        .Where(f => !f.EndsWith(".rich.dmp", StringComparison.OrdinalIgnoreCase))
                        .Select(f => new FileInfo(f))
                        .Where(fi => fi.Length <= 4 * 1024 * 1024 &&
                                     Math.Abs((fi.LastWriteTimeUtc - eventTimeUtc).TotalSeconds) <= 30.0)
                        .OrderByDescending(fi => fi.LastWriteTimeUtc)
                        .Select(fi => fi.FullName)
                        .FirstOrDefault();

                    if (!string.IsNullOrEmpty(companionNativeGuardTier1))
                    {
                        if (!primaryIsNativeGuardTier1 && string.IsNullOrEmpty(richPath))
                        {
                            richPath = primaryInfo.FullName;
                        }

                        tier1Path = companionNativeGuardTier1;
                    }
                }

                if (string.IsNullOrEmpty(richPath))
                {
                    richPath = Directory.GetFiles(crashDir, "minidump_*.rich.dmp")
                        .Select(f => new FileInfo(f))
                        .Where(fi => Math.Abs((fi.LastWriteTimeUtc - eventTimeUtc).TotalSeconds) <= 30.0)
                        .OrderByDescending(fi => fi.LastWriteTimeUtc)
                        .Select(fi => fi.FullName)
                        .FirstOrDefault();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Companion crash dump lookup notice: {ex.Message}");
            }
        }

        byte[]? bytes = null;
        if (!string.IsNullOrEmpty(tier1Path) && File.Exists(tier1Path))
        {
            try
            {
                bytes = File.ReadAllBytes(tier1Path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Minidump read notice for '{tier1Path}': {ex.Message}");
            }
        }

        return (bytes, tier1Path, richPath);
    }

    private static string FormatSafeOnDiskDumpEntryFileName(string dumpPath, DateTime fallbackUtc)
    {
        string rawFileName = Path.GetFileName(dumpPath);
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            return "minidump.dmp";
        }

        // Replace raw K2 CCrashReporter UUID filenames (<uuid>.dmp) using the dump's own LastWriteTimeUtc
        // so FindZeroPiiCanaryViolation never trips on raw UUIDs while preserving the true on-disk dump timestamp.
        if (Regex.IsMatch(rawFileName, @"^[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\.dmp$"))
        {
            DateTime fileWriteUtc = File.Exists(dumpPath)
                ? File.GetLastWriteTimeUtc(dumpPath)
                : fallbackUtc;
            return $"k2_crash_{fileWriteUtc:yyyyMMdd_HHmmss}.dmp";
        }

        string safeName = SanitizeFilename(rawFileName);
        return safeName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)
            ? safeName
            : $"{safeName}.dmp";
    }

    private static string? DetectEmptyHangDumpNotice(
        string clientRoot,
        DateTimeOffset minWriteTimeUtc,
        int? expectedProcessId)
    {
        if (string.IsNullOrWhiteSpace(clientRoot))
        {
            return null;
        }

        List<string> emptyHangDumpNames = new();
        DateTime cutoffUtc = minWriteTimeUtc.UtcDateTime.AddSeconds(-5);
        Regex pidRegex = new(@"_pid(\d+)\.dmp$", RegexOptions.IgnoreCase);

        foreach (string dir in GetClientCrashDumpDirectories(clientRoot))
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                foreach (string dmpPath in Directory.EnumerateFiles(dir, "hang_*_pid*.dmp", SearchOption.TopDirectoryOnly))
                {
                    FileInfo fi = new(dmpPath);
                    if (!fi.Exists || fi.Length != 0)
                    {
                        continue;
                    }

                    Match pidMatch = pidRegex.Match(fi.Name);
                    int? parsedPid = pidMatch.Success && int.TryParse(pidMatch.Groups[1].Value, out int pVal) && pVal > 0
                        ? pVal
                        : null;

                    if (expectedProcessId.HasValue)
                    {
                        if (!parsedPid.HasValue || parsedPid.Value != expectedProcessId.Value)
                        {
                            continue;
                        }
                    }
                    else if (fi.LastWriteTimeUtc < cutoffUtc)
                    {
                        continue;
                    }

                    if (!emptyHangDumpNames.Contains(fi.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        emptyHangDumpNames.Add(fi.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Empty hang dump scan notice in '{dir}': {ex.Message}");
            }
        }

        if (emptyHangDumpNames.Count == 0)
        {
            return null;
        }

        emptyHangDumpNames.Sort(StringComparer.OrdinalIgnoreCase);
        string namesFormatted = string.Join("`, `", emptyHangDumpNames);
        string notice =
            $"Detected 0-byte truncated hang dump(s) on disk (`{namesFormatted}`) — live hang snapshot (`MiniDumpWriteDump`) was interrupted or failed before flushing MDMP stream headers.";
        System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] {notice}");
        return notice;
    }

    private static readonly HashSet<string> GenericBinaryCanaryUserNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "users", "admin", "administrator", "root", "guest", "default",
        "public", "system", "owner", "runner", "player", "local", "test", "dev", "home", "temp", "tmp"
    };

    /// <summary>
    /// Fast binary Zero-PII canary scan executed ONLY on packaged Tier-1 minimal <c>.dmp</c> buffers
    /// (typically ~89 KB, bounded &lt;= 4 MB; never run on local <c>.rich.dmp</c> dumps) right before writing into the zip archive.
    /// Scans raw ASCII and UTF-16LE (both even and odd byte alignments) for:
    /// (a) Current OS username (when length &gt;= 3 and non-generic),
    /// (b) User profile root path, and
    /// (c) Raw labeled hardware MAC addresses (<c>MAC: xx-xx-xx-xx-xx-xx</c>).
    /// Returns a non-PII-leaking violation description if contaminated, or <c>null</c> if clean.
    /// </summary>
    public static string? FindBinaryMinidumpZeroPiiViolation(
        ReadOnlySpan<byte> minidumpBytes,
        string? userNameOverride = null,
        string? userProfilePathOverride = null)
    {
        if (minidumpBytes.IsEmpty)
        {
            return null;
        }

        string asciiView = Encoding.ASCII.GetString(minidumpBytes);
        int evenLen = minidumpBytes.Length & ~1;
        string utf16EvenView = evenLen >= 2 ? Encoding.Unicode.GetString(minidumpBytes[..evenLen]) : string.Empty;
        int oddLen = minidumpBytes.Length >= 3 ? ((minidumpBytes.Length - 1) & ~1) : 0;
        string utf16OddView = oddLen >= 2 ? Encoding.Unicode.GetString(minidumpBytes.Slice(1, oddLen)) : string.Empty;

        // (b) User profile root path (checked before bare username so full-path leaks get the more specific classification)
        string rawProfilePath = userProfilePathOverride ?? SafeGetUserProfilePath();
        if (!string.IsNullOrWhiteSpace(rawProfilePath))
        {
            string trimmedProfile = rawProfilePath.Trim().TrimEnd('\\', '/');
            if (trimmedProfile.Length >= 4)
            {
                string winProfile = trimmedProfile.Replace('/', '\\');
                string posixProfile = trimmedProfile.Replace('\\', '/');
                string escapedWinProfile = winProfile.Replace("\\", "\\\\");

                if (asciiView.Contains(winProfile, StringComparison.OrdinalIgnoreCase) ||
                    asciiView.Contains(posixProfile, StringComparison.OrdinalIgnoreCase) ||
                    asciiView.Contains(escapedWinProfile, StringComparison.OrdinalIgnoreCase))
                {
                    return "Unredacted user profile root path detected in binary minidump (ASCII).";
                }

                if (utf16EvenView.Contains(winProfile, StringComparison.OrdinalIgnoreCase) ||
                    utf16EvenView.Contains(posixProfile, StringComparison.OrdinalIgnoreCase) ||
                    utf16EvenView.Contains(escapedWinProfile, StringComparison.OrdinalIgnoreCase) ||
                    utf16OddView.Contains(winProfile, StringComparison.OrdinalIgnoreCase) ||
                    utf16OddView.Contains(posixProfile, StringComparison.OrdinalIgnoreCase) ||
                    utf16OddView.Contains(escapedWinProfile, StringComparison.OrdinalIgnoreCase))
                {
                    return "Unredacted user profile root path detected in binary minidump (UTF-16LE).";
                }
            }
        }

        // (a) Current OS username (length >= 3 and not a generic name like user/admin)
        string rawUserName = (userNameOverride ?? Environment.UserName ?? string.Empty).Trim();
        if (rawUserName.Length >= 3 && !GenericBinaryCanaryUserNames.Contains(rawUserName))
        {
            if (DiagnosticRedactor.ContainsBoundedIdentifier(asciiView, rawUserName))
            {
                return "Unredacted OS username detected in binary minidump (ASCII).";
            }

            if (DiagnosticRedactor.ContainsBoundedIdentifier(utf16EvenView, rawUserName) ||
                DiagnosticRedactor.ContainsBoundedIdentifier(utf16OddView, rawUserName))
            {
                return "Unredacted OS username detected in binary minidump (UTF-16LE).";
            }
        }

        // (c) Any raw labeled hardware MAC address (e.g. MAC: xx-xx-xx-xx-xx-xx / MAC=xx:xx:xx:xx:xx:xx)
        if (DiagnosticRedactor.ContainsRawHardwareMacAddress(asciiView, requireLabeledOnly: true))
        {
            return "Unredacted hardware MAC address detected in binary minidump (ASCII).";
        }

        if (DiagnosticRedactor.ContainsRawHardwareMacAddress(utf16EvenView, requireLabeledOnly: true) ||
            DiagnosticRedactor.ContainsRawHardwareMacAddress(utf16OddView, requireLabeledOnly: true))
        {
            return "Unredacted hardware MAC address detected in binary minidump (UTF-16LE).";
        }

        return null;
    }

    private static string SafeGetUserProfilePath()
    {
        try
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static (string SurgicallyScrubbedText, int RedactedLineCount) ApplyZeroPiiCanarySurgicalLineFallback(
        string entryName,
        string scrubbedText,
        PrivacyScrubContext privacyContext)
    {
        string[] lines = scrubbedText.Split(["\r\n", "\n"], StringSplitOptions.None);
        int redactedLineCount = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            string? lineViolation = DiagnosticRedactor.FindZeroPiiCanaryViolation(
                entryName,
                Encoding.UTF8.GetBytes(line),
                privacyContext,
                isBinaryMinidump: false);

            if (lineViolation != null)
            {
                string kind = DiagnosticRedactor.ClassifyZeroPiiViolationKind(lineViolation);
                lines[i] = $"[LINE_REDACTED_BY_ZERO_PII_CANARY: {kind}]";
                redactedLineCount++;
            }
        }

        return (string.Join(Environment.NewLine, lines), redactedLineCount);
    }

    /// <summary>
    /// Decodes well-known Windows NTSTATUS / CRT fatal exit codes into human-readable names and descriptions
    /// when a process terminates without an unhandled SEH minidump (e.g. kernel __fastfail 0xC0000409).
    /// </summary>
    public static (string ExceptionName, string Description) DescribeWindowsNtStatusExitCode(uint exitCode) => exitCode switch
    {
        0xC0000409u => ("STATUS_STACK_BUFFER_OVERRUN", "Stack Buffer Overrun / Kernel __fastfail (0xC0000409)"),
        0xC0000374u => ("STATUS_HEAP_CORRUPTION", "Heap Corruption (0xC0000374)"),
        0xC0000005u => ("STATUS_ACCESS_VIOLATION", "Access Violation (0xC0000005)"),
        0xC0000006u => ("STATUS_IN_PAGE_ERROR", "In-Page I/O Error (0xC0000006)"),
        0xC0000194u => ("STATUS_POSSIBLE_DEADLOCK", "Loader / Critical-Section Deadlock (0xC0000194)"),
        0xC00000FDu => ("STATUS_STACK_OVERFLOW", "Stack Overflow (0xC00000FD)"),
        0xC000001Du => ("STATUS_ILLEGAL_INSTRUCTION", "Illegal Instruction (0xC000001D)"),
        0xC0000025u => ("STATUS_NONCONTINUABLE_EXCEPTION", "Noncontinuable Exception (0xC0000025)"),
        0xCFFFFFFFu => ("STATUS_APPLICATION_HANG", "Application / Shutdown Hang (0xCFFFFFFF)"),
        0xE0000001u => ("CRT_PURE_VIRTUAL_CALL", "CRT Pure Virtual Call (0xE0000001)"),
        0xE0000002u => ("CRT_INVALID_PARAMETER", "CRT Invalid Parameter (0xE0000002)"),
        _ => ("ABRUPT_NONZERO_PROCESS_EXIT", $"Abrupt Non-Zero Process Exit (0x{exitCode:X8})")
    };

    private static string TryReadNativeGuardSentinelTail(string clientRoot, int maxLines = 10)
    {
        try
        {
            string sentinelPath = Path.Combine(clientRoot, "logs", "crash", "native_guard_sentinel.log");
            if (!File.Exists(sentinelPath)) return string.Empty;
            string tail = ReadBoundedLogText(sentinelPath, isUtf16Possible: false, maxTailLines: maxLines, maxTailBytes: 16 * 1024);
            return tail.Trim();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] NativeGuard sentinel tail notice: {ex.Message}");
            return string.Empty;
        }
    }

    private static bool IsShutdownHangOrIncompleteQuit(
        string clientRoot,
        string? resolvedCrashDumpPath,
        uint? processExitCode,
        int? expectedProcessId,
        out string? detail)
    {
        detail = null;
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        bool hasSentinel = TryReadShutdownHangSentinelDetail(crashDir, expectedProcessId, out detail);
        bool isHangDumpFile = !string.IsNullOrWhiteSpace(resolvedCrashDumpPath) &&
            Path.GetFileName(resolvedCrashDumpPath).StartsWith("hang_", StringComparison.OrdinalIgnoreCase);
        bool isHangExitCode = processExitCode == 0xCFFFFFFFu;
        return isHangDumpFile || isHangExitCode || hasSentinel;
    }

    private static bool TryReadShutdownHangSentinelDetail(string crashDir, int? expectedProcessId, out string? detail)
    {
        detail = null;
        try
        {
            if (!Directory.Exists(crashDir)) return false;
            List<string> candidates = [];
            if (expectedProcessId is int pid and > 0)
            {
                candidates.Add(Path.Combine(crashDir, $"last-crashed.{pid}.sentinel"));
                candidates.Add(Path.Combine(crashDir, $"quit-initiated.{pid}.sentinel"));
            }
            candidates.Add(Path.Combine(crashDir, "last-crashed.sentinel"));

            foreach (string path in candidates)
            {
                if (!File.Exists(path)) continue;
                string content = ReadBoundedLogText(path, isUtf16Possible: false, maxTailLines: 20, maxTailBytes: 4096);
                if (content.Contains("ShutdownHangTimeout", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("ShutdownStall", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).StartsWith("quit-initiated.", StringComparison.OrdinalIgnoreCase))
                {
                    detail = string.Join(", ", content
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Take(6));
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Shutdown hang sentinel read notice: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// Captures a PID-targeted out-of-process Tier-1 Zero-PII minidump (safe even when the target
    /// process is deadlocked inside ntdll!LdrShutdownProcess holding LdrpLoaderLock) and writes it
    /// directly to <paramref name="crashDir"/>.
    /// </summary>
    public static string? TryCaptureProcessMinidumpToFile(int processId, string crashDir, string filePrefix = "hang")
    {
        if (!OperatingSystem.IsWindows() || processId <= 0 || string.IsNullOrWhiteSpace(crashDir))
        {
            return null;
        }

        string dumpPath = string.Empty;
        try
        {
            using Process targetProc = Process.GetProcessById(processId);
            if (targetProc.HasExited)
            {
                return null;
            }

            Directory.CreateDirectory(crashDir);
            string safePrefix = SanitizeFilename(string.IsNullOrWhiteSpace(filePrefix) ? "hang" : filePrefix);
            string timestamp = TimeProvider.System.GetUtcNow().UtcDateTime.ToString("yyyyMMdd_HHmmss");
            dumpPath = Path.Combine(crashDir, $"{safePrefix}_{timestamp}_pid{processId}.dmp");

            using (FileStream fs = new(dumpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                // MiniDumpNormal (0x0) | MiniDumpFilterModulePaths (0x80) | MiniDumpWithThreadInfo (0x1000) | MiniDumpWithoutAuxiliaryState (0x4000)
                const uint flags = 0x00000000u | 0x00000080u | 0x00001000u | 0x00004000u;
                bool ok = MiniDumpWriteDump(targetProc.Handle, (uint)targetProc.Id, fs.SafeFileHandle, flags, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                fs.Flush(flushToDisk: true);
                if (!ok)
                {
                    try { File.Delete(dumpPath); } catch (Exception delEx) { System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Failed dump delete notice: {delEx.Message}"); }
                    return null;
                }
            }

            FileInfo fi = new(dumpPath);
            if (fi.Exists && fi.Length > 0)
            {
                return dumpPath;
            }

            try { if (File.Exists(dumpPath)) File.Delete(dumpPath); } catch (Exception delEx) { System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Zero-byte dump delete notice: {delEx.Message}"); }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] PID-targeted minidump capture failed for PID {processId}: {ex.Message}");
            try { if (!string.IsNullOrEmpty(dumpPath) && File.Exists(dumpPath)) File.Delete(dumpPath); } catch (Exception delEx) { System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Failed dump cleanup notice: {delEx.Message}"); }
        }

        return null;
    }

    internal static string ResolveLiveProcessScopePrefix(string clientRoot)
    {
        string normalized = Path.GetFullPath(clientRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(normalized);
        if ((leaf.Equals("win-x64", StringComparison.OrdinalIgnoreCase) ||
             leaf.Equals("windows-x64", StringComparison.OrdinalIgnoreCase) ||
             leaf.Equals("linux-x64", StringComparison.OrdinalIgnoreCase) ||
             leaf.Equals("macos-x64", StringComparison.OrdinalIgnoreCase)) &&
            Directory.GetParent(normalized) is { } parentDir)
        {
            normalized = parentDir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return normalized + Path.DirectorySeparatorChar;
    }

    internal static bool IsLiveProcessCandidateMatch(
        int candidatePid,
        string? candidateExecutablePath,
        string? clientRoot,
        int? expectedProcessId,
        out string rejectionReason)
    {
        if (candidatePid <= 0)
        {
            rejectionReason = $"invalid candidate PID {candidatePid}";
            return false;
        }

        if (expectedProcessId is > 0 && candidatePid != expectedProcessId.Value)
        {
            rejectionReason = $"PID {candidatePid} does not match expectedProcessId={expectedProcessId.Value}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(clientRoot))
        {
            rejectionReason = "clientRoot is empty";
            return false;
        }

        string scopePrefix = ResolveLiveProcessScopePrefix(clientRoot);
        StringComparison pathComparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        if (!string.IsNullOrWhiteSpace(candidateExecutablePath))
        {
            string fullExe = Path.GetFullPath(candidateExecutablePath);
            if (fullExe.StartsWith(scopePrefix, pathComparison))
            {
                rejectionReason = string.Empty;
                return true;
            }

            rejectionReason = $"executable '{fullExe}' is outside client scope '{scopePrefix}'";
            return false;
        }

        // Fallback when MainModule is inaccessible (e.g., cross-privilege Win32Exception):
        // require an explicit PID match AND an on-disk client executable inside the target scope.
        string scopeDir = scopePrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        bool hasOnDiskClientBinary =
            File.Exists(Path.Combine(scopeDir, "hon_x64.exe")) ||
            File.Exists(Path.Combine(scopeDir, "windows-x64", "hon_x64.exe")) ||
            File.Exists(Path.Combine(scopeDir, "win-x64", "hon_x64.exe"));

        if (expectedProcessId is > 0 && candidatePid == expectedProcessId.Value && hasOnDiskClientBinary)
        {
            rejectionReason = string.Empty;
            return true;
        }

        rejectionReason = $"MainModule path unavailable for PID {candidatePid} and no verified PID/on-disk match under '{scopePrefix}'";
        return false;
    }

    private static byte[]? TryCaptureLiveProcessDump(string clientRoot, int? expectedProcessId)
    {
        Process[] candidates = [];
        try
        {
            candidates = Process.GetProcessesByName("hon_x64");
            if (candidates.Length == 0)
            {
                return null;
            }

            Process? gameProc = null;
            foreach (Process candidate in candidates)
            {
                try
                {
                    if (candidate.HasExited)
                    {
                        continue;
                    }

                    string? exePath = null;
                    try
                    {
                        exePath = candidate.MainModule?.FileName;
                    }
                    catch (Exception modEx)
                    {
                        System.Diagnostics.Trace.WriteLine(
                            $"[DIAGNOSTICS] Live hang snapshot MainModule query notice for hon_x64 (PID {candidate.Id}): {modEx.Message}");
                    }

                    if (!IsLiveProcessCandidateMatch(
                            candidate.Id,
                            exePath,
                            clientRoot,
                            expectedProcessId,
                            out string rejectionReason))
                    {
                        System.Diagnostics.Trace.WriteLine(
                            $"[DIAGNOSTICS] Skipping live hon_x64 (PID {candidate.Id}) during manual snapshot: {rejectionReason}");
                        continue;
                    }

                    gameProc = candidate;
                    break;
                }
                catch (Exception inspectEx)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[DIAGNOSTICS] Live hang snapshot candidate inspection notice: {inspectEx.Message}");
                }
            }

            if (gameProc == null || gameProc.HasExited)
            {
                return null;
            }

            System.Diagnostics.Trace.WriteLine(
                $"[DIAGNOSTICS] Capturing live freeze minidump snapshot from hon_x64 (PID {gameProc.Id}) scoped to '{clientRoot}'.");

            string tempDumpPath = Path.Combine(Path.GetTempPath(), $"hon_hang_{Guid.NewGuid():N}.dmp");
            using (FileStream fs = new(tempDumpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                // MiniDumpNormal (0x00000000) | MiniDumpFilterModulePaths (0x00000080) | MiniDumpWithThreadInfo (0x00001000)
                uint flags = 0x00000000u | 0x00000080u | 0x00001000u;
                bool ok = MiniDumpWriteDump(gameProc.Handle, (uint)gameProc.Id, fs.SafeFileHandle, flags, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (!ok)
                {
                    try { if (File.Exists(tempDumpPath)) File.Delete(tempDumpPath); } catch (Exception delEx) { System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Failed live dump cleanup notice: {delEx.Message}"); }
                    return null;
                }
            }

            if (File.Exists(tempDumpPath))
            {
                byte[] data = File.ReadAllBytes(tempDumpPath);
                try { File.Delete(tempDumpPath); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Temp dump cleanup notice: {ex.Message}"); }
                return data;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Live hang snapshot notice: {ex.Message}");
        }
        finally
        {
            foreach (Process proc in candidates)
            {
                try
                {
                    proc.Dispose();
                }
                catch (Exception dispEx)
                {
                    System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Process handle dispose notice: {dispEx.Message}");
                }
            }
        }
        return null;
    }

    private static string? LocateActiveConsoleLog(string clientRoot, string? activeModString)
    {
        List<string> candidateDirs = new()
        {
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "override"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "game"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "KONGOR", "extensions"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "editor"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "base"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth", "override"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth", "game"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth x64", "override"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth x64", "game"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth x64", "KONGOR", "extensions"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth x64"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth", "override"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth", "game"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth", "KONGOR", "extensions"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth x64", "override"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth x64", "game"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth x64"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth", "override"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth", "game"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth"),
            Path.Combine(clientRoot, "macos-x64", "userdata", "Library", "Application Support", "Heroes of Newerth", "override"),
            Path.Combine(clientRoot, "macos-x64", "userdata", "Library", "Application Support", "Heroes of Newerth", "game"),
            Path.Combine(clientRoot, "macos-x64", "userdata", "Library", "Application Support", "Heroes of Newerth"),
            Path.Combine(clientRoot, "game"),
            Path.Combine(clientRoot, "linux-x64", "game"),
            clientRoot
        };

        return candidateDirs
            .Select(d => Path.Combine(d, "console.log"))
            .Where(f => File.Exists(f) && new FileInfo(f).Length > 0)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string? LocateActiveStartupCfg(string clientRoot)
    {
        string[] candidates =
        [
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "game", "startup.cfg"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth", "game", "startup.cfg"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth x64", "game", "startup.cfg"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth", "game", "startup.cfg"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth x64", "game", "startup.cfg"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth", "game", "startup.cfg"),
            Path.Combine(clientRoot, "macos-x64", "userdata", "Library", "Application Support", "Heroes of Newerth", "game", "startup.cfg")
        ];

        return candidates
            .Where(f => File.Exists(f) && new FileInfo(f).Length > 0)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string? LocateActiveWillowmakerLog(string clientRoot, string outDir, string? activeLauncherLogPath)
    {
        if (!string.IsNullOrWhiteSpace(activeLauncherLogPath) &&
            File.Exists(activeLauncherLogPath) &&
            new FileInfo(activeLauncherLogPath).Length > 0)
        {
            return activeLauncherLogPath;
        }

        string[] scopedCandidates =
        [
            Path.Combine(clientRoot, "WILLOWMAKER.log"),
            Path.Combine(outDir, "WILLOWMAKER.log")
        ];

        string? scopedMatch = scopedCandidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => File.Exists(f) && new FileInfo(f).Length > 0)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (scopedMatch != null)
        {
            return scopedMatch;
        }

        string[] processFallbackCandidates =
        [
            Path.Combine(AppContext.BaseDirectory, "WILLOWMAKER.log"),
            Path.Combine(Environment.CurrentDirectory, "WILLOWMAKER.log")
        ];

        return processFallbackCandidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => File.Exists(f) && new FileInfo(f).Length > 0)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string? LocateFirstExistingFile(params string[] candidatePaths)
    {
        foreach (string path in candidatePaths)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string? TryResolvePlayerNicknameForScrubbing(string clientRoot)
    {
        if (string.IsNullOrWhiteSpace(clientRoot))
        {
            return null;
        }

        try
        {
            string canonicalDir = Path.Combine(clientRoot, "userdataKONGOR");
            if (File.Exists(LauncherSettingsService.GetSettingsFilePath(canonicalDir)))
            {
                string nick = LauncherSettingsService.Load(canonicalDir).LastPlayerNickname.Trim();
                if (!string.IsNullOrWhiteSpace(nick))
                {
                    return nick;
                }
            }

            if (File.Exists(LauncherSettingsService.GetSettingsFilePath(clientRoot)))
            {
                string legacyNick = LauncherSettingsService.Load(clientRoot).LastPlayerNickname.Trim();
                if (!string.IsNullOrWhiteSpace(legacyNick))
                {
                    return legacyNick;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] TryResolvePlayerNicknameForScrubbing notice: {ex.Message}");
        }

        return null;
    }

    private static void PopulateSensitiveSessionContextFromConfigs(
        string clientRoot,
        PrivacyScrubContext context,
        string? activeWillowmakerLogPath = null,
        string? activeConsoleLogPath = null)
    {
        if (!string.IsNullOrWhiteSpace(activeWillowmakerLogPath) && File.Exists(activeWillowmakerLogPath))
        {
            try
            {
                string wmTail = ReadBoundedLogText(activeWillowmakerLogPath, isUtf16Possible: false, maxTailLines: 4000, maxTailBytes: 1024 * 1024);
                DiagnosticRedactor.HarvestKnownPlayerNamesFromLauncherLog(wmTail, context);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Launcher log handle pre-harvest notice: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(activeConsoleLogPath) && File.Exists(activeConsoleLogPath))
        {
            try
            {
                string consoleTail = ReadBoundedLogText(activeConsoleLogPath, isUtf16Possible: true, maxTailLines: 4000, maxTailBytes: 1024 * 1024);
                DiagnosticRedactor.HarvestKnownPlayerNamesFromConsoleLog(consoleTail, context);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Console log handle pre-harvest notice: {ex.Message}");
            }
        }
        string?[] candidateCfgs =
        [
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "game", "startup.cfg"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth", "game", "startup.cfg"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth x64", "game", "startup.cfg"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth", "game", "startup.cfg"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth x64", "game", "startup.cfg"),
            Path.Combine(clientRoot, "linux-x64", "userdata", ".Heroes of Newerth", "game", "startup.cfg"),
            Path.Combine(clientRoot, "macos-x64", "userdata", "Library", "Application Support", "Heroes of Newerth", "game", "startup.cfg"),
            Path.Combine(clientRoot, "userdataKONGOR", "configuration", "autoexec.cfg"),
            Path.Combine(clientRoot, "game", "autoexec.cfg")
        ];

        foreach (string? cfgPath in candidateCfgs)
        {
            if (string.IsNullOrWhiteSpace(cfgPath) || !File.Exists(cfgPath))
            {
                continue;
            }

            try
            {
                string cfgText = ReadBoundedLogText(cfgPath, isUtf16Possible: true, maxTailLines: 4000, maxTailBytes: 512 * 1024);
                foreach (string rawLine in cfgText.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!TryParseK2SetDirective(rawLine, out _, out string cvarName, out string cvarVal))
                    {
                        continue;
                    }

                    if (cvarName.Equals("login_name", StringComparison.OrdinalIgnoreCase) ||
                        cvarName.Equals("p2p_nickname", StringComparison.OrdinalIgnoreCase) ||
                        cvarName.Equals("host_cloudLastModifiedUser", StringComparison.OrdinalIgnoreCase) ||
                        cvarName.Equals("man_masterLogin", StringComparison.OrdinalIgnoreCase))
                    {
                        context.AddKnownPlayerName(cvarVal);
                    }
                    else if (cvarName.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                             cvarName.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
                             cvarName.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                             cvarName.Contains("secret", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!double.TryParse(cvarVal, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) &&
                            !string.Equals(cvarVal, "true", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(cvarVal, "false", StringComparison.OrdinalIgnoreCase))
                        {
                            context.AddKnownSecret(cvarVal);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] PopulateSensitiveSessionContext notice: {ex.Message}");
            }
        }
    }

    private static readonly string[] AllowedK2DiagnosticCvarPrefixes =
    [
        "vid_", "d3d", "vk_", "gfx_", "scene_", "model_", "tex_", "foliage_",
        "shadow_", "effect_", "post_", "con_", "sys_", "mem_", "net_", "cl_",
        "cam_", "ui_", "sound_", "host_maxfps", "host_affinity", "svr_requestsessioncookietimeout"
    ];

    private static (string ProjectedText, int DroppedCount) ProjectSafeK2ConfigForDiagnostics(
        string rawCfg,
        PrivacyScrubContext context)
    {
        StringBuilder sb = new();
        sb.AppendLine("// Project KONGOR Zero-PII Allowlisted Engine/Graphics Configuration Snapshot");
        int dropped = 0;
        int kept = 0;

        foreach (string rawLine in rawCfg.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryParseK2SetDirective(trimmed, out string directive, out string cvarName, out string cvarValue))
            {
                // Categorically drop all Bind, BindImpulse, BindButton, Alias, Exec, Echo, Say macros
                dropped++;
                continue;
            }

            if (cvarName.Equals("sound_output", StringComparison.OrdinalIgnoreCase) ||
                cvarName.Equals("sound_record_driver", StringComparison.OrdinalIgnoreCase))
            {
                dropped++;
                continue;
            }

            bool nameAllowed = AllowedK2DiagnosticCvarPrefixes.Any(prefix =>
                cvarName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (!nameAllowed || !IsSafeDiagnosticCvarValue(cvarValue, context))
            {
                dropped++;
                continue;
            }

            sb.AppendLine($"{directive} \"{cvarName}\" \"{cvarValue}\"");
            kept++;
        }

        return kept > 0 ? (sb.ToString(), dropped) : (string.Empty, dropped);
    }

    private static bool TryParseK2SetDirective(string line, out string directive, out string cvarName, out string cvarValue)
    {
        directive = string.Empty;
        cvarName = string.Empty;
        cvarValue = string.Empty;

        Match m = Regex.Match(
            line,
            @"^\s*(SetSave|Set)\s+[""']?([A-Za-z0-9_.\-]+)[""']?\s+(?:""([^""\r\n]*)""|'([^'\r\n]*)'|([^\s""'\r\n]+))\s*$");
        if (!m.Success)
        {
            return false;
        }

        directive = m.Groups[1].Value;
        cvarName = m.Groups[2].Value;
        cvarValue = m.Groups[3].Success ? m.Groups[3].Value : (m.Groups[4].Success ? m.Groups[4].Value : m.Groups[5].Value);
        return true;
    }

    private static bool IsSafeDiagnosticCvarValue(string value, PrivacyScrubContext context)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
            double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return true;
        }

        // Only permit short alphanumeric/underscore enum tokens (e.g. "high", "low", "vulkan", "d3d9", "auto")
        // Reject any slashes, colons, spaces, dots, or strings matching known session identifiers.
        if (!Regex.IsMatch(value, @"^[A-Za-z0-9_]{1,24}$"))
        {
            return false;
        }

        foreach (string p in context.KnownPlayerNames)
        {
            if (value.Contains(p, StringComparison.OrdinalIgnoreCase)) return false;
        }
        foreach (string u in context.KnownUserLiterals)
        {
            if (value.Contains(u, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    private static string? ProjectSafeLauncherSettingsJson(string rawJson, ref int redactedFieldsCount)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        LauncherSettingsModel? model = JsonSerializer.Deserialize(rawJson, WillowmakerJsonSerializerContext.Default.LauncherSettingsModel);
        if (model == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(model.LastPlayerNickname)) redactedFieldsCount++;
        if (!string.IsNullOrWhiteSpace(model.CustomMasterServerAddress)) redactedFieldsCount++;
        if (!string.IsNullOrWhiteSpace(model.CustomCDNAddress)) redactedFieldsCount++;
        if (model.FavoriteModIds.Count > 0) redactedFieldsCount++;

        using MemoryStream ms = new();
        using (Utf8JsonWriter writer = new(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("swapchainBackend", model.SwapchainBackend);
            writer.WriteString("presentationMode", model.PresentationMode);
            writer.WriteString("displayMode", model.DisplayMode);
            writer.WriteString("resolution", model.Resolution);
            writer.WriteString("refreshRate", model.RefreshRate);
            writer.WriteNumber("fpsCap", model.FpsCap);
            writer.WriteBoolean("enableRayTracing", model.EnableRayTracing);
            writer.WriteString("rayTracingPresetName", model.RayTracingPresetName);
            writer.WriteBoolean("enableAsyncPipelineCaching", model.EnableAsyncPipelineCaching);
            writer.WriteBoolean("enableTextureClarityBoost", model.EnableTextureClarityBoost);
            writer.WriteString("shadowFidelity", model.ShadowFidelity);
            writer.WriteString("vulkanHudMode", model.VulkanHudMode);
            writer.WriteBoolean("enableNvidiaReflex", model.EnableNvidiaReflex);
            writer.WriteBoolean("enableFidelityFxRcas", model.EnableFidelityFxRcas);
            writer.WriteNumber("fidelityFxRcasSharpness", model.FidelityFxRcasSharpness);
            writer.WriteBoolean("allowThirdPartyVulkanOverlays", model.AllowThirdPartyVulkanOverlays);
            writer.WriteBoolean("enableSpatialAudio", model.EnableSpatialAudio);
            writer.WriteString("spatialHrtfProfile", model.SpatialHrtfProfile);
            writer.WriteNumber("spatialAudioChannels", model.SpatialAudioChannels);
            writer.WriteBoolean("enableOcclusionDamping", model.EnableOcclusionDamping);
            writer.WriteBoolean("enableQuicHttp3", model.EnableQuicHttp3);
            writer.WriteBoolean("enableDirectFlip", model.EnableDirectFlip);
            writer.WriteString("networkProfile", model.NetworkProfile);
            writer.WriteNumber("consecutiveCrashCount", model.ConsecutiveCrashCount);
            writer.WriteBoolean("autoRestartMidMatchCrash", model.AutoRestartMidMatchCrash);
            writer.WriteBoolean("pureCompatibilityMode", model.PureCompatibilityMode);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static List<string> SanitizeActiveModList(string? activeModString)
    {
        if (string.IsNullOrWhiteSpace(activeModString))
        {
            return new List<string>();
        }

        string normalizedInput = activeModString.Trim();
        if (normalizedInput.StartsWith("--mod ", StringComparison.OrdinalIgnoreCase))
        {
            normalizedInput = normalizedInput["--mod ".Length..].Trim();
        }
        else if (normalizedInput.StartsWith("-mod ", StringComparison.OrdinalIgnoreCase))
        {
            normalizedInput = normalizedInput["-mod ".Length..].Trim();
        }
        normalizedInput = normalizedInput.Trim('"', '\'');

        HashSet<string> standardMods = new(StringComparer.OrdinalIgnoreCase)
        {
            "base",
            "game",
            "ClientPackageFiles",
            "../shared",
            "shared",
            "KONGOR/configuration",
            "KONGOR/updates",
            "KONGOR/extensions",
            "override",
            "editor",
            "KONGOR"
        };

        List<string> safeMods = new();
        int customIdx = 0;
        foreach (string rawToken in normalizedInput.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string normalizedMod = rawToken.Trim('"', '\'').Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalizedMod))
            {
                continue;
            }

            if (standardMods.Contains(normalizedMod))
            {
                safeMods.Add(normalizedMod);
            }
            else
            {
                customIdx++;
                safeMods.Add(customIdx == 1 ? "[CUSTOM_MOD]" : $"[CUSTOM_MOD_{customIdx}]");
            }
        }

        return safeMods;
    }

    private static string FilterSocialLinesAndRelativizeClocks(
        string rawLogText,
        bool dropSocialChatLines,
        bool relativizeBracketClocks)
    {
        if (string.IsNullOrEmpty(rawLogText))
        {
            return string.Empty;
        }

        Regex socialChatRegex = new(
            @"(?i)(?:\[(?:ALL|TEAM|SPEC|SPECTATOR|WHISPER|CLAN|PARTY|MENTOR|OFFICER|ALL_CHAT_MSG)\]|\b(?:ChatClient|Whisper\s+from|Whisper\s+to|BuddyList|ClanRoster|LobbyRoster|VoiceClient)\b)",
            RegexOptions.Compiled);
        Regex bracketClockRegex = new(@"\[(\d{2}):(\d{2}):(\d{2})(\.\d{1,6})?\]", RegexOptions.Compiled);

        int? originSeconds = null;
        StringBuilder sb = new(rawLogText.Length);
        string[] lines = rawLogText.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (dropSocialChatLines && socialChatRegex.IsMatch(line))
            {
                continue;
            }

            if (relativizeBracketClocks)
            {
                line = bracketClockRegex.Replace(line, m =>
                {
                    int hh = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    int mm = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    int ss = int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (hh > 23 || mm > 59 || ss > 59)
                    {
                        return m.Value;
                    }

                    int totalSec = (hh * 3600) + (mm * 60) + ss;
                    originSeconds ??= totalSec;
                    int delta = (totalSec - originSeconds.Value + 86400) % 86400;
                    int dH = delta / 3600;
                    int dM = (delta % 3600) / 60;
                    int dS = delta % 60;
                    string frac = m.Groups[4].Success ? m.Groups[4].Value : string.Empty;
                    return $"[+{dH:D2}:{dM:D2}:{dS:D2}{frac}]";
                });
            }

            sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static string? LocateLatestLinuxSignalCrashLog(string clientRoot, DateTimeOffset? minWriteTimeUtc = null)
    {
        DateTime? minUtc = minWriteTimeUtc?.UtcDateTime;
        FileInfo? newest = null;

        foreach (string dir in GetClientCrashDumpDirectories(clientRoot))
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir, "crash_*.log", SearchOption.TopDirectoryOnly))
                {
                    FileInfo fi = new(file);
                    if (!fi.Exists || fi.Length <= 0) continue;
                    if (minUtc.HasValue && fi.LastWriteTimeUtc < minUtc.Value) continue;
                    if (newest == null || fi.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                    {
                        newest = fi;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Linux signal crash log lookup notice for '{dir}': {ex.Message}");
            }
        }

        return newest?.FullName;
    }

    private static MinidumpTriageResult? TryParseLinuxSignalCrashLog(string crashLogPath)
    {
        try
        {
            string content = ReadBoundedLogText(crashLogPath, isUtf16Possible: false, maxTailLines: 400, maxTailBytes: 128 * 1024);
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            Match sigMatch = Regex.Match(content, @"(?im)^\s*Signal\s*:\s*([^\r\n]+)");
            string signalText = sigMatch.Success ? sigMatch.Groups[1].Value.Trim() : "SIGSEGV (Linux Signal Crash)";

            Dictionary<string, string> regs = new(StringComparer.OrdinalIgnoreCase);
            foreach (Match regMatch in Regex.Matches(content, @"\b(RIP|RSP|RBP|RAX|RBX|RCX|RDX|RSI|RDI|R8|R9|R10|R11|R12|R13|R14|R15)\s*[:=]\s*(0x[0-9A-Fa-f]+|[0-9A-Fa-f]{8,16})"))
            {
                regs[regMatch.Groups[1].Value.ToUpperInvariant()] = regMatch.Groups[2].Value;
            }

            Match frameMatch = Regex.Match(content, @"(?im)^\s*(?:#0|\[0\]|0:)?\s*([^\s\(\[]+\.(?:so(?:\.[0-9]+)*)|hon-x86_64)[^\r\n]*?(?:\+|\()?(0x[0-9A-Fa-f]+)?");
            string faultModule = frameMatch.Success && !string.IsNullOrWhiteSpace(frameMatch.Groups[1].Value)
                ? Path.GetFileName(frameMatch.Groups[1].Value)
                : "hon-x86_64";
            string faultRva = frameMatch.Success && !string.IsNullOrWhiteSpace(frameMatch.Groups[2].Value)
                ? frameMatch.Groups[2].Value
                : (regs.TryGetValue("RIP", out string? ripVal) ? ripVal : "0x0");

            return new MinidumpTriageResult(
                IsValidDump: true,
                Status: "LINUX_SIGNAL_CRASH_LOG",
                Architecture: "x64",
                ExceptionCodeHex: signalText,
                ExceptionName: signalText,
                FaultingAddress: 0UL,
                FaultingModule: faultModule,
                FaultingRva: faultRva,
                TargetFormatted: $"{faultModule}+{faultRva}",
                FaultType: $"Linux Native Signal Crash ({signalText})",
                AccessViolationKind: null,
                AccessViolationTargetHex: null,
                CppExceptionTypeName: null,
                CppExceptionWhatMessage: null,
                Registers: regs,
                LoadedModules: [],
                StreamCount: 0U);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Linux signal crash parse notice: {ex.Message}");
            return null;
        }
    }

    private static string? TryExtractMissingPosixSharedLibrary(uint? processExitCode, string? capturedStderr)
    {
        if (!string.IsNullOrWhiteSpace(capturedStderr))
        {
            Match linuxMatch = Regex.Match(
                capturedStderr,
                @"error while loading shared libraries:\s*([^\s:]+)\s*:",
                RegexOptions.IgnoreCase);
            if (linuxMatch.Success)
            {
                return linuxMatch.Groups[1].Value.Trim();
            }

            Match macMatch = Regex.Match(
                capturedStderr,
                @"Library not loaded:\s*([^\r\n]+)",
                RegexOptions.IgnoreCase);
            if (macMatch.Success)
            {
                return Path.GetFileName(macMatch.Groups[1].Value.Trim());
            }
        }

        if (processExitCode == 127U)
        {
            return "unknown_shared_lib";
        }

        return null;
    }

    private static bool IsLogRelevantForCrashTriage(
        string? logPath,
        string? resolvedCrashDumpPath,
        DateTimeOffset? minDumpWriteTimeUtc)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return false;
        }

        try
        {
            DateTime logWriteUtc = File.GetLastWriteTimeUtc(logPath);
            if (minDumpWriteTimeUtc.HasValue && logWriteUtc < minDumpWriteTimeUtc.Value.UtcDateTime.AddSeconds(-30))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(resolvedCrashDumpPath) && File.Exists(resolvedCrashDumpPath))
            {
                DateTime dumpWriteUtc = File.GetLastWriteTimeUtc(resolvedCrashDumpPath);
                if (logWriteUtc < dumpWriteUtc.AddHours(-2) || logWriteUtc > dumpWriteUtc.AddMinutes(5))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Log timestamp relevance check notice for '{logPath}': {ex.Message}");
            return true;
        }
    }

    private static List<string> ExtractDxvkEnumeratedAdapterNames(string dxvkSnippet)
    {
        List<string> names = [];
        if (string.IsNullOrWhiteSpace(dxvkSnippet))
        {
            return names;
        }

        foreach (Match match in Regex.Matches(dxvkSnippet, @"(?im)^\s*(?:info:\s*)?([^\r\n:]+):\s*\r?$"))
        {
            string candidate = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(candidate) &&
                !candidate.StartsWith("DXVK", StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith("Built-in", StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith("Effective", StringComparison.OrdinalIgnoreCase) &&
                !names.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(candidate);
            }
        }

        foreach (Match match in Regex.Matches(dxvkSnippet, @"(?im)Adapter\s+\d+\s*:\s*([^\r\n]+)"))
        {
            string candidate = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(candidate) && !names.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(candidate);
            }
        }

        return names;
    }

    private static List<string> DetectThirdPartyGraphicsHooks(
        MinidumpTriageResult? triageResult,
        string dxvkSnippet,
        string spySnippet)
    {
        List<string> detected = [];
        if (triageResult?.LoadedModules != null)
        {
            foreach (string mod in triageResult.LoadedModules)
            {
                string fileName = Path.GetFileName(mod);
                foreach (string knownHook in KnownThirdPartyGraphicsHooks)
                {
                    if (string.Equals(fileName, knownHook, StringComparison.OrdinalIgnoreCase) &&
                        !detected.Contains(knownHook, StringComparer.OrdinalIgnoreCase))
                    {
                        detected.Add(knownHook);
                    }
                }
            }
        }

        string combinedLogs = $"{dxvkSnippet}\n{spySnippet}";
        foreach (string knownHook in KnownThirdPartyGraphicsHooks)
        {
            if (combinedLogs.Contains(knownHook, StringComparison.OrdinalIgnoreCase) &&
                !detected.Contains(knownHook, StringComparer.OrdinalIgnoreCase))
            {
                detected.Add(knownHook);
            }
        }

        return detected;
    }

    public static string TryReadBuildId(string clientRoot)
    {
        if (!string.IsNullOrWhiteSpace(clientRoot))
        {
            string manifest = Path.Combine(clientRoot, "build_manifest.json");
            if (File.Exists(manifest))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (doc.RootElement.TryGetProperty("buildId", out JsonElement bEl))
                    {
                        string? fromManifest = bEl.GetString();
                        if (!string.IsNullOrWhiteSpace(fromManifest))
                        {
                            return fromManifest;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] TryReadBuildId notice: {ex.Message}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(GeneratedVersionInformation.VersionString))
        {
            return GeneratedVersionInformation.VersionString;
        }

        Version? asmVersion = typeof(DiagnosticBundleService).Assembly.GetName().Version;
        return asmVersion != null ? $"{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}" : "5.0.2";
    }

    private static string GenerateCrashSummaryMarkdown(
        MinidumpTriageResult? triage,
        string reportType,
        string buildId,
        bool hasLocalFullDump,
        IReadOnlyList<string> entries,
        DateTimeOffset nowUtc,
        uint? processExitCode = null,
        string? remediationNote = null,
        IReadOnlyList<string>? detectedThirdPartyHooks = null,
        string? minidumpOmittedNotice = null,
        string? finalConsoleErrorLine = null,
        string? silentExitZeroReason = null,
        string? emptyHangDumpNotice = null,
        IReadOnlyList<string>? canarySurgicalRedactions = null,
        IReadOnlyList<string>? omittedEntries = null)
    {
        StringBuilder sb = new();
        sb.AppendLine("# Project KONGOR Diagnostic & Crash Triage Report");
        sb.AppendLine();
        sb.AppendLine($"* **Report Generated (UTC):** `{nowUtc:yyyy-MM-dd HH:mm:ss}`");
        sb.AppendLine($"* **Report Type:** `{reportType}`");
        sb.AppendLine($"* **Build ID:** `{buildId}`");
        sb.AppendLine($"* **OS:** `{RuntimeInformation.OSDescription}` ({RuntimeInformation.ProcessArchitecture})");
        if (processExitCode.HasValue)
        {
            sb.AppendLine($"* **Process Exit Code:** `0x{processExitCode.Value:X8}` (`{processExitCode.Value}`)");
        }
        sb.AppendLine();

        sb.AppendLine("## 1. Crash / Fault Classification");
        if (triage != null && triage.IsValidDump)
        {
            sb.AppendLine($"* **Target:** `{triage.TargetFormatted}`");
            sb.AppendLine($"* **Exception Code:** `{triage.ExceptionCodeHex}` ({triage.ExceptionName})");
            sb.AppendLine($"* **Fault Type:** {triage.FaultType}");
            if (!string.IsNullOrEmpty(triage.AccessViolationKind))
            {
                string avAddr = string.IsNullOrWhiteSpace(triage.AccessViolationTargetHex)
                    ? "0x0"
                    : (triage.AccessViolationTargetHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? triage.AccessViolationTargetHex
                        : $"0x{triage.AccessViolationTargetHex}");
                sb.AppendLine($"* **Access Violation:** `{triage.AccessViolationKind}` on `{avAddr}`");
            }
            if (!string.IsNullOrEmpty(triage.CppExceptionTypeName))
            {
                sb.AppendLine($"* **C++ RTTI Type:** `{triage.CppExceptionTypeName}`");
            }
            if (!string.IsNullOrWhiteSpace(triage.CppExceptionWhatMessage))
            {
                sb.AppendLine($"* **C++ Exception Detail:** `{triage.CppExceptionWhatMessage}`");
            }
        }
        else if (string.Equals(reportType, "CrashReport", StringComparison.OrdinalIgnoreCase))
        {
            string codeStr = processExitCode.HasValue ? $"0x{processExitCode.Value:X8} ({processExitCode.Value})" : "Abnormal process exit";
            sb.AppendLine($"* **Crash Exit Detected:** `{codeStr}` (no binary MDMP minidump was produced before process exit; inspect `logs/console.tail.log`, `logs/captured_stderr.log`, and `logs/hon_x64_d3d9.log`).");
        }
        else
        {
            sb.AppendLine("* **Notice:** Manual triage snapshot or live hang capture (no unhandled fatal exception occurred).");
        }

        if (processExitCode == 0U && !string.IsNullOrWhiteSpace(silentExitZeroReason))
        {
            sb.AppendLine($"* **Silent ExitCode 0 Reason:** {silentExitZeroReason}");
        }

        if (!string.IsNullOrWhiteSpace(remediationNote))
        {
            sb.AppendLine($"* **Actionable Triage & Remediation:** {remediationNote}");
        }

        if (!string.IsNullOrWhiteSpace(finalConsoleErrorLine))
        {
            sb.AppendLine($"* **Final Console Error / Fatal Line:** `{finalConsoleErrorLine}`");
        }

        if (detectedThirdPartyHooks is { Count: > 0 })
        {
            List<string> displayHooks = detectedThirdPartyHooks
                .Select(h => string.Equals(h, triage?.FaultingModule, StringComparison.OrdinalIgnoreCase)
                    ? h
                    : "External Graphics/Overlay Hook DLL")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            sb.AppendLine($"* **Third-Party Graphics / Vulkan Hooks Detected:** `{string.Join("`, `", displayHooks)}` — External overlay/recording hooks can corrupt Vulkan swapchain transitions; keep `Allow Third-Party Vulkan Overlays` OFF in WILLOWMAKER.");
        }
        sb.AppendLine();

        sb.AppendLine("## 2. Registers & Call Context");
        if (triage?.Registers != null && triage.Registers.Count > 0)
        {
            sb.AppendLine("```text");
            foreach (KeyValuePair<string, string> reg in triage.Registers.OrderBy(k => k.Key))
            {
                sb.AppendLine($"{reg.Key,-4} = {reg.Value}");
            }
            sb.AppendLine("```");
        }
        else
        {
            sb.AppendLine("*No active register frame captured.*");
        }
        sb.AppendLine();

        sb.AppendLine("## 3. Preserved Local Artifacts");
        if (hasLocalFullDump)
        {
            sb.AppendLine("> [!NOTE]");
            sb.AppendLine("> A complete full-memory dump is preserved safely on the local disk (`[LOCAL_CRASH_DIR]`).");
            sb.AppendLine("> Full paths and dump UUIDs are omitted from this Discord zip to protect tester privacy.");
        }
        else
        {
            sb.AppendLine("*No separate full-memory dump was detected.*");
        }

        if (!string.IsNullOrWhiteSpace(emptyHangDumpNotice))
        {
            sb.AppendLine();
            sb.AppendLine($"* **Hang Snapshot Diagnostic Note:** {emptyHangDumpNotice}");
        }

        if (!string.IsNullOrWhiteSpace(minidumpOmittedNotice))
        {
            sb.AppendLine();
            sb.AppendLine($"* **Privacy Gate Notice:** {minidumpOmittedNotice}");
        }

        if (canarySurgicalRedactions is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"* **Zero-PII Canary Surgical Line Redactions:** `{string.Join("`, `", canarySurgicalRedactions)}`");
        }

        if (omittedEntries is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"* **Omitted Entries:** `{string.Join("`, `", omittedEntries)}`");
        }
        sb.AppendLine();

        return sb.ToString();
    }

    private static string GenerateTransparencyNotice(
        int secrets,
        int userIds,
        int peerIps,
        IReadOnlyList<string> files,
        bool hasLocalFullDump,
        string? minidumpOmittedNotice = null)
    {
        StringBuilder sb = new();
        sb.AppendLine("================================================================================");
        sb.AppendLine("PROJECT KONGOR - PRIVACY & REDACTION AUDIT NOTICE");
        sb.AppendLine("================================================================================");
        sb.AppendLine();
        sb.AppendLine("This diagnostic bundle was generated locally on your machine at your request.");
        sb.AppendLine("All included files have been sanitized by WILLOWMAKER's Zero-PII Privacy Firewall:");
        sb.AppendLine();
        sb.AppendLine($"  [✓] Passwords, Session Cookies & Tokens Redacted : {secrets}");
        sb.AppendLine($"  [✓] Paths, Nicknames, UUIDs & Devices Scrubbed  : {userIds}");
        sb.AppendLine($"  [✓] External Peer IPv4/IPv6 Addresses Redacted  : {peerIps}");
        sb.AppendLine();
        sb.AppendLine("PACKAGED FILES IN THIS ARCHIVE:");
        foreach (string file in files)
        {
            sb.AppendLine($"  - {file}");
        }
        sb.AppendLine();
        if (hasLocalFullDump)
        {
            sb.AppendLine("LOCAL FULL-MEMORY DUMP: Preserved strictly on local disk (path & UUID omitted). Never uploaded.");
        }
        if (!string.IsNullOrWhiteSpace(minidumpOmittedNotice))
        {
            sb.AppendLine($"MINIDUMP PRIVACY POLICY: {minidumpOmittedNotice}");
        }
        sb.AppendLine();
        sb.AppendLine("No data was uploaded to any server. You control sharing this .zip file.");
        sb.AppendLine("================================================================================");
        return sb.ToString();
    }

    /// <summary>
    /// Enforces a 1.0 GB local crash storage cap on standalone tester installs while
    /// strictly skipping developer .git checkouts and protecting the newest unacknowledged crash dump.
    /// </summary>
    public static int EnforceLocalCrashRetention(
        string clientRoot,
        DateTimeOffset? lastAcknowledgedUtc = null,
        long maxTotalBytes = DefaultCrashRetentionCapBytes)
    {
        if (string.IsNullOrWhiteSpace(clientRoot) || !Directory.Exists(clientRoot))
        {
            return 0;
        }

        if (IsGitDeveloperWorkspace(clientRoot))
        {
            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Skipping automatic crash pruning inside .git developer workspace '{clientRoot}'.");
            return 0;
        }

        try
        {
            IReadOnlyList<string> candidateDirs = GetClientCrashDumpDirectories(clientRoot);

            List<FileInfo> allDumps = new();
            foreach (string dir in candidateDirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string dmpPath in Directory.EnumerateFiles(dir, "*.dmp", SearchOption.TopDirectoryOnly))
                {
                    try { allDumps.Add(new FileInfo(dmpPath)); }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Retention stat notice: {ex.Message}"); }
                }
            }

            if (allDumps.Count <= 1) return 0;

            long totalBytes = allDumps.Sum(f => f.Length);
            if (totalBytes <= maxTotalBytes) return 0;

            List<FileInfo> newestFirst = allDumps.OrderByDescending(f => f.LastWriteTimeUtc).ToList();

            // Always protect the newest crash pair (both tier-1 .dmp and matching .rich.dmp if unacknowledged)
            HashSet<string> protectedPaths = new(StringComparer.OrdinalIgnoreCase);
            FileInfo newest = newestFirst[0];
            bool newestIsUnacknowledged = !lastAcknowledgedUtc.HasValue ||
                                          new DateTimeOffset(newest.LastWriteTimeUtc, TimeSpan.Zero) > lastAcknowledgedUtc.Value;

            if (newestIsUnacknowledged)
            {
                protectedPaths.Add(newest.FullName);
                string stem = newest.Name.EndsWith(".rich.dmp", StringComparison.OrdinalIgnoreCase)
                    ? newest.Name[..^".rich.dmp".Length]
                    : Path.GetFileNameWithoutExtension(newest.Name);

                foreach (FileInfo companion in newestFirst.Where(f => f.Name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)))
                {
                    protectedPaths.Add(companion.FullName);
                }
            }
            else
            {
                // Even if acknowledged, keep the single newest tier-1 minidump for quick reference
                FileInfo? newestTier1 = newestFirst.FirstOrDefault(f => !f.Name.EndsWith(".rich.dmp", StringComparison.OrdinalIgnoreCase));
                if (newestTier1 != null) protectedPaths.Add(newestTier1.FullName);
            }

            int deletedCount = 0;
            // Prune oldest first until totalBytes <= maxTotalBytes
            foreach (FileInfo candidate in newestFirst.AsEnumerable().Reverse())
            {
                if (totalBytes <= maxTotalBytes) break;
                if (protectedPaths.Contains(candidate.FullName)) continue;

                try
                {
                    long len = candidate.Length;
                    candidate.Delete();
                    totalBytes -= len;
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Could not prune old crash dump '{candidate.FullName}': {ex.Message}");
                }
            }

            return deletedCount;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] EnforceLocalCrashRetention warning: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Enforces a bounded count of exported <c>Kongor_Diag_*.zip</c> bundles (default 25) in standalone
    /// playtest installations while strictly skipping developer <c>.git</c> checkouts.
    /// </summary>
    public static int EnforceExportedReportZipRetention(
        string reportOutputDir,
        string? clientRoot = null,
        int maxRetainedZips = DefaultMaxRetainedReportZips)
    {
        if (string.IsNullOrWhiteSpace(reportOutputDir) || !Directory.Exists(reportOutputDir) || maxRetainedZips < 1)
        {
            return 0;
        }

        if (IsGitDeveloperWorkspace(reportOutputDir) ||
            (!string.IsNullOrWhiteSpace(clientRoot) && IsGitDeveloperWorkspace(clientRoot)))
        {
            System.Diagnostics.Trace.WriteLine(
                $"[DIAGNOSTICS] Skipping exported report zip pruning inside .git developer workspace '{reportOutputDir}'.");
            return 0;
        }

        try
        {
            List<FileInfo> zips = new();
            foreach (string path in Directory.EnumerateFiles(reportOutputDir, "Kongor_Diag_*.zip", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    zips.Add(new FileInfo(path));
                }
                catch (Exception statEx)
                {
                    System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Report zip retention stat notice for '{path}': {statEx.Message}");
                }
            }

            if (zips.Count <= maxRetainedZips)
            {
                return 0;
            }

            List<FileInfo> orderedNewestFirst = zips
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .ThenByDescending(static f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int deletedCount = 0;
            for (int i = maxRetainedZips; i < orderedNewestFirst.Count; i++)
            {
                FileInfo staleZip = orderedNewestFirst[i];
                try
                {
                    staleZip.Delete();
                    deletedCount++;
                    System.Diagnostics.Trace.WriteLine(
                        $"[DIAGNOSTICS] Pruned old diagnostic zip '{staleZip.Name}' (keeping newest {maxRetainedZips}).");
                }
                catch (Exception delEx)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[DIAGNOSTICS] Could not prune old diagnostic zip '{staleZip.FullName}': {delEx.Message}");
                }
            }

            return deletedCount;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] EnforceExportedReportZipRetention warning: {ex.Message}");
            return 0;
        }
    }

    public static bool IsGitDeveloperWorkspace(string startDirectory)
    {
        try
        {
            DirectoryInfo? current = new(Path.GetFullPath(startDirectory));
            while (current != null)
            {
                string gitMarker = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
                {
                    return true;
                }
                current = current.Parent;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Git workspace check notice: {ex.Message}");
        }
        return false;
    }

    private static string ReadConsoleDiagnosticText(string filePath, int headerLines, int maxTailLines, int maxScanBytes)
    {
        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length == 0) return string.Empty;

        bool isUtf16Le = false;
        if (fs.Length >= 2)
        {
            byte[] bom = new byte[2];
            int readBom = fs.Read(bom, 0, 2);
            isUtf16Le = readBom == 2 && bom[0] == 0xFF && bom[1] == 0xFE;
        }

        Encoding encoding = isUtf16Le ? Encoding.Unicode : Encoding.UTF8;

        // Read startup header (CPU/OS/GPU/Vulkan adapter & initial CVars)
        fs.Seek(0, SeekOrigin.Begin);
        List<string> header = new(headerLines);
        using (StreamReader headerReader = new(fs, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
        {
            if (fs.Length <= maxScanBytes)
            {
                List<string> allLines = new();
                string? fullLine;
                while ((fullLine = headerReader.ReadLine()) != null)
                {
                    allLines.Add(fullLine);
                }

                List<string> collapsedAll = CollapseConsecutiveIdenticalLines(allLines);
                if (collapsedAll.Count <= headerLines + maxTailLines)
                {
                    return string.Join(Environment.NewLine, collapsedAll);
                }

                List<string> collapsedHeader = collapsedAll.Take(headerLines).ToList();
                List<string> remaining = collapsedAll.Skip(headerLines).ToList();
                List<string> midDiagnostics = remaining
                    .Take(Math.Max(0, remaining.Count - maxTailLines))
                    .Where(IsDiagnosticErrorOrWarningLine)
                    .TakeLast(400)
                    .ToList();
                IEnumerable<string> tail = remaining.Skip(Math.Max(0, remaining.Count - maxTailLines));

                return CombineConsoleSections(collapsedHeader, midDiagnostics, tail);
            }

            string? line;
            while (header.Count < headerLines && (line = headerReader.ReadLine()) != null)
            {
                header.Add(line);
            }
        }

        header = CollapseConsecutiveIdenticalLines(header);

        // Large console.log (> 16 MB): scan bounded 16 MB tail window aligned to UTF-16LE boundary
        long seekOffset = fs.Length - maxScanBytes;
        if (isUtf16Le && (seekOffset % 2 != 0))
        {
            seekOffset++;
        }

        fs.Seek(seekOffset, SeekOrigin.Begin);
        using StreamReader tailReader = new(fs, encoding, detectEncodingFromByteOrderMarks: false);
        _ = tailReader.ReadLine(); // Discard partial line

        List<string> windowLines = new();
        string? wLine;
        while ((wLine = tailReader.ReadLine()) != null)
        {
            windowLines.Add(wLine);
        }

        windowLines = CollapseConsecutiveIdenticalLines(windowLines);

        int tailStart = Math.Max(0, windowLines.Count - maxTailLines);
        List<string> scannedDiagnostics = windowLines
            .Take(tailStart)
            .Where(IsDiagnosticErrorOrWarningLine)
            .TakeLast(400)
            .ToList();
        IEnumerable<string> finalTail = windowLines.Skip(tailStart);

        return CombineConsoleSections(header, scannedDiagnostics, finalTail);
    }

    public static List<string> CollapseConsecutiveIdenticalLines(IReadOnlyList<string> lines)
    {
        List<string> result = new(lines.Count);
        int i = 0;
        while (i < lines.Count)
        {
            string current = lines[i];
            if (string.IsNullOrWhiteSpace(current))
            {
                result.Add(current);
                i++;
                continue;
            }

            int runEnd = i + 1;
            while (runEnd < lines.Count && string.Equals(lines[runEnd], current, StringComparison.Ordinal))
            {
                runEnd++;
            }

            int runLength = runEnd - i;
            if (runLength >= 5)
            {
                result.Add(current);
                result.Add(lines[i + 1]);
                result.Add(lines[i + 2]);
                result.Add($"... [suppressed {runLength - 4} identical lines] ...");
                result.Add(lines[runEnd - 1]);
            }
            else
            {
                for (int k = i; k < runEnd; k++)
                {
                    result.Add(lines[k]);
                }
            }

            i = runEnd;
        }

        return result;
    }

    private static bool IsDiagnosticErrorOrWarningLine(string line) =>
        line.Contains("Err", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Warn", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Fatal", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Crash", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Exception", StringComparison.OrdinalIgnoreCase);

    private static string CombineConsoleSections(
        IReadOnlyList<string> header,
        IReadOnlyList<string> midDiagnostics,
        IEnumerable<string> tail)
    {
        StringBuilder sb = new();
        sb.AppendLine("=== [KONGOR DIAGNOSTICS: STARTUP HEADER] ===");
        foreach (string h in header) sb.AppendLine(h);

        if (midDiagnostics.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"=== [KONGOR DIAGNOSTICS: {midDiagnostics.Count} MID-SESSION ERR/WARN EVENTS] ===");
            foreach (string m in midDiagnostics) sb.AppendLine(m);
        }

        sb.AppendLine();
        sb.AppendLine("=== [KONGOR DIAGNOSTICS: RECENT CONSOLE TAIL] ===");
        foreach (string t in tail) sb.AppendLine(t);
        return sb.ToString();
    }

    private static string? LocateLatestMacIpsReport(string clientRoot)
    {
        try
        {
            List<string> candidateDirs = new()
            {
                Path.Combine(clientRoot, "DiagnosticReports")
            };

            if (OperatingSystem.IsMacOS())
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(home))
                {
                    candidateDirs.Add(Path.Combine(home, "Library", "Logs", "DiagnosticReports"));
                }
            }

            foreach (string dir in candidateDirs)
            {
                if (!Directory.Exists(dir)) continue;
                string? ips = Directory.EnumerateFiles(dir, "*.ips", SearchOption.TopDirectoryOnly)
                    .Where(f =>
                    {
                        string name = Path.GetFileName(f);
                        return name.Contains("hon", StringComparison.OrdinalIgnoreCase) ||
                               name.Contains("newerth", StringComparison.OrdinalIgnoreCase) ||
                               name.Contains("willowmaker", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();

                if (ips != null) return ips;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] macOS .ips lookup notice: {ex.Message}");
        }
        return null;
    }

    private static byte[] TrimZipToCap(byte[] originalZip, long maxBytes, DateTimeOffset utcEntryTimestamp)
    {
        if (originalZip.Length <= maxBytes) return originalZip;

        // Load all archive entries into memory so we can apply deterministic priority eviction
        Dictionary<string, byte[]> entries = new(StringComparer.OrdinalIgnoreCase);
        using (MemoryStream srcMs = new(originalZip, writable: false))
        using (ZipArchive srcZip = new(srcMs, ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry entry in srcZip.Entries)
            {
                using Stream es = entry.Open();
                using MemoryStream copyMs = new();
                es.CopyTo(copyMs);
                entries[entry.FullName] = copyMs.ToArray();
            }
        }

        List<string> evictedItems = new();

        // Step 1: Drop WILLOWMAKER.tail.log
        if (entries.Remove("logs/WILLOWMAKER.tail.log"))
        {
            evictedItems.Add("logs/WILLOWMAKER.tail.log (evicted to stay <= 8.0 MB)");
            byte[] candidate = RebuildZipBytes(entries, evictedItems, utcEntryTimestamp);
            if (candidate.Length <= maxBytes) return candidate;
        }

        // Step 2: Drop secondary debug logs (DXVK, Vulkan layer/spy, & client_lua_debug)
        bool droppedSecondary = false;
        if (entries.Remove("logs/hon_x64_d3d9.log")) { evictedItems.Add("logs/hon_x64_d3d9.log"); droppedSecondary = true; }
        if (entries.Remove("logs/vid_vulkan_spy.prev.log")) { evictedItems.Add("logs/vid_vulkan_spy.prev.log"); droppedSecondary = true; }
        if (entries.Remove("logs/hon_vulkan_layer.log.bak")) { evictedItems.Add("logs/hon_vulkan_layer.log.bak"); droppedSecondary = true; }
        if (entries.Remove("logs/client_lua_debug.log")) { evictedItems.Add("logs/client_lua_debug.log"); droppedSecondary = true; }
        if (droppedSecondary)
        {
            byte[] candidate = RebuildZipBytes(entries, evictedItems, utcEntryTimestamp);
            if (candidate.Length <= maxBytes) return candidate;
        }

        // Step 3: Halve console.tail.log
        if (entries.TryGetValue("logs/console.tail.log", out byte[]? consoleBytes) && consoleBytes.Length > 64 * 1024)
        {
            string text = Encoding.UTF8.GetString(consoleBytes);
            string[] lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
            if (lines.Length > 200)
            {
                IEnumerable<string> trimmedLines = lines.Take(80)
                    .Append("... [MIDDLE TRIMMED TO MEET 8.0 MB DISCORD UPLOAD LIMIT] ...")
                    .Concat(lines.Skip(lines.Length - Math.Max(100, lines.Length / 3)));
                entries["logs/console.tail.log"] = Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, trimmedLines));
                evictedItems.Add("logs/console.tail.log (trimmed middle lines)");
                byte[] candidate = RebuildZipBytes(entries, evictedItems, utcEntryTimestamp);
                if (candidate.Length <= maxBytes) return candidate;
            }
        }

        // Step 4: Drop embedded minidump_*.dmp (remains preserved on tester's local disk!)
        string? dmpKey = entries.Keys.FirstOrDefault(k => k.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
        if (dmpKey != null && entries.Remove(dmpKey))
        {
            evictedItems.Add($"{dmpKey} (kept locally on disk; omitted from zip to stay <= 8.0 MB)");
            byte[] candidate = RebuildZipBytes(entries, evictedItems, utcEntryTimestamp);
            if (candidate.Length <= maxBytes) return candidate;
        }

        // Step 5: Final hard clamp on any remaining large log/config entry (e.g. high-entropy stress payloads)
        foreach (string key in entries.Keys.Where(k => k.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) || k.StartsWith("config/", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (entries[key].Length > 64 * 1024)
            {
                byte[] clamped = new byte[64 * 1024];
                Buffer.BlockCopy(entries[key], entries[key].Length - clamped.Length, clamped, 0, clamped.Length);
                entries[key] = clamped;
                evictedItems.Add($"{key} (clamped to final 64 KB)");
            }
        }

        return RebuildZipBytes(entries, evictedItems, utcEntryTimestamp);
    }

    private static byte[] RebuildZipBytes(Dictionary<string, byte[]> entries, IReadOnlyList<string> evictedItems, DateTimeOffset utcEntryTimestamp)
    {
        if (evictedItems.Count > 0)
        {
            string notice = Environment.NewLine + Environment.NewLine +
                            "## Size-Cap Priority Eviction Notice (<= 8.0 MB)" + Environment.NewLine +
                            string.Join(Environment.NewLine, evictedItems.Select(i => $"* `{i}`")) + Environment.NewLine;

            if (entries.TryGetValue("CRASH_SUMMARY.md", out byte[]? summaryBytes))
            {
                string existing = Encoding.UTF8.GetString(summaryBytes);
                int markerIdx = existing.IndexOf("## Size-Cap Priority Eviction Notice", StringComparison.Ordinal);
                if (markerIdx >= 0) existing = existing[..markerIdx].TrimEnd();
                entries["CRASH_SUMMARY.md"] = Encoding.UTF8.GetBytes(existing + notice);
            }

            if (entries.TryGetValue("README_WHAT_IS_SHARED.txt", out byte[]? readmeBytes))
            {
                string existing = Encoding.UTF8.GetString(readmeBytes);
                int markerIdx = existing.IndexOf("SIZE-CAP EVICTION:", StringComparison.Ordinal);
                if (markerIdx >= 0) existing = existing[..markerIdx].TrimEnd();
                entries["README_WHAT_IS_SHARED.txt"] = Encoding.UTF8.GetBytes(
                    existing + Environment.NewLine + "SIZE-CAP EVICTION: " + string.Join("; ", evictedItems) + Environment.NewLine);
            }
        }

        using MemoryStream outMs = new();
        using (ZipArchive outZip = new(outMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (KeyValuePair<string, byte[]> kv in entries)
            {
                ZipArchiveEntry e = outZip.CreateEntry(kv.Key, CompressionLevel.Optimal);
                e.LastWriteTime = utcEntryTimestamp;
                using Stream s = e.Open();
                s.Write(kv.Value, 0, kv.Value.Length);
            }
        }
        return outMs.ToArray();
    }

    private static string BuildCallerSpecificTriageHint(string? callerModule, string? callerRva)
    {
        if (string.IsNullOrWhiteSpace(callerModule) || string.IsNullOrWhiteSpace(callerRva))
        {
            return string.Empty;
        }

        if (callerModule.Equals("vid_d3d9_x64.dll", StringComparison.OrdinalIgnoreCase) &&
            TryParseHexRva(callerRva, out ulong rva) &&
            rva == 0x5D7B7UL)
        {
            return " Known failure signature: `D3D_Start: CreateWindow() failed` — verify `vid_display` / `vid_resolution` match an active monitor, try `Borderless Windowed` or `Windowed` mode in WILLOWMAKER, check GPU/RDP display state, or toggle DXVK vs Compatibility Native D3D9.";
        }

        return string.Empty;
    }

    private static string? TryExtractFinalConsoleFatalOrErrorLine(
        string? consoleLogPath,
        string? resolvedCrashDumpPath,
        DateTimeOffset? minDumpWriteTimeUtc)
    {
        if (string.IsNullOrWhiteSpace(consoleLogPath) || !File.Exists(consoleLogPath))
        {
            return null;
        }

        if (!IsLogRelevantForCrashTriage(consoleLogPath, resolvedCrashDumpPath, minDumpWriteTimeUtc))
        {
            return null;
        }

        try
        {
            string snippet = ReadBoundedLogText(consoleLogPath, isUtf16Possible: true, maxTailLines: 600, maxTailBytes: 256 * 1024);
            if (string.IsNullOrWhiteSpace(snippet))
            {
                return null;
            }

            string[] lines = snippet.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string noColor = Regex.Replace(lines[i], @"\^(?:\d{3}|#[0-9a-fA-F]{6}|[a-zA-Z0-9*:\-~])", string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(noColor))
                {
                    continue;
                }

                if (!Regex.IsMatch(noColor, @"(?i)(?:^|[\s\]])(?:Fatal|Error)\s*:"))
                {
                    continue;
                }

                string filtered = FilterSocialLinesAndRelativizeClocks(noColor, dropSocialChatLines: true, relativizeBracketClocks: true).Trim();
                if (string.IsNullOrWhiteSpace(filtered))
                {
                    continue;
                }

                return filtered.Length > 512 ? filtered[..512] + "..." : filtered;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Final console error extraction notice: {ex.Message}");
        }

        return null;
    }

    private static bool TryExtractLatestForcedExitTelemetry(
        string root,
        string? resolvedCrashDumpPath,
        DateTimeOffset? minDumpWriteTimeUtc,
        int? expectedProcessId,
        bool requireTimestampedEntry,
        out string exitCodeHex,
        out string callerModule,
        out string callerRva,
        out string stackSummary)
    {
        exitCodeHex = string.Empty;
        callerModule = string.Empty;
        callerRva = string.Empty;
        stackSummary = string.Empty;

        string crashDir = Path.Combine(root, "logs", "crash");
        if (!Directory.Exists(crashDir))
        {
            return false;
        }

        List<string> candidateLogs = [Path.Combine(crashDir, "engine_crashes.log")];
        try
        {
            foreach (string exitLog in Directory.EnumerateFiles(crashDir, "exit_process_*.log")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                candidateLogs.Add(exitLog);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DIAGNOSTICS] Forced exit log enumeration notice in '{crashDir}': {ex.Message}");
        }

        foreach (string logPath in candidateLogs)
        {
            if (!File.Exists(logPath) || !IsLogRelevantForCrashTriage(logPath, resolvedCrashDumpPath, minDumpWriteTimeUtc))
            {
                continue;
            }

            string snippet = ReadBoundedLogText(logPath, isUtf16Possible: false, maxTailLines: 200, maxTailBytes: 128 * 1024);
            if (string.IsNullOrWhiteSpace(snippet))
            {
                continue;
            }

            string[] lines = snippet.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i];
                if (TryParseExitTelemetryLine(line, out exitCodeHex, out callerModule, out callerRva, out stackSummary, out DateTimeOffset? lineUtc, out int? linePid))
                {
                    if (expectedProcessId.HasValue && (!linePid.HasValue || linePid.Value != expectedProcessId.Value))
                    {
                        System.Diagnostics.Trace.WriteLine(
                            $"[DIAGNOSTICS] Skipping [EXIT_TELEMETRY] entry for mismatched PID (expected={expectedProcessId.Value}, found={linePid?.ToString() ?? "none"}).");
                        exitCodeHex = string.Empty;
                        callerModule = string.Empty;
                        callerRva = string.Empty;
                        stackSummary = string.Empty;
                        continue;
                    }

                    if (requireTimestampedEntry && !lineUtc.HasValue)
                    {
                        System.Diagnostics.Trace.WriteLine(
                            "[DIAGNOSTICS] Skipping legacy untimestamped [EXIT_TELEMETRY] entry because cold-boot/PID isolation requires UTC= timestamp.");
                        exitCodeHex = string.Empty;
                        callerModule = string.Empty;
                        callerRva = string.Empty;
                        stackSummary = string.Empty;
                        continue;
                    }

                    if (minDumpWriteTimeUtc.HasValue && lineUtc.HasValue && lineUtc.Value < minDumpWriteTimeUtc.Value)
                    {
                        exitCodeHex = string.Empty;
                        callerModule = string.Empty;
                        callerRva = string.Empty;
                        stackSummary = string.Empty;
                        continue;
                    }
                    return true;
                }
            }
        }

        return false;
    }

    public static bool TryParseExitTelemetryLine(
        string? line,
        out string exitCodeHex,
        out string callerModule,
        out string callerRva,
        out string stackSummary)
        => TryParseExitTelemetryLine(line, out exitCodeHex, out callerModule, out callerRva, out stackSummary, out _, out _);

    public static bool TryParseExitTelemetryLine(
        string? line,
        out string exitCodeHex,
        out string callerModule,
        out string callerRva,
        out string stackSummary,
        out DateTimeOffset? utcTimestamp)
        => TryParseExitTelemetryLine(line, out exitCodeHex, out callerModule, out callerRva, out stackSummary, out utcTimestamp, out _);

    public static bool TryParseExitTelemetryLine(
        string? line,
        out string exitCodeHex,
        out string callerModule,
        out string callerRva,
        out string stackSummary,
        out DateTimeOffset? utcTimestamp,
        out int? processId)
    {
        exitCodeHex = string.Empty;
        callerModule = string.Empty;
        callerRva = string.Empty;
        stackSummary = string.Empty;
        utcTimestamp = null;
        processId = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        const string marker = "[EXIT_TELEMETRY] Non-zero ExitProcess(";
        int markerIdx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx < 0)
        {
            return false;
        }

        int codeStart = markerIdx + marker.Length;
        int codeEnd = line.IndexOf(')', codeStart);
        if (codeEnd <= codeStart)
        {
            return false;
        }

        exitCodeHex = line[codeStart..codeEnd].Trim();

        const string utcKey = "UTC=";
        int utcIdx = line.IndexOf(utcKey, codeEnd, StringComparison.OrdinalIgnoreCase);
        if (utcIdx >= 0)
        {
            int utcValStart = utcIdx + utcKey.Length;
            int utcValEnd = line.IndexOf(' ', utcValStart);
            string utcToken = (utcValEnd > utcValStart ? line[utcValStart..utcValEnd] : line[utcValStart..]).Trim();
            if (DateTimeOffset.TryParse(utcToken, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsedUtc))
            {
                utcTimestamp = parsedUtc;
            }
        }

        const string pidKey = "PID=";
        int pidIdx = line.IndexOf(pidKey, codeEnd, StringComparison.OrdinalIgnoreCase);
        if (pidIdx >= 0)
        {
            int pidValStart = pidIdx + pidKey.Length;
            int pidValEnd = line.IndexOfAny([' ', '\t', '\r', '\n'], pidValStart);
            string pidToken = (pidValEnd > pidValStart ? line[pidValStart..pidValEnd] : line[pidValStart..]).Trim();
            if (int.TryParse(pidToken, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsedPid) && parsedPid > 0)
            {
                processId = parsedPid;
            }
        }

        const string callerKey = "PrimaryCaller=";
        int callerIdx = line.IndexOf(callerKey, codeEnd, StringComparison.OrdinalIgnoreCase);
        if (callerIdx < 0)
        {
            return false;
        }

        int callerValStart = callerIdx + callerKey.Length;
        int callerValEnd = line.IndexOf(' ', callerValStart);
        string callerToken = (callerValEnd > callerValStart ? line[callerValStart..callerValEnd] : line[callerValStart..]).Trim();
        ParseModuleAndRvaToken(callerToken, out callerModule, out callerRva);

        const string stackKey = "Stack=[";
        int stackIdx = line.IndexOf(stackKey, callerValStart, StringComparison.OrdinalIgnoreCase);
        if (stackIdx >= 0)
        {
            int stackValStart = stackIdx + stackKey.Length;
            int stackValEnd = line.IndexOf(']', stackValStart);
            if (stackValEnd > stackValStart)
            {
                stackSummary = line[stackValStart..stackValEnd].Trim();
            }
        }

        // Unwrap CSystem::Exit (k2_x64.dll+0x9FDB90..0x9FE320) & CSystem::Error (k2_x64.dll+0xA05300..0xA05440)
        // plus OS/CRT frames so real fatal callers (e.g. vid_d3d9_x64.dll+0x5D7B7) surface in zip slugs and summaries.
        if (IsForcedExitWrapperFrame(callerModule, callerRva) && !string.IsNullOrWhiteSpace(stackSummary))
        {
            string[] frames = stackSummary.Split(["<-", "->"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string frame in frames)
            {
                ParseModuleAndRvaToken(frame, out string frameMod, out string frameRva);
                if (!IsForcedExitWrapperFrame(frameMod, frameRva))
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[DIAGNOSTICS] Unwrapped forced-exit wrapper '{callerModule}+{callerRva}' -> '{frameMod}+{frameRva}'.");
                    callerModule = frameMod;
                    callerRva = frameRva;
                    break;
                }
            }
        }

        return !string.IsNullOrWhiteSpace(exitCodeHex) && !string.IsNullOrWhiteSpace(callerModule);
    }

    private static void ParseModuleAndRvaToken(string token, out string module, out string rva)
    {
        int plusIdx = token.LastIndexOf('+');
        if (plusIdx <= 0 || plusIdx >= token.Length - 1)
        {
            module = string.IsNullOrWhiteSpace(token) ? "unknown" : token.Trim();
            rva = "0x0";
        }
        else
        {
            module = token[..plusIdx].Trim();
            rva = token[(plusIdx + 1)..].Trim();
        }
    }

    private static bool TryParseHexRva(string? rvaText, out ulong value)
    {
        value = 0UL;
        if (string.IsNullOrWhiteSpace(rvaText))
        {
            return false;
        }

        string trimmed = rvaText.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return ulong.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool IsForcedExitWrapperFrame(string? moduleName, string? rvaText)
    {
        if (string.IsNullOrWhiteSpace(moduleName) ||
            moduleName.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            moduleName.Equals("version.dll", StringComparison.OrdinalIgnoreCase) ||
            moduleName.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase) ||
            moduleName.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase) ||
            moduleName.Equals("kernelbase.dll", StringComparison.OrdinalIgnoreCase) ||
            moduleName.Equals("ucrtbase.dll", StringComparison.OrdinalIgnoreCase) ||
            moduleName.StartsWith("msvcr", StringComparison.OrdinalIgnoreCase) ||
            moduleName.StartsWith("vcruntime", StringComparison.OrdinalIgnoreCase) ||
            moduleName.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (moduleName.Equals("k2_x64.dll", StringComparison.OrdinalIgnoreCase) &&
            TryParseHexRva(rvaText, out ulong rva))
        {
            // Ghidra-verified fatal-exit wrappers in k2_x64.dll:
            //   0x9FDB90..0x9FE320 = CSystem::Exit(int) (+0x9FE30B)
            //   0xA05300..0xA05440 = CSystem::Error(...) (+0xA0542D)
            return (rva >= 0x9FDB90UL && rva <= 0x9FE320UL) ||
                   (rva >= 0xA05300UL && rva <= 0xA05440UL);
        }

        return false;
    }

    private static string SanitizeFilename(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder sb = new();
        foreach (char c in name)
        {
            sb.Append(invalid.Contains(c) || c == '+' || c == ' ' ? '_' : c);
        }
        return sb.ToString();
    }
}

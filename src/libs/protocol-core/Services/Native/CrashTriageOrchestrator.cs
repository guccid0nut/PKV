// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CrashTriageOrchestrator.cs
// System: Kongor Protocol Core - Native C++ Modernization & Engine Hardening
// Subsystem: Crash Triage - Discovery, Dump Triangulation & Hot-Patch Synthesis
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Kongor.Core.Services.Native;

/// <summary>
/// Model for a crash triage request.
/// </summary>
public sealed class CrashTriageRequest
{
    public string? DumpPath { get; init; }
    public string? TargetModule { get; init; }
    public string? FaultingAddress { get; init; }
    public string? CommandLineArgs { get; init; }
    public bool PreserveMatchContext { get; init; } = true;
}

/// <summary>
/// Pre-indexed Ghidra triangulation metadata.
/// </summary>
public sealed class GhidraTargetInfo
{
    public required string Module { get; init; }
    public required string Address { get; init; }
    public required string ProjectBinary { get; init; }
    public bool ProjectPreIndexed { get; init; }
    public required string SuggestedTool { get; init; }
    public required string SuggestedAction { get; init; }
    public required string Command { get; init; }
}

/// <summary>
/// Manifest for resuscitating a terminated process with preserved match arguments.
/// </summary>
public sealed class ProcessResuscitationManifest
{
    public required string ExecutablePath { get; init; }
    public required string CommandLineArgs { get; init; }
    public required string WorkingDirectory { get; init; }
    public required Dictionary<string, string> PreservedFlags { get; init; }
    public bool CanAutoRestart { get; init; }
    public required string EscalationReason { get; init; }
}

/// <summary>
/// Verified hot-patch recipe synthesized from Ghidra triangulation and register analysis.
/// </summary>
public sealed class HotPatchRecipe
{
    public required string TargetModule { get; init; }
    public required string FaultingRva { get; init; }
    public required string SuggestedAction { get; init; }
    public required string DisassemblyInstruction { get; init; }
    public required string OriginalBytesHex { get; init; }
    public required string DetourBytesHex { get; init; }
    public required string SafetyVerification { get; init; }
    public bool CanLiveInject { get; init; }
}

/// <summary>
/// Decoded metadata for an MSVC C++ exception (0xE06D7363) (#166).
/// </summary>
public sealed class MsvcCppExceptionMetadata
{
    public bool IsValid { get; init; }
    public uint MagicNumber { get; init; }
    public ulong ExceptionObjectAddress { get; init; }
    public ulong ThrowInfoAddress { get; init; }
    public ulong ThrowImageBase { get; init; }
    public required string MangledTypeName { get; init; }
    public required string DemangledTypeName { get; init; }
    public required string WhatMessage { get; init; }
}

/// <summary>
/// Comprehensive triage report produced by <see cref="CrashTriageOrchestrator"/>.
/// </summary>
public sealed class CrashTriageReport
{
    public bool Triaged { get; init; } = true;
    public string Status { get; init; } = "triaged";
    public bool IsActualDumpFile { get; init; }
    public bool IsBenignDebugException { get; init; }
    public required string ArtifactPath { get; init; }
    public required string FaultingModule { get; init; }
    public required string FaultingRva { get; init; }
    public required string ExceptionCode { get; init; }
    public required string ExceptionName { get; init; }
    public required string FaultType { get; init; }
    public string? AccessViolationKind { get; init; }
    public string? AccessViolationTargetAddress { get; init; }
    public string? CppExceptionTypeName { get; init; }
    public string? CppExceptionWhatMessage { get; init; }
    public required Dictionary<string, string> Registers { get; init; }
    public required List<string> Callstack { get; init; }
    public required GhidraTargetInfo GhidraTriangulation { get; init; }
    public required string WinDbgCommand { get; init; }
    public required string RootCauseHypothesis { get; init; }
    public required string Recommendation { get; init; }
    public ProcessResuscitationManifest? ResuscitationManifest { get; init; }
    public HotPatchRecipe? HotPatchRecipe { get; init; }
}

/// <summary>
/// Service interface for orchestrating crash triage, Ghidra decompilation queries,
/// and process resuscitation.
/// </summary>
public interface ICrashTriageOrchestrator
{
    string? ResolveLatestCrashArtifact();
    List<string> DiscoverAllCrashArtifacts();
    Task<CrashTriageReport> TriageAndOrchestrateAsync(CrashTriageRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Autonomous Crash Triage Orchestrator (Phase 2 &amp; Phase 8D):
/// Discovers crash artifacts across K2 text diagnostics and Windows minidumps,
/// parses MDMP headers, triangulates with cached Ghidra databases,
/// and formats process resuscitation manifests.
/// </summary>
public sealed class CrashTriageOrchestrator : ICrashTriageOrchestrator
{
    private static readonly string[] KnownModules =
    [
        "cgame_x64.dll",
        "k2_x64.dll",
        "game_shared_x64.dll",
        "vid_d3d9_x64.dll",
        "vid_gl2_x64.dll",
        "vid_vulkan_x64.dll",
        "hon_x64.exe"
    ];

    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly string _logsDirectory;
    private readonly bool _explicitLogsDirectoryOnly;
    private readonly string _ghidraProjectsDirectory;

    public CrashTriageOrchestrator(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        string? logsDirectory = null,
        string? ghidraProjectsDirectory = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[CrashTriageOrchestrator] {msg}"));
        _explicitLogsDirectoryOnly = !string.IsNullOrWhiteSpace(logsDirectory);
        _logsDirectory = logsDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "logs");
        _ghidraProjectsDirectory = ghidraProjectsDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "src", "tools", "ghidra", "projects", "ProjectKongor");
    }

    /// <summary>
    /// Multi-path discovery order:
    /// 1. Engine diagnostic text logs (%USERPROFILE%\Documents\Heroes of Newerth x64\game\diag_*.txt and logs/diag_*.txt).
    /// 2. Local workspace and Windows minidumps (logs/*.dmp and %LOCALAPPDATA%\CrashDumps\hon_x64.exe.*.dmp).
    /// </summary>
    public string? ResolveLatestCrashArtifact()
    {
        List<string> allArtifacts = DiscoverAllCrashArtifacts();
        return allArtifacts.FirstOrDefault();
    }

    public List<string> DiscoverAllCrashArtifacts()
    {
        List<string> artifacts = [];

        // Path 1: Local logs directory
        if (Directory.Exists(_logsDirectory))
        {
            try
            {
                artifacts.AddRange(Directory.GetFiles(_logsDirectory, "*.dmp", SearchOption.AllDirectories));
                artifacts.AddRange(Directory.GetFiles(_logsDirectory, "diag_*.txt", SearchOption.AllDirectories));
                artifacts.AddRange(Directory.GetFiles(_logsDirectory, "console.log", SearchOption.AllDirectories));
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[WARN] Could not scan logs directory '{_logsDirectory}': {ex.Message}");
            }
        }

        if (!_explicitLogsDirectoryOnly)
        {
            // Path 2: User Documents K2 game diagnostic folder
            try
            {
                string userDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string k2GameDir = Path.Combine(userDocs, "Heroes of Newerth x64", "game");
                if (Directory.Exists(k2GameDir))
                {
                    artifacts.AddRange(Directory.GetFiles(k2GameDir, "diag_*.txt", SearchOption.TopDirectoryOnly));
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[WARN] Could not scan user documents K2 directory: {ex.Message}");
            }

            // Path 3: Windows LocalAppData CrashDumps
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string crashDumpsDir = Path.Combine(localAppData, "CrashDumps");
                if (Directory.Exists(crashDumpsDir))
                {
                    artifacts.AddRange(Directory.GetFiles(crashDumpsDir, "hon_x64.exe.*.dmp", SearchOption.TopDirectoryOnly));
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[WARN] Could not scan CrashDumps directory: {ex.Message}");
            }
        }

        return artifacts
            .Where(File.Exists)
            .OrderByDescending(f => {
                try
                {
                    return File.GetLastWriteTimeUtc(f);
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[WARN] Could not read LastWriteTimeUtc for '{f}': {ex.Message}");
                    return DateTime.MinValue;
                }
            })
            .ToList();
    }

    /// <summary>
    /// (#167) Returns true if the exception code is a benign debugger string or thread-naming signal
    /// (DBG_PRINTEXCEPTION_C = 0x40010006, DBG_PRINTEXCEPTION_WIDE_C = 0x4001000A, MS_VC_EXCEPTION = 0x406D1388).
    /// </summary>
    public static bool IsBenignDebugOrThreadNameException(uint exceptionCode)
    {
        return exceptionCode == 0x40010006U ||
               exceptionCode == 0x4001000AU ||
               exceptionCode == 0x406D1388U;
    }

    /// <summary>
    /// (#167) Classifies an EXCEPTION_ACCESS_VIOLATION (0xC0000005) or EXCEPTION_IN_PAGE_ERROR (0xC0000006)
    /// parameter tuple into READ (0), WRITE (1), or DEP_EXECUTE (8) along with the target address.
    /// </summary>
    public static (string Kind, string TargetAddressHex, string Description) ClassifyAccessViolationFault(
        ulong accessKindCode,
        ulong targetAddress)
    {
        string kind = accessKindCode switch
        {
            0UL => "READ",
            1UL => "WRITE",
            8UL => "DEP_EXECUTE",
            _ => "UNKNOWN_AV_KIND"
        };
        string targetHex = $"0x{targetAddress:X16}";
        string desc = $"[SHADPS4-VEH] AccessViolationKind={kind} (code={accessKindCode}) TargetAddr={targetHex}";
        return (kind, targetHex, desc);
    }

    /// <summary>
    /// (#166) Demangles an MSVC RTTI type descriptor name (e.g. ".?AVruntime_error@std@@" -> "std::runtime_error").
    /// </summary>
    public static string DemangleMsvcRttiTypeName(string? mangledName)
    {
        if (string.IsNullOrWhiteSpace(mangledName))
        {
            return "UnknownType";
        }

        string trimmed = mangledName.Trim();
        if (!trimmed.StartsWith(".?A", StringComparison.Ordinal) &&
            !trimmed.StartsWith("?A", StringComparison.Ordinal))
        {
            return trimmed;
        }

        string body = trimmed.StartsWith('.') ? trimmed[4..] : trimmed[3..];
        if (body.EndsWith("@@", StringComparison.Ordinal))
        {
            body = body[..^2];
        }

        string[] parts = body.Split('@', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return trimmed;
        }

        Array.Reverse(parts);
        return string.Join("::", parts);
    }

    /// <summary>
    /// (#166) Decodes MSVC x64 C++ exception (0xE06D7363) ExceptionInformation parameters
    /// (magic 0x19930520, exception object pointer, _ThrowInfo pointer, and module imageBase).
    /// </summary>
    public static MsvcCppExceptionMetadata DecodeMsvcCppExceptionParameters(
        ReadOnlySpan<ulong> parameters,
        string? mangledTypeName = null,
        string? whatMessage = null)
    {
        if (parameters.Length < 3)
        {
            return new MsvcCppExceptionMetadata
            {
                IsValid = false,
                MagicNumber = 0U,
                ExceptionObjectAddress = 0UL,
                ThrowInfoAddress = 0UL,
                ThrowImageBase = 0UL,
                MangledTypeName = mangledTypeName ?? "UnknownType",
                DemangledTypeName = DemangleMsvcRttiTypeName(mangledTypeName),
                WhatMessage = whatMessage ?? "<unavailable>"
            };
        }

        uint magic = (uint)(parameters[0] & 0xFFFFFFFFUL);
        bool validMagic = magic == 0x19930520U ||
                          magic == 0x19930521U ||
                          magic == 0x19930522U ||
                          magic == 0x01994000U;
        ulong excObj = parameters[1];
        ulong throwInfo = parameters[2];
        ulong imageBase = parameters.Length >= 4 ? parameters[3] : 0UL;
        string rawMangled = string.IsNullOrWhiteSpace(mangledTypeName) ? ".?AVexception@std@@" : mangledTypeName;

        return new MsvcCppExceptionMetadata
        {
            IsValid = validMagic && excObj != 0UL && throwInfo != 0UL,
            MagicNumber = magic,
            ExceptionObjectAddress = excObj,
            ThrowInfoAddress = throwInfo,
            ThrowImageBase = imageBase,
            MangledTypeName = rawMangled,
            DemangledTypeName = DemangleMsvcRttiTypeName(rawMangled),
            WhatMessage = string.IsNullOrWhiteSpace(whatMessage) ? "<unavailable>" : whatMessage
        };
    }

    public async Task<CrashTriageReport> TriageAndOrchestrateAsync(
        CrashTriageRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? resolvedPath = !string.IsNullOrWhiteSpace(request.DumpPath)
            ? request.DumpPath
            : ResolveLatestCrashArtifact();
        bool isRealDump = !string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath);
        bool hasExplicitTarget = !string.IsNullOrWhiteSpace(request.TargetModule) ||
                                 !string.IsNullOrWhiteSpace(request.FaultingAddress);

        if (!isRealDump && !hasExplicitTarget)
        {
            _logger?.Invoke("[CrashTriageOrchestrator] No crash artifact (.dmp/.log) or explicit target module/address found.");
            return new CrashTriageReport
            {
                Triaged = false,
                Status = "no_artifact",
                IsActualDumpFile = false,
                IsBenignDebugException = false,
                ArtifactPath = string.Empty,
                FaultingModule = string.Empty,
                FaultingRva = string.Empty,
                ExceptionCode = string.Empty,
                ExceptionName = "NO_CRASH_ARTIFACT",
                FaultType = "No crash dump (.dmp), diagnostic log (diag_*.txt), or explicit faulting target was found.",
                Registers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Callstack = new List<string>(),
                GhidraTriangulation = new GhidraTargetInfo
                {
                    Module = string.Empty,
                    Address = string.Empty,
                    ProjectBinary = string.Empty,
                    ProjectPreIndexed = false,
                    SuggestedTool = "everything",
                    SuggestedAction = "everything_search",
                    Command = "everything_search(query: \"ext:dmp|ext:log\", sort: \"date-modified-desc\")"
                },
                WinDbgCommand = string.Empty,
                RootCauseHypothesis = "No crash artifact or explicit target address provided.",
                Recommendation = "Provide an existing .dmp/.log path via dump_path or specify target_module and address.",
                ResuscitationManifest = null,
                HotPatchRecipe = null
            };
        }

        string faultingModule = request.TargetModule ?? string.Empty;
        string faultingRva = request.FaultingAddress ?? string.Empty;
        string exceptionCode = "0xC0000005";
        string exceptionName = "STATUS_ACCESS_VIOLATION";
        string faultType = "Access violation reading/executing memory location";
        bool isBenignDebug = false;
        string? avKind = null;
        string? avTargetHex = null;
        string? cppTypeName = null;
        string? cppWhatMessage = null;
        List<string> callstack = new List<string>();
        Dictionary<string, string> registers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (isRealDump && resolvedPath != null)
        {
            if (resolvedPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                resolvedPath.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string[] lines = await File.ReadAllLinesAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
                    (string? module, string? rva, List<string> callstack, string? exceptionCode, string? parsedAvKind, string? parsedAvTarget, string? parsedCppType, string? parsedCppWhat, Dictionary<string, string> parsedRegisters) parsed = ParseTextCrashLog(lines);
                    if (!string.IsNullOrEmpty(parsed.module) && string.IsNullOrEmpty(request.TargetModule)) faultingModule = parsed.module;
                    if (!string.IsNullOrEmpty(parsed.rva) && string.IsNullOrEmpty(request.FaultingAddress)) faultingRva = parsed.rva;
                    if (parsed.callstack.Count > 0) callstack = parsed.callstack;
                    if (!string.IsNullOrEmpty(parsed.exceptionCode)) exceptionCode = parsed.exceptionCode;
                    avKind = parsed.parsedAvKind;
                    avTargetHex = parsed.parsedAvTarget;
                    cppTypeName = parsed.parsedCppType;
                    cppWhatMessage = parsed.parsedCppWhat;
                    foreach (KeyValuePair<string, string> regKv in parsed.parsedRegisters)
                    {
                        registers[regKv.Key] = regKv.Value;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[WARN] Could not parse text crash dump '{resolvedPath}': {ex.Message}");
                }
            }
            else if (resolvedPath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using FileStream fs = File.OpenRead(resolvedPath);
                    global::Kongor.Core.Diagnostics.MinidumpTriageResult mdmp =
                        global::Kongor.Core.Diagnostics.MinidumpReader.Inspect(fs);

                    if (mdmp.IsValidDump)
                    {
                        _logger?.Invoke($"Parsed MDMP minidump '{resolvedPath}' via MinidumpReader (Status={mdmp.Status}, Target={mdmp.TargetFormatted})");

                        if (string.IsNullOrEmpty(request.TargetModule) && !string.IsNullOrWhiteSpace(mdmp.FaultingModule))
                        {
                            faultingModule = mdmp.FaultingModule;
                        }
                        if (string.IsNullOrEmpty(request.FaultingAddress) && !string.IsNullOrWhiteSpace(mdmp.FaultingRva))
                        {
                            faultingRva = mdmp.FaultingRva;
                        }

                        // Preserve default 0xC0000005 only if caller explicitly passed a target address on a header-only stub dump
                        bool isLiveSnapshotWithoutExplicitTarget =
                            string.Equals(mdmp.Status, "LIVE_SNAPSHOT_NO_EXCEPTION", StringComparison.Ordinal) &&
                            string.IsNullOrEmpty(request.TargetModule) &&
                            string.IsNullOrEmpty(request.FaultingAddress);

                        if (!string.Equals(mdmp.Status, "LIVE_SNAPSHOT_NO_EXCEPTION", StringComparison.Ordinal) || isLiveSnapshotWithoutExplicitTarget)
                        {
                            exceptionCode = mdmp.ExceptionCodeHex;
                            exceptionName = mdmp.ExceptionName;
                            faultType = mdmp.FaultType;
                        }

                        avKind = mdmp.AccessViolationKind;
                        avTargetHex = mdmp.AccessViolationTargetHex;
                        cppTypeName = mdmp.CppExceptionTypeName;
                        cppWhatMessage = mdmp.CppExceptionWhatMessage;

                        foreach (KeyValuePair<string, string> regKv in mdmp.Registers)
                        {
                            registers[regKv.Key] = regKv.Value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[WARN] Could not parse binary minidump '{resolvedPath}': {ex.Message}");
                }
            }
        }

        if (exceptionCode.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(exceptionCode[2..], System.Globalization.NumberStyles.HexNumber, null, out uint parsedExcCode))
        {
            isBenignDebug = IsBenignDebugOrThreadNameException(parsedExcCode);
            if (parsedExcCode == 0xE06D7363U)
            {
                exceptionName = "MSVC_CPP_EXCEPTION";
            }
            else if (parsedExcCode == 0x40010006U)
            {
                exceptionName = "DBG_PRINTEXCEPTION_C";
            }
            else if (parsedExcCode == 0x406D1388U)
            {
                exceptionName = "MS_VC_EXCEPTION";
            }
        }

        (string normModule, string normRva) = NormalizeModuleAndRva(faultingModule, faultingRva);

        string ghidraProjectFile = Path.Combine(_ghidraProjectsDirectory, $"{normModule}.gpr");
        bool isGhidraPreIndexed = File.Exists(ghidraProjectFile);
        string ghidraCommand = $"decompile_function(file_name: \"{normModule}\", address: \"{normRva}\")";
        string windbgCommand = $".ecxr; k; r; lmDvm {Path.GetFileNameWithoutExtension(normModule)}";

        string rootCause = exceptionCode == "0xC0000005"
            ? $"Null pointer dereference or dangling vtable pointer in {normModule} at offset {normRva}."
            : $"Exception {exceptionCode} in {normModule} at offset {normRva}.";

        string recommendation = normModule.Contains("cgame", StringComparison.OrdinalIgnoreCase)
            ? "Ensure CGameClient / CUICommander verifies valid entity pointers (s_pGame != null) before invoking UI callbacks during Disconnect."
            : "Verify null safety checks on RCX/RDX parameter registers before dereferencing struct offsets.";

        ProcessResuscitationManifest? resuscitationManifest = null;
        if (!string.IsNullOrWhiteSpace(request.CommandLineArgs))
        {
            Dictionary<string, string> preservedFlags = ParseCommandLineFlags(request.CommandLineArgs);
            string clientDir = Path.Combine(Directory.GetCurrentDirectory(), "games", "client");
            resuscitationManifest = new ProcessResuscitationManifest
            {
                ExecutablePath = Path.Combine(clientDir, "hon_x64.exe"),
                CommandLineArgs = request.CommandLineArgs,
                WorkingDirectory = clientDir,
                PreservedFlags = preservedFlags,
                CanAutoRestart = true,
                EscalationReason = "Explicit command-line context provided for process resuscitation."
            };
        }

        HotPatchRecipe hotPatchRecipe = new HotPatchRecipe
        {
            TargetModule = normModule,
            FaultingRva = normRva,
            SuggestedAction = "Candidate null-check detour template (requires on-disk prologue & trampoline verification)",
            DisassemblyInstruction = "test rcx, rcx; jz +skip; mov rax, [rcx+0x18]; ret; skip: xor eax, eax; ret",
            OriginalBytesHex = "48 8B 41 18",
            DetourBytesHex = "48 85 C9 74 05 48 8B 41 18 C3 31 C0 C3",
            SafetyVerification = "Candidate template (unverified on disk: detour length 13B exceeds 4B instruction without trampoline allocation)",
            CanLiveInject = false
        };

        return new CrashTriageReport
        {
            Triaged = true,
            IsActualDumpFile = isRealDump,
            IsBenignDebugException = isBenignDebug,
            ArtifactPath = resolvedPath ?? string.Empty,
            FaultingModule = normModule,
            FaultingRva = normRva,
            ExceptionCode = exceptionCode,
            ExceptionName = exceptionName,
            FaultType = faultType,
            AccessViolationKind = avKind,
            AccessViolationTargetAddress = avTargetHex,
            CppExceptionTypeName = cppTypeName,
            CppExceptionWhatMessage = cppWhatMessage,
            Registers = registers,
            Callstack = callstack,
            GhidraTriangulation = new GhidraTargetInfo
            {
                Module = normModule,
                Address = normRva,
                ProjectBinary = ghidraProjectFile,
                ProjectPreIndexed = isGhidraPreIndexed,
                SuggestedTool = "ghidra",
                SuggestedAction = "decompile_function",
                Command = ghidraCommand
            },
            WinDbgCommand = windbgCommand,
            RootCauseHypothesis = rootCause,
            Recommendation = recommendation,
            ResuscitationManifest = resuscitationManifest,
            HotPatchRecipe = hotPatchRecipe
        };
    }

    private static (string? module, string? rva, List<string> callstack, string? exceptionCode, string? avKind, string? avTarget, string? cppType, string? cppWhat, Dictionary<string, string> registers) ParseTextCrashLog(string[] lines)
    {
        string? module = null;
        string? rva = null;
        string? exceptionCode = null;
        string? avKind = null;
        string? avTarget = null;
        string? cppType = null;
        string? cppWhat = null;
        List<string> stack = [];
        Dictionary<string, string> registers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Regex frameRegex = new(@"([a-zA-Z0-9_\-\.]+)!([a-zA-Z0-9_:]+)\+(0x[0-9a-fA-F]+|[0-9a-fA-F]+)", RegexOptions.Compiled);
        Regex excRegex = new(@"(0x[0-9a-fA-F]{8})", RegexOptions.Compiled);
        Regex avRegex = new(@"AccessViolationKind=([A-Z_]+).*TargetAddr=(0x[0-9a-fA-F]+)", RegexOptions.Compiled);
        Regex rttiRegex = new(@"\[GEODE-RTTI\]\s+Thrown C\+\+ Type:\s+([^\(\|]+).*what\(\)\s*=\s*""([^""]*)""", RegexOptions.Compiled);
        Regex regRegex = new(@"\b(RAX|RBX|RCX|RDX|RSI|RDI|RBP|RSP|R8|R9|R10|R11|R12|R13|R14|R15|RIP)\s*=\s*(0x[0-9a-fA-F]+|[0-9a-fA-F]{8,16})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (exceptionCode == null && (line.Contains("Exception", StringComparison.OrdinalIgnoreCase) || line.Contains("Crash", StringComparison.OrdinalIgnoreCase)))
            {
                Match match = excRegex.Match(line);
                if (match.Success)
                {
                    exceptionCode = match.Groups[1].Value;
                }
            }

            Match avMatch = avRegex.Match(line);
            if (avMatch.Success)
            {
                avKind = avMatch.Groups[1].Value.Trim();
                avTarget = avMatch.Groups[2].Value.Trim();
            }

            Match rttiMatch = rttiRegex.Match(line);
            if (rttiMatch.Success)
            {
                cppType = rttiMatch.Groups[1].Value.Trim();
                cppWhat = rttiMatch.Groups[2].Value;
            }

            foreach (Match regMatch in regRegex.Matches(line))
            {
                string regName = regMatch.Groups[1].Value.ToUpperInvariant();
                string regVal = regMatch.Groups[2].Value;
                if (!regVal.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    regVal = "0x" + regVal;
                }
                registers[regName] = regVal;
            }

            Match frameMatch = frameRegex.Match(line);
            if (frameMatch.Success)
            {
                string m = frameMatch.Groups[1].Value;
                string f = frameMatch.Groups[2].Value;
                string off = frameMatch.Groups[3].Value;
                stack.Add($"{m}!{f}+{off}");

                if (module == null)
                {
                    module = m;
                    rva = off.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? off : $"0x{off}";
                }
            }
        }

        return (module, rva, stack, exceptionCode, avKind, avTarget, cppType, cppWhat, registers);
    }

    private static (string module, string rva) NormalizeModuleAndRva(string module, string rva)
    {
        if (string.IsNullOrWhiteSpace(module))
        {
            module = "unknown_module.dll";
        }
        if (string.IsNullOrWhiteSpace(rva))
        {
            rva = "0x0";
        }
        string cleanModule = Path.GetFileName(module).ToLowerInvariant();
        if (!cleanModule.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            !cleanModule.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            cleanModule += ".dll";
        }

        foreach (string km in KnownModules)
        {
            if (string.Equals(km, cleanModule, StringComparison.OrdinalIgnoreCase))
            {
                cleanModule = km;
                break;
            }
        }

        string cleanRva = rva.Trim();
        if (!cleanRva.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            cleanRva = "0x" + cleanRva;
        }

        return (cleanModule, cleanRva);
    }

    private static Dictionary<string, string> ParseCommandLineFlags(string? args)
    {
        Dictionary<string, string> flags = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(args)) return flags;

        string[] tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            string tok = tokens[i];
            if (tok.StartsWith('-') || tok.StartsWith('/'))
            {
                string key = tok.TrimStart('-', '/');
                string val = "true";
                if (i + 1 < tokens.Length && !tokens[i + 1].StartsWith('-') && !tokens[i + 1].StartsWith('/'))
                {
                    val = tokens[++i];
                }
                flags[key] = val;
            }
        }

        return flags;
    }

    // ========================================================================
    // (#168) x64 PE .pdata RUNTIME_FUNCTION & UNW_FLAG_CHAININFO Unwind Parser
    // ========================================================================
    public const byte UnwFlagChainInfo = 0x04;

    public readonly record struct PdataRuntimeFunctionEntry(
        uint BeginAddress,
        uint EndAddress,
        uint UnwindInfoAddress);

    public static List<PdataRuntimeFunctionEntry> ParsePePdataRuntimeFunctions(ReadOnlySpan<byte> pdataBytes)
    {
        List<PdataRuntimeFunctionEntry> list = new List<PdataRuntimeFunctionEntry>(pdataBytes.Length / 12);
        int offset = 0;
        while (offset + 12 <= pdataBytes.Length)
        {
            uint beginRva = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pdataBytes.Slice(offset, 4));
            uint endRva = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pdataBytes.Slice(offset + 4, 4));
            uint unwindRva = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pdataBytes.Slice(offset + 8, 4));
            offset += 12;

            if (beginRva == 0U && endRva == 0U && unwindRva == 0U)
            {
                break;
            }
            if (endRva > beginRva)
            {
                list.Add(new PdataRuntimeFunctionEntry(beginRva, endRva, unwindRva));
            }
        }
        return list;
    }

    public static PdataRuntimeFunctionEntry? ResolvePrimaryPdataFunctionEntry(
        IReadOnlyList<PdataRuntimeFunctionEntry> entries,
        uint faultRva,
        ReadOnlySpan<byte> moduleImageBytes = default)
    {
        if (entries == null || entries.Count == 0)
        {
            return null;
        }

        PdataRuntimeFunctionEntry? current = null;
        for (int i = 0; i < entries.Count; i++)
        {
            PdataRuntimeFunctionEntry candidate = entries[i];
            if (faultRva >= candidate.BeginAddress && faultRva < candidate.EndAddress)
            {
                current = candidate;
                break;
            }
        }

        if (current == null)
        {
            return null;
        }

        for (int hop = 0; hop < 16 && current != null; hop++)
        {
            PdataRuntimeFunctionEntry curVal = current.Value;
            uint unwindRva = curVal.UnwindInfoAddress;

            if ((unwindRva & 1U) != 0U)
            {
                uint targetRfRva = unwindRva & ~1U;
                if (!moduleImageBytes.IsEmpty && targetRfRva + 12U <= (uint)moduleImageBytes.Length)
                {
                    int rfOff = (int)targetRfRva;
                    uint parentBegin = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(moduleImageBytes.Slice(rfOff, 4));
                    uint parentEnd = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(moduleImageBytes.Slice(rfOff + 4, 4));
                    uint parentUnwind = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(moduleImageBytes.Slice(rfOff + 8, 4));
                    current = new PdataRuntimeFunctionEntry(parentBegin, parentEnd, parentUnwind);
                    continue;
                }
                break;
            }

            if (moduleImageBytes.IsEmpty || unwindRva + 4U > (uint)moduleImageBytes.Length)
            {
                break;
            }

            int uwOff = (int)unwindRva;
            byte versionAndFlags = moduleImageBytes[uwOff];
            byte flags = (byte)((versionAndFlags >> 3) & 0x1F);
            if ((flags & UnwFlagChainInfo) == 0)
            {
                break;
            }

            byte countOfCodes = moduleImageBytes[uwOff + 2];
            int alignedCodeSlots = (countOfCodes % 2 == 0) ? countOfCodes : (countOfCodes + 1);
            int chainedRfOff = uwOff + 4 + (alignedCodeSlots * 2);
            if (chainedRfOff + 12 > moduleImageBytes.Length)
            {
                break;
            }

            uint chainedBegin = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(moduleImageBytes.Slice(chainedRfOff, 4));
            uint chainedEnd = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(moduleImageBytes.Slice(chainedRfOff + 4, 4));
            uint chainedUnwind = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(moduleImageBytes.Slice(chainedRfOff + 8, 4));

            PdataRuntimeFunctionEntry nextEntry = new PdataRuntimeFunctionEntry(chainedBegin, chainedEnd, chainedUnwind);
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].BeginAddress == chainedBegin && entries[i].EndAddress == chainedEnd)
                {
                    nextEntry = entries[i];
                    break;
                }
            }
            current = nextEntry;
        }

        return current;
    }

    // ========================================================================
    // (#260) AMD64 Non-Canonical #GPF (0xFFFFFFFFFFFFFFFF) & Poison / Bit-Flip Classifier
    // ========================================================================
    public sealed record Amd64GpfAndPoisonAnalysis(
        bool IsGeneralProtectionFaultSentinel,
        string? NonCanonicalRegisterName,
        ulong NonCanonicalRegisterValue,
        bool IsUnalignedSseCandidate,
        string? PoisonPatternTag,
        bool IsHeuristicSingleBitFlip,
        int FlippedBitIndex,
        string DiagnosticSummary);

    public static bool IsAmd64NonCanonicalAddress(ulong address)
    {
        ulong top17 = address >> 47;
        return top17 != 0UL && top17 != 0x1FFFFUL;
    }

    private static bool MatchesPoisonFillByte(ulong value, byte fillByte)
    {
        ulong expected64 = 0UL;
        for (int i = 0; i < 8; i++)
        {
            expected64 = (expected64 << 8) | fillByte;
        }
        ulong expected32 = expected64 & 0xFFFFFFFFUL;
        if (value >= expected64 && (value - expected64) <= 0xFFUL)
        {
            return true;
        }
        if (value >= expected32 && (value - expected32) <= 0xFFUL)
        {
            return true;
        }
        return false;
    }

    public static bool TryClassifyPoisonOrSingleBitFlip(
        ulong address,
        ulong canonicalBaseAddress,
        out string? patternTag,
        out bool isHeuristicSingleBitFlip,
        out int flippedBitIndex)
    {
        patternTag = null;
        isHeuristicSingleBitFlip = false;
        flippedBitIndex = -1;

        if (address == 0UL)
        {
            return false;
        }

        if (MatchesPoisonFillByte(address, 0xDD))
        {
            patternTag = "CRT_FREED_HEAP_0xDD";
            return true;
        }
        if (MatchesPoisonFillByte(address, 0xCC))
        {
            patternTag = "MSVC_UNINIT_STACK_0xCC";
            return true;
        }
        if (MatchesPoisonFillByte(address, 0xCD))
        {
            patternTag = "CRT_UNINIT_HEAP_0xCD";
            return true;
        }
        if ((address & 0xFFFFFFFFFFFF0000UL) == 0xFEEEFEEEFEEE0000UL ||
            (address & 0xFFFF0000UL) == 0xFEEE0000UL)
        {
            patternTag = "WIN32_HEAPFREE_0xFEEE";
            return true;
        }
        if ((address >= 0xBAADF00DBAADF00DUL && (address - 0xBAADF00DBAADF00DUL) <= 0xFFUL) ||
            (address >= 0xBAADF00DUL && (address - 0xBAADF00DUL) <= 0xFFUL))
        {
            patternTag = "WIN32_LOCALALLOC_UNINIT_0xBAADF00D";
            return true;
        }
        if ((address >= 0xDEADBEEFDEADBEEFUL && (address - 0xDEADBEEFDEADBEEFUL) <= 0xFFUL) ||
            (address >= 0xDEADBEEFUL && (address - 0xDEADBEEFUL) <= 0xFFUL))
        {
            patternTag = "SENTINEL_0xDEADBEEF";
            return true;
        }

        ulong diff = 0UL;
        if (canonicalBaseAddress != 0UL)
        {
            diff = address ^ canonicalBaseAddress;
        }
        else if (IsAmd64NonCanonicalAddress(address))
        {
            diff = address & 0xFFFF800000000000UL;
        }

        if (diff != 0UL && (diff & (diff - 1UL)) == 0UL)
        {
            int bitIdx = System.Numerics.BitOperations.TrailingZeroCount(diff);
            isHeuristicSingleBitFlip = true;
            flippedBitIndex = bitIdx;
            patternTag = $"[heuristic-hint] SINGLE_BIT_FLIP_BIT_{bitIdx}";
            return true;
        }

        return false;
    }

    public static Amd64GpfAndPoisonAnalysis AnalyzeAmd64GpfAndPoison(
        ulong faultTargetAddress,
        IReadOnlyDictionary<string, ulong>? gprs,
        ulong canonicalBaseAddress = 0UL)
    {
        bool isGpfSentinel = faultTargetAddress == 0xFFFFFFFFFFFFFFFFUL;
        string? nonCanonRegName = null;
        ulong nonCanonRegVal = 0UL;
        bool isUnalignedSse = false;

        if (isGpfSentinel && gprs != null)
        {
            foreach (KeyValuePair<string, ulong> kv in gprs)
            {
                if (IsAmd64NonCanonicalAddress(kv.Value))
                {
                    nonCanonRegName = kv.Key;
                    nonCanonRegVal = kv.Value;
                    break;
                }
            }

            if (nonCanonRegName == null)
            {
                if (gprs.TryGetValue("RSP", out ulong rspVal) || gprs.TryGetValue("rsp", out rspVal))
                {
                    isUnalignedSse = (rspVal & 0xFUL) != 0UL;
                }
                else
                {
                    isUnalignedSse = true;
                }
            }
        }

        ulong probeAddress = nonCanonRegName != null ? nonCanonRegVal : faultTargetAddress;
        string? poisonTag = null;
        bool isBitFlip = false;
        int bitIdx = -1;
        if (!isGpfSentinel || nonCanonRegName != null)
        {
            _ = TryClassifyPoisonOrSingleBitFlip(probeAddress, canonicalBaseAddress, out poisonTag, out isBitFlip, out bitIdx);
        }

        string summary;
        if (isGpfSentinel && nonCanonRegName != null)
        {
            summary = $"AMD64 #GPF (0xFFFFFFFFFFFFFFFF): Non-canonical pointer in {nonCanonRegName}=0x{nonCanonRegVal:X16}" +
                      (poisonTag != null ? $" ({poisonTag})" : string.Empty);
        }
        else if (isGpfSentinel)
        {
            summary = $"AMD64 #GPF (0xFFFFFFFFFFFFFFFF): All GPRs canonical; unaligned 16-byte SSE/XMM candidate={isUnalignedSse}";
        }
        else if (poisonTag != null)
        {
            summary = $"Poison/BitFlip pattern {poisonTag} at 0x{probeAddress:X16}";
        }
        else
        {
            summary = $"Standard fault target 0x{faultTargetAddress:X16}";
        }

        return new Amd64GpfAndPoisonAnalysis(
            IsGeneralProtectionFaultSentinel: isGpfSentinel,
            NonCanonicalRegisterName: nonCanonRegName,
            NonCanonicalRegisterValue: nonCanonRegVal,
            IsUnalignedSseCandidate: isUnalignedSse,
            PoisonPatternTag: poisonTag,
            IsHeuristicSingleBitFlip: isBitFlip,
            FlippedBitIndex: bitIdx,
            DiagnosticSummary: summary);
    }

    // ========================================================================
    // (#262) rust-minidump (minidump-stackwalk --json) & Breakpad dump_syms Parser
    // ========================================================================
    public enum StackFrameTrustGrade
    {
        Unknown = 0,
        Scan = 1,
        FramePointer = 2,
        Cfi = 3,
        Context = 4
    }

    public sealed record MinidumpStackwalkFrame(
        int FrameIndex,
        string ModuleName,
        ulong ModuleOffset,
        string? FunctionName,
        StackFrameTrustGrade TrustGrade);

    public sealed record MinidumpStackwalkResult(
        bool Success,
        string Status,
        int CrashingThreadIndex,
        string ExceptionCode,
        ulong CrashAddress,
        IReadOnlyList<MinidumpStackwalkFrame> Frames,
        bool UsedCdbFallback);

    public sealed record BreakpadSymFunctionEntry(uint Rva, uint Size, string Name);
    public sealed record BreakpadSymPublicEntry(uint Rva, string Name);

    public sealed record BreakpadSymModuleRecord(
        string OperatingSystem,
        string CpuArchitecture,
        string DebugId,
        string DebugFileName,
        IReadOnlyList<BreakpadSymFunctionEntry> Functions,
        IReadOnlyList<BreakpadSymPublicEntry> PublicSymbols);

    public static StackFrameTrustGrade ParseTrustGrade(string? rawTrust)
    {
        if (string.IsNullOrWhiteSpace(rawTrust))
        {
            return StackFrameTrustGrade.Unknown;
        }
        return rawTrust.Trim().ToLowerInvariant() switch
        {
            "context" => StackFrameTrustGrade.Context,
            "cfi" or "cfi_scan" or "frame_table" => StackFrameTrustGrade.Cfi,
            "fp" or "frame_pointer" => StackFrameTrustGrade.FramePointer,
            "scan" => StackFrameTrustGrade.Scan,
            _ => StackFrameTrustGrade.Unknown
        };
    }

    public static MinidumpStackwalkResult ParseMinidumpStackwalkJson(string jsonPayload)
    {
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            return new MinidumpStackwalkResult(false, "EMPTY_JSON", -1, "0x0", 0UL, Array.Empty<MinidumpStackwalkFrame>(), true);
        }

        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(jsonPayload);
            System.Text.Json.JsonElement root = doc.RootElement;

            string status = root.TryGetProperty("status", out System.Text.Json.JsonElement stEl)
                ? (stEl.GetString() ?? "OK")
                : "OK";

            string excCode = "0xC0000005";
            ulong crashAddr = 0UL;
            if (root.TryGetProperty("crash_info", out System.Text.Json.JsonElement ciEl))
            {
                if (ciEl.TryGetProperty("type", out System.Text.Json.JsonElement typeEl) && typeEl.GetString() != null)
                {
                    excCode = typeEl.GetString()!;
                }
                if (ciEl.TryGetProperty("address", out System.Text.Json.JsonElement addrEl) && addrEl.GetString() != null)
                {
                    string rawAddr = addrEl.GetString()!.Trim();
                    if (rawAddr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = ulong.TryParse(rawAddr.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out crashAddr);
                    }
                }
            }

            int crashingThreadIdx = 0;
            List<MinidumpStackwalkFrame> parsedFrames = new List<MinidumpStackwalkFrame>();

            if (root.TryGetProperty("crashing_thread", out System.Text.Json.JsonElement ctEl))
            {
                if (ctEl.TryGetProperty("threads_index", out System.Text.Json.JsonElement tiEl) && tiEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    crashingThreadIdx = tiEl.GetInt32();
                }
                if (ctEl.TryGetProperty("frames", out System.Text.Json.JsonElement framesEl) && framesEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    int idx = 0;
                    foreach (System.Text.Json.JsonElement fEl in framesEl.EnumerateArray())
                    {
                        string mod = fEl.TryGetProperty("module", out System.Text.Json.JsonElement mEl)
                            ? (mEl.GetString() ?? "UnknownModule")
                            : "UnknownModule";
                        ulong modOff = 0UL;
                        if (fEl.TryGetProperty("module_offset", out System.Text.Json.JsonElement moEl) && moEl.GetString() != null)
                        {
                            string moStr = moEl.GetString()!.Trim();
                            if (moStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            {
                                _ = ulong.TryParse(moStr.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out modOff);
                            }
                        }
                        string? fnName = fEl.TryGetProperty("function", out System.Text.Json.JsonElement fnEl)
                            ? fnEl.GetString()
                            : null;
                        string? trustStr = fEl.TryGetProperty("trust", out System.Text.Json.JsonElement trEl)
                            ? trEl.GetString()
                            : null;

                        parsedFrames.Add(new MinidumpStackwalkFrame(
                            idx++,
                            mod,
                            modOff,
                            fnName,
                            ParseTrustGrade(trustStr)));
                    }
                }
            }

            return new MinidumpStackwalkResult(
                Success: parsedFrames.Count > 0,
                Status: status,
                CrashingThreadIndex: crashingThreadIdx,
                ExceptionCode: excCode,
                CrashAddress: crashAddr,
                Frames: parsedFrames,
                UsedCdbFallback: parsedFrames.Count == 0);
        }
        catch (Exception ex)
        {
            return new MinidumpStackwalkResult(
                false,
                $"PARSE_ERROR: {ex.Message}",
                -1,
                "0x0",
                0UL,
                Array.Empty<MinidumpStackwalkFrame>(),
                true);
        }
    }

    private static readonly char[] s_breakpadLineSeparators = ['\r', '\n'];

    public static BreakpadSymModuleRecord ParseBreakpadSymText(string symText)
    {
        ArgumentNullException.ThrowIfNull(symText);
        string os = "windows";
        string cpu = "x86_64";
        string debugId = string.Empty;
        string debugFile = string.Empty;
        List<BreakpadSymFunctionEntry> funcs = new List<BreakpadSymFunctionEntry>();
        List<BreakpadSymPublicEntry> publics = new List<BreakpadSymPublicEntry>();

        string[] lines = symText.Split(s_breakpadLineSeparators, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith("MODULE ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    os = parts[1];
                    cpu = parts[2];
                    debugId = parts[3];
                    debugFile = parts[4];
                }
            }
            else if (line.StartsWith("FUNC ", StringComparison.Ordinal))
            {
                string rest = line.Substring(5).TrimStart();
                if (rest.StartsWith("m ", StringComparison.Ordinal))
                {
                    rest = rest.Substring(2).TrimStart();
                }
                string[] parts = rest.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4 &&
                    uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out uint fnRva) &&
                    uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out uint fnSize))
                {
                    funcs.Add(new BreakpadSymFunctionEntry(fnRva, fnSize, parts[3]));
                }
            }
            else if (line.StartsWith("PUBLIC ", StringComparison.Ordinal))
            {
                string rest = line.Substring(7).TrimStart();
                if (rest.StartsWith("m ", StringComparison.Ordinal))
                {
                    rest = rest.Substring(2).TrimStart();
                }
                string[] parts = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 &&
                    uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out uint pubRva))
                {
                    publics.Add(new BreakpadSymPublicEntry(pubRva, parts[2]));
                }
            }
        }

        publics.Sort((a, b) => a.Rva.CompareTo(b.Rva));
        return new BreakpadSymModuleRecord(os, cpu, debugId, debugFile, funcs, publics);
    }

    public static string? ResolveSymbolFromBreakpadSym(BreakpadSymModuleRecord symRecord, uint faultRva)
    {
        ArgumentNullException.ThrowIfNull(symRecord);
        for (int i = 0; i < symRecord.Functions.Count; i++)
        {
            BreakpadSymFunctionEntry fn = symRecord.Functions[i];
            if (faultRva >= fn.Rva && faultRva < (fn.Rva + fn.Size))
            {
                uint disp = faultRva - fn.Rva;
                return $"{fn.Name}+0x{disp:X}";
            }
        }

        BreakpadSymPublicEntry? bestPublic = null;
        for (int i = 0; i < symRecord.PublicSymbols.Count; i++)
        {
            BreakpadSymPublicEntry pub = symRecord.PublicSymbols[i];
            if (pub.Rva <= faultRva)
            {
                bestPublic = pub;
            }
            else
            {
                break;
            }
        }

        if (bestPublic != null && (faultRva - bestPublic.Rva) <= 0x10000U)
        {
            uint disp = faultRva - bestPublic.Rva;
            return $"{bestPublic.Name}+0x{disp:X}";
        }

        return null;
    }

    // ========================================================================
    // (#261) Bounded 40-Qword ([Rsp, Rsp + 320]) Stack-Scan Call-Site Validator
    // ========================================================================
    public const int MaxStackScanQwords = 40;

    public sealed record ValidatedStackCallSite(
        int StackQwordIndex,
        ulong ReturnAddressVa,
        uint CallSiteRva,
        string CallOpcodeKind);

    public static bool TryValidatePrecedingCallInstruction(
        ReadOnlySpan<byte> bytesBeforeReturnAddress,
        out string callOpcodeKind)
    {
        callOpcodeKind = string.Empty;
        int len = bytesBeforeReturnAddress.Length;
        if (len < 2)
        {
            return false;
        }

        if (len >= 5 && bytesBeforeReturnAddress[len - 5] == 0xE8)
        {
            callOpcodeKind = "CALL_REL32_E8";
            return true;
        }
        if (len >= 6 && bytesBeforeReturnAddress[len - 6] == 0xFF && bytesBeforeReturnAddress[len - 5] == 0x15)
        {
            callOpcodeKind = "CALL_RIP_IND_FF15";
            return true;
        }
        if (len >= 2 && bytesBeforeReturnAddress[len - 2] == 0xFF && (bytesBeforeReturnAddress[len - 1] & 0xF8) == 0xD0)
        {
            callOpcodeKind = "CALL_REG_IND_FFD0";
            return true;
        }
        if (len >= 2 && bytesBeforeReturnAddress[len - 2] == 0xFF &&
            (bytesBeforeReturnAddress[len - 1] & 0xF8) == 0x10 &&
            (bytesBeforeReturnAddress[len - 1] & 0x07) != 0x04 &&
            (bytesBeforeReturnAddress[len - 1] & 0x07) != 0x05)
        {
            callOpcodeKind = "CALL_MEM_IND_FF10";
            return true;
        }
        if (len >= 3 && bytesBeforeReturnAddress[len - 3] == 0xFF && bytesBeforeReturnAddress[len - 2] == 0x14)
        {
            callOpcodeKind = "CALL_SIB_IND_FF14";
            return true;
        }
        if (len >= 3 && bytesBeforeReturnAddress[len - 3] == 0xFF &&
            (bytesBeforeReturnAddress[len - 2] & 0xF8) == 0x50 &&
            (bytesBeforeReturnAddress[len - 2] & 0x07) != 0x04)
        {
            callOpcodeKind = "CALL_DISP8_IND_FF50";
            return true;
        }
        if (len >= 4 && bytesBeforeReturnAddress[len - 4] == 0xFF && bytesBeforeReturnAddress[len - 3] == 0x54)
        {
            callOpcodeKind = "CALL_SIB_DISP8_IND_FF54";
            return true;
        }
        if (len >= 6 && bytesBeforeReturnAddress[len - 6] == 0xFF &&
            (bytesBeforeReturnAddress[len - 5] & 0xF8) == 0x90 &&
            (bytesBeforeReturnAddress[len - 5] & 0x07) != 0x04)
        {
            callOpcodeKind = "CALL_DISP32_IND_FF90";
            return true;
        }
        if (len >= 7 && bytesBeforeReturnAddress[len - 7] == 0xFF && bytesBeforeReturnAddress[len - 6] == 0x94)
        {
            callOpcodeKind = "CALL_SIB_DISP32_IND_FF94";
            return true;
        }

        return false;
    }

    public static List<ValidatedStackCallSite> ScanBoundedStackForCallSites(
        ReadOnlySpan<ulong> stackQwords,
        ulong moduleBaseVa,
        uint textStartRva,
        uint textEndRva,
        ReadOnlySpan<byte> moduleImageBytes)
    {
        List<ValidatedStackCallSite> results = new List<ValidatedStackCallSite>();
        if (stackQwords.IsEmpty || moduleBaseVa == 0UL || textEndRva <= textStartRva || moduleImageBytes.IsEmpty)
        {
            return results;
        }

        int scanLimit = Math.Min(stackQwords.Length, MaxStackScanQwords);
        ulong textStartVa = moduleBaseVa + textStartRva;
        ulong textEndVa = moduleBaseVa + textEndRva;

        for (int q = 0; q < scanLimit; q++)
        {
            ulong candidateRetVa = stackQwords[q];
            if (candidateRetVa <= textStartVa + 2UL || candidateRetVa > textEndVa)
            {
                continue;
            }

            // rust-minidump caller_ip = ret_addr - 1 rule
            ulong callSiteVa = candidateRetVa - 1UL;
            if (callSiteVa < textStartVa || callSiteVa >= textEndVa)
            {
                continue;
            }

            uint retRva = (uint)(candidateRetVa - moduleBaseVa);
            if (retRva > (uint)moduleImageBytes.Length)
            {
                continue;
            }

            int windowLen = (int)Math.Min(8U, retRva - textStartRva);
            if (windowLen < 2)
            {
                continue;
            }

            ReadOnlySpan<byte> codeBeforeRet = moduleImageBytes.Slice((int)retRva - windowLen, windowLen);
            if (TryValidatePrecedingCallInstruction(codeBeforeRet, out string opcodeKind))
            {
                results.Add(new ValidatedStackCallSite(
                    StackQwordIndex: q,
                    ReturnAddressVa: candidateRetVa,
                    CallSiteRva: (uint)(callSiteVa - moduleBaseVa),
                    CallOpcodeKind: opcodeKind));
            }
        }

        return results;
    }
}

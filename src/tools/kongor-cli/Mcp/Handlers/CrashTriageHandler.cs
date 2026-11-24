// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Services.Native;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_crash_triage' (aliases: 'crash_triage', 'kongor_triage').
/// Automated Crash & Fault Triangulation: minidump / crash log parsing, ASLR multi-DLL mapping,
/// exception register extraction, and Ghidra MCP decompiler triangulation in &lt; 500ms.
/// </summary>
public sealed class CrashTriageHandler
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
    private readonly ICrashTriageOrchestrator _orchestrator;

    public CrashTriageHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        string? logsDirectory = null,
        ICrashTriageOrchestrator? orchestrator = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[CrashTriage] {msg}"));
        _logsDirectory = logsDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "logs");
        _orchestrator = orchestrator ?? new CrashTriageOrchestrator(_timeProvider, _logger, _logsDirectory);
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string action = "triage";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        string? dumpPath = null;
        if (arguments.TryGetProperty("dump_path", out JsonElement dpEl) && !string.IsNullOrWhiteSpace(dpEl.GetString()))
        {
            dumpPath = dpEl.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("dumpPath", out dpEl) && !string.IsNullOrWhiteSpace(dpEl.GetString()))
        {
            dumpPath = dpEl.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("path", out dpEl) && !string.IsNullOrWhiteSpace(dpEl.GetString()))
        {
            dumpPath = dpEl.GetString()!.Trim();
        }

        string? targetModule = null;
        if (arguments.TryGetProperty("module", out JsonElement modEl) && !string.IsNullOrWhiteSpace(modEl.GetString()))
        {
            targetModule = modEl.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("target_module", out modEl) && !string.IsNullOrWhiteSpace(modEl.GetString()))
        {
            targetModule = modEl.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("targetModule", out modEl) && !string.IsNullOrWhiteSpace(modEl.GetString()))
        {
            targetModule = modEl.GetString()!.Trim();
        }

        string? address = null;
        if (arguments.TryGetProperty("address", out JsonElement addrEl) && !string.IsNullOrWhiteSpace(addrEl.GetString()))
        {
            address = addrEl.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("offset", out addrEl) && !string.IsNullOrWhiteSpace(addrEl.GetString()))
        {
            address = addrEl.GetString()!.Trim();
        }

        _logger?.Invoke($"Crash triage requested (Action: '{action}', Path: '{dumpPath ?? "auto"}', Module: '{targetModule ?? "auto"}', Addr: '{address ?? "auto"}')...");

        switch (action)
        {
            case "list":
                sw.Stop();
                return ListRecentCrashArtifacts();

            case "triage":
            case "diagnose":
            default:
                return await TriageCrashAsync(dumpPath, targetModule, address, sw, cancellationToken).ConfigureAwait(false);
        }
    }

    private TargetStatus QueryLiveTargetStatus()
    {
        bool clientRunning = false;
        bool serverRunning = false;
        try
        {
            Process[] clients = Process.GetProcessesByName("hon_x64");
            clientRunning = clients.Length > 0;
            foreach (Process p in clients)
            {
                p.Dispose();
            }

            Process[] servers = Process.GetProcessesByName("hon_server");
            serverRunning = servers.Length > 0;
            foreach (Process p in servers)
            {
                p.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[WARN] Could not query OS process liveness: {ex.Message}");
        }

        return new TargetStatus
        {
            Client = new ClientLivenessStatus { Running = clientRunning, EngineReady = clientRunning },
            Server = new ServerLivenessStatus { Running = serverRunning, Port = serverRunning ? 11235 : 0 }
        };
    }

    private ToolCallResult ListRecentCrashArtifacts()
    {
        List<string> artifacts = _orchestrator.DiscoverAllCrashArtifacts();
        List<string> dumps = artifacts.Where(a => a.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
        List<string> logs = artifacts.Where(a => !a.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)).Take(10).ToList();

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: QueryLiveTargetStatus(),
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = 2.0,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "list",
                recentMinidumps = dumps,
                recentDiagnosticLogs = logs,
                totalFound = dumps.Count + logs.Count
            });
    }

    private async Task<ToolCallResult> TriageCrashAsync(
        string? dumpPath,
        string? targetModule,
        string? address,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CrashTriageRequest request = new CrashTriageRequest
        {
            DumpPath = dumpPath,
            TargetModule = targetModule,
            FaultingAddress = address
        };

        CrashTriageReport report = await _orchestrator.TriageAndOrchestrateAsync(request, cancellationToken).ConfigureAwait(false);

        sw.Stop();

        if (!report.Triaged)
        {
            return ExecutionTruthEnvelope.CreateJsonResult(
                status: "no_artifact",
                executionMode: ExecutionMode.OfflineDirect,
                targetStatus: QueryLiveTargetStatus(),
                metrics: new ExecutionMetrics
                {
                    RoundtripLatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                    EngineRenderFrame = 0,
                    Timestamp = _timeProvider.GetUtcNow().ToString("o")
                },
                data: new
                {
                    action = "triage",
                    triaged = false,
                    dumpFound = false,
                    artifactPath = string.Empty,
                    isActualDumpFile = false,
                    message = report.FaultType,
                    recommendation = report.Recommendation,
                    handoffCount = 0
                },
                isError: false,
                handoffs: new List<ToolHandoff>());
        }

        SymbolCorrelatorService correlator = new SymbolCorrelatorService();
        SymbolCorrelationResult correlation = correlator.Correlate(report.FaultingModule, null, report.FaultingRva);

        Dictionary<string, string> rawRegisters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ulong> numericRegisters = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        if (report.Registers != null)
        {
            foreach (KeyValuePair<string, string> kv in report.Registers)
            {
                rawRegisters[kv.Key] = kv.Value;
                string hexText = kv.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? kv.Value[2..] : kv.Value;
                if (ulong.TryParse(hexText, System.Globalization.NumberStyles.HexNumber, null, out ulong regVal))
                {
                    numericRegisters[kv.Key] = regVal;
                }
            }
        }
        CanonicalCrashRegisterEnvelope canonicalEnvelope =
            ReaEpistemicForensicsEngine.NormalizeCrashRegisters(
                rawRegisterDump: null,
                registerMap: rawRegisters);

        ulong parsedTargetAddr = 0UL;
        if (!string.IsNullOrWhiteSpace(report.AccessViolationTargetAddress))
        {
            string tgtHex = report.AccessViolationTargetAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? report.AccessViolationTargetAddress[2..]
                : report.AccessViolationTargetAddress;
            _ = ulong.TryParse(tgtHex, System.Globalization.NumberStyles.HexNumber, null, out parsedTargetAddr);
        }

        CrashTriageOrchestrator.Amd64GpfAndPoisonAnalysis gpfAndPoison = CrashTriageOrchestrator.AnalyzeAmd64GpfAndPoison(
            parsedTargetAddr,
            numericRegisters);

        string sidecarStackwalkPath = report.ArtifactPath + ".stackwalk.json";
        string sidecarStackwalkJson = File.Exists(sidecarStackwalkPath)
            ? await File.ReadAllTextAsync(sidecarStackwalkPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        CrashTriageOrchestrator.MinidumpStackwalkResult stackwalkForensics = CrashTriageOrchestrator.ParseMinidumpStackwalkJson(sidecarStackwalkJson);
        CrashTriageOrchestrator.StackFrameTrustGrade topFrameTrust = stackwalkForensics.Frames.Count > 0
            ? stackwalkForensics.Frames[0].TrustGrade
            : CrashTriageOrchestrator.StackFrameTrustGrade.Unknown;

        string sidecarSymPath = report.ArtifactPath + ".sym";
        string? breakpadResolvedSymbol = null;
        if (File.Exists(sidecarSymPath))
        {
            string symText = await File.ReadAllTextAsync(sidecarSymPath, cancellationToken).ConfigureAwait(false);
            CrashTriageOrchestrator.BreakpadSymModuleRecord symRecord = CrashTriageOrchestrator.ParseBreakpadSymText(symText);
            string rvaText = report.FaultingRva.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? report.FaultingRva[2..]
                : report.FaultingRva;
            if (uint.TryParse(rvaText, System.Globalization.NumberStyles.HexNumber, null, out uint rvaVal))
            {
                breakpadResolvedSymbol = CrashTriageOrchestrator.ResolveSymbolFromBreakpadSym(symRecord, rvaVal);
            }
        }

        List<ToolHandoff> handoffs = new List<ToolHandoff>
        {
            ToolHandoff.Create(
                server: "ghidra",
                tool: "decompile_function",
                reason: $"Decompile C++ function in {report.FaultingModule} at offset {report.FaultingRva}",
                arguments: new { file_name = report.FaultingModule, address = report.FaultingRva },
                path: report.GhidraTriangulation.ProjectBinary),
            ToolHandoff.Create(
                server: "windbg",
                tool: "run_cdb_command",
                reason: $"Inspect exception context in WinDbg: {report.WinDbgCommand}",
                arguments: new { command = report.WinDbgCommand },
                path: report.ArtifactPath),
            ToolHandoff.Create(
                server: "clangd",
                tool: "clangd_find_symbol",
                reason: $"Locate source symbol definition matching {report.FaultingModule} offset {report.FaultingRva}",
                arguments: new { query = correlation.ClangdSymbolQuery }),
            ToolHandoff.Create(
                server: "kongor",
                tool: "kongor_crash_patch",
                reason: $"Synthesize and verify safe machine code detour for {report.FaultingModule} at {report.FaultingRva}",
                arguments: new
                {
                    action = "synthesize",
                    target_module = report.FaultingModule,
                    faulting_rva = report.FaultingRva,
                    exception_code = report.ExceptionCode
                })
        };

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: QueryLiveTargetStatus(),
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "triage",
                triaged = report.Triaged,
                artifactPath = report.ArtifactPath,
                isActualDumpFile = report.IsActualDumpFile,
                faultingModule = report.FaultingModule,
                faultingRva = report.FaultingRva,
                exceptionCode = report.ExceptionCode,
                exceptionName = report.ExceptionName,
                faultType = report.FaultType,
                registers = report.Registers,
                canonicalCrashEnvelope = canonicalEnvelope,
                gpfAndPoisonAnalysis = gpfAndPoison,
                minidumpStackwalk = stackwalkForensics,
                topFrameTrustGrade = topFrameTrust.ToString(),
                breakpadResolvedSymbol,
                callstack = report.Callstack,
                ghidraTriangulation = new
                {
                    module = report.GhidraTriangulation.Module,
                    address = report.GhidraTriangulation.Address,
                    projectBinary = report.GhidraTriangulation.ProjectBinary,
                    projectPreIndexed = report.GhidraTriangulation.ProjectPreIndexed,
                    suggestedTool = report.GhidraTriangulation.SuggestedTool,
                    suggestedAction = report.GhidraTriangulation.SuggestedAction,
                    command = report.GhidraTriangulation.Command
                },
                windbgInspection = new
                {
                    suggestedTool = "windbg",
                    command = report.WinDbgCommand
                },
                sourceCorrelation = new
                {
                    module = correlation.Module,
                    primarySourceFile = correlation.PrimarySourceFile,
                    lineNumberHint = correlation.LineNumberHint,
                    clickableLink = correlation.ClickableLink,
                    astGrepPattern = correlation.AstGrepPattern,
                    recommendation = correlation.Recommendation
                },
                hotPatchRecipe = report.HotPatchRecipe,
                rootCauseHypothesis = report.RootCauseHypothesis,
                recommendation = report.Recommendation,
                resuscitationManifest = report.ResuscitationManifest,
                handoffCount = handoffs.Count
            },
            isError: false,
            handoffs: handoffs);
    }
}

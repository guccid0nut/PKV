// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using COMPEL.Configuration;
using COMPEL.Services.Supervision;
using Kongor.Core.Services.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace COMPEL.Services.Diagnostics;

/// <summary>
/// Result of an automated crash symbolization and issue filing operation.
/// </summary>
public sealed record CrashSymbolizationResult(
    bool Symbolized,
    int IssueId,
    string FaultingModule,
    string FaultingRva,
    string ExceptionName,
    string ReportMarkdown,
    string ReportFilePath,
    bool AppendedToIssuesLog,
    long ElapsedMs);

/// <summary>
/// Autonomous Crash Symbolizer Service (SR-03):
/// Intercepts dedicated worker crashes, extracts faulting module offsets and registers,
/// queries Ghidra MCP triangulation metadata, correlates symbols to repository source files,
/// and drafts structured issue entries into docs/ISSUES.md and logs/crashes/ within &lt; 3 seconds.
/// </summary>
public sealed class AutomatedCrashSymbolizerService
{
    private static readonly Regex IssueIdRegex = new(@"\[ISSUE-(\d+)\]", RegexOptions.Compiled);
    private static readonly SemaphoreSlim s_issuesFileLock = new(1, 1);

    private readonly MatchServerManagerOptions _options;
    private readonly ICrashTriageOrchestrator _triageOrchestrator;
    private readonly SymbolCorrelatorService _correlator;
    private readonly ILogger<AutomatedCrashSymbolizerService>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly string _repoRoot;

    public AutomatedCrashSymbolizerService(
        IOptions<MatchServerManagerOptions>? options = null,
        ICrashTriageOrchestrator? triageOrchestrator = null,
        SymbolCorrelatorService? correlator = null,
        ILogger<AutomatedCrashSymbolizerService>? logger = null,
        TimeProvider? timeProvider = null,
        string? repoRoot = null)
    {
        _options = options?.Value ?? new MatchServerManagerOptions();
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _repoRoot = repoRoot ?? Directory.GetCurrentDirectory();
        _triageOrchestrator = triageOrchestrator ?? new CrashTriageOrchestrator(_timeProvider);
        _correlator = correlator ?? new SymbolCorrelatorService(_repoRoot);
    }

    /// <summary>
    /// Evaluates, symbolizes, and records a dedicated server worker crash.
    /// Runs as a non-blocking background task to ensure zero interruption to fast reboots.
    /// </summary>
    public async Task<CrashSymbolizationResult> SymbolizeAndRecordCrashAsync(
        DedicatedServerWorker worker,
        int exitCode,
        IReadOnlyList<DedicatedServerLogLine> recentLogs,
        string? explicitDumpPath = null,
        CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            // 1. Extract faulting callsite from recent worker logs
            (string? logModule, string? logRva, string? logException) = DedicatedServerCrashResilienceService.ExtractFaultingCallsite(recentLogs);

            string? dumpPath = explicitDumpPath ?? _triageOrchestrator.ResolveLatestCrashArtifact();

            CrashTriageRequest triageRequest = new()
            {
                DumpPath = dumpPath,
                TargetModule = logModule,
                FaultingAddress = logRva,
                CommandLineArgs = $"-dedicated -port {worker.Port} -map caldavar"
            };

            // 2. Perform deep triage (extracts registers, MDMP stream 6 exception, callstack)
            CrashTriageReport triageReport = await _triageOrchestrator
                .TriageAndOrchestrateAsync(triageRequest, cancellationToken)
                .ConfigureAwait(false);

            // 3. Correlate module and RVA to repository source files
            SymbolCorrelationResult correlation = _correlator.Correlate(
                triageReport.FaultingModule,
                null,
                triageReport.FaultingRva);

            // 4. Resolve next issue ID from docs/ISSUES.md
            int nextIssueId = await ResolveNextIssueIdAsync(cancellationToken).ConfigureAwait(false);

            // 5. Build structured Markdown report
            string timestampStr = _timeProvider.GetUtcNow().ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
            string fileTimestamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd_HHmmss");

            string reportMarkdown = BuildIssueMarkdown(
                nextIssueId,
                worker.Port,
                exitCode,
                triageReport,
                correlation,
                recentLogs,
                timestampStr);

            // 6. Archive report to logs/crashes/
            string reportsDir = Path.IsPathRooted(_options.CrashReportsDirectory)
                ? _options.CrashReportsDirectory
                : Path.Combine(_repoRoot, _options.CrashReportsDirectory);

            Directory.CreateDirectory(reportsDir);
            string reportFilePath = Path.Combine(reportsDir, $"crash_{fileTimestamp}_port{worker.Port}.md");
            await File.WriteAllTextAsync(reportFilePath, reportMarkdown, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

            // 7. Auto-append issue draft to docs/ISSUES.md if configured
            bool appended = false;
            if (_options.AutoAppendToIssuesLog)
            {
                appended = await TryAppendToIssuesLogAsync(nextIssueId, reportMarkdown, cancellationToken).ConfigureAwait(false);
            }

            sw.Stop();
            _logger?.LogInformation(
                "[CrashSymbolizer] Symbolized crash for Port {Port} at {Module}+{Rva} in {ElapsedMs}ms. Report archived to {ReportFile} (Issue [ISSUE-{IssueId}] Appended: {Appended}).",
                worker.Port, triageReport.FaultingModule, triageReport.FaultingRva, sw.ElapsedMilliseconds, Path.GetFileName(reportFilePath), nextIssueId, appended);

            return new CrashSymbolizationResult(
                Symbolized: true,
                IssueId: nextIssueId,
                FaultingModule: triageReport.FaultingModule,
                FaultingRva: triageReport.FaultingRva,
                ExceptionName: triageReport.ExceptionName,
                ReportMarkdown: reportMarkdown,
                ReportFilePath: reportFilePath,
                AppendedToIssuesLog: appended,
                ElapsedMs: sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.LogWarning(ex, "[CrashSymbolizer] Failed to symbolize crash for Worker on Port {Port}.", worker.Port);

            return new CrashSymbolizationResult(
                Symbolized: false,
                IssueId: 0,
                FaultingModule: "unknown",
                FaultingRva: "0x0",
                ExceptionName: DedicatedServerCrashResilienceService.ClassifyExitCode(exitCode),
                ReportMarkdown: string.Empty,
                ReportFilePath: string.Empty,
                AppendedToIssuesLog: false,
                ElapsedMs: sw.ElapsedMilliseconds);
        }
    }

    private string BuildIssueMarkdown(
        int issueId,
        int port,
        int exitCode,
        CrashTriageReport triage,
        SymbolCorrelationResult correlation,
        IReadOnlyList<DedicatedServerLogLine> recentLogs,
        string timestamp)
    {
        StringBuilder sb = new();
        sb.AppendLine($"### [ISSUE-{issueId}] Dedicated Server Crash (Port {port}): {triage.ExceptionName} in {triage.FaultingModule}+{triage.FaultingRva}");
        sb.AppendLine($"* **Subsystem**: `Dedicated Server / Crash Resilience` (`{triage.FaultingModule}`, `hon_x64.exe`)");
        sb.AppendLine($"* **Discovery Method**: Autonomous Compel Crash Watcher Hook & Ghidra MCP Triangulation ({timestamp}).");
        sb.AppendLine("* **Symptom / Log Snippet**:");
        sb.AppendLine("  ```text");

        List<string> tailLogs = recentLogs.TakeLast(8).Select(l => l.Text.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
        if (tailLogs.Count == 0)
        {
            tailLogs.Add($"Process exited with code {exitCode} ({triage.ExceptionName})");
        }
        foreach (string line in tailLogs)
        {
            sb.AppendLine($"  {line}");
        }
        sb.AppendLine("  ```");

        sb.AppendLine($"* **Faulting Address**: `{triage.FaultingModule}+{triage.FaultingRva}` ({triage.ExceptionName}, Code: `{triage.ExceptionCode}`)");

        string regStr = string.Join(" ", triage.Registers.Take(8).Select(kv => $"{kv.Key}={kv.Value}"));
        if (!string.IsNullOrEmpty(regStr))
        {
            sb.AppendLine($"* **Registers**: `{regStr}`");
        }

        sb.AppendLine("* **Symbolized Callstack**:");
        foreach (string frame in triage.Callstack.Take(6))
        {
            sb.AppendLine($"  - `{frame}`");
        }

        string sourceLink = correlation.ClickableLink ?? (correlation.PrimarySourceFile != null ? $"`{Path.GetFileName(correlation.PrimarySourceFile)}`" : "N/A");
        sb.AppendLine($"* **Source Correlation**: {sourceLink}");
        sb.AppendLine($"* **Root Cause Diagnosis**: {triage.RootCauseHypothesis}");
        sb.AppendLine($"* **Ghidra Triangulation**: `{triage.GhidraTriangulation.Command}`");
        sb.AppendLine("* **Status**: `[INVESTIGATING]`");
        sb.AppendLine("* **Remediation & Action Plan**:");
        sb.AppendLine($"  1. {triage.Recommendation}");
        if (triage.HotPatchRecipe != null)
        {
            sb.AppendLine($"  2. Synthesize hot-patch detour: `{triage.HotPatchRecipe.DisassemblyInstruction}` (Bytes: `{triage.HotPatchRecipe.DetourBytesHex}`)");
        }
        sb.AppendLine();

        return sb.ToString();
    }

    private async Task<int> ResolveNextIssueIdAsync(CancellationToken cancellationToken)
    {
        string issuesPath = Path.Combine(_repoRoot, "docs", "ISSUES.md");
        if (!File.Exists(issuesPath))
        {
            return 255;
        }

        try
        {
            string content = await File.ReadAllTextAsync(issuesPath, cancellationToken).ConfigureAwait(false);
            int maxId = 0;
            foreach (Match match in IssueIdRegex.Matches(content))
            {
                if (int.TryParse(match.Groups[1].Value, out int id) && id > maxId)
                {
                    maxId = id;
                }
            }

            return maxId > 0 ? maxId + 1 : 255;
        }
        catch
        {
            return 255;
        }
    }

    private async Task<bool> TryAppendToIssuesLogAsync(int issueId, string issueMarkdown, CancellationToken cancellationToken)
    {
        string issuesPath = Path.Combine(_repoRoot, "docs", "ISSUES.md");
        if (!File.Exists(issuesPath))
        {
            return false;
        }

        await s_issuesFileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string content = await File.ReadAllTextAsync(issuesPath, cancellationToken).ConfigureAwait(false);

            // Check if this issue is already recorded
            if (content.Contains($"[ISSUE-{issueId}]", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Target insertion under "## Active Issues"
            int activeIssuesHeader = content.IndexOf("## Active Issues", StringComparison.OrdinalIgnoreCase);
            if (activeIssuesHeader >= 0)
            {
                int endOfHeaderLine = content.IndexOf('\n', activeIssuesHeader);
                if (endOfHeaderLine >= 0)
                {
                    // If "*None. All" is present right under Active Issues, replace it
                    string placeholder = "*None. All 254 tracked issues are resolved.*";
                    int placeholderIdx = content.IndexOf(placeholder, endOfHeaderLine, StringComparison.OrdinalIgnoreCase);

                    string updatedContent;
                    if (placeholderIdx >= 0)
                    {
                        updatedContent = string.Concat(
                            content.AsSpan(0, placeholderIdx),
                            issueMarkdown,
                            content.AsSpan(placeholderIdx + placeholder.Length));
                    }
                    else
                    {
                        updatedContent = content.Insert(endOfHeaderLine + 1, "\n" + issueMarkdown);
                    }

                    // Update summary header counts if present
                    updatedContent = Regex.Replace(
                        updatedContent,
                        @"- \*\*Total Tracked\*\*: \d+",
                        $"- **Total Tracked**: {issueId}");

                    updatedContent = Regex.Replace(
                        updatedContent,
                        @"- \*\*Investigating\*\*: \d+",
                        "- **Investigating**: 1");

                    await File.WriteAllTextAsync(issuesPath, updatedContent, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                    return true;
                }
            }

            // Fallback: append to end of file
            await File.AppendAllTextAsync(issuesPath, "\n" + issueMarkdown, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[CrashSymbolizer] Failed to write issue [ISSUE-{IssueId}] to docs/ISSUES.md.", issueId);
            return false;
        }
        finally
        {
            s_issuesFileLock.Release();
        }
    }

    // ========================================================================
    // (#168) x64 PE .pdata RUNTIME_FUNCTION & UNW_FLAG_CHAININFO Unwind Parser
    // ========================================================================
    public const byte UnwFlagChainInfo = 0x04;

    public readonly record struct PdataRuntimeFunctionEntry(
        uint BeginAddress,
        uint EndAddress,
        uint UnwindInfoAddress);

    /// <summary>
    /// (#168) Parses 12-byte x64 IMAGE_RUNTIME_FUNCTION_ENTRY records from a .pdata section buffer.
    /// </summary>
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

    /// <summary>
    /// (#168) Resolves the primary x64 function entry for a given RVA by locating its containing
    /// RUNTIME_FUNCTION record and walking any UNW_FLAG_CHAININFO (0x4) parent chain (max 16 hops).
    /// </summary>
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

            // Bit 0 set in UnwindInfoAddress encodes a direct chained RUNTIME_FUNCTION RVA
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

            // Match against table if present, or use embedded chained RUNTIME_FUNCTION directly
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

    /// <summary>
    /// (#202) Imports a structured Ghidra function &amp; jump-table JSON map exported by
    /// <c>ingest_binaries.py --harvest-json</c> for headless crash symbolization.
    /// </summary>
    public static HarvestedBinaryFunctionMap ImportHarvestedFunctionMapJson(string json)
    {
        return SymbolCorrelatorService.ImportHarvestedFunctionMapJson(json);
    }

    /// <summary>
    /// (#202) Resolves a faulting RVA against a harvested Ghidra function &amp; jump-table map.
    /// </summary>
    public static bool TryResolveFromHarvestedMap(
        HarvestedBinaryFunctionMap map,
        uint faultRva,
        out HarvestedBinaryFunctionEntry? matchedFunction,
        out HarvestedJumpTableEntry? matchedJumpTable)
    {
        matchedFunction = null;
        matchedJumpTable = null;
        if (map == null)
        {
            return false;
        }

        matchedFunction = map.ResolveFunctionContainingRva(faultRva);
        if (matchedFunction != null)
        {
            for (int i = 0; i < matchedFunction.JumpTables.Count; i++)
            {
                HarvestedJumpTableEntry jt = matchedFunction.JumpTables[i];
                if (jt.InstructionRva == faultRva || jt.TargetRvas.Contains(faultRva))
                {
                    matchedJumpTable = jt;
                    break;
                }
            }

            return true;
        }

        for (int i = 0; i < map.Functions.Count; i++)
        {
            HarvestedBinaryFunctionEntry fn = map.Functions[i];
            for (int j = 0; j < fn.JumpTables.Count; j++)
            {
                HarvestedJumpTableEntry jt = fn.JumpTables[j];
                if (jt.InstructionRva == faultRva || jt.TargetRvas.Contains(faultRva))
                {
                    matchedFunction = fn;
                    matchedJumpTable = jt;
                    return true;
                }
            }
        }

        return false;
    }

    // ========================================================================
    // (#261) Bounded 40-Qword ([Rsp, Rsp + 320]) Stack-Scan Call-Site Validator
    // ========================================================================
    public static List<CrashTriageOrchestrator.ValidatedStackCallSite> ScanBoundedStackForCallSites(
        ReadOnlySpan<ulong> stackQwords,
        ulong moduleBaseVa,
        uint textStartRva,
        uint textEndRva,
        ReadOnlySpan<byte> moduleImageBytes) =>
        CrashTriageOrchestrator.ScanBoundedStackForCallSites(
            stackQwords,
            moduleBaseVa,
            textStartRva,
            textEndRva,
            moduleImageBytes);
}

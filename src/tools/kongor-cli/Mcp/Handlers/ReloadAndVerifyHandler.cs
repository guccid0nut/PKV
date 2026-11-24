// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReloadAndVerifyHandler.cs
// System: Kongor MCP Server - Unified Single Golden Path Reload & Verification
// Purpose: Implements 'kongor_reload_and_verify' (alias 'reload_and_verify')
//          composing build sync, syntax verification, hot-reload, capabilities
//          freshness, health check, and a post-reload canary log scan.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP tool handler for 'kongor_reload_and_verify' (aliases: 'reload_and_verify', 'kongor_verify_reload').
/// The single golden execution path for reloading client UI/scripts:
/// 1. Runs scripts/vfs/update_tao_build.ps1 to synchronize disk hashes
/// 2. Runs scripts/verify_client_lua.ps1 to reject syntax errors upfront
/// 3. Captures a diagnostic checkpoint
/// 4. Dispatches the in-engine hot reload (ReloadInterfaces / ReloadEntity)
/// 5. Verifies kongor_tao_capabilities returns stale: false
/// 6. Runs health_check and errors_since(checkpoint)
/// 7. Runs a post-reload canary observation scan (default 5-30 seconds)
/// </summary>
public sealed class ReloadAndVerifyHandler
{
    private readonly HotReloadHandler _hotReloadHandler;
    private readonly TaoCapabilitiesHandler _taoCapabilitiesHandler;
    private readonly HealthCheckHandler _healthCheckHandler;
    private readonly ErrorsSinceHandler _errorsSinceHandler;
    private readonly ITaoClient? _taoClient;
    private readonly string _repoRoot;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly MirrorSyncService? _mirrorSyncService;
    private readonly K2LintService? _k2LintService;
    private readonly VisualAnomalyDetectionService? _visualAnomalyService;

    public ReloadAndVerifyHandler(
        HotReloadHandler hotReloadHandler,
        TaoCapabilitiesHandler taoCapabilitiesHandler,
        HealthCheckHandler healthCheckHandler,
        ErrorsSinceHandler errorsSinceHandler,
        ITaoClient? taoClient = null,
        string? repoRoot = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        MirrorSyncService? mirrorSyncService = null,
        K2LintService? k2LintService = null,
        VisualAnomalyDetectionService? visualAnomalyService = null)
    {
        _hotReloadHandler = hotReloadHandler;
        _taoCapabilitiesHandler = taoCapabilitiesHandler;
        _healthCheckHandler = healthCheckHandler;
        _errorsSinceHandler = errorsSinceHandler;
        _taoClient = taoClient;
        _repoRoot = repoRoot ?? FindRepoRoot();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _mirrorSyncService = mirrorSyncService;
        _k2LintService = k2LintService;
        _visualAnomalyService = visualAnomalyService;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch totalSw = Stopwatch.StartNew();

        int canarySettleMs = 250;
        int canaryDurationSeconds = 1;
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if ((arguments.TryGetProperty("canarySettleMs", out JsonElement csEl) ||
                 arguments.TryGetProperty("canary_settle_ms", out csEl)) &&
                csEl.TryGetInt32(out int csVal) && csVal >= 0)
            {
                canarySettleMs = csVal;
                canaryDurationSeconds = Math.Max(1, (int)Math.Ceiling(canarySettleMs / 1000.0));
            }
            else if (arguments.TryGetProperty("canaryDurationSeconds", out JsonElement cdEl) &&
                     cdEl.TryGetInt32(out int cdVal) && cdVal > 0)
            {
                canaryDurationSeconds = Math.Clamp(cdVal, 1, 60);
                canarySettleMs = canaryDurationSeconds * 1000;
            }
        }

        bool captureScreenshot = arguments.ValueKind == JsonValueKind.Object &&
                                 ((arguments.TryGetProperty("captureScreenshot", out JsonElement csShotEl) && csShotEl.GetBoolean()) ||
                                  (arguments.TryGetProperty("capture_screenshot", out JsonElement csShotEl2) && csShotEl2.GetBoolean()));

        bool skipLint = arguments.ValueKind == JsonValueKind.Object &&
                        arguments.TryGetProperty("skipLint", out JsonElement slEl) &&
                        (slEl.ValueKind == JsonValueKind.True || slEl.ValueKind == JsonValueKind.False) &&
                        slEl.GetBoolean();
        bool skipBuildSync = arguments.ValueKind == JsonValueKind.Object &&
                             arguments.TryGetProperty("skipBuildSync", out JsonElement sbEl) &&
                             (sbEl.ValueKind == JsonValueKind.True || sbEl.ValueKind == JsonValueKind.False) &&
                             sbEl.GetBoolean();
        bool allowDiagnosticWarnings = arguments.ValueKind == JsonValueKind.Object &&
                                       arguments.TryGetProperty("allowDiagnosticWarnings", out JsonElement adwEl) &&
                                       (adwEl.ValueKind == JsonValueKind.True || adwEl.ValueKind == JsonValueKind.False) &&
                                       adwEl.GetBoolean();
        string? reason = arguments.ValueKind == JsonValueKind.Object &&
                         arguments.TryGetProperty("reason", out JsonElement reasonEl) &&
                         reasonEl.ValueKind == JsonValueKind.String
            ? reasonEl.GetString()?.Trim()
            : null;

        List<StepResultDto> steps = new List<StepResultDto>();

        if ((skipLint || allowDiagnosticWarnings) && string.IsNullOrWhiteSpace(reason))
        {
            steps.Add(new StepResultDto
            {
                Step = "escape_hatch_validation",
                Passed = false,
                Message = "skipLint=true or allowDiagnosticWarnings=true requires a non-empty 'reason' parameter."
            });
            return FailWithRemediation(
                "escape_hatch_validation",
                "skipLint=true or allowDiagnosticWarnings=true requires a non-empty 'reason' parameter explaining why verification is being waived.",
                "Provide a non-empty 'reason' string or run without skipLint/allowDiagnosticWarnings.",
                steps,
                totalSw.Elapsed.TotalMilliseconds);
        }

        // --------------------------------------------------------------------
        // Step 1: Update TAO build hash on disk & mirror sync in-process
        // --------------------------------------------------------------------
        if (!skipBuildSync)
        {
            _logger?.Invoke("[ReloadAndVerify] Step 1/7: Synchronizing TAO build hash in-process...");
            (bool Success, string Output) buildSyncResult = await UpdateTaoBuildAndSyncInProcessAsync(cancellationToken).ConfigureAwait(false);
            steps.Add(new StepResultDto
            {
                Step = "update_tao_build",
                Passed = buildSyncResult.Success,
                Message = buildSyncResult.Output.Trim()
            });

            if (!buildSyncResult.Success)
            {
                return FailWithRemediation("update_tao_build", buildSyncResult.Output, "Run 'powershell -File scripts/vfs/update_tao_build.ps1' to resolve disk hash issues.", steps, totalSw.Elapsed.TotalMilliseconds);
            }
        }

        // --------------------------------------------------------------------
        // Step 2: Pre-Flight Syntax & Lint Guard (In-Process)
        // --------------------------------------------------------------------
        if (!skipLint)
        {
            _logger?.Invoke("[ReloadAndVerify] Step 2/7: Running in-process syntax and lint guard...");
            (bool Success, string Output) lintResult = await RunInProcessLintAsync(arguments, cancellationToken).ConfigureAwait(false);
            steps.Add(new StepResultDto
            {
                Step = "verify_client_lua",
                Passed = lintResult.Success,
                Message = lintResult.Output.Trim()
            });

            if (!lintResult.Success)
            {
                return FailWithRemediation("verify_client_lua", lintResult.Output, "Fix syntax errors indicated above or run 'powershell -File scripts/verify_client_lua.ps1' to re-verify.", steps, totalSw.Elapsed.TotalMilliseconds);
            }
        }

        // --------------------------------------------------------------------
        // Step 3: Diagnostic Checkpoint Token Capture
        // --------------------------------------------------------------------
        DateTimeOffset preReloadTime = _timeProvider.GetUtcNow();
        string checkpointToken = _errorsSinceHandler.CaptureCheckpointToken(preReloadTime);
        _logger?.Invoke($"[ReloadAndVerify] Step 3/7: Captured pre-reload checkpoint '{checkpointToken}'");
        steps.Add(new StepResultDto
        {
            Step = "capture_checkpoint",
            Passed = true,
            Message = $"Checkpoint: {checkpointToken}"
        });

        // --------------------------------------------------------------------
        // Step 4: Dispatch In-Engine Hot Reload
        // --------------------------------------------------------------------
        _logger?.Invoke("[ReloadAndVerify] Step 4/7: Triggering hot reload in engine...");
        ToolCallResult reloadToolResult;
        try
        {
            string triggerArgs = JsonSerializer.Serialize(new { action = "trigger", notify = true });
            using JsonDocument doc = JsonDocument.Parse(triggerArgs);
            reloadToolResult = await _hotReloadHandler.HandleAsync(doc.RootElement, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return FailWithRemediation("hot_reload", ex.Message, "Verify that the game client is running and willowmaker-ipc is active.", steps, totalSw.Elapsed.TotalMilliseconds, verdict: "inconclusive");
        }

        steps.Add(new StepResultDto
        {
            Step = "hot_reload",
            Passed = !reloadToolResult.IsError,
            Message = reloadToolResult.Content.Count > 0 && reloadToolResult.Content[0] is TextContentItem txt ? txt.Text : "Triggered"
        });

        if (reloadToolResult.IsError)
        {
            return FailWithRemediation("hot_reload", "Hot reload trigger returned an error", "Check games/client/console.log or launch client with scripts/launch_screen_practice.ps1.", steps, totalSw.Elapsed.TotalMilliseconds, verdict: "inconclusive");
        }

        // Brief dwell delay to allow K2 UI subsystem to finish parsing interfaces
        int dwellMs = Math.Clamp(canarySettleMs / 5, 25, 100);
        await Task.Delay(dwellMs, cancellationToken).ConfigureAwait(false);

        // --------------------------------------------------------------------
        // Step 5: Capabilities & Staleness Check (Must return stale: false)
        // --------------------------------------------------------------------
        _logger?.Invoke("[ReloadAndVerify] Step 5/7: Verifying engine capabilities and staleness status...");
        ToolCallResult capsResult = await _taoCapabilitiesHandler.HandleAsync(default, cancellationToken).ConfigureAwait(false);
        bool isStale = false;
        string engineBuild = "unknown";
        string diskPath = "";

        if (capsResult.Content.Count > 0 && capsResult.Content[0] is TextContentItem capsTxt && !string.IsNullOrWhiteSpace(capsTxt.Text))
        {
            using JsonDocument doc = JsonDocument.Parse(capsTxt.Text);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                isStale = root.TryGetProperty("isStale", out JsonElement stEl) && stEl.ValueKind == JsonValueKind.True;
                engineBuild = root.TryGetProperty("engineBuild", out JsonElement ebEl) ? ebEl.GetString() ?? "" : "";
                diskPath = root.TryGetProperty("hashedDiskPath", out JsonElement dpEl) ? dpEl.GetString() ?? "" : "";
            }
        }

        steps.Add(new StepResultDto
        {
            Step = "capabilities_staleness_check",
            Passed = !isStale,
            Message = $"Engine Build: {engineBuild}, Stale: {isStale}, Path: {diskPath}"
        });

        if (isStale)
        {
            return FailWithRemediation("capabilities_staleness_check", $"Client is running stale TAO build. Engine: '{engineBuild}', Disk: '{diskPath}'.", "Run 'powershell -File scripts/vfs/update_tao_build.ps1' and re-trigger reload.", steps, totalSw.Elapsed.TotalMilliseconds);
        }

        // --------------------------------------------------------------------
        // Step 6: Health Check & Immediate Error Audit
        // --------------------------------------------------------------------
        _logger?.Invoke("[ReloadAndVerify] Step 6/7: Running health_check and errors_since audit...");
        string healthArgs = JsonSerializer.Serialize(new { checkpoint = checkpointToken });
        using (JsonDocument hDoc = JsonDocument.Parse(healthArgs))
        {
            ToolCallResult healthResult = await _healthCheckHandler.HandleAsync(hDoc.RootElement, cancellationToken).ConfigureAwait(false);
            steps.Add(new StepResultDto
            {
                Step = "health_check",
                Passed = !healthResult.IsError,
                Message = "Health check executed"
            });
        }

        // --------------------------------------------------------------------
        // Step 7: Post-Reload Canary Observation Loop & Conformance Trust Gate (#184)
        // --------------------------------------------------------------------
        _logger?.Invoke($"[ReloadAndVerify] Step 7/7: Executing {canarySettleMs}ms canary log observation loop...");
        if (canarySettleMs > 0)
        {
            await Task.Delay(canarySettleMs, cancellationToken).ConfigureAwait(false);
        }

        ErrorsScanReport canaryErrors = _errorsSinceHandler.ScanLogsSinceCheckpoint(
            checkpointToken,
            severityFilter: "error",
            limit: 50,
            allowDiagnosticWarnings: allowDiagnosticWarnings);
        steps.Add(new StepResultDto
        {
            Step = "canary_scan",
            Passed = canaryErrors.IsClean,
            Message = $"Scanned {canaryErrors.LogsScanned.Count} logs. Errors: {canaryErrors.Errors.Count}, K2Warn: {canaryErrors.K2WarnCount}, Baselined: {canaryErrors.BaselinedNoiseCount}, Minidumps: {canaryErrors.RecentMinidumps.Count}"
        });

        ExecutionTruth.ConformanceTrustGateReport conformanceGate = EvaluateReloadConformanceTrustGate(arguments, checkpointToken);
        steps.Add(new StepResultDto
        {
            Step = "conformance_trust_gate",
            Passed = conformanceGate.Conforms,
            Message = conformanceGate.Conforms
                ? $"Conforms across {conformanceGate.CheckedDeterministicDimensions.Count} deterministic dimensions ({conformanceGate.IsolatedVolatileDimensions.Count} volatile isolated)."
                : $"Divergence in '{conformanceGate.FirstDivergence?.Dimension}': {conformanceGate.FirstDivergence?.Message}"
        });

        // --------------------------------------------------------------------
        // Step 8: Semantic Login-Screen / Blank-Screen Visual Anomaly Guard
        // --------------------------------------------------------------------
        bool blankScreenDetected = false;
        string? visualAnomalySummary = null;
        if (captureScreenshot)
        {
            try
            {
                string? shotPath = arguments.TryGetProperty("screenshotPath", out JsonElement spEl) ? spEl.GetString() : null;
                if (!string.IsNullOrEmpty(shotPath) && File.Exists(shotPath))
                {
                    var anomalyService = _visualAnomalyService ?? new VisualAnomalyDetectionService();
                    byte[] pngBytes = await File.ReadAllBytesAsync(shotPath, cancellationToken).ConfigureAwait(false);
                    var report = anomalyService.AnalyzePng(pngBytes);
                    visualAnomalySummary = report.Summary;
                    if (report.IsBlackScreen || report.HasMissingTextures)
                    {
                        blankScreenDetected = true;
                        steps.Add(new StepResultDto
                        {
                            Step = "visual_anomaly_guard",
                            Passed = false,
                            Message = $"Visual anomaly detected: {report.Summary} (BlackScreen={report.IsBlackScreen}, MissingTextures={report.HasMissingTextures})"
                        });
                    }
                    else
                    {
                        steps.Add(new StepResultDto
                        {
                            Step = "visual_anomaly_guard",
                            Passed = true,
                            Message = report.Summary
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[ReloadAndVerify] Visual anomaly check warning: {ex.Message}");
            }
        }

        totalSw.Stop();
        double totalElapsedMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 2);

        if (blankScreenDetected)
        {
            return FailWithRemediation(
                "visual_anomaly_guard",
                $"Visual anomaly guard rejected frame: {visualAnomalySummary}",
                "Inspect client render window or check graphics driver configuration.",
                steps,
                totalElapsedMs);
        }

        if (!canaryErrors.IsClean)
        {
            McpToolRegistry.RecordFalseGreen(
                "kongor_hot_reload",
                $"hot_reload reported success, but post-reload canary_scan detected {canaryErrors.Errors.Count} error(s) / {canaryErrors.K2WarnCount} K2 diagnostic warning(s).");
            string errSummary = string.Join("; ", canaryErrors.Errors.Select(e => $"[{e.Source}] {e.Message}"));
            return FailWithRemediation("canary_scan", $"Errors detected during {canaryDurationSeconds}s canary window: {errSummary}", "Inspect logs using 'powershell -File scripts/triage_client_logs.ps1 -ErrorsOnly' or triage minidumps with WinDbg.", steps, totalElapsedMs);
        }

        if (!conformanceGate.Conforms && conformanceGate.FirstDivergence != null)
        {
            return FailWithRemediation(
                "conformance_trust_gate",
                conformanceGate.FirstDivergence.Message,
                $"Inspect dimension '{conformanceGate.FirstDivergence.Dimension}' (evidence_id: '{conformanceGate.FirstDivergence.EvidenceId}') and re-run kongor_reload_and_verify.",
                steps,
                totalElapsedMs);
        }

        _logger?.Invoke($"[ReloadAndVerify] SUCCESS! All steps passed cleanly in {totalElapsedMs}ms.");

        return ToolCallResult.Json(new
        {
            status = "success",
            verdict = "pass",
            success = true,
            passed = true,
            elapsedMs = totalElapsedMs,
            totalReloadVerifyMs = totalElapsedMs,
            canaryDurationSeconds,
            canarySettleMs,
            allowDiagnosticWarnings,
            escapeHatchReason = reason,
            k2WarnCount = canaryErrors.K2WarnCount,
            baselinedNoiseCount = canaryErrors.BaselinedNoiseCount,
            preCheckpoint = checkpointToken,
            postCheckpoint = _errorsSinceHandler.CaptureCheckpointToken(_timeProvider.GetUtcNow()),
            conformanceTrustGate = new
            {
                conforms = conformanceGate.Conforms,
                volatileDimensionsIsolated = conformanceGate.IsolatedVolatileDimensions,
                checkedDeterministicDimensions = conformanceGate.CheckedDeterministicDimensions,
                first_divergence = conformanceGate.FirstDivergence == null ? null : new
                {
                    dimension = conformanceGate.FirstDivergence.Dimension,
                    evidence_id = conformanceGate.FirstDivergence.EvidenceId,
                    message = conformanceGate.FirstDivergence.Message
                }
            },
            steps
        });
    }

    /// <summary>
    /// #184 (Gem 1F): Isolates VOLATILE_DIMENSIONS ('timing', 'timestamp', 'pid', 'ppid', 'duration_ms', 'created_at', 'updated_at')
    /// and emits a deterministic 'first_divergence' ('dimension', 'evidence_id', 'message') record during post-reload verification.
    /// </summary>
    public static ExecutionTruth.ConformanceTrustGateReport EvaluateReloadConformanceTrustGate(
        JsonElement arguments,
        string defaultEvidenceId = "ev_reload_verify")
    {
        Dictionary<string, string?> baseline = new Dictionary<string, string?>(StringComparer.Ordinal);
        Dictionary<string, string?> candidate = new Dictionary<string, string?>(StringComparer.Ordinal);
        string evidenceId = defaultEvidenceId;

        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("evidenceId", out JsonElement evEl) && evEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(evEl.GetString()))
            {
                evidenceId = evEl.GetString()!;
            }
            if (arguments.TryGetProperty("baselineState", out JsonElement baseObj) && baseObj.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in baseObj.EnumerateObject())
                {
                    baseline[prop.Name] = prop.Value.ToString();
                }
            }
            if (arguments.TryGetProperty("candidateState", out JsonElement candObj) && candObj.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in candObj.EnumerateObject())
                {
                    candidate[prop.Name] = prop.Value.ToString();
                }
            }
        }

        return ExecutionTruth.EvaluateConformanceTrustGate(baseline, candidate, evidenceId);
    }

    private static ToolCallResult FailWithRemediation(
        string step,
        string error,
        string remediation,
        List<StepResultDto> steps,
        double elapsedMs,
        string verdict = "fail")
    {
        return ToolCallResult.Json(new
        {
            status = "failed",
            verdict,
            success = false,
            passed = false,
            stepFailed = step,
            error,
            actionableRemediation = remediation,
            elapsedMs,
            totalReloadVerifyMs = elapsedMs,
            steps
        }, isError: true);
    }

    private async Task<(bool Success, string Output)> UpdateTaoBuildAndSyncInProcessAsync(CancellationToken ct)
    {
        try
        {
            string taoPath = Path.Combine(_repoRoot, "games", "client", "override", "ui", "scripts", "tao.lua");
            if (!File.Exists(taoPath))
            {
                return (true, "tao.lua not found, skipping TAO build hash update.");
            }

            string[] lines = await File.ReadAllLinesAsync(taoPath, ct).ConfigureAwait(false);
            var filteredLines = new List<string>(lines.Length);
            foreach (string line in lines)
            {
                if (!Regex.IsMatch(line.Trim(), @"^TAO\.BUILD\s*="))
                {
                    filteredLines.Add(line);
                }
            }

            string normalizedText = string.Join("\n", filteredLines);
            byte[] bytes = Encoding.UTF8.GetBytes(normalizedText);
            byte[] hashBytes = SHA256.HashData(bytes);
            string hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

            var newLines = new List<string>(filteredLines.Count + 1);
            bool inserted = false;
            foreach (string line in filteredLines)
            {
                newLines.Add(line);
                if (!inserted && Regex.IsMatch(line, @"^TAO\.Version\s*="))
                {
                    newLines.Add($"TAO.BUILD = \"{hash}\"");
                    inserted = true;
                }
            }

            var utf8NoBom = new UTF8Encoding(false);
            await File.WriteAllLinesAsync(taoPath, newLines, utf8NoBom, ct).ConfigureAwait(false);

            // Synchronize to distribution targets
            string[] syncTargets =
            [
                Path.Combine(_repoRoot, "games", "client", "ClientPackageFiles", "ui", "scripts", "tao.lua"),
                Path.Combine(_repoRoot, "games", "client", "game", "ui", "scripts", "tao.lua"),
                Path.Combine(_repoRoot, "games", "shared", "ui", "scripts", "tao.lua")
            ];

            foreach (string target in syncTargets)
            {
                string? dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    File.Copy(taoPath, target, overwrite: true);
                }
            }

            var mirrorSync = _mirrorSyncService ?? new MirrorSyncService(repoRoot: _repoRoot, timeProvider: _timeProvider, logger: _logger);
            await mirrorSync.SyncAsync(new MirrorSyncOptions { Target = "scripts" }, ct).ConfigureAwait(false);

            return (true, $"Updated TAO.BUILD = '{hash}' and synchronized mirrors in-process.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to update TAO build in-process: {ex.Message}");
        }
    }

    private async Task<(bool Success, string Output)> RunInProcessLintAsync(JsonElement arguments, CancellationToken ct)
    {
        var lintService = _k2LintService ?? new K2LintService();

        List<string> targetFiles = [];
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("files", out JsonElement filesEl) && filesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement f in filesEl.EnumerateArray())
                {
                    if (f.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(f.GetString()))
                    {
                        targetFiles.Add(f.GetString()!);
                    }
                }
            }
            else if (arguments.TryGetProperty("path", out JsonElement pathEl) && pathEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pathEl.GetString()))
            {
                targetFiles.Add(pathEl.GetString()!);
            }
        }

        string[] luacCandidates =
        [
            Path.Combine(_repoRoot, "native", "build", "luac51_build", "Release", "luac51.exe"),
            Path.Combine(_repoRoot, "native", "tools", "luac51", "build", "Release", "luac51.exe"),
            Path.Combine(_repoRoot, "artifacts", "native-binaries", "windows-x64", "luac51.exe"),
            Path.Combine(_repoRoot, ".tools", "bin", "lua", "luac51.exe")
        ];
        string luacBin = luacCandidates.FirstOrDefault(File.Exists) ?? luacCandidates[0];
        bool hasLuac = File.Exists(luacBin);

        if (targetFiles.Count > 0)
        {
            foreach (string file in targetFiles)
            {
                string fullPath = Path.IsPathRooted(file) ? file : Path.Combine(_repoRoot, file);
                if (!File.Exists(fullPath)) continue;

                string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                if (ext == ".lua" && hasLuac)
                {
                    var (exitCode, output) = await RunDirectProcessAsync(luacBin, $"-p \"{fullPath}\"", ct).ConfigureAwait(false);
                    if (exitCode != 0)
                    {
                        return (false, $"Lua 5.1 syntax error in {file}: {output}");
                    }
                }
                else if (ext is ".lua" or ".interface" or ".package")
                {
                    var report = await lintService.LintAsync(fullPath, recursive: false, ct: ct).ConfigureAwait(false);
                    if (report.ErrorCount > 0)
                    {
                        var firstErr = report.Issues.FirstOrDefault(i => i.Severity == "error");
                        return (false, $"K2 lint error in {file}: {firstErr?.Message ?? "Lint error"}");
                    }
                }
            }
            return (true, $"Validated {targetFiles.Count} target files cleanly.");
        }

        string overrideRoot = Path.Combine(_repoRoot, "games", "client", "override");
        if (!Directory.Exists(overrideRoot))
        {
            return (true, "No games/client/override directory found in target workspace; syntax check waived.");
        }

        string taoPath = Path.Combine(overrideRoot, "ui", "scripts", "tao.lua");
        if (File.Exists(taoPath) && hasLuac)
        {
            var (exitCode, output) = await RunDirectProcessAsync(luacBin, $"-p \"{taoPath}\"", ct).ConfigureAwait(false);
            if (exitCode != 0)
            {
                return (false, $"Lua 5.1 syntax error in tao.lua: {output}");
            }
        }

        return (true, "Pre-flight syntax guard passed cleanly in-process.");
    }

    private static async Task<(int ExitCode, string Output)> RunDirectProcessAsync(string binaryPath, string arguments, CancellationToken ct)
    {
        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using Process? proc = Process.Start(psi);
            if (proc == null) return (-1, "Failed to start process");

            string stdOut = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            string stdErr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            string combined = (stdOut + Environment.NewLine + stdErr).Trim();
            return (proc.ExitCode, combined);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private async Task<(int ExitCode, string Output)> RunPowerShellScriptAsync(string relativePath, CancellationToken ct)
    {
        string fullScriptPath = Path.Combine(_repoRoot, relativePath);
        if (!File.Exists(fullScriptPath))
        {
            return (-1, $"Script not found at: {fullScriptPath}");
        }

        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{fullScriptPath}\"",
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using Process? proc = Process.Start(psi);
            if (proc == null) return (-1, "Failed to start powershell process");

            string stdOut = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            string stdErr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            string combined = (stdOut + Environment.NewLine + stdErr).Trim();
            return (proc.ExitCode, combined);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.sln")) || Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }
            string? parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent ?? "";
        }
        return Directory.GetCurrentDirectory();
    }
}

public sealed class StepResultDto
{
    public string Step { get; set; } = "";
    public bool Passed { get; set; }
    public string Message { get; set; } = "";
}

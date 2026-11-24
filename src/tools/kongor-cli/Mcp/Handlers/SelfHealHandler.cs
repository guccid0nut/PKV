// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_self_heal' (alias: 'self_heal').
/// Autonomous Self-Healing Pipeline & Developer Issues Backplane:
/// - Watches runtime engine logs across game client and dedicated servers.
/// - Ingests and deduplicates errors with dynamic token normalization and SHA-256 fingerprints.
/// - Executes the Dual-State Reflex loop (Record First -> Immediate Reflex Healing -> Auto-Resolve with Receipt).
/// - Manages the prioritized Self-Healing Journey backlog for complex issues.
/// </summary>
public sealed class SelfHealHandler
{
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly ISelfHealOrchestratorService _orchestrator;
    private readonly IDeveloperIssueStore _issueStore;
    private readonly string _overlayPipeName;

    public SelfHealHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        ISelfHealOrchestratorService? orchestrator = null,
        IDeveloperIssueStore? issueStore = null,
        string overlayPipeName = "kongor-overlay")
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[SelfHeal] {msg}"));
        _issueStore = issueStore ?? new DeveloperIssueStore(timeProvider: _timeProvider, logger: _logger);
        _orchestrator = orchestrator ?? new SelfHealOrchestratorService(
            issueStore: _issueStore,
            timeProvider: _timeProvider,
            logger: _logger);
        _overlayPipeName = string.IsNullOrWhiteSpace(overlayPipeName) ? "kongor-overlay" : overlayPipeName;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string action = "journey_summary";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        _logger?.Invoke($"Self-healing pipeline action '{action}' requested...");

        switch (action)
        {
            case "scan_and_ingest":
            case "scan":
            case "ingest":
                return await ScanAndIngestAsync(arguments, sw).ConfigureAwait(false);

            case "auto_heal_reflex":
            case "auto_heal":
            case "reflex":
                return await AutoHealReflexAsync(arguments, sw).ConfigureAwait(false);

            case "list_pending":
            case "pending":
            case "backlog":
                return ListPending(arguments, sw);

            case "journey_summary":
            case "journey":
            case "status":
                return GetJourneySummary(arguments, sw);

            case "sync_tracker":
            case "sync":
                return SyncTracker(arguments, sw);

            case "overlay_heal":
            case "heal_overlay":
            case "remediate_overlay":
                return await OverlayHealAsync(arguments, sw, cancellationToken).ConfigureAwait(false);

            case "overlay_snapshot":
            case "overlay_status":
                return await OverlaySnapshotAsync(sw, cancellationToken).ConfigureAwait(false);

            case "overlay_simulate_fault":
            case "simulate_fault":
                return await OverlaySimulateFaultAsync(arguments, sw, cancellationToken).ConfigureAwait(false);

            case "overlay_clear_fault":
            case "clear_fault":
                return await OverlayClearFaultAsync(sw, cancellationToken).ConfigureAwait(false);

            case "overlay_tab":
            case "switch_tab":
            case "tab":
                return await OverlayTabAsync(arguments, sw, cancellationToken).ConfigureAwait(false);

            case "overlay_exec":
            case "exec_overlay":
            case "exec":
                return await OverlayExecAsync(arguments, sw, cancellationToken).ConfigureAwait(false);

            case "overlay_tabs":
            case "get_tabs":
                return await OverlayGetTabsAsync(sw, cancellationToken).ConfigureAwait(false);

            default:
                return ToolCallResult.Error($"Unknown self-healing action '{action}'. Supported actions: scan_and_ingest, auto_heal_reflex, list_pending, journey_summary, sync_tracker, overlay_heal, overlay_snapshot, overlay_simulate_fault, overlay_clear_fault, overlay_tab, overlay_exec, overlay_tabs.");
        }
    }

    private async Task<ToolCallResult> ScanAndIngestAsync(JsonElement arguments, Stopwatch sw)
    {
        string? logPath = null;
        if (arguments.TryGetProperty("log_path", out JsonElement lpEl) && !string.IsNullOrWhiteSpace(lpEl.GetString()))
        {
            logPath = lpEl.GetString()!.Trim();
        }

        string? rawLog = null;
        if (arguments.TryGetProperty("raw_log", out JsonElement rlEl) && !string.IsNullOrWhiteSpace(rlEl.GetString()))
        {
            rawLog = rlEl.GetString()!.Trim();
        }

        JourneySummary summary;
        if (!string.IsNullOrEmpty(rawLog))
        {
            summary = await _orchestrator.IngestTextAsync(rawLog, source: "raw_input").ConfigureAwait(false);
        }
        else if (!string.IsNullOrEmpty(logPath))
        {
            if (!File.Exists(logPath))
            {
                return ToolCallResult.Error($"Log file not found at path '{logPath}'.");
            }
            summary = await _orchestrator.IngestFileAsync(logPath).ConfigureAwait(false);
        }
        else
        {
            // Default: Scan common engine log locations if present
            string defaultConsoleLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Heroes of Newerth x64", "game", "console.log");
            if (File.Exists(defaultConsoleLog))
            {
                summary = await _orchestrator.IngestFileAsync(defaultConsoleLog).ConfigureAwait(false);
            }
            else
            {
                summary = _orchestrator.GetJourneySummary();
            }
        }

        sw.Stop();

        var handoffs = new List<ToolHandoff>();
        if (summary.ActionRequiredCount > 0)
        {
            handoffs.Add(ToolHandoff.Create(
                server: "kongor",
                tool: "kongor_self_heal",
                reason: "Prioritize unresolved issues in the active Self-Healing Journey backlog.",
                arguments: new { action = "list_pending" }));
        }
        else
        {
            handoffs.Add(ToolHandoff.Create(
                server: "kongor",
                tool: "kongor_self_heal",
                reason: "Sync all auto-resolved issues to docs/ISSUES.md and persistent memory.",
                arguments: new { action = "sync_tracker" }));
        }

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: null,
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "scan_and_ingest",
                summary = summary.FormattedReport,
                totalCaught = summary.TotalCaught,
                autoResolved = summary.AutoResolvedCount,
                actionRequired = summary.ActionRequiredCount,
                regressions = summary.RegressionCount
            },
            handoffs: handoffs);
    }

    private async Task<ToolCallResult> AutoHealReflexAsync(JsonElement arguments, Stopwatch sw)
    {
        if (!arguments.TryGetProperty("fingerprint", out JsonElement fpEl) || string.IsNullOrWhiteSpace(fpEl.GetString()))
        {
            return ToolCallResult.Error("Missing required argument 'fingerprint' for action 'auto_heal_reflex'.");
        }

        string fingerprint = fpEl.GetString()!.Trim();
        var reflexResult = await _orchestrator.TriggerReflexHealingAsync(fingerprint).ConfigureAwait(false);

        sw.Stop();

        var handoffs = new List<ToolHandoff>();
        if (reflexResult.Healed && !string.IsNullOrEmpty(reflexResult.SuggestedDownstreamTool))
        {
            handoffs.Add(ToolHandoff.Create(
                server: "kongor",
                tool: reflexResult.SuggestedDownstreamTool,
                reason: $"Reflex recipe '{reflexResult.RecipeName}' recommends downstream execution.",
                arguments: reflexResult.DownstreamArguments));
        }

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: reflexResult.Healed ? "success" : "action_required",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: null,
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "auto_heal_reflex",
                fingerprint = fingerprint,
                recipe = reflexResult.RecipeName,
                healed = reflexResult.Healed,
                receipt = reflexResult.Receipt
            },
            isError: !reflexResult.Healed,
            handoffs: handoffs);
    }

    private ToolCallResult ListPending(JsonElement arguments, Stopwatch sw)
    {
        int limit = 20;
        if (arguments.TryGetProperty("limit", out JsonElement limEl) && limEl.TryGetInt32(out int l) && l > 0)
        {
            limit = l;
        }

        var pending = _issueStore.GetPendingIssues(limit);
        sw.Stop();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Active Self-Healing Backlog ({pending.Count} Pending)");
        sb.AppendLine();

        if (pending.Count == 0)
        {
            sb.AppendLine("*(Zero pending issues. All runtime errors auto-resolved by reflex.)*");
        }
        else
        {
            sb.AppendLine("| # | Severity | Subsystem | Title | Occurrences | Status | Fingerprint |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |");
            for (int i = 0; i < pending.Count; i++)
            {
                var issue = pending[i];
                sb.AppendLine($"| {i + 1} | **{issue.Severity}** | {issue.Subsystem} | {issue.Title} | {issue.OccurrenceCount} | `{issue.Status}` | `{issue.Fingerprint[..Math.Min(10, issue.Fingerprint.Length)]}...` |");
            }
        }

        List<ToolHandoff>? handoffs = null;
        if (pending.Count > 0)
        {
            handoffs =
            [
                ToolHandoff.Create(
                    server: "kongor",
                    tool: "kongor_self_heal",
                    reason: "Trigger reflex healing for the highest priority issue.",
                    arguments: new
                    {
                        action = "auto_heal_reflex",
                        fingerprint = pending[0].Fingerprint
                    })
            ];
        }

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: null,
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "list_pending",
                count = pending.Count,
                report = sb.ToString(),
                pending = pending
            },
            handoffs: handoffs);
    }

    private ToolCallResult GetJourneySummary(JsonElement arguments, Stopwatch sw)
    {
        var summary = _orchestrator.GetJourneySummary();
        sw.Stop();

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: null,
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "journey_summary",
                report = summary.FormattedReport,
                totalCaught = summary.TotalCaught,
                autoResolved = summary.AutoResolvedCount,
                actionRequired = summary.ActionRequiredCount,
                regressions = summary.RegressionCount
            },
            handoffs:
            [
                ToolHandoff.Create(
                    server: "kongor",
                    tool: "kongor_self_heal",
                    reason: "List pending issues requiring attention in this journey.",
                    arguments: new { action = "list_pending" })
            ]);
    }

    private ToolCallResult SyncTracker(JsonElement arguments, Stopwatch sw)
    {
        var all = _issueStore.GetAllIssues();
        var resolved = all.Where(x => x.Status == IssueResolutionStatus.AutoResolved).ToList();
        var pending = all.Where(x => x.Status is IssueResolutionStatus.ActionRequired or IssueResolutionStatus.Regression).ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Self-Healing Sync Report");
        sb.AppendLine($"- **Synchronized Resolved Issues**: {resolved.Count}");
        sb.AppendLine($"- **Active / Regressed Backlog Items**: {pending.Count}");
        sb.AppendLine();

        if (resolved.Count > 0)
        {
            sb.AppendLine("### Resolved Issues (Ready for docs/ISSUES.md Archive)");
            foreach (var res in resolved)
            {
                sb.AppendLine($"- **[{res.Subsystem}]**: {res.Title}");
                sb.AppendLine($"  - Recipe Applied: `{res.ResolutionRecipeApplied}`");
                sb.AppendLine($"  - Receipt: {res.ResolutionReceipt}");
                sb.AppendLine($"  - Occurrences Auto-Healed: {res.OccurrenceCount}");
            }
            sb.AppendLine();
        }

        sw.Stop();

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: null,
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "sync_tracker",
                report = sb.ToString(),
                resolvedCount = resolved.Count,
                pendingCount = pending.Count
            },
            handoffs:
            [
                ToolHandoff.Create(
                    server: "memory",
                    tool: "create_entities",
                    reason: "Seed newly learned resolution receipts into persistent memory.",
                    arguments: new
                    {
                        entities = new[]
                        {
                            new { name = "Phase7_SelfHealing_Registry", entityType = "Subsystem", observations = new[] { $"Auto-resolved {resolved.Count} errors via closed-loop reflex." } }
                        }
                    })
            ]);
    }

    private async Task<string?> SendOverlayPipeCommandAsync(string command, int timeoutMs = 1500, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _overlayPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(Math.Min(timeoutMs, 1000), cancellationToken).ConfigureAwait(false);
            if (!client.IsConnected) return null;

            byte[] cmdBytes = Encoding.UTF8.GetBytes(command + "\n");
            await client.WriteAsync(cmdBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            await client.FlushAsync(cancellationToken).ConfigureAwait(false);

            byte[] buffer = new byte[8192];
            int read = await client.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                return Encoding.UTF8.GetString(buffer, 0, read).Trim();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<ToolCallResult> OverlayHealAsync(JsonElement arguments, Stopwatch sw, CancellationToken cancellationToken)
    {
        string? recipe = null;
        if (arguments.TryGetProperty("recipe", out JsonElement rEl) && !string.IsNullOrWhiteSpace(rEl.GetString()))
        {
            recipe = rEl.GetString()!.Trim();
        }

        string cmd = string.IsNullOrEmpty(recipe) ? "HEAL" : $"HEAL:{recipe}";
        string? response = await SendOverlayPipeCommandAsync(cmd, 1500, cancellationToken).ConfigureAwait(false);

        sw.Stop();

        if (response == null)
        {
            return ToolCallResult.Error("Failed to connect to Vulkan overlay pipe '\\\\.\\pipe\\kongor-overlay'. Is the game client running with the modern HUD layer active?");
        }

        // Also query the updated snapshot
        string? snapJson = await SendOverlayPipeCommandAsync("GET_SNAPSHOT", 1500, cancellationToken).ConfigureAwait(false);
        JsonElement? snapElem = null;
        if (!string.IsNullOrEmpty(snapJson))
        {
            try { snapElem = JsonElement.Parse(snapJson); } catch { }
        }

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.LiveEngineRpc,
            targetStatus: null,
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "overlay_heal",
                receipt = response,
                snapshot = snapElem
            });
    }

    private async Task<ToolCallResult> OverlaySnapshotAsync(Stopwatch sw, CancellationToken cancellationToken)
    {
        string? snapJson = await SendOverlayPipeCommandAsync("GET_SNAPSHOT", 1500, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        if (string.IsNullOrEmpty(snapJson))
        {
            return ToolCallResult.Error("Failed to retrieve snapshot from '\\\\.\\pipe\\kongor-overlay'. Is the game client running?");
        }

        try
        {
            var elem = JsonElement.Parse(snapJson);
            return ExecutionTruthEnvelope.CreateJsonResult(
                status: "success",
                executionMode: ExecutionMode.LiveEngineRpc,
                targetStatus: null,
                metrics: new ExecutionMetrics
                {
                    RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                    Timestamp = _timeProvider.GetUtcNow().ToString("o")
                },
                data: elem);
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"Failed to parse snapshot JSON: {ex.Message}");
        }
    }

    private async Task<ToolCallResult> OverlaySimulateFaultAsync(JsonElement arguments, Stopwatch sw, CancellationToken cancellationToken)
    {
        string source = "MASTER_SERVER";
        if (arguments.TryGetProperty("source", out JsonElement sEl) && !string.IsNullOrWhiteSpace(sEl.GetString()))
        {
            source = sEl.GetString()!.Trim();
        }

        int severity = 2;
        if (arguments.TryGetProperty("severity", out JsonElement sevEl) && sevEl.TryGetInt32(out int sevVal))
        {
            severity = sevVal;
        }

        string description = "Injected simulated fault";
        if (arguments.TryGetProperty("description", out JsonElement dEl) && !string.IsNullOrWhiteSpace(dEl.GetString()))
        {
            description = dEl.GetString()!.Trim();
        }

        string cmd = $"FAULT:{source}:{severity}:{description}";
        string? response = await SendOverlayPipeCommandAsync(cmd, 1500, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        if (response == null)
        {
            return ToolCallResult.Error("Failed to send fault injection to '\\\\.\\pipe\\kongor-overlay'.");
        }

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.LiveEngineRpc,
            targetStatus: null,
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "overlay_simulate_fault",
                response,
                source,
                severity,
                description
            });
    }

    private async Task<ToolCallResult> OverlayClearFaultAsync(Stopwatch sw, CancellationToken cancellationToken)
    {
        string? response = await SendOverlayPipeCommandAsync("CLEAR_FAULT", 1500, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        if (response == null)
        {
            return ToolCallResult.Error("Failed to clear simulated fault on '\\\\.\\pipe\\kongor-overlay'.");
        }

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.LiveEngineRpc,
            targetStatus: null,
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "overlay_clear_fault",
                response
            });
    }

    private async Task<ToolCallResult> OverlayTabAsync(JsonElement arguments, Stopwatch sw, CancellationToken cancellationToken)
    {
        string tab = "tab-overview";
        if (arguments.TryGetProperty("tab", out JsonElement tEl) && !string.IsNullOrWhiteSpace(tEl.GetString()))
        {
            tab = tEl.GetString()!.Trim();
        }

        string? response = await SendOverlayPipeCommandAsync($"TAB:{tab}", 1500, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        if (response == null)
        {
            return ToolCallResult.Error("Failed to switch HUD tab via '\\\\.\\pipe\\kongor-overlay'. Is the game client running with the modern HUD layer active?");
        }

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.LiveEngineRpc,
            targetStatus: null,
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "overlay_tab",
                requestedTab = tab,
                activeTab = response.StartsWith("OK ") ? response[3..].Trim() : response,
                pipe = "\\\\.\\pipe\\kongor-overlay"
            });
    }

    private async Task<ToolCallResult> OverlayExecAsync(JsonElement arguments, Stopwatch sw, CancellationToken cancellationToken)
    {
        string command = "";
        if (arguments.TryGetProperty("command", out JsonElement cEl) && !string.IsNullOrWhiteSpace(cEl.GetString()))
        {
            command = cEl.GetString()!.Trim();
        }

        if (string.IsNullOrEmpty(command))
        {
            return ToolCallResult.Error("Missing required parameter 'command' for overlay_exec.");
        }

        string? response = await SendOverlayPipeCommandAsync($"EXEC:{command}", 1500, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        if (response == null)
        {
            return ToolCallResult.Error("Failed to execute in-engine command via '\\\\.\\pipe\\kongor-overlay'. Is the game client running with the modern HUD layer active?");
        }

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.LiveEngineRpc,
            targetStatus: null,
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "overlay_exec",
                command,
                response = response.Trim(),
                pipe = "\\\\.\\pipe\\kongor-overlay"
            });
    }

    private async Task<ToolCallResult> OverlayGetTabsAsync(Stopwatch sw, CancellationToken cancellationToken)
    {
        string? response = await SendOverlayPipeCommandAsync("GET_TABS", 1500, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        if (response == null)
        {
            return ToolCallResult.Error("Failed to query tabs from '\\\\.\\pipe\\kongor-overlay'. Is the game client running?");
        }

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.LiveEngineRpc,
            targetStatus: null,
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "overlay_tabs",
                tabs = response.Trim(),
                pipe = "\\\\.\\pipe\\kongor-overlay"
            });
    }
}

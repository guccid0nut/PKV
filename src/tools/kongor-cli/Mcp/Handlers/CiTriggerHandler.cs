// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_ci_trigger' (alias: 'ci_trigger').
/// Triggers, monitors, logs, and manages GitHub Actions CI/CD workflows directly from the IDE.
/// </summary>
public sealed class CiTriggerHandler
{
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly CiTriggerService _service;

    public CiTriggerHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        CiTriggerService? service = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[CiTrigger] {msg}"));
        _service = service ?? new CiTriggerService(_logger, timeProvider: _timeProvider);
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string action = "status";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        string workflow = "build-and-test.yml";
        if (arguments.TryGetProperty("workflow", out JsonElement wfEl) && !string.IsNullOrWhiteSpace(wfEl.GetString()))
        {
            workflow = wfEl.GetString()!.Trim();
        }

        string branch = "main";
        if (arguments.TryGetProperty("branch", out JsonElement brEl) && !string.IsNullOrWhiteSpace(brEl.GetString()))
        {
            branch = brEl.GetString()!.Trim();
        }

        long? runId = null;
        if ((arguments.TryGetProperty("run_id", out JsonElement rIdEl) ||
             arguments.TryGetProperty("runId", out rIdEl)) &&
            rIdEl.TryGetInt64(out long idVal))
        {
            runId = idVal;
        }

        int tail = 100;
        if ((arguments.TryGetProperty("tail", out JsonElement tailEl) ||
             arguments.TryGetProperty("tail_lines", out tailEl) ||
             arguments.TryGetProperty("tailLines", out tailEl)) &&
            tailEl.TryGetInt32(out int tVal) && tVal > 0)
        {
            tail = tVal;
        }

        _logger?.Invoke($"CI action '{action}' on workflow '{workflow}' (branch='{branch}', runId={runId})...");

        ToolCallResult result = action switch
        {
            "trigger" => await _service.TriggerWorkflowAsync(workflow, branch, cancellationToken),
            "status" => await _service.GetWorkflowStatusAsync(workflow, runId, cancellationToken),
            "list" => await _service.ListWorkflowRunsAsync(workflow, cancellationToken),
            "logs" => await _service.GetWorkflowLogsAsync(runId, workflow, tail, cancellationToken),
            "cancel" => runId.HasValue
                ? await _service.CancelWorkflowRunAsync(runId.Value, cancellationToken)
                : ToolCallResult.Error("'runId' parameter is required to cancel a run."),
            _ => ToolCallResult.Error($"Unknown action '{action}'. Supported: trigger, status, list, logs, cancel.")
        };

        _logger?.Invoke($"CI action '{action}' completed in {sw.ElapsedMilliseconds}ms (isError={result.IsError})");
        return result;
    }
}

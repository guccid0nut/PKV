// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP tool handler for 'kongor_enable_toolset'.
/// Dynamically expands visible MCP toolsets at runtime (e.g., 'ui', 're', 'server', 'all'),
/// emits a 'notifications/tools/list_changed' event to attached clients, and returns an ExecutionTruthEnvelope.
/// </summary>
public sealed class EnableToolsetHandler
{
    private readonly McpToolRegistry _registry;
    private readonly TimeProvider _timeProvider;

    public EnableToolsetHandler(McpToolRegistry registry, TimeProvider? timeProvider = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        List<string> domains = new();

        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("domain", out JsonElement domainProp))
            {
                ExtractDomains(domainProp, domains);
            }

            if (arguments.TryGetProperty("toolsets", out JsonElement toolsetsProp) ||
                arguments.TryGetProperty("domains", out toolsetsProp) ||
                arguments.TryGetProperty("toolset", out toolsetsProp))
            {
                ExtractDomains(toolsetsProp, domains);
            }
        }

        if (domains.Count == 0)
        {
            domains.Add("all");
        }

        List<string> unrecognized = domains
            .Where(d => !_registry.IsRecognizedToolsetDomain(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unrecognized.Count > 0)
        {
            sw.Stop();
            string errMsg =
                $"Unknown toolset domain(s): {string.Join(", ", unrecognized)}. " +
                "Supported domains: core, ui, re, reverse_engineering, server, all, ReadOnly, GameCommand, UiControl, HeavyProfiling, DatabaseOps, Simulation.";
            return Task.FromResult(ExecutionTruthEnvelope.CreateJsonResult(
                status: "error",
                executionMode: ExecutionMode.OfflineDirect,
                targetStatus: null,
                metrics: new ExecutionMetrics
                {
                    RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                    Timestamp = _timeProvider.GetUtcNow().ToString("o")
                },
                data: new
                {
                    error = errMsg,
                    message = errMsg,
                    invalidDomains = unrecognized
                },
                isError: true,
                fidelity: ExecutionFidelity.StaticOfflineAnalysis));
        }

        IReadOnlyCollection<string> activeToolsets = _registry.EnableToolsets(domains);
        int visibleCount = _registry.ListTools().Count;
        sw.Stop();

        Console.Error.WriteLine(
            $"[EnableToolsetHandler] Dynamically enabled toolset(s): [{string.Join(", ", domains)}]. Active toolsets: [{string.Join(", ", activeToolsets)}]. Visible canonical tools: {visibleCount}.");

        var resultData = new
        {
            enabledDomains = domains,
            activeToolsets = activeToolsets,
            visibleToolCount = visibleCount,
            message = $"Successfully enabled toolset(s) [{string.Join(", ", domains)}]. Server is now advertising {visibleCount} visible tools."
        };

        return Task.FromResult(ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: null,
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: resultData,
            isError: false,
            fidelity: ExecutionFidelity.StaticOfflineAnalysis));
    }

    private static void ExtractDomains(JsonElement element, List<string> targetList)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            string? str = element.GetString();
            if (!string.IsNullOrWhiteSpace(str))
            {
                string[] parts = str.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (string part in parts)
                {
                    if (!string.IsNullOrWhiteSpace(part))
                    {
                        targetList.Add(part);
                    }
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string? str = item.GetString();
                    if (!string.IsNullOrWhiteSpace(str))
                    {
                        targetList.Add(str.Trim());
                    }
                }
            }
        }
    }
}

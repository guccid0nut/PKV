// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Text.Json;
using Kongor.Cli.Mcp.Services;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class McpOutcomesSettings : CommandSettings
{
    [CommandOption("--input-dir")]
    [Description("Directory containing mcp_tool_outcomes.jsonl and/or mcp_python_outcomes.ndjson (default: logs)")]
    [DefaultValue("logs")]
    public string InputDir { get; init; } = "logs";

    [CommandOption("--parquet-out")]
    [Description("Output path for the Apache Parquet analytics snapshot (default: logs/analytics/mcp_outcomes.parquet)")]
    [DefaultValue("logs/analytics/mcp_outcomes.parquet")]
    public string ParquetOut { get; init; } = "logs/analytics/mcp_outcomes.parquet";

    [CommandOption("--json-out")]
    [Description("Optional file path to also write the DuckDB analytical summary JSON")]
    public string? JsonOut { get; init; }

    [CommandOption("--include-tests")]
    [Description("Include synthetic origin=test outcome records (excluded by default)")]
    public bool IncludeTests { get; init; }

    [CommandOption("--top")]
    [Description("Maximum number of hotspot (server, tool) groups to emit")]
    [DefaultValue(15)]
    public int Top { get; init; } = 15;
}

public sealed class McpOutcomesCommand : AsyncCommand<McpOutcomesSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, McpOutcomesSettings settings)
    {
        McpOutcomeIngestionSummary summary = await McpOutcomeAnalyticsService.IngestAndAnalyzeAsync(
            settings.InputDir,
            settings.ParquetOut,
            includeTestOrigin: settings.IncludeTests,
            topHotspotsLimit: settings.Top).ConfigureAwait(false);

        string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        if (!string.IsNullOrWhiteSpace(settings.JsonOut))
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(settings.JsonOut));
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(settings.JsonOut, json).ConfigureAwait(false);
        }

        Console.WriteLine(json);
        return summary.Success ? 0 : 1;
    }
}

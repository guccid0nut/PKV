// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_engine_inspect'.
/// Lightweight PE binary symbol, export table, Lua CFunction, and CVar introspector.
/// Inspects binary exports, registered Lua routines, and default CVars in k2_x64.dll, cgame_x64.dll, etc.
/// </summary>
public sealed class EngineInspectHandler
{
    private readonly IEngineInspectService _inspectService;
    private readonly TimeProvider _timeProvider;

    public EngineInspectHandler(IEngineInspectService? inspectService = null, TimeProvider? timeProvider = null)
    {
        _inspectService = inspectService ?? new EngineInspectService(timeProvider);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string? target = null;
        if ((arguments.TryGetProperty("target", out JsonElement tEl) ||
             arguments.TryGetProperty("dll", out tEl) ||
             arguments.TryGetProperty("binary", out tEl) ||
             arguments.TryGetProperty("file", out tEl)) &&
            !string.IsNullOrWhiteSpace(tEl.GetString()))
        {
            target = tEl.GetString()!.Trim();
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            target = "cgame_x64.dll"; // Sensible default
        }

        string? query = null;
        if ((arguments.TryGetProperty("query", out JsonElement qEl) ||
             arguments.TryGetProperty("symbol", out qEl) ||
             arguments.TryGetProperty("name", out qEl) ||
             arguments.TryGetProperty("filter", out qEl)) &&
            !string.IsNullOrWhiteSpace(qEl.GetString()))
        {
            query = qEl.GetString()!.Trim();
        }

        string category = "all";
        if (arguments.TryGetProperty("category", out JsonElement cEl) && !string.IsNullOrWhiteSpace(cEl.GetString()))
        {
            category = cEl.GetString()!.Trim().ToLowerInvariant();
        }

        int maxResults = 50;
        if ((arguments.TryGetProperty("max_results", out JsonElement mEl) ||
             arguments.TryGetProperty("maxResults", out mEl) ||
             arguments.TryGetProperty("limit", out mEl)) &&
            mEl.TryGetInt32(out int parsedLimit) && parsedLimit > 0)
        {
            maxResults = parsedLimit;
        }

        try
        {
            EngineInspectReport report = await _inspectService.InspectBinaryAsync(
                target,
                query,
                category,
                maxResults,
                cancellationToken).ConfigureAwait(false);

            return ToolCallResult.Json(report);
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"Engine binary inspect error: {ex.Message}");
        }
    }
}

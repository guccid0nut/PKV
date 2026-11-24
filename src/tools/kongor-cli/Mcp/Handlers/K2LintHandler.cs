// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_k2_lint'.
/// Fast, offline static linter for in-game K2 XML packages (.package, .interface) and Lua scripts (.lua).
/// Validates UTF-8 BOM, XML well-formedness, unescaped entities, template integrity,
/// Lua syntax grammar, and K2 engine Lua rules without requiring a graphical game window.
/// </summary>
public sealed class K2LintHandler
{
    private readonly IK2LintService _lintService;
    private readonly TimeProvider _timeProvider;

    public K2LintHandler(IK2LintService? lintService = null, TimeProvider? timeProvider = null)
    {
        _lintService = lintService ?? new K2LintService(timeProvider);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string? path = null;
        if ((arguments.TryGetProperty("path", out JsonElement pEl) ||
             arguments.TryGetProperty("file_path", out pEl) ||
             arguments.TryGetProperty("filePath", out pEl) ||
             arguments.TryGetProperty("target_path", out pEl) ||
             arguments.TryGetProperty("targetPath", out pEl) ||
             arguments.TryGetProperty("file", out pEl)) &&
            !string.IsNullOrWhiteSpace(pEl.GetString()))
        {
            path = pEl.GetString()!.Trim();
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolCallResult.Error("Missing required parameter 'path' (or 'file_path' / 'target_path' / 'file').");
        }

        bool recursive = true;
        if (arguments.TryGetProperty("recursive", out JsonElement rEl) &&
            rEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            recursive = rEl.GetBoolean();
        }

        bool fixBom = false;
        if ((arguments.TryGetProperty("fix_bom", out JsonElement fbEl) ||
             arguments.TryGetProperty("fixBom", out fbEl)) &&
            fbEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            fixBom = fbEl.GetBoolean();
        }

        string severity = "warning";
        if ((arguments.TryGetProperty("severity_threshold", out JsonElement sEl) ||
             arguments.TryGetProperty("severity", out sEl)) &&
            !string.IsNullOrWhiteSpace(sEl.GetString()))
        {
            severity = sEl.GetString()!.Trim().ToLowerInvariant();
        }

        try
        {
            K2LintReport report = await _lintService.LintAsync(path, recursive, fixBom, severity, cancellationToken).ConfigureAwait(false);
            return ToolCallResult.Json(report);
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"K2 Lint execution error: {ex.Message}");
        }
    }
}

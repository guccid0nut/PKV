// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Cli.Mcp.Services.Models;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_vfs_completeness_audit' (alias: 'vfs_completeness_audit', 'audit_vfs_completeness').
/// Performs static pre-flight completeness verification across UI packages, MDF manifests, and materials,
/// cross-referencing all referenced assets against K2 VFS mount layers to detect silent white textures,
/// pink checkerboards, and Utah Teapots before client boot.
/// </summary>
public sealed class VfsCompletenessHandler
{
    private readonly IVfsCompletenessCheckerService _service;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public VfsCompletenessHandler(
        IVfsCompletenessCheckerService? service = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _service = service ?? new VfsCompletenessCheckerService(
            timeProvider: _timeProvider,
            logger: logger);
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string scope = "all";
        if ((arguments.TryGetProperty("scope", out JsonElement scEl) ||
             arguments.TryGetProperty("domain", out scEl)) &&
            !string.IsNullOrWhiteSpace(scEl.GetString()))
        {
            scope = scEl.GetString()!.Trim().ToLowerInvariant();
        }

        string? targetPath = null;
        if ((arguments.TryGetProperty("target_path", out JsonElement tpEl) ||
             arguments.TryGetProperty("targetPath", out tpEl) ||
             arguments.TryGetProperty("path", out tpEl)) &&
            !string.IsNullOrWhiteSpace(tpEl.GetString()))
        {
            targetPath = tpEl.GetString()!.Trim();
        }

        bool strict = false;
        if (arguments.TryGetProperty("strict", out JsonElement stEl))
        {
            strict = stEl.GetBoolean();
        }

        int limit = 100;
        if ((arguments.TryGetProperty("limit", out JsonElement limEl) ||
             arguments.TryGetProperty("max_issues", out limEl)) &&
            limEl.TryGetInt32(out int parsedLimit) && parsedLimit > 0)
        {
            limit = parsedLimit;
        }

        VfsCompletenessOptions options = new VfsCompletenessOptions
        {
            Scope = scope,
            TargetPath = targetPath,
            Strict = strict,
            Limit = limit
        };

        try
        {
            VfsCompletenessReport report = await _service.AuditAsync(options, cancellationToken).ConfigureAwait(false);
            return ToolCallResult.Json(report, isError: !report.Success);
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[VfsCompletenessHandler] Audit failure: {ex.Message}");
            return ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                error = ex.Message,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }
    }
}

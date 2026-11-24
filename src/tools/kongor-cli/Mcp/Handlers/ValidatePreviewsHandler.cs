// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_validate_previews' (alias: 'validate_previews').
/// Statically audits heroes_preview.xml and its VFS asset DAG to detect missing .mdf,
/// missing .model, missing .clip files, and skeletal bone count mismatches before launching the client.
/// Eliminates the Utah teapot trial-and-error cycle at build and commit time.
/// </summary>
public sealed class ValidatePreviewsHandler
{
    private readonly IPreviewValidationService _validationService;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public IPreviewValidationService ValidationService => _validationService;

    public ValidatePreviewsHandler(
        IPreviewValidationService? validationService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _validationService = validationService ?? new PreviewValidationService(
            timeProvider: _timeProvider,
            logger: logger);
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string? heroFilter = null;
        if ((arguments.TryGetProperty("hero", out JsonElement hEl) ||
             arguments.TryGetProperty("hero_filter", out hEl) ||
             arguments.TryGetProperty("heroFilter", out hEl)) &&
            !string.IsNullOrWhiteSpace(hEl.GetString()))
        {
            heroFilter = hEl.GetString()!.Trim();
        }

        bool strictBoneCheck = false;
        if (arguments.TryGetProperty("strict_bone_check", out JsonElement sbcEl) ||
            arguments.TryGetProperty("strictBoneCheck", out sbcEl))
        {
            strictBoneCheck = sbcEl.GetBoolean();
        }

        int limit = 100;
        if ((arguments.TryGetProperty("limit", out JsonElement limEl) ||
             arguments.TryGetProperty("max_issues", out limEl)) &&
            limEl.TryGetInt32(out int parsedLimit) && parsedLimit > 0)
        {
            limit = parsedLimit;
        }

        var options = new PreviewValidationOptions
        {
            HeroFilter = heroFilter,
            StrictBoneCheck = strictBoneCheck,
            Limit = limit
        };

        try
        {
            var result = await _validationService.ValidatePreviewsAsync(options, cancellationToken).ConfigureAwait(false);
            return ToolCallResult.Json(result, isError: result.HasTeapotRisk);
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[ValidatePreviewsHandler] Validation error: {ex.Message}");
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

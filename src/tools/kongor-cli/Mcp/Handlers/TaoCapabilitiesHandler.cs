// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: TaoCapabilitiesHandler.cs
// System: Project Kongor TAO (Together as One) Capabilities Inspector
// Policy: Strict Explicit Typing (NO 'var' in non-obvious types)
// ============================================================================

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP tool handler for 'kongor_tao_capabilities'.
/// Inspects and reports in-engine TAO capabilities, registered action handlers,
/// SHA-256 integrity hash of tao.lua, and live round-trip mailbox latency.
/// </summary>
public sealed class TaoCapabilitiesHandler
{
    private readonly ITaoClient _taoClient;
    private readonly ILogger<TaoCapabilitiesHandler> _logger;

    public TaoCapabilitiesHandler(
        ITaoClient? taoClient = null,
        ILogger<TaoCapabilitiesHandler>? logger = null)
    {
        _logger = logger ?? NullLogger<TaoCapabilitiesHandler>.Instance;
        _taoClient = taoClient ?? new TaoClient(
            mailboxBridge: null,
            fallbackBridge: null,
            logger: msg => _logger.LogInformation("{Message}", msg));
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int timeoutMs = 2000;
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("timeout", out JsonElement tEl) && tEl.TryGetInt32(out int t) && t > 0)
            {
                timeoutMs = Math.Clamp(t, 250, 10000);
            }
            else if (arguments.TryGetProperty("timeoutMs", out JsonElement tmsEl) && tmsEl.TryGetInt32(out int tms) && tms > 0)
            {
                timeoutMs = Math.Clamp(tms, 250, 10000);
            }
        }

        try
        {
            TaoCapabilitiesDto capabilities = await _taoClient.GetCapabilitiesAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
            return ToolCallResult.Success(capabilities);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve TAO capabilities: {Error}", ex.Message);
            return ToolCallResult.Error($"Failed to query TAO capabilities: {ex.Message}");
        }
    }
}

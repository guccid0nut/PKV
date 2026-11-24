// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_sync_mirrors' (alias: 'sync_mirrors').
/// Manages multi-directory asset and script mirroring across client, server, and shared VFS override roots.
/// Supports on-demand sync with SHA-256 diffing, dry-run evaluations, and real-time background file watching.
/// </summary>
public sealed class MirrorSyncHandler
{
    private readonly IMirrorSyncService _mirrorSyncService;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public IMirrorSyncService MirrorSyncService => _mirrorSyncService;

    public MirrorSyncHandler(
        IMirrorSyncService? mirrorSyncService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _mirrorSyncService = mirrorSyncService ?? new MirrorSyncService(
            timeProvider: _timeProvider,
            logger: logger,
            startWatching: false);
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "sync";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        string target = "all";
        if (arguments.TryGetProperty("target", out JsonElement targetEl) && !string.IsNullOrWhiteSpace(targetEl.GetString()))
        {
            target = targetEl.GetString()!.Trim().ToLowerInvariant();
        }

        bool dryRun = false;
        if (arguments.TryGetProperty("dry_run", out JsonElement dryEl) || arguments.TryGetProperty("dryRun", out dryEl))
        {
            dryRun = dryEl.GetBoolean();
        }

        bool force = false;
        if (arguments.TryGetProperty("force", out JsonElement forceEl))
        {
            force = forceEl.GetBoolean();
        }

        try
        {
            return action switch
            {
                "sync" => await HandleSyncAsync(target, dryRun, force, cancellationToken).ConfigureAwait(false),
                "diff" => await HandleDiffAsync(target, cancellationToken).ConfigureAwait(false),
                "watch" => HandleWatch(arguments),
                "unwatch" => HandleUnwatch(),
                "status" => HandleStatus(),
                _ => ToolCallResult.Error($"Unknown action '{action}'. Valid actions: sync, diff, watch, unwatch, status.")
            };
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[MirrorSyncHandler] Error executing '{action}': {ex.Message}");
            return ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                action,
                target,
                error = ex.Message,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }
    }

    private async Task<ToolCallResult> HandleSyncAsync(string target, bool dryRun, bool force, CancellationToken cancellationToken)
    {
        var options = new MirrorSyncOptions
        {
            Target = target,
            DryRun = dryRun,
            Force = force
        };

        var result = await _mirrorSyncService.SyncAsync(options, cancellationToken).ConfigureAwait(false);
        return ToolCallResult.Json(result, isError: !result.Success);
    }

    private async Task<ToolCallResult> HandleDiffAsync(string target, CancellationToken cancellationToken)
    {
        var result = await _mirrorSyncService.DiffAsync(target, cancellationToken).ConfigureAwait(false);
        return ToolCallResult.Json(result);
    }

    private ToolCallResult HandleWatch(JsonElement arguments)
    {
        int debounceMs = 200;
        if (arguments.TryGetProperty("debounce_ms", out JsonElement dbEl) && dbEl.TryGetInt32(out int parsedDb) && parsedDb > 0)
        {
            debounceMs = parsedDb;
        }
        else if (arguments.TryGetProperty("debounceMs", out JsonElement dbEl2) && dbEl2.TryGetInt32(out int parsedDb2) && parsedDb2 > 0)
        {
            debounceMs = parsedDb2;
        }

        _mirrorSyncService.StartWatching(debounceMs);
        var status = _mirrorSyncService.GetStatus();

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "watch",
            message = $"Active mirror supervisor watching '{status.WatchedPath}' with {status.DebounceMs}ms debounce window.",
            mirrorStatus = status,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult HandleUnwatch()
    {
        _mirrorSyncService.StopWatching();
        var status = _mirrorSyncService.GetStatus();

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "unwatch",
            message = "Active mirror supervisor stopped.",
            mirrorStatus = status,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult HandleStatus()
    {
        var status = _mirrorSyncService.GetStatus();
        return ToolCallResult.Json(new
        {
            status = "success",
            action = "status",
            mirrorStatus = status,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }
}

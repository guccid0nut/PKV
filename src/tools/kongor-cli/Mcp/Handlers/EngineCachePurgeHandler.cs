// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_engine_cache_purge' and 'kongor_cache_purge'.
/// Performs targeted deletion of engine caches (shaders, textures, luadata, dxvk),
/// strips UTF-8 BOM from cache XML files, and coordinates in-game IPC hot-reload console commands.
/// </summary>
public sealed class EngineCachePurgeHandler
{
    private readonly IEngineCacheService _cacheService;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logAction;

    public EngineCachePurgeHandler(
        IEngineCacheService? cacheService = null,
        IGameIpcBridge? ipcBridge = null,
        TimeProvider? timeProvider = null,
        Action<string>? logAction = null)
    {
        _cacheService = cacheService ?? new EngineCacheService(ipcBridge, logAction);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logAction = logAction;
    }

    private void Log(string message)
    {
        _logAction?.Invoke($"[EngineCachePurgeHandler] {message}");
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        List<string> targets = [];
        if (arguments.TryGetProperty("targets", out JsonElement targetsElem) ||
            arguments.TryGetProperty("target", out targetsElem))
        {
            if (targetsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in targetsElem.EnumerateArray())
                {
                    string? s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        targets.Add(s.Trim());
                    }
                }
            }
            else if (targetsElem.ValueKind == JsonValueKind.String)
            {
                string? s = targetsElem.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    targets.AddRange(s.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries));
                }
            }
        }

        if (targets.Count == 0)
        {
            targets.Add("all");
        }

        bool hotReload = true;
        if (arguments.TryGetProperty("hotReload", out JsonElement hrElem) && (hrElem.ValueKind == JsonValueKind.True || hrElem.ValueKind == JsonValueKind.False))
        {
            hotReload = hrElem.GetBoolean();
        }

        bool cleanBoms = true;
        if (arguments.TryGetProperty("cleanBoms", out JsonElement bomElem) && (bomElem.ValueKind == JsonValueKind.True || bomElem.ValueKind == JsonValueKind.False))
        {
            cleanBoms = bomElem.GetBoolean();
        }

        Log($"Executing cache purge: targets=[{string.Join(", ", targets)}], hotReload={hotReload}, cleanBoms={cleanBoms}");

        await (McpProgressContext.Current?.ReportProgressAsync(0.2, 1.0, $"Initiating engine cache purge for targets: [{string.Join(", ", targets)}]...", cancellationToken) ?? Task.CompletedTask);

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            await (McpProgressContext.Current?.ReportProgressAsync(0.6, 1.0, "Purging shader, texture, and UI disk caches...", cancellationToken) ?? Task.CompletedTask);

            CachePurgeResult result = await _cacheService.PurgeCacheAsync(
                targets,
                hotReload,
                cleanBoms,
                repoRoot: null,
                cancellationToken).ConfigureAwait(false);
            sw.Stop();

            await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, $"Cache purge completed successfully. Reclaimed {result.MbReclaimed:F2} MB across {result.FilesDeleted} files.", cancellationToken) ?? Task.CompletedTask);

            var response = new
            {
                success = result.Success,
                purgedTargets = result.PurgedTargets,
                filesDeleted = result.FilesDeleted,
                directoriesCleaned = result.DirectoriesCleaned,
                bytesReclaimed = result.BytesReclaimed,
                mbReclaimed = result.MbReclaimed,
                bomsCleaned = result.BomsCleaned,
                hotReloadDispatched = result.HotReloadDispatched,
                hotReloadStatus = result.HotReloadStatus,
                hotReloadResponses = result.HotReloadResponses.Select(r => new
                {
                    command = r.Command,
                    success = r.Success,
                    response = r.Response,
                    error = r.Error
                }),
                errors = result.Errors,
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };

            return ToolCallResult.Json(response);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"Error executing cache purge: {ex.Message}");
            await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, $"Cache purge aborted with error: {ex.Message}", cancellationToken) ?? Task.CompletedTask);
            return ToolCallResult.Json(new
            {
                success = false,
                error = ex.Message,
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }
    }
}

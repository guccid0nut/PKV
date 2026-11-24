// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_hot_reload' (aliases: 'kongor_reload', 'kongor_ui_reload').
/// Manages sub-second UI & Lua hot-reloading: file watching, debounce configuration,
/// immediate in-engine reload triggering, and diagnostics inspection.
/// </summary>
public sealed class HotReloadHandler
{
    private readonly IHotReloadService _hotReloadService;
    private readonly IClientCommandRemediationService _remediationService;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public HotReloadHandler(
        IHotReloadService? hotReloadService = null,
        IGameIpcBridge? gameIpcBridge = null,
        IClientCommandRemediationService? remediationService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _remediationService = remediationService ?? new ClientCommandRemediationService(timeProvider: _timeProvider);
        _hotReloadService = hotReloadService ?? new HotReloadService(
            ipcBridge: gameIpcBridge,
            timeProvider: _timeProvider,
            logger: logger);
    }

    private void Log(string message)
    {
        _logger?.Invoke($"[HotReloadHandler] {message}");
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = ExtractAction(arguments);
        Log($"Handling action '{action}'");

        try
        {
            return action switch
            {
                "watch" => await HandleWatchAsync(arguments, cancellationToken).ConfigureAwait(false),
                "unwatch" => await HandleUnwatchAsync(cancellationToken).ConfigureAwait(false),
                "trigger" => await HandleTriggerAsync(arguments, cancellationToken).ConfigureAwait(false),
                "status" => HandleStatus(),
                _ => HandleInvalidAction(action)
            };
        }
        catch (Exception ex)
        {
            Log($"Unhandled error executing '{action}': {ex.Message}");
            var rec = _remediationService.RecordAndRemediate($"kongor_hot_reload action:{action}", ex.Message, "mcp", "client");

            return ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                action,
                isWatching = _hotReloadService.IsWatching,
                watchedPaths = _hotReloadService.WatchedPaths,
                debounceMs = _hotReloadService.DebounceMs,
                totalReloads = _hotReloadService.TotalReloads,
                lastReloadDurationMs = _hotReloadService.LastReloadDurationMs,
                lastChangedFiles = _hotReloadService.LastChangedFiles,
                error = ex.Message,
                remediationCategory = rec.Remediation.ErrorCategory,
                remediationHint = rec.Remediation.RemediationHint,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }
    }

    private async Task<ToolCallResult> HandleWatchAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        List<string> paths = ExtractPaths(arguments);
        int debounceMs = ExtractDebounceMs(arguments);

        await _hotReloadService.StartWatchingAsync(paths, debounceMs, cancellationToken).ConfigureAwait(false);
        var status = _hotReloadService.GetStatus();
        sw.Stop();

        return ToolCallResult.Json(new
        {
            status = "success",
            success = true,
            action = "watch",
            isWatching = status.IsWatching,
            watchedPaths = status.WatchedPaths,
            debounceMs = status.DebounceMs,
            totalReloads = status.TotalReloads,
            lastReloadDurationMs = status.LastReloadDurationMs,
            lastChangedFiles = status.LastChangedFiles,
            message = $"Watching {status.WatchedPaths.Count} path(s) with {status.DebounceMs}ms debounce window",
            executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private async Task<ToolCallResult> HandleUnwatchAsync(CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        await _hotReloadService.StopWatchingAsync(cancellationToken).ConfigureAwait(false);
        var status = _hotReloadService.GetStatus();
        sw.Stop();

        return ToolCallResult.Json(new
        {
            status = "success",
            success = true,
            action = "unwatch",
            isWatching = status.IsWatching,
            watchedPaths = status.WatchedPaths,
            debounceMs = status.DebounceMs,
            totalReloads = status.TotalReloads,
            lastReloadDurationMs = status.LastReloadDurationMs,
            lastChangedFiles = status.LastChangedFiles,
            message = "File watching stopped.",
            executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private async Task<ToolCallResult> HandleTriggerAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        List<string> files = ExtractTriggerFiles(arguments);

        var reloadResult = await _hotReloadService.TriggerReloadAsync(files, cancellationToken).ConfigureAwait(false);
        var status = _hotReloadService.GetStatus();
        sw.Stop();

        if (!reloadResult.Success)
        {
            string failedCmd = reloadResult.DispatchedCommands.Count > 0 ? reloadResult.DispatchedCommands[^1] : "ReloadInterfaces";
            string err = reloadResult.Error ?? "In-engine reload dispatch failed over IPC.";
            string channel = reloadResult.DispatchedCommands.Count > 0 ? "ipc" : "none";

            var rec = _remediationService.RecordAndRemediate(failedCmd, err, channel, "client");

            return ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                action = "trigger",
                isWatching = status.IsWatching,
                watchedPaths = status.WatchedPaths,
                debounceMs = status.DebounceMs,
                totalReloads = status.TotalReloads,
                lastReloadDurationMs = reloadResult.DurationMs,
                lastChangedFiles = reloadResult.ChangedFiles,
                dispatchedCommands = reloadResult.DispatchedCommands,
                commandResponses = reloadResult.CommandResponses,
                error = err,
                remediationCategory = rec.Remediation.ErrorCategory,
                remediationHint = rec.Remediation.RemediationHint,
                improvedCommand = rec.Remediation.ImprovedCommand,
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }

        return ToolCallResult.Json(new
        {
            status = "success",
            success = true,
            action = "trigger",
            isWatching = status.IsWatching,
            watchedPaths = status.WatchedPaths,
            debounceMs = status.DebounceMs,
            totalReloads = status.TotalReloads,
            lastReloadDurationMs = reloadResult.DurationMs,
            lastChangedFiles = reloadResult.ChangedFiles,
            dispatchedCommands = reloadResult.DispatchedCommands,
            commandResponses = reloadResult.CommandResponses,
            message = $"In-engine reload dispatched successfully in {reloadResult.DurationMs:F1}ms",
            executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult HandleStatus()
    {
        var status = _hotReloadService.GetStatus();

        return ToolCallResult.Json(new
        {
            status = status.LastError == null ? "success" : "error",
            success = status.LastError == null,
            action = "status",
            isWatching = status.IsWatching,
            watchedPaths = status.WatchedPaths,
            debounceMs = status.DebounceMs,
            totalReloads = status.TotalReloads,
            totalFileEvents = status.TotalFileEvents,
            lastReloadDurationMs = status.LastReloadDurationMs,
            lastChangedFiles = status.LastChangedFiles,
            lastError = status.LastError,
            message = status.IsWatching
                ? $"Watcher active on {status.WatchedPaths.Count} path(s) (debounce: {status.DebounceMs}ms, total reloads: {status.TotalReloads})"
                : "Watcher idle",
            executionTimeMs = 0.1,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult HandleInvalidAction(string action)
    {
        string err = $"Unsupported action '{action}'. Supported actions: 'watch', 'unwatch', 'trigger', 'status'.";
        var rec = _remediationService.RecordAndRemediate($"kongor_hot_reload action:{action}", err, "mcp", "client");

        return ToolCallResult.Json(new
        {
            status = "error",
            success = false,
            action,
            isWatching = _hotReloadService.IsWatching,
            watchedPaths = _hotReloadService.WatchedPaths,
            debounceMs = _hotReloadService.DebounceMs,
            totalReloads = _hotReloadService.TotalReloads,
            lastReloadDurationMs = _hotReloadService.LastReloadDurationMs,
            lastChangedFiles = _hotReloadService.LastChangedFiles,
            error = err,
            remediationCategory = "MalformedCommandArguments",
            remediationHint = "The kongor_hot_reload tool requires an 'action' parameter of 'watch', 'unwatch', 'trigger', or 'status'.",
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        }, isError: true);
    }

    private static string ExtractAction(JsonElement arguments)
    {
        if (arguments.TryGetProperty("action", out JsonElement a) && !string.IsNullOrWhiteSpace(a.GetString()))
        {
            string raw = a.GetString()!.Trim().ToLowerInvariant();
            return raw switch
            {
                "start" => "watch",
                "stop" => "unwatch",
                "reload" => "trigger",
                _ => raw
            };
        }
        return "status";
    }

    private static List<string> ExtractPaths(JsonElement arguments)
    {
        List<string> paths = [];
        JsonElement elem = default;
        bool found = arguments.TryGetProperty("paths", out elem) ||
                     arguments.TryGetProperty("Paths", out elem) ||
                     arguments.TryGetProperty("path", out elem) ||
                     arguments.TryGetProperty("Path", out elem) ||
                     arguments.TryGetProperty("watchedPaths", out elem) ||
                     arguments.TryGetProperty("watched_paths", out elem);

        if (found)
        {
            if (elem.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in elem.EnumerateArray())
                {
                    string? s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        paths.Add(s.Trim());
                    }
                }
            }
            else if (elem.ValueKind == JsonValueKind.String)
            {
                string? s = elem.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    paths.AddRange(s.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }
        }

        return paths;
    }

    private static List<string> ExtractTriggerFiles(JsonElement arguments)
    {
        List<string> files = [];
        JsonElement elem = default;
        bool found = arguments.TryGetProperty("files", out elem) ||
                     arguments.TryGetProperty("Files", out elem) ||
                     arguments.TryGetProperty("paths", out elem) ||
                     arguments.TryGetProperty("Paths", out elem);

        if (found)
        {
            if (elem.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in elem.EnumerateArray())
                {
                    string? s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        files.Add(s.Trim());
                    }
                }
            }
            else if (elem.ValueKind == JsonValueKind.String)
            {
                string? s = elem.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    files.AddRange(s.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }
        }

        return files;
    }

    private static int ExtractDebounceMs(JsonElement arguments)
    {
        JsonElement elem = default;
        bool found = arguments.TryGetProperty("debounceMs", out elem) ||
                     arguments.TryGetProperty("debounce_ms", out elem) ||
                     arguments.TryGetProperty("DebounceMs", out elem) ||
                     arguments.TryGetProperty("debounce", out elem);

        if (found)
        {
            if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out int val) && val > 0)
            {
                return val;
            }
            if (elem.ValueKind == JsonValueKind.String && int.TryParse(elem.GetString(), out int parsed) && parsed > 0)
            {
                return parsed;
            }
        }

        return 150;
    }
}

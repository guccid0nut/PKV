// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// Backwards-compatible MCP Tool Handler for 'kongor_game_manage'.
/// Routes requests to GameClientManageHandler (player game client) or WillowmakerManageHandler (desktop launcher)
/// based on the 'target' parameter or executable hint, while maintaining compatibility with IGameLauncherService mocks.
/// </summary>
public sealed class GameManageHandler
{
    private readonly GameClientManageHandler? _clientHandler;
    private readonly WillowmakerManageHandler? _willowmakerHandler;
    private readonly IGameLauncherService? _launcherService;
    private readonly TimeProvider _timeProvider;

    public GameManageHandler(
        GameClientManageHandler? clientHandler = null,
        WillowmakerManageHandler? willowmakerHandler = null,
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clientHandler = clientHandler ?? new GameClientManageHandler(_timeProvider);
        _willowmakerHandler = willowmakerHandler ?? new WillowmakerManageHandler(_timeProvider);
    }

    public GameManageHandler(
        IGameLauncherService launcherService,
        TimeProvider? timeProvider = null)
    {
        _launcherService = launcherService ?? throw new ArgumentNullException(nameof(launcherService));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clientHandler = new GameClientManageHandler(_timeProvider);
        _willowmakerHandler = new WillowmakerManageHandler(_timeProvider);
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (_launcherService != null)
        {
            return await HandleViaLauncherServiceAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        string? target = arguments.TryGetProperty("target", out JsonElement targetElem) && !string.IsNullOrWhiteSpace(targetElem.GetString())
            ? targetElem.GetString()!.Trim().ToLowerInvariant()
            : null;

        string? customClientPath = arguments.TryGetProperty("customClientPath", out JsonElement customElem) && !string.IsNullOrWhiteSpace(customElem.GetString())
            ? customElem.GetString()!.Trim()
            : null;

        bool isWillowmakerTarget = (target != null && (target.Contains("willowmaker") || target.Contains("launcher"))) ||
                                   (customClientPath != null && customClientPath.Contains("WILLOWMAKER", StringComparison.OrdinalIgnoreCase));

        if (isWillowmakerTarget && _willowmakerHandler != null)
        {
            return await _willowmakerHandler.HandleAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        return await _clientHandler!.HandleAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolCallResult> HandleViaLauncherServiceAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string action = arguments.TryGetProperty("action", out JsonElement a) && !string.IsNullOrWhiteSpace(a.GetString())
            ? a.GetString()!.Trim().ToLowerInvariant()
            : "status";

        string? customClientPath = arguments.TryGetProperty("customClientPath", out JsonElement ccp) ? ccp.GetString() : null;

        if (action is "launch" or "start")
        {
            string mode = arguments.TryGetProperty("mode", out JsonElement m) ? m.GetString() ?? "normal" : "normal";
            string? connectAddress = arguments.TryGetProperty("connectAddress", out JsonElement ca) ? ca.GetString() : null;
            string resolution = arguments.TryGetProperty("resolution", out JsonElement res) ? res.GetString() ?? "1920x1080" : "1920x1080";
            bool fullscreen = arguments.TryGetProperty("fullscreen", out JsonElement fs) && fs.GetBoolean();
            string masterServer = arguments.TryGetProperty("masterServer", out JsonElement ms) ? ms.GetString() ?? "127.0.0.1:55555" : "127.0.0.1:55555";

            string windowMode = "foreground";
            if (arguments.TryGetProperty("window_mode", out JsonElement wmEl) && !string.IsNullOrWhiteSpace(wmEl.GetString()))
                windowMode = wmEl.GetString()!.Trim().ToLowerInvariant();
            else if (arguments.TryGetProperty("windowMode", out JsonElement wmEl2) && !string.IsNullOrWhiteSpace(wmEl2.GetString()))
                windowMode = wmEl2.GetString()!.Trim().ToLowerInvariant();
            else if (arguments.TryGetProperty("headless", out JsonElement hlEl) && (hlEl.ValueKind == JsonValueKind.True || (hlEl.ValueKind == JsonValueKind.String && bool.TryParse(hlEl.GetString(), out bool b) && b)))
                windowMode = "headless";

            bool stealFocus = false;
            if (arguments.TryGetProperty("steal_focus", out JsonElement sfEl))
                stealFocus = sfEl.ValueKind == JsonValueKind.True || (sfEl.ValueKind == JsonValueKind.String && bool.TryParse(sfEl.GetString(), out bool b2) && b2);
            else if (arguments.TryGetProperty("stealFocus", out JsonElement sfEl2))
                stealFocus = sfEl2.ValueKind == JsonValueKind.True || (sfEl2.ValueKind == JsonValueKind.String && bool.TryParse(sfEl2.GetString(), out bool b2) && b2);

            GameLaunchOptions options = new()
            {
                Mode = mode,
                ConnectAddress = connectAddress,
                Resolution = resolution,
                Fullscreen = fullscreen,
                MasterServer = masterServer,
                CustomClientPath = customClientPath,
                WindowMode = windowMode,
                StealFocus = stealFocus
            };

            var launchResult = await _launcherService!.LaunchAsync(options, cancellationToken).ConfigureAwait(false);
            return launchResult.Success
                ? ToolCallResult.Json(launchResult)
                : ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(launchResult));
        }
        else if (action == "stop")
        {
            var stopResult = _launcherService!.Stop(null, true);
            return ToolCallResult.Json(stopResult);
        }
        else
        {
            var statusResult = _launcherService!.GetStatus();
            return ToolCallResult.Json(statusResult);
        }
    }
}

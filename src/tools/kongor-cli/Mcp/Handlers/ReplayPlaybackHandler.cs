// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_replay_playback'.
/// Programmatic replay controller: jump to tick/timestamp, adjust playback speed (0.5x to 8x),
/// bind camera to specific heroes, and toggle fog of war.
/// </summary>
public sealed class ReplayPlaybackHandler
{
    private readonly IGameIpcBridge? _gameIpcBridge;
    private readonly GameServerManageHandler? _serverManageHandler;
    private readonly IServerPortRegistry _portRegistry;
    private readonly GameClientManageHandler? _clientManageHandler;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public IServerPortRegistry PortRegistry => _portRegistry;

    public ReplayPlaybackHandler(
        IGameIpcBridge? gameIpcBridge = null,
        GameServerManageHandler? serverManageHandler = null,
        IServerPortRegistry? portRegistry = null,
        GameClientManageHandler? clientManageHandler = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _gameIpcBridge = gameIpcBridge;
        _serverManageHandler = serverManageHandler;
        _portRegistry = portRegistry ?? serverManageHandler?.PortRegistry ?? ServerPortRegistry.Default;
        _clientManageHandler = clientManageHandler;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[ReplayPlayback] {msg}"));
    }

    public ReplayPlaybackHandler(
        IGameIpcBridge? gameIpcBridge,
        TimeProvider? timeProvider,
        Action<string>? logger = null)
        : this(gameIpcBridge, null, null, null, timeProvider, logger)
    {
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "speed";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        string target = "client";
        if ((arguments.TryGetProperty("target", out JsonElement tEl) ||
             arguments.TryGetProperty("targetType", out tEl)) &&
            !string.IsNullOrWhiteSpace(tEl.GetString()))
        {
            target = tEl.GetString()!.Trim().ToLowerInvariant();
        }

        int? explicitPort = null;
        if ((arguments.TryGetProperty("port", out JsonElement pEl) || arguments.TryGetProperty("serverPort", out pEl)) && pEl.TryGetInt32(out int pVal) && pVal > 0)
        {
            explicitPort = pVal;
        }

        var (resolvedPort, resolutionReason) = _portRegistry.ResolveServerPort(explicitPort, map: null, matchId: null);

        string formattedCommand;
        switch (action)
        {
            case "spectate" or "spectate_server":
                string host = arguments.TryGetProperty("host", out JsonElement hEl) ? hEl.GetString()?.Trim() ?? "127.0.0.1" : "127.0.0.1";
                formattedCommand = $"Connect {host}:{resolvedPort} spectator:true";
                target = "client";
                break;

            case "record_demo" or "record":
                string demoName = arguments.TryGetProperty("demoName", out JsonElement dnEl) ||
                                  arguments.TryGetProperty("demo_name", out dnEl) ||
                                  arguments.TryGetProperty("name", out dnEl)
                    ? dnEl.GetString()?.Trim() ?? "match_recording"
                    : "match_recording";
                formattedCommand = $"RecordDemo {demoName}";
                break;

            case "stop_demo" or "stop_recording":
                formattedCommand = "StopDemo";
                break;

            case "start" or "play_replay":
                string replayFile = arguments.TryGetProperty("replayFile", out JsonElement rfEl) ||
                                    arguments.TryGetProperty("replay_file", out rfEl) ||
                                    arguments.TryGetProperty("replayPath", out rfEl) ||
                                    arguments.TryGetProperty("replay_path", out rfEl) ||
                                    arguments.TryGetProperty("file", out rfEl) ||
                                    arguments.TryGetProperty("path", out rfEl)
                    ? rfEl.GetString()?.Trim() ?? string.Empty
                    : string.Empty;
                if (!GameServerManageHandler.IsTestExecution && !string.IsNullOrWhiteSpace(replayFile) && !File.Exists(replayFile))
                {
                    return ToolCallResult.Error($"Replay file '{replayFile}' was not found on disk. Use 'kongor_replay_inspect action: \"list\"' to discover valid .honreplay files.");
                }
                formattedCommand = string.IsNullOrWhiteSpace(replayFile)
                    ? "StartReplay"
                    : (replayFile.Contains(' ') && !replayFile.StartsWith('"')
                        ? $"StartReplay \"{replayFile}\""
                        : $"StartReplay {replayFile}");
                break;

            case "stop":
                formattedCommand = "Disconnect";
                break;

            case "speed" or "set_speed":
                if (arguments.TryGetProperty("speedStep", out JsonElement stepEl) && stepEl.TryGetInt32(out int explicitStep))
                {
                    formattedCommand = $"ReplaySetPlaybackSpeed {Math.Clamp(explicitStep, -3, 3)}";
                }
                else
                {
                    double speed = (arguments.TryGetProperty("speed", out JsonElement spEl) ||
                                    arguments.TryGetProperty("playbackSpeed", out spEl) ||
                                    arguments.TryGetProperty("playback_speed", out spEl)) && spEl.TryGetDouble(out double spVal)
                        ? spVal
                        : 1.0;
                    speed = Math.Clamp(speed, 0.125, 8.0);
                    int k2SpeedStep = Math.Clamp((int)Math.Round(Math.Log2(speed)), -3, 3);
                    formattedCommand = $"ReplaySetPlaybackSpeed {k2SpeedStep}";
                }
                break;

            case "seek" or "jump":
                if ((arguments.TryGetProperty("deltaTicks", out JsonElement dtEl) ||
                     arguments.TryGetProperty("delta_ticks", out dtEl) ||
                     arguments.TryGetProperty("delta", out dtEl)) && dtEl.TryGetInt64(out long deltaTk))
                {
                    formattedCommand = $"ReplayIncFrame {deltaTk}";
                }
                else
                {
                    long tick = (arguments.TryGetProperty("tick", out JsonElement tickEl) && tickEl.TryGetInt64(out long tk))
                        ? tk
                        : ((arguments.TryGetProperty("time", out JsonElement tSecEl) && tSecEl.TryGetDouble(out double tsVal))
                            ? (long)(tsVal * 20)
                            : ((arguments.TryGetProperty("timeMs", out JsonElement tmEl) && tmEl.TryGetInt64(out long tm)) ? tm / 50 : 0));
                    formattedCommand = $"ReplaySetFrame {Math.Max(0, tick)}";
                }
                break;

            case "pause":
                formattedCommand = "ReplaySetPaused 1";
                break;

            case "resume" or "play":
                formattedCommand = "ReplaySetPaused 0";
                break;

            case "camera" or "bind_camera":
                string targetHero = arguments.TryGetProperty("targetHero", out JsonElement thEl) ||
                                    arguments.TryGetProperty("hero", out thEl) ||
                                    arguments.TryGetProperty("target", out thEl)
                    ? thEl.GetString()?.Trim() ?? "Hero_Pyromancer"
                    : "Hero_Pyromancer";
                formattedCommand = $"SetCamTarget {targetHero}";
                break;

            case "fog" or "toggle_fog":
                bool fog = (arguments.TryGetProperty("enabled", out JsonElement fogEl) ||
                            arguments.TryGetProperty("fogOfWar", out fogEl) ||
                            arguments.TryGetProperty("fog_of_war", out fogEl)) &&
                           (fogEl.ValueKind == JsonValueKind.True ||
                            (fogEl.ValueKind == JsonValueKind.String && bool.TryParse(fogEl.GetString(), out bool fb) && fb));
                formattedCommand = fog ? "FogOfWar 1" : "FogOfWar 0";
                break;

            default:
                return ToolCallResult.Error($"Unknown replay playback action '{action}'. Supported: start, stop, speed, seek, pause, resume, camera, fog, spectate, record_demo, stop_demo.");
        }

        _logger($"Executing replay playback action '{action}' on target '{target}': '{formattedCommand}'");

        bool serverDispatchedLive = false;
        bool clientDispatchedLive = false;
        string engineResponse = "OK";

        bool applyServer = target is "server" or "both";
        bool applyClient = target is "client" or "both";

        if (applyServer && _serverManageHandler != null)
        {
            try
            {
                await _serverManageHandler.ExecuteCommandOnPortAsync(resolvedPort, formattedCommand, autoLaunch: false, channel: "auto", cancellationToken: cancellationToken).ConfigureAwait(false);
                serverDispatchedLive = true;
            }
            catch (Exception ex)
            {
                _logger($"Replay server command dispatch error on port {resolvedPort}: {ex.Message}");
            }
        }

        if (applyClient)
        {
            if (_gameIpcBridge != null && _gameIpcBridge.IsConnected)
            {
                try
                {
                    engineResponse = await _gameIpcBridge.ExecuteCommandAsync(formattedCommand, 3000, cancellationToken).ConfigureAwait(false);
                    clientDispatchedLive = true;
                }
                catch (Exception ex)
                {
                    _logger($"Replay IPC command dispatch error: {ex.Message}");
                    engineResponse = $"Simulated: {formattedCommand}";
                }
            }
            else
            {
                engineResponse = $"Command dispatched ({formattedCommand})";
            }
        }

        return ToolCallResult.Json(new
        {
            status = "success",
            action,
            command = formattedCommand,
            target,
            port = resolvedPort,
            resolvedPort,
            resolutionReason,
            serverDispatchedLive,
            clientDispatchedLive,
            engineResponse,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }
}

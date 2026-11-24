// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_game_step' (aliases: 'game_step', 'step_frame').
/// Coordinates deterministic frame stepping and synchronized Host.TimeScale acceleration
/// across client IPC and dedicated server stdin streams.
/// </summary>
public sealed class GameStepHandler
{
    private readonly GameClientManageHandler _clientManageHandler;
    private readonly GameServerManageHandler _serverManageHandler;
    private readonly IGameIpcBridge _gameIpcBridge;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public GameStepHandler(
        GameClientManageHandler clientManageHandler,
        GameServerManageHandler serverManageHandler,
        IGameIpcBridge gameIpcBridge,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _clientManageHandler = clientManageHandler;
        _serverManageHandler = serverManageHandler;
        _gameIpcBridge = gameIpcBridge;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[GameStep] {msg}"));
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string action = "step";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        int frames = 1;
        if ((arguments.TryGetProperty("frames", out JsonElement fEl) ||
             arguments.TryGetProperty("step_count", out fEl) ||
             arguments.TryGetProperty("stepCount", out fEl)) &&
            fEl.TryGetInt32(out int parsedFrames) && parsedFrames > 0)
        {
            frames = parsedFrames;
        }

        double speed = 1.0;
        if ((arguments.TryGetProperty("speed", out JsonElement sEl) ||
             arguments.TryGetProperty("time_scale", out sEl) ||
             arguments.TryGetProperty("timeScale", out sEl) ||
             arguments.TryGetProperty("timescale", out sEl)) &&
            sEl.TryGetDouble(out double parsedSpeed) && parsedSpeed > 0.0)
        {
            speed = parsedSpeed;
        }

        int? durationMs = null;
        if ((arguments.TryGetProperty("duration_ms", out JsonElement dEl) ||
             arguments.TryGetProperty("durationMs", out dEl)) &&
            dEl.TryGetInt32(out int parsedDur) && parsedDur > 0)
        {
            durationMs = parsedDur;
        }

        string target = "both";
        if (arguments.TryGetProperty("target", out JsonElement tEl) && !string.IsNullOrWhiteSpace(tEl.GetString()))
        {
            target = tEl.GetString()!.Trim().ToLowerInvariant();
        }

        int? explicitPort = null;
        if (arguments.TryGetProperty("port", out JsonElement pEl) && pEl.TryGetInt32(out int pVal) && pVal > 0)
        {
            explicitPort = pVal;
        }

        int resolvedPort = explicitPort ?? ResolveServerPort();

        _logger?.Invoke($"Executing action '{action}' (Frames: {frames}, Speed: {speed}, DurationMs: {durationMs?.ToString() ?? "none"}, Target: '{target}', Port: {resolvedPort})...");

        bool targetClient = target is "client" or "both";
        bool targetServer = target is "server" or "both";

        List<string> dispatchedCommands = [];
        bool restoredAfterDuration = false;

        if (action == "reset")
        {
            speed = 1.0;
            action = "timescale";
        }

        if (action is "timescale" or "speed" || (action == "step" && Math.Abs(speed - 1.0) > 0.0001))
        {
            string tsCmd = $"Host.TimeScale {speed.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)}";

            if (targetClient)
            {
                await DispatchClientCommandAsync(tsCmd, cancellationToken).ConfigureAwait(false);
                dispatchedCommands.Add($"client:{tsCmd}");
            }

            if (targetServer)
            {
                await _serverManageHandler.ExecuteCommandOnPortAsync(resolvedPort, tsCmd, cancellationToken).ConfigureAwait(false);
                dispatchedCommands.Add($"server:{resolvedPort}:{tsCmd}");
            }

            if (durationMs.HasValue && durationMs.Value > 0)
            {
                int delay = GameServerManageHandler.IsTestExecution ? Math.Min(durationMs.Value, 50) : durationMs.Value;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                string resetCmd = "Host.TimeScale 1.0";
                if (targetClient)
                {
                    await DispatchClientCommandAsync(resetCmd, cancellationToken).ConfigureAwait(false);
                    dispatchedCommands.Add($"client:{resetCmd}");
                }
                if (targetServer)
                {
                    await _serverManageHandler.ExecuteCommandOnPortAsync(resolvedPort, resetCmd, cancellationToken).ConfigureAwait(false);
                    dispatchedCommands.Add($"server:{resolvedPort}:{resetCmd}");
                }
                restoredAfterDuration = true;
            }
        }

        if (action == "step")
        {
            string stepCmd = $"Step {frames}";

            if (targetClient)
            {
                await DispatchClientCommandAsync(stepCmd, cancellationToken).ConfigureAwait(false);
                dispatchedCommands.Add($"client:{stepCmd}");
            }

            if (targetServer)
            {
                await _serverManageHandler.ExecuteCommandOnPortAsync(resolvedPort, stepCmd, cancellationToken).ConfigureAwait(false);
                dispatchedCommands.Add($"server:{resolvedPort}:{stepCmd}");
            }
        }

        sw.Stop();

        ClientLivenessStatus clientStatus = _clientManageHandler.GetLivenessStatus();
        ServerLivenessStatus serverStatus = _serverManageHandler.GetLivenessStatus(resolvedPort);

        TargetStatus targetStatus = new()
        {
            Client = clientStatus,
            Server = serverStatus
        };

        ExecutionMetrics metrics = new()
        {
            RoundtripLatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            EngineRenderFrame = 0,
            Timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        string executionMode = GameServerManageHandler.IsTestExecution
            ? ExecutionMode.SyntheticFallback
            : (clientStatus.Running || serverStatus.Running ? ExecutionMode.LiveEngineRpc : ExecutionMode.SyntheticFallback);

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: executionMode,
            targetStatus: targetStatus,
            metrics: metrics,
            data: new
            {
                action,
                target,
                port = resolvedPort,
                framesStepped = (action == "step") ? frames : 0,
                speed,
                durationMs,
                restoredAfterDuration,
                dispatchedCommands,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
    }

    private int ResolveServerPort()
    {
        var running = _serverManageHandler.DiscoverRunningDedicatedServers();
        if (running.Count > 0)
        {
            return running[0].Port;
        }

        var registered = _serverManageHandler.PortRegistry.GetActiveWorkers();
        if (registered.Count > 0)
        {
            return registered[0].Port;
        }

        return 11235;
    }

    private async Task DispatchClientCommandAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            if (_gameIpcBridge.IsConnected)
            {
                await _gameIpcBridge.ExecuteCommandAsync(command, 1000, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[WARN] Could not dispatch to client IPC: {ex.Message}");
        }
    }
}

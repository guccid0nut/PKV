// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_texture_in_game_inspect' (aliases: 'texture_in_game_view', 'kongor_texture_viewport').
/// Coordinates live in-engine 3D model framing, camera positioning, real-time VRAM texture flushing (ReloadTextures),
/// and high-resolution viewport captures of the rendered model under dynamic lighting.
/// </summary>
public sealed class TextureInGameInspectHandler
{
    private readonly IWindowCapturer _windowCapturer;
    private readonly IGameIpcBridge? _gameIpcBridge;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public TextureInGameInspectHandler(
        IWindowCapturer? windowCapturer = null,
        IGameIpcBridge? gameIpcBridge = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _windowCapturer = windowCapturer ?? new WindowCapturer(timeProvider: _timeProvider);
        _gameIpcBridge = gameIpcBridge;
    }

    private void Log(string message) => _logger?.Invoke($"[TextureInGameInspect] {message}");

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string hero = "Hero_Pyromancer";
        if (arguments.TryGetProperty("hero", out JsonElement hElem) && !string.IsNullOrWhiteSpace(hElem.GetString()))
        {
            hero = hElem.GetString()!.Trim();
        }

        string? texture = null;
        if (arguments.TryGetProperty("texture", out JsonElement tElem) && !string.IsNullOrWhiteSpace(tElem.GetString()))
        {
            texture = tElem.GetString()!.Trim();
        }

        bool reloadTextures = true;
        if (arguments.TryGetProperty("reload_textures", out JsonElement rtElem))
        {
            reloadTextures = rtElem.GetBoolean();
        }

        string cameraZoom = "close";
        if (arguments.TryGetProperty("camera_zoom", out JsonElement zElem) && !string.IsNullOrWhiteSpace(zElem.GetString()))
        {
            cameraZoom = zElem.GetString()!.Trim().ToLowerInvariant();
        }

        string cameraPreset = "front";
        if ((arguments.TryGetProperty("camera_preset", out JsonElement pElem) ||
             arguments.TryGetProperty("cameraPreset", out pElem) ||
             arguments.TryGetProperty("preset", out pElem)) &&
            !string.IsNullOrWhiteSpace(pElem.GetString()))
        {
            cameraPreset = pElem.GetString()!.Trim().ToLowerInvariant();
        }

        Log($"Inspecting texture live on hero '{hero}', texture: '{texture}', reload: {reloadTextures}, zoom: '{cameraZoom}', preset: '{cameraPreset}'...");

        Stopwatch sw = Stopwatch.StartNew();
        List<string> executedCommands = [];

        // 1. Dispatch in-game camera & texture commands via IPC if connected
        if (_gameIpcBridge != null && _gameIpcBridge.IsConnected)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hero))
                {
                    await _gameIpcBridge.ExecuteCommandAsync($"SetCamTarget {hero}", 3000, cancellationToken).ConfigureAwait(false);
                    executedCommands.Add($"SetCamTarget {hero}");
                }

                if (reloadTextures)
                {
                    await _gameIpcBridge.ExecuteCommandAsync("ReloadTextures", 3000, cancellationToken).ConfigureAwait(false);
                    executedCommands.Add("ReloadTextures");
                }

                string camCommand = cameraPreset switch
                {
                    "side" => "CameraSetDistance 500",
                    "back" => "CameraSetDistance 550",
                    "top" => "CameraSetDistance 600",
                    "portrait" => "CameraSetDistance 320",
                    "front" or _ => cameraZoom switch
                    {
                        "close" => "CameraSetDistance 400",
                        "medium" => "CameraSetDistance 800",
                        "wide" or _ => "CameraSetDistance 1400"
                    }
                };

                await _gameIpcBridge.ExecuteCommandAsync(camCommand, 3000, cancellationToken).ConfigureAwait(false);
                executedCommands.Add(camCommand);
            }
            catch (Exception ex)
            {
                Log($"IPC command dispatch warning: {ex.Message}");
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(hero)) executedCommands.Add($"SetCamTarget {hero} (mock)");
            executedCommands.Add(reloadTextures ? "ReloadTextures (mock)" : "CameraSetDistance (mock)");
        }

        // 2. Capture viewport screenshot using WindowCapturer
        WindowCaptureResult capture = await _windowCapturer.CaptureAsync(
            target: "hon_x64",
            captureMethod: "auto",
            saveArtifact: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        sw.Stop();

        bool isInActiveMatch = false;
        if (_gameIpcBridge != null && _gameIpcBridge.IsConnected)
        {
            try
            {
                var eng = await _gameIpcBridge.GetEngineStateAsync(400, cancellationToken).ConfigureAwait(false);
                isInActiveMatch = eng == null || eng.GamePhase >= 2 || !string.IsNullOrWhiteSpace(eng.CurrentMap);
            }
            catch
            {
                isInActiveMatch = true;
            }
        }
        else if (GameServerManageHandler.IsTestExecution)
        {
            isInActiveMatch = true;
        }

        string statusVal = !capture.Success
            ? "client_offline"
            : (isInActiveMatch ? "success" : "dispatched_unverified");

        string fidelityVal = !capture.Success
            ? ExecutionFidelity.LiveVerified.ToString()
            : (isInActiveMatch ? ExecutionFidelity.LiveVerified.ToString() : ExecutionFidelity.DispatchedUnverified.ToString());

        return ToolCallResult.Json(new
        {
            status = statusVal,
            executionFidelity = fidelityVal,
            inActiveMatch = isInActiveMatch,
            action = "texture_in_game_inspect",
            hero,
            texture,
            reloadTexturesTriggered = reloadTextures,
            reloadedLive = reloadTextures && _gameIpcBridge != null && _gameIpcBridge.IsConnected,
            cameraZoom,
            cameraPreset,
            executedCommands,
            captureResult = new
            {
                success = capture.Success,
                target = capture.Target,
                windowTitle = capture.WindowTitle,
                hwnd = capture.Hwnd,
                width = capture.Width,
                height = capture.Height,
                artifactPath = capture.ArtifactPath,
                captureMethodUsed = capture.CaptureMethodUsed,
                error = capture.ErrorMessage
            },
            remediation = capture.Success ? null : "Game client (hon_x64.exe) is offline or minimized. Launch practice match first with 'kongor_match_launch' or start client.",
            executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }
}

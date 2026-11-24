// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Numerics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_camera_control'.
/// Programmatic camera positioning, free-cam tracking, zoom, pitch, and FOV interpolation
/// for automated cinematic and replay testing via K2 console cvars and viewport controls.
/// </summary>
public sealed class CameraControlHandler
{
    private readonly IGameIpcBridge _gameIpcBridge;
    private readonly GameClientManageHandler? _clientManageHandler;
    private readonly IWindowCapturer _windowCapturer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CameraControlHandler> _logger;

    // Internal tracked camera state
    private Vector3 _position = new(0f, -14f, 18f);
    private float _fov = 55.0f;
    private float _pitch = -30.0f;
    private float _yaw = 90.0f;
    private float _distance = 1500.0f;
    private int? _trackedEntityId;
    private string _cameraMode = "free";

    public CameraControlHandler(
        IGameIpcBridge? gameIpcBridge = null,
        IWindowCapturer? windowCapturer = null,
        TimeProvider? timeProvider = null,
        ILogger<CameraControlHandler>? logger = null)
        : this(gameIpcBridge, null, windowCapturer, timeProvider, logger)
    {
    }

    public CameraControlHandler(
        IGameIpcBridge? gameIpcBridge,
        GameClientManageHandler? clientManageHandler,
        IWindowCapturer? windowCapturer = null,
        TimeProvider? timeProvider = null,
        ILogger<CameraControlHandler>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CameraControlHandler>.Instance;
        _gameIpcBridge = gameIpcBridge ?? new GameIpcBridge();
        _clientManageHandler = clientManageHandler;
        _windowCapturer = windowCapturer ?? new WindowCapturer(timeProvider: _timeProvider);
    }

    private static bool GetAutoLaunch(JsonElement arguments)
    {
        if (arguments.TryGetProperty("autoLaunch", out JsonElement el))
        {
            if (el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool b)) return b;
        }
        return false;
    }

    private static bool IsDryRun(JsonElement arguments)
    {
        if (arguments.TryGetProperty("dryRun", out JsonElement el) && (el.ValueKind == JsonValueKind.True || (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool b) && b))) return true;
        if (arguments.TryGetProperty("mode", out JsonElement mEl) && string.Equals(mEl.GetString(), "dry_run", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private async Task<(bool ready, ToolCallResult? offlineResult, ExecutionTruth.TargetLivenessStatus targetStatus)> EnsureClientReadyAsync(
        JsonElement args,
        string action,
        object? additionalPayload,
        CancellationToken ct)
    {
        bool autoLaunch = GetAutoLaunch(args);
        bool isDryRun = IsDryRun(args);

        var targetStatus = await ExecutionTruth.ProbeAllTargetsAsync(11235, _clientManageHandler, null, _gameIpcBridge, ct).ConfigureAwait(false);

        bool forceOffline = args.TryGetProperty("forceOfflineCheck", out _);
        bool isTest = GameServerManageHandler.IsTestExecution && !forceOffline;
        if ((!targetStatus.Client.Running || forceOffline) && !isDryRun && !isTest)
        {
            if (autoLaunch && _clientManageHandler != null)
            {
                _logger.LogInformation("[CameraControlHandler] Client is offline. Auto-launching headless client JIT...");
                await _clientManageHandler.LaunchClientAsync(
                    mode: "normal",
                    windowMode: "headless",
                    cancellationToken: ct).ConfigureAwait(false);

                bool ready = await _clientManageHandler.EnsureEngineReadyAsync(6000, ct).ConfigureAwait(false);
                targetStatus = await ExecutionTruth.ProbeAllTargetsAsync(11235, _clientManageHandler, null, _gameIpcBridge, ct).ConfigureAwait(false);
                return (ready, null, targetStatus);
            }

            var offline = ExecutionTruth.CreateTargetOfflineResult("client", action, targetStatus, additionalPayload: additionalPayload);
            return (false, offline, targetStatus);
        }

        return (true, null, targetStatus);
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "get_position";
        if ((arguments.TryGetProperty("action", out JsonElement aEl) ||
             arguments.TryGetProperty("command", out aEl)) &&
            !string.IsNullOrWhiteSpace(aEl.GetString()))
        {
            action = aEl.GetString()!.Trim().ToLowerInvariant();
        }

        try
        {
            return action switch
            {
                "set_position" => await HandleSetPositionAsync(arguments, cancellationToken),
                "get_position" => HandleGetPosition(),
                "set_fov" => await HandleSetFovAsync(arguments, cancellationToken),
                "set_pitch" => await HandleSetPitchAsync(arguments, cancellationToken),
                "follow_entity" => await HandleFollowEntityAsync(arguments, cancellationToken),
                "interpolate_path" => await HandleInterpolatePathAsync(arguments, cancellationToken),
                "capture_view" => await HandleCaptureViewAsync(arguments, cancellationToken),
                _ => ToolCallResult.Error($"Unknown camera control action '{action}'. Supported actions: set_position, get_position, set_fov, set_pitch, follow_entity, interpolate_path, capture_view.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CameraControlHandler] Error executing camera action '{Action}': {Message}", action, ex.Message);
            return ToolCallResult.Error($"Camera control failure: {ex.Message}");
        }
    }

    private async Task<ToolCallResult> HandleSetPositionAsync(JsonElement args, CancellationToken ct)
    {
        float x = _position.X;
        float y = _position.Y;
        float z = _position.Z;

        if (args.TryGetProperty("x", out JsonElement xEl) && xEl.TryGetSingle(out float valX)) x = valX;
        if (args.TryGetProperty("y", out JsonElement yEl) && yEl.TryGetSingle(out float valY)) y = valY;
        if (args.TryGetProperty("z", out JsonElement zEl) && zEl.TryGetSingle(out float valZ)) z = valZ;

        var (ready, offline, targetStatus) = await EnsureClientReadyAsync(args, "set_position", new { position = new { x, y, z } }, ct).ConfigureAwait(false);
        if (offline != null) return offline;

        _position = new Vector3(x, y, z);
        _cameraMode = "free";
        _trackedEntityId = null;

        // Dispatch console cvars if live client IPC is active
        if (ShouldDispatchIpc)
        {
            await _gameIpcBridge.ExecuteCommandAsync($"Set cam_position \"{x:F2} {y:F2} {z:F2}\"", cancellationToken: ct);
        }

        _logger.LogInformation("[CameraControlHandler] Updated camera position to ({X:F2}, {Y:F2}, {Z:F2})", x, y, z);

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "set_position",
            executionMode = _gameIpcBridge.IsConnected ? ExecutionTruth.ModeLiveEngine : ExecutionTruth.ModeDryRunPlan,
            targetStatus,
            position = new { x = _position.X, y = _position.Y, z = _position.Z },
            fov = _fov,
            pitch = _pitch,
            yaw = _yaw,
            mode = _cameraMode
        });
    }

    private bool ShouldDispatchIpc =>
        _gameIpcBridge.IsConnected &&
        (!GameServerManageHandler.IsTestExecution ||
         (_gameIpcBridge is not GameIpcBridge and not NexusMailboxIpcBridge and not NativeClientIpcBridge and not CompositeGameIpcBridge));

    private ToolCallResult HandleGetPosition()
    {
        return ToolCallResult.Json(new
        {
            status = "success",
            action = "get_position",
            position = new { x = _position.X, y = _position.Y, z = _position.Z },
            fov = _fov,
            pitch = _pitch,
            yaw = _yaw,
            distance = _distance,
            mode = _cameraMode,
            trackedEntityId = _trackedEntityId,
            readout = $"Pos: ({_position.X:F1}, {_position.Y:F1}, {_position.Z:F1}) | FOV: {_fov:F1}° | Pitch: {_pitch:F1}° | Yaw: {_yaw:F1}°"
        });
    }

    private async Task<ToolCallResult> HandleSetFovAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("fov", out JsonElement fovEl) || !fovEl.TryGetSingle(out float fov))
        {
            return ToolCallResult.Error("Missing required parameter 'fov' (float between 30.0 and 130.0).");
        }

        _fov = Math.Clamp(fov, 30.0f, 130.0f);

        var (ready, offline, targetStatus) = await EnsureClientReadyAsync(args, "set_fov", new { fov = _fov }, ct).ConfigureAwait(false);
        if (offline != null) return offline;

        if (ShouldDispatchIpc)
        {
            await _gameIpcBridge.ExecuteCommandAsync($"Set cam_fov {_fov:F1}", cancellationToken: ct);
        }

        _logger.LogInformation("[CameraControlHandler] Set FOV to {Fov:F1}°", _fov);

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "set_fov",
            executionMode = _gameIpcBridge.IsConnected ? ExecutionTruth.ModeLiveEngine : ExecutionTruth.ModeDryRunPlan,
            targetStatus,
            fov = _fov
        });
    }

    private async Task<ToolCallResult> HandleSetPitchAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("pitch", out JsonElement pitchEl) || !pitchEl.TryGetSingle(out float pitch))
        {
            return ToolCallResult.Error("Missing required parameter 'pitch' (float between -89.0 and 89.0).");
        }

        _pitch = Math.Clamp(pitch, -89.0f, 89.0f);

        var (ready, offline, targetStatus) = await EnsureClientReadyAsync(args, "set_pitch", new { pitch = _pitch }, ct).ConfigureAwait(false);
        if (offline != null) return offline;

        if (ShouldDispatchIpc)
        {
            await _gameIpcBridge.ExecuteCommandAsync($"Set cam_angle {_pitch:F1}", cancellationToken: ct);
        }

        _logger.LogInformation("[CameraControlHandler] Set Pitch to {Pitch:F1}°", _pitch);

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "set_pitch",
            executionMode = _gameIpcBridge.IsConnected ? ExecutionTruth.ModeLiveEngine : ExecutionTruth.ModeDryRunPlan,
            targetStatus,
            pitch = _pitch
        });
    }

    private async Task<ToolCallResult> HandleFollowEntityAsync(JsonElement args, CancellationToken ct)
    {
        int entityId = 0;
        if ((args.TryGetProperty("entity_id", out JsonElement idEl) ||
             args.TryGetProperty("entityId", out idEl) ||
             args.TryGetProperty("slot", out idEl)) &&
            idEl.TryGetInt32(out int parsedId))
        {
            entityId = parsedId;
        }
        else
        {
            return ToolCallResult.Error("Missing required parameter 'entity_id' or 'slot' (integer entity index to follow).");
        }

        var (ready, offline, targetStatus) = await EnsureClientReadyAsync(args, "follow_entity", new { entityId }, ct).ConfigureAwait(false);
        if (offline != null) return offline;

        _trackedEntityId = entityId;
        _cameraMode = "follow";

        if (ShouldDispatchIpc)
        {
            await _gameIpcBridge.ExecuteCommandAsync($"CameraFollow {entityId}", cancellationToken: ct);
        }

        _logger.LogInformation("[CameraControlHandler] Camera now following entity slot {EntityId}", entityId);

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "follow_entity",
            executionMode = _gameIpcBridge.IsConnected ? ExecutionTruth.ModeLiveEngine : ExecutionTruth.ModeDryRunPlan,
            targetStatus,
            trackedEntityId = entityId,
            mode = "follow"
        });
    }

    private async Task<ToolCallResult> HandleInterpolatePathAsync(JsonElement args, CancellationToken ct)
    {
        int durationMs = 2000;
        if ((args.TryGetProperty("duration_ms", out JsonElement dEl) ||
             args.TryGetProperty("durationMs", out dEl)) &&
            dEl.TryGetInt32(out int dVal))
        {
            durationMs = Math.Max(100, dVal);
        }

        var (ready, offline, targetStatus) = await EnsureClientReadyAsync(args, "interpolate_path", new { durationMs }, ct).ConfigureAwait(false);
        if (offline != null) return offline;

        List<Vector3> waypoints = [];
        if (args.TryGetProperty("waypoints", out JsonElement wpEl) && wpEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in wpEl.EnumerateArray())
            {
                float x = 0f, y = 0f, z = 0f;
                if (item.TryGetProperty("x", out JsonElement xEl) && xEl.TryGetSingle(out float vx)) x = vx;
                if (item.TryGetProperty("y", out JsonElement yEl) && yEl.TryGetSingle(out float vy)) y = vy;
                if (item.TryGetProperty("z", out JsonElement zEl) && zEl.TryGetSingle(out float vz)) z = vz;
                waypoints.Add(new Vector3(x, y, z));
            }
        }

        if (waypoints.Count < 2)
        {
            waypoints =
            [
                _position,
                new Vector3(_position.X + 200f, _position.Y + 200f, _position.Z + 50f)
            ];
        }

        // Generate Catmull-Rom interpolated sample points
        List<object> trajectory = [];
        int stepCount = Math.Clamp(durationMs / 100, 5, 50);

        for (int i = 0; i <= stepCount; i++)
        {
            float t = (float)i / stepCount;
            float segT = t * (waypoints.Count - 1);
            int idx = Math.Min((int)segT, waypoints.Count - 2);
            float localT = segT - idx;

            Vector3 p0 = idx > 0 ? waypoints[idx - 1] : waypoints[idx];
            Vector3 p1 = waypoints[idx];
            Vector3 p2 = waypoints[idx + 1];
            Vector3 p3 = idx + 2 < waypoints.Count ? waypoints[idx + 2] : p2;

            Vector3 interpolated = CatmullRom(p0, p1, p2, p3, localT);
            trajectory.Add(new
            {
                step = i,
                timeMs = (int)(t * durationMs),
                x = Math.Round(interpolated.X, 2),
                y = Math.Round(interpolated.Y, 2),
                z = Math.Round(interpolated.Z, 2)
            });
        }

        _position = waypoints[^1];

        if (ShouldDispatchIpc)
        {
            await _gameIpcBridge.ExecuteCommandAsync($"Set cam_position \"{_position.X:F2} {_position.Y:F2} {_position.Z:F2}\"", cancellationToken: ct);
        }

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "interpolate_path",
            executionMode = _gameIpcBridge.IsConnected ? ExecutionTruth.ModeLiveEngine : ExecutionTruth.ModeDryRunPlan,
            targetStatus,
            durationMs,
            waypointsCount = waypoints.Count,
            samplePoints = trajectory.Count,
            finalPosition = new { x = _position.X, y = _position.Y, z = _position.Z },
            trajectory
        });
    }

    private async Task<ToolCallResult> HandleCaptureViewAsync(JsonElement args, CancellationToken ct)
    {
        var (ready, offline, targetStatus) = await EnsureClientReadyAsync(args, "capture_view", null, ct).ConfigureAwait(false);
        if (offline != null) return offline;

        string target = "hon_x64";
        if (args.TryGetProperty("target", out JsonElement tEl) && !string.IsNullOrWhiteSpace(tEl.GetString()))
        {
            target = tEl.GetString()!.Trim();
        }

        WindowCaptureResult captureResult = await _windowCapturer.CaptureAsync(
            target: target,
            captureMethod: "auto",
            saveArtifact: true,
            customOutputPath: null,
            cancellationToken: ct
        );

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "capture_view",
            executionMode = _gameIpcBridge.IsConnected ? ExecutionTruth.ModeLiveEngine : ExecutionTruth.ModeDryRunPlan,
            targetStatus,
            camera = new
            {
                position = new { x = _position.X, y = _position.Y, z = _position.Z },
                fov = _fov,
                pitch = _pitch,
                yaw = _yaw
            },
            capture = new
            {
                success = captureResult.Success,
                artifactPath = captureResult.ArtifactPath,
                width = captureResult.Width,
                height = captureResult.Height,
                windowTitle = captureResult.WindowTitle,
                errorMessage = captureResult.ErrorMessage
            }
        });
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;

        return 0.5f * (
            2f * p1 +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }
}

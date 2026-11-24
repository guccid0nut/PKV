// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Common.Telemetry;
using NEXUS.Contracts.Telemetry;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_game_telemetry'.
/// Dual-plane telemetry querying:
/// 1. Shared Memory Data Plane: Sub-microsecond zero-copy reads from MemoryMappedTelemetryBuffer ring buffer.
/// 2. Control Plane: Named Pipe IPC fallback for engine state (FPS, working set, match ID, map, players, uptime).
/// </summary>
public sealed class GameTelemetryHandler
{
    private readonly IGameIpcBridge _ipcBridge;
    private readonly TimeProvider _timeProvider;
    private readonly GameClientManageHandler? _clientManageHandler;

    public GameTelemetryHandler(
        IGameIpcBridge? ipcBridge = null,
        TimeProvider? timeProvider = null,
        GameClientManageHandler? clientManageHandler = null)
    {
        _ipcBridge = ipcBridge ?? new GameIpcBridge();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clientManageHandler = clientManageHandler;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string channel = "auto";
        if (arguments.TryGetProperty("channel", out JsonElement chEl) && !string.IsNullOrWhiteSpace(chEl.GetString()))
        {
            channel = chEl.GetString()!.Trim().ToLowerInvariant();
        }

        string action = "latest";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        int limit = 64;
        if ((arguments.TryGetProperty("limit", out JsonElement limEl) ||
             arguments.TryGetProperty("count", out limEl)) &&
            limEl.TryGetInt32(out int lVal) && lVal > 0)
        {
            limit = Math.Clamp(lVal, 1, 1024);
        }

        if (action is "push" or "update" or "feed")
        {
            uint drawCalls = 180;
            if (arguments.TryGetProperty("drawCalls", out JsonElement dcEl) && dcEl.TryGetUInt32(out uint dcVal))
                drawCalls = dcVal;

            uint activeEntities = 110;
            if (arguments.TryGetProperty("activeEntities", out JsonElement aeEl) && aeEl.TryGetUInt32(out uint aeVal))
                activeEntities = aeVal;

            uint particlesAlive = 45;
            if (arguments.TryGetProperty("particlesAlive", out JsonElement paEl) && paEl.TryGetUInt32(out uint paVal))
                particlesAlive = paVal;

            uint pingMicroseconds = 12000;
            if (arguments.TryGetProperty("pingMicroseconds", out JsonElement pingEl) && pingEl.TryGetUInt32(out uint pingVal))
                pingMicroseconds = pingVal;

            uint serverTick = 1000;
            if (arguments.TryGetProperty("serverTick", out JsonElement tickEl) && tickEl.TryGetUInt32(out uint tickVal))
                serverTick = tickVal;

            float inputLatencyMs = 0f;
            if (arguments.TryGetProperty("inputLatencyMs", out JsonElement latEl) && latEl.TryGetSingle(out float latVal))
                inputLatencyMs = latVal;

            bool updated = false;
            try
            {
                updated = await _ipcBridge.PushTelemetryUpdateAsync(drawCalls, activeEntities, particlesAlive, pingMicroseconds, serverTick, inputLatencyMs, 2000, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                updated = false;
            }

            return ToolCallResult.Success(new
            {
                success = updated,
                action = "telemetry_feed",
                drawCalls,
                activeEntities,
                particlesAlive,
                pingMicroseconds,
                serverTick,
                inputLatencyMs,
                telemetryUpdated = updated,
                message = updated ? "Dynamic telemetry updated successfully." : "Failed to update dynamic telemetry (no connected native client)."
            });
        }

        // 1. Shared Memory Data Plane: Query zero-copy MemoryMappedTelemetryBuffer ring buffer
        if (channel is "shm" or "mmf" or "shared_memory" or "auto")
        {
            string? explicitMapName = null;
            if ((arguments.TryGetProperty("mapName", out JsonElement mapEl) ||
                 arguments.TryGetProperty("map", out mapEl)) &&
                !string.IsNullOrWhiteSpace(mapEl.GetString()))
            {
                explicitMapName = mapEl.GetString()!.Trim();
            }

            int? targetPid = null;
            if ((arguments.TryGetProperty("pid", out JsonElement pidEl) ||
                 arguments.TryGetProperty("processId", out pidEl)) &&
                pidEl.TryGetInt32(out int pVal) && pVal > 0)
            {
                targetPid = pVal;
            }
            else
            {
                System.Diagnostics.Process? proc = _clientManageHandler?.GetRunningClientProcess() ?? new ClientProcessSupervisor().FindRunningClientProcess();
                if (proc != null)
                {
                    targetPid = proc.Id;
                }
            }

            (bool shmSuccess, ToolCallResult? shmResult) = TryQuerySharedMemory(explicitMapName, targetPid, action, limit);
            if (shmSuccess)
            {
                return shmResult!;
            }

            // If MMF was explicitly requested (not "auto"), report error rather than falling back silently
            if (channel is "shm" or "mmf" or "shared_memory")
            {
                return shmResult!;
            }
        }

        // 2. Control Plane Fallback: Query via Named Pipe IPC
        return await QueryNamedPipeIpcAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    private (bool Success, ToolCallResult? Result) TryQuerySharedMemory(
        string? explicitMapName,
        int? processId,
        string action,
        int limit)
    {
        List<string> candidates = [];
        if (!string.IsNullOrWhiteSpace(explicitMapName))
        {
            candidates.Add(explicitMapName);
        }
        else if (processId.HasValue)
        {
            candidates.Add($"Local\\KongorClientTelemetry_{processId.Value}");
            candidates.Add($"KongorClientTelemetry_{processId.Value}");
        }
        else
        {
            candidates.Add("Local\\KongorTelemetryBuffer");
            candidates.Add("KongorTelemetryBuffer");
            try
            {
                System.Diagnostics.Process[] procs = System.Diagnostics.Process.GetProcessesByName("hon_x64");
                foreach (System.Diagnostics.Process p in procs)
                {
                    if (!p.HasExited)
                    {
                        candidates.Add($"Local\\KongorClientTelemetry_{p.Id}");
                        candidates.Add($"KongorClientTelemetry_{p.Id}");
                    }
                }
            }
            catch { }
        }
        candidates.Add("Local\\KongorTelemetry_Global");
        candidates.Add("KongorTelemetry_Global");

        MemoryMappedTelemetryBuffer? buffer = null;
        string? activeMapName = null;
        string? lastError = null;

        foreach (string candidate in candidates)
        {
            if (MemoryMappedTelemetryBuffer.TryOpenConsumer(candidate, out buffer, out string? err))
            {
                activeMapName = candidate;
                break;
            }
            lastError = err;
        }

        if (buffer == null || activeMapName == null)
        {
            return (false, ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                channel = "shm",
                error = $"Failed to open shared memory telemetry buffer: {lastError ?? "No active telemetry map found."}",
                candidatesTried = candidates,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true));
        }

        using (buffer)
        {
            if (action is "hitches" or "sentinel")
            {
                int scanLimit = Math.Clamp(limit * 4, 256, 1024);
                Span<FrameTelemetryRecord> recordSpan = stackalloc FrameTelemetryRecord[scanLimit];
                int readCount = buffer.ReadRecent(recordSpan);

                var hitches = new List<object>();
                for (int i = 0; i < readCount; i++)
                {
                    var r = recordSpan[i];
                    if ((r.ReservedFlags & FrameTelemetryRecord.FlagHitch) != 0 || r.CpuFrameTimeMs > 10.0f || r.GpuFrameTimeMs > 10.0f)
                    {
                        string reason = "FRAME_PACING_SPIKE";
                        if ((r.ReservedFlags & FrameTelemetryRecord.FlagCpuStall) != 0 || r.CpuFrameTimeMs > 8.0f)
                            reason = "CPU_ENGINE_TICK_STALL";
                        else if ((r.ReservedFlags & FrameTelemetryRecord.FlagGpuStall) != 0 || r.GpuFrameTimeMs > 8.0f)
                            reason = "GPU_RENDER_PRESENT_STALL";
                        else if ((r.ReservedFlags & FrameTelemetryRecord.FlagDwmWait) != 0 || r.InputLatencyMs > 8.0f)
                            reason = "DWM_COMPOSITOR_WAIT";
                        else if ((r.ReservedFlags & FrameTelemetryRecord.FlagPreemption) != 0)
                            reason = "QUANTUM_PREEMPTION_OR_THROTTLE";

                        hitches.Add(new
                        {
                            frameIndex = r.FrameIndex,
                            timestampQpc = r.TimestampQpc,
                            cpuFrameTimeMs = Math.Round(r.CpuFrameTimeMs, 3),
                            gpuFrameTimeMs = Math.Round(r.GpuFrameTimeMs, 3),
                            inputLatencyMs = Math.Round(r.InputLatencyMs, 3),
                            drawCallCount = r.DrawCallCount,
                            memoryWorkingSetMb = r.MemoryWorkingSetMb,
                            serverTick = r.ServerTick,
                            reason = reason,
                            flags = r.ReservedFlags
                        });
                    }
                }

                return (true, ToolCallResult.Json(new
                {
                    status = "success",
                    success = true,
                    channel = "shm",
                    action = "hitches",
                    mapName = activeMapName,
                    scannedFrames = readCount,
                    hitchCount = hitches.Count,
                    hitches = hitches,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                }));
            }

            if (action is "poll_ring" or "stream")
            {
                Span<FrameTelemetryRecord> recordSpan = stackalloc FrameTelemetryRecord[limit];
                int readCount = buffer.ReadAvailable(recordSpan);

                var frames = new List<object>(readCount);
                for (int i = 0; i < readCount; i++)
                {
                    var r = recordSpan[i];
                    double fps = r.CpuFrameTimeMs > 0.001f ? Math.Round(1000.0 / r.CpuFrameTimeMs, 2) : 0.0;
                    frames.Add(new
                    {
                        frameIndex = r.FrameIndex,
                        timestampQpc = r.TimestampQpc,
                        cpuFrameTimeMs = Math.Round(r.CpuFrameTimeMs, 3),
                        gpuFrameTimeMs = Math.Round(r.GpuFrameTimeMs, 3),
                        inputLatencyMs = Math.Round(r.InputLatencyMs, 3),
                        currentFps = fps,
                        drawCallCount = r.DrawCallCount,
                        activeEntities = r.ActiveEntities,
                        particlesAlive = r.ParticlesAlive,
                        pingMicroseconds = r.PingMicroseconds,
                        pingMs = r.PingMicroseconds / 1000u,
                        packetLossPercent = Math.Round(r.PacketLossPercent, 2),
                        memoryWorkingSetMb = r.MemoryWorkingSetMb,
                        audioVoicesActive = r.AudioVoicesActive,
                        serverTick = r.ServerTick
                    });
                }

                return (true, ToolCallResult.Json(new
                {
                    status = "success",
                    success = true,
                    channel = "shm",
                    action = "poll_ring",
                    mapName = activeMapName,
                    framesRead = readCount,
                    records = frames,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                }));
            }
            else // "latest" or default
            {
                bool hasRecord = buffer.ReadLatest(out FrameTelemetryRecord r);
                if (!hasRecord)
                {
                    return (false, ToolCallResult.Json(new
                    {
                        status = "error",
                        success = false,
                        channel = "shm",
                        mapName = activeMapName,
                        error = "Telemetry ring buffer is active but no frames have been produced yet.",
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    }, isError: true));
                }

                double fps = r.CpuFrameTimeMs > 0.001f ? Math.Round(1000.0 / r.CpuFrameTimeMs, 2) : 0.0;
                int gamePhase = (int)buffer.GamePhase;
                string gamePhaseName = gamePhase switch
                {
                    0 => "In Lobby/Menu",
                    1 => "Loading Map",
                    2 => "Hero Selection",
                    3 => "Active Match",
                    4 => "Post Match",
                    _ => "In Lobby/Menu"
                };
                uint activeMatchId = buffer.ActiveMatchId;
                int connectedPlayers = (int)buffer.ConnectedPlayers;
                uint luaMemoryKb = buffer.LuaMemoryKb;
                long luaMemoryBytes = (long)luaMemoryKb * 1024L;
                string currentMap = buffer.CurrentMap;
                string playerName = buffer.PlayerName;

                return (true, ToolCallResult.Json(new
                {
                    status = "success",
                    success = true,
                    connected = true,
                    channel = "shm",
                    mapName = activeMapName,
                    gamePhase,
                    gamePhaseName,
                    activeMatchId,
                    connectedPlayers,
                    luaMemoryKb,
                    luaMemoryBytes,
                    currentMap,
                    playerName,
                    telemetry = new
                    {
                        frameIndex = r.FrameIndex,
                        timestampQpc = r.TimestampQpc,
                        cpuFrameTimeMs = Math.Round(r.CpuFrameTimeMs, 3),
                        gpuFrameTimeMs = Math.Round(r.GpuFrameTimeMs, 3),
                        inputLatencyMs = Math.Round(r.InputLatencyMs, 3),
                        currentFps = fps,
                        drawCallCount = r.DrawCallCount,
                        activeEntities = r.ActiveEntities,
                        particlesAlive = r.ParticlesAlive,
                        pingMicroseconds = r.PingMicroseconds,
                        pingMs = r.PingMicroseconds / 1000u,
                        packetLossPercent = Math.Round(r.PacketLossPercent, 2),
                        memoryWorkingSetMb = (double)r.MemoryWorkingSetMb,
                        memoryWorkingSetBytes = (long)r.MemoryWorkingSetMb * 1024L * 1024L,
                        luaMemoryKb,
                        luaMemoryBytes,
                        audioVoicesActive = r.AudioVoicesActive,
                        serverTick = r.ServerTick,
                        gamePhase,
                        gamePhaseName,
                        activeMatchId,
                        connectedPlayers,
                        currentMap,
                        playerName
                    },
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                }));
            }
        }
    }

    private async Task<ToolCallResult> QueryNamedPipeIpcAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        int timeoutMs = 2000;
        if (arguments.TryGetProperty("timeoutMs", out JsonElement toElem) && toElem.TryGetInt32(out int parsedTimeout) && parsedTimeout > 0)
        {
            timeoutMs = parsedTimeout;
        }

        try
        {
            EngineStateDto? state = await _ipcBridge.GetEngineStateAsync(timeoutMs, cancellationToken).ConfigureAwait(false);

            if (state == null)
            {
                var nullResponse = new
                {
                    success = false,
                    connected = _ipcBridge.IsConnected,
                    ipcConnected = _ipcBridge.IsConnected,
                    gamePhase = (int?)null,
                    gamePhaseName = "Offline",
                    isSpectator = false,
                    activeSpectatorCount = 0,
                    maxSpectators = 0,
                    spectatorTarget = (string?)null,
                    telemetry = new
                    {
                        currentFps = 0.0,
                        memoryWorkingSetBytes = 0L,
                        memoryWorkingSetMb = 0.0,
                        activeMatchId = 0,
                        currentMap = string.Empty,
                        playerName = string.Empty,
                        connectedPlayers = 0,
                        uptimeSeconds = 0L,
                        gamePhase = (int?)null,
                        gamePhaseName = "Offline",
                        isSpectator = false,
                        activeSpectatorCount = 0,
                        maxSpectators = 0,
                        spectatorTarget = (string?)null
                    },
                    nextAction = "Game client is not responding to IPC. Verify hon_x64 is running with '-pipe willowmaker-ipc'.",
                    error = "Received null telemetry state from engine.",
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                };
                return ToolCallResult.Json(nullResponse);
            }

            // Map game phase: 0 = In Lobby/Menu, 1 = Picking Phase, 2 = Active Match, 3 = Post-Match
            int phase;
            string phaseName;
            if (state.GamePhase == 1 || state.GamePhase == 2 || state.GamePhase == 3)
            {
                phase = state.GamePhase;
                phaseName = phase == 1 ? "Picking Phase" : (phase == 2 ? "Active Match" : "Post Match");
            }
            else if (!string.IsNullOrEmpty(state.CurrentMap) && !state.CurrentMap.Equals("idle", StringComparison.OrdinalIgnoreCase))
            {
                phase = 2; // Active match
                phaseName = "Active Match";
            }
            else
            {
                phase = 0; // In Lobby/Menu
                phaseName = "In Lobby/Menu";
            }

            // Fallback enrichment for spectator fields if running through live IPC
            bool isSpectator = state.IsSpectator;
            int activeSpectatorCount = state.ActiveSpectatorCount;
            int maxSpectators = state.MaxSpectators;
            string? spectatorTarget = state.SpectatorTarget;

            if (_ipcBridge.IsConnected && !isSpectator && maxSpectators == 0)
            {
                try
                {
                    // Fast non-blocking query for spectator CVars
                    string cvarResp = await _ipcBridge.ExecuteCommandAsync("GetVal cl_spectator; GetVal cg_spectating; GetVal cg_numSpectators; GetVal svr_maxSpectators", Math.Min(timeoutMs, 500), cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(cvarResp))
                    {
                        if (cvarResp.Contains("cl_spectator = true", StringComparison.OrdinalIgnoreCase) ||
                            cvarResp.Contains("cg_spectating = true", StringComparison.OrdinalIgnoreCase) ||
                            cvarResp.Contains("cl_spectator = 1", StringComparison.OrdinalIgnoreCase))
                        {
                            isSpectator = true;
                        }

                        var matchNum = System.Text.RegularExpressions.Regex.Match(cvarResp, @"cg_numSpectators\s*=\s*(\d+)");
                        if (matchNum.Success && int.TryParse(matchNum.Groups[1].Value, out int numSpec))
                        {
                            activeSpectatorCount = numSpec;
                        }

                        var matchMax = System.Text.RegularExpressions.Regex.Match(cvarResp, @"svr_maxSpectators\s*=\s*(\d+)");
                        if (matchMax.Success && int.TryParse(matchMax.Groups[1].Value, out int maxSpec))
                        {
                            maxSpectators = maxSpec;
                        }
                    }
                }
                catch
                {
                    // Non-fatal telemetry enrichment fallback
                }
            }

            var response = new
            {
                success = true,
                connected = true,
                ipcConnected = true,
                gamePhase = phase,
                gamePhaseName = phaseName,
                isSpectator,
                activeSpectatorCount,
                maxSpectators,
                spectatorTarget,
                telemetry = new
                {
                    currentFps = state.CurrentFps,
                    memoryWorkingSetBytes = state.MemoryWorkingSetBytes,
                    memoryWorkingSetMb = state.MemoryWorkingSetMb,
                    activeMatchId = state.ActiveMatchId,
                    currentMap = state.CurrentMap,
                    playerName = state.PlayerName,
                    connectedPlayers = state.ConnectedPlayers,
                    uptimeSeconds = state.UptimeSeconds,
                    gamePhase = phase,
                    gamePhaseName = phaseName,
                    isSpectator,
                    activeSpectatorCount,
                    maxSpectators,
                    spectatorTarget
                },
                nextAction = phase == 2 ? (isSpectator ? "Client is actively spectating match." : "Client is active in-game.") : "Client is in menu/lobby.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };

            return ToolCallResult.Json(response);
        }
        catch (TimeoutException tex)
        {
            var timeoutResponse = new
            {
                success = false,
                connected = _ipcBridge.IsConnected,
                telemetry = new
                {
                    currentFps = 0.0,
                    memoryWorkingSetBytes = 0L,
                    memoryWorkingSetMb = 0.0,
                    activeMatchId = 0,
                    currentMap = string.Empty,
                    playerName = string.Empty,
                    connectedPlayers = 0,
                    uptimeSeconds = 0L
                },
                error = $"Telemetry query timed out after {timeoutMs}ms: {tex.Message}",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
            return ToolCallResult.Json(timeoutResponse);
        }
        catch (Exception ex)
        {
            var errorResponse = new
            {
                success = false,
                connected = _ipcBridge.IsConnected,
                telemetry = new
                {
                    currentFps = 0.0,
                    memoryWorkingSetBytes = 0L,
                    memoryWorkingSetMb = 0.0,
                    activeMatchId = 0,
                    currentMap = string.Empty,
                    playerName = string.Empty,
                    connectedPlayers = 0,
                    uptimeSeconds = 0L
                },
                error = $"Telemetry query error: {ex.Message}",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
            return ToolCallResult.Json(errorResponse);
        }
    }
}

// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: TelemetryRingConsumerService.cs
// System: WILLOWMAKER Core Services - High-Speed Shared Memory Telemetry Consumer
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Diagnostics;
using ASPIRE.Common.Telemetry;
using NEXUS.Contracts.Telemetry;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Immutable diagnostic snapshot representing calculated real-time client performance metrics.
/// </summary>
public sealed record ClientTelemetrySnapshot(
    float CurrentFps,
    float AverageFrametimeMs,
    float Low1PercentFps,
    float GpuFrametimeMs,
    uint DrawCalls,
    uint ActiveEntities,
    uint PingMs,
    float PacketLossPercent,
    ulong TotalFramesCaptured,
    bool IsConnected)
{
    public static readonly ClientTelemetrySnapshot Disconnected = new(
        CurrentFps: 0f,
        AverageFrametimeMs: 0f,
        Low1PercentFps: 0f,
        GpuFrametimeMs: 0f,
        DrawCalls: 0,
        ActiveEntities: 0,
        PingMs: 0,
        PacketLossPercent: 0f,
        TotalFramesCaptured: 0,
        IsConnected: false);
}

/// <summary>
///     Immutable V2 shared-memory header state snapshot emitted when game phase, match ID, map, or player changes.
/// </summary>
public readonly record struct EngineHeaderStateSnapshot(
    uint GamePhase,
    uint ActiveMatchId,
    uint ConnectedPlayers,
    uint LuaMemoryKb,
    string CurrentMap,
    string PlayerName);

/// <summary>
///     Background service consuming real-time frame telemetry from the game engine
///     via the memory-mapped ring buffer with zero impact on the game loop.
/// </summary>
public sealed class TelemetryRingConsumerService : IDisposable
{
    private const int SlidingWindowCapacity = 256;
    private readonly float[] _frametimeWindow = new float[SlidingWindowCapacity];
    private readonly float[] _sortBuffer = new float[SlidingWindowCapacity];
    private int _windowHead;
    private int _windowCount;

    private MemoryMappedTelemetryBuffer? _buffer;
    private CancellationTokenSource? _cts;
    private Task? _consumerTask;
    private bool _disposed;
    private ulong _totalFramesRead;
    private ulong _lastPttFlags;
    private uint _lastGamePhase = uint.MaxValue;
    private uint _lastActiveMatchId = uint.MaxValue;
    private string _lastCurrentMap = string.Empty;
    private string _lastPlayerName = string.Empty;

    public ClientTelemetrySnapshot CurrentSnapshot { get; private set; } = ClientTelemetrySnapshot.Disconnected;

    public bool IsConnected => _buffer != null && CurrentSnapshot.IsConnected;

    public ulong CurrentPttFlags => _buffer?.ReservedFlags ?? 0;
    public bool IsTeamPttActive => (CurrentPttFlags & TelemetryRingHeader.VoicePttTeamFlag) != 0;
    public bool IsLanePttActive => (CurrentPttFlags & TelemetryRingHeader.VoicePttLaneFlag) != 0;
    public bool IsPartyPttActive => (CurrentPttFlags & TelemetryRingHeader.VoicePttPartyFlag) != 0;
    public bool IsMentorPttActive => (CurrentPttFlags & TelemetryRingHeader.VoicePttMentorFlag) != 0;

    public event Action<ClientTelemetrySnapshot>? OnSnapshotUpdated;
    public event Action<ulong>? OnPttFlagsChanged;
    public event Action<EngineHeaderStateSnapshot>? OnEngineStateChanged;

    /// <summary>
    ///     Builds the standard shared memory mapping name for a game client process.
    /// </summary>
    public static string GetClientMapName(int processId) => $"Local\\KongorClientTelemetry_{processId}";

    /// <summary>
    ///     Builds the standard shared memory mapping name for a dedicated server process.
    /// </summary>
    public static string GetServerMapName(int port) => $"Global\\KongorServerTelemetry_{port}";

    /// <summary>
    ///     Starts monitoring the specified shared memory mapping in the background.
    /// </summary>
    public void StartMonitoring(string mapName, TimeSpan? pollInterval = null)
    {
        StopMonitoring();

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        TimeSpan interval = pollInterval ?? TimeSpan.FromMilliseconds(33); // ~30Hz polling

        _consumerTask = Task.Run(async () =>
        {
            using PeriodicTimer timer = new(interval);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Attempt to connect if not yet attached
                    if (_buffer == null)
                    {
                        if (MemoryMappedTelemetryBuffer.TryOpenConsumer(mapName, out MemoryMappedTelemetryBuffer? buffer, out _))
                        {
                            _buffer = buffer;
                        }
                    }

                    if (_buffer != null)
                    {
                        PollTelemetry();
                    }

                    await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // If buffer became invalid (e.g. process terminated), reset connection
                    ResetConnection();
                }
            }

            ResetConnection();
        }, token);
    }

    /// <summary>
    ///     Starts monitoring a game client process by its process ID.
    /// </summary>
    public void StartMonitoringClient(int processId, TimeSpan? pollInterval = null)
    {
        StartMonitoring(GetClientMapName(processId), pollInterval);
    }

    /// <summary>
    ///     Stops monitoring and releases all shared memory resources.
    /// </summary>
    public void StopMonitoring()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            try
            {
#pragma warning disable RS0030 // Justification: Synchronous monitoring teardown bounded wait
                _consumerTask?.Wait(TimeSpan.FromMilliseconds(200));
#pragma warning restore RS0030
            }
            catch
            {
                // Ignored
            }

            _cts.Dispose();
            _cts = null;
            _consumerTask = null;
        }

        ResetConnection();
    }

    private void PollTelemetry()
    {
        if (_buffer == null) return;

        ulong pttFlags = _buffer.ReservedFlags;
        if (pttFlags != _lastPttFlags)
        {
            _lastPttFlags = pttFlags;
            OnPttFlagsChanged?.Invoke(pttFlags);
        }

        if (_buffer.SupportsSeqlock)
        {
            uint gamePhase = _buffer.GamePhase;
            uint activeMatchId = _buffer.ActiveMatchId;
            string currentMap = _buffer.CurrentMap;
            string playerName = _buffer.PlayerName;
            if (gamePhase != _lastGamePhase ||
                activeMatchId != _lastActiveMatchId ||
                !string.Equals(currentMap, _lastCurrentMap, StringComparison.Ordinal) ||
                !string.Equals(playerName, _lastPlayerName, StringComparison.Ordinal))
            {
                _lastGamePhase = gamePhase;
                _lastActiveMatchId = activeMatchId;
                _lastCurrentMap = currentMap;
                _lastPlayerName = playerName;
                EngineHeaderStateSnapshot headerSnapshot = new(
                    GamePhase: gamePhase,
                    ActiveMatchId: activeMatchId,
                    ConnectedPlayers: _buffer.ConnectedPlayers,
                    LuaMemoryKb: _buffer.LuaMemoryKb,
                    CurrentMap: currentMap,
                    PlayerName: playerName);
                OnEngineStateChanged?.Invoke(headerSnapshot);
            }
        }

        Span<FrameTelemetryRecord> batch = stackalloc FrameTelemetryRecord[128];
        int readCount = _buffer.ReadAvailable(batch);

        if (readCount > 0)
        {
            _totalFramesRead += (ulong)readCount;

            // Ingest new frametimes into circular window
            for (int i = 0; i < readCount; i++)
            {
                float ft = batch[i].CpuFrameTimeMs;
                if (ft > 0f)
                {
                    _frametimeWindow[_windowHead] = ft;
                    _windowHead = (_windowHead + 1) % SlidingWindowCapacity;
                    if (_windowCount < SlidingWindowCapacity)
                    {
                        _windowCount++;
                    }
                }
            }

            // Inspect the latest frame for instantaneous metrics
            FrameTelemetryRecord latest = batch[readCount - 1];

            // Compute statistics across sliding window
            float avgFrametime = 0f;
            float low1PercentFps = 0f;
            float currentFps = 0f;

            if (_windowCount > 0)
            {
                float sum = 0f;
                for (int i = 0; i < _windowCount; i++)
                {
                    float val = _frametimeWindow[i];
                    sum += val;
                    _sortBuffer[i] = val;
                }

                avgFrametime = sum / _windowCount;
                if (avgFrametime > 0.001f)
                {
                    currentFps = 1000f / avgFrametime;
                }

                // Calculate 1% Lows (99th percentile frametime)
                Array.Sort(_sortBuffer, 0, _windowCount);
                int p99Index = Math.Clamp((int)(_windowCount * 0.99f), 0, _windowCount - 1);
                float p99Frametime = _sortBuffer[p99Index];
                if (p99Frametime > 0.001f)
                {
                    low1PercentFps = 1000f / p99Frametime;
                }
            }

            uint pingMs = latest.PingMicroseconds > 0 ? (latest.PingMicroseconds / 1000u) : 0u;

            ClientTelemetrySnapshot snapshot = new(
                CurrentFps: MathF.Round(currentFps, 1),
                AverageFrametimeMs: MathF.Round(avgFrametime, 2),
                Low1PercentFps: MathF.Round(low1PercentFps, 1),
                GpuFrametimeMs: MathF.Round(latest.GpuFrameTimeMs, 2),
                DrawCalls: latest.DrawCallCount,
                ActiveEntities: latest.ActiveEntities,
                PingMs: pingMs,
                PacketLossPercent: latest.PacketLossPercent,
                TotalFramesCaptured: _totalFramesRead,
                IsConnected: true);

            CurrentSnapshot = snapshot;
            OnSnapshotUpdated?.Invoke(snapshot);
        }
    }

    private void ResetConnection()
    {
        _buffer?.Dispose();
        _buffer = null;
        _windowHead = 0;
        _windowCount = 0;
        _totalFramesRead = 0;
        _lastPttFlags = 0;
        _lastGamePhase = uint.MaxValue;
        _lastActiveMatchId = uint.MaxValue;
        _lastCurrentMap = string.Empty;
        _lastPlayerName = string.Empty;

        if (CurrentSnapshot.IsConnected)
        {
            CurrentSnapshot = ClientTelemetrySnapshot.Disconnected;
            OnSnapshotUpdated?.Invoke(CurrentSnapshot);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopMonitoring();
    }
}

// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SharedMemoryTelemetryConsumer.cs
// System: KONGOR Telemetry Core - Zero-Copy Shared Memory Telemetry Consumer
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using NEXUS.Contracts.Telemetry;

namespace ASPIRE.Common.Telemetry;

/// <summary>
///     Immutable statistical summary of live game engine telemetry and netcode performance.
/// </summary>
public sealed record TelemetryMetricsSummary(
    ulong TotalFramesRead,
    float CurrentFps,
    float AverageCpuFrametimeMs,
    float AverageGpuFrametimeMs,
    float Low1PercentFps,
    float AverageInputLatencyMs,
    float AveragePingMs,
    float PacketLossPercent,
    uint LatestServerTick,
    uint ActiveEntities,
    bool IsConnected)
{
    public static readonly TelemetryMetricsSummary Disconnected = new(
        TotalFramesRead: 0,
        CurrentFps: 0f,
        AverageCpuFrametimeMs: 0f,
        AverageGpuFrametimeMs: 0f,
        Low1PercentFps: 0f,
        AverageInputLatencyMs: 0f,
        AveragePingMs: 0f,
        PacketLossPercent: 0f,
        LatestServerTick: 0,
        ActiveEntities: 0,
        IsConnected: false);
}

/// <summary>
///     High-performance zero-copy consumer service reading real-time frame telemetry
///     from the game engine via lock-free memory-mapped circular ring buffers.
/// </summary>
public sealed class SharedMemoryTelemetryConsumer : IDisposable
{
    private const int SlidingWindowCapacity = 256;
    private readonly float[] _frametimeWindow = new float[SlidingWindowCapacity];
    private readonly float[] _sortBuffer = new float[SlidingWindowCapacity];
    private readonly Lock _sync = new();

    private MemoryMappedTelemetryBuffer? _buffer;
    private bool _ownsBuffer;
    private int _windowHead;
    private int _windowCount;
    private ulong _totalFramesRead;
    private FrameTelemetryRecord _latestRecord;
    private bool _disposed;

    public bool IsConnected
    {
        get
        {
            lock (_sync)
            {
                return _buffer != null;
            }
        }
    }

    public FrameTelemetryRecord LatestRecord
    {
        get
        {
            lock (_sync)
            {
                return _latestRecord;
            }
        }
    }

    public TelemetryMetricsSummary CurrentSummary { get; private set; } = TelemetryMetricsSummary.Disconnected;

    public event Action<TelemetryMetricsSummary>? OnMetricsUpdated;

    public SharedMemoryTelemetryConsumer()
    {
    }

    /// <summary>
    ///     Creates a consumer attaching to an already opened telemetry buffer (primarily for unit testing and direct injection).
    /// </summary>
    public SharedMemoryTelemetryConsumer(MemoryMappedTelemetryBuffer buffer, bool ownsBuffer = false)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _ownsBuffer = ownsBuffer;
    }

    public static string GetClientMapName(int processId) => $"Local\\KongorClientTelemetry_{processId}";

    public static string GetServerMapName(int port) => $"Local\\KongorServerTelemetry_{port}";

    /// <summary>
    ///     Attempts to connect to the specified shared memory ring buffer.
    /// </summary>
    public bool TryConnect(string mapName)
    {
        lock (_sync)
        {
            DisconnectUnsafe();

            if (MemoryMappedTelemetryBuffer.TryOpenConsumer(mapName, out MemoryMappedTelemetryBuffer? buffer, out _))
            {
                _buffer = buffer;
                _ownsBuffer = true;
                return true;
            }

            // Fallback for dedicated servers which might be registered under Global\ namespace
            if (mapName.StartsWith("Local\\", StringComparison.OrdinalIgnoreCase))
            {
                string globalMapName = "Global\\" + mapName[6..];
                if (MemoryMappedTelemetryBuffer.TryOpenConsumer(globalMapName, out MemoryMappedTelemetryBuffer? globalBuffer, out _))
                {
                    _buffer = globalBuffer;
                    _ownsBuffer = true;
                    return true;
                }
            }

            // Fallback for primary Fast Bus telemetry buffer (Local\KongorTelemetryBuffer) strictly when explicitly requested.
            // Never fall back for GetServerMapName(11235), otherwise dedicated worker #L1 (port 11235) cross-wires onto a local game client's telemetry buffer.
            if (string.Equals(mapName, "Local\\KongorTelemetryBuffer", StringComparison.OrdinalIgnoreCase))
            {
                if (MemoryMappedTelemetryBuffer.TryOpenConsumer("Local\\KongorTelemetryBuffer", out MemoryMappedTelemetryBuffer? fastBusBuffer, out _))
                {
                    _buffer = fastBusBuffer;
                    _ownsBuffer = true;
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    ///     Attaches an existing memory mapped buffer directly.
    /// </summary>
    public void AttachBuffer(MemoryMappedTelemetryBuffer buffer, bool ownsBuffer = false)
    {
        lock (_sync)
        {
            DisconnectUnsafe();
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _ownsBuffer = ownsBuffer;
        }
    }

    /// <summary>
    ///     Disconnects from the active shared memory buffer and resets statistical counters.
    /// </summary>
    public void Disconnect()
    {
        lock (_sync)
        {
            DisconnectUnsafe();
        }
    }

    private void DisconnectUnsafe()
    {
        if (_ownsBuffer && _buffer != null)
        {
            _buffer.Dispose();
        }

        _buffer = null;
        _ownsBuffer = false;
        _windowHead = 0;
        _windowCount = 0;
        _totalFramesRead = 0;
        _latestRecord = default;
        CurrentSummary = TelemetryMetricsSummary.Disconnected;
    }

    /// <summary>
    ///     Attempts to read the single latest frame from the shared memory buffer.
    /// </summary>
    public bool TryGetLatestFrame(out FrameTelemetryRecord record)
    {
        lock (_sync)
        {
            if (_buffer != null && _buffer.ReadLatest(out record))
            {
                _latestRecord = record;
                return true;
            }

            record = _latestRecord;
            return false;
        }
    }

    /// <summary>
    ///     Reads all newly produced frames into the provided span.
    /// </summary>
    public int ReadAvailableFrames(Span<FrameTelemetryRecord> destination)
    {
        lock (_sync)
        {
            if (_buffer == null)
            {
                return 0;
            }

            int count = _buffer.ReadAvailable(destination);
            if (count > 0)
            {
                _totalFramesRead += (ulong)count;
                _latestRecord = destination[count - 1];
            }

            return count;
        }
    }

    /// <summary>
    ///     Polls newly produced records, updates the 256-sample sliding statistical window,
    ///     computes rolling FPS and 1% low metrics, and fires the notification event.
    /// </summary>
    public TelemetryMetricsSummary UpdateAndGetSummary()
    {
        lock (_sync)
        {
            if (_buffer == null)
            {
                CurrentSummary = TelemetryMetricsSummary.Disconnected;
                return CurrentSummary;
            }

            Span<FrameTelemetryRecord> batch = stackalloc FrameTelemetryRecord[128];
            int readCount;

            while ((readCount = _buffer.ReadAvailable(batch)) > 0)
            {
                _totalFramesRead += (ulong)readCount;
                _latestRecord = batch[readCount - 1];

                for (int i = 0; i < readCount; i++)
                {
                    float frametime = batch[i].CpuFrameTimeMs;
                    if (frametime > 0.001f && frametime < 500f) // filter anomalies
                    {
                        _frametimeWindow[_windowHead] = frametime;
                        _windowHead = (_windowHead + 1) % SlidingWindowCapacity;
                        if (_windowCount < SlidingWindowCapacity)
                        {
                            _windowCount++;
                        }
                    }
                }
            }

            if (_windowCount == 0)
            {
                if (_buffer.ReadLatest(out FrameTelemetryRecord fallback))
                {
                    _latestRecord = fallback;
                }

                CurrentSummary = new TelemetryMetricsSummary(
                    TotalFramesRead: _totalFramesRead,
                    CurrentFps: _latestRecord.CpuFrameTimeMs > 0.001f ? (1000f / _latestRecord.CpuFrameTimeMs) : 0f,
                    AverageCpuFrametimeMs: _latestRecord.CpuFrameTimeMs,
                    AverageGpuFrametimeMs: _latestRecord.GpuFrameTimeMs,
                    Low1PercentFps: _latestRecord.CpuFrameTimeMs > 0.001f ? (1000f / _latestRecord.CpuFrameTimeMs) : 0f,
                    AverageInputLatencyMs: _latestRecord.InputLatencyMs,
                    AveragePingMs: _latestRecord.PingMicroseconds / 1000f,
                    PacketLossPercent: _latestRecord.PacketLossPercent,
                    LatestServerTick: _latestRecord.ServerTick,
                    ActiveEntities: _latestRecord.ActiveEntities,
                    IsConnected: true);

                OnMetricsUpdated?.Invoke(CurrentSummary);
                return CurrentSummary;
            }

            // Calculate moving average
            float sumFrametime = 0f;
            for (int i = 0; i < _windowCount; i++)
            {
                sumFrametime += _frametimeWindow[i];
                _sortBuffer[i] = _frametimeWindow[i];
            }

            float avgFrametime = sumFrametime / _windowCount;
            float currentFps = avgFrametime > 0.001f ? (1000f / avgFrametime) : 0f;

            // Calculate 1% Low FPS (99th percentile frame duration)
            Array.Sort(_sortBuffer, 0, _windowCount);
            int p99Index = (int)Math.Floor(_windowCount * 0.99f);
            if (p99Index >= _windowCount)
            {
                p99Index = _windowCount - 1;
            }
            float p99Frametime = _sortBuffer[p99Index];
            float low1PercentFps = p99Frametime > 0.001f ? (1000f / p99Frametime) : 0f;

            CurrentSummary = new TelemetryMetricsSummary(
                TotalFramesRead: _totalFramesRead,
                CurrentFps: MathF.Round(currentFps, 1),
                AverageCpuFrametimeMs: MathF.Round(avgFrametime, 2),
                AverageGpuFrametimeMs: MathF.Round(_latestRecord.GpuFrameTimeMs, 2),
                Low1PercentFps: MathF.Round(low1PercentFps, 1),
                AverageInputLatencyMs: MathF.Round(_latestRecord.InputLatencyMs, 2),
                AveragePingMs: MathF.Round(_latestRecord.PingMicroseconds / 1000f, 2),
                PacketLossPercent: MathF.Round(_latestRecord.PacketLossPercent, 2),
                LatestServerTick: _latestRecord.ServerTick,
                ActiveEntities: _latestRecord.ActiveEntities,
                IsConnected: true);

            OnMetricsUpdated?.Invoke(CurrentSummary);
            return CurrentSummary;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            DisconnectUnsafe();
        }
    }
}

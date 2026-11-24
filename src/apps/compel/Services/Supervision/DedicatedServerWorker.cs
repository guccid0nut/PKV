// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using ASPIRE.Common.Telemetry;
using COMPEL.Endpoints;
using COMPEL.Services.Telemetry;
using NEXUS.Contracts.Telemetry;

namespace COMPEL.Services.Supervision;

/// <summary>
/// Represents a captured console stdout or stderr line emitted by a dedicated game server worker.
/// </summary>
public readonly record struct DedicatedServerLogEntry(int Port, string Text, bool IsError);

/// <summary>
/// Represents an active Server-Sent Events (SSE) or reactive stream subscription to a dedicated server worker's logs.
/// </summary>
public sealed class DedicatedServerWorkerLogSubscription : IDisposable
{
    private readonly DedicatedServerWorker _worker;
    private readonly Channel<DedicatedServerLogLine> _channel;
    private int _disposed;

    public ChannelReader<DedicatedServerLogLine> Reader => _channel.Reader;
    internal ChannelWriter<DedicatedServerLogLine> Writer => _channel.Writer;

    public DedicatedServerWorkerLogSubscription(DedicatedServerWorker worker)
    {
        _worker = worker;
        _channel = Channel.CreateBounded<DedicatedServerLogLine>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Writer.TryComplete();
        _worker.UnsubscribeLogStream(this);
    }
}

/// <summary>
/// Encapsulates the runtime state, process handle, standard input stream, and port allocation
/// for an individual dedicated game server worker instance (hon_x64 in -dedicated mode).
/// </summary>
public sealed class DedicatedServerWorker
{
    public DedicatedServerWorker(
        int instanceIndex,
        int port,
        int voicePort,
        int publicGamePort,
        int publicVoicePort,
        string serverName,
        int targetTickRate = Configuration.MatchServerManagerOptions.CanonicalSimulationTickRate,
        int workerSlotIndex = -1)
    {
        InstanceIndex = instanceIndex;
        WorkerSlotIndex = workerSlotIndex >= 0 ? workerSlotIndex : (instanceIndex >= 1000 ? (instanceIndex % 1000) - 1 : Math.Max(0, instanceIndex - 1));
        Port = port;
        VoicePort = voicePort;
        PublicGamePort = publicGamePort;
        PublicVoicePort = publicVoicePort;
        ServerName = serverName;
        TickrateTracker = new ServerTickrateTracker(targetTickRate);
    }

    /// <summary>
    /// Real-time sub-millisecond simulation tickrate and variance tracker for this dedicated server worker.
    /// </summary>
    public ServerTickrateTracker TickrateTracker { get; }

    private readonly SemaphoreSlim _stdinLock = new(1, 1);

    /// <summary>
    /// 1-based index of this server instance (1..N, or tier-partitioned e.g. 1001+, 2001+, 3001+).
    /// </summary>
    public int InstanceIndex { get; }

    /// <summary>
    /// 0-based local slot index within this COMPEL node (0..N-1), used for CPU topology core affinity pinning.
    /// </summary>
    public int WorkerSlotIndex { get; }

    /// <summary>
    /// Local UDP listen port for game traffic (e.g. 11235).
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Local UDP listen port for voice traffic (e.g. 11435).
    /// </summary>
    public int VoicePort { get; }

    /// <summary>
    /// Public UDP game port (equal to Port or offset by 10000 when proxy is active).
    /// </summary>
    public int PublicGamePort { get; }

    /// <summary>
    /// Public UDP voice port (equal to VoicePort or offset by 10000 when proxy is active).
    /// </summary>
    public int PublicVoicePort { get; }

    /// <summary>
    /// Advertised display name for this dedicated game server.
    /// </summary>
    public string ServerName { get; set; }

    /// <summary>
    /// Named Pipe IPC pipe name (hon-server-{port}).
    /// </summary>
    public string PipeName => $"hon-server-{Port}";

    /// <summary>
    /// Target pipe path (\\.\pipe\hon-server-{port}).
    /// </summary>
    public string FullPipePath => $@"\\.\pipe\{PipeName}";

    /// <summary>
    /// Active process handle. Null when offline or crashed.
    /// </summary>
    public Process? Process { get; set; }

    /// <summary>
    /// Redirected standard input stream for instant console command execution.
    /// </summary>
    public StreamWriter? StdinWriter { get; set; }

    /// <summary>
    /// CancellationTokenSource governing stdout/stderr PipeReader streams.
    /// </summary>
    public CancellationTokenSource? StreamCts { get; set; }

    /// <summary>
    /// Bounded in-memory channel decoupling PipeReader consumption from downstream logging and telemetry dispatch.
    /// </summary>
    public Channel<DedicatedServerLogEntry>? StreamChannel { get; set; }

    /// <summary>
    /// Active Write-Ahead Log (WAL) writer for in-flight match replays and combat event journaling.
    /// </summary>
    public ReplayWalWriter? WalWriter { get; set; }

    /// <summary>
    /// Real-time compact WAL status beacon (~58 bytes) for in-engine Ctrl+Shift+D diagnostic QR code multiplexing.
    /// </summary>
    public ReplayWalBeacon? CurrentWalBeacon => WalWriter?.CurrentBeacon;

    /// <summary>
    /// Asynchronous background consumer task pumping log entries from StreamChannel to logger and telemetry relay.
    /// </summary>
    public Task? StreamConsumerTask { get; set; }

    /// <summary>
    /// Master Server session cookie returned from f=new_session.
    /// </summary>
    public string? SessionCookie { get; set; }

    /// <summary>
    /// Master Server deterministic integer server ID.
    /// </summary>
    public int? ServerId { get; set; }

    /// <summary>
    /// Currently hosted or most recently completed match ID.
    /// </summary>
    public long MatchId { get; set; }

    /// <summary>
    /// Process start timestamp.
    /// </summary>
    public DateTimeOffset? StartTime { get; set; }

    /// <summary>
    /// Monotonic per-launch boot nonce minted by COMPEL when spawning a worker process.
    /// Used to bind initial f=new_session cookie reuse strictly to the live process incarnation.
    /// </summary>
    public string? LaunchBootNonce { get; set; }

    /// <summary>
    /// Captured OS process ID of the launched worker process (preserved even across quick exits for diagnostics/auth binding).
    /// </summary>
    public int? LaunchProcessId { get; set; }

    /// <summary>
    /// Captured OS process start timestamp (UTC) for process identity binding on f=new_session.
    /// </summary>
    public DateTimeOffset? LaunchProcessStartUtc { get; set; }

    /// <summary>
    /// Environment tick timestamp of the last spawn attempt.
    /// </summary>
    public long LastAttemptTicks { get; set; }

    /// <summary>
    /// Cumulative crash/exit count for this worker instance.
    /// </summary>
    public int CrashCount { get; set; }

    /// <summary>
    /// Total count of completed matches hosted by this worker instance since its last recycle.
    /// </summary>
    public int MatchesHostedSinceRecycle { get; set; }

    /// <summary>
    /// Total lifetime matches hosted by this worker slot across all recycles.
    /// </summary>
    public int TotalLifetimeMatchesHosted { get; set; }

    /// <summary>
    /// Rapid consecutive crash count within the throttle evaluation window.
    /// </summary>
    public int ConsecutiveCrashCount { get; set; }

    /// <summary>
    /// Timestamp of the most recent crash exit.
    /// </summary>
    public DateTimeOffset? LastCrashUtc { get; set; }

    /// <summary>
    /// Staged rel32 detour recipe to apply to this worker upon next launch.
    /// </summary>
    public Kongor.Core.Services.Native.SynthesizedDetour? StagedDetourRecipe { get; set; }

    /// <summary>
    /// Whether this worker is intended to be running.
    /// </summary>
    public volatile bool DesiredRunning = true;

    /// <summary>
    /// Whether this worker is currently in draining mode pending graceful shutdown/resurrection.
    /// </summary>
    public volatile bool IsDraining;

    /// <summary>
    /// Human-readable reason why this worker was marked for draining.
    /// </summary>
    public string? DrainReason { get; set; }

    /// <summary>
    /// Atomic sentinel indicating whether an automated recycling/reset sequence is currently in progress.
    /// </summary>
    public int IsRecycling;

    /// <summary>
    /// Thread-safe map of active client slot indices (0..N-1) currently connected to this worker.
    /// </summary>
    public ConcurrentDictionary<int, byte> ActiveClientSlots { get; } = new();

    /// <summary>
    /// Number of active connected clients tracked on this worker instance.
    /// </summary>
    public int ConnectedClients { get; set; }

    /// <summary>
    /// Monotonic generation counter for the all-clients-disconnected grace timer.
    /// Incremented (via <see cref="Interlocked.Increment(ref long)"/>) on every client connect and every zero-client event.
    /// A pending grace timer may only recycle the worker if the generation it captured is still current, so a
    /// disconnect -> reconnect -> disconnect sequence always receives a full, fresh grace window.
    /// </summary>
    public long DisconnectGraceGeneration;

    /// <summary>
    /// Current map loaded or pre-warmed on this dedicated server.
    /// </summary>
    public string CurrentMap { get; set; } = "midwars";

    /// <summary>
    /// Active match name or advertised custom game name.
    /// </summary>
    public string? MatchName { get; set; }

    /// <summary>
    /// UTC timestamp when a lobby/arranged match was opened on this worker (via StartGame / LoadWorld / CreateGame / c_state=2).
    /// Null when the worker is sitting in a pristine idle warm state prior to any match or lobby allocation.
    /// </summary>
    public DateTimeOffset? LobbyOpenedAtUtc { get; set; }

    /// <summary>
    /// Timestamp when draining was initiated.
    /// </summary>
    public DateTimeOffset? DrainStartTime { get; set; }

    private const int MaxLogHistory = 500;
    private readonly Channel<DedicatedServerLogLine> _recentLogsChannel = Channel.CreateBounded<DedicatedServerLogLine>(
        new BoundedChannelOptions(MaxLogHistory)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = false
        });
    private readonly DedicatedServerLogLine[] _recentLogsRing = new DedicatedServerLogLine[MaxLogHistory];
    private int _recentLogsHead;
    private int _recentLogsCount;
    private readonly Lock _logHistoryLock = new();
    private readonly Lock _recentChatLock = new();
    private readonly Dictionary<string, DateTimeOffset> _recentChatMessages = new(StringComparer.Ordinal);

    private volatile string _currentState = "Stopped";

    /// <summary>
    /// Current logical lifecycle state of the dedicated server worker:
    /// "Available", "InMatch", "Starting", "Restarting", "Recovering", "Stopped", "Crashed", "Draining".
    /// </summary>
    public string CurrentState
    {
        get
        {
            if (IsDraining && IsRunning)
            {
                return "Draining";
            }
            if (!IsRunning)
            {
                if (_currentState.Equals("Recovering", StringComparison.OrdinalIgnoreCase) ||
                    _currentState.Equals("Starting", StringComparison.OrdinalIgnoreCase) ||
                    _currentState.Equals("Restarting", StringComparison.OrdinalIgnoreCase))
                {
                    return _currentState;
                }
                return CrashCount > 0 && DesiredRunning ? "Crashed" : "Stopped";
            }
            if (_currentState.Equals("Stopped", StringComparison.OrdinalIgnoreCase))
            {
                return "Available";
            }
            return _currentState;
        }
        set => _currentState = value;
    }

    /// <summary>
    /// Whether this worker is in an active, healthy state capable of accepting new matches.
    /// Draining, recovering, crashed, or in-match workers return false.
    /// </summary>
    public bool CanAcceptNewMatches => IsRunning && !IsDraining && CurrentState.Equals("Available", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this worker is currently actively hosting a game match.
    /// Returns false if the process is offline, stopped, or crashed.
    /// Preserves true even if the worker was marked for draining during the match.
    /// </summary>
    public bool IsInMatch => IsRunning && _currentState.Equals("InMatch", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Active player/bot count connected to this server instance.
    /// </summary>
    public int PlayerCount { get; set; }

    private readonly List<DedicatedServerWorkerLogSubscription> _logSubscriptions = [];
    private readonly Lock _subscriptionLock = new();

    /// <summary>
    /// Subscribes to live log lines emitted by this dedicated server instance.
    /// Disposing the returned subscription completes the channel and removes it from the subscriber registry.
    /// </summary>
    public DedicatedServerWorkerLogSubscription SubscribeLogStream()
    {
        DedicatedServerWorkerLogSubscription subscription = new(this);
        lock (_subscriptionLock)
        {
            _logSubscriptions.Add(subscription);
        }
        return subscription;
    }

    internal void UnsubscribeLogStream(DedicatedServerWorkerLogSubscription subscription)
    {
        lock (_subscriptionLock)
        {
            _logSubscriptions.Remove(subscription);
        }
    }

    /// <summary>
    /// Records a console line or operator command into the bounded DropOldest channel and O(1) rolling buffer.
    /// </summary>
    public void AddLogLine(string text, bool isError = false, string source = "server")
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        DedicatedServerLogLine line = new(TimeProvider.System.GetUtcNow(), text.Trim(), isError, source);
        _recentLogsChannel.Writer.TryWrite(line);

        lock (_logHistoryLock)
        {
            _recentLogsRing[_recentLogsHead] = line;
            _recentLogsHead = (_recentLogsHead + 1) % MaxLogHistory;
            if (_recentLogsCount < MaxLogHistory)
            {
                _recentLogsCount++;
            }
        }

        if (WalWriter != null)
        {
            WalWriter.TryEnqueueLine(text);
        }

        lock (_subscriptionLock)
        {
            if (_logSubscriptions.Count > 0)
            {
                for (int i = 0; i < _logSubscriptions.Count; i++)
                {
                    _logSubscriptions[i].Writer.TryWrite(line);
                }
            }
        }
    }

    /// <summary>
    /// Records an in-game chat message and returns true if this is the first time seen within a 15-second sliding window.
    /// Returns false if it is a duplicate to prevent echoing between stream pipe and log tailers.
    /// </summary>
    public bool TryRecordRecentChatMessage(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        string clean = line.Trim();
        var now = TimeProvider.System.GetUtcNow();

        lock (_recentChatLock)
        {
            var expired = new List<string>();
            foreach (var kvp in _recentChatMessages)
            {
                if (now - kvp.Value > TimeSpan.FromSeconds(15))
                {
                    expired.Add(kvp.Key);
                }
            }
            foreach (var key in expired)
            {
                _recentChatMessages.Remove(key);
            }

            if (_recentChatMessages.TryGetValue(clean, out var lastSeen))
            {
                if (now - lastSeen < TimeSpan.FromSeconds(15))
                {
                    return false;
                }
            }

            _recentChatMessages[clean] = now;
            return true;
        }
    }

    /// <summary>
    /// Retrieves recent log lines from the in-memory rolling buffer up to the specified limit.
    /// </summary>
    public IReadOnlyList<DedicatedServerLogLine> GetRecentLogs(int limit = 100)
    {
        if (limit <= 0) return [];
        lock (_logHistoryLock)
        {
            if (_recentLogsCount == 0) return [];
            int take = Math.Min(limit, _recentLogsCount);
            DedicatedServerLogLine[] result = new DedicatedServerLogLine[take];
            int start = (_recentLogsHead - take + MaxLogHistory) % MaxLogHistory;
            for (int i = 0; i < take; i++)
            {
                result[i] = _recentLogsRing[(start + i) % MaxLogHistory];
            }
            return result;
        }
    }

    /// <summary>
    /// Clears the in-memory rolling log buffer.
    /// </summary>
    public void ClearLogHistory()
    {
        while (_recentLogsChannel.Reader.TryRead(out _)) { }
        lock (_logHistoryLock)
        {
            Array.Clear(_recentLogsRing);
            _recentLogsHead = 0;
            _recentLogsCount = 0;
        }
    }

    private bool? _isRunningOverride;
    internal bool? IsRunningOverride
    {
        get => _isRunningOverride;
        set => _isRunningOverride = value;
    }

    /// <summary>
    /// Whether the worker process is currently running and has not exited.
    /// </summary>
    public bool IsRunning => _isRunningOverride ?? (Process is { HasExited: false });

    /// <summary>
    /// Process ID or null if not running.
    /// </summary>
    public int? ProcessId
    {
        get
        {
            try
            {
                return Process is { HasExited: false } ? Process.Id : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Current working set memory bytes.
    /// </summary>
    public long WorkingSetBytes
    {
        get
        {
            try
            {
                if (Process is { HasExited: false })
                {
                    Process.Refresh();
                    return Process.WorkingSet64;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DedicatedServer:{Port}] WorkingSetBytes query failed: {ex.Message}");
            }
            return 0;
        }
    }

    /// <summary>
    /// Current working set in megabytes.
    /// </summary>
    public double WorkingSetMb => Math.Round(WorkingSetBytes / (1024.0 * 1024.0), 2);

    /// <summary>
    /// Total elapsed uptime seconds.
    /// </summary>
    public double UptimeSeconds => StartTime.HasValue
        ? Math.Max(0, (TimeProvider.System.GetUtcNow() - StartTime.Value).TotalSeconds)
        : 0;

    /// <summary>
    /// Writes a console command or cvar directly to the dedicated server's standard input stream.
    /// </summary>
    public async Task<bool> WriteStdinAsync(string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        StreamWriter? writer = StdinWriter;
        if (writer == null || !IsRunning) return false;

        if (!await _stdinLock.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false))
            return false;

        try
        {
            writer = StdinWriter;
            if (writer == null || !IsRunning) return false;

            await writer.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    /// <summary>
    /// Gets the canonical Linux Unix Domain Socket path for the in-process NativeGuard IPC server.
    /// </summary>
    public static string GetLinuxNativeSocketPath(int port) => $"/tmp/hon-server-native-{port}.sock";

    /// <summary>
    /// Non-blocking snapshot of this worker's active IPC endpoint (NativeGuard Unix socket on Linux/Docker,
    /// NativeGuard or COMPEL command Named Pipe on Windows) without initiating any intrusive pipe connection.
    /// </summary>
    public (bool IpcConnected, string? IpcPath, string? IpcTransport) GetIpcStatusSnapshot()
    {
        if (!IsRunning)
        {
            return (false, null, null);
        }

        if (!string.IsNullOrWhiteSpace(UnixSocketPathOverride) || !OperatingSystem.IsWindows())
        {
            string socketPath = !string.IsNullOrWhiteSpace(UnixSocketPathOverride)
                ? UnixSocketPathOverride!
                : GetLinuxNativeSocketPath(Port);
            try
            {
                if (File.Exists(socketPath))
                {
                    return (true, socketPath, "UnixSocket");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DedicatedServer:{Port}] Unix socket existence check notice for '{socketPath}': {ex.Message}");
            }

            return StdinWriter is not null
                ? (true, $"stdin:{Port}", "Stdin")
                : (false, socketPath, "UnixSocket");
        }

        string nativePipeName = !string.IsNullOrWhiteSpace(NativePipeNameOverride)
            ? NativePipeNameOverride!
            : $"hon-server-native-{Port}";
        string nativePipePath = $@"\\.\pipe\{nativePipeName}";
        try
        {
            if (File.Exists(nativePipePath))
            {
                return (true, nativePipePath, "NamedPipe");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DedicatedServer:{Port}] Native pipe existence check notice for '{nativePipePath}': {ex.Message}");
        }

        if (_pipeServerTask is { IsCompleted: false })
        {
            return (true, FullPipePath, "NamedPipe");
        }

        try
        {
            if (File.Exists(FullPipePath))
            {
                return (true, FullPipePath, "NamedPipe");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DedicatedServer:{Port}] Command pipe existence check notice for '{FullPipePath}': {ex.Message}");
        }

        return StdinWriter is not null
            ? (true, $"stdin:{Port}", "Stdin")
            : (false, nativePipePath, "NamedPipe");
    }

    /// <summary>
    /// Optional Unix domain socket path override for cross-platform integration tests.
    /// </summary>
    public string? UnixSocketPathOverride { get; set; }

    /// <summary>
    /// Optional Windows Named Pipe name override for hermetic unit tests of NativeGuard IPC framing.
    /// </summary>
    public string? NativePipeNameOverride { get; set; }

    /// <summary>
    /// Sends a binary framed IPC request ([0x4D, 0x57, OpCode, PayloadLen, ReqId, Payload]) over a Unix Domain Socket
    /// (/tmp/hon-server-native-{Port}.sock) and reads the framed response.
    /// </summary>
    public async Task<(bool Success, ushort ResponseOpCode, string ResponsePayload)> SendUnixSocketFrameAsync(
        ushort opCode,
        string payload,
        int timeoutMs = 500,
        CancellationToken cancellationToken = default)
    {
        string socketPath = !string.IsNullOrWhiteSpace(UnixSocketPathOverride)
            ? UnixSocketPathOverride
            : GetLinuxNativeSocketPath(Port);

        if (!File.Exists(socketPath))
            return (false, 0, string.Empty);

        using CancellationTokenSource timeoutCts = new(Math.Max(50, timeoutMs));
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using Socket unixSocket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await unixSocket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), linkedCts.Token).ConfigureAwait(false);

            if (opCode != 0x0010 && opCode != 0x0001)
            {
                string? ipcToken = Environment.GetEnvironmentVariable("KONGOR_IPC_TOKEN");
                if (!string.IsNullOrEmpty(ipcToken))
                {
                    byte[] tokenBytes = Encoding.UTF8.GetBytes(ipcToken);
                    byte[] authFrame = new byte[12 + tokenBytes.Length];
                    authFrame[0] = 0x4D;
                    authFrame[1] = 0x57;
                    BitConverter.TryWriteBytes(authFrame.AsSpan(2, 2), (ushort)0x0010);
                    BitConverter.TryWriteBytes(authFrame.AsSpan(4, 4), tokenBytes.Length);
                    BitConverter.TryWriteBytes(authFrame.AsSpan(8, 4), 1u);
                    tokenBytes.CopyTo(authFrame.AsSpan(12));

                    await unixSocket.SendAsync(authFrame, SocketFlags.None, linkedCts.Token).ConfigureAwait(false);

                    byte[] authHdr = new byte[12];
                    int authHdrRead = 0;
                    while (authHdrRead < 12)
                    {
                        int r = await unixSocket.ReceiveAsync(authHdr.AsMemory(authHdrRead, 12 - authHdrRead), SocketFlags.None, linkedCts.Token).ConfigureAwait(false);
                        if (r <= 0) break;
                        authHdrRead += r;
                    }
                    if (authHdrRead == 12)
                    {
                        int authRespLen = BitConverter.ToInt32(authHdr.AsSpan(4, 4));
                        if (authRespLen > 0 && authRespLen <= 65536)
                        {
                            byte[] discardBuf = new byte[authRespLen];
                            int dRead = 0;
                            while (dRead < authRespLen)
                            {
                                int r = await unixSocket.ReceiveAsync(discardBuf.AsMemory(dRead, authRespLen - dRead), SocketFlags.None, linkedCts.Token).ConfigureAwait(false);
                                if (r <= 0) break;
                                dRead += r;
                            }
                        }
                    }
                }
            }

            byte[] payloadBytes = Encoding.UTF8.GetBytes(payload ?? string.Empty);
            byte[] frame = new byte[12 + payloadBytes.Length];
            frame[0] = 0x4D;
            frame[1] = 0x57;
            BitConverter.TryWriteBytes(frame.AsSpan(2, 2), opCode);
            BitConverter.TryWriteBytes(frame.AsSpan(4, 4), payloadBytes.Length);
            BitConverter.TryWriteBytes(frame.AsSpan(8, 4), 1u);
            payloadBytes.CopyTo(frame.AsSpan(12));

            await unixSocket.SendAsync(frame, SocketFlags.None, linkedCts.Token).ConfigureAwait(false);

            byte[] headerBuf = new byte[12];
            int headerRead = 0;
            while (headerRead < 12)
            {
                int r = await unixSocket.ReceiveAsync(headerBuf.AsMemory(headerRead, 12 - headerRead), SocketFlags.None, linkedCts.Token).ConfigureAwait(false);
                if (r <= 0) break;
                headerRead += r;
            }

            if (headerRead == 12 && headerBuf[0] == 0x4D && headerBuf[1] == 0x57)
            {
                ushort respOpCode = BitConverter.ToUInt16(headerBuf.AsSpan(2, 2));
                int respLen = BitConverter.ToInt32(headerBuf.AsSpan(4, 4));
                if (respLen > 0 && respLen <= 4 * 1024 * 1024)
                {
                    byte[] respBytes = new byte[respLen];
                    int bodyRead = 0;
                    while (bodyRead < respLen)
                    {
                        int r = await unixSocket.ReceiveAsync(respBytes.AsMemory(bodyRead, respLen - bodyRead), SocketFlags.None, linkedCts.Token).ConfigureAwait(false);
                        if (r <= 0) break;
                        bodyRead += r;
                    }
                    string respStr = Encoding.UTF8.GetString(respBytes, 0, bodyRead);
                    return (true, respOpCode, respStr);
                }

                return (true, respOpCode, string.Empty);
            }

            return (true, 0, string.Empty);
        }
        catch
        {
            return (false, 0, string.Empty);
        }
    }

    /// <summary>
    /// Evaluates a Lua snippet synchronously against the dedicated server's NativeGuard IPC endpoint
    /// (OpCode 0x000E EvalLuaSync -> 0x000F EvalLuaResponse).
    /// </summary>
    public async Task<string?> EvalLuaNativeAsync(string luaCode, int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(luaCode))
            return null;

        if (!string.IsNullOrWhiteSpace(UnixSocketPathOverride) || !OperatingSystem.IsWindows())
        {
            (bool ok, ushort respOp, string respPayload) = await SendUnixSocketFrameAsync(0x000E, luaCode, timeoutMs, cancellationToken).ConfigureAwait(false);
            if (ok && respOp == 0x000F)
            {
                return respPayload;
            }
            return null;
        }

        string nativePipeName = $"hon-server-native-{Port}";
        using CancellationTokenSource timeoutCts = new(Math.Max(100, timeoutMs));
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await using NamedPipeClientStream nativeClient = new(
                ".",
                nativePipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await nativeClient.ConnectAsync(linkedCts.Token).ConfigureAwait(false);

            byte[] cmdBytes = Encoding.UTF8.GetBytes(luaCode);
            byte[] frame = new byte[12 + cmdBytes.Length];
            frame[0] = 0x4D;
            frame[1] = 0x57;
            BitConverter.TryWriteBytes(frame.AsSpan(2, 2), (ushort)0x000E);
            BitConverter.TryWriteBytes(frame.AsSpan(4, 4), cmdBytes.Length);
            BitConverter.TryWriteBytes(frame.AsSpan(8, 4), 1u);
            cmdBytes.CopyTo(frame.AsSpan(12));

            await nativeClient.WriteAsync(frame, linkedCts.Token).ConfigureAwait(false);
            await nativeClient.FlushAsync(linkedCts.Token).ConfigureAwait(false);

            byte[] headerBuf = new byte[12];
            int headerRead = 0;
            while (headerRead < 12)
            {
                int r = await nativeClient.ReadAsync(headerBuf.AsMemory(headerRead, 12 - headerRead), linkedCts.Token).ConfigureAwait(false);
                if (r <= 0) break;
                headerRead += r;
            }

            if (headerRead == 12 && headerBuf[0] == 0x4D && headerBuf[1] == 0x57)
            {
                ushort respOp = BitConverter.ToUInt16(headerBuf.AsSpan(2, 2));
                int respLen = BitConverter.ToInt32(headerBuf.AsSpan(4, 4));
                if (respOp == 0x000F && respLen > 0 && respLen <= 4 * 1024 * 1024)
                {
                    byte[] respBytes = new byte[respLen];
                    int bodyRead = 0;
                    while (bodyRead < respLen)
                    {
                        int r = await nativeClient.ReadAsync(respBytes.AsMemory(bodyRead, respLen - bodyRead), linkedCts.Token).ConfigureAwait(false);
                        if (r <= 0) break;
                        bodyRead += r;
                    }
                    return Encoding.UTF8.GetString(respBytes, 0, bodyRead);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DedicatedServer:{Port}] EvalLuaNativeAsync fallback: {ex.Message}");
        }

        return null;
    }

    public Task<(bool Success, string Channel, string Detail)> WriteCommandAsync(
        string command,
        CancellationToken cancellationToken)
        => WriteCommandAsync(command, 500, cancellationToken);

    /// <summary>
    /// Dispatches a console command to the dedicated server via its in-process NativeGuard binary pipe
    /// (\\.\pipe\hon-server-native-{port}, OpCode 0x0003 -> 0x0004 JSON ACK) or Linux Unix domain socket,
    /// falling back to redirected stdin only when the native pipe is unavailable.
    /// </summary>
    public async Task<(bool Success, string Channel, string Detail)> WriteCommandAsync(
        string command,
        int timeoutMs = 500,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command) || !IsRunning)
        {
            return (false, "offline", "Worker is offline or command is empty.");
        }

        try
        {
            int effectiveTimeoutMs = Math.Max(100, timeoutMs);

            if (!string.IsNullOrWhiteSpace(UnixSocketPathOverride) || !OperatingSystem.IsWindows())
            {
                (bool unixOk, ushort unixRespOp, string unixPayload) =
                    await SendUnixSocketFrameAsync(0x0003, command, effectiveTimeoutMs, cancellationToken).ConfigureAwait(false);
                if (unixOk && (unixRespOp == 0x0004 || unixRespOp == 0) &&
                    !unixPayload.Contains("\"success\":false", StringComparison.OrdinalIgnoreCase))
                {
                    return (true, "native-socket", string.IsNullOrWhiteSpace(unixPayload) ? "Dispatched via Unix native socket." : unixPayload);
                }
            }

            bool canAttemptWindowsPipe = OperatingSystem.IsWindows() &&
                (Process != null || !string.IsNullOrWhiteSpace(NativePipeNameOverride) || Port > 11250);

            if (canAttemptWindowsPipe)
            {
                string nativePipeName = !string.IsNullOrWhiteSpace(NativePipeNameOverride)
                    ? NativePipeNameOverride!
                    : $"hon-server-native-{Port}";
                using CancellationTokenSource timeoutCts = new(effectiveTimeoutMs);
                using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                try
                {
                    await using NamedPipeClientStream nativeClient = new(
                        ".",
                        nativePipeName,
                        PipeDirection.InOut,
                        PipeOptions.Asynchronous);

                    await nativeClient.ConnectAsync(linkedCts.Token).ConfigureAwait(false);

                    // Frame command: [0x4D, 0x57, OpCode=3 (ExecuteCommand), PayloadLen, ReqId=1, Payload]
                    byte[] cmdBytes = Encoding.UTF8.GetBytes(command);
                    byte[] frame = new byte[12 + cmdBytes.Length];
                    frame[0] = 0x4D;
                    frame[1] = 0x57;
                    BitConverter.TryWriteBytes(frame.AsSpan(2, 2), (ushort)0x0003);
                    BitConverter.TryWriteBytes(frame.AsSpan(4, 4), cmdBytes.Length);
                    BitConverter.TryWriteBytes(frame.AsSpan(8, 4), 1u);
                    cmdBytes.CopyTo(frame.AsSpan(12));

                    await nativeClient.WriteAsync(frame, linkedCts.Token).ConfigureAwait(false);
                    try
                    {
                        await nativeClient.FlushAsync(linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (IOException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DedicatedServer:{Port}] WriteCommandAsync FlushAsync IOException: {ex.Message}");
                    }

                    byte[] headerBuf = new byte[12];
                    int headerRead = 0;
                    try
                    {
                        while (headerRead < 12)
                        {
                            int r = await nativeClient.ReadAsync(headerBuf.AsMemory(headerRead, 12 - headerRead), linkedCts.Token).ConfigureAwait(false);
                            if (r <= 0) break;
                            headerRead += r;
                        }
                    }
                    catch (IOException)
                    {
                        if (headerRead == 0)
                        {
                            return (true, "native-pipe", $"Successfully piped command '{command}' via NativeGuard pipe '{nativePipeName}'.");
                        }
                    }

                    if (headerRead == 0)
                    {
                        return (true, "native-pipe", $"Successfully piped command '{command}' via NativeGuard pipe '{nativePipeName}'.");
                    }

                    if (headerRead == 12 && headerBuf[0] == 0x4D && headerBuf[1] == 0x57)
                    {
                        ushort respOp = BitConverter.ToUInt16(headerBuf.AsSpan(2, 2));
                        int respLen = BitConverter.ToInt32(headerBuf.AsSpan(4, 4));
                        string respPayload = string.Empty;
                        if (respLen > 0 && respLen <= 4 * 1024 * 1024)
                        {
                            byte[] respBytes = new byte[respLen];
                            int bodyRead = 0;
                            while (bodyRead < respLen)
                            {
                                int r = await nativeClient.ReadAsync(respBytes.AsMemory(bodyRead, respLen - bodyRead), linkedCts.Token).ConfigureAwait(false);
                                if (r <= 0) break;
                                bodyRead += r;
                            }
                            respPayload = Encoding.UTF8.GetString(respBytes, 0, bodyRead);
                        }

                        if (respOp == 0x0004 && !respPayload.Contains("\"success\":false", StringComparison.OrdinalIgnoreCase))
                        {
                            return (true, "native-pipe", string.IsNullOrWhiteSpace(respPayload)
                                ? $"Successfully piped command '{command}' via NativeGuard pipe '{nativePipeName}'."
                                : respPayload);
                        }

                        return (false, "native-pipe", string.IsNullOrWhiteSpace(respPayload)
                            ? $"Unexpected NativeGuard response opcode 0x{respOp:X4}."
                            : respPayload);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DedicatedServer:{Port}] WriteCommandAsync native pipe fallback to stdin: {ex.Message}");
                }
            }

            bool stdinOk = await WriteStdinAsync(command, cancellationToken).ConfigureAwait(false);
            return stdinOk
                ? (true, "stdin", $"Successfully piped command '{command}' to server on port {Port} via stdin.")
                : (false, "failed", $"Failed to write command '{command}' to server on port {Port}: NativeGuard pipe 'hon-server-native-{Port}' and standard input stream are unavailable or process is unresponsive.");
        }
        catch (Exception ex)
        {
            return (false, "failed", ex.Message);
        }
    }

    /// <summary>
    /// Writes a console command to the dedicated server via its in-process native pipe (\\.\pipe\hon-server-native-{port})
    /// or Linux Unix domain socket (/tmp/hon-server-native-{port}.sock),
    /// falling back directly to redirected stdin without circular self-loopback through \\.\pipe\hon-server-{port}.
    /// </summary>
    public async Task<bool> WritePipeAsync(string command, int timeoutMs = 500, CancellationToken cancellationToken = default)
    {
        (bool success, _, _) = await WriteCommandAsync(command, timeoutMs, cancellationToken).ConfigureAwait(false);
        return success;
    }

    private CancellationTokenSource? _pipeServerCts;
    private Task? _pipeServerTask;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static NamedPipeServerStream CreateCommandPipeServer(string pipeName)
    {
        System.Security.Principal.SecurityIdentifier? ownerSid = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        PipeSecurity pipeSecurity = new();
        pipeSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        if (ownerSid is not null)
        {
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                ownerSid,
                PipeAccessRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity);
    }

    /// <summary>
    /// Starts an asynchronous Win32 Named Pipe server for this worker instance (\\.\pipe\hon-server-{port}).
    /// External tools, PowerShell scripts, and operators can write console commands to this pipe to execute them on the dedicated server.
    /// </summary>
    public void StartNamedPipeServer(Action<string>? logger = null)
    {
        if (!OperatingSystem.IsWindows()) return;

        _pipeServerCts?.Cancel();
        _pipeServerCts?.Dispose();
        _pipeServerCts = new CancellationTokenSource();
        CancellationToken ct = _pipeServerCts.Token;

        _pipeServerTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && IsRunning)
            {
                NamedPipeServerStream? serverStream = null;
                try
                {
                    if (!OperatingSystem.IsWindows()) break;
                    serverStream = CreateCommandPipeServer(PipeName);

                    await serverStream.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    using StreamReader reader = new(serverStream, Encoding.UTF8, leaveOpen: false);
                    string? line;
                    while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        string cmd = line.Trim();
                        logger?.Invoke($"[DedicatedServer:{Port}] Command received via Named Pipe '{PipeName}': '{cmd}'");
                        (bool forwarded, string fwdChannel, string fwdDetail) = await WriteCommandAsync(cmd, 500, ct).ConfigureAwait(false);
                        if (forwarded)
                        {
                            logger?.Invoke($"[DedicatedServer:{Port}] Forwarded Named Pipe command '{cmd}' via {fwdChannel} ({fwdDetail}).");
                        }
                        else
                        {
                            logger?.Invoke($"[DedicatedServer:{Port}] Failed to forward Named Pipe command '{cmd}' ({fwdDetail}).");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    logger?.Invoke($"[DedicatedServer:{Port}] Named Pipe listener error: {ex.Message}");
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                finally
                {
                    if (serverStream != null)
                    {
                        await serverStream.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }, ct);
    }

    private CancellationTokenSource? _telemetryCts;
    private Task? _telemetryTask;

    /// <summary>
    /// Active zero-copy shared memory consumer sampling 1000 FPS simulation telemetry directly from RAM.
    /// </summary>
    public SharedMemoryTelemetryConsumer? TelemetryConsumer { get; private set; }

    /// <summary>
    /// Most recent frame telemetry record sampled from shared memory.
    /// </summary>
    public FrameTelemetryRecord LatestTelemetryRecord => TelemetryConsumer?.LatestRecord ?? default;

    /// <summary>
    /// Whether the worker is actively connected to the native guard shared memory fast bus.
    /// </summary>
    public bool IsTelemetryConnected => TelemetryConsumer?.IsConnected ?? false;

    /// <summary>
    /// Rolling statistical summary of simulation performance (FPS, frametime, latency, server tick).
    /// </summary>
    public TelemetryMetricsSummary TelemetrySummary => TelemetryConsumer?.CurrentSummary ?? TelemetryMetricsSummary.Disconnected;

    /// <summary>
    /// Starts the high-speed shared memory telemetry consumer for this dedicated server worker.
    /// Decoupled from stdout parsing; tracks server ticks, entity counts, memory, and stalls directly from RAM.
    /// </summary>
    public void StartTelemetryPolling(Action<string>? logger = null, Action<DedicatedServerWorker, FrameTelemetryRecord>? onFrameRecord = null)
    {
        StopTelemetryPolling();

        _telemetryCts = new CancellationTokenSource();
        CancellationToken ct = _telemetryCts.Token;
        SharedMemoryTelemetryConsumer consumer = new();
        TelemetryConsumer = consumer;

        string mapName = SharedMemoryTelemetryConsumer.GetServerMapName(Port);

        _telemetryTask = Task.Run(async () =>
        {
            // Give native guard a brief moment to initialize the shared memory mapping
            while (!ct.IsCancellationRequested && IsRunning && !consumer.IsConnected)
            {
                if (consumer.TryConnect(mapName))
                {
                    logger?.Invoke($"[DedicatedServer:{Port}] Attached to Shared Memory Telemetry Fast Bus '{mapName}'.");
                    break;
                }

                try
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            ulong lastProcessedFrame = 0;
            uint lastProcessedTick = 0;
            bool sawPositiveActiveEntities = false;

            while (!ct.IsCancellationRequested && IsRunning)
            {
                try
                {
                    if (consumer.TryGetLatestFrame(out FrameTelemetryRecord record))
                    {
                        // Reset monotonic frame watermark if shared memory ring restarted with a lower FrameIndex after worker recycle
                        if (record.FrameIndex > 0 && record.FrameIndex < lastProcessedFrame)
                        {
                            lastProcessedFrame = 0;
                            lastProcessedTick = 0;
                            sawPositiveActiveEntities = false;
                        }

                        if (record.FrameIndex > lastProcessedFrame)
                        {
                            lastProcessedFrame = record.FrameIndex;

                            // Simulation tick tracking
                            if (record.ServerTick != lastProcessedTick && record.ServerTick > 0)
                            {
                                lastProcessedTick = record.ServerTick;
                                TickrateTracker.RecordTick();

                                // State confirmation: if server is ticking and was starting/stopped, it is now available
                                if (_currentState.Equals("Starting", StringComparison.OrdinalIgnoreCase) ||
                                    _currentState.Equals("Stopped", StringComparison.OrdinalIgnoreCase))
                                {
                                    _currentState = "Available";
                                }

                                // Guard against legacy hardcoded ActiveEntities == 110 when no clients or match session exist; genuine matches (including 0-human bot matches) transition cleanly.
                                long activeMatchId = MatchId;
                                bool hasActiveSession = ConnectedClients > 0 || !ActiveClientSlots.IsEmpty || activeMatchId > 0;
                                uint effectiveActiveEntities = (record.ActiveEntities == 110u && !hasActiveSession) ? 0u : record.ActiveEntities;
                                if (effectiveActiveEntities > 0)
                                {
                                    sawPositiveActiveEntities = true;
                                }
                                if (effectiveActiveEntities > 0 && !_currentState.Equals("InMatch", StringComparison.OrdinalIgnoreCase))
                                {
                                    _currentState = "InMatch";
                                    logger?.Invoke($"[DedicatedServer:{Port}] Worker state -> InMatch (ActiveEntities={effectiveActiveEntities}, Clients={ConnectedClients}, MatchId={activeMatchId}).");
                                }
                                else if (effectiveActiveEntities == 0 && sawPositiveActiveEntities && !hasActiveSession && _currentState.Equals("InMatch", StringComparison.OrdinalIgnoreCase))
                                {
                                    _currentState = "Available";
                                    logger?.Invoke($"[DedicatedServer:{Port}] Worker state -> Available (ActiveEntities={effectiveActiveEntities}, Clients={ConnectedClients}, MatchId={activeMatchId}).");
                                }
                            }

                            onFrameRecord?.Invoke(this, record);
                        }
                    }

                    // Poll every 10ms (~100 Hz sampling for tick rate)
                    await Task.Delay(10, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"[DedicatedServer:{Port}] Telemetry poller error: {ex.Message}");
                    try
                    {
                        await Task.Delay(100, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }, ct);
    }

    /// <summary>
    /// Stops the shared memory telemetry consumer and disposes memory mapped resources.
    /// </summary>
    public void StopTelemetryPolling()
    {
        try
        {
            _telemetryCts?.Cancel();
            _telemetryCts?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DedicatedServer:{Port}] StopTelemetryPolling CTS dispose error: {ex.Message}");
        }
        _telemetryCts = null;

        try
        {
            TelemetryConsumer?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DedicatedServer:{Port}] StopTelemetryPolling consumer dispose error: {ex.Message}");
        }
        TelemetryConsumer = null;
        _telemetryTask = null;
    }

    /// <summary>
    /// Clears the in-memory rolling log buffer for this worker.
    /// </summary>
    public void ClearLogs() => ClearLogHistory();

    /// <summary>
    /// Cleanly stops this dedicated server instance.
    /// Attempts graceful shutdown via 'quit' command over stdin first,
    /// then forces termination if process does not exit within timeout.
    /// </summary>
    public void Stop(TimeSpan gracefulTimeout, bool addLogLine = true, bool setDesiredRunningFalse = false)
    {
        if (setDesiredRunningFalse)
        {
            DesiredRunning = false;
        }
        _currentState = "Stopped";
        if (addLogLine)
        {
            AddLogLine($"Server on Port {Port} stopped.", false, "system");
        }
        StopTelemetryPolling();
        _pipeServerCts?.Cancel();
        _pipeServerCts?.Dispose();
        _pipeServerCts = null;

        StreamCts?.Cancel();
        StreamChannel?.Writer.TryComplete();

        try
        {
            int waitMs = Math.Max(2000, (int)gracefulTimeout.TotalMilliseconds);
#pragma warning disable RS0030 // Justification: Synchronous process teardown bounded wait for stream consumer task
            StreamConsumerTask?.Wait(TimeSpan.FromMilliseconds(waitMs));
#pragma warning restore RS0030
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DedicatedServer:{Port}] StreamConsumerTask wait ended with exception: {ex.Message}");
        }
        StreamConsumerTask = null;
        StreamChannel = null;

        Process? proc = Process;
        if (proc == null)
        {
            StreamCts?.Dispose();
            StreamCts = null;
            return;
        }

        try
        {
            if (!proc.HasExited)
            {
                // Attempt graceful console 'quit' command over redirected stdin
                try
                {
                    if (_stdinLock.Wait(TimeSpan.FromMilliseconds(500)))
                    {
                        try
                        {
                            StdinWriter?.WriteLine("quit");
                            StdinWriter?.Flush();
                        }
                        finally
                        {
                            _stdinLock.Release();
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DedicatedServer:{Port}] Graceful 'quit' write over stdin failed: {ex.Message}");
                }

                if (!proc.WaitForExit((int)gracefulTimeout.TotalMilliseconds))
                {
                    proc.Kill(true);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DedicatedServer:{Port}] Process shutdown error: {ex.Message}");
        }
        finally
        {
            if (_stdinLock.Wait(TimeSpan.FromSeconds(1)))
            {
                try
                {
                    StdinWriter?.Dispose();
                    StdinWriter = null;
                }
                finally
                {
                    _stdinLock.Release();
                }
            }
            else
            {
                StdinWriter?.Dispose();
                StdinWriter = null;
            }

            StreamCts?.Dispose();
            StreamCts = null;
            try
            {
                proc.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DedicatedServer:{Port}] Process dispose error: {ex.Message}");
            }
            Process = null;
        }
    }
}

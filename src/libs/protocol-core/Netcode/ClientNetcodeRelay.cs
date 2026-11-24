// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ClientNetcodeRelay.cs
// System: Kongor Core Netcode - Transparent Client-Side UDP Decompression Relay
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Netcode.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ASPIRE.Common.Netcode;

/// <summary>
///     Statistics and performance telemetry for the client-side netcode relay.
/// </summary>
public sealed record ClientRelayStats(
    long TotalPacketsReceived,
    long PacketsDecompressed,
    long TotalBytesReceived,
    long TotalBytesDecompressed,
    long DecompressionErrors,
    bool IsBypassed = false,
    long HandshakeTimeouts = 0);

/// <summary>
///     High-performance, asynchronous client-side loopback UDP relay.
///     Sits between the local game client (hon_x64.exe) and the remote COMPEL match server proxy.
///     Transparently decompresses incoming RFC 8878 Zstandard entity snapshot bursts in under 40 microseconds,
///     completely preventing IP packet fragmentation without requiring modifications to the game client binary.
/// </summary>
/// <summary>
///     High-performance client-side loopback UDP relay with <see cref="IOptionsMonitor{NetcodeRelayOptions}"/> dynamic rollout
///     and 500ms handshake auto-bypass. Exposes <see cref="EffectiveClientConnectEndPoint"/> for launcher integration when transparent
///     RFC 8878 Zstandard decompression is enabled for retail clients.
/// </summary>
// shortcut: matchmade games receive the dedicated server endpoint directly from chat-server via NET_CHAT_CL_TMM_MATCH_FOUND, so WILLOWMAKER launcher manages ClientNetcodeRelay exclusively for DirectPlay/custom hosted connections; upgrade to client-side proxy injection when native netcode hook lands.
public sealed class ClientNetcodeRelay : IAsyncDisposable, IDisposable
{
    private const int MaxDatagramSize = 65535;

    private readonly Socket _localClientSocket;
    private readonly Socket _remoteServerSocket;
    private readonly IPEndPoint _remoteServerEndPoint;
    private readonly CancellationTokenSource _cts = new();

    private readonly IOptionsMonitor<NetcodeRelayOptions>? _optionsMonitor;
    private readonly IDisposable? _optionsChangeSubscription;
    private readonly ILogger<ClientNetcodeRelay>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _effectiveHandshakeTimeout;

    private int _firstPacketSentFlag;
    private DateTimeOffset? _firstClientPacketSentUtc;
    private CancellationTokenSource? _handshakeTimeoutCts;
    private Task? _handshakeMonitorTask;
    private long _handshakeTimeouts;

    private int _bypassState; // 0 = active, 1 = dispatching OnAutoBypassToDirectUdp, 2 = bypass completed
    private volatile string? _bypassReason;

    private IPEndPoint? _activeClientEndPoint;
    private long _totalPacketsReceived;
    private long _packetsDecompressed;
    private long _totalBytesReceived;
    private long _totalBytesDecompressed;
    private long _decompressionErrors;

    private Task? _clientToServerPumpTask;
    private Task? _serverToClientPumpTask;
    private bool _isDisposed;
    private readonly bool _enableFec;
    private readonly XorFecEncoder? _fecEncoder;

    /// <summary>
    ///     Fires when the relay automatically trips to direct UDP bypass due to server handshake timeout
    ///     or runtime configuration kill-switch.
    /// </summary>
    public event Action<IPEndPoint, string>? OnAutoBypassToDirectUdp;

    public int LocalPort => ((IPEndPoint)_localClientSocket.LocalEndPoint!).Port;

    public bool IsBypassed => Volatile.Read(ref _bypassState) == 2;
    public string? BypassReason => _bypassReason;
    public DateTimeOffset? FirstClientPacketSentUtc => _firstClientPacketSentUtc;
    public IPEndPoint RemoteServerEndPoint => _remoteServerEndPoint;
    public IPEndPoint RemoteSocketLocalEndPoint => (IPEndPoint)_remoteServerSocket.LocalEndPoint!;

    /// <summary>
    ///     Returns the effective connect endpoint that the game client should target.
    ///     Returns the remote server endpoint directly when bypassed or disabled via options,
    ///     or the local loopback relay endpoint when the relay is actively running.
    /// </summary>
    public IPEndPoint EffectiveClientConnectEndPoint
    {
        get
        {
            if (Volatile.Read(ref _bypassState) != 0 || (_optionsMonitor != null && !_optionsMonitor.CurrentValue.RelayEnabled))
            {
                return _remoteServerEndPoint;
            }

            return new IPEndPoint(IPAddress.Loopback, LocalPort);
        }
    }

    public ClientRelayStats Stats => new(
        Volatile.Read(ref _totalPacketsReceived),
        Volatile.Read(ref _packetsDecompressed),
        Volatile.Read(ref _totalBytesReceived),
        Volatile.Read(ref _totalBytesDecompressed),
        Volatile.Read(ref _decompressionErrors),
        Volatile.Read(ref _bypassState) != 0,
        Volatile.Read(ref _handshakeTimeouts));

    public ClientNetcodeRelay(
        IPEndPoint remoteServerEndPoint,
        int localListenPort = 0,
        bool enableFec = false)
        : this(remoteServerEndPoint, localListenPort, enableFec, null, null, null, null)
    {
    }

    public ClientNetcodeRelay(
        IPEndPoint remoteServerEndPoint,
        IOptionsMonitor<NetcodeRelayOptions>? optionsMonitor,
        int localListenPort = 0,
        TimeSpan? handshakeTimeout = null,
        ILogger<ClientNetcodeRelay>? logger = null,
        TimeProvider? timeProvider = null)
        : this(
            remoteServerEndPoint,
            localListenPort,
            optionsMonitor?.CurrentValue.EnableFec ?? false,
            optionsMonitor,
            handshakeTimeout,
            logger,
            timeProvider)
    {
    }

    public ClientNetcodeRelay(
        IPEndPoint remoteServerEndPoint,
        int localListenPort,
        bool enableFec,
        IOptionsMonitor<NetcodeRelayOptions>? optionsMonitor,
        TimeSpan? handshakeTimeout = null,
        ILogger<ClientNetcodeRelay>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _remoteServerEndPoint = remoteServerEndPoint ?? throw new ArgumentNullException(nameof(remoteServerEndPoint));
        _optionsMonitor = optionsMonitor;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _enableFec = enableFec;

        if (handshakeTimeout.HasValue && handshakeTimeout.Value > TimeSpan.Zero)
        {
            _effectiveHandshakeTimeout = handshakeTimeout.Value;
        }
        else if (_optionsMonitor != null && _optionsMonitor.CurrentValue.HandshakeTimeoutMs > 0)
        {
            _effectiveHandshakeTimeout = TimeSpan.FromMilliseconds(_optionsMonitor.CurrentValue.HandshakeTimeoutMs);
        }
        else
        {
            _effectiveHandshakeTimeout = TimeSpan.FromMilliseconds(500);
        }

        if (enableFec)
        {
            _fecEncoder = new XorFecEncoder();
        }

        _localClientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 1024 * 1024,
            SendBufferSize = 1024 * 1024
        };
        _localClientSocket.ConfigureForFastRebind();
        DisableConnectionResetReporting(_localClientSocket);
        _localClientSocket.Bind(new IPEndPoint(IPAddress.Loopback, localListenPort));

        IPAddress remoteBindAddress;
        if (IPAddress.IsLoopback(_remoteServerEndPoint.Address))
        {
            remoteBindAddress = _remoteServerEndPoint.AddressFamily == AddressFamily.InterNetworkV6
                ? IPAddress.IPv6Loopback
                : IPAddress.Loopback;
        }
        else
        {
            remoteBindAddress = _remoteServerEndPoint.AddressFamily == AddressFamily.InterNetworkV6
                ? IPAddress.IPv6Any
                : IPAddress.Any;
        }

        _remoteServerSocket = new Socket(_remoteServerEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 1024 * 1024,
            SendBufferSize = 1024 * 1024
        };
        _remoteServerSocket.ConfigureForFastRebind();
        DisableConnectionResetReporting(_remoteServerSocket);
        _remoteServerSocket.Bind(new IPEndPoint(remoteBindAddress, 0));

        if (_optionsMonitor != null && !_optionsMonitor.CurrentValue.RelayEnabled)
        {
            Volatile.Write(ref _bypassState, 2);
            _bypassReason = "DisabledViaOptionsMonitor";
            _logger?.LogInformation(
                "[ClientNetcodeRelay] Relay starts disabled via options for remote {RemoteEndPoint}.",
                _remoteServerEndPoint);
        }

        _optionsChangeSubscription = _optionsMonitor?.OnChange((NetcodeRelayOptions newOptions, string? _) =>
        {
            if (!newOptions.RelayEnabled && Volatile.Read(ref _bypassState) == 0)
            {
                TriggerBypass("DisabledViaOptionsMonitor");
            }
        });
    }

    private void TriggerBypass(string reason)
    {
        if (Interlocked.CompareExchange(ref _bypassState, 1, 0) != 0) return;
        try
        {
            _bypassReason = reason;
            _logger?.LogWarning(
                "[ClientNetcodeRelay] Bypassing relay to direct UDP for remote {RemoteEndPoint}. Reason: {Reason}",
                _remoteServerEndPoint, reason);
            OnAutoBypassToDirectUdp?.Invoke(_remoteServerEndPoint, reason);
        }
        finally
        {
            Volatile.Write(ref _bypassState, 2);
        }
    }

    /// <summary>
    ///     Starts the bidirectional relay pumps.
    /// </summary>
    public void Start()
    {
        _clientToServerPumpTask = Task.Run(PumpClientToServerAsync);
        _serverToClientPumpTask = Task.Run(PumpServerToClientAsync);
    }

    private async Task MonitorHandshakeTimeoutAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_effectiveHandshakeTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
            if (!_isDisposed && Volatile.Read(ref _totalPacketsReceived) == 0 && Volatile.Read(ref _bypassState) == 0)
            {
                Interlocked.Increment(ref _handshakeTimeouts);
                TriggerBypass("ServerHandshakeTimeout");
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled because server packet arrived or relay disposed/bypassed
        }
    }

    private async Task PumpClientToServerAsync()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxDatagramSize);
        EndPoint anyEndPoint = new IPEndPoint(IPAddress.Loopback, 0);

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await _localClientSocket.ReceiveFromAsync(
                        buffer, SocketFlags.None, anyEndPoint, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                if (result.ReceivedBytes <= 0) continue;

                // Cache the client's endpoint so incoming server traffic knows where to return
                _activeClientEndPoint = (IPEndPoint)result.RemoteEndPoint;

                if (Interlocked.CompareExchange(ref _firstPacketSentFlag, 1, 0) == 0)
                {
                    _firstClientPacketSentUtc = _timeProvider.GetUtcNow();
                    _handshakeTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    CancellationToken timeoutToken = _handshakeTimeoutCts.Token;
                    _handshakeMonitorTask = Task.Run(() => MonitorHandshakeTimeoutAsync(timeoutToken), CancellationToken.None);
                }

                // Client-to-server commands are relayed to remote match server (with optional FEC encoding)
                if (Volatile.Read(ref _bypassState) == 0 && _enableFec && _fecEncoder != null && result.ReceivedBytes <= XorFecEncoder.MaxDatagramPayloadSize)
                {
                    byte[] fecBuffer = ArrayPool<byte>.Shared.Rent(MaxDatagramSize);
                    try
                    {
                        if (_fecEncoder.TryEncodeData(buffer.AsSpan(0, result.ReceivedBytes), fecBuffer, out int encodedWritten, out bool isFull))
                        {
                            await _remoteServerSocket.SendToAsync(
                                fecBuffer.AsMemory(0, encodedWritten),
                                SocketFlags.None,
                                _remoteServerEndPoint,
                                _cts.Token).ConfigureAwait(false);

                            if (isFull)
                            {
                                if (_fecEncoder.TryEmitParity(fecBuffer, out int parityWritten))
                                {
                                    await _remoteServerSocket.SendToAsync(
                                        fecBuffer.AsMemory(0, parityWritten),
                                        SocketFlags.None,
                                        _remoteServerEndPoint,
                                        _cts.Token).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(fecBuffer);
                    }
                }
                else
                {
                    await _remoteServerSocket.SendToAsync(
                        buffer.AsMemory(0, result.ReceivedBytes),
                        SocketFlags.None,
                        _remoteServerEndPoint,
                        _cts.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task PumpServerToClientAsync()
    {
        byte[] serverBuffer = ArrayPool<byte>.Shared.Rent(MaxDatagramSize);
        byte[] decompressedBuffer = ArrayPool<byte>.Shared.Rent(MaxDatagramSize);
        EndPoint remoteAny = _remoteServerEndPoint.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await _remoteServerSocket.ReceiveFromAsync(
                        serverBuffer, SocketFlags.None, remoteAny, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                int receivedBytes = result.ReceivedBytes;
                if (receivedBytes <= 0) continue;

                Interlocked.Increment(ref _totalPacketsReceived);
                Interlocked.Add(ref _totalBytesReceived, receivedBytes);

                CancellationTokenSource? handshakeCts = Interlocked.Exchange(ref _handshakeTimeoutCts, null);
                if (handshakeCts != null)
                {
                    handshakeCts.Cancel();
                    handshakeCts.Dispose();
                }

                ReadOnlySpan<byte> packetSpan = serverBuffer.AsSpan(0, receivedBytes);
                ReadOnlyMemory<byte> payloadToSend;

                if (Volatile.Read(ref _bypassState) == 0 && ZstdDeltaCompressor.IsDeltaCompressed(packetSpan))
                {
                    if (ZstdDeltaCompressor.TryDecompressDelta(packetSpan, decompressedBuffer, out int written))
                    {
                        Interlocked.Increment(ref _packetsDecompressed);
                        Interlocked.Add(ref _totalBytesDecompressed, written);
                        payloadToSend = decompressedBuffer.AsMemory(0, written);
                    }
                    else
                    {
                        Interlocked.Increment(ref _decompressionErrors);
                        // Forward original buffer as fallback
                        payloadToSend = serverBuffer.AsMemory(0, receivedBytes);
                    }
                }
                else
                {
                    payloadToSend = serverBuffer.AsMemory(0, receivedBytes);
                }

                IPEndPoint? clientTarget = _activeClientEndPoint;
                if (clientTarget != null)
                {
                    await _localClientSocket.SendToAsync(
                        payloadToSend,
                        SocketFlags.None,
                        clientTarget,
                        _cts.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(serverBuffer);
            ArrayPool<byte>.Shared.Return(decompressedBuffer);
        }
    }

    private static void DisableConnectionResetReporting(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;

        const int windowsUDPConnectionResetControlCode = unchecked((int)0x9800000C);
        socket.IOControl(windowsUDPConnectionResetControlCode, [0x00, 0x00, 0x00, 0x00], null);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts.Cancel();
        _optionsChangeSubscription?.Dispose();
        CancellationTokenSource? handshakeCts = Interlocked.Exchange(ref _handshakeTimeoutCts, null);
        if (handshakeCts != null)
        {
            handshakeCts.Cancel();
            handshakeCts.Dispose();
        }

        _localClientSocket.Dispose();
        _remoteServerSocket.Dispose();
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await _cts.CancelAsync().ConfigureAwait(false);
        _optionsChangeSubscription?.Dispose();
        CancellationTokenSource? handshakeCts = Interlocked.Exchange(ref _handshakeTimeoutCts, null);
        if (handshakeCts != null)
        {
            await handshakeCts.CancelAsync().ConfigureAwait(false);
            handshakeCts.Dispose();
        }

        _localClientSocket.Dispose();
        _remoteServerSocket.Dispose();

        if (_clientToServerPumpTask != null && _serverToClientPumpTask != null)
        {
            try
            {
                await Task.WhenAll(_clientToServerPumpTask, _serverToClientPumpTask).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                _logger?.LogDebug(ex, "[ClientNetcodeRelay] Pump tasks stopped during DisposeAsync.");
            }
        }

        if (_handshakeMonitorTask != null)
        {
            try
            {
                await _handshakeMonitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _logger?.LogDebug(ex, "[ClientNetcodeRelay] Handshake monitor cancelled during DisposeAsync.");
            }
        }

        _cts.Dispose();
    }
}

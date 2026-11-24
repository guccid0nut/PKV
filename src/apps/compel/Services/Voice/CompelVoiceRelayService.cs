// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CompelVoiceRelayService.cs
// System: COMPEL - Fleet Orchestrator & Opus Voice Datagram Relay
// Policy: Strict Explicit Typing (NO 'var') - High-Throughput Non-Blocking UDP Switching
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Hosting;
using ASPIRE.Common.Netcode.Sockets;
using COMPEL.Services.Proxy;
using COMPEL.Services.Supervision;
using Kongor.Core.Netcode.Voice;
using Kongor.Core.Services.Networking;
using Kongor.Core.Services.Voice;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace COMPEL.Services.Voice;

/// <summary>
///     Participant registry entry for an active client endpoint.
/// </summary>
public sealed class VoiceParticipant
{
    public uint AccountId { get; }
    public IPEndPoint EndPoint { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public int PacketCountInCurrentWindow { get; set; }
    public long WindowStartTicks { get; set; }
    public ReplySequenceReassembler<VoiceDatagramPacket> SequenceReassembler { get; } = new(0UL);

    public VoiceParticipant(uint accountId, IPEndPoint endPoint, DateTimeOffset lastSeen)
    {
        AccountId = accountId;
        EndPoint = endPoint;
        LastSeen = lastSeen;
        PacketCountInCurrentWindow = 0;
        WindowStartTicks = Environment.TickCount64;
    }
}

/// <summary>
///     Composite key identifying a specific routing group.
/// </summary>
public readonly record struct VoiceChannelKey(VoiceChannelType ChannelType, uint ChannelId);

public interface ICompelVoiceRelayService
{
    int Port { get; }
    bool IsRunning { get; }
    long TotalPacketsReceived { get; }
    long TotalPacketsRelayed { get; }
    long TotalPacketsDropped { get; }
    long TotalPacketsMuted { get; }
    int ActiveChannelsCount { get; }
    int ActiveParticipantsCount { get; }
    IVoiceMuteRegistry? MuteRegistry { get; }

    void RegisterParticipant(VoiceChannelType channelType, uint channelId, uint accountId, IPEndPoint endPoint);
    void UnregisterParticipant(VoiceChannelType channelType, uint channelId, uint accountId);
    int RoutePacket(VoiceDatagramPacket packet, IPEndPoint senderEndPoint);
}

/// <summary>
///     High-performance asynchronous UDP voice relay server for ProjectKongor.
///     Listens on port 11600 (PortPlan.BaseVoiceRelayPort) and switches Opus voice datagrams between pre-registered
///     participants sharing the same party, team, or lane channel.
/// </summary>
public sealed class CompelVoiceRelayService : ResilientBackgroundService, ICompelVoiceRelayService
{
    public const int DefaultVoicePort = PortPlan.BaseVoiceRelayPort;
    public const int MaxPacketsPerSecondPerClient = 60; // 50 pps nominal for 20ms frames + headroom
    public const int MaxParticipantsPerChannel = 32;
    public const int MaxRoomBindingsPerPublicIp = 16;
    public static readonly TimeSpan StaleParticipantTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<CompelVoiceRelayService> _logger;
    private readonly ConcurrentDictionary<VoiceChannelKey, ConcurrentDictionary<uint, VoiceParticipant>> _channels = new();
    private readonly TimeProvider _timeProvider;
    private readonly IVoiceMuteRegistry? _muteRegistry;
    private readonly UdpRateLimiter _ipRateLimiter;
    private Socket? _socket;
    private long _totalPacketsReceived;
    private long _totalPacketsRelayed;
    private long _totalPacketsDropped;
    private long _totalPacketsMuted;

    public int Port { get; }
    public bool IsRunning => Volatile.Read(ref _socket) != null;
    public long TotalPacketsReceived => Interlocked.Read(ref _totalPacketsReceived);
    public long TotalPacketsRelayed => Interlocked.Read(ref _totalPacketsRelayed);
    public long TotalPacketsDropped => Interlocked.Read(ref _totalPacketsDropped);
    public long TotalPacketsMuted => Interlocked.Read(ref _totalPacketsMuted);
    public IVoiceMuteRegistry? MuteRegistry => _muteRegistry;

    public int ActiveChannelsCount => _channels.Count;

    public int ActiveParticipantsCount
    {
        get
        {
            int total = 0;
            foreach (KeyValuePair<VoiceChannelKey, ConcurrentDictionary<uint, VoiceParticipant>> pair in _channels)
            {
                total += pair.Value.Count;
            }
            return total;
        }
    }

    public CompelVoiceRelayService(
        ILogger<CompelVoiceRelayService> logger,
        int port = DefaultVoicePort,
        TimeProvider? timeProvider = null,
        IVoiceMuteRegistry? muteRegistry = null)
        : base(logger, nameof(CompelVoiceRelayService), timeProvider: timeProvider)
    {
        _logger = logger;
        Port = port;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _muteRegistry = muteRegistry;
        _ipRateLimiter = new UdpRateLimiter(
            refillRatePerSecond: 180.0,
            burstCapacity: 240.0);
    }

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[VoiceRelay] Starting COMPEL Opus UDP Voice Relay on port {Port}...", Port);

        Socket? boundSocket = null;
        try
        {
            boundSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            boundSocket.ConfigureForFastRebind();
            if (OperatingSystem.IsWindows())
            {
                const int sioUdpConnReset = -1744830452;
                boundSocket.IOControl((IOControlCode)sioUdpConnReset, [0, 0, 0, 0], null);
            }
            boundSocket.Bind(new IPEndPoint(IPAddress.Any, Port));
            Volatile.Write(ref _socket, boundSocket);
        }
        catch (Exception ex)
        {
            boundSocket?.Dispose();
            _logger.LogError(ex, "[VoiceRelay] Failed to bind UDP Voice Relay socket on port {Port}.", Port);
            return;
        }

        byte[] receiveBuffer = new byte[2048];
        EndPoint senderRemote = new IPEndPoint(IPAddress.Any, 0);

        // Background maintenance task for pruning stale endpoints
        Task pruningTask = Task.Run(() => PruningLoopAsync(stoppingToken), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                SocketReceiveFromResult result = await _socket.ReceiveFromAsync(
                    receiveBuffer.AsMemory(),
                    SocketFlags.None,
                    senderRemote,
                    stoppingToken);

                Interlocked.Increment(ref _totalPacketsReceived);
                IPEndPoint clientEndPoint = (IPEndPoint)result.RemoteEndPoint;

                // Per-IP ingress packet rate limit before parsing/routing to block UDP flood amplification
                if (!_ipRateLimiter.ShouldAllow(clientEndPoint.Address))
                {
                    Interlocked.Increment(ref _totalPacketsDropped);
                    continue;
                }

                ReadOnlyMemory<byte> rawDatagram = receiveBuffer.AsMemory(0, result.ReceivedBytes);
                if (!VoiceDatagramPacket.TryParse(rawDatagram, out VoiceDatagramPacket packet))
                {
                    Interlocked.Increment(ref _totalPacketsDropped);
                    continue;
                }

                // Zero-Bypass Mute Enforcement: unconditionally drop packets from muted accounts
                if (_muteRegistry != null && _muteRegistry.IsMuted(packet.SenderAccountId))
                {
                    Interlocked.Increment(ref _totalPacketsDropped);
                    Interlocked.Increment(ref _totalPacketsMuted);
                    continue;
                }

                // Per-participant rate limiting check
                if (IsRateLimited(packet.ChannelType, packet.ChannelId, packet.SenderAccountId, clientEndPoint))
                {
                    Interlocked.Increment(ref _totalPacketsDropped);
                    continue;
                }

                // Zero-Trust Room Admission: NEVER auto-register anonymous UDP callers on 11600.
                // Only control-plane pre-registered participants matching their bound endpoint may relay audio.
                int relayed = RoutePacket(packet, clientEndPoint);
                Interlocked.Add(ref _totalPacketsRelayed, relayed);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "[VoiceRelay] Error processing voice datagram.");
                }
            }
        }

        try
        {
            await pruningTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }

        _logger.LogInformation("[VoiceRelay] COMPEL Opus Voice Relay on port {Port} stopped.", Port);
    }

    public void RegisterParticipant(VoiceChannelType channelType, uint channelId, uint accountId, IPEndPoint endPoint)
    {
        VoiceChannelKey key = new VoiceChannelKey(channelType, channelId);
        ConcurrentDictionary<uint, VoiceParticipant> participants = _channels.GetOrAdd(
            key,
            static _ => new ConcurrentDictionary<uint, VoiceParticipant>());

        if (!participants.ContainsKey(accountId))
        {
            if (participants.Count >= MaxParticipantsPerChannel)
            {
                _logger.LogWarning(
                    "[SECURITY-ALERT] [VoiceRelay] Rejected participant {AccountId} registration in channel {ChannelType}:{ChannelId}: channel cap ({Cap}) reached.",
                    accountId,
                    channelType,
                    channelId,
                    MaxParticipantsPerChannel);
                return;
            }

            if (!IPAddress.IsLoopback(endPoint.Address) && CountActiveBindingsForIp(endPoint.Address) >= MaxRoomBindingsPerPublicIp)
            {
                _logger.LogWarning(
                    "[SECURITY-ALERT] [VoiceRelay] Rejected participant {AccountId} registration from {Address}: per-IP room cap ({Cap}) reached.",
                    accountId,
                    endPoint.Address,
                    MaxRoomBindingsPerPublicIp);
                return;
            }
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        participants.AddOrUpdate(
            accountId,
            _ => new VoiceParticipant(accountId, endPoint, now),
            (_, existing) =>
            {
                existing.EndPoint = endPoint;
                existing.LastSeen = now;
                return existing;
            });
    }

    private int CountActiveBindingsForIp(IPAddress address)
    {
        int count = 0;
        foreach (KeyValuePair<VoiceChannelKey, ConcurrentDictionary<uint, VoiceParticipant>> channelPair in _channels)
        {
            foreach (KeyValuePair<uint, VoiceParticipant> participantPair in channelPair.Value)
            {
                if (participantPair.Value.EndPoint.Address.Equals(address))
                {
                    count++;
                }
            }
        }
        return count;
    }

    public void UnregisterParticipant(VoiceChannelType channelType, uint channelId, uint accountId)
    {
        VoiceChannelKey key = new VoiceChannelKey(channelType, channelId);
        if (_channels.TryGetValue(key, out ConcurrentDictionary<uint, VoiceParticipant>? participants))
        {
            participants.TryRemove(accountId, out _);
            if (participants.IsEmpty)
            {
                _channels.TryRemove(key, out _);
            }
        }
    }

    public int RoutePacket(VoiceDatagramPacket packet, IPEndPoint senderEndPoint)
    {
        // Zero-Bypass Mute Enforcement
        if (_muteRegistry != null && _muteRegistry.IsMuted(packet.SenderAccountId))
        {
            Interlocked.Increment(ref _totalPacketsDropped);
            Interlocked.Increment(ref _totalPacketsMuted);
            return 0;
        }

        VoiceChannelKey key = new VoiceChannelKey(packet.ChannelType, packet.ChannelId);
        if (!_channels.TryGetValue(key, out ConcurrentDictionary<uint, VoiceParticipant>? participants) ||
            !participants.TryGetValue(packet.SenderAccountId, out VoiceParticipant? senderParticipant))
        {
            Interlocked.Increment(ref _totalPacketsDropped);
            _logger.LogDebug(
                "[SECURITY-ALERT] [VoiceRelay] Dropped voice packet from unregistered AccountId {AccountId} ({EndPoint}) for room {ChannelType}:{ChannelId}",
                packet.SenderAccountId,
                senderEndPoint,
                packet.ChannelType,
                packet.ChannelId);
            return 0;
        }

        // Enforce strict endpoint binding so an attacker cannot spoof a registered player's SenderAccountId
        if (senderParticipant.EndPoint.Port == 0 && senderParticipant.EndPoint.Address.Equals(senderEndPoint.Address))
        {
            senderParticipant.EndPoint = senderEndPoint;
        }
        else if (!senderParticipant.EndPoint.Equals(senderEndPoint))
        {
            Interlocked.Increment(ref _totalPacketsDropped);
            _logger.LogWarning(
                "[SECURITY-ALERT] [VoiceRelay] Blocked SenderAccountId {AccountId} spoofing from {SenderEndPoint} (bound endpoint: {BoundEndPoint}) in room {ChannelType}:{ChannelId}",
                packet.SenderAccountId,
                senderEndPoint,
                senderParticipant.EndPoint,
                packet.ChannelType,
                packet.ChannelId);
            return 0;
        }

        senderParticipant.LastSeen = _timeProvider.GetUtcNow();

        byte[] serializedBytes = packet.ToByteArray();
        int relayedCount = 0;

        foreach (KeyValuePair<uint, VoiceParticipant> kvp in participants)
        {
            VoiceParticipant participant = kvp.Value;
            if (participant.AccountId == packet.SenderAccountId && participant.EndPoint.Equals(senderEndPoint))
            {
                continue; // Do not echo back to sender
            }

            // Zero-Bypass Ignore Enforcement: Drop datagram for any participant who has ignored sender
            if (_muteRegistry != null && _muteRegistry.IsParticipantIgnored(participant.AccountId, packet.SenderAccountId))
            {
                continue;
            }

            if (_socket != null)
            {
                try
                {
                    _socket.SendTo(serializedBytes, participant.EndPoint);
                    relayedCount++;
                }
                catch
                {
                    // Ignore transient network send failures to dead endpoints; pruning will clean them up
                }
            }
        }

        return relayedCount;
    }

    private bool IsRateLimited(VoiceChannelType channelType, uint channelId, uint accountId, IPEndPoint endPoint)
    {
        VoiceChannelKey key = new VoiceChannelKey(channelType, channelId);
        if (!_channels.TryGetValue(key, out ConcurrentDictionary<uint, VoiceParticipant>? participants) ||
            !participants.TryGetValue(accountId, out VoiceParticipant? participant))
        {
            return false;
        }

        long nowTicks = Environment.TickCount64;
        long elapsed = nowTicks - participant.WindowStartTicks;

        if (elapsed >= 1000)
        {
            participant.WindowStartTicks = nowTicks;
            participant.PacketCountInCurrentWindow = 1;
            return false;
        }

        participant.PacketCountInCurrentWindow++;
        return participant.PacketCountInCurrentWindow > MaxPacketsPerSecondPerClient;
    }

    private async Task PruningLoopAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _timeProvider);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            DateTimeOffset cutoff = _timeProvider.GetUtcNow().Subtract(StaleParticipantTimeout);

            foreach (KeyValuePair<VoiceChannelKey, ConcurrentDictionary<uint, VoiceParticipant>> channelPair in _channels)
            {
                ConcurrentDictionary<uint, VoiceParticipant> participants = channelPair.Value;
                foreach (KeyValuePair<uint, VoiceParticipant> participantPair in participants)
                {
                    if (participantPair.Value.LastSeen < cutoff)
                    {
                        participants.TryRemove(participantPair.Key, out _);
                    }
                }

                if (participants.IsEmpty)
                {
                    _channels.TryRemove(channelPair.Key, out _);
                }
            }
        }
    }

    public IReadOnlyList<VoiceDatagramPacket> ReassembleParticipantSequence(
        VoiceChannelType channelType,
        uint channelId,
        uint accountId,
        ulong sequenceNumber,
        VoiceDatagramPacket packet)
    {
        VoiceChannelKey key = new VoiceChannelKey(channelType, channelId);
        if (!_channels.TryGetValue(key, out ConcurrentDictionary<uint, VoiceParticipant>? participants) ||
            !participants.TryGetValue(accountId, out VoiceParticipant? participant))
        {
            return [packet];
        }

        if (!participant.SequenceReassembler.Insert(sequenceNumber, packet))
        {
            return Array.Empty<VoiceDatagramPacket>();
        }

        List<VoiceDatagramPacket> drained = [];
        while (participant.SequenceReassembler.ReadyToPop())
        {
            (ulong _, VoiceDatagramPacket readyPacket) = participant.SequenceReassembler.Pop();
            drained.Add(readyPacket);
        }

        return drained;
    }

    public override void Dispose()
    {
        Interlocked.Exchange(ref _socket, null)?.Dispose();
        base.Dispose();
    }
}

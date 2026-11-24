// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ASPIRE.Common.Netcode;
using ASPIRE.Common.Netcode.Sockets;
using COMPEL.Services.Proxy.Congestion;
using COMPEL.Services.Security;
using Microsoft.Extensions.Logging;

namespace COMPEL.Services.Proxy;

/// <summary>
///     A bidirectional UDP relay for a single public port. It validates each client datagram's length, challenge and counter,
///     scores abusive sources and drops them, and relays only what passes, while the server's replies are relayed back unexamined.
///     A dedicated upstream socket per client preserves the server's per-client addressing, mirroring how the native proxy
///     mapped each public port to its local server port.
///     Heroes Of Newerth clients throttle their own traffic on the public (20000-29999) port range until the proxy
///     authenticates them with a challenge, so the forwarder issues a challenge to each session on creation and renews it
///     periodically.
/// </summary>
internal sealed class UDPForwarder : IDisposable
{
    private const int DatagramBufferSize = 65535;

    // The Challenge Packet's Leading Watermark Bytes, Which The Client Skips Before Reading The Control Payload: WATERMARK_LEN_TOTAL (20) Plus ENHANCED_WATERMARK_LEN_TOTAL (20).
    private const int WatermarkPrefixLength = 40;

    // Identifies A Proxy Control Packet (PACKET_PROXY, Bit 6) And The Challenge Sub-Type Within It.
    private const byte ProxyPacketFlag = 0x40;
    private const byte ChallengePacketType = 0x00;

    // The Window (Seconds) The Client Treats Itself As Authenticated After A Challenge.
    private const ushort ChallengeExpirySeconds = 60;

    // No Reference #define To Cite: The Reference Bounds Its Own Maps With A Bare Literal Of A Thousand.
    // Reporting Stops At The Bound Rather Than Clearing, Because Clearing Would Un-Throttle Every Source Already Reported And Turn A Flood Into A Log Flood.
    private const int ReportedDropLimit = 1000;

    // MAX_GAME_CONNECTIONS / MAX_VOICE_CONNECTIONS
    private const int MaxSessionsPerForwarder = 24;

    // MAX_GAME_CONNECTIONS_PER_IP / MAX_VOICE_CONNECTIONS_PER_IP
    private const int MaxSessionsPerAddress = 10;

    // A Session That Has Never Had A Datagram Admitted May Belong To A Client Still Echoing A Challenge Issued To An Earlier Session,
    // Because The Client Keys Its Challenge On Our Public Port Rather Than Its Own Source Port.
    // The Grace Is Bounded Both Ways: It Ends At The First Admitted Datagram, And It Expires Regardless, So A Source That Never Authenticates Does Not Keep It.
    internal static readonly TimeSpan UnknownChallengeGrace = TimeSpan.FromSeconds(30);

    public const int MinCompressionThresholdBytes = 256;

    private readonly IPEndPoint serverEndPoint;
    private readonly ProxyForwarderKind kind;
    private readonly ViolationScoreContainer scoreContainer;
    private readonly AttackIndicatorContainer attackIndicator;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly Socket frontSocket;
    private readonly ConcurrentDictionary<IPEndPoint, ClientSession> sessions = new();
    private readonly ConcurrentDictionary<IPEndPoint, bool> reportedDrops = new();
    private readonly SessionChallengeState challenges = new();
    private readonly Lock sessionsLock = new();
    private readonly ushort packetQuota;
    private readonly ushort gameCommandQuota;
    private readonly bool enableZstdCompression;
    private readonly bool enableBbrPacing;
    private readonly bool enableSubTickNetcode;
    private readonly bool enableFec;
    private readonly bool enableAnomalyShield;
    private readonly UdpTokenBucketPacer? pacer;
    private readonly Func<IPEndPoint, byte[]>? sessionKeyResolver;
    private readonly string? serverName;
    private readonly string? serverLocation;
    private readonly bool enableLegacyWatermarkChallenge;
    private readonly byte[]? pongTemplate;
    private readonly UdpRateLimiter pingRateLimiter;
    private readonly Func<IPAddress, bool> isPublicAddress;
    private int nonPublicCapExemptionLogged;

    private long droppedDatagramCount;
    private int reportedDropCount;

    public int PublicPort { get; }

    public int LocalPort { get; }

    private volatile bool sessionsRetained;

    public int ActiveSessionCount => sessions.Count;

    public bool AreSessionsRetained => sessionsRetained;

    public void RetainSessions(bool retain)
    {
        sessionsRetained = retain;
    }

    public long DroppedDatagramCount => Volatile.Read(ref droppedDatagramCount);

    public UDPForwarder(
        int publicPort,
        int localPort,
        ProxyForwarderKind kind,
        TimeSpan challengeRenewalInterval,
        ViolationScoreContainer scoreContainer,
        AttackIndicatorContainer attackIndicator,
        TimeProvider timeProvider,
        ILogger logger,
        bool enableZstdCompression = true,
        bool enableBbrPacing = false,
        bool enableSubTickNetcode = false,
        bool enableFec = true,
        bool enableAnomalyShield = true,
        Func<IPEndPoint, byte[]>? sessionKeyResolver = null,
        string? serverName = null,
        string? serverLocation = null,
        bool enableLegacyWatermarkChallenge = true,
        Func<IPAddress, bool>? isPublicAddress = null)
    {
        PublicPort = publicPort;
        LocalPort = localPort;
        serverEndPoint = new IPEndPoint(IPAddress.Loopback, localPort);
        this.kind = kind;
        this.scoreContainer = scoreContainer;
        this.attackIndicator = attackIndicator;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.enableZstdCompression = enableZstdCompression;
        this.enableBbrPacing = enableBbrPacing;
        this.enableSubTickNetcode = enableSubTickNetcode;
        this.enableFec = enableFec;
        this.enableAnomalyShield = enableAnomalyShield;
        this.sessionKeyResolver = sessionKeyResolver;
        this.serverName = serverName;
        this.serverLocation = serverLocation;
        this.enableLegacyWatermarkChallenge = enableLegacyWatermarkChallenge;
        this.isPublicAddress = isPublicAddress ?? NetworkAddressClassification.IsPublicRoutable;
        this.pingRateLimiter = new UdpRateLimiter(refillRatePerSecond: 5.0, burstCapacity: 10.0);

        if (kind == ProxyForwarderKind.Game && !string.IsNullOrWhiteSpace(serverName))
        {
            pongTemplate = CreatePongTemplate(serverName, serverLocation ?? "USE");
        }

        if (enableBbrPacing)
        {
            pacer = new UdpTokenBucketPacer(
                bbr: new BbrCongestionController(timeProvider),
                timeProvider: timeProvider);
        }

        // The Interval Is Not Stored: It Is Only Needed To Derive The Two Quotas, Which Are Fixed For The Life Of The Forwarder
        packetQuota = ChallengeQuota.ForKind(kind, challengeRenewalInterval);
        gameCommandQuota = ChallengeQuota.GameCommandForInterval(challengeRenewalInterval);

        try
        {
            frontSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
            {
                DualMode = true,
                ReceiveBufferSize = 1024 * 1024,
                SendBufferSize = 1024 * 1024
            };
            frontSocket.ConfigureForFastRebind();
            DisableConnectionResetReporting(frontSocket);
            frontSocket.Bind(new IPEndPoint(IPAddress.IPv6Any, publicPort));
            logger.LogInformation("[UDPForwarder] Bound dual-stack IPv4/IPv6 listener on port {PublicPort}", publicPort);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "[UDPForwarder] IPv6 dual-stack socket unavailable on port {PublicPort}; falling back to IPv4", publicPort);
            frontSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveBufferSize = 1024 * 1024,
                SendBufferSize = 1024 * 1024
            };
            frontSocket.ConfigureForFastRebind();
            DisableConnectionResetReporting(frontSocket);
            frontSocket.Bind(new IPEndPoint(IPAddress.Any, publicPort));
        }
    }

    private EndPoint PrepareClientTarget(IPEndPoint client)
    {
        return frontSocket.AddressFamily == AddressFamily.InterNetworkV6 && client.AddressFamily == AddressFamily.InterNetwork
            ? new IPEndPoint(client.Address.MapToIPv6(), client.Port)
            : client;
    }

    public UDPForwarder(int publicPort, int localPort, ILogger logger, bool enableZstdCompression = true, bool enableFec = true, bool enableAnomalyShield = true)
        : this(
            publicPort,
            localPort,
            ProxyForwarderKind.Game,
            TimeSpan.FromSeconds(10),
            new ViolationScoreContainer(TimeProvider.System),
            new AttackIndicatorContainer(TimeProvider.System),
            TimeProvider.System,
            logger,
            enableZstdCompression,
            enableBbrPacing: false,
            enableSubTickNetcode: false,
            enableFec: enableFec,
            enableAnomalyShield: enableAnomalyShield)
    {
    }

    // On Windows A UDP Socket Reports A Received ICMP Port-Unreachable As A Connection-Reset Error On The Next Socket Operation.
    // Disabling It (SIO_UDP_CONNRESET) Stops A Momentarily-Down Server From Killing The Relay With Spurious Exceptions.
    // The Control Code Does Not Exist On Other Platforms.
    private static void DisableConnectionResetReporting(Socket socket)
    {
        if (OperatingSystem.IsWindows() is false)
            return;

        const int windowsUDPConnectionResetControlCode = unchecked((int)0x9800000C);

        socket.IOControl(windowsUDPConnectionResetControlCode, [0x00, 0x00, 0x00, 0x00], null);
    }

    public async Task Run(CancellationToken stoppingToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(DatagramBufferSize);
        byte[]? batchBuffer = null;
        EndPoint any = frontSocket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        try
        {
            while (stoppingToken.IsCancellationRequested is false)
            {
                SocketReceiveFromResult result;

                try
                {
                    result = await frontSocket.ReceiveFromAsync(buffer, SocketFlags.None, any, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException exception)
                {
                    logger.LogDebug(exception, "Front Receive Failed On Port {Port}", PublicPort);
                    continue;
                }

                IPEndPoint client = (IPEndPoint)result.RemoteEndPoint;
                if (client.Address.IsIPv4MappedToIPv6)
                {
                    client = new IPEndPoint(client.Address.MapToIPv4(), client.Port);
                }

                await ProcessIncomingDatagramAsync(buffer.AsMemory(0, result.ReceivedBytes), client, stoppingToken).ConfigureAwait(false);

                // High-Frequency Batched Drain: If more datagrams are queued in the socket receive buffer,
                // synchronously drain up to 31 additional packets without context switching.
                const int MaxBatchDrain = 31;
                int batchDrained = 0;
                while (batchDrained < MaxBatchDrain && frontSocket.Available > 0 && !stoppingToken.IsCancellationRequested)
                {
                    batchBuffer ??= ArrayPool<byte>.Shared.Rent(DatagramBufferSize);
                    EndPoint batchRemote = any;
                    int batchBytes;
                    try
                    {
                        batchBytes = frontSocket.ReceiveFrom(batchBuffer, SocketFlags.None, ref batchRemote);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
                    {
                        break;
                    }
                    catch (SocketException ex)
                    {
                        logger.LogDebug(ex, "Front Synchronous Batch Receive Failed On Port {Port}", PublicPort);
                        break;
                    }

                    if (batchBytes <= 0) break;

                    IPEndPoint batchClient = (IPEndPoint)batchRemote;
                    if (batchClient.Address.IsIPv4MappedToIPv6)
                    {
                        batchClient = new IPEndPoint(batchClient.Address.MapToIPv4(), batchClient.Port);
                    }

                    await ProcessIncomingDatagramAsync(batchBuffer.AsMemory(0, batchBytes), batchClient, stoppingToken).ConfigureAwait(false);
                    batchDrained++;
                }

                if (batchDrained > 0)
                {
                    CompelMetrics.UdpBatchedDatagramsDrained.Add(batchDrained);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (batchBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(batchBuffer);
            }
        }
    }

    private async ValueTask ProcessIncomingDatagramAsync(
        ReadOnlyMemory<byte> rawMemory,
        IPEndPoint client,
        CancellationToken stoppingToken)
    {
        ReadOnlySpan<byte> rawSpan = rawMemory.Span;

        if (pongTemplate != null && TryExtractPingChallenge(rawSpan, out byte c0, out byte c1))
        {
            if (!pingRateLimiter.ShouldAllow(client.Address))
            {
                Drop(client, "PingAmplificationRateLimited");
                return;
            }

            await SendPongAsync(c0, c1, client, stoppingToken).ConfigureAwait(false);
            return;
        }

        SessionAdmissionResult admissionResult = TryGetOrCreateSession(client, stoppingToken, out ClientSession? session, out bool created);

        if (admissionResult is not SessionAdmissionResult.Admitted || session is null)
        {
            // Counted, Charged And Throttled Like Any Other Refusal. Unthrottled And Unscored, This Path Disabled The Abuse Protection For New Sources
            // At Exactly The Moment The Proxy Was Being Exhausted, And Logged Once Per Datagram.
            // Only The Arrival Is Charged And No Violation Weight, Because A Session Can Fail To Open For Reasons That Are Not The Client's Fault.
            scoreContainer.ChargeArrival(client);

            if (Drop(client, "Session Creation Failed"))
                logger.LogDebug("Failed To Create Proxy Session For {Client} ({Reason})", client, admissionResult);

            return;
        }

        // Authenticate A New Client Immediately So It Does Not Exhaust Its Unauthenticated Packet Budget Waiting For The First Periodic Renewal.
        if (enableLegacyWatermarkChallenge && created && !KongorPacketEnvelope.IsModernEnvelope(rawSpan))
            SendChallenge(client, session);

        // Every Datagram Costs Its Source, Whatever It Turns Out To Contain, Which Is What The Drain Rate Is Calibrated Against; The Reference Does This First As Well.
        scoreContainer.ChargeArrival(client);

        // A Source Already Over The Threshold Is Refused Before Any Field Of Its Datagram Is Read.
        // By This Point The Session Exists And Its Challenge Has Been Issued, Because A Client Must Be Challenged Before It Can Send Anything Valid.
        if (scoreContainer.IsWithinAllowance(client) is false)
        {
            Drop(client, "Actioned");
            return;
        }

        ReadOnlySpan<byte> datagram = rawSpan;

        // Modern Authenticated Transport Envelope (Phase 14A Dual-Stack Classifier)
        if (KongorPacketEnvelope.IsModernEnvelope(datagram))
        {
            if (session.Authenticator == null)
            {
                CompelMetrics.ModernEnvelopesTampered.Add(1);
                Drop(client, ViolationScoreContainer.TamperedDatagramWeight, "ModernEnvelopeSessionKeyUnavailable");
                return;
            }
            byte[] modernRentedBuffer = ArrayPool<byte>.Shared.Rent(datagram.Length);
            try
            {
                if (session.Authenticator.TryUnwrapAndDecrypt(
                    datagram,
                    DatagramDirection.ClientToServer,
                    modernRentedBuffer.AsSpan(),
                    out KongorPacketHeader modernHeader,
                    out int decryptedLen,
                    enforceAntiReplay: true))
                {
                    session.MarkModernSession();
                    session.MarkAuthenticated();
                    session.Touch();
                    session.SetSessionId(modernHeader.SessionId);

                    session.UpdateLastReceivedClientFeedback(modernHeader.PacketSequence, modernHeader.SubTickDeltaUs);

                    long nowTicks = timeProvider.GetTimestamp();
                    long nowUs = (long)(nowTicks * 1_000_000.0 / timeProvider.TimestampFrequency);

                    // Phase 14B: Process closed-loop BBR ACK feedback from modern client
                    if (session.InflightTracker != null && session.Bbr != null && modernHeader.AckSequence > 0)
                    {
                        if (session.InflightTracker.TryProcessAck(
                            modernHeader.AckSequence,
                            modernHeader.AckTimestampEchoUs,
                            nowUs,
                            out long bytesAcked,
                            out TimeSpan measuredRtt))
                        {
                            session.Bbr.OnAckReceived(bytesAcked, measuredRtt);
                            if (session.PmtuDiscovery.OnProbeAcknowledged(modernHeader.AckSequence))
                            {
                                CompelMetrics.PmtuProbesConfirmed.Add(1);
                                CompelMetrics.PmtuCurrentMtuBytes.Record(session.PmtuDiscovery.CurrentMtu);
                            }

                            if (session.InflightTracker.ConsecutiveLossCount >= 3)
                            {
                                ushort beforeMtu = session.PmtuDiscovery.CurrentMtu;
                                session.PmtuDiscovery.ReportConsecutiveLoss(session.InflightTracker.ConsecutiveLossCount);
                                if (session.PmtuDiscovery.CurrentMtu < beforeMtu)
                                {
                                    CompelMetrics.PmtuBlackHolesRecovered.Add(1);
                                }
                            }

                            CompelMetrics.BbrAcksProcessed.Add(1);
                            CompelMetrics.BbrEstimatedBandwidthBps.Record(session.Bbr.MaxBandwidthBytesPerSec);
                            CompelMetrics.BbrMinRttMs.Record(session.Bbr.MinRtt.TotalMilliseconds);
                        }
                    }

                    CompelMetrics.ModernEnvelopesAdmitted.Add(1);
                    CompelMetrics.UdpDatagramsReceived.Add(1);
                    CompelMetrics.UdpBytesTransferred.Add(rawMemory.Length);

                    ReadOnlyMemory<byte> modernPayload = modernRentedBuffer.AsMemory(0, decryptedLen);
                    ushort seq = (ushort)(modernHeader.PacketSequence & 0xFFFF);

                    if (!session.InboundQueue.TryEnqueue(modernPayload.Span, seq, nowUs, isModern: true))
                    {
                        CompelMetrics.UdpInboundQueueDropped.Add(1);
                    }

                    return;
                }
                else
                {
                    CompelMetrics.ModernEnvelopesTampered.Add(1);
                    Drop(client, ViolationScoreContainer.TamperedDatagramWeight, "ModernEnvelopeAuthenticationFailed");
                    return;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(modernRentedBuffer);
            }
        }

        ReadOnlyMemory<byte> payloadToSend;
        byte[]? fecRentedBuffer = null;

        if (enableFec && FecPacketHeader.IsFecPacket(datagram))
        {
            if (session.FecDecoder.TryProcessIncomingPacket(datagram, out FecDecodeResult fecResult))
            {
                if (fecResult.Action is FecPacketAction.DuplicateIgnored or FecPacketAction.ParityAcknowledged or FecPacketAction.Unrecoverable)
                {
                    return;
                }

                if (fecResult.Action is FecPacketAction.PassThrough or FecPacketAction.PacketRecovered)
                {
                    datagram = fecResult.DeliveredPayload;
                    fecRentedBuffer = ArrayPool<byte>.Shared.Rent(datagram.Length);
                    datagram.CopyTo(fecRentedBuffer);
                    payloadToSend = fecRentedBuffer.AsMemory(0, datagram.Length);
                }
                else
                {
                    return;
                }
            }
            else
            {
                return;
            }
        }
        else
        {
            payloadToSend = rawMemory;
        }

        if (enableLegacyWatermarkChallenge)
        {
            DatagramValidationResult outcome = DatagramValidator.Validate(
                datagram,
                kind,
                challenges,
                client,
                session.IsWithinUnknownChallengeGrace);

            if (outcome.IsAdmitted is false)
            {
                if (fecRentedBuffer != null)
                    ArrayPool<byte>.Shared.Return(fecRentedBuffer);

                if (outcome.ViolationWeight > 0)
                    Drop(client, outcome.ViolationWeight, outcome.DropReason!);
                else
                    Drop(client, outcome.DropReason!);

                return;
            }

            // A Matched Non-Zero Challenge Means The Client Has Demonstrably Accepted One Of Ours, So It No Longer Needs The Benefit Of The Doubt.
            if (outcome.IsAuthenticatedChallenge)
                session.MarkAuthenticated();

            // Anomaly Shield: Validate monotonic sequence, sliding 64-bit anti-replay, and burst flood
            if (enableAnomalyShield && ClientPacketReader.TryRead(datagram, kind, out uint clientChallenge, out ushort clientCounter))
            {
                AnomalyEvaluationResult anomaly = session.AnomalyShield.Evaluate(clientCounter, clientChallenge);
                if (!anomaly.IsAdmitted)
                {
                    if (fecRentedBuffer != null)
                        ArrayPool<byte>.Shared.Return(fecRentedBuffer);

                    if (anomaly.ViolationWeight > 0)
                        Drop(client, anomaly.ViolationWeight, anomaly.DropReason!);
                    else
                        Drop(client, anomaly.DropReason!);

                    return;
                }
            }
        }
        else
        {
            // Un-watermarked K2 packet mode (net_enableWatermark = false on both client and dedicated worker):
            // Game packets carry at least [seq_lo, seq_hi, flags] (3 bytes); Voice packets carry at least 1 byte.
            int minUnwatermarkedLen = kind == ProxyForwarderKind.Game ? 3 : 1;
            if (datagram.Length < minUnwatermarkedLen)
            {
                if (fecRentedBuffer != null)
                    ArrayPool<byte>.Shared.Return(fecRentedBuffer);

                Drop(client, ViolationScoreContainer.TooShortViolationWeight, "Below Minimum Unwatermarked Length");
                return;
            }

            session.MarkAuthenticated();
        }

        // Refreshed Only Now, So That Refused Traffic Never Extends A Session's Life; The Reference Does The Same.
        session.Touch();

        CompelMetrics.UdpDatagramsReceived.Add(1);
        CompelMetrics.UdpBytesTransferred.Add(rawMemory.Length);

        byte[]? decompressedBuffer = null;

        if (enableZstdCompression && ZstdDeltaCompressor.IsDeltaCompressed(datagram))
        {
            int expectedLen = ZstdDeltaCompressor.GetUncompressedLength(datagram);
            if (expectedLen > 0 && expectedLen <= DatagramBufferSize)
            {
                decompressedBuffer = ArrayPool<byte>.Shared.Rent(expectedLen);
                if (ZstdDeltaCompressor.TryDecompressDelta(datagram, decompressedBuffer, out int written))
                {
                    payloadToSend = decompressedBuffer.AsMemory(0, written);
                }
            }
        }

        try
        {
            ushort seq = 0;
            if (enableLegacyWatermarkChallenge)
            {
                if (ClientPacketReader.TryRead(datagram, kind, out _, out ushort counter))
                    seq = counter;
            }
            else if (datagram.Length >= 2)
            {
                seq = BinaryPrimitives.ReadUInt16LittleEndian(datagram);
            }

            long nowUs = (long)(timeProvider.GetTimestamp() * 1_000_000.0 / timeProvider.TimestampFrequency);

            if (!session.InboundQueue.TryEnqueue(payloadToSend.Span, seq, nowUs, isModern: false))
            {
                CompelMetrics.UdpInboundQueueDropped.Add(1);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed To Queue Datagram For {Client}", client);
        }
        finally
        {
            if (fecRentedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(fecRentedBuffer);
            }

            if (decompressedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(decompressedBuffer);
            }
        }
    }

    /// <summary>
    ///     Issues a fresh challenge to every active session, which is what resets each client's packet counter for the next window.
    /// </summary>
    public void RotateChallenges()
    {
        if (!enableLegacyWatermarkChallenge)
            return;

        uint sequence = NextChallengeSequence();
        challenges.Rotate(sequence, packetQuota);

        foreach (KeyValuePair<IPEndPoint, ClientSession> pair in sessions)
        {
            if (pair.Value.IsModernSession)
                continue;

            uint issuedTimestamp = pair.Value.NextIssuedTimestamp();

            Volatile.Write(ref pair.Value.IssuedTimestamp, issuedTimestamp);
            TransmitChallenge(pair.Key, sequence, issuedTimestamp);
        }
    }

    /// <summary>
    ///     Re-sends each session's current challenge without issuing a new one.
    ///     The reference does this about once a second, so that a single lost challenge datagram cannot leave a client holding an allowance sized for less time than it must now cover.
    ///     A client ignores a repeat of the challenge it already holds, so this is free of side effects for one that received the original.
    /// </summary>
    public void RepeatChallenges()
    {
        if (!enableLegacyWatermarkChallenge)
            return;

        if (challenges.CurrentChallenge is uint current)
        {
            foreach (KeyValuePair<IPEndPoint, ClientSession> pair in sessions)
            {
                if (pair.Value.IsModernSession)
                    continue;

                TransmitChallenge(pair.Key, current, Volatile.Read(ref pair.Value.IssuedTimestamp));
            }
        }
    }

    public void ChallengeActiveSessions() => RotateChallenges();

    private static byte[] CreatePongTemplate(string name, string location)
    {
        name = name.Trim('"', '\'');
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] descBytes = Array.Empty<byte>();
        byte[] locBytes = Encoding.UTF8.GetBytes(location);
        byte[] verBytes = Encoding.UTF8.GetBytes("5.0.0.0");
        byte[] mapBytes = Encoding.UTF8.GetBytes("caldavar");
        byte[] modeBytes = Encoding.UTF8.GetBytes("casual");

        int total = 6 + (nameBytes.Length + 1) + 2 + (descBytes.Length + 1) + (verBytes.Length + 1) + 4 + (locBytes.Length + 1) + (mapBytes.Length + 1) + (modeBytes.Length + 1) + 11;
        byte[] pong = new byte[total];
        int pos = 0;
        pong[pos++] = 0x00;
        pong[pos++] = 0x00;
        pong[pos++] = 0x01;
        pong[pos++] = 0x66;
        pong[pos++] = 0x00;
        pong[pos++] = 0x00;

        Array.Copy(nameBytes, 0, pong, pos, nameBytes.Length);
        pos += nameBytes.Length;
        pong[pos++] = 0x00;

        pong[pos++] = 0x00; // num_players
        pong[pos++] = 0x0A; // max_players (10)

        // 4. description (empty for idle dedicated servers ready for match hosting per K2 protocol)
        if (descBytes.Length > 0)
        {
            Array.Copy(descBytes, 0, pong, pos, descBytes.Length);
            pos += descBytes.Length;
        }
        pong[pos++] = 0x00;

        Array.Copy(verBytes, 0, pong, pos, verBytes.Length);
        pos += verBytes.Length;
        pong[pos++] = 0x00;

        // Bytes 6..9: 0x00, 0x01, 0x00, 0x00
        pong[pos++] = 0x00;
        pong[pos++] = 0x01;
        pong[pos++] = 0x00;
        pong[pos++] = 0x00;

        // 10. location
        Array.Copy(locBytes, 0, pong, pos, locBytes.Length);
        pos += locBytes.Length;
        pong[pos++] = 0x00;

        // 11. map ("caldavar")
        Array.Copy(mapBytes, 0, pong, pos, mapBytes.Length);
        pos += mapBytes.Length;
        pong[pos++] = 0x00;

        // 12. mode ("casual")
        Array.Copy(modeBytes, 0, pong, pos, modeBytes.Length);
        pos += modeBytes.Length;
        pong[pos++] = 0x00;

        // 13..18: 11 zeroes (matching native server)
        pos += 11;

        return pong;
    }


    private static bool TryExtractPingChallenge(ReadOnlySpan<byte> datagram, out byte c0, out byte c1)
    {
        c0 = 0;
        c1 = 0;

        if (datagram.Length >= 46 && datagram[42] == 0x01 && datagram[43] == 0xCA)
        {
            c0 = datagram[44];
            c1 = datagram[45];
            return true;
        }

        if (datagram.Length >= 6 && datagram[2] == 0x01 && datagram[3] == 0xCA)
        {
            c0 = datagram[4];
            c1 = datagram[5];
            return true;
        }

        return false;
    }

    private async Task SendPongAsync(byte c0, byte c1, IPEndPoint client, CancellationToken stoppingToken)
    {
        if (pongTemplate is null)
            return;

        byte[] reply = (byte[])pongTemplate.Clone();
        reply[4] = c0;
        reply[5] = c1;

        try
        {
            await frontSocket.SendToAsync(reply, SocketFlags.None, PrepareClientTarget(client), stoppingToken).ConfigureAwait(false);
            logger.LogDebug("[UDPForwarder] Responded with PONG ({Len} bytes) to {Client} on port {Port} for {ServerName}", reply.Length, client, PublicPort, serverName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UDPForwarder] Failed to send PONG to {Client} on port {Port}", client, PublicPort);
        }
    }

    private void SendChallenge(IPEndPoint client, ClientSession session)
    {
        uint? current = challenges.CurrentChallenge;

        if (current is null)
        {
            uint sequence = NextChallengeSequence();
            challenges.Rotate(sequence, packetQuota);
            current = sequence;
        }

        uint issuedTimestamp = session.NextIssuedTimestamp();

        Volatile.Write(ref session.IssuedTimestamp, issuedTimestamp);

        TransmitChallenge(client, current.Value, issuedTimestamp);
    }

    private uint NextChallengeSequence()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        uint sequence;

        do
        {
            RandomNumberGenerator.Fill(bytes);
            sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
        while (challenges.ContainsChallenge(sequence));

        return sequence;
    }

    private void TransmitChallenge(IPEndPoint client, uint challenge, uint serverCreationTimestamp)
    {
        byte[] packet = BuildChallengePacket(serverCreationTimestamp, challenge);

        try
        {
            frontSocket.SendTo(packet, SocketFlags.None, PrepareClientTarget(client));
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed To Send Challenge To {Client}", client);
        }
    }

    private byte[] BuildChallengePacket(uint serverCreationTimestamp, uint value)
    {
        byte[] packet = new byte[18];

        packet[0] = 0xFF;
        packet[1] = 0xFF;
        packet[2] = ProxyPacketFlag;
        packet[3] = ChallengePacketType;

        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), serverCreationTimestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), ChallengeExpirySeconds);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10), packetQuota);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12), gameCommandQuota);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(14), value);

        return packet;
    }

    private void Drop(IPEndPoint client, int weight, string reason)
    {
        scoreContainer.ChargeViolation(client, weight);
        attackIndicator.Charge(AttackIndicatorContainer.ValidationDropAttackWeight);

        Drop(client, reason);
    }

    /// <summary>
    ///     Counts a refused datagram and logs the reason once per source endpoint, so that a flood from a single source cannot become a log flood.
    ///     Returns whether this was the first refusal reported for the source, so a caller with more detail can log it under the same throttle.
    /// </summary>
    private bool Drop(IPEndPoint client, string reason)
    {
        Interlocked.Increment(ref droppedDatagramCount);

        if (Volatile.Read(ref reportedDropCount) >= ReportedDropLimit || reportedDrops.TryAdd(client, true) is false)
            return false;

        Interlocked.Increment(ref reportedDropCount);

        logger.LogWarning("Dropped A Datagram From {Client} On Public Port {Port} ({Reason}); Further Drops From This Source Are Not Logged", client, PublicPort, reason);

        return true;
    }

    private void ReleaseDropReport(IPEndPoint client)
    {
        if (reportedDrops.TryRemove(client, out _))
            Interlocked.Decrement(ref reportedDropCount);
    }

    private enum SessionAdmissionResult
    {
        Admitted,
        UnderAttack,
        ForwarderCapReached,
        AddressCapReached,
        CreationFailed
    }

    private SessionAdmissionResult TryGetOrCreateSession(IPEndPoint client, CancellationToken stoppingToken, out ClientSession? session, out bool created)
    {
        if (sessions.TryGetValue(client, out session))
        {
            created = false;

            return SessionAdmissionResult.Admitted;
        }

        lock (sessionsLock)
        {
            if (sessions.TryGetValue(client, out session))
            {
                created = false;

                return SessionAdmissionResult.Admitted;
            }

            if (attackIndicator.IsUnderAttack)
            {
                session = null;
                created = false;

                return SessionAdmissionResult.UnderAttack;
            }

            if (sessions.Count >= MaxSessionsPerForwarder)
            {
                attackIndicator.Charge(AttackIndicatorContainer.CapRefusalAttackWeight);

                session = null;
                created = false;

                return SessionAdmissionResult.ForwarderCapReached;
            }

            int sessionCountForAddress = 0;
            foreach (KeyValuePair<IPEndPoint, ClientSession> pair in sessions)
            {
                if (pair.Key.Address.Equals(client.Address))
                    sessionCountForAddress++;
            }

            if (sessionCountForAddress >= MaxSessionsPerAddress)
            {
                if (isPublicAddress(client.Address))
                {
                    attackIndicator.Charge(AttackIndicatorContainer.CapRefusalAttackWeight);

                    session = null;
                    created = false;

                    return SessionAdmissionResult.AddressCapReached;
                }

                if (Interlocked.Exchange(ref nonPublicCapExemptionLogged, 1) == 0)
                {
                    logger.LogWarning(
                        "[UDPForwarder] Non-public source {Address} exceeded MaxSessionsPerAddress ({Cap}) on public port {PublicPort} (active sessions from source: {Count}); admitting up to MaxSessionsPerForwarder ({ForwarderCap}). Verify Docker/NAT source-IP preservation if unexpected.",
                        client.Address,
                        MaxSessionsPerAddress,
                        PublicPort,
                        sessionCountForAddress + 1,
                        MaxSessionsPerForwarder);
                }
            }

            Socket upstreamSocket;
            try
            {
                upstreamSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    ReceiveBufferSize = 64 * 1024,
                    SendBufferSize = 64 * 1024
                };
                upstreamSocket.ConfigureForFastRebind();
                DisableConnectionResetReporting(upstreamSocket);
                upstreamSocket.Connect(serverEndPoint);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Failed To Open Upstream Proxy Socket For {Client}", client);

                session = null;
                created = false;

                return SessionAdmissionResult.CreationFailed;
            }

            attackIndicator.Charge(AttackIndicatorContainer.NovelEndpointAttackWeight);

            byte[]? resolvedKey = sessionKeyResolver?.Invoke(client);
            byte[]? sessionKey = resolvedKey != null && resolvedKey.Length == DatagramAuthenticator.KeySizeBytes
                ? resolvedKey
                : null;

            session = new ClientSession(upstreamSocket, timeProvider, stoppingToken, enableSubTickNetcode, enableBbrPacing, sessionKey);
            sessions[client] = session;

            _ = PumpServerToClient(client, session);
            _ = PumpClientToUpstream(client, session);

            created = true;

            return SessionAdmissionResult.Admitted;
        }
    }

    private async Task PumpServerToClient(IPEndPoint client, ClientSession session)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(DatagramBufferSize);
        CancellationToken cancellationToken = session.Cancellation.Token;

        try
        {
            while (cancellationToken.IsCancellationRequested is false)
            {
                int received;

                try
                {
                    received = await session.UpstreamSocket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException exception) when (exception.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
                {
                    continue;
                }

                if (received is 0)
                    continue;

                session.Touch();

                byte[]? compressedBuffer = null;
                bool isCompressed = false;
                int payloadLength = received;
                ushort activeMtu = session.PmtuDiscovery.CurrentMtu;

                if (enableZstdCompression && received >= MinCompressionThresholdBytes)
                {
                    compressedBuffer = ArrayPool<byte>.Shared.Rent(received + ZstdDeltaCompressor.HeaderSize + 32);
                    try
                    {
                        int compressedBytes = ZstdDeltaCompressor.CompressDelta(
                            buffer.AsSpan(0, received),
                            compressedBuffer.AsSpan(),
                            level: 1);

                        if (compressedBytes < received)
                        {
                            isCompressed = true;
                            payloadLength = compressedBytes;

                            CompelMetrics.ZstdDeltaBytesUncompressed.Add(received);
                            CompelMetrics.ZstdDeltaBytesCompressed.Add(compressedBytes);
                            CompelMetrics.ZstdDeltaDatagramsCompressed.Add(1);

                            if (received > activeMtu && compressedBytes <= activeMtu)
                            {
                                CompelMetrics.ZstdUdpFragmentationPrevented.Add(1);
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        logger.LogTrace(exception, "Failed to compress server payload with Zstd; falling back to uncompressed.");
                    }
                }

                int totalOutboundBytes = isCompressed ? payloadLength : received;
                int envelopeOverhead = session.IsModernSession && session.Authenticator != null ? KongorPacketEnvelope.TotalOverhead : 0;

                // Dynamic PMTU Clamping: If the snapshot payload exceeds the discovered Path MTU ceiling,
                // clamp or apply aggressive Zstd level 3 compression to prevent router-level IP packet fragmentation.
                if (totalOutboundBytes + envelopeOverhead > activeMtu)
                {
                    CompelMetrics.PmtuSnapshotsClamped.Add(1);

                    if (enableZstdCompression)
                    {
                        compressedBuffer ??= ArrayPool<byte>.Shared.Rent(received + ZstdDeltaCompressor.HeaderSize + 32);
                        try
                        {
                            int highCompressionBytes = ZstdDeltaCompressor.CompressDelta(
                                buffer.AsSpan(0, received),
                                compressedBuffer.AsSpan(),
                                level: 3);

                            if (highCompressionBytes < payloadLength)
                            {
                                isCompressed = true;
                                payloadLength = highCompressionBytes;

                                if (highCompressionBytes + envelopeOverhead <= activeMtu)
                                {
                                    CompelMetrics.ZstdUdpFragmentationPrevented.Add(1);
                                }
                            }
                        }
                        catch (Exception exception)
                        {
                            logger.LogTrace(exception, "Failed level 3 Zstd delta compression under PMTU clamping for {Client}", client);
                        }
                    }
                }

                ReadOnlyMemory<byte> memoryToSend = isCompressed && compressedBuffer != null
                    ? compressedBuffer.AsMemory(0, payloadLength)
                    : buffer.AsMemory(0, received);

                UdpTokenBucketPacer? activePacer = session.Pacer ?? pacer;
                if (enableBbrPacing && activePacer != null)
                {
                    if (session.Bbr != null && session.InflightTracker != null)
                    {
                        int targetCwnd = session.Bbr.GetTargetCongestionWindowBytes();
                        if (session.InflightTracker.InflightBytes >= targetCwnd)
                        {
                            CompelMetrics.BbrInflightCapped.Add(1);
                        }
                    }

                    PacingDecision decision = activePacer.Evaluate(memoryToSend.Length);
                    if (!decision.ShouldSendImmediately && decision.RecommendedDelay > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(decision.RecommendedDelay, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            activePacer.NotifyTransmissionComplete();
                            break;
                        }
                    }
                }

                byte[]? modernOutBuffer = null;
                try
                {
                    ReadOnlyMemory<byte> memoryToTransmit = memoryToSend;
                    if (session.IsModernSession && session.Authenticator != null)
                    {
                        int modernRequired = memoryToSend.Length + KongorPacketEnvelope.TotalOverhead;
                        modernOutBuffer = ArrayPool<byte>.Shared.Rent(modernRequired);

                        long nowTicks = timeProvider.GetTimestamp();
                        long nowUs = (long)(nowTicks * 1_000_000.0 / timeProvider.TimestampFrequency);
                        uint serverSeq = session.NextServerPacketSequence();

                        uint sid = session.SessionId != 0 ? session.SessionId : session.Authenticator.SessionId;
                        KongorPacketHeader serverHeader = new(
                            sessionId: sid,
                            packetSequence: serverSeq,
                            subTickDeltaUs: (uint)(nowUs & 0xFFFFFFFF),
                            ackSequence: session.LastReceivedClientSequence,
                            ackTimestampEchoUs: session.LastReceivedClientTimestampUs);

                        if (session.Authenticator.TryEncryptAndWrap(
                            modernOutBuffer.AsSpan(),
                            in serverHeader,
                            memoryToSend.Span,
                            DatagramDirection.ServerToClient,
                            out int modernBytesWritten))
                        {
                            memoryToTransmit = modernOutBuffer.AsMemory(0, modernBytesWritten);
                            session.InflightTracker?.TrackPacket(serverSeq, (ushort)modernBytesWritten, nowUs);
                        }
                    }

                    await frontSocket.SendToAsync(memoryToTransmit, SocketFlags.None, PrepareClientTarget(client), cancellationToken).ConfigureAwait(false);
                    activePacer?.NotifyTransmissionComplete();
                    CompelMetrics.UdpDatagramsRelayed.Add(1);
                    CompelMetrics.UdpBytesTransferred.Add(memoryToTransmit.Length);

                    if (session.IsModernSession && session.Authenticator != null && session.PmtuDiscovery.ShouldProbe(out ushort probeTargetSize))
                    {
                        await SendPmtuProbeAsync(client, session, probeTargetSize, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (modernOutBuffer != null)
                    {
                        ArrayPool<byte>.Shared.Return(modernOutBuffer);
                    }

                    if (compressedBuffer != null)
                    {
                        ArrayPool<byte>.Shared.Return(compressedBuffer);
                    }
                }
            }
        }
        catch (OperationCanceledException ex) { logger.LogTrace(ex, "Server-To-Client Relay Canceled For {Client}", client); }
        catch (ObjectDisposedException ex) { logger.LogTrace(ex, "Server-To-Client Relay Socket Disposed For {Client}", client); }
        catch (Exception exception) { logger.LogDebug(exception, "Server-To-Client Relay Ended For {Client}", client); }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);

            lock (sessionsLock)
            {
                if (sessions.TryGetValue(client, out ClientSession? current) && ReferenceEquals(current, session))
                {
                    if (sessions.TryRemove(client, out ClientSession? removed))
                    {
                        removed.Dispose();
                        ReleaseDropReport(client);
                    }
                }
            }
        }
    }

    private async Task SendPmtuProbeAsync(
        IPEndPoint client,
        ClientSession session,
        ushort probeTargetSize,
        CancellationToken cancellationToken)
    {
        if (session.Authenticator == null)
            return;

        int probePayloadSize = probeTargetSize - KongorPacketEnvelope.TotalOverhead;
        if (probePayloadSize < 8)
            return;

        byte[] probeRentedBuffer = ArrayPool<byte>.Shared.Rent(probeTargetSize);
        byte[] probeInnerBuffer = ArrayPool<byte>.Shared.Rent(probePayloadSize);
        try
        {
            uint probeSeq = session.NextServerPacketSequence();
            long probeNowTicks = timeProvider.GetTimestamp();
            long probeNowUs = (long)(probeNowTicks * 1_000_000.0 / timeProvider.TimestampFrequency);

            PathMtuDiscoveryService.TryWriteProbePayload(probeInnerBuffer.AsSpan(0, probePayloadSize), probeSeq, (ushort)probePayloadSize);

            uint sid = session.SessionId != 0 ? session.SessionId : session.Authenticator.SessionId;
            KongorPacketHeader probeHeader = new(
                sessionId: sid,
                packetSequence: probeSeq,
                subTickDeltaUs: (uint)(probeNowUs & 0xFFFFFFFF),
                ackSequence: session.LastReceivedClientSequence,
                ackTimestampEchoUs: session.LastReceivedClientTimestampUs);

            if (session.Authenticator.TryEncryptAndWrap(
                probeRentedBuffer.AsSpan(0, probeTargetSize),
                in probeHeader,
                probeInnerBuffer.AsSpan(0, probePayloadSize),
                DatagramDirection.ServerToClient,
                out int probeBytesWritten))
            {
                session.PmtuDiscovery.OnProbeDispatched(probeSeq, (ushort)probeBytesWritten);
                session.InflightTracker?.TrackPacket(probeSeq, (ushort)probeBytesWritten, probeNowUs);

                await frontSocket.SendToAsync(
                    probeRentedBuffer.AsMemory(0, probeBytesWritten),
                    SocketFlags.None,
                    PrepareClientTarget(client),
                    cancellationToken).ConfigureAwait(false);

                CompelMetrics.PmtuProbesDispatched.Add(1);
                CompelMetrics.UdpDatagramsRelayed.Add(1);
                CompelMetrics.UdpBytesTransferred.Add(probeBytesWritten);
            }
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "Failed to transmit PMTU probe to {Client}", client);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(probeInnerBuffer);
            ArrayPool<byte>.Shared.Return(probeRentedBuffer);
        }
    }

    private async Task PumpClientToUpstream(IPEndPoint client, ClientSession session)
    {
        CancellationToken cancellationToken = session.Cancellation.Token;

        try
        {
            while (!cancellationToken.IsCancellationRequested && await session.InboundQueue.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!session.InboundQueue.TryDequeue(out ReadOnlyMemory<byte> datagram, out ushort seq, out long nowUs, out _))
                {
                    continue;
                }

                if (enableSubTickNetcode && session.JitterBuffer != null)
                {
                    session.JitterBuffer.TryEnqueue(datagram.Span, seq, nowUs, out _);

                    byte[] rentSlot = ArrayPool<byte>.Shared.Rent(DatagramBufferSize);
                    try
                    {
                        while (session.JitterBuffer.TryDequeue(nowUs, rentSlot, out int dequeuedBytes, out _))
                        {
                            await session.UpstreamSocket.SendAsync(rentSlot.AsMemory(0, dequeuedBytes), SocketFlags.None, cancellationToken).ConfigureAwait(false);
                            CompelMetrics.UdpDatagramsRelayed.Add(1);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rentSlot);
                    }
                }
                else
                {
                    await session.UpstreamSocket.SendAsync(datagram, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                    CompelMetrics.UdpDatagramsRelayed.Add(1);
                }
            }
        }
        catch (OperationCanceledException ex) { logger.LogTrace(ex, "Client-To-Upstream Relay Canceled For {Client}", client); }
        catch (ObjectDisposedException ex) { logger.LogTrace(ex, "Client-To-Upstream Relay Socket Disposed For {Client}", client); }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Client-To-Upstream Relay Ended For {Client}", client);
        }
    }

    public void EvictIdleSessions(TimeSpan idleTimeout, TimeSpan unauthenticatedTimeout)
    {
        if (sessionsRetained)
        {
            // Milestone 4.5: Freeze session eviction while dedicated worker is resurrecting from crash
            return;
        }

        long now = timeProvider.GetTimestamp();

        lock (sessionsLock)
        {
            foreach (KeyValuePair<IPEndPoint, ClientSession> pair in sessions)
            {
                TimeSpan timeout = pair.Value.HasAuthenticated ? idleTimeout : unauthenticatedTimeout;

                if (timeProvider.GetElapsedTime(Volatile.Read(ref pair.Value.LastActivityTimestamp), now) < timeout)
                    continue;

                if (sessions.TryRemove(pair.Key, out ClientSession? removed))
                {
                    removed.Dispose();
                    ReleaseDropReport(pair.Key);
                }
            }
        }

        foreach (KeyValuePair<IPEndPoint, bool> report in reportedDrops)
        {
            if (sessions.ContainsKey(report.Key) is false)
                ReleaseDropReport(report.Key);
        }
    }

    public void EvictIdleSessions(TimeSpan idleTimeout)
        => EvictIdleSessions(idleTimeout, TimeSpan.FromSeconds(15));

    public void Dispose()
    {
        frontSocket.Dispose();

        foreach (ClientSession session in sessions.Values)
            session.Dispose();

        sessions.Clear();
    }

    internal BbrCongestionController? GetSessionBbr(IPEndPoint client)
        => sessions.TryGetValue(client, out ClientSession? s) ? s.Bbr : null;

    internal InflightPacketTracker? GetSessionInflightTracker(IPEndPoint client)
        => sessions.TryGetValue(client, out ClientSession? s) ? s.InflightTracker : null;

    internal UdpTokenBucketPacer? GetSessionPacer(IPEndPoint client)
        => sessions.TryGetValue(client, out ClientSession? s) ? s.Pacer : null;

    internal PathMtuDiscoveryService? GetSessionPmtuDiscovery(IPEndPoint client)
        => sessions.TryGetValue(client, out ClientSession? s) ? s.PmtuDiscovery : null;

    private sealed class ClientSession : IDisposable
    {
        public Socket UpstreamSocket { get; }
        public CancellationTokenSource Cancellation { get; }
        public SpscDatagramRingBuffer InboundQueue { get; } = new(capacity: 64);
        public XorFecDecoder FecDecoder { get; } = new();
        public DatagramAnomalyShield AnomalyShield { get; }
        public AdaptiveJitterBuffer? JitterBuffer { get; }
        public DatagramAuthenticator? Authenticator { get; }
        public BbrCongestionController? Bbr { get; }
        public InflightPacketTracker? InflightTracker { get; }
        public UdpTokenBucketPacer? Pacer { get; }
        public PathMtuDiscoveryService PmtuDiscovery { get; }
        public long LastActivityTimestamp;
        public uint IssuedTimestamp;

        private readonly TimeProvider timeProvider;
        private readonly long createdTimestamp;
        private long lastIssuedTimestamp;
        private long serverPacketSequence;
        private long lastReceivedClientSequence;
        private long lastReceivedClientTimestampUs;
        private long sessionId;
        private bool isModernSession;
        private bool authenticated;
        private int disposed;

        public bool IsModernSession => Volatile.Read(ref isModernSession);

        public void MarkModernSession() => Volatile.Write(ref isModernSession, true);

        public uint NextServerPacketSequence() => (uint)Interlocked.Increment(ref serverPacketSequence);

        public uint SessionId => (uint)Volatile.Read(ref sessionId);

        public void SetSessionId(uint id) => Volatile.Write(ref sessionId, id);

        public uint LastReceivedClientSequence => (uint)Volatile.Read(ref lastReceivedClientSequence);

        public uint LastReceivedClientTimestampUs => (uint)Volatile.Read(ref lastReceivedClientTimestampUs);

        public void UpdateLastReceivedClientFeedback(uint sequence, uint timestampUs)
        {
            Volatile.Write(ref lastReceivedClientSequence, sequence);
            Volatile.Write(ref lastReceivedClientTimestampUs, timestampUs);
        }

        public ClientSession(
            Socket upstreamSocket,
            TimeProvider timeProvider,
            CancellationToken stoppingToken,
            bool enableSubTickNetcode = false,
            bool enableBbrPacing = false,
            byte[]? sessionKey = null)
        {
            UpstreamSocket = upstreamSocket;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            this.timeProvider = timeProvider;
            createdTimestamp = timeProvider.GetTimestamp();
            AnomalyShield = new DatagramAnomalyShield(timeProvider);
            PmtuDiscovery = new PathMtuDiscoveryService(timeProvider);
            if (enableSubTickNetcode)
            {
                JitterBuffer = new AdaptiveJitterBuffer(capacity: 32, minDelayUs: 0, maxDelayUs: 20_000);
            }

            if (enableBbrPacing)
            {
                Bbr = new BbrCongestionController(timeProvider);
                InflightTracker = new InflightPacketTracker(capacity: 256);
                Pacer = new UdpTokenBucketPacer(bbr: Bbr, timeProvider: timeProvider);
            }

            if (sessionKey != null && sessionKey.Length == DatagramAuthenticator.KeySizeBytes)
            {
                Authenticator = new DatagramAuthenticator(sessionKey, sessionId: 0);
            }

            Touch();
        }

        public bool HasAuthenticated => Volatile.Read(ref authenticated);

        public bool IsWithinUnknownChallengeGrace
            => HasAuthenticated is false && timeProvider.GetElapsedTime(createdTimestamp) < UnknownChallengeGrace;

        public void MarkAuthenticated()
        {
            if (Volatile.Read(ref authenticated) is false)
                Volatile.Write(ref authenticated, true);
        }

        public void Touch() => Volatile.Write(ref LastActivityTimestamp, timeProvider.GetTimestamp());

        public uint NextIssuedTimestamp()
        {
            long now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            long monotonic = Math.Max(Interlocked.Read(ref lastIssuedTimestamp) + 1, now);

            Interlocked.Exchange(ref lastIssuedTimestamp, monotonic);

            return (uint)monotonic;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) is not 0)
                return;

            Authenticator?.Dispose();
            InboundQueue.Dispose();

            if (JitterBuffer != null)
            {
                JitterBuffer.DrainAll((payload, seq) =>
                {
                    try
                    {
                        UpstreamSocket.Send(payload.Span, SocketFlags.None);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ClientSession] JitterBuffer drain Send error (seq={seq}): {ex.Message}");
                    }
                });
            }

            Cancellation.Cancel();
            Cancellation.Dispose();
            UpstreamSocket.Dispose();
        }
    }
}

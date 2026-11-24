// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Wave4P2pStunRelayTests.cs
// System: ProjectKongor - Wave 4 Track 2 P2P STUN, COMPEL Relay & RNNoise Tests
// Policy: Strict Explicit Typing (NO 'var') - Sub-2s Automated Unit Tests
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Core.Network;
using Kongor.Core.Services.Voice;
using Xunit;

namespace Kongor.Core.Tests.Network;

public sealed class Wave4P2pStunRelayTests
{
    private sealed class LoopbackMockStunServer : IAsyncDisposable
    {
        private readonly Socket _socket;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loopTask;
        private readonly Func<IPEndPoint, IPEndPoint> _mappedAddressFactory;
        private readonly bool _useXorMappedAddress;
        private readonly bool _includeMappedFallback;
        private readonly bool _sendSpoofedPacketFirst;
        private readonly bool _dropAllPackets;

        public IPEndPoint BoundEndPoint { get; }

        public LoopbackMockStunServer(
            AddressFamily family,
            Func<IPEndPoint, IPEndPoint> mappedAddressFactory,
            bool useXorMappedAddress = true,
            bool includeMappedFallback = false,
            bool sendSpoofedPacketFirst = false,
            bool dropAllPackets = false)
        {
            _mappedAddressFactory = mappedAddressFactory;
            _useXorMappedAddress = useXorMappedAddress;
            _includeMappedFallback = includeMappedFallback;
            _sendSpoofedPacketFirst = sendSpoofedPacketFirst;
            _dropAllPackets = dropAllPackets;

            _socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            IPAddress bindAddr = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            _socket.Bind(new IPEndPoint(bindAddr, 0));
            BoundEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
            _loopTask = Task.Run(RunLoopAsync);
        }

        private async Task RunLoopAsync()
        {
            byte[] rx = new byte[512];
            byte[] tx = new byte[256];
            EndPoint any = _socket.AddressFamily == AddressFamily.InterNetworkV6
                ? new IPEndPoint(IPAddress.IPv6Any, 0)
                : new IPEndPoint(IPAddress.Any, 0);

            while (!_cts.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try
                {
                    received = await _socket.ReceiveFromAsync(rx, SocketFlags.None, any, _cts.Token).ConfigureAwait(false);
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
                    break;
                }

                if (_dropAllPackets || received.ReceivedBytes < StunNatClassifier.HeaderLengthBytes)
                {
                    continue;
                }

                ushort msgType = BinaryPrimitives.ReadUInt16BigEndian(rx.AsSpan(0, 2));
                uint cookie = BinaryPrimitives.ReadUInt32BigEndian(rx.AsSpan(4, 4));
                if (msgType != StunNatClassifier.BindingRequestMessageType || cookie != StunNatClassifier.MagicCookie)
                {
                    continue;
                }

                IPEndPoint clientEp = (IPEndPoint)received.RemoteEndPoint;
                IPEndPoint mappedEp = _mappedAddressFactory(clientEp);
                byte[] requestTxId = new byte[StunNatClassifier.TransactionIdLengthBytes];
                rx.AsSpan(8, StunNatClassifier.TransactionIdLengthBytes).CopyTo(requestTxId);

                if (_sendSpoofedPacketFirst)
                {
                    byte[] badTxId = (byte[])requestTxId.Clone();
                    badTxId[0] ^= 0xFF; // Deliberate TxId corruption (spoofing test)
                    IPEndPoint fakeMapped = new IPEndPoint(IPAddress.Parse("198.51.100.254"), 65000);
                    int spoofLen = StunNatClassifier.BuildBindingSuccessResponse(badTxId, fakeMapped, tx, useXorMappedAddress: true);
                    await _socket.SendToAsync(tx.AsMemory(0, spoofLen), SocketFlags.None, clientEp, _cts.Token).ConfigureAwait(false);
                }

                int respLen = StunNatClassifier.BuildBindingSuccessResponse(
                    requestTxId,
                    mappedEp,
                    tx,
                    useXorMappedAddress: _useXorMappedAddress,
                    includeMappedAddressFallback: _includeMappedFallback);
                await _socket.SendToAsync(tx.AsMemory(0, respLen), SocketFlags.None, clientEp, _cts.Token).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _socket.Dispose();
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }

            _cts.Dispose();
        }
    }

    private sealed class FakeRnNoiseNativeApi : IRnNoiseNativeApi
    {
        public string LibraryName { get; init; } = "fake-librnnoise-unit-test";
        public int ExpectedFrameSize { get; init; } = RnNoiseNativeBridge.FrameSizeSamples;
        public float ReturnedVad { get; set; } = 0.87f;
        public float OutputScaleFactor { get; set; } = 0.5f;
        public int CreateCount { get; private set; }
        public int DestroyCount { get; private set; }
        public int ProcessCallCount { get; private set; }
        public int LastObservedFrameLength { get; private set; }
        public float LastObservedMaxAbsInput { get; private set; }
        private long _nextHandle = 1000;

        public IntPtr CreateState()
        {
            CreateCount++;
            return new IntPtr(++_nextHandle);
        }

        public void DestroyState(IntPtr state)
        {
            if (state != IntPtr.Zero)
            {
                DestroyCount++;
            }
        }

        public float ProcessFrame(IntPtr state, Span<float> output, ReadOnlySpan<float> input)
        {
            ProcessCallCount++;
            LastObservedFrameLength = input.Length;
            float maxAbs = 0f;
            for (int i = 0; i < input.Length; i++)
            {
                float absVal = MathF.Abs(input[i]);
                if (absVal > maxAbs)
                {
                    maxAbs = absVal;
                }

                output[i] = input[i] * OutputScaleFactor;
            }

            LastObservedMaxAbsInput = maxAbs;
            return ReturnedVad;
        }
    }

    [Fact]
    public void StunCodec_Ipv4AndIpv6XorMappedAddress_RoundTripsAndRejectsSpoofedTransactionId()
    {
        // protects: RFC 5389 Binding Request/Response codec for IPv4 & IPv6 XOR-MAPPED-ADDRESS and TxId anti-spoofing.
        // fails_when: Magic cookie, XOR masking (IPv4 32-bit cookie or IPv6 128-bit cookie+TxId), or constant-time TxId check breaks.
        // seam: StunNatClassifier.BuildBindingRequest, BuildBindingSuccessResponse, TryParseBindingResponse.
        byte[] req = new byte[StunNatClassifier.HeaderLengthBytes];
        byte[] txId = new byte[StunNatClassifier.TransactionIdLengthBytes];
        int reqLen = StunNatClassifier.BuildBindingRequest(req, txId);

        reqLen.Should().Be(20);
        BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(0, 2)).Should().Be(0x0001);
        BinaryPrimitives.ReadUInt32BigEndian(req.AsSpan(4, 4)).Should().Be(0x2112A442u);

        // IPv4 XOR-MAPPED-ADDRESS round-trip
        IPEndPoint expectedV4 = new IPEndPoint(IPAddress.Parse("203.0.113.77"), 45123);
        byte[] respV4 = new byte[128];
        int respV4Len = StunNatClassifier.BuildBindingSuccessResponse(txId, expectedV4, respV4, useXorMappedAddress: true);

        bool parsedV4 = StunNatClassifier.TryParseBindingResponse(
            respV4.AsSpan(0, respV4Len),
            txId,
            out IPEndPoint? actualV4,
            out bool usedXorV4,
            out string? errV4);

        parsedV4.Should().BeTrue(errV4);
        usedXorV4.Should().BeTrue();
        actualV4.Should().Be(expectedV4);

        // IPv6 XOR-MAPPED-ADDRESS round-trip (128-bit mask = MagicCookie || TransactionId)
        IPEndPoint expectedV6 = new IPEndPoint(IPAddress.Parse("2001:db8:85a3::8a2e:370:7334"), 51820);
        byte[] respV6 = new byte[128];
        int respV6Len = StunNatClassifier.BuildBindingSuccessResponse(txId, expectedV6, respV6, useXorMappedAddress: true);

        bool parsedV6 = StunNatClassifier.TryParseBindingResponse(
            respV6.AsSpan(0, respV6Len),
            txId,
            out IPEndPoint? actualV6,
            out bool usedXorV6,
            out string? errV6);

        parsedV6.Should().BeTrue(errV6);
        usedXorV6.Should().BeTrue();
        actualV6.Should().Be(expectedV6);

        // Legacy MAPPED-ADDRESS fallback when XOR-MAPPED-ADDRESS is absent
        byte[] respLegacy = new byte[128];
        int respLegacyLen = StunNatClassifier.BuildBindingSuccessResponse(txId, expectedV4, respLegacy, useXorMappedAddress: false);
        bool parsedLegacy = StunNatClassifier.TryParseBindingResponse(
            respLegacy.AsSpan(0, respLegacyLen),
            txId,
            out IPEndPoint? actualLegacy,
            out bool usedXorLegacy,
            out string? errLegacy);

        parsedLegacy.Should().BeTrue(errLegacy);
        usedXorLegacy.Should().BeFalse();
        actualLegacy.Should().Be(expectedV4);

        // Transaction ID spoofing rejection
        byte[] spoofedTxId = (byte[])txId.Clone();
        spoofedTxId[11] ^= 0x01;
        bool parsedSpoofed = StunNatClassifier.TryParseBindingResponse(
            respV4.AsSpan(0, respV4Len),
            spoofedTxId,
            out IPEndPoint? spoofedEp,
            out _,
            out string? spoofReason);

        parsedSpoofed.Should().BeFalse();
        spoofedEp.Should().BeNull();
        spoofReason.Should().Contain("Transaction ID mismatch");
    }

    [Fact]
    public async Task ProbeNatAsync_LiveLoopbackStun_ClassifiesConeNatAndRejectsSpoofedPacketBeforeRealReply()
    {
        // protects: Live UDP 2-endpoint STUN Cone NAT detection and immunity to spoofed TxId packets in flight.
        // fails_when: Same mapped external IP:port fails ConeNat classification or spoofed packet aborts probe.
        // seam: StunNatClassifier.ProbeNatAsync against two real loopback UDP mock STUN servers.
        IPEndPoint sharedExternal = new IPEndPoint(IPAddress.Parse("198.51.100.42"), 42010);
        await using LoopbackMockStunServer ep1 = new LoopbackMockStunServer(
            AddressFamily.InterNetwork,
            _ => sharedExternal,
            useXorMappedAddress: true,
            includeMappedFallback: true,
            sendSpoofedPacketFirst: true);
        await using LoopbackMockStunServer ep2 = new LoopbackMockStunServer(
            AddressFamily.InterNetwork,
            _ => sharedExternal,
            useXorMappedAddress: true);

        List<string> logs = [];
        StunProbeResult result = await StunNatClassifier.ProbeNatAsync(
            ep1.BoundEndPoint,
            ep2.BoundEndPoint,
            timeout: TimeSpan.FromMilliseconds(800),
            logSink: logs.Add);

        result.Classification.Should().Be(NatBehaviorClassification.ConeNat);
        result.RequiresRelay.Should().BeFalse();
        result.UsedXorMappedAddress.Should().BeTrue();
        result.PrimaryMappedEndPoint.Should().Be(sharedExternal);
        result.SecondaryMappedEndPoint.Should().Be(sharedExternal);
        result.RejectedSpoofedPackets.Should().BeGreaterThanOrEqualTo(1);
        logs.Should().Contain(l => l.Contains("[P2P-STUN]", StringComparison.Ordinal) && l.Contains("Rejected invalid/spoofed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProbeNatAsync_LiveLoopbackIpv6Stun_ClassifiesOpenInternetOnMatchingSocket()
    {
        // protects: IPv6 STUN socket path and OpenInternet detection when mapped IP:port equals local bound endpoint.
        // fails_when: IPv6 XOR-MAPPED-ADDRESS over loopback UDP fails or misclassifies matching local endpoint.
        // seam: StunNatClassifier.ProbeNatAsync over IPv6 loopback UDP.
        await using LoopbackMockStunServer ep1 = new LoopbackMockStunServer(AddressFamily.InterNetworkV6, client => client);
        await using LoopbackMockStunServer ep2 = new LoopbackMockStunServer(AddressFamily.InterNetworkV6, client => client);

        StunProbeResult result = await StunNatClassifier.ProbeNatAsync(
            ep1.BoundEndPoint,
            ep2.BoundEndPoint,
            timeout: TimeSpan.FromMilliseconds(800));

        result.Classification.Should().Be(NatBehaviorClassification.OpenInternet);
        result.RequiresRelay.Should().BeFalse();
        result.UsedXorMappedAddress.Should().BeTrue();
        result.PrimaryMappedEndPoint.Should().NotBeNull();
        result.PrimaryMappedEndPoint!.AddressFamily.Should().Be(AddressFamily.InterNetworkV6);
    }

    [Fact]
    public async Task ProbeNatAsync_LiveLoopbackStun_ClassifiesSymmetricNatAndCgnatAndUdpBlocked()
    {
        // protects: Symmetric NAT (divergent ports across EP1/EP2), RFC 6598 CGNAT (100.64.0.0/10), and UdpBlocked classification.
        // fails_when: Divergent ports or 100.64.x.x CGNAT fail to require relay, or silent STUN endpoints hang/misclassify.
        // seam: StunNatClassifier.ProbeNatAsync against real loopback UDP servers.

        // 1. Symmetric NAT (endpoint-dependent external port)
        await using (LoopbackMockStunServer ep1 = new LoopbackMockStunServer(AddressFamily.InterNetwork, _ => new IPEndPoint(IPAddress.Parse("203.0.113.10"), 50001)))
        await using (LoopbackMockStunServer ep2 = new LoopbackMockStunServer(AddressFamily.InterNetwork, _ => new IPEndPoint(IPAddress.Parse("203.0.113.10"), 50002)))
        {
            StunProbeResult sym = await StunNatClassifier.ProbeNatAsync(ep1.BoundEndPoint, ep2.BoundEndPoint, TimeSpan.FromMilliseconds(600));
            sym.Classification.Should().Be(NatBehaviorClassification.SymmetricOrCgnat);
            sym.IsCgnatAddress.Should().BeFalse();
            sym.RequiresRelay.Should().BeTrue();
        }

        // 2. RFC 6598 Carrier-Grade NAT (100.64.0.0/10)
        IPEndPoint cgnatEp = new IPEndPoint(IPAddress.Parse("100.64.88.19"), 41100);
        await using (LoopbackMockStunServer ep1 = new LoopbackMockStunServer(AddressFamily.InterNetwork, _ => cgnatEp))
        await using (LoopbackMockStunServer ep2 = new LoopbackMockStunServer(AddressFamily.InterNetwork, _ => cgnatEp))
        {
            StunProbeResult cgnat = await StunNatClassifier.ProbeNatAsync(ep1.BoundEndPoint, ep2.BoundEndPoint, TimeSpan.FromMilliseconds(600));
            cgnat.Classification.Should().Be(NatBehaviorClassification.SymmetricOrCgnat);
            cgnat.IsCgnatAddress.Should().BeTrue();
            cgnat.RequiresRelay.Should().BeTrue();
        }

        // 3. UDP Blocked (silent endpoints)
        await using (LoopbackMockStunServer silent1 = new LoopbackMockStunServer(AddressFamily.InterNetwork, client => client, dropAllPackets: true))
        await using (LoopbackMockStunServer silent2 = new LoopbackMockStunServer(AddressFamily.InterNetwork, client => client, dropAllPackets: true))
        {
            StunProbeResult blocked = await StunNatClassifier.ProbeNatAsync(silent1.BoundEndPoint, silent2.BoundEndPoint, TimeSpan.FromMilliseconds(160));
            blocked.Classification.Should().Be(NatBehaviorClassification.UdpBlocked);
            blocked.RequiresRelay.Should().BeTrue();
        }
    }

    [Fact]
    public void DirectPlayRelayCoordinator_Compel40ByteWatermarkAnd18ByteChallenge_MatchUdpForwarderOffsets()
    {
        // protects: Exact byte-level alignment with COMPEL UDPForwarder / ClientPacketReader (40B watermark, challenge @ 28, counter @ 32, ProxyPacketFlag=0x40).
        // fails_when: Offsets or minimum datagram lengths drift from COMPEL UDPForwarder.
        // seam: DirectPlayRelayCoordinator.TryBuildCompelKeepaliveDatagram, TryParseCompelWatermarkDatagram, TryBuildCompelChallengePacket, TryParseCompelChallengePacket.
        byte[] keepalive = new byte[64];
        const uint challenge = 0xCAFEBABEu;
        const ushort counter = 4242;

        bool built = DirectPlayRelayCoordinator.TryBuildCompelKeepaliveDatagram(keepalive, challenge, counter, out int written);
        built.Should().BeTrue();
        written.Should().Be(43);

        // Verify exact byte offsets matching ClientPacketReader.ChallengeOffset (28) & CounterOffset (32)
        BinaryPrimitives.ReadUInt32LittleEndian(keepalive.AsSpan(28, 4)).Should().Be(challenge);
        BinaryPrimitives.ReadUInt16LittleEndian(keepalive.AsSpan(32, 2)).Should().Be(counter);
        keepalive[40].Should().Be(0xFF);
        keepalive[41].Should().Be(0xFF);
        keepalive[42].Should().Be(DirectPlayRelayCoordinator.CompelProxyPacketFlag);

        bool parsed = DirectPlayRelayCoordinator.TryParseCompelWatermarkDatagram(
            keepalive.AsSpan(0, written),
            isVoice: false,
            out uint parsedChallenge,
            out ushort parsedCounter,
            out ReadOnlySpan<byte> payload);

        parsed.Should().BeTrue();
        parsedChallenge.Should().Be(challenge);
        parsedCounter.Should().Be(counter);
        payload.Length.Should().Be(3);
        payload[2].Should().Be(0x40);

        // Game rejects < 43 bytes; Voice accepts 41 bytes
        byte[] voiceDatagram = new byte[41];
        DirectPlayRelayCoordinator.TryWriteCompelWatermarkDatagram(voiceDatagram, challenge, counter, [0x77], out int voiceWritten, isVoice: true).Should().BeTrue();
        voiceWritten.Should().Be(41);
        DirectPlayRelayCoordinator.TryParseCompelWatermarkDatagram(voiceDatagram, isVoice: false, out _, out _, out _).Should().BeFalse();
        DirectPlayRelayCoordinator.TryParseCompelWatermarkDatagram(voiceDatagram, isVoice: true, out uint vChal, out ushort vCnt, out ReadOnlySpan<byte> vPay).Should().BeTrue();
        vChal.Should().Be(challenge);
        vCnt.Should().Be(counter);
        vPay[0].Should().Be(0x77);

        // 18-byte COMPEL Proxy Challenge packet round-trip
        byte[] challengePkt = new byte[18];
        DirectPlayRelayCoordinator.TryBuildCompelChallengePacket(
            challengePkt,
            serverCreationTimestamp: 1700000123u,
            challengeValue: challenge,
            packetQuota: 1200,
            gameCommandQuota: 400,
            out int chalLen).Should().BeTrue();
        chalLen.Should().Be(18);

        DirectPlayRelayCoordinator.TryParseCompelChallengePacket(
            challengePkt,
            out uint ts,
            out ushort expiry,
            out ushort pktQuota,
            out ushort cmdQuota,
            out uint chalVal).Should().BeTrue();
        ts.Should().Be(1700000123u);
        expiry.Should().Be(60);
        pktQuota.Should().Be(1200);
        cmdQuota.Should().Be(400);
        chalVal.Should().Be(challenge);
    }

    [Fact]
    public void DirectPlayRelayCoordinator_PlanTransport_SelectsOptimalStrategyAndGeneratesSanitizedRelayConfigs()
    {
        // protects: Transport ladder (DirectUpnp -> StunHolePunch -> CompelUdpProxyRelay / CoturnTurnRelay / RatholeUdpTunnel) + CGNAT override + TOML/TURN config hygiene.
        // fails_when: Symmetric/CGNAT fails to require relay, UPnP+CGNAT contradiction is missed, or TOML/TURN configs omit placeholders.
        // seam: DirectPlayRelayCoordinator.PlanTransport, BuildRatholeClientToml, BuildRatholeServerToml, BuildCoturnAllocationProfile.
        StunProbeResult symStun = new StunProbeResult(
            NatBehaviorClassification.SymmetricOrCgnat,
            new IPEndPoint(IPAddress.Parse("192.168.1.50"), 11550),
            new IPEndPoint(IPAddress.Parse("203.0.113.10"), 51001),
            new IPEndPoint(IPAddress.Parse("203.0.113.10"), 51002),
            UsedXorMappedAddress: true,
            IsCgnatAddress: false,
            PrimaryRttMs: 14.2,
            SecondaryRttMs: 18.5,
            RejectedSpoofedPackets: 0,
            DiagnosticMessage: "Symmetric NAT");

        // 1. COMPEL UDP proxy preferred when configured under Symmetric NAT
        DirectPlayTransportPlan compelPlan = DirectPlayRelayCoordinator.PlanTransport(
            hostPort: 11550,
            localIp: "192.168.1.50",
            upnpResult: null,
            stunResult: symStun,
            options: new DirectPlayRelayOptions(CompelProxyHost: "relay.kongor.example", CompelProxyPort: 20450));

        compelPlan.Strategy.Should().Be(DirectPlayTransportStrategy.CompelUdpProxyRelay);
        compelPlan.RequiresRelay.Should().BeTrue();
        compelPlan.RecommendedInviteAddress.Should().Be("relay.kongor.example:20450");

        // 2. coturn TURN selected when configured without COMPEL proxy (UDP for Symmetric, TLS/TCP for UdpBlocked)
        DirectPlayTransportPlan turnUdpPlan = DirectPlayRelayCoordinator.PlanTransport(
            11550,
            "192.168.1.50",
            upnpResult: null,
            stunResult: symStun,
            options: new DirectPlayRelayOptions(CoturnHost: "turn.kongor.example", CoturnPort: 3478, CoturnTlsPort: 5349));

        turnUdpPlan.Strategy.Should().Be(DirectPlayTransportStrategy.CoturnTurnRelay);
        turnUdpPlan.TurnServerUri.Should().Be("turn:turn.kongor.example:3478?transport=udp");
        turnUdpPlan.TurnAllocationProfile.Should().Contain("${KONGOR_TURN_SECRET}");

        StunProbeResult blockedStun = symStun with { Classification = NatBehaviorClassification.UdpBlocked };
        DirectPlayTransportPlan turnTlsPlan = DirectPlayRelayCoordinator.PlanTransport(
            11550,
            "192.168.1.50",
            upnpResult: null,
            stunResult: blockedStun,
            options: new DirectPlayRelayOptions(CoturnHost: "turn.kongor.example", CoturnPort: 3478, CoturnTlsPort: 5349));

        turnTlsPlan.Strategy.Should().Be(DirectPlayTransportStrategy.CoturnTurnRelay);
        turnTlsPlan.TurnServerUri.Should().Be("turns:turn.kongor.example:5349?transport=tcp");

        // 3. rathole UDP tunnel generated for CGNAT even if home router UPnP mapped a 100.64.x.x WAN IP
        UpnpPortMappingResult cgnatUpnp = new UpnpPortMappingResult(
            Succeeded: true,
            Protocol: "UDP",
            ExternalPort: 11550,
            InternalPort: 11550,
            GatewayAddress: "192.168.1.1",
            ExternalIpAddress: "100.64.20.5",
            ServiceType: "urn:schemas-upnp-org:service:WANIPConnection:1",
            DiagnosticMessage: "UPnP mapped behind CGNAT");

        DirectPlayTransportPlan ratholePlan = DirectPlayRelayCoordinator.PlanTransport(
            11550,
            "192.168.1.50",
            upnpResult: cgnatUpnp,
            stunResult: null,
            options: new DirectPlayRelayOptions(RatholeServerHost: "tunnel.kongor.example\r\n\"evil\"", RatholeControlPort: 2333, RatholePublicUdpPort: 20550));

        ratholePlan.NatClassification.Should().Be(NatBehaviorClassification.SymmetricOrCgnat);
        ratholePlan.Strategy.Should().Be(DirectPlayTransportStrategy.RatholeUdpTunnel);
        ratholePlan.RequiresRelay.Should().BeTrue();
        ratholePlan.RatholeClientToml.Should().Contain("local_addr = \"127.0.0.1:11550\"");
        ratholePlan.RatholeClientToml.Should().Contain("local_addr = \"127.0.0.1:12050\"");
        ratholePlan.RatholeClientToml.Should().Contain("${KONGOR_RATHOLE_TOKEN}");
        ratholePlan.RatholeClientToml.Should().NotContain("evil\"\r");
        ratholePlan.RatholeServerToml.Should().Contain("bind_addr = \"0.0.0.0:20550\"");
        ratholePlan.RatholeServerToml.Should().Contain("bind_addr = \"0.0.0.0:21050\"");
    }

    [Fact]
    public void AiNoiseSuppressionProcessor_RnNoiseBridge_ReportsHonestFallbackAndFramesExact480SamplesWhenNativeActive()
    {
        // protects: Honest RNNoise backend telemetry (never claiming native GRU on fallback) + exact 480-sample 48 kHz framing & lifecycle.
        // fails_when: Fallback reports RnNoiseNativeGru, 960-sample Opus frame is not sliced into 2x480 native calls, or native state leaks on Reset/Dispose.
        // seam: RnNoiseNativeBridge + AiNoiseSuppressionProcessor + VoiceClientService telemetry.

        // 1. Explicit unavailable bridge reports honest SpectralGatingFallback
        List<string> fallbackLogs = [];
        using (AiNoiseSuppressionProcessor fallbackProc = new AiNoiseSuppressionProcessor(fallbackLogs.Add, preferNativeRnNoise: false))
        {
            fallbackProc.ActiveBackend.Should().Be(NoiseSuppressionBackend.SpectralGatingFallback);
            fallbackProc.IsNativeRnNoiseActive.Should().BeFalse();
            fallbackLogs.Should().Contain(l => l.Contains("[VOICE-RNNOISE]", StringComparison.Ordinal) && l.Contains("SpectralGatingFallback", StringComparison.Ordinal));

            short[] frame960 = new short[960];
            for (int i = 0; i < frame960.Length; i++)
            {
                frame960[i] = (short)((i % 2 == 0) ? 400 : -400);
            }

            fallbackProc.ProcessFrame(frame960, sampleRate: 48000, enableNoiseSuppression: true);
            fallbackProc.NativeFramesProcessed.Should().Be(0);
            fallbackProc.FallbackFramesProcessed.Should().Be(1);
        }

        // 2. Incompatible native frame size (!= 480) is rejected safely to SpectralGatingFallback
        FakeRnNoiseNativeApi badFrameApi = new FakeRnNoiseNativeApi { ExpectedFrameSize = 256 };
        using (RnNoiseNativeBridge rejectedBridge = RnNoiseNativeBridge.CreateWithApi(badFrameApi))
        {
            rejectedBridge.IsNativeLoaded.Should().BeFalse();
            badFrameApi.CreateCount.Should().Be(0);
        }

        // 3. Valid native RNNoise bridge processes 960 samples (20 ms @ 48 kHz) as TWO exact 480-sample float32 frames
        FakeRnNoiseNativeApi fakeApi = new FakeRnNoiseNativeApi { ReturnedVad = 0.91f, OutputScaleFactor = 0.5f };
        List<string> nativeLogs = [];
        using (RnNoiseNativeBridge nativeBridge = RnNoiseNativeBridge.CreateWithApi(fakeApi, nativeLogs.Add))
        using (AiNoiseSuppressionProcessor nativeProc = new AiNoiseSuppressionProcessor(nativeBridge, nativeLogs.Add))
        {
            nativeProc.ActiveBackend.Should().Be(NoiseSuppressionBackend.RnNoiseNativeGru);
            nativeProc.IsNativeRnNoiseActive.Should().BeTrue();
            fakeApi.CreateCount.Should().Be(2); // Initial create + constructor Reset() recreating clean GRU state

            short[] opusFrame960 = new short[960];
            for (int i = 0; i < opusFrame960.Length; i++)
            {
                opusFrame960[i] = 2000;
            }

            nativeProc.ProcessFrame(opusFrame960, sampleRate: 48000, volumePercent: 100f, gainDb: 0f, enableNoiseSuppression: true);

            fakeApi.ProcessCallCount.Should().Be(2);
            fakeApi.LastObservedFrameLength.Should().Be(480);
            fakeApi.LastObservedMaxAbsInput.Should().BeApproximately(2000f, 0.01f);
            nativeProc.NativeFramesProcessed.Should().Be(2);
            nativeProc.FallbackFramesProcessed.Should().Be(0);
            nativeProc.LastSpeechProbability.Should().BeApproximately(0.91f, 0.001f);
            opusFrame960[0].Should().Be(1000);
            opusFrame960[959].Should().Be(1000);

            // Partial 600-sample frame -> 1x480 native + 120-sample spectral fallback tail
            short[] partial600 = new short[600];
            Array.Fill(partial600, (short)1000);
            nativeProc.ProcessFrame(partial600, sampleRate: 48000, enableNoiseSuppression: true);
            nativeProc.NativeFramesProcessed.Should().Be(3);
            nativeProc.FallbackFramesProcessed.Should().Be(1);

            // Non-48 kHz frame routes cleanly to fallback without calling 48 kHz RNNoise ABI
            short[] narrow16k = new short[480];
            Array.Fill(narrow16k, (short)1000);
            nativeProc.ProcessFrame(narrow16k, sampleRate: 16000, enableNoiseSuppression: true);
            nativeProc.NativeFramesProcessed.Should().Be(3);
            nativeProc.FallbackFramesProcessed.Should().Be(2);
        }

        // Verify zero native DenoiseState leaks (every CreateState balanced by DestroyState)
        fakeApi.DestroyCount.Should().Be(fakeApi.CreateCount);

        // 4. VoiceClientService surfaces honest backend telemetry cleanly
        using VoiceClientService voiceService = new VoiceClientService(localAccountId: 42);
        voiceService.NoiseSuppressionDiagnostic.Should().NotBeNullOrWhiteSpace();
        voiceService.IsNativeRnNoiseActive.Should().Be(voiceService.ActiveNoiseSuppressionBackend == NoiseSuppressionBackend.RnNoiseNativeGru);
    }
}

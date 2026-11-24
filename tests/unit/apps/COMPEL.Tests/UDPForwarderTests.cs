// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using FluentAssertions;

namespace COMPEL.Tests;

/// <summary>
///     Exercises the proxy forwarder over loopback: that datagrams are relayed to the server and back, that a client is
///     issued a challenge, and that each renewal carries a strictly greater value.
///     This validates the challenge wire format (which cannot be tested against a live client here) end to end against the
///     real forwarder.
///     The tests run serially and tolerate the occasional loopback datagram drop by retrying, so they do not flake under
///     load.
/// </summary>
[Collection("SequentialUDPForwarder")]
public sealed class UDPForwarderTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromMilliseconds(250);

    private static byte[] GameDatagram(uint challenge, ushort counter)
    {
        byte[] datagram = new byte[ClientPacketReader.MinimumLength(ProxyForwarderKind.Game)];
        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(ClientPacketReader.ChallengeOffset), challenge);
        BinaryPrimitives.WriteUInt16LittleEndian(datagram.AsSpan(ClientPacketReader.CounterOffset), counter);
        return datagram;
    }

    [Fact]
    public async Task A_Datagram_Is_Relayed_To_The_Server_And_The_Reply_Is_Relayed_Back_To_The_Client()
    {
        int publicPort = FreeUDPPort();
        int localPort = FreeUDPPort();

        // The Server The Forwarder Relays To.
        using Socket server = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, localPort));

        using UDPForwarder forwarder = new(publicPort, localPort, NullLogger.Instance);

        using CancellationTokenSource lifetime = new();
        Task run = forwarder.Run(lifetime.Token);

        try
        {
            using Socket client = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            IPEndPoint publicEndPoint = new(IPAddress.Loopback, publicPort);
            byte[] hello = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0);
            byte[] pong = Encoding.UTF8.GetBytes("PONG");

            bool relayedToServer = false;
            bool relayedToClient = false;
            bool challenged = false;

            // Loopback UDP Can Occasionally Drop A Datagram, So The Exchange Is Retried; Each Attempt Only Needs To Observe The Behaviours Not Already Seen. Forcing A Challenge After The Session Exists Recovers A Dropped Initial Challenge.
            for (int attempt = 0;
                 attempt < 4 && (relayedToServer is false || relayedToClient is false || challenged is false);
                 attempt++)
            {
                await client.SendToAsync(hello, SocketFlags.None, publicEndPoint);

                (byte[] Payload, EndPoint Sender)? fromServer = await TryReceive(server);

                if (fromServer is null)
                    continue;

                relayedToServer |= fromServer.Value.Payload.SequenceEqual(hello);

                await server.SendToAsync(pong, SocketFlags.None, fromServer.Value.Sender);

                forwarder.ChallengeActiveSessions();

                while (relayedToClient is false || challenged is false)
                {
                    (byte[] Payload, EndPoint Sender)? datagram = await TryReceive(client);

                    if (datagram is null)
                        break;

                    if (IsChallenge(datagram.Value.Payload))
                        challenged = true;

                    else if (datagram.Value.Payload.SequenceEqual(pong))
                        relayedToClient = true;
                }
            }

            Assert.True(relayedToServer);
            Assert.True(relayedToClient);
            Assert.True(challenged);
        }
        finally
        {
            await lifetime.CancelAsync();

            try
            {
                await run;
            }
            catch (Exception)
            {
            }
        }
    }

    [Fact]
    public async Task Unwatermarked_K2_Connect_Datagram_Is_Relayed_Without_Challenge_Packets_When_Watermark_Disabled()
    {
        int publicPort = FreeUDPPort();
        int localPort = FreeUDPPort();

        using Socket server = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, localPort));

        TimeProvider clock = TimeProvider.System;
        using UDPForwarder forwarder = new(
            publicPort,
            localPort,
            ProxyForwarderKind.Game,
            TimeSpan.FromSeconds(10),
            new ViolationScoreContainer(clock),
            new AttackIndicatorContainer(clock),
            clock,
            NullLogger.Instance,
            enableLegacyWatermarkChallenge: false);

        using CancellationTokenSource lifetime = new();
        Task run = forwarder.Run(lifetime.Token);

        try
        {
            using Socket client = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            IPEndPoint publicEndPoint = new(IPAddress.Loopback, publicPort);
            // 12-byte un-watermarked K2 Connect packet (0xC9) with net_enableWatermark = false
            byte[] connectPacket = [0x01, 0x00, 0x01, 0xC9, 0x35, 0x2E, 0x30, 0x2E, 0x30, 0x2E, 0x30, 0x00];
            byte[] serverAccept = [0x01, 0x00, 0x01, 0xCA, 0x01, 0x00];

            bool relayedToServer = false;
            bool relayedToClient = false;
            bool receivedUnexpectedChallenge = false;

            for (int attempt = 0; attempt < 4 && (!relayedToServer || !relayedToClient); attempt++)
            {
                await client.SendToAsync(connectPacket, SocketFlags.None, publicEndPoint);

                (byte[] Payload, EndPoint Sender)? fromServer = await TryReceive(server);
                if (fromServer is null)
                    continue;

                relayedToServer |= fromServer.Value.Payload.SequenceEqual(connectPacket);
                await server.SendToAsync(serverAccept, SocketFlags.None, fromServer.Value.Sender);

                forwarder.RotateChallenges();
                forwarder.RepeatChallenges();

                while (true)
                {
                    (byte[] Payload, EndPoint Sender)? datagram = await TryReceive(client);
                    if (datagram is null)
                        break;

                    if (IsChallenge(datagram.Value.Payload))
                        receivedUnexpectedChallenge = true;
                    else if (datagram.Value.Payload.SequenceEqual(serverAccept))
                    {
                        relayedToClient = true;
                        break;
                    }
                }
            }

            Assert.True(relayedToServer);
            Assert.True(relayedToClient);
            Assert.False(receivedUnexpectedChallenge);
            Assert.Equal(0, forwarder.DroppedDatagramCount);
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await run; }
            catch (Exception) { }
        }
    }

    [Fact]
    public async Task Each_Challenge_Renewal_Carries_A_Strictly_Greater_Value()
    {
        int publicPort = FreeUDPPort();
        int localPort = FreeUDPPort();

        using UDPForwarder forwarder = new(publicPort, localPort, NullLogger.Instance);

        using CancellationTokenSource lifetime = new();
        Task run = forwarder.Run(lifetime.Token);

        try
        {
            using Socket client = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            // Establishing A Session Triggers The Initial Challenge; retry until established
            uint initialChallenge = 0;
            for (int attempt = 0; attempt < 5 && initialChallenge == 0; attempt++)
            {
                await client.SendToAsync(GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0), SocketFlags.None,
                    new IPEndPoint(IPAddress.Loopback, publicPort));
                var pkt = await TryReceive(client);
                if (pkt is not null && IsChallenge(pkt.Value.Payload))
                {
                    initialChallenge = ChallengeValue(pkt.Value.Payload);
                }
            }

            // Flush Any Challenges Buffered From Session Creation So The Two Values Compared Below Are Read In Issue Order.
            await DrainUntilIdle(client);

            forwarder.ChallengeActiveSessions();
            uint firstValue = await ReadOneChallengeValue(forwarder, client);

            forwarder.ChallengeActiveSessions();
            uint secondValue = await ReadOneChallengeValue(forwarder, client);

            Assert.NotEqual((uint)0, firstValue);
            Assert.NotEqual(firstValue, secondValue);
        }
        finally
        {
            await lifetime.CancelAsync();

            try
            {
                await run;
            }
            catch (Exception)
            {
            }
        }
    }

    [Fact]
    public async Task Multiple_Clients_Can_Relay_Concurrently_Without_Socket_Exceptions()
    {
        int publicPort = FreeUDPPort();
        int localPort = FreeUDPPort();

        using Socket server = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, localPort));

        using UDPForwarder forwarder = new(publicPort, localPort, NullLogger.Instance);
        using CancellationTokenSource lifetime = new();
        Task run = forwarder.Run(lifetime.Token);

        try
        {
            const int clientCount = 5;
            Socket[] clients = new Socket[clientCount];
            IPEndPoint publicEndPoint = new(IPAddress.Loopback, publicPort);

            for (int i = 0; i < clientCount; i++)
            {
                clients[i] = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                clients[i].Bind(new IPEndPoint(IPAddress.Loopback, 0));
            }

            try
            {
                Dictionary<int, EndPoint> clientUpstreams = [];

                for (int attempt = 0; attempt < 5 && clientUpstreams.Count < clientCount; attempt++)
                {
                    for (int i = 0; i < clientCount; i++)
                    {
                        if (!clientUpstreams.ContainsKey(i))
                        {
                            byte[] hello = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, (ushort)i);
                            await clients[i].SendToAsync(hello, SocketFlags.None, publicEndPoint);
                        }
                    }

                    while (clientUpstreams.Count < clientCount)
                    {
                        (byte[] Payload, EndPoint Sender)? received = await TryReceive(server);
                        if (received is null) break;
                        if (received.Value.Payload.Length >= ClientPacketReader.MinimumLength(ProxyForwarderKind.Game))
                        {
                            ushort idx = BinaryPrimitives.ReadUInt16LittleEndian(received.Value.Payload.AsSpan(ClientPacketReader.CounterOffset));
                            clientUpstreams[idx] = received.Value.Sender;
                        }
                    }
                }

                Assert.True(clientUpstreams.Count > 0);

                // Server responds concurrently to all active client sessions
                Task[] sendTasks = clientUpstreams.Select(kvp =>
                {
                    byte[] reply = Encoding.UTF8.GetBytes($"REPLY_{kvp.Key}");
                    return (Task)server.SendToAsync(reply, SocketFlags.None, kvp.Value);
                }).ToArray();

                await Task.WhenAll(sendTasks);

                // Verify clients receive without exceptions
                bool anyRelayed = false;
                for (int i = 0; i < clientCount; i++)
                {
                    while (true)
                    {
                        (byte[] Payload, EndPoint Sender)? datagram = await TryReceive(clients[i]);
                        if (datagram is null) break;
                        if (!IsChallenge(datagram.Value.Payload))
                        {
                            anyRelayed = true;
                            break;
                        }
                    }
                }

                Assert.True(anyRelayed);
            }
            finally
            {
                foreach (Socket c in clients)
                {
                    c.Dispose();
                }
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            try
            {
                await run;
            }
            catch (Exception)
            {
            }
        }
    }

    private static bool IsChallenge(byte[] datagram)
    {
        if (datagram.Length == 18)
            return datagram[0] is 0xFF && datagram[1] is 0xFF && (datagram[2] & 0x40) is not 0 && datagram[3] is 0x00;

        return datagram.Length >= 58 && datagram[40] is 0xFF && datagram[41] is 0xFF &&
               (datagram[42] & 0x40) is not 0 && datagram[43] is 0x00;
    }

    private static uint ChallengeValue(byte[] datagram)
    {
        int offset = datagram.Length == 18 ? 0 : 40;
        return BinaryPrimitives.ReadUInt32LittleEndian(datagram.AsSpan(offset + 14));
    }

    private static async Task<uint> ReadOneChallengeValue(UDPForwarder forwarder, Socket client)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            (byte[] Payload, EndPoint Sender)? datagram = await TryReceive(client);

            if (datagram is not null && IsChallenge(datagram.Value.Payload))
                return ChallengeValue(datagram.Value.Payload);

            // Nothing Usable Arrived (Idle Timeout Or A Dropped Challenge); Re-Issue And Try Again.
            forwarder.ChallengeActiveSessions();
        }

        throw new InvalidOperationException("No Challenge Packet Was Received");
    }

    private static async Task DrainUntilIdle(Socket socket)
    {
        while (socket.Available > 0) await TryReceive(socket);
    }

    private static async Task<(byte[] Payload, EndPoint Sender)?> TryReceive(Socket socket)
    {
        byte[] buffer = new byte[65535];
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);

        using CancellationTokenSource timeout = new(ReceiveTimeout);

        try
        {
            SocketReceiveFromResult result =
                await socket.ReceiveFromAsync(buffer, SocketFlags.None, sender, timeout.Token);

            return (buffer[..result.ReceivedBytes], result.RemoteEndPoint);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static int FreeUDPPort()
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return probe.LocalEndPoint is IPEndPoint endpoint
            ? endpoint.Port
            : throw new InvalidOperationException("Could Not Determine A Free UDP Port");
    }

    private static async Task<byte[]> ReadOneChallengePacket(UDPForwarder forwarder, Socket client)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            (byte[] Payload, EndPoint Sender)? datagram = await TryReceive(client);

            if (datagram is not null && IsChallenge(datagram.Value.Payload))
                return datagram.Value.Payload;

            forwarder.RotateChallenges();
        }

        throw new InvalidOperationException("No Challenge Packet Was Received");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task Forty_Spoofed_Violation_Datagrams_Do_Not_Refuse_A_Victims_Valid_Traffic()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        await using ForwarderProbe probe = new();

        uint challenge = await probe.Establish();
        (await probe.Relays(GameDatagram(challenge, counter: 0))).Should().BeTrue();

        for (int index = 0; index < 40; index++)
            await probe.Refuses(GameDatagram(challenge: 0xDEADBEEF, counter: (ushort)(index + 10)));

        (await probe.Relays(GameDatagram(challenge, counter: 1))).Should().BeTrue();
    }

    [Fact]
    public async Task When_The_Under_Attack_Indicator_Is_Active_A_Novel_Session_Is_Refused()
    {
        await using ForwarderProbe probe = new();

        probe.AttackIndicator.Charge(AttackIndicatorContainer.UnderAttackThreshold + 100);

        using Socket newClient = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        newClient.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        byte[] datagram = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0);

        (await probe.RefusesFrom(newClient, datagram)).Should().BeTrue();
    }

    [Fact]
    public async Task When_The_Under_Attack_Indicator_Is_Active_An_Existing_Session_Continues_To_Relay()
    {
        await using ForwarderProbe probe = new();

        uint challenge = await probe.Establish();
        (await probe.Relays(GameDatagram(challenge, counter: 0))).Should().BeTrue();

        probe.AttackIndicator.Charge(AttackIndicatorContainer.UnderAttackThreshold + 100);

        (await probe.Relays(GameDatagram(challenge, counter: 1))).Should().BeTrue();
    }

    [Fact]
    public async Task A_Forwarder_At_Its_Total_Session_Cap_Refuses_A_Novel_Session()
    {
        await using ForwarderProbe probe = new();

        List<Socket> clients = [];
        try
        {
            for (int ipIndex = 1; ipIndex <= 3; ipIndex++)
            {
                IPAddress ipAddress = IPAddress.Parse($"127.0.0.{ipIndex}");

                for (int sessionIndex = 0; sessionIndex < 8; sessionIndex++)
                {
                    Socket clientSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    clientSocket.Bind(new IPEndPoint(ipAddress, 0));
                    clients.Add(clientSocket);

                    byte[] datagram = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: (ushort)sessionIndex);
                    (await probe.RelaysFrom(clientSocket, datagram)).Should().BeTrue();
                }
            }

            using Socket client25 = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client25.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.4"), 0));

            byte[] overflowDatagram = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0);

            (await probe.RefusesFrom(client25, overflowDatagram)).Should().BeTrue();
        }
        finally
        {
            foreach (Socket clientSocket in clients)
                clientSocket.Dispose();
        }
    }

    [Fact]
    public async Task A_Forwarder_At_Its_Per_Address_Session_Cap_Refuses_A_Novel_Session_From_That_Address_While_Admitting_Another_Address()
    {
        await using ForwarderProbe probe = new();

        List<Socket> clients = [];
        try
        {
            probe.EnforcePublicAddressCapOnLoopback();
            IPAddress ipAddress1 = IPAddress.Parse("127.0.0.1");

            for (int sessionIndex = 0; sessionIndex < 10; sessionIndex++)
            {
                Socket clientSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                clientSocket.Bind(new IPEndPoint(ipAddress1, 0));
                clients.Add(clientSocket);

                byte[] datagram = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: (ushort)sessionIndex);
                (await probe.RelaysFrom(clientSocket, datagram)).Should().BeTrue();
            }

            using Socket client11 = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client11.Bind(new IPEndPoint(ipAddress1, 0));

            byte[] overflowDatagram = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0);
            (await probe.RefusesFrom(client11, overflowDatagram)).Should().BeTrue();

            using Socket clientOtherIP = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            clientOtherIP.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));

            byte[] otherIPDatagram = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0);
            (await probe.RelaysFrom(clientOtherIP, otherIPDatagram)).Should().BeTrue();
        }
        finally
        {
            foreach (Socket clientSocket in clients)
                clientSocket.Dispose();
        }
    }

    [Fact]
    public async Task A_Forwarder_Exempts_Non_Public_Addresses_From_Per_Address_Cap_Up_To_Total_Forwarder_Cap()
    {
        await using ForwarderProbe probe = new();

        List<Socket> clients = [];
        try
        {
            IPAddress loopback = IPAddress.Loopback;

            for (int sessionIndex = 0; sessionIndex < 24; sessionIndex++)
            {
                Socket clientSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                clientSocket.Bind(new IPEndPoint(loopback, 0));
                clients.Add(clientSocket);

                byte[] datagram = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: (ushort)sessionIndex);
                (await probe.RelaysFrom(clientSocket, datagram)).Should().BeTrue($"non-public socket #{sessionIndex + 1} should bypass MaxSessionsPerAddress=10 up to MaxSessionsPerForwarder=24");
            }

            using Socket client25 = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client25.Bind(new IPEndPoint(loopback, 0));
            byte[] overflowDatagram = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 24);
            (await probe.RefusesFrom(client25, overflowDatagram)).Should().BeTrue("total forwarder cap (24) must still refuse the 25th session even for non-public sources");
        }
        finally
        {
            foreach (Socket clientSocket in clients)
                clientSocket.Dispose();
        }
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.255.255.254", false)]
    [InlineData("::1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("10.255.255.254", false)]
    [InlineData("172.15.255.255", true)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.254", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.50", false)]
    [InlineData("192.169.1.1", true)]
    [InlineData("169.254.10.20", false)]
    [InlineData("100.63.255.255", true)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.100.100.100", false)]
    [InlineData("100.127.255.254", false)]
    [InlineData("100.128.0.1", true)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456:789a::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:192.168.1.5", false)]
    [InlineData("::ffff:100.100.1.2", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void NetworkAddressClassification_IsPublicRoutable_ClassifiesAllCidrBoundariesCorrectly(string ipText, bool expectedPublic)
    {
        IPAddress parsed = IPAddress.Parse(ipText);
        NetworkAddressClassification.IsPublicRoutable(parsed).Should().Be(expectedPublic);
    }

    private sealed class ForwarderProbe : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task run;
        private readonly Socket server;
        private readonly Socket client;
        private readonly IPEndPoint publicEndPoint;
        private bool enforcePublicAddressCap;

        internal void EnforcePublicAddressCapOnLoopback() => enforcePublicAddressCap = true;

        internal ForwarderProbe(TimeProvider? timeProvider = null)
        {
            TimeProvider clock = timeProvider ?? TimeProvider.System;

            int publicPort = FreeUDPPort();

            server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            server.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            int localPort = server.LocalEndPoint is IPEndPoint boundServer ? boundServer.Port : throw new InvalidOperationException("Could Not Determine The Bound UDP Port");

            Scores = new ViolationScoreContainer(clock);
            AttackIndicator = new AttackIndicatorContainer(clock);
            Forwarder = new UDPForwarder(
                publicPort,
                localPort,
                ProxyForwarderKind.Game,
                TimeSpan.FromSeconds(10),
                Scores,
                AttackIndicator,
                clock,
                NullLogger.Instance,
                isPublicAddress: address => enforcePublicAddressCap || NetworkAddressClassification.IsPublicRoutable(address));

            run = Forwarder.Run(lifetime.Token);

            client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            publicEndPoint = new IPEndPoint(IPAddress.Loopback, publicPort);
        }

        internal UDPForwarder Forwarder { get; }
        internal ViolationScoreContainer Scores { get; }
        internal AttackIndicatorContainer AttackIndicator { get; }
        internal ClientChallengeStoreDouble ClientChallenges { get; } = new();
        internal Socket Server => server;
        internal Socket Client => client;
        internal IPEndPoint PublicEndPoint => publicEndPoint;

        internal async Task<bool> Relays(byte[] datagram) => await RelaysFrom(client, datagram);

        internal async Task<bool> RelaysFrom(Socket senderSocket, byte[] datagram)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await senderSocket.SendToAsync(datagram, SocketFlags.None, publicEndPoint);
                if (await TryReceive(server) is not null)
                    return true;
            }
            return false;
        }

        internal async Task<bool> Refuses(byte[] datagram) => await RefusesFrom(client, datagram);

        internal async Task<bool> RefusesFrom(Socket senderSocket, byte[] datagram)
        {
            long refusedBefore = Forwarder.DroppedDatagramCount;
            await senderSocket.SendToAsync(datagram, SocketFlags.None, publicEndPoint);

            long start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 500)
            {
                if (Forwarder.DroppedDatagramCount > refusedBefore)
                {
                    await Task.Delay(10);
                    return server.Available == 0;
                }
                await Task.Delay(5);
            }
            return server.Available == 0 && Forwarder.DroppedDatagramCount > refusedBefore;
        }

        internal async Task<uint> Establish()
        {
            await Relays(GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0));
            byte[] packet = await ReadOneChallengePacket(Forwarder, client);
            ClientChallenges.TryProcessChallengePacket(publicEndPoint, packet);
            return ChallengeValue(packet);
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            try { await run; }
            catch (OperationCanceledException) { }

            client.Dispose();
            Forwarder.Dispose();
            server.Dispose();
            lifetime.Dispose();
        }
    }
}

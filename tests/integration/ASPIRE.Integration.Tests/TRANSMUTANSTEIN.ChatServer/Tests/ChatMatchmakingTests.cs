// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Sockets;
using ASPIRE.Common.Configuration.Matchmaking;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public class ChatMatchmakingTests(TRANSMUTANSTEINTestFixture fixture)
{
    [Trait("Category", "Manual")]
    [Fact]
    public async Task Matchmaking_WithIdleServer_ShouldConnectClients()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // 1. Get Application Instance First (ensures JSONConfiguration static constructor completes)
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        MatchmakingConfiguration originalConfig = SharedConfiguration.MatchmakingConfiguration;
        MatchmakingConfiguration mockConfig = new()
        {
            Ranked =
                new MatchmakingMapConfiguration
                {
                    Map = "caldavar",
                    Modes = ["ap", "sd"],
                    Regions = ["USE"],
                    Match =
                        new MatchConfiguration { MaximumPlayerRatingDifference = 500, IsRanked = true, TeamSize = 1 }
                },
            Unranked =
                new MatchmakingMapConfiguration
                {
                    Map = "mock_unranked",
                    Modes = ["ar"],
                    Regions = ["USW"],
                    Match =
                        new MatchConfiguration { MaximumPlayerRatingDifference = 0, IsRanked = false, TeamSize = 1 }
                },
            MidWars = new MatchmakingMapConfiguration
            {
                Map = "midwars",
                Modes = ["bd"],
                Regions = ["EU"],
                Match =
                    new MatchConfiguration { MaximumPlayerRatingDifference = 0, IsRanked = false, TeamSize = 1 }
            },
            RiftWars = new MatchmakingMapConfiguration
            {
                Map = "riftwars_test",
                Modes = ["bp"],
                Regions = ["AU"],
                Match = new MatchConfiguration { MaximumPlayerRatingDifference = 0, IsRanked = false, TeamSize = 1 }
            }
        };

        SharedConfiguration.MatchmakingConfiguration = mockConfig;

        try
        {
            // 2. Setup Game Server Mock
            int serverId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
            const int gamePort = 11235;
            string serverCookie = $"mock_server_cookie_{serverId}";
            int hostAccountId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
            int leaderId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
            int memberId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);

            await ChatTestHelpers.SeedGameServerHostAsync(app, hostAccountId, serverId, serverCookie);

            // 3. Connect Game Server
            using TcpClient gameServerClient = await ChatTestHelpers.ConnectAndAuthenticateGameServerAsync(
                app, app.MatchServerPort, serverId, serverCookie, gamePort: gamePort);

            NetworkStream gameServerStream = gameServerClient.GetStream();

            // 4. Connect Clients (Leader and Member)
            using TcpClient leader =
                await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, leaderId, "LeaderUser");
            using TcpClient member =
                await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, memberId, "MemberUser");

            NetworkStream leaderStream = leader.GetStream();
            NetworkStream memberStream = member.GetStream();

            // 5. Leader Creates Group
            ChatBuffer createBuffer = new();
            createBuffer.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_CREATE);
            createBuffer.WriteString("4.10.1.0");
            createBuffer.WriteInt8((byte)ChatProtocol.TMMType.TMM_TYPE_PVP);
            createBuffer.WriteInt8((byte)ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL);
            createBuffer.WriteString("caldavar");
            createBuffer.WriteString("ap");
            createBuffer.WriteString("USE");
            createBuffer.WriteBool(true); // Ranked
            createBuffer.WriteInt8(0);
            createBuffer.WriteInt8(0);
            createBuffer.WriteBool(false);
            await ChatTestHelpers.SendPacketAsync(leaderStream, createBuffer);

            // Wait for group create
            await ChatTestHelpers.ExpectCommandAsync(leaderStream,
                ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_UPDATE);

            // 6. Member Creates Group
            ChatBuffer memberCreateBuffer = new();
            memberCreateBuffer.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_CREATE);
            memberCreateBuffer.WriteString("4.10.1.0");
            memberCreateBuffer.WriteInt8((byte)ChatProtocol.TMMType.TMM_TYPE_PVP);
            memberCreateBuffer.WriteInt8((byte)ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL);
            memberCreateBuffer.WriteString("caldavar");
            memberCreateBuffer.WriteString("ap");
            memberCreateBuffer.WriteString("USE");
            memberCreateBuffer.WriteBool(true); // Ranked
            memberCreateBuffer.WriteInt8(0);
            memberCreateBuffer.WriteInt8(0);
            memberCreateBuffer.WriteBool(false);
            await ChatTestHelpers.SendPacketAsync(memberStream, memberCreateBuffer);

            // Wait for group create
            await ChatTestHelpers.ExpectCommandAsync(memberStream,
                ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_UPDATE);

            // 7. Set Loading & Ready (Both)
            ChatBuffer loading = new();
            loading.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_PLAYER_LOADING_STATUS);
            loading.WriteInt8(100);

            ChatBuffer ready = new();
            ready.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_PLAYER_READY_STATUS);
            ready.WriteInt8(1);
            ready.WriteInt8((byte)ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL);

            await ChatTestHelpers.SendPacketAsync(leaderStream, loading);
            await ChatTestHelpers.SendPacketAsync(memberStream, loading);

            await ChatTestHelpers.SendPacketAsync(leaderStream, ready);
            await ChatTestHelpers.SendPacketAsync(memberStream, ready);

            // 9. Match Broker Trigger: Wait for GS to receive CREATE_MATCH
            ChatBuffer createMatchPacket = await ChatTestHelpers.ExpectCommandAsync(gameServerStream,
                ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH);

            byte matchTypeRx = createMatchPacket.ReadInt8();
            int matchUpId = createMatchPacket.ReadInt32();

            // 10. Simulate GS sending ANNOUNCE_MATCH (0x0503)
            ChatBuffer announceMatch = new();
            announceMatch.WriteCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_ANNOUNCE_MATCH);
            announceMatch.WriteInt32(matchUpId); // MatchUpID from CreateMatch
            announceMatch.WriteInt32(0); // Challenge
            announceMatch.WriteInt32(0); // GroupCount (Ignored by logic for now)
            announceMatch.WriteInt32(123456); // Real MatchID generated by GS

            await ChatTestHelpers.SendPacketAsync(gameServerStream, announceMatch);

            // 11. Now verify Clients receive match found
            await VerifyMatchFoundAsync(leaderStream, "127.0.0.1", gamePort);
            await VerifyMatchFoundAsync(memberStream, "127.0.0.1", gamePort);
        }
        finally
        {
            SharedConfiguration.MatchmakingConfiguration = originalConfig;
        }
    }

    private static async Task VerifyMatchFoundAsync(NetworkStream stream, string expectedIp, int expectedPort)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        int packetsFound = 0;

        while (!cts.Token.IsCancellationRequested)
        {
            byte[] header = new byte[4];
            int read = 0;
            while (read < 4)
            {
                int r = await stream.ReadAsync(header, read, 4 - read, cts.Token);
                if (r == 0) throw new EndOfStreamException();

                read += r;
            }

            ushort length = BitConverter.ToUInt16(header, 0);
            ushort command = BitConverter.ToUInt16(header, 2);

            int payloadSize = length - 2;
            byte[] payload = new byte[payloadSize];
            if (payloadSize > 0)
            {
                read = 0;
                while (read < payloadSize)
                {
                    int r = await stream.ReadAsync(payload, read, payloadSize - read, cts.Token);
                    if (r == 0) throw new EndOfStreamException();

                    read += r;
                }
            }

            ChatBuffer reader = new(payload);

            if (command == ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_MATCH_FOUND_UPDATE)
            {
                string mapName = reader.ReadString();
                byte teamSize = reader.ReadInt8();
                byte gameType = reader.ReadInt8();
                string modes = reader.ReadString();
                string regions = reader.ReadString();
                string unknown = reader.ReadString();

                packetsFound++;
            }
            else if (command == ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_QUEUE_UPDATE)
            {
                byte updateType = reader.ReadInt8();
                packetsFound++;
            }
            else if (command == 0x0062) // NET_CHAT_CL_AUTO_MATCH_CONNECT
            {
                byte matchType = reader.ReadInt8();
                int matchId = reader.ReadInt32();
                string ip = reader.ReadString();
                short port = reader.ReadInt16();
                int unknownRandom = reader.ReadInt32();

                Assert.Equal(expectedIp, ip);
                Assert.Equal((short)expectedPort, port);
                Assert.True(packetsFound >= 0);
                return;
            }
        }

        Assert.Fail("Did not receive NET_CHAT_CL_AUTO_MATCH_CONNECT (0x0062)");
    }
}

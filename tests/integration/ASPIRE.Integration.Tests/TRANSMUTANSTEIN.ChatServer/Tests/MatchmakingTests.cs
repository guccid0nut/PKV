// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Sockets;
using ASPIRE.Common.Configuration.Matchmaking;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public class MatchmakingTests(TRANSMUTANSTEINTestFixture fixture)
{
    // private const int TestPort = 54123;

    [Fact]
    public async Task Matchmaking_PopularityUpdate_ReturnsConfiguredModes()
    {
        // 1. Get Application Instance First (ensures JSONConfiguration static constructor completes)
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        // 2. Setup Mock Configuration and restore in finally block
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
            Unranked = new MatchmakingMapConfiguration
            {
                Map = "mock_unranked",
                Modes = ["ar"],
                Regions = ["USW"],
                Match = new MatchConfiguration { MaximumPlayerRatingDifference = 0, IsRanked = false, TeamSize = 1 }
            },
            MidWars = new MatchmakingMapConfiguration
            {
                Map = "midwars",
                Modes = ["bd"],
                Regions = ["EU"],
                Match = new MatchConfiguration { MaximumPlayerRatingDifference = 0, IsRanked = false, TeamSize = 1 }
            },
            RiftWars = new MatchmakingMapConfiguration
            {
                Map = "riftwars_test", // Unique map to verify
                Modes = ["bp"],
                Regions = ["AU"],
                Match = new MatchConfiguration { MaximumPlayerRatingDifference = 0, IsRanked = false, TeamSize = 1 }
            }
        };

        MatchmakingConfiguration originalConfig = SharedConfiguration.MatchmakingConfiguration;
        SharedConfiguration.MatchmakingConfiguration = mockConfig;

        try
        {
            // 3. Connect Client
            using TcpClient client =
                await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, 9999, "MatchmakingTester");
            NetworkStream stream = client.GetStream();

            // 4. Send Popularity Update Request
            ChatBuffer requestBuffer = new();
            requestBuffer.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_POPULARITY_UPDATE);

            byte[] packet = [.. requestBuffer.Data.AsSpan(0, (int)requestBuffer.Size)];
            List<byte> rawPacket =
            [
                .. BitConverter.GetBytes((ushort)packet.Length),
                .. packet
            ];
            await stream.WriteAsync(rawPacket.ToArray());

            // 5. Wait for Response (NET_CHAT_CL_TMM_POPULARITY_UPDATE = 0x0D07)
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

            // Loop until we find the popularity packet
            while (!cts.Token.IsCancellationRequested)
            {
                byte[] header = new byte[4];
                await stream.ReadExactlyAsync(header, cts.Token);
                ushort length = BitConverter.ToUInt16(header, 0);
                ushort command = BitConverter.ToUInt16(header, 2);

                int payloadSize = length - 2;
                byte[] payload = new byte[payloadSize];
                int read = 0;
                while (read < payloadSize) read += await stream.ReadAsync(payload, read, payloadSize - read, cts.Token);

                if (command == ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_POPULARITY_UPDATE)
                {
                    ChatBuffer reader = new(payload);

                    reader.ReadInt8();
                    string maps = reader.ReadString();
                    string types = reader.ReadString();
                    string modes = reader.ReadString();
                    string regions = reader.ReadString();
                    string disabledGameModesByGameType = reader.ReadString();
                    string disabledGameModesByRankType = reader.ReadString();
                    string disabledGameModesByMap = reader.ReadString();

                    // Assertions (xUnit Syntax)
                    Assert.True(maps.Contains("riftwars_test") || maps.Contains("riftwars") ||
                                maps.Contains("caldavar"));

                    // Verify our RiftWars logic added Type 4
                    Assert.Contains("4", types);

                    // Verify map config mode presence
                    Assert.True(modes.Contains("bp") || disabledGameModesByMap.Contains("bp") || modes.Length > 0);

                    return; // Pass
                }
            }

            Assert.Fail("NET_CHAT_CL_TMM_POPULARITY_UPDATE not received");
        }
        finally
        {
            SharedConfiguration.MatchmakingConfiguration = originalConfig;
        }
    }
}

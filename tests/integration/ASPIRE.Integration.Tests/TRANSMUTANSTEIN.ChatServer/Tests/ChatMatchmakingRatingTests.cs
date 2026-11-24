// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Sockets;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using MERRICK.DatabaseContext.Entities.Statistics;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public class ChatMatchmakingRatingTests(TRANSMUTANSTEINTestFixture fixture)
{
    [Fact]
    public async Task GroupCreate_WithSeededRating_ShouldReturnCorrectAverageRating()
    {
        // 1. Start Application
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int leaderId = 3001;
        const double leaderRating = 1650.0;

        // 2. Connect Leader
        using TcpClient leader =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, leaderId, "RatingLeader");

        // 3. Seed AccountStatistics
        await SeedAccountStatistics(app, leaderId, leaderRating);

        NetworkStream leaderStream = leader.GetStream();

        // 4. Leader Creates Group
        ChatBuffer createBuffer = new();
        createBuffer.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_CREATE);

        createBuffer.WriteString("4.10.1.0");
        createBuffer.WriteInt8((byte)ChatProtocol.TMMType.TMM_TYPE_PVP);
        createBuffer.WriteInt8((byte)ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL);
        createBuffer.WriteString("caldavar");
        createBuffer.WriteString("ap");
        createBuffer.WriteString("USE");
        createBuffer.WriteBool(true);
        createBuffer.WriteInt8(0);
        createBuffer.WriteInt8(0);
        createBuffer.WriteBool(false);

        await SendPacketAsync(leaderStream, createBuffer);

        // 5. Expect Group Update and Verify Rating
        await ExpectGroupUpdateAndVerifyRatingAsync(leaderStream, ChatProtocol.TMMUpdateType.TMM_CREATE_GROUP,
            leaderRating);
    }

    [Fact]
    public async Task GroupJoin_WithNonExistentGroup_ShouldReturnFailedToJoinResponse()
    {
        // 1. Start Application
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int memberId = 3004;

        // 2. Connect Member
        using TcpClient member =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, memberId, "JoinFailMember");

        NetworkStream memberStream = member.GetStream();

        // 3. Member Attempts to Join a Non-Existent Group
        ChatBuffer joinBuffer = new();
        joinBuffer.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_JOIN);
        joinBuffer.WriteString("4.10.1.0");
        joinBuffer.WriteString("NonExistentLeader"); // Leader Name

        await SendPacketAsync(memberStream, joinBuffer);

        // 4. Verify TMM_FAILED_TO_JOIN Response
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        bool failedToJoinReceived = false;

        while (!cts.Token.IsCancellationRequested)
        {
            byte[] header = new byte[4];
            int read = 0;
            while (read < 4)
            {
                int r = await memberStream.ReadAsync(header, read, 4 - read, cts.Token);
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
                    int r = await memberStream.ReadAsync(payload, read, payloadSize - read, cts.Token);
                    if (r == 0) throw new EndOfStreamException();

                    read += r;
                }
            }

            if (command == ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_FAILED_TO_JOIN)
            {
                ChatBuffer reader = new(payload);
                byte reason = reader.ReadInt8();

                if (reason != (byte)ChatProtocol.TMMFailedToJoinReason.TMMFTJR_BUSY)
                    throw new Exception(
                        $"Failure reason mismatch! Expected {(byte)ChatProtocol.TMMFailedToJoinReason.TMMFTJR_BUSY}, Got {reason}");

                failedToJoinReceived = true;
                break;
            }
        }

        if (!failedToJoinReceived) throw new Exception("Did not receive TMM_FAILED_TO_JOIN response.");
    }

    [Fact]
    public async Task GroupJoin_WithMultipleMembers_ShouldReturnCorrectAverageRating()
    {
        // 1. Start Application
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int leaderId = 3002;
        const double leaderRating = 1700.0;

        const int memberId = 3003;
        const double memberRating = 1300.0;

        const double expectedAverage = (leaderRating + memberRating) / 2.0; // 1500

        // 2. Connect Leader and Member
        using TcpClient leader =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, leaderId, "AvgLeader");
        using TcpClient member =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, memberId, "AvgMember");

        // 3. Seed Stats
        await SeedAccountStatistics(app, leaderId, leaderRating);
        await SeedAccountStatistics(app, memberId, memberRating);

        NetworkStream leaderStream = leader.GetStream();
        NetworkStream memberStream = member.GetStream();

        // 4. Leader Creates Group
        ChatBuffer createBuffer = new();
        createBuffer.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_CREATE);
        // ... (minimal payload)
        createBuffer.WriteString("4.10.1.0");
        createBuffer.WriteInt8((byte)ChatProtocol.TMMType.TMM_TYPE_PVP);
        createBuffer.WriteInt8((byte)ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL);
        createBuffer.WriteString("caldavar");
        createBuffer.WriteString("ap");
        createBuffer.WriteString("USE");
        createBuffer.WriteBool(true);
        createBuffer.WriteInt8(0);
        createBuffer.WriteInt8(0);
        createBuffer.WriteBool(false);

        await SendPacketAsync(leaderStream, createBuffer);
        await ExpectGroupUpdateAndVerifyRatingAsync(leaderStream, ChatProtocol.TMMUpdateType.TMM_CREATE_GROUP,
            leaderRating);

        // 5. Member Joins Group
        ChatBuffer joinBuffer = new();
        joinBuffer.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_JOIN);
        joinBuffer.WriteString("4.10.1.0");
        joinBuffer.WriteString("AvgLeader"); // Leader Name

        await SendPacketAsync(memberStream, joinBuffer);

        // 6. Verify Average Rating Update on Leader (TMM_PLAYER_JOINED_GROUP or FULL_UPDATE)
        // Note: When a player joins, the server sends TMM_PLAYER_JOINED_GROUP (0x04)
        // The packet includes the Average Rating.

        // We expect the Leader to receive an update with the new average.
        await ExpectGroupUpdateAndVerifyRatingAsync(leaderStream, ChatProtocol.TMMUpdateType.TMM_PLAYER_JOINED_GROUP,
            expectedAverage);
    }

    private async Task SeedAccountStatistics(TRANSMUTANSTEINServiceProvider app, int accountId, double rating)
    {
        await ChatTestHelpers.SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();

            AccountStatistics? stats = await db.AccountStatistics.SingleOrDefaultAsync(s =>
                s.AccountID == accountId && s.Type == AccountStatisticsType.Matchmaking);
            if (stats == null)
            {
                stats = new AccountStatistics
                {
                    AccountID = accountId,
                    Account = null!,
                    Type = AccountStatisticsType.Matchmaking,
                    SkillRating = rating,
                    MatchesPlayed = 0,
                    MatchesWon = 0,
                    MatchesLost = 0,
                    MatchesConceded = 0,
                    MatchesDisconnected = 0,
                    MatchesKicked = 0,
                    PerformanceScore = 0
                };
                db.AccountStatistics.Add(stats);
            }
            else
            {
                stats.SkillRating = rating;
                db.AccountStatistics.Update(stats);
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }
    }

    private async Task SendPacketAsync(NetworkStream stream, ChatBuffer buffer)
    {
        byte[] packet = [.. buffer.Data.AsSpan(0, (int)buffer.Size)];
        List<byte> rawPacket =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await stream.WriteAsync(rawPacket.ToArray());
    }

    private async Task ExpectGroupUpdateAndVerifyRatingAsync(NetworkStream stream,
        ChatProtocol.TMMUpdateType expectedType, double expectedRating)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
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

            if (command == ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_UPDATE)
            {
                ChatBuffer reader = new(payload);
                byte updateType = reader.ReadInt8();

                // If we get PLAYER_JOINED_GROUP or FULL_GROUP_UPDATE or CREATE_GROUP, they all share structure prefix for rating
                // However, logic says "Wait for expectedType".
                if (updateType == (byte)expectedType)
                {
                    reader.ReadInt32(); // Emitter Account ID
                    reader.ReadInt8(); // Group Size
                    short averageRating = reader.ReadInt16(); // Average Group Rating

                    if (Math.Abs(averageRating - expectedRating) > 1)
                        throw new Exception($"Rating mismatch! Expected ~{expectedRating}, Got {averageRating}");

                    return;
                }
            }
        }

        throw new Exception($"Did not receive Group Update {expectedType}");
    }
}

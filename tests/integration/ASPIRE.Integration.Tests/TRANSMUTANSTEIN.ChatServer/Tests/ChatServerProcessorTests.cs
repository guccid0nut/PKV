// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Sockets;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using MERRICK.DatabaseContext.Entities.Statistics;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public sealed class ChatServerProcessorTests(TRANSMUTANSTEINTestFixture fixture)
{
    [Fact]
    public async Task ChatServer_SendInstantMessage_DirectWhisper_Success()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int senderId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int recipientId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        string senderName = $"WpS_{senderId}";
        string recipientName = $"WpR_{recipientId}";

        using TcpClient senderClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, senderId, senderName);
        using TcpClient recipientClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, recipientId, recipientName);

        ChatBuffer cmd = new();
        cmd.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
        cmd.WriteString(recipientName);
        cmd.WriteString("Hello ChatServer test!");

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await senderClient.GetStream().WriteAsync(rawBytes.ToArray());

        ChatBuffer resp =
            await ChatTestHelpers.ExpectCommandAsync(recipientClient.GetStream(), ChatProtocol.Command.CHAT_CMD_WHISPER,
                5);
        string fromAccount = resp.ReadString();
        string messageBody = resp.ReadString();

        Assert.Equal(senderName, fromAccount);
        Assert.Equal("Hello ChatServer test!", messageBody);
    }

    [Fact]
    public async Task ChatServer_UploadRequest_ReturnsUploadStatusWithDownloadUrl()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;
        const int requesterId = 9903;
        const int matchId = 2060568734;

        await ChatTestHelpers.SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            Role role = await db.Roles.FindAsync(1) ?? new Role { ID = 1, Name = "User" };
            if (role.ID == 0) db.Roles.Add(role);

            if (await db.Accounts.FindAsync(requesterId) == null)
            {
                User u = new()
                {
                    ID = requesterId,
                    EmailAddress = $"requester{requesterId}@test.com",
                    SRPPasswordHash = "h",
                    SRPPasswordSalt = "s",
                    PBKDF2PasswordHash = "h",
                    Role = role
                };
                Account a = new() { ID = requesterId, Name = "ReplayRequester", IsMain = true, User = u };
                u.Accounts = [a];
                db.Users.Add(u);
                await db.SaveChangesAsync();
            }

            if (await db.MatchStatistics.FirstOrDefaultAsync(m => m.MatchID == matchId) == null)
            {
                MatchStatistics match = new()
                {
                    MatchID = matchId,
                    ServerID = 1,
                    HostAccountName = "ServerHost",
                    Map = "caldavar",
                    MapVersion = "1.0",
                    TimePlayed = 1800,
                    FileSize = 5000000,
                    FileName = $"M{matchId}.honreplay",
                    ConnectionState = 1,
                    Version = "4.10.1",
                    AveragePSR = 1500,
                    AveragePSRTeamOne = 1500,
                    AveragePSRTeamTwo = 1500,
                    GameMode = "bot",
                    ScoreTeam1 = 30,
                    ScoreTeam2 = 20,
                    TeamScoreGoal = 0,
                    PlayerScoreGoal = 0,
                    NumberOfRounds = 1,
                    ReleaseStage = "retail",
                    BannedHeroes = "",
                    AwardMostAnnihilations = null,
                    AwardMostQuadKills = null,
                    AwardLargestKillStreak = null,
                    AwardMostSmackdowns = null,
                    AwardMostKills = null,
                    AwardMostAssists = null,
                    AwardLeastDeaths = null,
                    AwardMostBuildingDamage = null,
                    AwardMostWardsKilled = null,
                    AwardMostHeroDamageDealt = null,
                    AwardHighestCreepScore = null,
                    ReplayUrl = $"http://masterserver.nexus.is/replays/M{matchId}.honreplay"
                };
                db.MatchStatistics.Add(match);
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }

        using TcpClient client =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, requesterId, "ReplayRequester");

        ChatBuffer cmd = new();
        cmd.WriteCommand(ChatProtocol.Command.CHAT_CMD_UPLOAD_REQUEST);
        cmd.WriteInt32(matchId);
        cmd.WriteString("honreplay");

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await client.GetStream().WriteAsync(rawBytes.ToArray());

        ChatBuffer resp =
            await ChatTestHelpers.ExpectCommandAsync(client.GetStream(), ChatProtocol.Command.CHAT_CMD_UPLOAD_STATUS,
                5);
        int respMatchId = resp.ReadInt32();
        byte updateType = resp.ReadInt8();

        Assert.Equal(matchId, respMatchId);
        Assert.Equal((byte)ChatProtocol.UploadUpdateType.EUUT_FILE_ALREADY_UPLOADED, updateType);
    }
}

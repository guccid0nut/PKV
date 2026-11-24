// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Security.Claims;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Services;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Statistics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class ReplayEvidenceTests
{
    private static MerrickContext CreateInMemoryContext(string dbName)
    {
        return TestDbContextFactory.CreateInMemoryContext(dbName);
    }

    private static ReplayEvidenceService CreateService(MerrickContext dbContext)
    {
        IPlayerStatsApiClient statsApi = TestApiClientFactory.CreatePlayerStatsApiClient(dbContext);
        IAdminApiClient adminApi = TestApiClientFactory.CreateAdminApiClient(dbContext);
        return new ReplayEvidenceService(statsApi, adminApi, NullLogger<ReplayEvidenceService>.Instance);
    }

    private static StaffController CreateStaffController(MerrickContext dbContext, bool isStaff = true,
        ReplayEvidenceService? evidenceService = null)
    {
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(dbContext);
        IAdminApiClient adminApi = TestApiClientFactory.CreateAdminApiClient(dbContext);
        IPlayerStatsApiClient statsApi = TestApiClientFactory.CreatePlayerStatsApiClient(dbContext);
        StaffStoreService storeService =
            new(new ConfigurationBuilder().Build(), NullLogger<StaffStoreService>.Instance);
        evidenceService ??= CreateService(dbContext);
        StaffController controller = new(adminApi, userApi, statsApi, storeService, evidenceService: evidenceService,
            logger: NullLogger<StaffController>.Instance);

        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, isStaff ? "StaffAdmin" : "RegularUser"),
            .. isStaff ? [new Claim("is_staff", "true"), new Claim(ClaimTypes.Role, "Staff")] : Array.Empty<Claim>()
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
            { HttpContext = new DefaultHttpContext { User = principal } };
        return controller;
    }

    private static MatchStatistics CreateMatchStatistics(int matchId, string map = "caldavar", int duration = 1420)
    {
        return new MatchStatistics
        {
            MatchID = matchId,
            ServerID = 1,
            HostAccountName = "ServerHost",
            Map = map,
            MapVersion = "1.0",
            TimePlayed = duration,
            FileSize = 1048576,
            FileName = $"M{matchId}.honreplay",
            ConnectionState = 0,
            Version = "4.10.1",
            AveragePSR = 1500,
            AveragePSRTeamOne = 1500,
            AveragePSRTeamTwo = 1500,
            GameMode = "Ranked",
            ScoreTeam1 = 25,
            ScoreTeam2 = 18,
            TeamScoreGoal = 0,
            PlayerScoreGoal = 0,
            NumberOfRounds = 1,
            ReleaseStage = "Release",
            BannedHeroes = null,
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
            AwardHighestCreepScore = null
        };
    }

    [Fact]
    public async Task GetMatchEvidenceAsync_WithReportedPlayer_ReturnsEvidenceWithFlagsAndTimestamps()
    {
        await using MerrickContext dbContext = CreateInMemoryContext("Evidence_Basic_" + Guid.NewGuid());
        ReplayEvidenceService service = CreateService(dbContext);

        const int matchId = 2060568724;
        MatchStatistics matchStat = CreateMatchStatistics(matchId, "caldavar", 1500);
        await dbContext.MatchStatistics.AddAsync(matchStat);

        PlayerReport report = new()
        {
            ID = 1001,
            MatchID = matchId,
            ReportedAccountName = "ClickBotAlpha",
            ReporterAccountName = "Teammate1",
            Reason = "AFK / Griefing",
            Description = "Went AFK at 14:22 in base",
            Status = "PENDING"
        };
        await dbContext.PlayerReports.AddAsync(report);
        await dbContext.SaveChangesAsync();

        MatchEvidenceResultDto result = await service.GetMatchEvidenceAsync(matchId, "ClickBotAlpha");

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(matchId, result.MatchID);
        Assert.Equal("caldavar", result.MapName);
        Assert.Equal("ClickBotAlpha", result.ReportedAccountName);
        Assert.NotEmpty(result.ChatLog);
        Assert.True(result.TotalMessages > 0);
        Assert.True(result.ReportedPlayerMessagesCount > 0);

        // Verify suggested timestamp extraction
        Assert.NotNull(result.SuggestedTimestampMs);
        Assert.Equal("14:22", result.SuggestedTimestampFormatted);

        // Verify reported player messages are flagged
        MatchChatMessageDto? reportedMessage = result.ChatLog.FirstOrDefault(c => c.IsReportedPlayer);
        Assert.NotNull(reportedMessage);
        Assert.Equal("ClickBotAlpha", reportedMessage.PlayerName);
    }

    [Fact]
    public async Task GetMatchEvidenceAsync_WhenCalledMultipleTimes_UsesCachedMatchEvidenceEntity()
    {
        await using MerrickContext dbContext = CreateInMemoryContext("Evidence_Cache_" + Guid.NewGuid());
        ReplayEvidenceService service = CreateService(dbContext);

        const int matchId = 2060568725;
        MatchStatistics matchStat = CreateMatchStatistics(matchId, "midwars", 1200);
        await dbContext.MatchStatistics.AddAsync(matchStat);
        await dbContext.SaveChangesAsync();

        // First call extracts and caches
        MatchEvidenceResultDto firstResult = await service.GetMatchEvidenceAsync(matchId, "PlayerOne");
        Assert.True(firstResult.Success);

        // Verify entity exists in DB
        MatchEvidence? cachedEntity = await dbContext.MatchEvidence.FirstOrDefaultAsync(e => e.MatchID == matchId);
        Assert.NotNull(cachedEntity);
        Assert.NotEmpty(cachedEntity.ChatLogJson);

        // Second call retrieves from cache
        MatchEvidenceResultDto secondResult = await service.GetMatchEvidenceAsync(matchId, "PlayerOne");
        Assert.True(secondResult.Success);
        Assert.Equal(firstResult.ChatLog.Count, secondResult.ChatLog.Count);
    }

    [Fact]
    public async Task ReplayEvidence_GetTeamVoiceStreamAsync_ReturnsValidAudioStreamWithWavHeader()
    {
        await using MerrickContext dbContext = CreateInMemoryContext("Evidence_Voice_" + Guid.NewGuid());
        ReplayEvidenceService service = CreateService(dbContext);

        const int matchId = 2060568726;

        Stream? voiceStreamTeam1 = await service.GetTeamVoiceStreamAsync(matchId, 1);
        Assert.NotNull(voiceStreamTeam1);
        Assert.True(voiceStreamTeam1.CanRead);
        Assert.True(voiceStreamTeam1.Length > 44); // WAV header is 44 bytes

        byte[] header = new byte[4];
        int read = await voiceStreamTeam1.ReadAsync(header, 0, 4);
        Assert.Equal(4, read);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(header));

        Stream? voiceStreamTeam2 = await service.GetTeamVoiceStreamAsync(matchId, 2);
        Assert.NotNull(voiceStreamTeam2);
        Assert.True(voiceStreamTeam2.Length > 44);
    }

    [Fact]
    public async Task StaffController_GetMatchEvidence_WhenAuthorized_ReturnsJsonResult()
    {
        await using MerrickContext dbContext = CreateInMemoryContext("Evidence_Controller_Auth_" + Guid.NewGuid());
        StaffController controller = CreateStaffController(dbContext, true);

        const int matchId = 2060568727;
        MatchStatistics matchStat = CreateMatchStatistics(matchId);
        await dbContext.MatchStatistics.AddAsync(matchStat);
        await dbContext.SaveChangesAsync();

        IActionResult actionResult = await controller.GetMatchEvidence(matchId, "ClickBotAlpha");
        JsonResult jsonResult = Assert.IsType<JsonResult>(actionResult);
        MatchEvidenceResultDto? dto = jsonResult.Value as MatchEvidenceResultDto;

        Assert.NotNull(dto);
        Assert.True(dto.Success);
        Assert.Equal(matchId, dto.MatchID);
        Assert.NotEmpty(dto.ChatLog);
    }

    [Fact]
    public async Task StaffController_GetMatchEvidence_WhenUnauthorized_ReturnsForbid()
    {
        await using MerrickContext dbContext = CreateInMemoryContext("Evidence_Controller_Unauth_" + Guid.NewGuid());
        StaffController controller = CreateStaffController(dbContext, false);

        IActionResult actionResult = await controller.GetMatchEvidence(2060568728);
        Assert.IsType<ForbidResult>(actionResult);
    }

    [Fact]
    public async Task StaffController_GetTeamVoiceStream_WhenAuthorized_ReturnsFileStreamResult()
    {
        await using MerrickContext dbContext = CreateInMemoryContext("Evidence_Controller_Voice_" + Guid.NewGuid());
        StaffController controller = CreateStaffController(dbContext, true);

        IActionResult actionResult = await controller.GetTeamVoiceStream(2060568729, 1);
        FileStreamResult fileResult = Assert.IsType<FileStreamResult>(actionResult);
        Assert.Equal("audio/wav", fileResult.ContentType);
        Assert.True(fileResult.EnableRangeProcessing);
        Assert.NotNull(fileResult.FileStream);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ProcessUploadedReplayAsync_WithReplayArchive_ExtractsRosterAndMetadata()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        await using MerrickContext dbContext = CreateInMemoryContext("Evidence_Upload_" + Guid.NewGuid());
        ReplayEvidenceService service = CreateService(dbContext);

        const int matchId = 2060568730;

        // Build a mock .honreplay zip archive in memory
        using MemoryStream zipStream = new();
        await using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create, true))
        {
            ZipArchiveEntry infoEntry = archive.CreateEntry("replayinfo");
            await using (Stream entryStream = await infoEntry.OpenAsync())
            await using (StreamWriter writer = new(entryStream))
            {
                await writer.WriteAsync(@"<?xml version=""1.0"" encoding=""UTF-8""?>
<replayinfo version=""4.10.1.0"" gamemode=""Mid Wars"" mapname=""midwars"" teamsize=""1"" matchid=""2060568730"" matchlength=""1411000"">
  <player name=""ClickBotAlpha"" accountid=""1077"" team=""1"" heroname=""Hero_BabaYaga"" />
  <player name=""guccidonut"" accountid=""58"" team=""2"" heroname=""Hero_Bubbles"" />
</replayinfo>");
            }

            ZipArchiveEntry dataEntry = archive.CreateEntry("replaydata");
            await using (Stream entryStream = await dataEntry.OpenAsync())
            {
                byte[] sampleBytes = Encoding.ASCII.GetBytes("S2R2" + new string('\0', 20));
                await entryStream.WriteAsync(sampleBytes, 0, sampleBytes.Length);
            }
        }

        zipStream.Position = 0;

        MatchEvidenceResultDto result = await service.ProcessUploadedReplayAsync(
            matchId,
            zipStream,
            $"M{matchId}.honreplay",
            "ClickBotAlpha");

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(matchId, result.MatchID);
        Assert.Equal("midwars", result.MapName);
        Assert.Equal("replay_archive", result.EvidenceSource);
        Assert.NotEmpty(result.RosterPlayers);
        Assert.Contains(result.RosterPlayers, p => p.Contains("ClickBotAlpha") && p.Contains("BabaYaga"));
        Assert.Contains(result.RosterPlayers, p => p.Contains("guccidonut") && p.Contains("Bubbles"));
        Assert.NotEmpty(result.ChatLog);
    }

    [Fact]
    public async Task StaffController_ExtractMatchEvidence_WhenAuthorized_ReturnsUpdatedResult()
    {
        await using MerrickContext dbContext = CreateInMemoryContext("Evidence_Extract_Controller_" + Guid.NewGuid());
        StaffController controller = CreateStaffController(dbContext, true);

        const int matchId = 2060568731;
        MatchStatistics matchStat = CreateMatchStatistics(matchId, "caldavar", 1200);
        await dbContext.MatchStatistics.AddAsync(matchStat);
        await dbContext.SaveChangesAsync();

        IActionResult actionResult = await controller.ExtractMatchEvidence(matchId, null, "ClickBotAlpha");
        JsonResult jsonResult = Assert.IsType<JsonResult>(actionResult);
        MatchEvidenceResultDto? dto = jsonResult.Value as MatchEvidenceResultDto;

        Assert.NotNull(dto);
        Assert.True(dto.Success);
        Assert.Equal(matchId, dto.MatchID);
    }
}

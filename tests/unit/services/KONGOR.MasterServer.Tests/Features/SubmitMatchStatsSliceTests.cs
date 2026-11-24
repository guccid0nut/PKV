// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Constants;
using ASPIRE.Common.Models.ServerManagement;
using FluentAssertions;
using KONGOR.MasterServer.Features.Matches.SubmitMatchStats;
using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services.Background;
using MemoryPack;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain.Identifiers;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests.Features;

public class SubmitMatchStatsSliceTests
{
    private readonly SubmitMatchStatsValidator _validator = new();

    [Fact]
    public void Validator_WhenSessionEmpty_ReturnsInvalid()
    {
        SubmitMatchStatsCommand command = new(
            MatchId: 100,
            Session: "",
            Form: new StatsForSubmissionRequestForm
            {
                Function = "submit_stats",
                MatchStats = new Dictionary<string, string> { ["match_id"] = "100" },
                TeamStats = [],
                PlayerStats = []
            },
            RemoteIp: "127.0.0.1");

        (bool isValid, string? error) = _validator.Validate(command);
        isValid.Should().BeFalse();
        error.Should().Contain("session");
    }

    [Fact]
    public void Validator_WhenMatchIdZeroOrNegative_ReturnsInvalid()
    {
        SubmitMatchStatsCommand command = new(
            MatchId: 0,
            Session: "valid-session",
            Form: new StatsForSubmissionRequestForm
            {
                Function = "submit_stats",
                MatchStats = new Dictionary<string, string> { ["match_id"] = "0" },
                TeamStats = [],
                PlayerStats = []
            },
            RemoteIp: "127.0.0.1");

        (bool isValid, string? error) = _validator.Validate(command);
        isValid.Should().BeFalse();
        error.Should().Contain("Invalid Match ID");
    }

    [Fact]
    public void Validator_WhenValid_ReturnsTrue()
    {
        SubmitMatchStatsCommand command = new(
            MatchId: 555,
            Session: "valid-session",
            Form: new StatsForSubmissionRequestForm
            {
                Function = "submit_stats",
                MatchStats = new Dictionary<string, string> { ["match_id"] = "555" },
                TeamStats = [],
                PlayerStats = []
            },
            RemoteIp: "127.0.0.1");

        (bool isValid, string? error) = _validator.Validate(command);
        isValid.Should().BeTrue();
        error.Should().BeNull();
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Handler tests
    // ----------------------------------------------------------------------------------------------------------------

    private const string SessionCookie = "test-session";
    private const int MatchId = 1234;

    private static MatchServer CreateRegisteredServer(int id = 10) => new()
    {
        ID = id,
        HostAccountID = 42,
        HostAccountName = "HostPlayer",
        Name = "Server-1",
        Instance = 1,
        IPAddress = "127.0.0.1",
        Port = 11235,
        Location = "Local",
        Description = "Test Server",
        Cookie = SessionCookie
    };

    private static SubmitMatchStatsCommand CreateCommand(int matchId = MatchId) => new(
        MatchId: matchId,
        Session: SessionCookie,
        Form: new StatsForSubmissionRequestForm
        {
            Function = "submit_stats",
            MatchStats = new Dictionary<string, string> { ["match_id"] = matchId.ToString() },
            TeamStats = [],
            PlayerStats = []
        },
        RemoteIp: "127.0.0.1");

    /// <summary>Mock Redis where the session cookie resolves to a registered match server via MATCH-SERVER-SESSIONS.</summary>
    private static Mock<IDatabase> CreateDbWithRegisteredServer(MatchServer server)
    {
        Mock<IDatabase> mockDb = new();
        mockDb.SetReturnsDefault<Task<bool>>(Task.FromResult(true));
        mockDb.Setup(d => d.HashGetAsync("MATCH-SERVER-SESSIONS", SessionCookie, CommandFlags.None))
            .ReturnsAsync(MemoryPackSerializer.Serialize(server));
        return mockDb;
    }

    private static Mock<IMatchProcessingChannel> CreateChannel(bool enqueueResult = true)
    {
        Mock<IMatchProcessingChannel> mockChannel = new();
        mockChannel.Setup(c => c.EnqueueAsync(It.IsAny<MatchSubmissionPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(enqueueResult);
        return mockChannel;
    }

    [Fact]
    public async Task Handler_WhenValid_EnqueuesToChannelAndReturnsSucceeded()
    {
        Mock<IDatabase> mockDb = CreateDbWithRegisteredServer(CreateRegisteredServer());
        Mock<IMatchProcessingChannel> mockChannel = CreateChannel();
        Mock<IMatchDeadLetterQueue> mockDlq = new();

        SubmitMatchStatsHandler handler = new(new Mock<IAccountRepository>().Object, mockDb.Object, mockChannel.Object,
            NullLogger<SubmitMatchStatsHandler>.Instance, mockDlq.Object);

        SubmitMatchStatsResponse response = await handler.HandleAsync(CreateCommand());

        response.Success.Should().BeTrue();
        response.StatusCode.Should().Be(200);
        response.ResponseData.Should().ContainKey("match_id");

        mockChannel.Verify(c => c.EnqueueAsync(
            It.Is<MatchSubmissionPayload>(p => p.MatchID == MatchId && p.MatchServer!.ID == 10 && p.AccountId == 42),
            It.IsAny<CancellationToken>()), Times.Once);
        mockDlq.Verify(q => q.EnqueueAsync(It.IsAny<DeadLetterMatchJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handler_WhenDuplicateMatchIdSubmitted_ReturnsSucceededWithoutQueueing()
    {
        Mock<IDatabase> mockDb = CreateDbWithRegisteredServer(CreateRegisteredServer());
        Mock<IMatchProcessingChannel> mockChannel = CreateChannel();

        // Simulate duplicate submission: Redis guard acquisition (SET NX) returns false
        mockDb.Setup(d => d.StringSetAsync(
                It.Is<RedisKey>(k => k.ToString() == $"LOCK:MATCH_SUBMISSION:{MatchId}"),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                When.NotExists,
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(false);

        SubmitMatchStatsHandler handler = new(new Mock<IAccountRepository>().Object, mockDb.Object, mockChannel.Object,
            NullLogger<SubmitMatchStatsHandler>.Instance);

        SubmitMatchStatsResponse response = await handler.HandleAsync(CreateCommand());

        response.Success.Should().BeTrue();
        response.StatusCode.Should().Be(200);

        mockChannel.Verify(c => c.EnqueueAsync(It.IsAny<MatchSubmissionPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handler_WhenSessionUnknown_QuarantinesToDeadLetterQueueAndDoesNotProcess()
    {
        // No registered server, no account session -> previously fabricated an "Orphaned Server" and processed stats.
        Mock<IDatabase> mockDb = new();
        mockDb.SetReturnsDefault<Task<bool>>(Task.FromResult(true));
        Mock<IMatchProcessingChannel> mockChannel = CreateChannel();
        Mock<IMatchDeadLetterQueue> mockDlq = new();

        SubmitMatchStatsHandler handler = new(new Mock<IAccountRepository>().Object, mockDb.Object, mockChannel.Object,
            NullLogger<SubmitMatchStatsHandler>.Instance, mockDlq.Object);

        SubmitMatchStatsResponse response = await handler.HandleAsync(CreateCommand());

        // Legacy protocol: still answer success so the submitting client is not logged out.
        response.Success.Should().BeTrue();
        response.StatusCode.Should().Be(200);

        mockChannel.Verify(c => c.EnqueueAsync(It.IsAny<MatchSubmissionPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
        mockDlq.Verify(q => q.EnqueueAsync(
            It.Is<DeadLetterMatchJob>(j => j.MatchId == MatchId &&
                                           j.FailureReason == SubmitMatchStatsHandler.QuarantineReasonOrphanedSession),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handler_WhenServerDidNotHostMatch_QuarantinesToDeadLetterQueue()
    {
        Mock<IDatabase> mockDb = CreateDbWithRegisteredServer(CreateRegisteredServer(id: 10));
        MatchStartData startData = new()
        {
            MatchID = MatchId,
            MatchName = "Test Match",
            ServerID = 99, // started on a DIFFERENT server
            ServerName = "Other-Server",
            HostAccountName = "OtherHost",
            Map = "caldavar",
            Version = "4.10.1",
            IsCasual = false,
            MatchType = 0,
            MatchMode = "ap"
        };
        mockDb.Setup(d => d.StringGetAsync(
                It.Is<RedisKey>(k => k.ToString().StartsWith("MATCH-START-DATA") && k.ToString().Contains(MatchId.ToString())),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(MemoryPackSerializer.Serialize(startData));

        Mock<IMatchProcessingChannel> mockChannel = CreateChannel();
        Mock<IMatchDeadLetterQueue> mockDlq = new();

        SubmitMatchStatsHandler handler = new(new Mock<IAccountRepository>().Object, mockDb.Object, mockChannel.Object,
            NullLogger<SubmitMatchStatsHandler>.Instance, mockDlq.Object);

        SubmitMatchStatsResponse response = await handler.HandleAsync(CreateCommand());

        response.Success.Should().BeTrue();
        mockChannel.Verify(c => c.EnqueueAsync(It.IsAny<MatchSubmissionPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
        mockDlq.Verify(q => q.EnqueueAsync(
            It.Is<DeadLetterMatchJob>(j => j.FailureReason == SubmitMatchStatsHandler.QuarantineReasonServerMismatch),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handler_WhenEnqueueRejected_ReleasesGuardAndReturns503()
    {
        Mock<IDatabase> mockDb = CreateDbWithRegisteredServer(CreateRegisteredServer());
        Mock<IMatchProcessingChannel> mockChannel = CreateChannel(enqueueResult: false);

        SubmitMatchStatsHandler handler = new(new Mock<IAccountRepository>().Object, mockDb.Object, mockChannel.Object,
            NullLogger<SubmitMatchStatsHandler>.Instance);

        SubmitMatchStatsResponse response = await handler.HandleAsync(CreateCommand());

        response.Success.Should().BeFalse();
        response.StatusCode.Should().Be(503);

        // The guard must be released so the match server's retry is accepted (no silent data loss).
        mockDb.Verify(d => d.KeyDeleteAsync(
            It.Is<RedisKey>(k => k.ToString() == $"LOCK:MATCH_SUBMISSION:{MatchId}"),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task Handler_WhenEnqueueThrows_ReleasesGuardAndReturns503()
    {
        Mock<IDatabase> mockDb = CreateDbWithRegisteredServer(CreateRegisteredServer());
        Mock<IMatchProcessingChannel> mockChannel = new();
        mockChannel.Setup(c => c.EnqueueAsync(It.IsAny<MatchSubmissionPayload>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "boom"));

        SubmitMatchStatsHandler handler = new(new Mock<IAccountRepository>().Object, mockDb.Object, mockChannel.Object,
            NullLogger<SubmitMatchStatsHandler>.Instance);

        SubmitMatchStatsResponse response = await handler.HandleAsync(CreateCommand());

        response.Success.Should().BeFalse();
        response.StatusCode.Should().Be(503);
        mockDb.Verify(d => d.KeyDeleteAsync(
            It.Is<RedisKey>(k => k.ToString() == $"LOCK:MATCH_SUBMISSION:{MatchId}"),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task Handler_WhenPlayerStatsAndTeamStatsOmitted_SucceedsAndEnqueues()
    {
        // Protects: bot matches / early concedes (time_played=0) where K2 omits player_stats.
        // Fails when: Handler or model binding requires non-empty PlayerStats or TeamStats.
        Mock<IDatabase> mockDb = CreateDbWithRegisteredServer(CreateRegisteredServer());
        Mock<IMatchProcessingChannel> mockChannel = CreateChannel();
        Mock<IMatchDeadLetterQueue> mockDlq = new();

        SubmitMatchStatsHandler handler = new(new Mock<IAccountRepository>().Object, mockDb.Object, mockChannel.Object,
            NullLogger<SubmitMatchStatsHandler>.Instance, mockDlq.Object);

        SubmitMatchStatsCommand command = new(
            MatchId: MatchId,
            Session: SessionCookie,
            Form: new StatsForSubmissionRequestForm
            {
                Function = "submit_stats",
                MatchStats = new Dictionary<string, string> { ["match_id"] = MatchId.ToString() }
                // TeamStats and PlayerStats omitted entirely, defaulting to []
            },
            RemoteIp: "127.0.0.1");

        SubmitMatchStatsResponse response = await handler.HandleAsync(command);

        response.Success.Should().BeTrue();
        response.ResponseData.Should().NotBeNull();
        mockChannel.Verify(c => c.EnqueueAsync(It.Is<MatchSubmissionPayload>(p =>
            p.MatchID == MatchId && p.Form != null && p.Form.PlayerStats.Count == 0 && p.Form.TeamStats.Count == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Extensions_ToMatchStatistics_WhenTeamStatsEmpty_DefaultsScoresToZero()
    {
        // Protects: ToMatchStatistics against InvalidOperationException on First()/Last() when TeamStats is empty.
        StatsForSubmissionRequestForm form = new()
        {
            Function = "submit_stats",
            MatchStats = new Dictionary<string, string>
            {
                [MatchStatsProtocolKeys.MatchId] = "999",
                [MatchStatsProtocolKeys.Map] = "caldavar",
                [MatchStatsProtocolKeys.MapVersion] = "1.0",
                [MatchStatsProtocolKeys.TimePlayed] = "0",
                [MatchStatsProtocolKeys.FileSize] = "0",
                [MatchStatsProtocolKeys.FileName] = "M999.honreplay",
                [MatchStatsProtocolKeys.ConnectionState] = "1",
                [MatchStatsProtocolKeys.Version] = "4.10.1",
                [MatchStatsProtocolKeys.AverageSkillRating] = "1500",
                [MatchStatsProtocolKeys.AverageSkillRatingTeamOne] = "1500",
                [MatchStatsProtocolKeys.AverageSkillRatingTeamTwo] = "1500",
                [MatchStatsProtocolKeys.GameMode] = "normal",
                [MatchStatsProtocolKeys.TeamScoreGoal] = "0",
                [MatchStatsProtocolKeys.PlayerScoreGoal] = "0",
                [MatchStatsProtocolKeys.NumberOfRounds] = "1",
                [MatchStatsProtocolKeys.ReleaseStage] = "live",
                [MatchAwardConstants.MostAnnihilations] = "0",
                [MatchAwardConstants.MostQuadKills] = "0",
                [MatchAwardConstants.LargestKillStreak] = "0",
                [MatchAwardConstants.MostSmackdowns] = "0",
                [MatchAwardConstants.MostKills] = "0",
                [MatchAwardConstants.MostAssists] = "0",
                [MatchAwardConstants.LeastDeaths] = "0",
                [MatchAwardConstants.MostBuildingDamage] = "0",
                [MatchAwardConstants.MostWardsKilled] = "0",
                [MatchAwardConstants.MostHeroDamageDealt] = "0",
                [MatchAwardConstants.HighestCreepScore] = "0"
            },
            TeamStats = [],
            PlayerStats = []
        };

        var stats = form.ToMatchStatistics(matchServerID: 10, hostAccountName: "ServerHost");

        stats.Should().NotBeNull();
        stats.MatchID.Should().Be(999);
        stats.ScoreTeam1.Should().Be(0);
        stats.ScoreTeam2.Should().Be(0);
    }

    [Fact]
    public void Extensions_ToPlayerStatistics_And_ToMatchStatistics_WhenBotMatchNoStatsKeysOmitted_DefaultsCleanly()
    {
        // Protects: botmatch / option[no_stats]=1 submissions where hon-x86_64-server omits 'wins', 'losses',
        //           'social_bonus', 'gameplaystat*', and award keys in submit_stats payloads.
        // Fails when: ToPlayerStatistics or ToMatchStatistics uses raw dictionary indexing (player[key]) and throws KeyNotFoundException.
        // Seam: Real StatsForSubmissionRequestFormExtensions mapping boundary.
        Mock<global::KONGOR.MasterServer.Services.IHeroDefinitionService> mockHeroes = new();
        mockHeroes.Setup(h => h.GetBaseHeroId("Hero_Pyromancer")).Returns(25u);

        StatsForSubmissionRequestForm form = new()
        {
            Function = "submit_stats",
            MatchStats = new Dictionary<string, string>
            {
                [MatchStatsProtocolKeys.MatchId] = "2048",
                [MatchStatsProtocolKeys.Map] = "caldavar",
                [MatchStatsProtocolKeys.GameMode] = "botmatch"
            },
            TeamStats = [],
            PlayerStats = new Dictionary<int, Dictionary<string, Dictionary<string, string>>>
            {
                [0] = new()
                {
                    ["Hero_Pyromancer"] = new Dictionary<string, string>
                    {
                        [PlayerStatsProtocolKeys.Team] = "1",
                        [PlayerStatsProtocolKeys.Position] = "0",
                        [PlayerStatsProtocolKeys.HeroKills] = "7",
                        [PlayerStatsProtocolKeys.Deaths] = "1"
                        // 'wins', 'losses', 'discos', 'concedes', 'kicked', 'social_bonus', 'gameplaystat0..9' omitted!
                    }
                }
            }
        };

        var matchStats = form.ToMatchStatistics(matchServerID: 10, hostAccountName: "BotHost");
        var playerStats = form.ToPlayerStatistics(
            playerIndex: 0,
            accountID: 777,
            accountName: "Playtester",
            clanID: null,
            clanTag: null,
            heroDefinitionService: mockHeroes.Object);

        matchStats.MatchID.Should().Be(2048);
        matchStats.GameMode.Should().Be("botmatch");
        matchStats.AwardMostKills.Should().Be(0);

        playerStats.MatchID.Should().Be(2048);
        playerStats.HeroProductID.Should().Be(25u);
        playerStats.HeroIdentifier.Should().Be("Hero_Pyromancer");
        playerStats.Team.Should().Be(1);
        playerStats.HeroKills.Should().Be(7);
        playerStats.HeroDeaths.Should().Be(1);
        playerStats.Win.Should().Be(0);
        playerStats.Loss.Should().Be(0);
        playerStats.Disconnected.Should().Be(0);
        playerStats.Conceded.Should().Be(0);
        playerStats.GameplayStat0.Should().Be(0);
    }

    [Fact]
    public async Task Handler_WhenQueueArcadeServerSubmitsStats_IgnoresWithoutEnqueuingOrQuarantining()
    {
        // protects: Queue Arcade isolation — warm Arcade matches must never record MMR, coins, match history, or leaver strikes
        //           for players added to CHostServer::m_mapArrangedRoster via 0x0517.
        // fails_when: SubmitMatchStatsHandler enqueues stats from kongor_arcade* / QUEUE ARCADE servers when MatchStartData is null.
        // seam: Real SubmitMatchStatsHandler + Redis registered server lookup + IMatchProcessingChannel / IMatchDeadLetterQueue.
        MatchServer arcadeServer = CreateRegisteredServer(id: 1784826586);
        arcadeServer.HostAccountID = 77;
        arcadeServer.HostAccountName = "kongor_arcade1";
        arcadeServer.Name = "KONGOR QUEUE ARCADE #Q1";

        Mock<IDatabase> mockDb = CreateDbWithRegisteredServer(arcadeServer);
        Mock<IMatchProcessingChannel> mockChannel = CreateChannel();
        Mock<IMatchDeadLetterQueue> mockDlq = new();

        SubmitMatchStatsHandler handler = new(
            new Mock<IAccountRepository>().Object,
            mockDb.Object,
            mockChannel.Object,
            NullLogger<SubmitMatchStatsHandler>.Instance,
            mockDlq.Object);

        SubmitMatchStatsResponse response = await handler.HandleAsync(CreateCommand());

        response.Success.Should().BeTrue();
        mockChannel.Verify(c => c.EnqueueAsync(It.IsAny<MatchSubmissionPayload>(), It.IsAny<CancellationToken>()), Times.Never);
        mockDlq.Verify(q => q.EnqueueAsync(It.IsAny<DeadLetterMatchJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

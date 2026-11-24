// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Statistics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASPIRE.Tests.MERRICK.DatabaseContext.Tests;

public class MatchHistoryRepositoryConcurrencyTests
{
    [Trait("Category", "Manual")]
    [Fact]
    public async Task SaveMatchAsync_UnderConcurrentRacingCalls_PersistsSuccessfullyWithoutUnhandledCollision()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        string dbName = "MatchRace_" + Guid.NewGuid().ToString("N");
        using MerrickContext seedContext = TestDbContextFactory.CreateInMemoryContext(dbName);
        User user = new()
        {
            ID = 1,
            EmailAddress = "testuser@kongor.net",
            SRPPasswordSalt = "salt123",
            SRPPasswordHash = "hash123",
            PBKDF2PasswordHash = "pbkdf2hash",
            Role = new Role { ID = 1, Name = "User" }
        };
        Account account = new()
        {
            ID = 1,
            Name = "Player1",
            User = user,
            IsMain = true
        };
        seedContext.Users.Add(user);
        seedContext.Accounts.Add(account);
        await seedContext.SaveChangesAsync();

        const int matchId = 60010;
        const int concurrentTasks = 8;

        // Act
        List<Task> tasks = [];
        for (int i = 0; i < concurrentTasks; i++)
        {
            int index = i;
            tasks.Add(Task.Run(async () =>
            {
                using MerrickContext ctx = TestDbContextFactory.CreateInMemoryContext(dbName);
                MatchHistoryRepository repo = new(ctx, NullLogger<MatchHistoryRepository>.Instance);

                MatchStatistics match = new()
                {
                    MatchID = matchId,
                    ServerID = 10,
                    HostAccountName = "Player1",
                    Map = "caldavar",
                    MapVersion = "1.0",
                    TimePlayed = 1200 + index,
                    FileSize = 1024,
                    FileName = $"m{matchId}.rep",
                    ConnectionState = 1,
                    Version = "4.10.1",
                    AveragePSR = 1500,
                    AveragePSRTeamOne = 1500,
                    AveragePSRTeamTwo = 1500,
                    GameMode = "ranked",
                    ScoreTeam1 = 30,
                    ScoreTeam2 = 25,
                    TeamScoreGoal = 0,
                    PlayerScoreGoal = 0,
                    NumberOfRounds = 1,
                    ReleaseStage = "release",
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

                await repo.SaveMatchAsync(match);
            }));
        }

        // Assert
        Func<Task> act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();

        using MerrickContext verifyContext = TestDbContextFactory.CreateInMemoryContext(dbName);
        MatchStatistics? saved = await verifyContext.MatchStatistics.FirstOrDefaultAsync(m => m.MatchID == matchId);
        saved.Should().NotBeNull();
        saved!.MatchID.Should().Be(matchId);
    }

    [Fact]
    public async Task UpdateAccountStatisticsAsync_UnderConcurrentCalls_UpsertsCorrectly()
    {
        // Arrange
        string dbName = "AccountStatsRace_" + Guid.NewGuid().ToString("N");
        using MerrickContext seedContext = TestDbContextFactory.CreateInMemoryContext(dbName);
        User user = new()
        {
            ID = 2,
            EmailAddress = "statsuser@kongor.net",
            SRPPasswordSalt = "salt123",
            SRPPasswordHash = "hash123",
            PBKDF2PasswordHash = "pbkdf2hash",
            Role = new Role { ID = 1, Name = "User" }
        };
        Account account = new()
        {
            ID = 2,
            Name = "Player2",
            User = user,
            IsMain = true
        };
        seedContext.Users.Add(user);
        seedContext.Accounts.Add(account);
        await seedContext.SaveChangesAsync();

        const int concurrentTasks = 8;

        // Act
        List<Task> tasks = [];
        for (int i = 0; i < concurrentTasks; i++)
        {
            int index = i;
            tasks.Add(Task.Run(async () =>
            {
                using MerrickContext ctx = TestDbContextFactory.CreateInMemoryContext(dbName);
                MatchHistoryRepository repo = new(ctx, NullLogger<MatchHistoryRepository>.Instance);

                AccountStatistics stats = new()
                {
                    AccountID = 2,
                    Account = null!,
                    Type = AccountStatisticsType.Matchmaking,
                    MatchesPlayed = 10 + index,
                    MatchesWon = 5 + index,
                    SkillRating = 1550.0 + index
                };

                await repo.UpdateAccountStatisticsAsync(stats);
            }));
        }

        // Assert
        Func<Task> act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();

        using MerrickContext verifyContext = TestDbContextFactory.CreateInMemoryContext(dbName);
        AccountStatistics? saved = await verifyContext.AccountStatistics
            .FirstOrDefaultAsync(s => s.AccountID == 2 && s.Type == AccountStatisticsType.Matchmaking);
        saved.Should().NotBeNull();
        saved!.AccountID.Should().Be(2);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task SaveMasteryAsync_UnderConcurrentCalls_UpsertsCorrectly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        string dbName = "MasteryRace_" + Guid.NewGuid().ToString("N");
        using MerrickContext seedContext = TestDbContextFactory.CreateInMemoryContext(dbName);
        User user = new()
        {
            ID = 3,
            EmailAddress = "masteryuser@kongor.net",
            SRPPasswordSalt = "salt123",
            SRPPasswordHash = "hash123",
            PBKDF2PasswordHash = "pbkdf2hash",
            Role = new Role { ID = 1, Name = "User" }
        };
        Account account = new()
        {
            ID = 3,
            Name = "Player3",
            User = user,
            IsMain = true
        };
        seedContext.Users.Add(user);
        seedContext.Accounts.Add(account);
        await seedContext.SaveChangesAsync();

        const int concurrentTasks = 8;

        // Act
        List<Task> tasks = [];
        for (int i = 0; i < concurrentTasks; i++)
        {
            int index = i;
            tasks.Add(Task.Run(async () =>
            {
                using MerrickContext ctx = TestDbContextFactory.CreateInMemoryContext(dbName);
                MatchHistoryRepository repo = new(ctx, NullLogger<MatchHistoryRepository>.Instance);

                Mastery mastery = new()
                {
                    AccountID = 3,
                    Account = null!,
                    HeroExperiences =
                    [
                        new HeroMasteryExperience { HeroIdentifier = "Hero_Accursed", Experience = 1000 + index * 100 }
                    ]
                };

                await repo.SaveMasteryAsync(mastery);
            }));
        }

        // Assert
        Func<Task> act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();

        using MerrickContext verifyContext = TestDbContextFactory.CreateInMemoryContext(dbName);
        Mastery? saved = await verifyContext.Masteries.FirstOrDefaultAsync(m => m.AccountID == 3);
        saved.Should().NotBeNull();
        saved!.AccountID.Should().Be(3);
    }
}

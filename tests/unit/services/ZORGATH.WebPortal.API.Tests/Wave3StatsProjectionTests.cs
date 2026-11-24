// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Wave3StatsProjectionTests.cs
// System: ZORGATH.WebPortal.API Tests - Wave 3B Dapper Read Projections
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.CQRS.PlayerStats;
using Xunit;
using ZORGATH.WebPortal.API.Controllers;
using ZORGATH.WebPortal.API.Features.PlayerStats;
using ZORGATH.WebPortal.API.Services.Stats;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class Wave3StatsProjectionTests
{
    [Fact]
    public async Task DapperProjection_RelationalSqlite_ExecutesParameterizedSqlAndOrdersDescending()
    {
        using SqliteConnection conn = new("Data Source=:memory:");
        await conn.OpenAsync();
        conn.CreateCollation("case_insensitive", StringComparer.OrdinalIgnoreCase.Compare);

        DbContextOptions<MerrickContext> options = new DbContextOptionsBuilder<MerrickContext>()
            .UseSqlite(conn)
            .Options;

        await using MerrickContext db = new(options);
        await db.Database.EnsureCreatedAsync();

        DateTimeOffset t1 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset t2 = new(2026, 11, 15, 18, 30, 0, TimeSpan.Zero);
        DateTimeOffset t3 = new(2026, 11, 19, 9, 45, 0, TimeSpan.Zero);

        Role playerRole = await db.Roles.FirstAsync();

        User u1 = new()
        {
            ID = 7001,
            RoleID = playerRole.ID,
            Role = playerRole,
            EmailAddress = "dapperace@kongor.local",
            SRPPasswordHash = "h1",
            SRPPasswordSalt = "s1",
            PBKDF2PasswordHash = "p1"
        };
        User u2 = new()
        {
            ID = 7002,
            RoleID = playerRole.ID,
            Role = playerRole,
            EmailAddress = "other@kongor.local",
            SRPPasswordHash = "h2",
            SRPPasswordSalt = "s2",
            PBKDF2PasswordHash = "p2"
        };
        db.Users.AddRange(u1, u2);
        db.Accounts.Add(new Account { ID = 7001, UserID = 7001, Name = "DapperAce", User = u1 });
        db.Accounts.Add(new Account { ID = 7002, UserID = 7002, Name = "OtherPlayer", User = u2 });

        db.MatchStatistics.AddRange(
            new MatchStatistics { ID = 1, MatchID = 9001, Map = "caldavar", GameMode = "ap", TimePlayed = 1800, TimestampRecorded = t1 },
            new MatchStatistics { ID = 2, MatchID = 9002, Map = "midwars", GameMode = "ap", TimePlayed = 1200, TimestampRecorded = t2 },
            new MatchStatistics { ID = 3, MatchID = 9003, Map = "grimmscrossing", GameMode = "sd", TimePlayed = 2400, TimestampRecorded = t3 });

        db.PlayerStatistics.AddRange(
            new PlayerStatistics { ID = 1, MatchID = 9001, AccountID = 7001, AccountName = "DapperAce", HeroIdentifier = "Hero_Pyromancer", Team = 1, Win = 1, Loss = 0, HeroKills = 12, HeroDeaths = 3, HeroAssists = 8, HeroDamage = 19500, Actions = 2800, MVP = 1 },
            new PlayerStatistics { ID = 2, MatchID = 9002, AccountID = 7001, AccountName = "DapperAce", HeroIdentifier = "Hero_Fayde", Team = 2, Win = 0, Loss = 1, HeroKills = 7, HeroDeaths = 6, HeroAssists = 4, HeroDamage = 11200, Actions = 2100, MVP = 0 },
            new PlayerStatistics { ID = 3, MatchID = 9003, AccountID = 7001, AccountName = "DapperAce", HeroIdentifier = "Hero_Pebbles", Team = 1, Win = 1, Loss = 0, HeroKills = 15, HeroDeaths = 2, HeroAssists = 11, HeroDamage = 26000, Actions = 3500, MVP = 1 },
            new PlayerStatistics { ID = 4, MatchID = 9003, AccountID = 7002, AccountName = "OtherPlayer", HeroIdentifier = "Hero_Glacius", Team = 2, Win = 0, Loss = 1, HeroKills = 1, HeroDeaths = 9, HeroAssists = 12, HeroDamage = 5400, Actions = 1700, MVP = 0 });

        await db.SaveChangesAsync();

        db.Database.IsRelational().Should().BeTrue();

        DapperMatchStatsReadProjectionService service = new(db, NullLogger<DapperMatchStatsReadProjectionService>.Instance);
        IReadOnlyList<PlayerMatchHistoryProjectionDto> rows = await service.GetPlayerMatchHistoryAsync(7001, limit: 2);

        rows.Should().HaveCount(2);
        rows[0].MatchId.Should().Be(9003);
        rows[0].HeroIdentifier.Should().Be("Hero_Pebbles");
        rows[0].Map.Should().Be("grimmscrossing");
        rows[0].Won.Should().BeTrue();
        rows[0].Kills.Should().Be(15);
        rows[0].Deaths.Should().Be(2);
        rows[0].Assists.Should().Be(11);
        rows[0].IsMvp.Should().BeTrue();

        rows[1].MatchId.Should().Be(9002);
        rows[1].Won.Should().BeFalse();
        rows[1].HeroIdentifier.Should().Be("Hero_Fayde");
    }

    [Fact]
    public async Task DapperProjection_RelationalSqlite_ExecutesInsideActiveEfCoreTransaction()
    {
        using SqliteConnection conn = new("Data Source=:memory:");
        await conn.OpenAsync();
        conn.CreateCollation("case_insensitive", StringComparer.OrdinalIgnoreCase.Compare);

        DbContextOptions<MerrickContext> options = new DbContextOptionsBuilder<MerrickContext>()
            .UseSqlite(conn)
            .Options;

        await using MerrickContext db = new(options);
        await db.Database.EnsureCreatedAsync();

        Role playerRole = await db.Roles.FirstAsync();
        User u1 = new()
        {
            ID = 8101,
            RoleID = playerRole.ID,
            Role = playerRole,
            EmailAddress = "txuser@kongor.local",
            SRPPasswordHash = "h1",
            SRPPasswordSalt = "s1",
            PBKDF2PasswordHash = "p1"
        };
        db.Users.Add(u1);
        db.Accounts.Add(new Account { ID = 8101, UserID = 8101, Name = "TxPlayer", User = u1 });
        await db.SaveChangesAsync();

        db.Database.SetCommandTimeout(45);
        DapperMatchStatsReadProjectionService service = new(db, NullLogger<DapperMatchStatsReadProjectionService>.Instance);

        await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
        {
            db.MatchStatistics.Add(new MatchStatistics
            {
                ID = 100,
                MatchID = 9555,
                Map = "caldavar",
                GameMode = "ap",
                TimePlayed = 1900,
                TimestampRecorded = new DateTimeOffset(2026, 11, 19, 14, 0, 0, TimeSpan.Zero)
            });
            db.PlayerStatistics.Add(new PlayerStatistics
            {
                ID = 100,
                MatchID = 9555,
                AccountID = 8101,
                AccountName = "TxPlayer",
                HeroIdentifier = "Hero_Tempest",
                Team = 1,
                Win = 1,
                HeroKills = 6,
                HeroDeaths = 2,
                HeroAssists = 19,
                HeroDamage = 14000,
                Actions = 2600,
                MVP = 1
            });
            await db.SaveChangesAsync();

            // Must enlist in db.Database.CurrentTransaction?.GetDbTransaction() without throwing
            IReadOnlyList<PlayerMatchHistoryProjectionDto> inTxRows = await service.GetPlayerMatchHistoryAsync(8101, limit: 10);
            inTxRows.Should().ContainSingle();
            inTxRows[0].MatchId.Should().Be(9555);
            inTxRows[0].HeroIdentifier.Should().Be("Hero_Tempest");

            await tx.RollbackAsync();
        }

        IReadOnlyList<PlayerMatchHistoryProjectionDto> postRollbackRows = await service.GetPlayerMatchHistoryAsync(8101, limit: 10);
        postRollbackRows.Should().BeEmpty();
    }

    [Fact]
    public async Task DapperProjection_InMemoryFallback_ReturnsOrderedRows()
    {
        DbContextOptions<MerrickContext> options = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase($"Wave3StatsProj_{Guid.NewGuid():N}")
            .Options;

        await using MerrickContext db = new(options);
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        db.MatchStatistics.AddRange(
            new MatchStatistics { ID = 10, MatchID = 101, Map = "caldavar", GameMode = "ap", TimePlayed = 1500, TimestampRecorded = now.AddHours(-2) },
            new MatchStatistics { ID = 11, MatchID = 102, Map = "midwars", GameMode = "ar", TimePlayed = 900, TimestampRecorded = now.AddHours(-1) });
        db.PlayerStatistics.AddRange(
            new PlayerStatistics { ID = 10, MatchID = 101, AccountID = 55, AccountName = "Tester", HeroIdentifier = "Hero_Swiftblade", Win = 1, HeroKills = 9, HeroDeaths = 1, HeroAssists = 5 },
            new PlayerStatistics { ID = 11, MatchID = 102, AccountID = 55, AccountName = "Tester", HeroIdentifier = "Hero_Behemoth", Win = 0, HeroKills = 3, HeroDeaths = 4, HeroAssists = 18 });
        await db.SaveChangesAsync();

        DapperMatchStatsReadProjectionService service = new(db, NullLogger<DapperMatchStatsReadProjectionService>.Instance);
        GetPlayerMatchHistoryProjectionHandler handler = new(service);

        IReadOnlyList<PlayerMatchHistoryProjectionDto> rows = await handler.Handle(
            new GetPlayerMatchHistoryProjectionQuery(55, 20),
            CancellationToken.None);

        rows.Should().HaveCount(2);
        rows[0].MatchId.Should().Be(102);
        rows[1].MatchId.Should().Be(101);
    }

    [Fact]
    public void BuildDialectSql_GeneratesSchemaQualifiedSqlPerProvider()
    {
        string pgSql = DapperMatchStatsReadProjectionService.BuildDialectSql("Npgsql.EntityFrameworkCore.PostgreSQL");
        pgSql.Should().Contain("stat.\"PlayerStatistics\"");
        pgSql.Should().Contain("stat.\"MatchStatistics\"");
        pgSql.Should().Contain("@AccountId");
        pgSql.Should().Contain("LIMIT @Limit");

        string sqliteSql = DapperMatchStatsReadProjectionService.BuildDialectSql("Microsoft.EntityFrameworkCore.Sqlite");
        sqliteSql.Should().Contain("FROM \"PlayerStatistics\"");
        sqliteSql.Should().Contain("LIMIT @Limit");

        string mssqlSql = DapperMatchStatsReadProjectionService.BuildDialectSql("Microsoft.EntityFrameworkCore.SqlServer");
        mssqlSql.Should().Contain("TOP (@Limit)");
        mssqlSql.Should().Contain("[stat].[PlayerStatistics]");
    }

    [Fact]
    public void ParseTimestamp_ConvertsLocalDateTimeToTrueUtc_AndLogsWarningOnFallback()
    {
        DateTime localDt = new(2026, 11, 19, 12, 30, 0, DateTimeKind.Local);
        DateTimeOffset expectedUtc = new(localDt.ToUniversalTime(), TimeSpan.Zero);

        DateTimeOffset parsedLocal = DapperMatchStatsReadProjectionService.ParseTimestamp(localDt);
        parsedLocal.Should().Be(expectedUtc);
        parsedLocal.Offset.Should().Be(TimeSpan.Zero);

        DateTime utcDt = new(2026, 11, 19, 12, 30, 0, DateTimeKind.Utc);
        DapperMatchStatsReadProjectionService.ParseTimestamp(utcDt)
            .Should().Be(new DateTimeOffset(2026, 11, 19, 12, 30, 0, TimeSpan.Zero));

        DateTime unspecifiedDt = new(2026, 11, 19, 12, 30, 0, DateTimeKind.Unspecified);
        DapperMatchStatsReadProjectionService.ParseTimestamp(unspecifiedDt)
            .Should().Be(new DateTimeOffset(2026, 11, 19, 12, 30, 0, TimeSpan.Zero));

        DateTimeOffset offsetDt = new(2026, 11, 19, 18, 0, 0, TimeSpan.FromHours(5.5));
        DapperMatchStatsReadProjectionService.ParseTimestamp(offsetDt)
            .Should().Be(new DateTimeOffset(2026, 11, 19, 12, 30, 0, TimeSpan.Zero));

        Mock<ILogger> loggerMock = new();
        DateTimeOffset fallback = DapperMatchStatsReadProjectionService.ParseTimestamp("not-a-timestamp", loggerMock.Object);
        fallback.Should().Be(DateTimeOffset.UnixEpoch);
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("falling back to DateTimeOffset.UnixEpoch", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task PlayerStatsApiController_HistoryEndpoint_ClampsLimitAndDispatchesMediatR()
    {
        Mock<IMediator> mediatorMock = new();
        IReadOnlyList<PlayerMatchHistoryProjectionDto> expected =
        [
            new PlayerMatchHistoryProjectionDto(500, 42, "Ace", "Hero_Pyromancer", "caldavar", "ap", 1800, 1, true, 10, 2, 7, 15000, 2400, true, TimeProvider.System.GetUtcNow())
        ];

        mediatorMock
            .Setup(m => m.Send(
                It.Is<GetPlayerMatchHistoryProjectionQuery>(q => q.AccountId == 42 && q.Limit == 100),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        PlayerStatsApiController controller = new(mediatorMock.Object);
        IActionResult result = await controller.GetPlayerMatchHistoryProjection(42, limit: 999);

        OkObjectResult ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-999)]
    public async Task PlayerStatsApiController_HistoryEndpoint_RejectsNonPositiveAccountId(int invalidAccountId)
    {
        Mock<IMediator> mediatorMock = new();
        PlayerStatsApiController controller = new(mediatorMock.Object);

        IActionResult result = await controller.GetPlayerMatchHistoryProjection(invalidAccountId, limit: 20);

        BadRequestObjectResult badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().Be("Invalid account ID.");
        mediatorMock.Verify(
            m => m.Send(It.IsAny<GetPlayerMatchHistoryProjectionQuery>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

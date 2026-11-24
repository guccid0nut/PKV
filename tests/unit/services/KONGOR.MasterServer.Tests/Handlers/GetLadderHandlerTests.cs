// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Enumerations.Statistics;
using FluentAssertions;
using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Tests.Infrastructure;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NEXUS.Contracts.Domain;
using Xunit;

namespace KONGOR.MasterServer.Tests.Handlers;

public sealed class GetLadderHandlerTests
{
    private static async Task SeedHundredRankedAccountsAsync(MerrickContext context)
    {
        User user = new()
        {
            EmailAddress = "ladder@projectkongor.com",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            DiscordId = "123456789012345678",
            Role = new Role { ID = 2, Name = "User" }
        };
        context.Users.Add(user);

        Clan clan = new()
        {
            Name = "Kongor Vanguard",
            Tag = "KNG"
        };
        context.Clans.Add(clan);
        await context.SaveChangesAsync();

        for (int i = 1; i <= 105; i++)
        {
            Account account = new()
            {
                Name = $"LadderHero_{i:D3}",
                User = user,
                Type = AccountType.Normal,
                IsMain = i == 1,
                Clan = i % 2 == 0 ? clan : null,
                ClanTier = i % 2 == 0 ? ClanTier.Member : ClanTier.None,
                TimestampJoinedClan = DateTimeOffset.UtcNow,
                SelectedStoreItems = ["ai.Default Icon"]
            };
            context.Accounts.Add(account);

            int focPlayed = 50 + i;
            int focWon = 30 + (i % 20);
            context.AccountStatistics.Add(new AccountStatistics
            {
                Account = account,
                Type = AccountStatisticsType.Matchmaking,
                MatchesPlayed = focPlayed,
                MatchesWon = focWon,
                MatchesLost = focPlayed - focWon,
                SkillRating = 2100.0 - (i * 5.0),
                PerformanceScore = 4.0
            });

            int mwPlayed = 40 + i;
            int mwWon = 25 + (i % 15);
            context.AccountStatistics.Add(new AccountStatistics
            {
                Account = account,
                Type = AccountStatisticsType.MidWars,
                MatchesPlayed = mwPlayed,
                MatchesWon = mwWon,
                MatchesLost = mwPlayed - mwWon,
                SkillRating = 1950.0 - (i * 4.0),
                PerformanceScore = 3.8
            });
        }

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task HandleRequestAsync_ReturnsRequestedPageOfTenRows_CappedAtTopHundredStandings()
    {
        // Arrange (LADDER-04)
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        await SeedHundredRankedAccountsAsync(context);

        GetLadderHandler handler = new(context, NullLogger<GetLadderHandler>.Instance);

        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["mode"] = "season",
            ["page"] = "2",
            ["page_size"] = "10"
        });

        // Act
        IActionResult actionResult = await handler.HandleRequestAsync(httpContext, CancellationToken.None);

        // Assert
        ContentResult contentResult = actionResult.Should().BeOfType<ContentResult>().Subject;
        string serialized = contentResult.Content ?? string.Empty;
        serialized.Should().NotBeNullOrWhiteSpace();
        serialized.Should().Contain(@"s:4:""mode"";s:6:""season""");
        serialized.Should().Contain(@"s:4:""page"";s:1:""2""");
        serialized.Should().Contain(@"s:13:""total_entries"";s:3:""100""");
        serialized.Should().Contain(@"s:11:""total_pages"";s:2:""10""");
        serialized.Should().Contain(@"s:5:""count"";s:2:""10""");
        serialized.Should().Contain(@"s:4:""rank"";s:2:""11""");
        serialized.Should().Contain(@"s:8:""nickname"";s:14:""LadderHero_011""");
        serialized.Should().Contain(@"s:3:""mmr"";s:4:""2045""");
        serialized.Should().Contain(@"s:4:""rank"";s:2:""20""");
        serialized.Should().Contain(@"s:8:""nickname"";s:14:""LadderHero_020""");
        serialized.Should().Contain(@"s:3:""mmr"";s:4:""2000""");
        serialized.Should().Contain(@"s:11:""entries_str"";");
        serialized.Should().Contain("11|LadderHero_011|LadderHero_011|2045|");
        serialized.Should().Contain("12|[KNG] LadderHero_012|LadderHero_012|2040|");
        serialized.Should().NotContain("LadderHero_010");
        serialized.Should().NotContain("LadderHero_021");
    }

    [Fact]
    public async Task HandleRequestAsync_SupportsMidwarsMode_AndReturnsMidwarsStandings()
    {
        // Arrange
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        await SeedHundredRankedAccountsAsync(context);

        GetLadderHandler handler = new(context, NullLogger<GetLadderHandler>.Instance);

        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["mode"] = "midwars",
            ["page"] = "1",
            ["page_size"] = "10"
        });

        // Act
        IActionResult actionResult = await handler.HandleRequestAsync(httpContext, CancellationToken.None);

        // Assert
        ContentResult contentResult = actionResult.Should().BeOfType<ContentResult>().Subject;
        string serialized = contentResult.Content ?? string.Empty;
        serialized.Should().Contain(@"s:4:""mode"";s:7:""midwars""");
        serialized.Should().Contain(@"s:4:""page"";s:1:""1""");
        serialized.Should().Contain(@"s:13:""total_entries"";s:3:""100""");
        serialized.Should().Contain(@"s:4:""rank"";s:1:""1""");
        serialized.Should().Contain(@"s:8:""nickname"";s:14:""LadderHero_001""");
        serialized.Should().Contain(@"s:3:""mmr"";s:4:""1946""");
    }

    [Fact]
    public async Task HandleRequestAsync_SupportsCodexMode_AndReturnsRealAvatarWallStandings()
    {
        // protects: LADDER -> CODEX tab integration with GetLadderHandler (Option A)
        // fails_when: mode=codex falls back to season MMR or leaks synthetic GUEST-* rows ahead of real collectors
        // seam: GetLadderHandler -> CodexGraffitiController.QueryGraffitiStandingsAsync -> PHP serialization
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        await SeedHundredRankedAccountsAsync(context);

        Account topCollector = context.Accounts.First(a => a.Name == "LadderHero_002");
        string[] punkAndNonPunkAvatars =
        [
            "Hero_Pyromancer.Alt8",
            "Hero_Valkyrie.Alt12",
            "Hero_DwarfMagi.Alt6",
            "Hero_Hiro.Alt12",
            "Hero_Nomad.Alt12",
            "Hero_PollywogPriest.Alt7",
            "Hero_DoctorRepulsor.Alt10",
            "aa.Hero_Ravenor.Alt8",
            "aa.Hero_Hammerstorm.Alt11",
            "aa.Hero_Scout.Alt2" // non-Punk avatar must be excluded from codex ladder count
        ];
        foreach (string itemCode in punkAndNonPunkAvatars)
        {
            context.UserInventory.Add(new UserInventory
            {
                UserID = topCollector.UserID,
                AccountID = topCollector.ID,
                ItemCode = itemCode
            });
        }
        await context.SaveChangesAsync();

        GetLadderHandler handler = new(context, NullLogger<GetLadderHandler>.Instance);

        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["mode"] = "codex",
            ["page"] = "1",
            ["page_size"] = "5"
        });

        IActionResult actionResult = await handler.HandleRequestAsync(httpContext, CancellationToken.None);
        ContentResult contentResult = actionResult.Should().BeOfType<ContentResult>().Subject;
        string serialized = contentResult.Content ?? string.Empty;

        serialized.Should().Contain(@"s:4:""mode"";s:5:""codex""");
        serialized.Should().Contain("1|[KNG] LadderHero_002|LadderHero_002|2|9|");
        serialized.Should().Contain("Graffiti Master");
    }
}

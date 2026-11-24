// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Common.Enumerations.Statistics;
using FluentAssertions;
using KONGOR.MasterServer.Controllers.Plinko;
using KONGOR.MasterServer.Services.Plinko;
using KONGOR.MasterServer.Services.Security;
using KONGOR.MasterServer.Tests.Infrastructure;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Security;
using Xunit;

namespace KONGOR.MasterServer.Tests.Controllers;

public sealed class CodexGraffitiControllerTests
{
    private static async Task<(User User, Account Account)> SeedCollectorAccountAsync(
        MerrickContext context,
        string accountName,
        string[] ownedItemCodes,
        int roleId = 2)
    {
        Role? role = await context.Roles.FindAsync(roleId);
        if (role is null)
        {
            role = new Role { ID = roleId, Name = "User" };
            context.Roles.Add(role);
        }

        User user = new()
        {
            EmailAddress = $"{accountName.ToLowerInvariant()}@projectkongor.com",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            DiscordId = $"90000000000000{Random.Shared.Next(1000, 9999)}",
            Role = role,
            PlinkoTickets = 10
        };
        context.Users.Add(user);

        Account account = new()
        {
            Name = accountName,
            User = user,
            Type = AccountType.Normal,
            IsMain = true,
            SelectedStoreItems = ["ai.Default Icon"]
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        foreach (string itemCode in ownedItemCodes)
        {
            context.UserInventory.Add(new UserInventory
            {
                UserID = user.ID,
                AccountID = account.ID,
                ItemCode = itemCode,
                Quantity = 1,
                IsPermanent = true
            });
        }

        context.AccountStatistics.Add(new AccountStatistics
        {
            Account = account,
            Type = AccountStatisticsType.Matchmaking,
            MatchesPlayed = 30,
            MatchesWon = 20,
            MatchesLost = 10,
            SkillRating = 1750.0,
            AwardMostAnnihilations = 2,
            AwardMostQuadKills = 3
        });

        await context.SaveChangesAsync();
        return (user, account);
    }

    private static CodexGraffitiController CreateController(
        MerrickContext context,
        Account? resolvedAccount,
        Dictionary<string, StringValues>? formFields = null)
    {
        Mock<ISessionContextResolver> sessionMock = new();
        sessionMock
            .Setup(s => s.ResolveAccountAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedAccount);

        Mock<IAccountRepository> repoMock = new();
        repoMock
            .Setup(r => r.UpdateUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(formFields ?? []);

        CodexGraffitiService graffitiService = new(
            context,
            repoMock.Object,
            NullLogger<CodexGraffitiService>.Instance);

        return new CodexGraffitiController(
            graffitiService,
            sessionMock.Object,
            NullLogger<CodexGraffitiController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };
    }

    [Fact]
    public async Task GetGraffitiWallList_EnforcesOfficial12AvatarPunkSet_ExcludesNonPunkAvatars_AndPaginatesSevenPerWall()
    {
        // protects: codex.lua OnGraffitiWallsResult JSON schema + strict 12-avatar Set 61 Punk Set filter
        // fails_when: PHP serialization is returned instead of JSON, non-Punk alt avatars leak onto walls, or base .Hero items leak onto walls
        // seam: CodexGraffitiController -> MerrickContext (EF Core InMemory) -> System.Text.Json serialization
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();

        string[] owned =
        [
            "aa.Hero_Scout.Alt2",       // non-Punk (must be excluded)
            "Hero_Pyromancer.Alt8",     // Multi-level Punk #1
            "aa.Hero_Ravenor.Alt8",     // One-level Punk #1
            "Hero_Aluna.Alt3",          // non-Punk (must be excluded)
            "Hero_Gladiator.Alt9",      // non-Punk (must be excluded)
            "Hero_Valkyrie.Alt12",      // Multi-level Punk #2
            "Hero_DwarfMagi.Alt6",      // Multi-level Punk #3
            "Hero_Hiro.Alt12",          // Multi-level Punk #4
            "Hero_Nomad.Alt12",         // Multi-level Punk #5
            "Hero_Hammerstorm.Alt11",   // One-level Punk #2
            "Hero_FlintBeastwood.Alt13",// One-level Punk #3
            "Hero_Chronos.Alt9",        // One-level Punk #4
            "Hero_Scout.Hero",          // base hero (must be excluded)
            "Hero_Chronos.Alt4",        // non-Punk (must be excluded)
            "Hero_Ra.Alt5",             // non-Punk (must be excluded)
            "Hero_WitchSlayer.Alt2"     // non-Punk (must be excluded)
        ];
        (_, Account account) = await SeedCollectorAccountAsync(context, "PunkCollector", owned);

        CodexGraffitiController controller = CreateController(context, account, new Dictionary<string, StringValues>
        {
            ["req_type"] = "normal"
        });

        IActionResult actionResult = await controller.GetGraffitiWallList(CancellationToken.None);
        JsonResult jsonResult = actionResult.Should().BeOfType<JsonResult>().Subject;

        string json = JsonSerializer.Serialize(jsonResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("page_count").GetInt32().Should().Be(2);
        root.GetProperty("page").GetInt32().Should().Be(1);
        root.GetProperty("graffiti_count_today").GetInt32().Should().Be(7);
        root.GetProperty("account").GetProperty("nickname").GetString().Should().Be("PunkCollector");

        JsonElement walls = root.GetProperty("data");
        walls.GetArrayLength().Should().Be(2);

        JsonElement firstWallInfos = walls[0].GetProperty("infos");
        firstWallInfos.GetArrayLength().Should().Be(7);

        // Owned Multi-level Punk Set first (level = 5), then owned One-level Punk Set (level = 1)
        firstWallInfos[0].GetProperty("layout_info").GetProperty("avatar").GetProperty("name").GetString()
            .Should().Be("Hero_Pyromancer.Alt8");
        firstWallInfos[0].GetProperty("layout_info").GetProperty("avatar").GetProperty("level").GetInt32()
            .Should().Be(5);
        firstWallInfos[0].GetProperty("layout_info").GetProperty("kill").GetString()
            .Should().Be("Annihilation");

        firstWallInfos[1].GetProperty("layout_info").GetProperty("avatar").GetProperty("name").GetString()
            .Should().Be("Hero_Valkyrie.Alt12");
        firstWallInfos[5].GetProperty("layout_info").GetProperty("avatar").GetProperty("name").GetString()
            .Should().Be("Hero_Ravenor.Alt8");
        firstWallInfos[5].GetProperty("layout_info").GetProperty("avatar").GetProperty("level").GetInt32()
            .Should().Be(1);

        JsonElement secondWallInfos = walls[1].GetProperty("infos");
        secondWallInfos.GetArrayLength().Should().Be(5); // Always all 12 official Set 61 Punk avatars (9 owned first + 3 remaining Set 61 filled without duplicates)

        List<string> renderedNames = firstWallInfos.EnumerateArray()
            .Concat(secondWallInfos.EnumerateArray())
            .Select(info => info.GetProperty("layout_info").GetProperty("avatar").GetProperty("name").GetString()!)
            .ToList();
        renderedNames.Should().HaveCount(12);
        renderedNames.Should().OnlyHaveUniqueItems();
        renderedNames.Should().OnlyContain(name => CodexGraffitiService.IsOfficialPunkSetAvatar(name));
        renderedNames.Should().Equal(
            "Hero_Pyromancer.Alt8",
            "Hero_Valkyrie.Alt12",
            "Hero_DwarfMagi.Alt6",
            "Hero_Hiro.Alt12",
            "Hero_Nomad.Alt12",
            "Hero_Ravenor.Alt8",
            "Hero_Hammerstorm.Alt11",
            "Hero_FlintBeastwood.Alt13",
            "Hero_Chronos.Alt9",
            "Hero_PollywogPriest.Alt7",
            "Hero_DoctorRepulsor.Alt10",
            "Hero_Rampage.Alt14");
        renderedNames.Should().NotContain(["Hero_Scout.Alt2", "Hero_Aluna.Alt3", "Hero_Gladiator.Alt9", "Hero_Chronos.Alt4", "Hero_Ra.Alt5", "Hero_WitchSlayer.Alt2", "Hero_Scout.Hero"]);
    }

    [Fact]
    public async Task GetGraffitiWallList_And_GetLadder_ExcludeNonPunkOwnedAltAvatars()
    {
        // protects: strict 12-avatar Set 61 Punk Set showcase on walls (owned first + remaining Set 61) and owned-only Set 61 counts on ladder
        // fails_when: non-Punk VICTORY fallback avatars appear on walls, fewer than 12 Punk avatars populate walls, or ladder inflates counts beyond owned Set 61
        // seam: CodexGraffitiController.GetGraffitiWallList + GetLadder + QueryGraffitiStandingsAsync -> MerrickContext
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();

        (_, Account nonPunkOnly) = await SeedCollectorAccountAsync(
            context,
            "NonPunkOnly",
            ["aa.Hero_Scout.Alt2", "Hero_Aluna.Alt3", "Hero_Chronos.Alt4"]);

        (_, Account mixedCollector) = await SeedCollectorAccountAsync(
            context,
            "MixedPunkCollector",
            ["Hero_Pyromancer.Alt8", "aa.Hero_Ravenor.Alt8", "Hero_Scout.Alt2", "Hero_Ra.Alt5", "Hero_WitchSlayer.Alt2"]);

        // Mixed collector wall puts the 2 owned Set 61 Punk avatars first, then fills remaining 10 official Set 61 avatars across 2 walls (7 + 5)
        CodexGraffitiController mixedController = CreateController(context, mixedCollector);
        JsonResult mixedRes = (await mixedController.GetGraffitiWallList(CancellationToken.None)).Should().BeOfType<JsonResult>().Subject;
        using (JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(mixedRes.Value)))
        {
            doc.RootElement.GetProperty("page_count").GetInt32().Should().Be(2);
            doc.RootElement.GetProperty("graffiti_count_today").GetInt32().Should().Be(7);
            JsonElement walls = doc.RootElement.GetProperty("data");
            walls.GetArrayLength().Should().Be(2);
            walls[0].GetProperty("infos").GetArrayLength().Should().Be(7);
            walls[1].GetProperty("infos").GetArrayLength().Should().Be(5);
            List<string> names = walls[0].GetProperty("infos").EnumerateArray()
                .Concat(walls[1].GetProperty("infos").EnumerateArray())
                .Select(i => i.GetProperty("layout_info").GetProperty("avatar").GetProperty("name").GetString()!)
                .ToList();
            names.Should().HaveCount(12);
            names.Should().OnlyHaveUniqueItems();
            names.Should().OnlyContain(name => CodexGraffitiService.IsOfficialPunkSetAvatar(name));
            names.Take(2).Should().Equal("Hero_Pyromancer.Alt8", "Hero_Ravenor.Alt8");
            names.Should().NotContain(["Hero_Scout.Alt2", "Hero_Ra.Alt5", "Hero_WitchSlayer.Alt2"]);
        }

        // Account with ONLY non-Punk avatars has 0 owned Set 61 Punk avatars, so wall populates all 12 official Set 61 avatars (7 + 5)
        CodexGraffitiController nonPunkController = CreateController(context, nonPunkOnly);
        JsonResult nonPunkRes = (await nonPunkController.GetGraffitiWallList(CancellationToken.None)).Should().BeOfType<JsonResult>().Subject;
        using (JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(nonPunkRes.Value)))
        {
            doc.RootElement.GetProperty("page_count").GetInt32().Should().Be(2);
            doc.RootElement.GetProperty("graffiti_count_today").GetInt32().Should().Be(7);
            doc.RootElement.GetProperty("data")[0].GetProperty("infos").GetArrayLength().Should().Be(7);
            doc.RootElement.GetProperty("data")[1].GetProperty("infos").GetArrayLength().Should().Be(5);
        }

        // Ladder counts ONLY Set 61 Punk avatars (MixedPunkCollector = 2/12, NonPunkOnly = 0/12)
        JsonResult ladderRes = (await mixedController.GetLadder(CancellationToken.None)).Should().BeOfType<JsonResult>().Subject;
        using (JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(ladderRes.Value)))
        {
            JsonElement rows = doc.RootElement.GetProperty("data");
            rows.GetArrayLength().Should().Be(2);
            rows[0].GetProperty("nickname").GetString().Should().Be("MixedPunkCollector");
            rows[0].GetProperty("avatars_count").GetInt32().Should().Be(2);
            rows[0].GetProperty("level_max").GetInt32().Should().Be(1);
            rows[1].GetProperty("nickname").GetString().Should().Be("NonPunkOnly");
            rows[1].GetProperty("avatars_count").GetInt32().Should().Be(0);
            rows[1].GetProperty("level_max").GetInt32().Should().Be(0);
        }
    }

    [Fact]
    public async Task GetGraffitiWallList_PopulatesStarterPunkShowcase_WhenAccountOwnsZeroAltAvatars()
    {
        // protects: non-empty 3D Graffiti Wall experience for brand-new accounts with 0 owned alt avatars
        // fails_when: 0-avatar accounts receive data=[] causing an empty brick wall in codex.package
        // seam: CodexGraffitiController -> MerrickContext
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        (_, Account account) = await SeedCollectorAccountAsync(context, "FreshPlayer", []);

        CodexGraffitiController controller = CreateController(context, account);
        JsonResult jsonResult = (await controller.GetGraffitiWallList(CancellationToken.None)).Should().BeOfType<JsonResult>().Subject;

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(jsonResult.Value));
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("page_count").GetInt32().Should().Be(2); // 12 Punk Set avatars -> 7 + 5
        doc.RootElement.GetProperty("data")[0].GetProperty("infos").GetArrayLength().Should().Be(7);
        doc.RootElement.GetProperty("data")[1].GetProperty("infos").GetArrayLength().Should().Be(5);
    }

    [Fact]
    public async Task UpdateGraffitiInfo_PersistsPoseOverrides_AndRejectsUnauthenticatedWrites()
    {
        // protects: 3D rotation, animation clip selection, and frame-scrubbing persistence across sessions + auth boundary
        // fails_when: updategraffitiinfo ignores form["data"] JSON or allows unauthenticated pose overwrites
        // seam: CodexGraffitiController.UpdateGraffitiInfo -> UserWebRewards -> GetGraffitiWallList round-trip
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        (_, Account account) = await SeedCollectorAccountAsync(context, "PoseArtist", ["Hero_Pyromancer.Alt8"]);

        CodexGraffitiController unauthController = CreateController(context, null, new Dictionary<string, StringValues>
        {
            ["data"] = @"[{""graffiti_id"":1,""rot"":45,""frame"":0.75,""ani"":3}]"
        });
        JsonResult unauthResult = (await unauthController.UpdateGraffitiInfo(CancellationToken.None)).Should().BeOfType<JsonResult>().Subject;
        using (JsonDocument unauthDoc = JsonDocument.Parse(JsonSerializer.Serialize(unauthResult.Value)))
        {
            unauthDoc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        }

        CodexGraffitiController authController = CreateController(context, account, new Dictionary<string, StringValues>
        {
            ["data"] = @"[{""graffiti_id"":1,""rot"":122.5,""frame"":0.82,""ani"":4}]"
        });
        JsonResult updateResult = (await authController.UpdateGraffitiInfo(CancellationToken.None)).Should().BeOfType<JsonResult>().Subject;
        using (JsonDocument updateDoc = JsonDocument.Parse(JsonSerializer.Serialize(updateResult.Value)))
        {
            updateDoc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            updateDoc.RootElement.GetProperty("updated").GetInt32().Should().Be(1);
        }

        CodexGraffitiController readController = CreateController(context, account);
        JsonResult listResult = (await readController.GetGraffitiWallList(CancellationToken.None)).Should().BeOfType<JsonResult>().Subject;
        using JsonDocument listDoc = JsonDocument.Parse(JsonSerializer.Serialize(listResult.Value));
        JsonElement slotAvatar = listDoc.RootElement
            .GetProperty("data")[0]
            .GetProperty("infos")[0]
            .GetProperty("layout_info")
            .GetProperty("avatar");

        slotAvatar.GetProperty("rot").GetDouble().Should().BeApproximately(122.5, 0.001);
        slotAvatar.GetProperty("frame").GetDouble().Should().BeApproximately(0.82, 0.001);
        slotAvatar.GetProperty("ani").GetInt32().Should().Be(4);
    }

    [Fact]
    public async Task RouletteAndLadderEndpoints_ReturnExactCodexLuaJsonContracts_AndFilterSyntheticGuestRows()
    {
        // protects: 68-slot roulette wheel, Unix timestamp lottery history, and real collector ranking in codex.lua
        // fails_when: lottery_list has != 68 items, history omits Unix create_time, or GUEST-* bots crowd out real accounts
        // seam: GetLotteryList / RollLottery / GetLotteryHistory / GetLadder -> MerrickContext
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        (_, Account realPlayer) = await SeedCollectorAccountAsync(
            context,
            "RealCollector",
            ["Hero_Pyromancer.Alt8", "Hero_Valkyrie.Alt12", "Hero_Hiro.Alt12", "Hero_Nomad.Alt12", "Hero_Ravenor.Alt8", "Hero_Chronos.Alt9", "Hero_Rampage.Alt14", "Hero_Scout.Alt2"]);
        await SeedCollectorAccountAsync(context, "GUEST-21", ["Hero_Pyromancer.Alt8"]);

        CodexGraffitiController controller = CreateController(context, realPlayer);

        JsonResult lotteryListRes = (await controller.GetLotteryList(CancellationToken.None)).Should().BeOfType<JsonResult>().Subject;
        using (JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(lotteryListRes.Value)))
        {
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("data").GetProperty("lottery_list").GetArrayLength().Should().Be(68);
        }

        JsonResult rollRes = (await controller.RollLottery(CancellationToken.None)).Should().BeOfType<JsonResult>().Subject;
        int wonGiftId;
        using (JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(rollRes.Value)))
        {
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            wonGiftId = doc.RootElement.GetProperty("data").GetProperty("gift_id").GetInt32();
            wonGiftId.Should().BeInRange(1, 68);
        }

        JsonResult historyRes = (await controller.GetLotteryHistory(CancellationToken.None)).Should().BeOfType<JsonResult>().Subject;
        using (JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(historyRes.Value)))
        {
            JsonElement hist = doc.RootElement.GetProperty("data");
            hist.GetArrayLength().Should().Be(1);
            hist[0].GetProperty("gift_id").GetInt32().Should().Be(wonGiftId);
            hist[0].GetProperty("create_time").GetInt64().Should().BeGreaterThan(1_700_000_000L);
        }

        JsonResult ladderRes = (await controller.GetLadder(CancellationToken.None)).Should().BeOfType<JsonResult>().Subject;
        using (JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(ladderRes.Value)))
        {
            JsonElement rows = doc.RootElement.GetProperty("data");
            rows.GetArrayLength().Should().Be(1);
            rows[0].GetProperty("nickname").GetString().Should().Be("RealCollector");
            rows[0].GetProperty("avatars_count").GetInt32().Should().Be(7); // 7 Set 61 Punk avatars (Hero_Scout.Alt2 excluded)
            rows[0].GetProperty("level_max").GetInt32().Should().Be(1);     // ceil(7 / 7) = 1 wall
        }
    }

    [Fact]
    public async Task RollLottery_HonorsRequestedUnclaimedGiftId_AndStandingsAggregateCasualAndMidWarsWins()
    {
        // protects: direct hex-tile claiming on Punk Play board (gift_id form field) + multi-mode Win Rate on LADDER -> CODEX
        // fails_when: RollLottery ignores gift_id and rolls randomly, or QueryGraffitiStandingsAsync drops Casual/Public/MidWars wins
        // seam: CodexGraffitiController.RollLottery + QueryGraffitiStandingsAsync -> MerrickContext
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        (User user, Account collector) = await SeedCollectorAccountAsync(
            context,
            "MidWarsCollector",
            ["Hero_Pyromancer.Alt8", "Hero_Valkyrie.Alt12"]);

        // Zero out Matchmaking wins and record MidWars/Public wins instead
        AccountStatistics mmRow = context.AccountStatistics.Single(s => s.AccountID == collector.ID);
        mmRow.MatchesWon = 0;
        mmRow.MatchesLost = 0;
        context.AccountStatistics.Add(new AccountStatistics
        {
            Account = collector,
            Type = AccountStatisticsType.Public,
            MatchesPlayed = 15,
            MatchesWon = 12,
            MatchesLost = 3,
            SkillRating = 1600.0
        });
        await context.SaveChangesAsync();

        CodexGraffitiController claimController = CreateController(context, collector, new Dictionary<string, StringValues>
        {
            ["gift_id"] = "24" // Plinko 120 ticket hex tile
        });

        int initialTickets = user.PlinkoTickets;
        JsonResult claimRes = (await claimController.RollLottery(CancellationToken.None)).Should().BeOfType<JsonResult>().Subject;
        using (JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(claimRes.Value)))
        {
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("data").GetProperty("gift_id").GetInt32().Should().Be(24);
            doc.RootElement.GetProperty("data").GetProperty("super_owned").GetInt32().Should().Be(0);
        }
        user.PlinkoTickets.Should().Be(initialTickets + 120);

        // Requesting gift_id=24 a second time (now claimed) falls back cleanly to another unclaimed tile
        CodexGraffitiController secondClaim = CreateController(context, collector, new Dictionary<string, StringValues>
        {
            ["gift_id"] = "24"
        });
        JsonResult secondRes = (await secondClaim.RollLottery(CancellationToken.None)).Should().BeOfType<JsonResult>().Subject;
        using (JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(secondRes.Value)))
        {
            doc.RootElement.GetProperty("data").GetProperty("gift_id").GetInt32().Should().NotBe(24);
        }

        List<GraffitiLadderStanding> standings =
            await CodexGraffitiService.QueryGraffitiStandingsAsync(context, 10, CancellationToken.None);
        standings.Should().ContainSingle();
        standings[0].Wins.Should().Be(12);
        standings[0].Losses.Should().Be(3);
    }
}

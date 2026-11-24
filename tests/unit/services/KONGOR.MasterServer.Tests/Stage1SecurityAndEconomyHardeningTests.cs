// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections;
using System.Data.Common;
using System.Globalization;
using Amazon.S3;
using Amazon.S3.Model;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Constants;
using ASPIRE.Common.Enumerations.Statistics;
using ASPIRE.Common.Models.ServerManagement;
using ASPIRE.Common.Serialization.PHP;
using ASPIRE.Common.Services.Mastery;
using FluentAssertions;
using KONGOR.MasterServer.Configuration;
using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Helpers.Stats;
using KONGOR.MasterServer.Models.RequestResponse.GameData;
using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Mastery;
using KONGOR.MasterServer.Services.Requester;
using KONGOR.MasterServer.Services.Security;
using KONGOR.MasterServer.Services.Stats;
using KONGOR.MasterServer.Services.Storage;
using KONGOR.MasterServer.Services.Store;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Relational;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Security;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class Stage1SecurityAndEconomyHardeningTests
{
    private static (Account account, User user) CreateTestAccount(string name = "player_one", int accountId = 101)
    {
        User user = new()
        {
            ID = accountId,
            EmailAddress = $"{name}@kongor.net",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            InventoryItems = [],
            Role = new MERRICK.DatabaseContext.Entities.Utility.Role { ID = 2, Name = "User" }
        };

        Account account = new()
        {
            ID = accountId,
            Name = name,
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };

        return (account, user);
    }

    [Fact]
    public void S1a_GetUnlockAllSetting_DoesNotMutateGlobalStaticStateAcrossScopedConfigs()
    {
        // protects: S1a DI-scoped UnlockAllItems resolution without mutating static _globalUnlockAllItems across concurrent requests
        // fails_when: GetUnlockAllSetting(config) overwrites _globalUnlockAllItems when called with Store:UnlockAllItems=false
        // seam: UpgradesResolutionService.GetUnlockAllSetting + UpgradesResolutionService.IsUnlockAllEnabled
        UpgradesResolutionService.UnlockAllItems = true;

        IConfiguration progressionConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Store:UnlockAllItems"] = "false"
            })
            .Build();

        bool resolvedProgression = UpgradesResolutionService.GetUnlockAllSetting(progressionConfig);
        resolvedProgression.Should().BeFalse();

        // Calling GetUnlockAllSetting(null) on another scope without AsyncLocal override must still observe the default true
        UpgradesResolutionService.GetUnlockAllSetting(null).Should().BeTrue(
            "GetUnlockAllSetting(config) must never mutate static _globalUnlockAllItems");

        UpgradesResolutionService scopedService = new(
            NullLogger<UpgradesResolutionService>.Instance,
            storeRepository: null,
            configuration: progressionConfig);
        scopedService.IsUnlockAllEnabled.Should().BeFalse();
    }

    private sealed class TestSqliteDbHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestSqliteDbHarness(SqliteConnection connection)
        {
            _connection = connection;
            Options = new DbContextOptionsBuilder<MerrickContext>()
                .UseSqlite(_connection)
                .Options;
        }

        public DbContextOptions<MerrickContext> Options { get; }

        public static async Task<TestSqliteDbHarness> CreateAsync()
        {
            SqliteConnection connection = new("DataSource=:memory:");
            await connection.OpenAsync();
            connection.CreateCollation("case_insensitive",
                static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));

            TestSqliteDbHarness harness = new(connection);
            await using MerrickContext db = harness.CreateContext();
            await db.Database.EnsureCreatedAsync();
            return harness;
        }

        public MerrickContext CreateContext(params IInterceptor[] interceptors)
        {
            DbContextOptionsBuilder<MerrickContext> builder = new DbContextOptionsBuilder<MerrickContext>()
                .UseSqlite(_connection);
            if (interceptors.Length > 0)
                builder.AddInterceptors(interceptors);

            return new MerrickContext(builder.Options);
        }

        public async Task<Account> SeedAccountAsync(
            string name,
            int accountId,
            int totalMasteryXpOnPyromancer = 0,
            int userTotalLevel = 0,
            IEnumerable<(string ItemCode, int Quantity)>? inventoryItems = null)
        {
            await using MerrickContext db = CreateContext();
            User user = new()
            {
                ID = accountId,
                RoleID = 2,
                EmailAddress = $"{name}@kongor.net",
                PBKDF2PasswordHash = "h",
                SRPPasswordHash = "h",
                SRPPasswordSalt = "s",
                TotalLevel = userTotalLevel,
                InventoryItems = []
            };

            if (inventoryItems != null)
            {
                foreach ((string code, int qty) in inventoryItems)
                {
                    user.InventoryItems.Add(new UserInventory
                    {
                        UserID = user.ID,
                        AccountID = accountId,
                        ItemCode = code,
                        Quantity = qty,
                        IsPermanent = true,
                        Source = "TestSeed"
                    });
                }
            }

            Account account = new()
            {
                ID = accountId,
                UserID = user.ID,
                Name = name,
                User = user,
                Type = AccountType.Normal,
                IsMain = true
            };

            if (totalMasteryXpOnPyromancer > 0)
            {
                Mastery mastery = new()
                {
                    AccountID = accountId,
                    Account = account,
                    HeroExperiences =
                    [
                        new HeroMasteryExperience
                        {
                            HeroIdentifier = "Hero_Pyromancer",
                            Experience = totalMasteryXpOnPyromancer
                        }
                    ]
                };
                account.Mastery = mastery;
            }

            db.Users.Add(user);
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            return account;
        }

        public async Task SeedMatchAsync(int matchId, int accountId, string accountName, string heroIdentifier = "Hero_Pyromancer")
        {
            await using MerrickContext db = CreateContext();
            MatchStatistics match = new()
            {
                MatchID = matchId,
                HostAccountName = accountName,
                Map = "caldavar",
                TimePlayed = 1800,
                FileName = $"M{matchId}.honreplay"
            };
            PlayerStatistics ps = new()
            {
                Match = match,
                MatchID = matchId,
                AccountID = accountId,
                AccountName = accountName,
                HeroIdentifier = heroIdentifier
            };
            db.MatchStatistics.Add(match);
            db.PlayerStatistics.Add(ps);
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }

    private sealed class ConcurrentClaimCollisionInterceptor(int userId, int accountId, string claimKey) : SaveChangesInterceptor
    {
        private bool _triggered;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_triggered && eventData.Context is MerrickContext ctx)
            {
                _triggered = true;
                await ctx.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO UserInventory (UserID, AccountID, ItemCode, Quantity, IsPermanent, TimestampAcquired, Source) VALUES ({userId}, {accountId}, {claimKey}, 1, 1, {TimeProvider.System.GetUtcNow()}, 'ConcurrentRace')",
                    cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task S1b1_ClaimMasteryRewardHandler_BindsToAuthenticatedSession_PersistsClaimMarkerAcrossFreshDbContexts_AndCatchesConcurrentDbUpdateException()
    {
        // protects: S1b-1 ClaimMasteryRewardHandler against cross-account IDOR, double-claim coin multiplication across fresh DbContexts, and concurrent duplicate UQ_UserInventory_UserID_ItemCode collisions
        // fails_when: Handler mutates another account, drops mastery_reward_lv_* due to .AsNoTracking()+UpdateAsync, or lets DbUpdateException escape as 500
        // seam: ClaimMasteryRewardHandler.HandleRequestAsync + AccountRepository + real SQLite MerrickContext
        await using TestSqliteDbHarness harness = await TestSqliteDbHarness.CreateAsync();
        await harness.SeedAccountAsync("victim_user", 201);
        // 5000 XP on Hero_Pyromancer => Level 3 (eligible for Level 2 & Level 3 rewards)
        await harness.SeedAccountAsync("attacker_user", 202, totalMasteryXpOnPyromancer: 5000, userTotalLevel: 3);

        // 1. Authenticated attacker passes victim's nickname -> must reject mismatched nickname and never touch victim
        await using (MerrickContext dbIdor = harness.CreateContext())
        {
            ClaimMasteryRewardHandler idorHandler = new(
                new AccountRepository(dbIdor),
                NullLogger<ClaimMasteryRewardHandler>.Instance);

            DefaultHttpContext idorCtx = new();
            idorCtx.Items["SessionAccountName"] = "attacker_user";
            idorCtx.Request.ContentType = "application/x-www-form-urlencoded";
            idorCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["cookie"] = "attacker_cookie",
                ["nickname"] = "victim_user",
                ["level"] = "2"
            });

            IActionResult idorRes = await idorHandler.HandleRequestAsync(idorCtx);
            idorRes.Should().BeOfType<UnauthorizedObjectResult>();
        }

        await using (MerrickContext dbCheckVictim = harness.CreateContext())
        {
            Account? victimPersisted = await new AccountRepository(dbCheckVictim).GetByNicknameAsync("victim_user");
            victimPersisted!.User.InventoryItems.Should().BeEmpty("cross-account IDOR attempt must never mutate victim inventory");
        }

        // 2. Claim Level 3 (750 SilverCoins) in fresh DbContext #1 -> succeeds and persists mastery_reward_lv_3
        await using (MerrickContext dbClaim1 = harness.CreateContext())
        {
            ClaimMasteryRewardHandler handler1 = new(
                new AccountRepository(dbClaim1),
                NullLogger<ClaimMasteryRewardHandler>.Instance);

            DefaultHttpContext claimCtx1 = new();
            claimCtx1.Items["SessionAccountName"] = "attacker_user";
            claimCtx1.Request.ContentType = "application/x-www-form-urlencoded";
            claimCtx1.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["cookie"] = "attacker_cookie",
                ["level"] = "3"
            });

            PhpResult claim1Php = (await handler1.HandleRequestAsync(claimCtx1)).Should().BeOfType<PhpResult>().Subject;
            IDictionary claim1Payload = claim1Php.Data.Should().BeAssignableTo<IDictionary>().Subject;
            claim1Payload["reward_status"].Should().Be("OK");
            claim1Payload["error_code"].Should().Be(0);
            claim1Payload["reward"].Should().Be(true);
        }

        await using (MerrickContext dbVerify1 = harness.CreateContext())
        {
            Account? persistedAfter1 = await new AccountRepository(dbVerify1).GetByNicknameAsync("attacker_user");
            persistedAfter1!.User.SilverCoins.Should().Be(750);
            persistedAfter1.User.InventoryItems.Should().Contain(i => i.ItemCode == "mastery_reward_lv_3",
                "claim marker must actually persist to UserInventory so a fresh DbContext rejects replay");
        }

        // 3. Re-claim Level 3 across a SECOND fresh DbContext -> MUST return ALREADY_CLAIMED and NOT double SilverCoins
        await using (MerrickContext dbClaim2 = harness.CreateContext())
        {
            ClaimMasteryRewardHandler handler2 = new(
                new AccountRepository(dbClaim2),
                NullLogger<ClaimMasteryRewardHandler>.Instance);

            DefaultHttpContext claimCtx2 = new();
            claimCtx2.Items["SessionAccountName"] = "attacker_user";
            claimCtx2.Request.ContentType = "application/x-www-form-urlencoded";
            claimCtx2.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["cookie"] = "attacker_cookie",
                ["level"] = "3"
            });

            PhpResult claim2Php = (await handler2.HandleRequestAsync(claimCtx2)).Should().BeOfType<PhpResult>().Subject;
            IDictionary claim2Payload = claim2Php.Data.Should().BeAssignableTo<IDictionary>().Subject;
            claim2Payload["reward_status"].Should().Be("ALREADY_CLAIMED");
            claim2Payload["error_code"].Should().Be(1);
            claim2Payload["reward"].Should().Be(false);
        }

        await using (MerrickContext dbVerify2 = harness.CreateContext())
        {
            Account? persistedAfter2 = await new AccountRepository(dbVerify2).GetByNicknameAsync("attacker_user");
            persistedAfter2!.User.SilverCoins.Should().Be(750, "replaying claim_mastery_reward across fresh DbContexts must not double currency");
        }

        // 4. Concurrent duplicate claim hitting real SQLite UNIQUE index -> returns ALREADY_CLAIMED cleanly and rolls back
        ConcurrentClaimCollisionInterceptor raceInterceptor = new(202, 202, "mastery_reward_lv_2");
        await using (MerrickContext dbRace = harness.CreateContext(raceInterceptor))
        {
            ClaimMasteryRewardHandler raceHandler = new(
                new AccountRepository(dbRace),
                NullLogger<ClaimMasteryRewardHandler>.Instance);

            DefaultHttpContext raceCtx = new();
            raceCtx.Items["SessionAccountName"] = "attacker_user";
            raceCtx.Request.ContentType = "application/x-www-form-urlencoded";
            raceCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["cookie"] = "attacker_cookie",
                ["level"] = "2"
            });

            PhpResult racePhp = (await raceHandler.HandleRequestAsync(raceCtx)).Should().BeOfType<PhpResult>().Subject;
            IDictionary racePayload = racePhp.Data.Should().BeAssignableTo<IDictionary>().Subject;
            racePayload["reward_status"].Should().Be("ALREADY_CLAIMED");
            racePayload["reward"].Should().Be(false);
        }
    }

    [Fact]
    public async Task S1b1b_ClaimMasteryRewardHandler_EnforcesEarnedLevelGate_AllowsLevel0_AndRejectsHigherOrInvalidLevels()
    {
        // protects: S1b-1b Level-0 starter claim eligibility and earned-level gate (preventing level-0 accounts from claiming Level 3/50 rewards)
        // fails_when: Level=0 returns INVALID_LEVEL, Level 3/50 succeeds on Level-0 account, or missing/garbage/negative level fails to return INVALID_LEVEL error_code=2
        // seam: ClaimMasteryRewardHandler.HandleRequestAsync + MasteryRewardCalculator.EvaluateClaim + real SQLite MerrickContext
        await using TestSqliteDbHarness harness = await TestSqliteDbHarness.CreateAsync();
        await harness.SeedAccountAsync("level_zero_hero", 210, totalMasteryXpOnPyromancer: 0, userTotalLevel: 0);

        // 1. Level-0 account claiming level=0 succeeds and grants 5x starter boosts
        await using (MerrickContext db0 = harness.CreateContext())
        {
            ClaimMasteryRewardHandler handler = new(
                new AccountRepository(db0),
                NullLogger<ClaimMasteryRewardHandler>.Instance);

            DefaultHttpContext ctx = new();
            ctx.Items["SessionAccountName"] = "level_zero_hero";
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["cookie"] = "cookie0",
                ["level"] = "0"
            });

            PhpResult php = (await handler.HandleRequestAsync(ctx)).Should().BeOfType<PhpResult>().Subject;
            IDictionary payload = php.Data.Should().BeAssignableTo<IDictionary>().Subject;
            payload["reward_status"].Should().Be("OK");
            payload["error_code"].Should().Be(0);
            payload["0"].Should().Be(true);
            payload["reward"].Should().Be(true);
        }

        await using (MerrickContext dbCheck0 = harness.CreateContext())
        {
            Account? acc = await new AccountRepository(dbCheck0).GetByNicknameAsync("level_zero_hero");
            MasteryConsumables.MasteryBoostsOwned(acc!.User).Should().Be(5, "Level-0 starter reward grants 5x Mastery Boost");
            acc.User.SilverCoins.Should().Be(0);
            acc.User.GoldCoins.Should().Be(0);
        }

        // 2. Level-0 account claiming level=3 or level=50 -> INSUFFICIENT_LEVEL, error_code=2, 0 coin change
        foreach (string unreachedLevel in new[] { "3", "50" })
        {
            await using MerrickContext dbDeny = harness.CreateContext();
            ClaimMasteryRewardHandler handler = new(
                new AccountRepository(dbDeny),
                NullLogger<ClaimMasteryRewardHandler>.Instance);

            DefaultHttpContext ctx = new();
            ctx.Items["SessionAccountName"] = "level_zero_hero";
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["cookie"] = "cookie0",
                ["level"] = unreachedLevel
            });

            PhpResult php = (await handler.HandleRequestAsync(ctx)).Should().BeOfType<PhpResult>().Subject;
            IDictionary payload = php.Data.Should().BeAssignableTo<IDictionary>().Subject;
            payload["reward_status"].Should().Be("INSUFFICIENT_LEVEL", $"level-0 account cannot claim level={unreachedLevel}");
            payload["error_code"].Should().Be(2);
            payload["0"].Should().Be(false);
            payload["reward"].Should().Be(false);
        }

        await using (MerrickContext dbVerifyZeroCoins = harness.CreateContext())
        {
            Account? acc = await new AccountRepository(dbVerifyZeroCoins).GetByNicknameAsync("level_zero_hero");
            acc!.User.SilverCoins.Should().Be(0);
            acc.User.GoldCoins.Should().Be(0);
        }

        // 3. Missing, empty, garbage, or negative level -> INVALID_LEVEL, error_code=2
        foreach (string? badLevel in new string?[] { null, "", "not_a_number", "-1" })
        {
            await using MerrickContext dbInvalid = harness.CreateContext();
            ClaimMasteryRewardHandler handler = new(
                new AccountRepository(dbInvalid),
                NullLogger<ClaimMasteryRewardHandler>.Instance);

            Dictionary<string, StringValues> form = new() { ["cookie"] = "cookie0" };
            if (badLevel != null) form["level"] = badLevel;

            DefaultHttpContext ctx = new();
            ctx.Items["SessionAccountName"] = "level_zero_hero";
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Form = new FormCollection(form);

            PhpResult php = (await handler.HandleRequestAsync(ctx)).Should().BeOfType<PhpResult>().Subject;
            IDictionary payload = php.Data.Should().BeAssignableTo<IDictionary>().Subject;
            payload["reward_status"].Should().Be("INVALID_LEVEL");
            payload["error_code"].Should().Be(2);
            payload["0"].Should().Be(false);
            payload["reward"].Should().Be(false);
        }
    }

    [Fact]
    public async Task S1b2_UseMasteryBoostHandler_GuardsFormOnGet_ConsumesCountedAndLegacyBoostsAtomically_AndRejectsSuperMismatchAcrossFreshDbContexts()
    {
        // protects: S1b-2 UseMasteryBoostHandler against GET 500s, dropped boost decrements/claim markers across fresh DbContexts, missing support for ma.Mastery Boost * N, and using Super Boost on is_super=false
        // fails_when: GET throws on Form, ma.Mastery Boost * 2 returns NO_BOOST_OWNED, Quantity/mb.claimed_match fail to persist, or Super Boost is consumed when is_super=false
        // seam: UseMasteryBoostHandler.HandleRequestAsync + AccountRepository + MatchHistoryRepository + real SQLite MerrickContext
        await using TestSqliteDbHarness harness = await TestSqliteDbHarness.CreateAsync();

        // Player A owns counted boosts ("ma.Mastery Boost * 2") AND matches 7777 & 7778
        await harness.SeedAccountAsync(
            "boost_player",
            301,
            totalMasteryXpOnPyromancer: 0,
            userTotalLevel: 0,
            inventoryItems: [("ma.Mastery Boost * 2", 1)]);
        await harness.SeedMatchAsync(7777, 301, "boost_player", "Hero_Pyromancer");
        await harness.SeedMatchAsync(7778, 301, "boost_player", "Hero_Pyromancer");

        // Player B owns zero boosts
        await harness.SeedAccountAsync("no_boost_player", 302);
        await harness.SeedMatchAsync(7780, 302, "no_boost_player", "Hero_Pyromancer");

        // Player C owns legacy boost ("m.Mastery Boost", Qty=2)
        await harness.SeedAccountAsync(
            "legacy_boost_player",
            303,
            inventoryItems: [("m.Mastery Boost", 2)]);
        await harness.SeedMatchAsync(7781, 303, "legacy_boost_player", "Hero_Pyromancer");

        // Player D owns ONLY Super Mastery Boost ("ma.Super Mastery Boost * 2")
        await harness.SeedAccountAsync(
            "super_only_player",
            304,
            totalMasteryXpOnPyromancer: 1200, // 1200 + 1000 = 2200 => Level 1
            userTotalLevel: 0,
            inventoryItems: [("ma.Super Mastery Boost * 2", 1)]);
        await harness.SeedMatchAsync(7782, 304, "super_only_player", "Hero_Pyromancer");

        Mock<ISessionRepository> mockSessionRepo = new();

        // 1. GET without FormContentType and 0 owned boosts -> no InvalidOperationException, no free XP
        await using (MerrickContext dbGet = harness.CreateContext())
        {
            UseMasteryBoostHandler handler = new(
                new AccountRepository(dbGet),
                new MatchHistoryRepository(dbGet),
                mockSessionRepo.Object);

            DefaultHttpContext getCtx = new();
            getCtx.Request.Method = "GET";
            getCtx.Items["SessionAccountName"] = "no_boost_player";
            getCtx.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
            {
                ["match_id"] = "7780"
            });

            PhpResult getPhp = (await handler.HandleRequestAsync(getCtx)).Should().BeOfType<PhpResult>().Subject;
            IDictionary getDict = getPhp.Data.Should().BeAssignableTo<IDictionary>().Subject;
            getDict["status"].Should().Be("NO_BOOST_OWNED");
        }

        // 2. Redeem counted "ma.Mastery Boost * 2" on Match #7777 -> succeeds, decrements to 1, saves mb.claimed_match:7777 & +240 XP
        await using (MerrickContext dbRedeem1 = harness.CreateContext())
        {
            UseMasteryBoostHandler handler = new(
                new AccountRepository(dbRedeem1),
                new MatchHistoryRepository(dbRedeem1),
                mockSessionRepo.Object);

            DefaultHttpContext postCtx1 = new();
            postCtx1.Items["SessionAccountName"] = "boost_player";
            postCtx1.Request.ContentType = "application/x-www-form-urlencoded";
            postCtx1.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["match_id"] = "7777",
                ["is_super"] = "0"
            });

            PhpResult redeem1 = (await handler.HandleRequestAsync(postCtx1)).Should().BeOfType<PhpResult>().Subject;
            IDictionary redeem1Dict = redeem1.Data.Should().BeAssignableTo<IDictionary>().Subject;
            redeem1Dict["status"].Should().Be("OK");
            redeem1Dict["success"].Should().Be("1");
        }

        await using (MerrickContext dbVerify1 = harness.CreateContext())
        {
            Account? persisted = await new AccountRepository(dbVerify1).GetByNicknameAsync("boost_player");
            persisted!.User.InventoryItems.Should().Contain(i => i.ItemCode == "mb.claimed_match:7777");
            MasteryConsumables.MasteryBoostsOwned(persisted.User).Should().Be(1, "consuming 1 of 2 counted boosts must persist remaining count=1");
            persisted.Mastery!.GetHeroExperienceByHeroIdentifier("Hero_Pyromancer").Should().Be(240);
        }

        // 3. Replaying Match #7777 in fresh DbContext -> ALREADY_CLAIMED, XP stays 240, boost stays 1
        await using (MerrickContext dbReplay = harness.CreateContext())
        {
            UseMasteryBoostHandler handler = new(
                new AccountRepository(dbReplay),
                new MatchHistoryRepository(dbReplay),
                mockSessionRepo.Object);

            DefaultHttpContext postCtx2 = new();
            postCtx2.Items["SessionAccountName"] = "boost_player";
            postCtx2.Request.ContentType = "application/x-www-form-urlencoded";
            postCtx2.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["match_id"] = "7777",
                ["is_super"] = "0"
            });

            PhpResult redeem2 = (await handler.HandleRequestAsync(postCtx2)).Should().BeOfType<PhpResult>().Subject;
            IDictionary redeem2Dict = redeem2.Data.Should().BeAssignableTo<IDictionary>().Subject;
            redeem2Dict["status"].Should().Be("ALREADY_CLAIMED");
        }

        await using (MerrickContext dbVerifyReplay = harness.CreateContext())
        {
            Account? persisted = await new AccountRepository(dbVerifyReplay).GetByNicknameAsync("boost_player");
            MasteryConsumables.MasteryBoostsOwned(persisted!.User).Should().Be(1);
            persisted.Mastery!.GetHeroExperienceByHeroIdentifier("Hero_Pyromancer").Should().Be(240);
        }

        // 4. Legacy boost ("m.Mastery Boost", Qty=2) decrements Quantity -> 1 and persists mb.claimed_match:7781
        await using (MerrickContext dbLegacy = harness.CreateContext())
        {
            UseMasteryBoostHandler handler = new(
                new AccountRepository(dbLegacy),
                new MatchHistoryRepository(dbLegacy),
                mockSessionRepo.Object);

            DefaultHttpContext legacyCtx = new();
            legacyCtx.Items["SessionAccountName"] = "legacy_boost_player";
            legacyCtx.Request.ContentType = "application/x-www-form-urlencoded";
            legacyCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["match_id"] = "7781",
                ["is_super"] = "0"
            });

            PhpResult legacyRes = (await handler.HandleRequestAsync(legacyCtx)).Should().BeOfType<PhpResult>().Subject;
            IDictionary legacyDict = legacyRes.Data.Should().BeAssignableTo<IDictionary>().Subject;
            legacyDict["status"].Should().Be("OK");
        }

        await using (MerrickContext dbVerifyLegacy = harness.CreateContext())
        {
            Account? persisted = await new AccountRepository(dbVerifyLegacy).GetByNicknameAsync("legacy_boost_player");
            UserInventory? legacyRow = persisted!.User.InventoryItems.FirstOrDefault(i => i.ItemCode == "m.Mastery Boost");
            legacyRow.Should().NotBeNull();
            legacyRow!.Quantity.Should().Be(1, "legacy boost quantity must actually decrement from 2 to 1 in DB");
            persisted.User.InventoryItems.Should().Contain(i => i.ItemCode == "mb.claimed_match:7781");
            persisted.Mastery!.GetHeroExperienceByHeroIdentifier("Hero_Pyromancer").Should().Be(240);
        }

        // 5. Owning ONLY "ma.Super Mastery Boost * 2" and calling is_super=false MUST return NO_BOOST_OWNED and keep Super count = 2
        await using (MerrickContext dbMismatch = harness.CreateContext())
        {
            UseMasteryBoostHandler handler = new(
                new AccountRepository(dbMismatch),
                new MatchHistoryRepository(dbMismatch),
                mockSessionRepo.Object);

            DefaultHttpContext mismatchCtx = new();
            mismatchCtx.Items["SessionAccountName"] = "super_only_player";
            mismatchCtx.Request.ContentType = "application/x-www-form-urlencoded";
            mismatchCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["match_id"] = "7782",
                ["is_super"] = "false"
            });

            PhpResult mismatchRes = (await handler.HandleRequestAsync(mismatchCtx)).Should().BeOfType<PhpResult>().Subject;
            IDictionary mismatchDict = mismatchRes.Data.Should().BeAssignableTo<IDictionary>().Subject;
            mismatchDict["status"].Should().Be("NO_BOOST_OWNED", "calling is_super=false must never consume a Super Mastery Boost");
        }

        await using (MerrickContext dbVerifySuperIntact = harness.CreateContext())
        {
            Account? persisted = await new AccountRepository(dbVerifySuperIntact).GetByNicknameAsync("super_only_player");
            MasteryConsumables.SuperMasteryBoostsOwned(persisted!.User).Should().Be(2);
            persisted.User.InventoryItems.Should().NotContain(i => i.ItemCode == "mb.claimed_match:7782");
        }

        // 6. Calling with is_super=1 consumes 1 Super boost, grants +1000 XP, and updates User.TotalLevel atomically
        await using (MerrickContext dbSuper = harness.CreateContext())
        {
            UseMasteryBoostHandler handler = new(
                new AccountRepository(dbSuper),
                new MatchHistoryRepository(dbSuper),
                mockSessionRepo.Object);

            DefaultHttpContext superCtx = new();
            superCtx.Items["SessionAccountName"] = "super_only_player";
            superCtx.Request.ContentType = "application/x-www-form-urlencoded";
            superCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["match_id"] = "7782",
                ["is_super"] = "1"
            });

            PhpResult superRes = (await handler.HandleRequestAsync(superCtx)).Should().BeOfType<PhpResult>().Subject;
            IDictionary superDict = superRes.Data.Should().BeAssignableTo<IDictionary>().Subject;
            superDict["status"].Should().Be("OK");
        }

        await using (MerrickContext dbVerifySuper = harness.CreateContext())
        {
            Account? persisted = await new AccountRepository(dbVerifySuper).GetByNicknameAsync("super_only_player");
            MasteryConsumables.SuperMasteryBoostsOwned(persisted!.User).Should().Be(1);
            persisted.User.InventoryItems.Should().Contain(i => i.ItemCode == "mb.claimed_match:7782");
            persisted.Mastery!.GetHeroExperienceByHeroIdentifier("Hero_Pyromancer").Should().Be(2200);
            persisted.User.TotalLevel.Should().Be(1, "UseMasteryBoostHandler must sync account.User.TotalLevel atomically when mastery XP increases level");
        }
    }

    [Fact]
    public void S1b2b_MatchPlayerStatistics_ComputesMasteryBoostCountsFromCountedAndLegacyRows_AndDisablesCanBoostWhenClaimedOrEmpty()
    {
        // protects: Post-game MasteryBoostNum, MasterySuperBoostNum, MasteryCanBoost, and MasterySuperCanBoost against counting claimed-match markers or ignoring * N quantities
        // fails_when: mb.claimed_match:* counts toward MasteryBoostNum, * N suffix is ignored, or MasteryCanBoost remains "1" for already-claimed matches or 0 boosts
        // seam: MatchPlayerStatistics construction
        (Account account, User user) = CreateTestAccount("postgame_player", 310);
        user.InventoryItems.Add(new UserInventory { UserID = user.ID, ItemCode = "ma.Mastery Boost * 3", Quantity = 1 });
        user.InventoryItems.Add(new UserInventory { UserID = user.ID, ItemCode = "ma.Super Mastery Boost * 2", Quantity = 1 });
        user.InventoryItems.Add(new UserInventory { UserID = user.ID, ItemCode = "mb.claimed_match:7777", Quantity = 1 });
        user.InventoryItems.Add(new UserInventory { UserID = user.ID, ItemCode = "mb.claimed_match:8888", Quantity = 1 });

        MatchStartData startData = new()
        {
            MatchID = 7777,
            MatchName = "TestMatch",
            ServerID = 1,
            ServerName = "TestServer",
            HostAccountName = "postgame_player",
            Map = "caldavar",
            Version = "1.0",
            IsCasual = false,
            MatchType = 1,
            MatchMode = "normal"
        };
        AccountStatistics emptyStats = new() { Type = AccountStatisticsType.Matchmaking };

        PlayerStatistics claimedMatchStats = new() { MatchID = 7777, AccountID = account.ID, HeroIdentifier = "Hero_Pyromancer" };
        MatchPlayerStatistics claimedView = new(startData, account, claimedMatchStats, emptyStats, emptyStats, emptyStats)
        {
            HeroIdentifier = "Hero_Pyromancer"
        };

        claimedView.MasteryBoostNum.Should().Be(3);
        claimedView.MasterySuperBoostNum.Should().Be(2);
        claimedView.MasteryCanBoost.Should().Be("0", "already-claimed match 7777 cannot be boosted again");
        claimedView.MasterySuperCanBoost.Should().Be("0", "already-claimed match 7777 cannot be super-boosted again");

        PlayerStatistics openMatchStats = new() { MatchID = 9001, AccountID = account.ID, HeroIdentifier = "Hero_Pyromancer" };
        MatchPlayerStatistics openView = new(startData, account, openMatchStats, emptyStats, emptyStats, emptyStats)
        {
            HeroIdentifier = "Hero_Pyromancer"
        };

        openView.MasteryBoostNum.Should().Be(3);
        openView.MasterySuperBoostNum.Should().Be(2);
        openView.MasteryCanBoost.Should().Be("1");
        openView.MasterySuperCanBoost.Should().Be("1");

        user.InventoryItems.Clear();
        user.InventoryItems.Add(new UserInventory { UserID = user.ID, ItemCode = "m.Mastery Boost", Quantity = 4 });
        MatchPlayerStatistics legacyOnlyView = new(startData, account, openMatchStats, emptyStats, emptyStats, emptyStats)
        {
            HeroIdentifier = "Hero_Pyromancer"
        };
        legacyOnlyView.MasteryBoostNum.Should().Be(4);
        legacyOnlyView.MasterySuperBoostNum.Should().Be(0);
        legacyOnlyView.MasteryCanBoost.Should().Be("1");
        legacyOnlyView.MasterySuperCanBoost.Should().Be("0", "0 owned super boosts must set mastery_super_canboost=0");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task S1b2c_AccountRepository_UpdateAsync_RemainsScalarOnly_AndLogsErrorWhenDetachedCallerPassesUntrackedInventoryItems()
    {
        // protects: AccountRepository.UpdateAsync scalar/profile-only contract and explicit LogError guardrail against future .AsNoTracking()+UpdateAsync regressions
        // fails_when: UpdateAsync silently drops detached ID==0 UserInventory items without logging an error
        // seam: AccountRepository.GetByNicknameAsync + AccountRepository.UpdateAsync + real SQLite MerrickContext
        await using TestSqliteDbHarness harness = await TestSqliteDbHarness.CreateAsync();
        await harness.SeedAccountAsync("detached_caller", 320);

        CapturingLogger<AccountRepository> logger = new();
        await using (MerrickContext db = harness.CreateContext())
        {
            AccountRepository repo = new(db, logger);
            Account? detached = await repo.GetByNicknameAsync("detached_caller");
            detached!.User.SilverCoins = 100;
            detached.User.InventoryItems.Add(new UserInventory
            {
                UserID = detached.User.ID,
                AccountID = detached.ID,
                ItemCode = "mastery_reward_lv_3",
                Quantity = 1,
                IsPermanent = true,
                Source = "AccidentalDetachedCaller"
            });

            await repo.UpdateAsync(detached);
        }

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains("scalar/profile-only", StringComparison.OrdinalIgnoreCase),
            "UpdateAsync must log a clear Error when detached UserInventory items with ID==0 are passed without loaded InventoryItems");

        await using (MerrickContext dbCheck = harness.CreateContext())
        {
            Account? persisted = await new AccountRepository(dbCheck).GetByNicknameAsync("detached_caller");
            persisted!.User.SilverCoins.Should().Be(100);
            persisted.User.InventoryItems.Should().BeEmpty("scalar UpdateAsync must not implicitly insert/delete InventoryItems");
        }
    }

    private sealed class FailOnSecondSaveInterceptor : SaveChangesInterceptor
    {
        private int _callCount;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _callCount++;
            if (_callCount == 2)
                throw new InvalidOperationException("Simulated mid-transaction SaveMasteryAsync failure");

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task S1b2d_UseMasteryBoostHandler_RollsBackBoostConsumptionAndClaimMarkerWhenSaveMasteryFailsMidTransaction()
    {
        // protects: Single-transaction atomicity across boost consumption, claim marker insert, and mastery XP grant
        // fails_when: Boost is consumed or mb.claimed_match:{matchId} persists when SaveMasteryAsync throws
        // seam: UseMasteryBoostHandler + MatchHistoryRepository + SQLite relational transaction
        await using TestSqliteDbHarness harness = await TestSqliteDbHarness.CreateAsync();
        await harness.SeedAccountAsync(
            "atomic_player",
            325,
            inventoryItems: [("ma.Mastery Boost * 2", 1)]);
        await harness.SeedMatchAsync(7790, 325, "atomic_player", "Hero_Pyromancer");

        Mock<ISessionRepository> mockSessionRepo = new();
        FailOnSecondSaveInterceptor failSecondSave = new();
        await using (MerrickContext db = harness.CreateContext(failSecondSave))
        {
            UseMasteryBoostHandler handler = new(
                new AccountRepository(db),
                new MatchHistoryRepository(db),
                mockSessionRepo.Object);

            DefaultHttpContext ctx = new();
            ctx.Items["SessionAccountName"] = "atomic_player";
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["match_id"] = "7790",
                ["is_super"] = "0"
            });

            Func<Task> act = async () => await handler.HandleRequestAsync(ctx);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        await using (MerrickContext dbVerify = harness.CreateContext())
        {
            Account? persisted = await new AccountRepository(dbVerify).GetByNicknameAsync("atomic_player");
            MasteryConsumables.MasteryBoostsOwned(persisted!.User).Should().Be(2, "failed mastery XP persistence must roll back boost consumption");
            persisted.User.InventoryItems.Should().NotContain(i => i.ItemCode == "mb.claimed_match:7790",
                "failed mastery XP persistence must roll back mb.claimed_match marker");
        }
    }

    [Fact]
    public async Task S1b3_SelectedUpgradesHandler_RemovesAllThreeAutoGrantSites_AndSupportsShadowLogVersusEnforceUnownedEquipPolicy()
    {
        // protects: S1b-3 removal of all 3 UserInventory auto-grant sites in SelectedUpgradesHandler and Store:UnownedEquipPolicy ShadowLog vs Enforce behavior
        // fails_when: Equipping an unowned item inserts into account.User.InventoryItems or Enforce mode equips an unowned item when UnlockAllItems=false
        // seam: SelectedUpgradesHandler.HandleRequestAsync + UpgradesResolutionService
        (Account account, User user) = CreateTestAccount("vault_player", 401);
        user.InventoryItems.Clear();

        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<ISessionRepository> mockSessionRepo = new();
        mockSessionRepo
            .Setup(s => s.GetAccountNameForSessionCookieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(account.Name);
        mockAccountRepo
            .Setup(a => a.GetByNicknameAsync(account.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        // 1. ShadowLog policy (default for Playtest 1) with UnlockAllItems=false: logs [ECONOMY-ALERT], never mutates UserInventory
        IConfiguration shadowConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Store:UnlockAllItems"] = "false",
                ["Store:UnownedEquipPolicy"] = "ShadowLog"
            })
            .Build();
        UpgradesResolutionService shadowResolver = new(
            NullLogger<UpgradesResolutionService>.Instance,
            storeRepository: null,
            configuration: shadowConfig);

        SelectedUpgradesHandler shadowHandler = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            NullLogger<SelectedUpgradesHandler>.Instance,
            hybridCache: null,
            upgradesResolver: shadowResolver,
            configuration: shadowConfig);

        DefaultHttpContext shadowCtx = new();
        shadowCtx.Request.ContentType = "application/x-www-form-urlencoded";
        shadowCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["cookie"] = "valid_cookie",
            ["selected_upgrades[]"] = new StringValues(["custom_icon:2", "cs.unowned_symbol", "t.Fat_Lady_Taunt"])
        });

        await shadowHandler.HandleRequestAsync(shadowCtx);
        user.InventoryItems.Should().BeEmpty("SelectedUpgradesHandler must NEVER auto-grant items into UserInventory across any of the 3 sites");
        account.SelectedStoreItemCodes.Should().NotBeEmpty("ShadowLog mode permits equip while recording telemetry");

        // 2. Enforce policy with UnlockAllItems=false: rejects unowned items from being equipped
        account.SetSelectedStoreItems([]);
        IConfiguration enforceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Store:UnlockAllItems"] = "false",
                ["Store:UnownedEquipPolicy"] = "Enforce"
            })
            .Build();
        UpgradesResolutionService enforceResolver = new(
            NullLogger<UpgradesResolutionService>.Instance,
            storeRepository: null,
            configuration: enforceConfig);

        SelectedUpgradesHandler enforceHandler = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            NullLogger<SelectedUpgradesHandler>.Instance,
            hybridCache: null,
            upgradesResolver: enforceResolver,
            configuration: enforceConfig);

        DefaultHttpContext enforceCtx = new();
        enforceCtx.Request.ContentType = "application/x-www-form-urlencoded";
        enforceCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["cookie"] = "valid_cookie",
            ["selected_upgrades[]"] = new StringValues(["custom_icon:2", "cs.unowned_symbol", "t.Fat_Lady_Taunt"])
        });

        await enforceHandler.HandleRequestAsync(enforceCtx);
        user.InventoryItems.Should().BeEmpty();
        account.SelectedStoreItemCodes.Should().BeEmpty("Enforce mode must block equipping unowned items when UnlockAllItems=false");
    }

    [Fact]
    public async Task S1b4_ServerListCacheKeySanitization_ClanInfoFriendPrivacy_AndReferralSessionEnforcement()
    {
        // protects: S1b-4 SERVER-LIST cache key against cookie amplification, ClanHandler get_clan_info against unauthenticated friend disclosure, and ReferralHandler against ?account_id= IDOR fallback
        // fails_when: ServerListHandler cache key contains raw cookie, ClanHandler leaks friends_str to unauthenticated caller, or ReferralHandler resolves account via ?account_id= when session is null
        // seam: ServerListHandler + ClanHandler + ReferralHandler
        // 1. ServerListHandler cache key must not contain raw cookie
        Mock<IDatabase> mockDb = new();
        IMemoryCache memCache = new MemoryCache(new MemoryCacheOptions());
        Mock<INexusHybridCache> mockHybrid = new();
        string? capturedCacheKey = null;
        mockHybrid
            .Setup(c => c.GetOrCreateAsync<string>(
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<CancellationToken, Task<string?>>, IReadOnlyCollection<string>?, TimeSpan?, TimeSpan?, CancellationToken>(
                (key, _, _, _, _, _) => capturedCacheKey = key)
            .ReturnsAsync("a:0:{}");

        ServerListHandler serverListHandler = new(mockDb.Object, memCache, NullLogger<ServerListHandler>.Instance, mockHybrid.Object);
        DefaultHttpContext slCtx = new();
        slCtx.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["cookie"] = "attacker_random_cookie_999999",
            ["gametype"] = "10",
            ["region"] = "USE"
        });
        await serverListHandler.HandleRequestAsync(slCtx);
        capturedCacheKey.Should().NotBeNull();
        capturedCacheKey.Should().NotContain("attacker_random_cookie_999999", "raw cookie must never be embedded in SERVER-LIST cache keys");

        // 2. ClanHandler get_clan_info must omit friends_str unless authenticated as that user
        (Account clanTarget, _) = CreateTestAccount("target_player", 501);
        (Account friendAcct, _) = CreateTestAccount("secret_friend", 502);
        Mock<IClanRepository> mockClanRepo = new();
        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<IFriendshipRepository> mockFriendRepo = new();

        mockClanRepo
            .Setup(c => c.GetAccountWithClanByNameAsync("target_player", It.IsAny<CancellationToken>()))
            .ReturnsAsync(clanTarget);
        mockFriendRepo
            .Setup(f => f.GetFriendshipsAsync(clanTarget.ID, FriendshipStatus.Accepted, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new Friendship
                {
                    AccountID = clanTarget.ID,
                    FriendAccountID = friendAcct.ID,
                    FriendAccount = friendAcct,
                    Status = FriendshipStatus.Accepted
                }
            ]);

        ClanHandler clanHandler = new(
            mockClanRepo.Object,
            mockAccountRepo.Object,
            mockFriendRepo.Object,
            mockDb.Object,
            NullLogger<ClanHandler>.Instance);

        DefaultHttpContext unauthClanCtx = new();
        unauthClanCtx.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "get_clan_info",
            ["nickname"] = "target_player"
        });
        PhpResult unauthClanPhp = (await clanHandler.HandleRequestAsync(unauthClanCtx)).Should().BeOfType<PhpResult>().Subject;
        Hashtable unauthClanTable = unauthClanPhp.Data.Should().BeOfType<Hashtable>().Subject;
        unauthClanTable.ContainsKey("friends_str").Should().BeFalse("unauthenticated get_clan_info must omit friends_str");

        // Authenticated as target_player -> includes friends_str
        DefaultHttpContext authClanCtx = new();
        authClanCtx.Items["SessionAccountName"] = "target_player";
        authClanCtx.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "get_clan_info",
            ["nickname"] = "target_player"
        });
        PhpResult authClanPhp = (await clanHandler.HandleRequestAsync(authClanCtx)).Should().BeOfType<PhpResult>().Subject;
        Hashtable authClanTable = authClanPhp.Data.Should().BeOfType<Hashtable>().Subject;
        authClanTable.ContainsKey("friends_str").Should().BeTrue();
        authClanTable["friends_str"]!.ToString().Should().Contain("secret_friend");

        // 3. ReferralHandler get_referrals must not fall back to unauthenticated ?account_id=
        Mock<ISessionContextResolver> mockSessionResolver = new();
        mockSessionResolver
            .Setup(r => r.ResolveAccountAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account?)null);

        ReferralHandler referralHandler = new(
            mockAccountRepo.Object,
            mockSessionResolver.Object,
            NullLogger<ReferralHandler>.Instance);

        DefaultHttpContext refCtx = new();
        refCtx.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "get_referrals",
            ["account_id"] = "501"
        });

        IActionResult refRes = await referralHandler.HandleRequestAsync(refCtx);
        refRes.Should().BeOfType<UnauthorizedObjectResult>();
        mockAccountRepo.Verify(a => a.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task S1b5_CloudStorageProvider_ReusesSingletonS3Client_OmitsPublicReadAclByDefault_AndRethrowsUploadExceptions()
    {
        // protects: S1b-5 CloudStorageProvider against silent upload lies, per-upload AmazonS3Client churn, and R2-incompatible CannedACL=PublicRead
        // fails_when: CloudStorageProvider swallows PutObjectAsync exceptions and returns publicUrl, or sets CannedACL=PublicRead by default
        // seam: CloudStorageProvider.UploadReplayAsync + IAmazonS3
        IOptions<ReplayStorageSettings> options = Options.Create(new ReplayStorageSettings
        {
            Provider = "Cloud",
            Cloud = new CloudStorageSettings
            {
                BucketName = "kongor-replays",
                AccessKey = string.Empty,
                SecretKey = string.Empty,
                ServiceUrl = "https://example.r2.cloudflarestorage.com"
            }
        });

        Mock<IAmazonS3> mockS3 = new();
        PutObjectRequest? capturedRequest = null;
        mockS3
            .Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new PutObjectResponse());

        using CloudStorageProvider provider = new(options, NullLogger<CloudStorageProvider>.Instance, mockS3.Object);
        using MemoryStream ms = new([1, 2, 3, 4]);
        string url = await provider.UploadReplayAsync("9001", ms, "M9001.honreplay");

        url.Should().Be("https://example.r2.cloudflarestorage.com/replays/M9001.honreplay");
        capturedRequest.Should().NotBeNull();
        capturedRequest!.CannedACL.Should().BeNull("R2-compatible default must not force S3CannedACL.PublicRead");

        // When PutObjectAsync throws, CloudStorageProvider must rethrow instead of returning a fake success URL
        mockS3
            .Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Bucket unreachable"));

        using MemoryStream ms2 = new([5, 6, 7, 8]);
        Func<Task> act = async () => await provider.UploadReplayAsync("9002", ms2, "M9002.honreplay");
        await act.Should().ThrowAsync<AmazonS3Exception>();

        // Path traversal filenames must be rejected with ArgumentException before S3 upload
        using MemoryStream ms3 = new([9, 10]);
        Func<Task> traversalAct = async () => await provider.UploadReplayAsync("9003", ms3, "../../outside.honreplay");
        await traversalAct.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task S2_FriendsHandler_EnforcesTwoPartyConsent_UnilateralNewBuddyRemainsPendingUntilApproved()
    {
        // protects: Two-party friend consent across FriendsHandler (blocking unilateral instant-mutual-friend MENTOR spectate exploit)
        // fails_when: f=new_buddy grants FriendshipStatus.Accepted without recipient approval, or f=approve_buddy succeeds without an incoming Pending request
        // seam: FriendsHandler.HandleRequestAsync + FriendshipRepository + real SQLite MerrickContext
        await using TestSqliteDbHarness harness = await TestSqliteDbHarness.CreateAsync();
        await harness.SeedAccountAsync("alice", 401);
        await harness.SeedAccountAsync("bob", 402);
        await harness.SeedAccountAsync("charlie", 403);
        await harness.SeedAccountAsync("dave", 404);
        await harness.SeedAccountAsync("eve", 405);

        Mock<IDatabase> mockRedis = new();

        // 1. Alice sends unilateral f=new_buddy targeting Bob -> ONLY alice -> bob (Pending) exists; NOT mutual Accepted
        await using (MerrickContext db1 = harness.CreateContext())
        {
            FriendsHandler handler = new(
                new FriendshipRepository(db1),
                new AccountRepository(db1),
                mockRedis.Object,
                NullLogger<FriendsHandler>.Instance);

            DefaultHttpContext ctx = new();
            ctx.Items["SessionAccountName"] = "alice";
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["f"] = "new_buddy",
                ["cookie"] = "alice_cookie",
                ["buddy_id"] = "402"
            });

            PhpResult php = (await handler.HandleRequestAsync(ctx)).Should().BeOfType<PhpResult>().Subject;
            IDictionary dict = php.Data.Should().BeAssignableTo<IDictionary>().Subject;
            dict["new_buddy"].Should().Be("OK");
            dict.Contains("approve_buddy").Should().BeFalse("unilateral friend request must NOT report approve_buddy=OK");
        }

        await using (MerrickContext dbVerify1 = harness.CreateContext())
        {
            FriendshipRepository checkRepo = new(dbVerify1);
            (await checkRepo.AreFriendsAsync(401, 402)).Should().BeFalse("unilateral f=new_buddy must NOT make alice and bob mutual friends");
            (await checkRepo.GetFriendedPeersAsync(401)).Should().BeEmpty();
            (await checkRepo.GetFriendedPeersAsync(402)).Should().BeEmpty();

            List<Friendship> rows = await dbVerify1.Friendships
                .Where(f => (f.AccountID == 401 && f.FriendAccountID == 402) ||
                            (f.AccountID == 402 && f.FriendAccountID == 401))
                .ToListAsync();
            rows.Should().ContainSingle();
            rows[0].AccountID.Should().Be(401);
            rows[0].FriendAccountID.Should().Be(402);
            rows[0].Status.Should().Be(FriendshipStatus.Pending);
        }

        // 2. Charlie cannot forge f=approve_buddy targeting Bob without an incoming Pending request from Bob
        await using (MerrickContext dbCharlie = harness.CreateContext())
        {
            FriendsHandler handler = new(
                new FriendshipRepository(dbCharlie),
                new AccountRepository(dbCharlie),
                mockRedis.Object,
                NullLogger<FriendsHandler>.Instance);

            DefaultHttpContext ctx = new();
            ctx.Items["SessionAccountName"] = "charlie";
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["f"] = "approve_buddy",
                ["cookie"] = "charlie_cookie",
                ["buddy_id"] = "402"
            });

            PhpResult php = (await handler.HandleRequestAsync(ctx)).Should().BeOfType<PhpResult>().Subject;
            IDictionary dict = php.Data.Should().BeAssignableTo<IDictionary>().Subject;
            dict["approve_buddy"].Should().Be("FAILED");
            dict.Contains("error").Should().BeTrue();
        }

        await using (MerrickContext dbVerifyCharlie = harness.CreateContext())
        {
            (await new FriendshipRepository(dbVerifyCharlie).AreFriendsAsync(403, 402)).Should().BeFalse();
        }

        // 3. Bob calls f=approve_buddy accepting Alice (where incoming alice -> bob Pending exists) -> mutual Accepted
        await using (MerrickContext dbAccept = harness.CreateContext())
        {
            FriendsHandler handler = new(
                new FriendshipRepository(dbAccept),
                new AccountRepository(dbAccept),
                mockRedis.Object,
                NullLogger<FriendsHandler>.Instance);

            DefaultHttpContext ctx = new();
            ctx.Items["SessionAccountName"] = "bob";
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["f"] = "approve_buddy",
                ["cookie"] = "bob_cookie",
                ["buddy_id"] = "401",
                ["group"] = "Squad"
            });

            PhpResult php = (await handler.HandleRequestAsync(ctx)).Should().BeOfType<PhpResult>().Subject;
            IDictionary dict = php.Data.Should().BeAssignableTo<IDictionary>().Subject;
            dict["approve_buddy"].Should().Be("OK");
        }

        await using (MerrickContext dbVerifyAccept = harness.CreateContext())
        {
            FriendshipRepository checkRepo = new(dbVerifyAccept);
            (await checkRepo.AreFriendsAsync(401, 402)).Should().BeTrue();
            (await checkRepo.GetFriendedPeersAsync(401)).Should().ContainSingle(p => p.ID == 402);
            (await checkRepo.GetFriendedPeersAsync(402)).Should().ContainSingle(p => p.ID == 401 && p.FriendGroup == "Squad");
        }

        // 4. Reciprocal f=add_buddy: Dave -> Eve (Pending), then Eve -> Dave via f=add_buddy auto-promotes mutual Accepted
        await using (MerrickContext dbReq = harness.CreateContext())
        {
            FriendsHandler handler = new(
                new FriendshipRepository(dbReq),
                new AccountRepository(dbReq),
                mockRedis.Object,
                NullLogger<FriendsHandler>.Instance);

            DefaultHttpContext ctx1 = new();
            ctx1.Items["SessionAccountName"] = "dave";
            ctx1.Request.ContentType = "application/x-www-form-urlencoded";
            ctx1.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["f"] = "new_buddy",
                ["cookie"] = "dave_cookie",
                ["buddy_id"] = "405"
            });
            await handler.HandleRequestAsync(ctx1);
        }

        await using (MerrickContext dbRecip = harness.CreateContext())
        {
            FriendsHandler handler = new(
                new FriendshipRepository(dbRecip),
                new AccountRepository(dbRecip),
                mockRedis.Object,
                NullLogger<FriendsHandler>.Instance);

            DefaultHttpContext ctx2 = new();
            ctx2.Items["SessionAccountName"] = "eve";
            ctx2.Request.ContentType = "application/x-www-form-urlencoded";
            ctx2.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["f"] = "add_buddy",
                ["cookie"] = "eve_cookie",
                ["buddy_id"] = "404"
            });

            PhpResult php = (await handler.HandleRequestAsync(ctx2)).Should().BeOfType<PhpResult>().Subject;
            IDictionary dict = php.Data.Should().BeAssignableTo<IDictionary>().Subject;
            dict["approve_buddy"].Should().Be("OK");
        }

        await using (MerrickContext dbVerifyRecip = harness.CreateContext())
        {
            (await new FriendshipRepository(dbVerifyRecip).AreFriendsAsync(404, 405)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task S2b_GuideHandler_BindsPrivateReadsAndMutationsStrictlyToAuthenticatedSession()
    {
        // protects: GuideHandler against unauthenticated ?account=<id> impersonation on private guide reads and unauthenticated guide mutations
        // fails_when: Unauthenticated request with ?account=501 sees author's private guide or guide_default_*/guide_favorite_*/guide_vote returns 200 OK
        // seam: GuideHandler.HandleRequestAsync + HeroGuideRepository + real SQLite MerrickContext
        await using TestSqliteDbHarness harness = await TestSqliteDbHarness.CreateAsync();
        Account author = await harness.SeedAccountAsync("guide_author", 501);

        await using (MerrickContext dbSeed = harness.CreateContext())
        {
            dbSeed.HeroGuides.AddRange(
                new HeroGuide
                {
                    ID = 510,
                    Name = "Secret Author Draft",
                    HeroName = "Valkyrie",
                    HeroIdentifier = "Hero_Valkyrie",
                    AuthorID = author.ID,
                    Public = false,
                    Featured = false
                },
                new HeroGuide
                {
                    ID = 511,
                    Name = "Public Valk Guide",
                    HeroName = "Valkyrie",
                    HeroIdentifier = "Hero_Valkyrie",
                    AuthorID = author.ID,
                    Public = true,
                    Featured = true
                });
            await dbSeed.SaveChangesAsync();
        }

        Mock<ISessionRepository> mockSessionRepo = new();
        Mock<IDatabase> mockRedis = new();

        // 1. Unauthenticated guest spoofing ?account=501 MUST NOT see private guide 510 in get_guide_list_filtered or get_guide
        await using (MerrickContext dbGuest = harness.CreateContext())
        {
            GuideHandler handler = new(
                new HeroGuideRepository(dbGuest),
                new AccountRepository(dbGuest),
                mockSessionRepo.Object,
                mockRedis.Object,
                NullLogger<GuideHandler>.Instance);

            DefaultHttpContext listCtx = new();
            listCtx.Request.Method = "GET";
            listCtx.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
            {
                ["f"] = "get_guide_list_filtered",
                ["account"] = "501",
                ["hero"] = "Hero_Valkyrie"
            });

            PhpResult listPhp = (await handler.HandleRequestAsync(listCtx)).Should().BeOfType<PhpResult>().Subject;
            GuideListResponse listData = listPhp.Data.Should().BeOfType<GuideListResponse>().Subject;
            string serializedList = PhpSerializer.SerializeToString(listData);
            serializedList.Should().Contain("Public Valk Guide");
            serializedList.Should().NotContain("Secret Author Draft", "unauthenticated ?account=501 must never leak private guides");

            DefaultHttpContext getCtx = new();
            getCtx.Request.Method = "GET";
            getCtx.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
            {
                ["f"] = "get_guide",
                ["account"] = "501",
                ["gid"] = "510",
                ["hero"] = "Hero_Valkyrie"
            });

            PhpResult getPhp = (await handler.HandleRequestAsync(getCtx)).Should().BeOfType<PhpResult>().Subject;
            GuideResponseSuccess getData = getPhp.Data.Should().BeOfType<GuideResponseSuccess>().Subject;
            getData.Name.Should().NotBe("Secret Author Draft");
        }

        // 2. Unauthenticated guide_default_set, guide_favorite_set, guide_vote (even with ?account=501) -> 401 Unauthorized
        foreach (string action in new[] { "guide_default_set", "guide_favorite_set", "guide_vote" })
        {
            await using MerrickContext dbMut = harness.CreateContext();
            GuideHandler handler = new(
                new HeroGuideRepository(dbMut),
                new AccountRepository(dbMut),
                mockSessionRepo.Object,
                mockRedis.Object,
                NullLogger<GuideHandler>.Instance);

            DefaultHttpContext mutCtx = new();
            mutCtx.Request.Method = "POST";
            mutCtx.Request.ContentType = "application/x-www-form-urlencoded";
            mutCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["f"] = action,
                ["account"] = "501",
                ["gid"] = "511",
                ["val"] = "5"
            });

            IActionResult res = await handler.HandleRequestAsync(mutCtx);
            res.Should().BeOfType<UnauthorizedObjectResult>($"{action} must require authenticated session");
        }

        // 3. Authenticated guide_author DOES see their own private guide Draft
        await using (MerrickContext dbAuth = harness.CreateContext())
        {
            GuideHandler handler = new(
                new HeroGuideRepository(dbAuth),
                new AccountRepository(dbAuth),
                mockSessionRepo.Object,
                mockRedis.Object,
                NullLogger<GuideHandler>.Instance);

            DefaultHttpContext authListCtx = new();
            authListCtx.Items["SessionAccountName"] = "guide_author";
            authListCtx.Request.Method = "GET";
            authListCtx.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
            {
                ["f"] = "get_guide_list_filtered",
                ["hero"] = "Hero_Valkyrie"
            });

            PhpResult authPhp = (await handler.HandleRequestAsync(authListCtx)).Should().BeOfType<PhpResult>().Subject;
            GuideListResponse authList = authPhp.Data.Should().BeOfType<GuideListResponse>().Subject;
            PhpSerializer.SerializeToString(authList).Should().Contain("Secret Author Draft");
        }
    }

    [Fact]
    public async Task S2c_Handlers_HandleGetWithoutFormContentTypeCleanly_AndMaskCookiesInLogsAndErrors()
    {
        // protects: Safe Request.Form guards on GET across client handlers and cookie masking in ClanHandler/SelectedUpgradesHandler
        // fails_when: Any handler throws InvalidOperationException reading Request.Form on GET, or raw cookie leaks to logs/401 body
        // seam: HttpRequestExtensions + ClanHandler, FriendsHandler, GameHandler, ServerListHandler, MatchStatsHandler, HeroStatsHandler, ReplayHandler, SelectedUpgradesHandler
        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<IFriendshipRepository> mockFriendRepo = new();
        Mock<IClanRepository> mockClanRepo = new();
        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        Mock<IHeroDefinitionService> mockHeroDefs = new();
        Mock<ISessionContextResolver> mockSessionResolver = new();
        Mock<ISessionRepository> mockSessionRepo = new();
        Mock<IReplayStorageProvider> mockStorage = new();
        Mock<IDatabase> mockRedis = new();

        DefaultHttpContext BuildGet(Dictionary<string, StringValues> query)
        {
            DefaultHttpContext ctx = new();
            ctx.Request.Method = "GET";
            ctx.Request.Query = new QueryCollection(query);
            return ctx;
        }

        // 1. FriendsHandler on GET
        FriendsHandler friendsHandler = new(mockFriendRepo.Object, mockAccountRepo.Object, mockRedis.Object, NullLogger<FriendsHandler>.Instance);
        await friendsHandler.Invoking(h => h.HandleRequestAsync(BuildGet(new() { ["f"] = "autocomplete" })))
            .Should().NotThrowAsync<InvalidOperationException>();

        // 2. GameHandler on GET
        GameHandler gameHandler = new(mockRedis.Object, NullLogger<GameHandler>.Instance);
        await gameHandler.Invoking(h => h.HandleRequestAsync(BuildGet(new() { ["f"] = "new_game_available" })))
            .Should().NotThrowAsync<InvalidOperationException>();

        // 3. ServerListHandler on GET
        ServerListHandler serverListHandler = new(mockRedis.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ServerListHandler>.Instance);
        await serverListHandler.Invoking(h => h.HandleRequestAsync(BuildGet(new() { ["f"] = "get_server_list" })))
            .Should().NotThrowAsync<InvalidOperationException>();

        // 4. MatchStatsHandler on GET
        MatchStatsHandler matchStatsHandler = new(mockAccountRepo.Object, mockMatchRepo.Object, mockRedis.Object, mockHeroDefs.Object, NullLogger<MatchStatsHandler>.Instance);
        await matchStatsHandler.Invoking(h => h.HandleRequestAsync(BuildGet(new() { ["f"] = "get_match_stats" })))
            .Should().NotThrowAsync<InvalidOperationException>();

        // 5. HeroStatsHandler on GET across all subfunctions
        HeroStatsHandler heroStatsHandler = new(mockMatchRepo.Object, mockAccountRepo.Object, mockSessionResolver.Object, mockHeroDefs.Object, NullLogger<HeroStatsHandler>.Instance);
        foreach (string fn in new[] { "get_hero_stats", "get_campaign_hero_stats", "get_account_mastery" })
        {
            await heroStatsHandler.Invoking(h => h.HandleRequestAsync(BuildGet(new() { ["f"] = fn })))
                .Should().NotThrowAsync<InvalidOperationException>();
        }

        // 6. ReplayHandler on GET (with authorized InternalServiceKey so it reaches TryGetFormFile instead of returning 401 before form access)
        IConfiguration replayCfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["InternalServiceKey"] = "replay-get-secret" })
            .Build();
        ReplayHandler replayHandler = new(NullLogger<ReplayHandler>.Instance, mockStorage.Object, mockMatchRepo.Object, null, mockRedis.Object, replayCfg);
        DefaultHttpContext replayGetCtx = BuildGet(new() { ["f"] = "upload_replay" });
        replayGetCtx.Request.Headers[SecurityConstants.InternalServiceKeyHeader] = "replay-get-secret";
        IActionResult replayGetRes = await replayHandler.HandleRequestAsync(replayGetCtx);
        replayGetRes.Should().BeOfType<BadRequestObjectResult>();

        // 7. ClanHandler masks cookie and never calls LogRawForm with raw cookie on set_rank or GET
        CapturingLogger<ClanHandler> clanLogger = new();
        ClanHandler clanHandler = new(mockClanRepo.Object, mockAccountRepo.Object, mockFriendRepo.Object, mockRedis.Object, clanLogger);
        const string secretCookie = "raw-secret-cookie-token-9876543210";
        IActionResult clanGetRes = await clanHandler.HandleRequestAsync(BuildGet(new() { ["f"] = "set_rank", ["cookie"] = secretCookie }));
        clanGetRes.Should().BeOfType<BadRequestObjectResult>();
        clanLogger.Entries.Should().NotContain(e => e.Message.Contains(secretCookie, StringComparison.Ordinal),
            "ClanHandler must never log raw unmasked session cookies");

        // 8. SelectedUpgradesHandler masks cookie in 401 UnauthorizedObjectResult
        SelectedUpgradesHandler selectedHandler = new(mockAccountRepo.Object, mockSessionRepo.Object, NullLogger<SelectedUpgradesHandler>.Instance);
        IActionResult selRes = await selectedHandler.HandleRequestAsync(BuildGet(new() { ["f"] = "selected_upgrades", ["cookie"] = secretCookie }));
        UnauthorizedObjectResult unauthSel = selRes.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthSel.Value?.ToString().Should().NotContain(secretCookie, "SelectedUpgradesHandler must mask unrecognised cookies in error responses");

        // 9. EventsHandler and MatchHistoryHandler on GET without FormContentType
        EventsHandler eventsHandler = new(NullLogger<EventsHandler>.Instance);
        await eventsHandler.Invoking(h => h.HandleRequestAsync(BuildGet(new() { ["f"] = "get_events" })))
            .Should().NotThrowAsync<InvalidOperationException>();

        MatchHistoryHandler matchHistoryHandler = new(mockMatchRepo.Object, mockAccountRepo.Object, mockRedis.Object, mockHeroDefs.Object, NullLogger<MatchHistoryHandler>.Instance);
        await matchHistoryHandler.Invoking(h => h.HandleRequestAsync(BuildGet(new() { ["f"] = "grab_last_matches" })))
            .Should().NotThrowAsync<InvalidOperationException>();
    }

    private sealed class BoostCasContentionCommandInterceptor(
        Action onSuppressedMutation) : DbCommandInterceptor
    {
        private bool _triggered;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_triggered &&
                command.CommandText.Contains("UserInventory", StringComparison.OrdinalIgnoreCase) &&
                (command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) ||
                 command.CommandText.Contains("DELETE", StringComparison.OrdinalIgnoreCase)))
            {
                _triggered = true;
                onSuppressedMutation();
                return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class DeferredConcurrentMutationTxInterceptor(
        Action<DbConnection> applyConcurrentCommitAfterRollback) : DbTransactionInterceptor
    {
        private bool _armed;
        private bool _applied;

        public void Arm() => _armed = true;

        public override Task TransactionRolledBackAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (_armed && !_applied && eventData.Context?.Database.GetDbConnection() is DbConnection conn)
            {
                _applied = true;
                applyConcurrentCommitAfterRollback(conn);
            }

            return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
        }
    }

    [Fact]
    public async Task S1b2e_UseMasteryBoostHandler_And_ClaimMasteryRewardHandler_RetryCleanlyOnCountedBoostCasContention_WithoutDoubleSpendOrLostUpdate()
    {
        // protects: CAS retry semantics on shared counted boost rows ("ma.Mastery Boost * N") preventing double-spend across different matches and lost updates between redeem and claim
        // fails_when: Two concurrent use_mastery_boost calls for different matches consume only 1 boost, or claim/use overwrite each other without retrying on fresh rows
        // seam: UseMasteryBoostHandler + ClaimMasteryRewardHandler + MasteryConsumables.*WithTarget + SQLite EF Core ExecuteUpdateAsync/ExecuteDeleteAsync
        await using TestSqliteDbHarness harness = await TestSqliteDbHarness.CreateAsync();
        await harness.SeedAccountAsync(
            "cas_redeem_player",
            accountId: 330,
            inventoryItems: [("ma.Mastery Boost * 2", 1)]);
        await harness.SeedMatchAsync(8881, accountId: 330, "cas_redeem_player", "Hero_Pyromancer");

        await harness.SeedAccountAsync(
            "cas_last_boost_player",
            accountId: 331,
            inventoryItems: [("ma.Mastery Boost * 1", 1)]);
        await harness.SeedMatchAsync(8891, accountId: 331, "cas_last_boost_player", "Hero_Pyromancer");

        await harness.SeedAccountAsync(
            "cas_claim_player",
            accountId: 332,
            inventoryItems: [("ma.Mastery Boost * 2", 1)]);

        Mock<ISessionRepository> mockSessionRepo = new();

        // Case A: Account owns * 2. Request for Match #8881 races with concurrent Match #8880 decrementing * 2 -> * 1.
        // CAS retry reloads * 1, consumes remaining boost cleanly (-> 0 owned), commits both matches' claim markers, grants +240 XP.
        DeferredConcurrentMutationTxInterceptor? txIntA = null;
        txIntA = new DeferredConcurrentMutationTxInterceptor(conn =>
        {
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE UserInventory SET ItemCode = 'ma.Mastery Boost * 1', Quantity = 1 WHERE UserID = 330 AND ItemCode = 'ma.Mastery Boost * 2'; " +
                "INSERT INTO UserInventory (UserID, AccountID, ItemCode, Quantity, IsPermanent, TimestampAcquired, Source) VALUES (330, 330, 'mb.claimed_match:8880', 1, 1, '2026-11-16T00:00:00Z', 'ConcurrentRace');";
            cmd.ExecuteNonQuery();
        });
        BoostCasContentionCommandInterceptor cmdIntA = new(() => txIntA.Arm());

        await using (MerrickContext dbA = harness.CreateContext(cmdIntA, txIntA))
        {
            UseMasteryBoostHandler handlerA = new(
                new AccountRepository(dbA),
                new MatchHistoryRepository(dbA),
                mockSessionRepo.Object);

            DefaultHttpContext ctxA = new();
            ctxA.Items["SessionAccountName"] = "cas_redeem_player";
            ctxA.Request.ContentType = "application/x-www-form-urlencoded";
            ctxA.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["match_id"] = "8881",
                ["is_super"] = "0"
            });

            PhpResult resA = (await handlerA.HandleRequestAsync(ctxA)).Should().BeOfType<PhpResult>().Subject;
            IDictionary dictA = resA.Data.Should().BeAssignableTo<IDictionary>().Subject;
            dictA["status"].Should().Be("OK");
        }

        await using (MerrickContext dbCheckA = harness.CreateContext())
        {
            Account? persistedA = await new AccountRepository(dbCheckA).GetByNicknameAsync("cas_redeem_player");
            MasteryConsumables.MasteryBoostsOwned(persistedA!.User).Should().Be(0, "2 boosts minus 2 concurrent redemptions must equal 0");
            persistedA.User.InventoryItems.Should().Contain(i => i.ItemCode == "mb.claimed_match:8880");
            persistedA.User.InventoryItems.Should().Contain(i => i.ItemCode == "mb.claimed_match:8881");
            persistedA.Mastery!.GetHeroExperienceByHeroIdentifier("Hero_Pyromancer").Should().Be(240);
        }

        // Case B: Account owns * 1. Concurrent request deletes the last boost before CAS; retry sees 0 remaining -> NO_BOOST_OWNED, no Match #8891 claim.
        DeferredConcurrentMutationTxInterceptor? txIntB = null;
        txIntB = new DeferredConcurrentMutationTxInterceptor(conn =>
        {
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "DELETE FROM UserInventory WHERE UserID = 331 AND ItemCode = 'ma.Mastery Boost * 1'; " +
                "INSERT INTO UserInventory (UserID, AccountID, ItemCode, Quantity, IsPermanent, TimestampAcquired, Source) VALUES (331, 331, 'mb.claimed_match:8890', 1, 1, '2026-11-16T00:00:00Z', 'ConcurrentRace');";
            cmd.ExecuteNonQuery();
        });
        BoostCasContentionCommandInterceptor cmdIntB = new(() => txIntB.Arm());

        await using (MerrickContext dbB = harness.CreateContext(cmdIntB, txIntB))
        {
            UseMasteryBoostHandler handlerB = new(
                new AccountRepository(dbB),
                new MatchHistoryRepository(dbB),
                mockSessionRepo.Object);

            DefaultHttpContext ctxB = new();
            ctxB.Items["SessionAccountName"] = "cas_last_boost_player";
            ctxB.Request.ContentType = "application/x-www-form-urlencoded";
            ctxB.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["match_id"] = "8891",
                ["is_super"] = "0"
            });

            PhpResult resB = (await handlerB.HandleRequestAsync(ctxB)).Should().BeOfType<PhpResult>().Subject;
            IDictionary dictB = resB.Data.Should().BeAssignableTo<IDictionary>().Subject;
            dictB["status"].Should().Be("NO_BOOST_OWNED", "last boost deleted concurrently must block double-spend across distinct matches");
        }

        await using (MerrickContext dbCheckB = harness.CreateContext())
        {
            Account? persistedB = await new AccountRepository(dbCheckB).GetByNicknameAsync("cas_last_boost_player");
            MasteryConsumables.MasteryBoostsOwned(persistedB!.User).Should().Be(0);
            persistedB.User.InventoryItems.Should().Contain(i => i.ItemCode == "mb.claimed_match:8890");
            persistedB.User.InventoryItems.Should().NotContain(i => i.ItemCode == "mb.claimed_match:8891");
            (persistedB.Mastery?.GetHeroExperienceByHeroIdentifier("Hero_Pyromancer") ?? 0).Should().Be(0);
        }

        // Case C: Account owns * 2. ClaimMasteryRewardHandler Level 0 (+5 boosts) races with concurrent consume (* 2 -> * 1).
        // CAS retry reloads * 1 and updates * 1 -> * 6 (no lost update!).
        DeferredConcurrentMutationTxInterceptor? txIntC = null;
        txIntC = new DeferredConcurrentMutationTxInterceptor(conn =>
        {
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE UserInventory SET ItemCode = 'ma.Mastery Boost * 1', Quantity = 1 WHERE UserID = 332 AND ItemCode = 'ma.Mastery Boost * 2';";
            cmd.ExecuteNonQuery();
        });
        BoostCasContentionCommandInterceptor cmdIntC = new(() => txIntC.Arm());

        await using (MerrickContext dbC = harness.CreateContext(cmdIntC, txIntC))
        {
            ClaimMasteryRewardHandler claimHandlerC = new(
                new AccountRepository(dbC),
                NullLogger<ClaimMasteryRewardHandler>.Instance);

            DefaultHttpContext ctxC = new();
            ctxC.Items["SessionAccountName"] = "cas_claim_player";
            ctxC.Request.ContentType = "application/x-www-form-urlencoded";
            ctxC.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["level"] = "0"
            });

            PhpResult resC = (await claimHandlerC.HandleRequestAsync(ctxC)).Should().BeOfType<PhpResult>().Subject;
            IDictionary dictC = resC.Data.Should().BeAssignableTo<IDictionary>().Subject;
            dictC["reward_status"].Should().Be("OK");
        }

        await using (MerrickContext dbCheckC = harness.CreateContext())
        {
            Account? persistedC = await new AccountRepository(dbCheckC).GetByNicknameAsync("cas_claim_player");
            MasteryConsumables.MasteryBoostsOwned(persistedC!.User).Should().Be(6, "2 - 1 concurrent redeem + 5 claimed starter boosts must equal 6");
            persistedC.User.InventoryItems.Should().Contain(i => i.ItemCode == "mastery_reward_lv_0");
        }
    }

    [Fact]
    public async Task S1b2f_UseMasteryBoostHandler_PersistsTotalLevelAfterChangeTrackerClear_InvalidatesHybridCache_AndMatchStatsRefreshesCachedPlayerBoostState()
    {
        // protects: User.TotalLevel durability if SaveMasteryAsync clears ChangeTracker on retry, plus post-redemption INexusHybridCache invalidation and MatchStatsHandler IMemoryCache boost freshness
        // fails_when: Detached User drops TotalLevel, stale cached profile/match payloads keep old boost counts, or MatchStatsHandler mutates shared MemoryCache entry
        // seam: UseMasteryBoostHandler + ClaimMasteryRewardHandler + INexusHybridCache + MatchStatsHandler IMemoryCache
        await using TestSqliteDbHarness harness = await TestSqliteDbHarness.CreateAsync();
        await harness.SeedAccountAsync(
            "cache_sync_player",
            accountId: 340,
            totalMasteryXpOnPyromancer: 1200,
            userTotalLevel: 0,
            inventoryItems: [("ma.Mastery Boost * 2", 1)]);
        await harness.SeedMatchAsync(7790, accountId: 340, "cache_sync_player", "Hero_Pyromancer");

        MemoryCache memoryCache = new(new MemoryCacheOptions());
        Mock<IDatabase> mockRedis = new();
        Mock<IHeroDefinitionService> mockHeroDefs = new();
        mockHeroDefs.Setup(h => h.CleanHeroIdentifier(It.IsAny<string>())).Returns("Hero_Pyromancer");
        mockHeroDefs.Setup(h => h.GetBaseHeroId(It.IsAny<string>())).Returns(1u);

        // 1. Prime MatchStatsHandler MemoryCache (match:core:7790) while account owns 2 boosts and has not claimed Match #7790
        await using (MerrickContext dbBefore = harness.CreateContext())
        {
            MatchStatsHandler statsBefore = new(
                new AccountRepository(dbBefore),
                new MatchHistoryRepository(dbBefore),
                mockRedis.Object,
                mockHeroDefs.Object,
                NullLogger<MatchStatsHandler>.Instance,
                hybridCache: null,
                memoryCache: memoryCache);

            DefaultHttpContext reqBefore = new();
            reqBefore.Items["SessionAccountName"] = "cache_sync_player";
            reqBefore.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
            {
                ["f"] = "get_match_stats",
                ["match_id"] = "7790"
            });

            PhpResult resBefore = (await statsBefore.HandleRequestAsync(reqBefore)).Should().BeOfType<PhpResult>().Subject;
            MatchStatsResponse dataBefore = resBefore.Data.Should().BeOfType<MatchStatsResponse>().Subject;
            dataBefore.MatchPlayerStatistics[7790][340].MasteryBoostNum.Should().Be(2);
            dataBefore.MatchPlayerStatistics[7790][340].MasteryCanBoost.Should().Be("1");
        }

        // 2. Redeem boost with a SaveMasteryAsync wrapper that calls ChangeTracker.Clear() (simulating transient retry) and verify HybridCache invalidation
        Mock<ISessionRepository> mockSessionRepo = new();
        Mock<INexusHybridCache> mockHybridCache = new();

        await using (MerrickContext dbRedeem = harness.CreateContext())
        {
            MatchHistoryRepository realHistoryRepo = new(dbRedeem);
            Mock<IMatchHistoryRepository> clearOnSaveHistoryRepo = new();
            clearOnSaveHistoryRepo
                .Setup(r => r.GetPlayerStatisticsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int mid, int aid, CancellationToken ct) => realHistoryRepo.GetPlayerStatisticsAsync(mid, aid, ct));
            clearOnSaveHistoryRepo
                .Setup(r => r.SaveMasteryAsync(It.IsAny<Mastery>(), It.IsAny<CancellationToken>()))
                .Returns(async (Mastery m, CancellationToken ct) =>
                {
                    await realHistoryRepo.SaveMasteryAsync(m, ct);
                    dbRedeem.ChangeTracker.Clear();
                });

            UseMasteryBoostHandler useHandler = new(
                new AccountRepository(dbRedeem),
                clearOnSaveHistoryRepo.Object,
                mockSessionRepo.Object,
                NullLogger<UseMasteryBoostHandler>.Instance,
                dbRedeem,
                mockHybridCache.Object);

            DefaultHttpContext boostCtx = new();
            boostCtx.Items["SessionAccountName"] = "cache_sync_player";
            boostCtx.Request.ContentType = "application/x-www-form-urlencoded";
            boostCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["match_id"] = "7790",
                ["is_super"] = "0"
            });

            PhpResult boostRes = (await useHandler.HandleRequestAsync(boostCtx)).Should().BeOfType<PhpResult>().Subject;
            IDictionary boostDict = boostRes.Data.Should().BeAssignableTo<IDictionary>().Subject;
            boostDict["status"].Should().Be("OK");
        }

        mockHybridCache.Verify(c => c.RemoveByTagAsync("account:340", It.IsAny<CancellationToken>()), Times.Once);
        mockHybridCache.Verify(c => c.RemoveByTagAsync("account:nick:cache_sync_player", It.IsAny<CancellationToken>()), Times.Once);
        mockHybridCache.Verify(c => c.RemoveAsync("match:stats:7790:340", It.IsAny<CancellationToken>()), Times.Once);

        // Verify User.TotalLevel persisted despite ChangeTracker.Clear() inside SaveMasteryAsync
        await using (MerrickContext dbVerify = harness.CreateContext())
        {
            Account? persisted = await new AccountRepository(dbVerify).GetByNicknameAsync("cache_sync_player");
            persisted!.User.TotalLevel.Should().Be(1, "UseMasteryBoostHandler must re-attach detached User and persist TotalLevel after ChangeTracker.Clear()");
            MasteryConsumables.MasteryBoostsOwned(persisted.User).Should().Be(1);
        }

        // 3. Call MatchStatsHandler with the SAME MemoryCache (match:core:7790 is still cached!) -> requester player stats MUST refresh to 1 boost and canboost=0
        await using (MerrickContext dbAfter = harness.CreateContext())
        {
            MatchStatsHandler statsAfter = new(
                new AccountRepository(dbAfter),
                new MatchHistoryRepository(dbAfter),
                mockRedis.Object,
                mockHeroDefs.Object,
                NullLogger<MatchStatsHandler>.Instance,
                hybridCache: null,
                memoryCache: memoryCache);

            DefaultHttpContext reqAfter = new();
            reqAfter.Items["SessionAccountName"] = "cache_sync_player";
            reqAfter.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
            {
                ["f"] = "get_match_stats",
                ["match_id"] = "7790"
            });

            PhpResult resAfter = (await statsAfter.HandleRequestAsync(reqAfter)).Should().BeOfType<PhpResult>().Subject;
            MatchStatsResponse dataAfter = resAfter.Data.Should().BeOfType<MatchStatsResponse>().Subject;
            dataAfter.MatchPlayerStatistics[7790][340].MasteryBoostNum.Should().Be(1);
            dataAfter.MatchPlayerStatistics[7790][340].MasteryCanBoost.Should().Be("0");
            dataAfter.MatchMastery!.MasteryExperienceCanBoost.Should().BeFalse();
        }
    }

    [Fact]
    public void S1b2g_MasteryConsumables_And_Calculator_HandleSuperBoostBundles_And_RepeatableSuperBoostRewards()
    {
        // protects: Support for "m.Super boost * N" bundle codes and repeatable "mb.super_boost" mastery tier claims
        // fails_when: Super boost bundles return 0 count, grant ignores "mb.super_boost", or EvaluateClaim flags repeatable super boosts as AlreadyClaimed
        // seam: MasteryConsumables.TryMatchBoostItem / TryConsumeOneBoost / GrantRewardProduct + MasteryRewardCalculator.EvaluateClaim
        (Account account, User user) = CreateTestAccount("super_bundle_player", 350);
        user.InventoryItems.Add(new UserInventory { UserID = user.ID, ItemCode = "m.Super boost * 3", Quantity = 2 });

        MasteryConsumables.GetTotalMasteryBoostsOwned(user, isSuper: true).Should().Be(6);
        MasteryConsumables.GetTotalMasteryBoostsOwned(user, isSuper: false).Should().Be(0, "super boost bundle must never count as regular boost");

        MasteryConsumables.TryConsumeOneBoost(user, isSuper: true, out _).Should().BeTrue();
        user.InventoryItems.Single(i => i.ItemCode == "m.Super boost * 3").Quantity.Should().Be(1);
        MasteryConsumables.SuperMasteryBoostsOwned(user).Should().Be(2);
        MasteryConsumables.GetTotalMasteryBoostsOwned(user, isSuper: true).Should().Be(5);

        (string normCode, int normQty) = MasteryConsumables.GrantRewardProduct(
            user, account.ID, "mb.super_boost", 2, TimeProvider.System.GetUtcNow(), "MasteryClaim");
        normCode.Should().Be(MasteryConsumables.SuperMasteryBoostCode);
        normQty.Should().Be(2);
        MasteryConsumables.SuperMasteryBoostsOwned(user).Should().Be(4);
        MasteryConsumables.GetTotalMasteryBoostsOwned(user, isSuper: true).Should().Be(7);

        MasteryRewardsConfiguration cfg = new()
        {
            MasteryRewards =
            [
                new MasteryReward
                {
                    RequiredLevel = 55,
                    ProductIdentifier = 4605,
                    ProductCode = "mb.super_boost",
                    ProductQuantity = 1,
                    ProductName = "Super Mastery Boost",
                    ProductLocalResource = string.Empty,
                    GoldCoins = 0,
                    SilverCoins = 0,
                    PlinkoTickets = 0
                }
            ]
        };
        MasteryClaimResult repeatClaim = MasteryRewardCalculator.EvaluateClaim(
            cfg,
            requestedLevel: 55,
            ownedStoreItems: ["mb.super_boost", "ma.Super Mastery Boost * 2"],
            earnedTotalLevel: 55);
        repeatClaim.Status.Should().Be(MasteryClaimStatus.Success);
    }

    [Fact]
    public void S2d_PlayerProfileStatsBuilder_AcceptsExplicitUnlockAllItemsOverride()
    {
        // protects: Explicit unlockAllItems flow through PlayerProfileStatsBuilder without relying solely on static global state
        // fails_when: Passing unlockAllItems=false still injects unlocked catalog items into OwnedStoreItems
        // seam: PlayerProfileStatsBuilder.CreateShowSimpleStatsResponse(..., unlockAllItems)
        (Account account, _) = CreateTestAccount("explicit_unlock_player", 601);
        UpgradesResolutionService.UnlockAllItems = true;

        PlayerStatisticsAggregatedDTO stats = new();
        Mock<IMasteryRewardService> mockMasteryRewards = new();
        mockMasteryRewards.Setup(m => m.GenerateMasteryRewardsString(account)).Returns(string.Empty);
        Mock<IHeroDefinitionService> mockHeroDefs = new();

        PlayerProfileStatsBuilder builder = new(mockMasteryRewards.Object);
        ShowSimpleStatsResponse lockedProfile = builder.CreateShowSimpleStatsResponse(
            account, stats, 1, mockHeroDefs.Object, unlockAllItems: false);

        lockedProfile.OwnedStoreItems.Should().Contain(StoreDefaults.DefaultTaunt);
        lockedProfile.OwnedStoreItems.Should().NotContain("aa.Hero_Valkyrie.Alt2",
            "explicit unlockAllItems:false must keep non-default store items excluded even if global static is true");
    }
}

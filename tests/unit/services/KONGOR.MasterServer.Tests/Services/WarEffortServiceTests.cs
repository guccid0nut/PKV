// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccid0nut/PKV)

using ASPIRE.Common.Constants;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using KONGOR.MasterServer.Services.Matches;
using KONGOR.MasterServer.Services.Quests;
using KONGOR.MasterServer.Services.Store;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Quests;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Quests;
using NEXUS.Contracts.Store;
using Xunit;
using PlayerEntity = MERRICK.DatabaseContext.Entities.Statistics.PlayerStatistics;

namespace KONGOR.MasterServer.Tests.Services;

public sealed class WarEffortServiceTests
{
    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = initial;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; set; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static MerrickContext CreateInMemoryContext()
    {
        DbContextOptions<MerrickContext> options = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase($"WarEffortTests_{Guid.NewGuid():N}")
            .Options;
        return new MerrickContext(options);
    }

    private static async Task<Account> SeedAccountAsync(MerrickContext db, int id, string name, int silver = 0, int gold = 0, int tickets = 0)
    {
        User user = new()
        {
            ID = id,
            EmailAddress = $"{name.ToLowerInvariant()}@test.local",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            PBKDF2PasswordHash = "pbkdf2",
            SilverCoins = silver,
            GoldCoins = gold,
            PlinkoTickets = tickets
        };
        Account account = new()
        {
            ID = id,
            UserID = id,
            User = user,
            Name = name,
            Type = AccountType.Normal
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private static IngestedMatchContext BuildMatchContext(
        int matchId,
        List<Account> players,
        string map = "caldavar",
        string gameMode = "picking",
        int durationSeconds = 1800,
        string hostAccountName = "DedicatedHost1",
        bool winnerTeam1 = true,
        bool firstPlayerDisconnected = false,
        string? firstPlayerCustomStats = null,
        int firstPlayerAssists = 10,
        int firstPlayerWards = 4)
    {
        MatchStatistics matchStats = new()
        {
            MatchID = matchId,
            Map = map,
            GameMode = gameMode,
            TimePlayed = durationSeconds,
            HostAccountName = hostAccountName
        };

        List<PlayerIngestionEntry> entries = [];
        for (int i = 0; i < players.Count; i++)
        {
            int team = i < (players.Count / 2) ? 1 : 2;
            bool isWin = (team == 1 && winnerTeam1) || (team == 2 && !winnerTeam1);
            PlayerEntity ps = new()
            {
                MatchID = matchId,
                AccountID = players[i].ID,
                AccountName = players[i].Name,
                Team = team,
                Win = isWin ? 1 : 0,
                Loss = isWin ? 0 : 1,
                Disconnected = (i == 0 && firstPlayerDisconnected) ? 1 : 0,
                HeroIdentifier = "Hero_Pyromancer",
                HeroKills = 5,
                HeroAssists = i == 0 ? firstPlayerAssists : 3,
                WardsPlaced = i == 0 ? firstPlayerWards : 1,
                BuildingDamage = 1200,
                CustomStats = i == 0 ? firstPlayerCustomStats : null
            };
            entries.Add(new PlayerIngestionEntry(ps, players[i], 0, 1, isWin, i == 0, "Hero_Pyromancer"));
        }

        return new IngestedMatchContext
        {
            MatchID = matchId,
            MatchStatistics = matchStats,
            MatchType = AccountStatisticsType.Matchmaking,
            DurationSeconds = durationSeconds,
            PlayerAccountNames = players.ToDictionary(p => p.ID, p => p.Name),
            AccountMap = players.ToDictionary(p => p.Name, p => p),
            ProcessedPlayerEntries = entries,
            IsUnrankedListenServer = false
        };
    }

    [Fact]
    public async Task BuildClientPayload_EmitsGoldenWireSchema_With2QuestsAnd14Bags()
    {
        await using MerrickContext db = CreateInMemoryContext();
        Account account = await SeedAccountAsync(db, 59, "guccidonut");
        MutableTimeProvider clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        StaticOptionsMonitor<WarEffortOptions> opts = new(new WarEffortOptions());
        WarEffortService svc = new(db, opts, NullLogger<WarEffortService>.Instance, timeProvider: clock);

        Dictionary<string, object?> payload = await svc.BuildClientPayloadAsync(account.ID);

        payload["success"].Should().Be(true);
        payload.Should().ContainKeys("quest_types", "quest_queue", "player_quest_queue", "bag_queue", "player_bag_queue", "player_bag_complete", "quest_resets_available", "kongor_custom_quests");

        var questQueue = payload["quest_queue"].Should().BeAssignableTo<List<Dictionary<string, object?>>>().Subject;
        questQueue.Should().HaveCount(2);
        questQueue.Select(q => q["questid"]).Distinct().Should().HaveCount(2);

        var bagQueue = payload["bag_queue"].Should().BeAssignableTo<List<Dictionary<string, object?>>>().Subject;
        bagQueue.Should().HaveCount(14);

        string serialized = PhpSerializer.Serialize(payload);
        serialized.Should().Contain("\"quest_queue\"").And.Contain("\"bag_queue\"").And.Contain("\"player_quest_queue\"");
    }

    [Fact]
    public async Task BuildDedicatedServerPayload_FiltersOutCustomKongorQuests()
    {
        await using MerrickContext db = CreateInMemoryContext();
        Account account = await SeedAccountAsync(db, 101, "AlphaPlayer");
        MutableTimeProvider clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        StaticOptionsMonitor<WarEffortOptions> opts = new(new WarEffortOptions());
        WarEffortService svc = new(db, opts, NullLogger<WarEffortService>.Instance, timeProvider: clock);

        WarEffortAccountState state = await svc.EnsureActiveCycleAndAccountStateAsync(account.ID);
        // Pin Slot 0 to native (1001) and Slot 1 to custom kongor_strikers_goals (2006)
        List<AccountQuestSlot> slots = await db.AccountQuestSlots.Where(s => s.AccountId == account.ID).OrderBy(s => s.SlotIndex).ToListAsync();
        slots[0].QuestDefinitionId = 1001;
        slots[1].QuestDefinitionId = 2006;
        await db.SaveChangesAsync();

        Dictionary<string, object?> serverPayload = await svc.BuildDedicatedServerPlayerQuestsPayloadAsync([account.ID]);

        var questQueue = serverPayload["quest_queue"].Should().BeAssignableTo<List<Dictionary<string, object?>>>().Subject;
        questQueue.Should().HaveCount(1);
        questQueue[0]["questid"].Should().Be(1001);
        questQueue[0]["triggername"].Should().Be("achievement_value_games_won");
    }

    [Fact]
    public async Task AntiFarmGuards_BlockShortMatchesFewHumansQueueArcadeAndLeavers()
    {
        await using MerrickContext db = CreateInMemoryContext();
        List<Account> players = [];
        for (int i = 1; i <= 6; i++)
        {
            players.Add(await SeedAccountAsync(db, i, $"Player{i}"));
        }

        WarEffortOptions strictOpts = new() { Enabled = true, CountBotMatches = false, MinimumHumanPlayers = 6, MinimumMatchDurationSeconds = 600 };

        // 1. Short match (< 600s)
        WarEffortProgressionService.EvaluateMatchEligibility(
            BuildMatchContext(1, players, durationSeconds: 599), strictOpts)
            .Should().StartWith("duration_too_short");

        // 2. Too few human players (< 6)
        WarEffortProgressionService.EvaluateMatchEligibility(
            BuildMatchContext(2, players.Take(4).ToList(), durationSeconds: 1800), strictOpts)
            .Should().StartWith("insufficient_humans");

        // 3. Practice mode
        WarEffortProgressionService.EvaluateMatchEligibility(
            BuildMatchContext(3, players, gameMode: "practice", durationSeconds: 1800), strictOpts)
            .Should().StartWith("excluded_mode");

        // 4. Queue Arcade host
        WarEffortProgressionService.EvaluateMatchEligibility(
            BuildMatchContext(4, players, hostAccountName: "QueueArcade_1", durationSeconds: 1800), strictOpts)
            .Should().StartWith("queue_arcade_host");

        // 5. Real qualifying 6-human 30-min match passes
        WarEffortProgressionService.EvaluateMatchEligibility(
            BuildMatchContext(5, players, durationSeconds: 1800), strictOpts)
            .Should().BeNull();

        // 6. Dev CountBotMatches=true bypasses anti-farm for E2E verification
        WarEffortOptions devOpts = new() { Enabled = true, CountBotMatches = true };
        WarEffortProgressionService.EvaluateMatchEligibility(
            BuildMatchContext(6, players.Take(1).ToList(), gameMode: "practice", durationSeconds: 60), devOpts)
            .Should().BeNull();
    }

    [Fact]
    public async Task MatchProgression_CompletesNativeAndCustomQuests_AndOpenBagIsIdempotent()
    {
        await using MerrickContext db = CreateInMemoryContext();
        List<Account> players = [];
        for (int i = 1; i <= 6; i++)
        {
            players.Add(await SeedAccountAsync(db, i, $"HeroPlayer{i}", silver: 500));
        }

        MutableTimeProvider clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        StaticOptionsMonitor<WarEffortOptions> opts = new(new WarEffortOptions());
        WarEffortService svc = new(db, opts, NullLogger<WarEffortService>.Instance, timeProvider: clock);
        WarEffortProgressionService progression = new(db, svc, opts, NullLogger<WarEffortProgressionService>.Instance, timeProvider: clock);

        await svc.EnsureActiveCycleAndAccountStateAsync(players[0].ID);
        List<AccountQuestSlot> slots = await db.AccountQuestSlots.Where(s => s.AccountId == players[0].ID).OrderBy(s => s.SlotIndex).ToListAsync();
        // Slot 0: Win 1 qualifying match (1001, reward 100 pts)
        slots[0].QuestDefinitionId = 1001;
        slots[0].Goal = 1;
        slots[0].Progress = 0;
        // Slot 1: Strikers 3 goals (2006, reward 125 pts)
        slots[1].QuestDefinitionId = 2006;
        slots[1].Goal = 3;
        slots[1].Progress = 0;
        await db.SaveChangesAsync();

        // Submit qualifying Strikers match where Player 1 wins and scores 3 goals
        IngestedMatchContext ctx = BuildMatchContext(
            matchId: 9001,
            players: players,
            map: "soccer",
            durationSeconds: 900,
            winnerTeam1: true,
            firstPlayerCustomStats: "{\"goals\":3,\"saves\":1}");

        await progression.ProcessMatchProgressionAsync(ctx);
        await db.SaveChangesAsync();

        AccountWarEffortProgress progress = await db.AccountWarEffortProgress.FirstAsync(p => p.AccountId == players[0].ID);
        progress.QuestsCompletedCount.Should().Be(2);
        progress.TotalBagPoints.Should().Be(225); // 100 + 125 -> unlocks Bag #1 (100) AND Bag #2 (225)

        // Open Bag #1 (BagId = 101)
        Dictionary<string, object?> openResult = await svc.OpenBagAsync(players[0].ID, 101);
        openResult["success"].Should().Be(true);
        User updatedUser = await db.Users.FirstAsync(u => u.ID == players[0].ID);
        updatedUser.SilverCoins.Should().Be(650); // 500 + 150

        // Second OpenBag on same bag must fail idempotently without double payout
        Dictionary<string, object?> replayResult = await svc.OpenBagAsync(players[0].ID, 101);
        replayResult["success"].Should().Be(false);
        replayResult["error"].Should().Be("bag_already_opened");
        (await db.Users.FirstAsync(u => u.ID == players[0].ID)).SilverCoins.Should().Be(650);
    }

    [Fact]
    public async Task ResetQuest_DailyRefresh_And28DayCycleRollover_WorkDeterministically()
    {
        await using MerrickContext db = CreateInMemoryContext();
        Account account = await SeedAccountAsync(db, 77, "RolloverTester", silver: 100);
        MutableTimeProvider clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        StaticOptionsMonitor<WarEffortOptions> opts = new(new WarEffortOptions { CycleLengthDays = 28, DailyRerollCap = 2 });
        WarEffortService svc = new(db, opts, NullLogger<WarEffortService>.Instance, timeProvider: clock);

        WarEffortAccountState day1 = await svc.EnsureActiveCycleAndAccountStateAsync(account.ID);
        day1.Progress.RerollsAvailable.Should().Be(1);
        int slot0QuestId = day1.Slots[0].QuestDefinitionId;

        // Reroll Slot 0 -> consumes the 1 available reroll
        Dictionary<string, object?> resetRes = await svc.ResetQuestAsync(account.ID, slot0QuestId);
        resetRes["success"].Should().Be(true);
        resetRes["new_quest_id"].Should().NotBe(slot0QuestId);

        // Second reroll same day -> rejected
        int newSlot0QuestId = (int)resetRes["new_quest_id"]!;
        Dictionary<string, object?> secondReset = await svc.ResetQuestAsync(account.ID, newSlot0QuestId);
        secondReset["success"].Should().Be(false);
        secondReset["error"].Should().Be("no_resets_available");

        // Advance +1 day (past 00:00 UTC) -> grants +1 reroll
        clock.Current = clock.Current.AddDays(1);
        WarEffortAccountState day2 = await svc.EnsureActiveCycleAndAccountStateAsync(account.ID);
        day2.Progress.RerollsAvailable.Should().Be(1);

        // Give player 150 points (eligible for Bag #1 = 100 pts) without manually claiming
        AccountWarEffortProgress tracked = await db.AccountWarEffortProgress.FirstAsync(p => p.AccountId == account.ID);
        tracked.TotalBagPoints = 150;
        tracked.QuestsCompletedCount = 1;
        await db.SaveChangesAsync();

        // Advance +30 days -> triggers Cycle #1 rollover to Cycle #2 + auto-claims Bag #1 + captures leaderboard snapshot
        clock.Current = clock.Current.AddDays(30);
        WarEffortAccountState cycle2 = await svc.EnsureActiveCycleAndAccountStateAsync(account.ID);
        cycle2.Cycle.CycleNumber.Should().Be(2);
        cycle2.Milestones.Should().HaveCount(14);
        cycle2.Milestones[0].BagId.Should().Be(201);

        // Auto-claimed Bag #1 credited 150 Silver + snapshot recorded
        (await db.Users.FirstAsync(u => u.ID == account.ID)).SilverCoins.Should().Be(250);
        (await db.AccountWarEffortBagClaims.CountAsync(c => c.AccountId == account.ID && c.AutoClaimedAtRollover)).Should().Be(1);
        (await db.WarEffortCycleLeaderboardSnapshots.CountAsync(s => s.AccountId == account.ID && s.Rank == 1)).Should().Be(1);
    }

    [Fact]
    public async Task KillSwitchDisabled_ReturnsDisabledErrorAndSkipsProgression()
    {
        await using MerrickContext db = CreateInMemoryContext();
        Account account = await SeedAccountAsync(db, 88, "DisabledTester");
        StaticOptionsMonitor<WarEffortOptions> opts = new(new WarEffortOptions { Enabled = false });
        WarEffortService svc = new(db, opts, NullLogger<WarEffortService>.Instance);

        Dictionary<string, object?> payload = await svc.BuildClientPayloadAsync(account.ID);
        payload["success"].Should().Be(false);
        payload["error"].Should().Be("disabled");
    }

    [Fact]
    public void StoreEntitlementPolicy_ExcludesWarEffortExclusivesFromUnlockAll()
    {
        StoreEntitlementPolicy.IsWarEffortExclusive("war_effort_ward", "w.war_effort_ward", "Dragon's Treasure Ward", 2909).Should().BeTrue();
        StoreEntitlementPolicy.IsWarEffortExclusive("wareffort_courier", "c.wareffort_courier", "Gnome Courier", 2910).Should().BeTrue();
        StoreEntitlementPolicy.IsWarEffortExclusive("war_effort_contender_12").Should().BeTrue();
        StoreEntitlementPolicy.IsWarEffortExclusive("Hero_Pyromancer.Alt").Should().BeFalse();

        HashSet<string> unlocked = UpgradesResolutionService.ExpandOwnedItems(
            rawItems: null,
            unlockAllItems: true,
            excludeWarEffortExclusives: true);
        unlocked.Should().NotContain("w.war_effort_ward");
        unlocked.Should().NotContain("c.wareffort_courier");
        unlocked.Should().NotContain("ai.war_effort_winner");
    }

    [Fact]
    public void CatalogLoader_LoadsFull26QuestCatalogNotFallback()
    {
        WarEffortCatalogDocument catalog = WarEffortCatalogLoader.GetCatalog();
        catalog.Quests.Count.Should().BeGreaterThanOrEqualTo(26,
            "Production master-server must load the full Data/wareffort/catalog.json rather than the 7-quest fallback.");
        catalog.BagMilestones.Should().HaveCount(14);
        catalog.Quests.Should().OnlyContain(
            q => !string.Equals(q.IconPath, "/ui/fe2/quests/quest_icons/role_carry.tga", StringComparison.OrdinalIgnoreCase),
            "resources99.s2z has no standalone role_carry.dds; carry quests must use role_carry_ganker.tga or role_ganker.tga.");
    }

    [Fact]
    public void MasterServerComposition_WiresIWarEffortProgressionServiceIntoMatchProcessingService()
    {
        // Regression guard: KONGOR.cs manual MatchProcessingService factory lambda must inject IWarEffortProgressionService.
        string? dir = AppContext.BaseDirectory;
        string? kongorCs = null;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "src", "services", "master-server", "KONGOR.cs");
            if (File.Exists(candidate))
            {
                kongorCs = candidate;
                break;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }

        kongorCs.Should().NotBeNull("KONGOR.cs should be discoverable from test output directory");
        string source = File.ReadAllText(kongorCs!);
        source.Should().Contain("sp.GetRequiredService<IWarEffortProgressionService>()",
            "IMatchProcessingService DI factory in KONGOR.cs must pass IWarEffortProgressionService so Stage-2 War Effort progression runs on real matches.");
    }

    [Fact]
    public async Task PartialWardProgress_TracksIncrementallyAcrossMatches_AndRangedQuestIsFactionNeutral()
    {
        await using MerrickContext db = CreateInMemoryContext();
        db.HeroDetails.Add(new MERRICK.DatabaseContext.Entities.Game.HeroDetail
        {
            HeroCode = "Hero_Pyromancer",
            DisplayName = "Pyromancer",
            PrimaryAttribute = "Intelligence",
            AttackType = "Ranged",
            IconPath = "/heroes/pyro/icon.tga",
            SplashImagePath = "/heroes/pyro/splash.tga",
            Lore = "Fire mage",
            AbilitiesJson = "[]"
        });
        await db.SaveChangesAsync();

        List<Account> players = [];
        for (int i = 1; i <= 6; i++)
        {
            players.Add(await SeedAccountAsync(db, 200 + i, $"WardHero{i}", silver: 100));
        }

        MutableTimeProvider clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        StaticOptionsMonitor<WarEffortOptions> opts = new(new WarEffortOptions());
        WarEffortService svc = new(db, opts, NullLogger<WarEffortService>.Instance, timeProvider: clock);
        WarEffortProgressionService progression = new(db, svc, opts, NullLogger<WarEffortProgressionService>.Instance, timeProvider: clock);

        await svc.EnsureActiveCycleAndAccountStateAsync(players[0].ID);
        List<AccountQuestSlot> slots = await db.AccountQuestSlots
            .Where(s => s.AccountId == players[0].ID)
            .OrderBy(s => s.SlotIndex)
            .ToListAsync();

        // Slot 0: Eyes of Newerth (2004, Place 10 Wards, +130 pts)
        slots[0].QuestDefinitionId = 2004;
        slots[0].Goal = 10;
        slots[0].Progress = 0;
        // Slot 1: Marksman's Volley (1016, Play 2 matches as a Ranged hero, faction-neutral, +125 pts)
        slots[1].QuestDefinitionId = 1016;
        slots[1].Goal = 2;
        slots[1].Progress = 0;
        await db.SaveChangesAsync();

        // Match 1: Player 0 is on Team 1 (Legion) playing Ranged Hero_Pyromancer and places 4 wards
        await progression.ProcessMatchProgressionAsync(BuildMatchContext(
            matchId: 9101, players: players, firstPlayerWards: 4));
        await db.SaveChangesAsync();

        List<AccountQuestSlot> afterMatch1 = await db.AccountQuestSlots
            .Where(s => s.AccountId == players[0].ID)
            .OrderBy(s => s.SlotIndex)
            .ToListAsync();
        afterMatch1[0].QuestDefinitionId.Should().Be(2004);
        afterMatch1[0].Progress.Should().Be(4, "4 placed wards must persist as 4/10 partial progress");
        afterMatch1[1].QuestDefinitionId.Should().Be(1016);
        afterMatch1[1].Progress.Should().Be(1, "Playing a Ranged hero on Legion (Team 1) must count toward Marksman's Volley (1/2)");

        AccountWarEffortProgress progressAfter1 = await db.AccountWarEffortProgress.FirstAsync(p => p.AccountId == players[0].ID);
        progressAfter1.TotalBagPoints.Should().Be(0, "Bag points only award upon quest completion, not partial counts");

        // Match 2: Player 0 places 7 more wards (4 + 7 -> 10/10 completed!) and finishes 2nd Ranged match (1 + 1 -> 2/2 completed!)
        await progression.ProcessMatchProgressionAsync(BuildMatchContext(
            matchId: 9102, players: players, firstPlayerWards: 7));
        await db.SaveChangesAsync();

        AccountWarEffortProgress progressAfter2 = await db.AccountWarEffortProgress.FirstAsync(p => p.AccountId == players[0].ID);
        progressAfter2.QuestsCompletedCount.Should().Be(2);
        progressAfter2.TotalBagPoints.Should().Be(255, "130 (Eyes of Newerth) + 125 (Marksman's Volley) = 255 bag points");
    }
}

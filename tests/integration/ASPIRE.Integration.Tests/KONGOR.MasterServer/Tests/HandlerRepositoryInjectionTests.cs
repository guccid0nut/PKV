// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections;
using System.Globalization;
using System.Net;
using System.Text.Json;
using ASPIRE.Common.Serialization.PHP;
using ASPIRE.Tests.InProcess;
using ASPIRE.Tests.MERRICK.DatabaseContext.Tests;
using KONGOR.MasterServer.Extensions.Cache;
using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Models.RequestResponse.ClientRequester;
using KONGOR.MasterServer.Models.RequestResponse.GameData;
using KONGOR.MasterServer.Models.RequestResponse.Store;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Replay;
using KONGOR.MasterServer.Services.Security;
using KONGOR.MasterServer.Services.Stats;
using KONGOR.MasterServer.Services.Storage;
using KONGOR.MasterServer.Services.Store;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Relational;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using StackExchange.Redis;
using Xunit;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class HandlerRepositoryInjectionTests
{
    private static User CreateTestUser(string name) => new()
    {
        EmailAddress = $"{name.ToLowerInvariant()}@kongor.net",
        SRPPasswordSalt = "salt",
        SRPPasswordHash = "hash",
        PBKDF2PasswordHash = "pbkdf2_hash"
    };

    [Fact]
    public async Task ClanHandler_ExecutesSuccessfully_WithInjectedRepositories()
    {
        string dbName = $"ClanHandlerTest_{Guid.NewGuid():N}";
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        Clan testClan = new() { Name = "AlphaClan", Tag = "ALP", TimestampCreated = TimeProvider.System.GetUtcNow() };
        await db.Clans.AddAsync(testClan);
        await db.SaveChangesAsync();

        Account leader = new() { Name = "LeaderPlayer", Clan = testClan, ClanID = testClan.ID, ClanTier = ClanTier.Leader, IsMain = true, User = CreateTestUser("LeaderPlayer") };
        Account member = new() { Name = "MemberPlayer", Clan = testClan, ClanID = testClan.ID, ClanTier = ClanTier.Member, IsMain = true, User = CreateTestUser("MemberPlayer") };

        await db.Accounts.AddRangeAsync(leader, member);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        IClanRepository clanRepo = new ClanRepository(db);
        IAccountRepository accountRepo = new AccountRepository(db);
        InProcessDistributedCacheStore cache = new();
        await cache.SetAccountNameForSessionCookie("valid_cookie", leader.Name);

        ClanHandler handler = new(clanRepo, accountRepo, cache, NullLogger<ClanHandler>.Instance);

        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            { "f", "set_rank" },
            { "cookie", "valid_cookie" },
            { "target_id", member.ID.ToString(CultureInfo.InvariantCulture) },
            { "clan_id", testClan.ID.ToString(CultureInfo.InvariantCulture) },
            { "rank", "Officer" }
        });

        IActionResult result = await handler.HandleRequestAsync(httpContext);
        Assert.IsType<PhpResult>(result);

        Account? updatedMember = await accountRepo.GetByIdAsync(member.ID);
        Assert.NotNull(updatedMember);
        Assert.Equal(ClanTier.Officer, updatedMember.ClanTier);
    }

    [Fact]
    public async Task FriendsHandler_SearchAndRemove_ExecutesWithInjectedRepositories()
    {
        string dbName = $"FriendsHandlerTest_{Guid.NewGuid():N}";
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        Account a1 = new() { Name = "GamerOne", IsMain = true, User = CreateTestUser("GamerOne") };
        Account a2 = new() { Name = "GamerTwo", IsMain = true, User = CreateTestUser("GamerTwo") };

        await db.Accounts.AddRangeAsync(a1, a2);
        await db.SaveChangesAsync();

        await db.Friendships.AddRangeAsync(
            new Friendship { AccountID = a1.ID, FriendAccountID = a2.ID, FriendGroup = "General", Status = FriendshipStatus.Accepted },
            new Friendship { AccountID = a2.ID, FriendAccountID = a1.ID, FriendGroup = "General", Status = FriendshipStatus.Accepted });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        IFriendshipRepository friendRepo = new FriendshipRepository(db);
        IAccountRepository accountRepo = new AccountRepository(db);
        InProcessDistributedCacheStore cache = new();
        await cache.SetAccountNameForSessionCookie("sess_cookie", a1.Name);

        FriendsHandler handler = new(friendRepo, accountRepo, cache, NullLogger<FriendsHandler>.Instance);

        // 1. Autocomplete Search
        DefaultHttpContext searchContext = new();
        searchContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            { "f", "autocomplete" },
            { "nickname", "GamerT" }
        });

        IActionResult searchResult = await handler.HandleRequestAsync(searchContext);
        PhpResult phpSearchResult = Assert.IsType<PhpResult>(searchResult);
        Hashtable? searchData = phpSearchResult.Data as Hashtable;
        Assert.NotNull(searchData);
        Assert.True(searchData.Count > 0);

        // 2. Remove Friend
        DefaultHttpContext removeContext = new();
        removeContext.Request.ContentType = "application/x-www-form-urlencoded";
        removeContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            { "f", "remove_buddy" },
            { "cookie", "sess_cookie" },
            { "buddy_id", a2.ID.ToString(CultureInfo.InvariantCulture) }
        });

        IActionResult removeResult = await handler.HandleRequestAsync(removeContext);
        Assert.IsType<PhpResult>(removeResult);

        bool areFriends = await friendRepo.AreFriendsAsync(a1.ID, a2.ID);
        Assert.False(areFriends);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HandlerRepositoryInjection_DailySpecialHandler_ExecutesWithStoreAndAccountRepositories()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string dbName = $"DailySpecialTest_{Guid.NewGuid():N}";
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        User user = CreateTestUser("StoreUser");
        Account account = new() { Name = "StoreUser", User = user, IsMain = true };
        await db.Users.AddAsync(user);
        await db.Accounts.AddAsync(account);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        IStoreRepository storeRepo = new StoreRepository(db);
        IAccountRepository accountRepo = new AccountRepository(db);
        InProcessDistributedCacheStore cache = new();
        await cache.SetAccountNameForSessionCookie("dummy_cookie", "StoreUser");

        DailySpecialHandler handler = new(storeRepo, accountRepo, cache, NullLogger<DailySpecialHandler>.Instance);

        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            { "cookie", "dummy_cookie" }
        });

        IActionResult result = await handler.HandleRequestAsync(httpContext);
        ContentResult contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal(200, contentResult.StatusCode);
        Assert.Contains("vested_threshold", contentResult.Content);
    }

    [Fact]
    public async Task HandlerRepositoryInjection_GetBundleContentsHandler_ExecutesWithInjectedRepositories()
    {
        string dbName = $"BundleContentsTest_{Guid.NewGuid():N}";
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        User user = CreateTestUser("BundleUser");
        Account account = new() { Name = "BundleUser", User = user, IsMain = true };
        await db.Users.AddAsync(user);
        await db.Accounts.AddAsync(account);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        IStoreRepository storeRepo = new StoreRepository(db);
        IAccountRepository accountRepo = new AccountRepository(db);
        InProcessDistributedCacheStore cache = new();
        await cache.SetAccountNameForSessionCookie("bundle_cookie", "BundleUser");

        Mock<IBundleCatalogService> bundleServiceMock = new();
        bundleServiceMock.Setup(b => b.GetBundleContent(It.IsAny<int>(), It.IsAny<Account?>()))
            .Returns(new BundleContentResponse
            {
                Info = new BundleInfoDTO
                {
                    BundleID = 10,
                    GrabBag = false,
                    IsOwned = false
                },
                List = []
            });

        GetBundleContentsHandler handler = new(
            cache,
            storeRepo,
            accountRepo,
            bundleServiceMock.Object,
            NullLogger<GetBundleContentsHandler>.Instance);

        DefaultHttpContext httpContext = new();
        httpContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            { "bundle_id", "10" }
        });
        httpContext.Items["SessionAccountName"] = "BundleUser";

        IActionResult result = await handler.HandleRequestAsync(httpContext);
        ContentResult contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal(200, contentResult.StatusCode);
        Assert.Contains("\"bundle_id\":10", contentResult.Content);
    }

    [Fact]
    public async Task HandlerRepositoryInjection_HeroUsageHandler_AggregatesFromMatchHistoryRepository()
    {
        string dbName = $"HeroUsageTest_{Guid.NewGuid():N}";
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        PlayerStatistics ps1 = new()
        {
            MatchID = 1001,
            AccountID = 1,
            HeroIdentifier = "Hero_Valkyrie",
            Win = 1,
            Loss = 0,
            AccountName = "Player1"
        };
        PlayerStatistics ps2 = new()
        {
            MatchID = 1002,
            AccountID = 2,
            HeroIdentifier = "Hero_Valkyrie",
            Win = 0,
            Loss = 1,
            AccountName = "Player2"
        };
        await db.PlayerStatistics.AddRangeAsync(ps1, ps2);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        IMatchHistoryRepository matchRepo = new MatchHistoryRepository(db);
        Mock<IHeroDefinitionService> heroDefMock = new();
        heroDefMock.Setup(h => h.GetBaseHeroId(It.IsAny<string>())).Returns(1u);

        HeroUsageHandler handler = new(matchRepo, heroDefMock.Object, NullLogger<HeroUsageHandler>.Instance);

        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            { "f", "get_hero_usage_list" },
            { "cookie", "session_cookie" },
            { "sort", "use" }
        });

        IActionResult result = await handler.HandleRequestAsync(httpContext);
        PhpResult phpResult = Assert.IsType<PhpResult>(result);
        Dictionary<string, object>? dict = phpResult.Data as Dictionary<string, object>;
        Assert.NotNull(dict);
        Assert.Equal("1", dict["success"]);
        Assert.Contains("Hero_Valkyrie", (string)dict["data"]);
    }

    [Fact]
    public async Task HandlerRepositoryInjection_ReferralHandler_ResolvesAccountViaRepository()
    {
        string dbName = $"ReferralTest_{Guid.NewGuid():N}";
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        Account referee = new() { Name = "RefereeAcc", IsMain = true, User = CreateTestUser("RefereeAcc") };
        Account referrer = new() { Name = "ReferrerAcc", IsMain = true, User = CreateTestUser("ReferrerAcc") };
        await db.Accounts.AddRangeAsync(referee, referrer);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        IAccountRepository accountRepo = new AccountRepository(db);
        Mock<ISessionContextResolver> sessionResolverMock = new();
        sessionResolverMock.Setup(s => s.ResolveAccountAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(referee);

        ReferralHandler handler = new(accountRepo, sessionResolverMock.Object, NullLogger<ReferralHandler>.Instance);

        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            { "f", "set_ingame_referrer" },
            { "referrer", "ReferrerAcc" },
            { "match_id", "500" },
            { "cookie", "test_cookie" }
        });

        IActionResult result = await handler.HandleRequestAsync(httpContext);
        PhpResult phpResult = Assert.IsType<PhpResult>(result);
        Dictionary<string, object>? dict = phpResult.Data as Dictionary<string, object>;
        Assert.NotNull(dict);
        Assert.Equal("OK", dict["status"]);
    }

    [Fact]
    public async Task HandlerRepositoryInjection_ReplayHandler_UpdatesReplayUrlViaMatchHistoryRepository()
    {
        string dbName = $"ReplayHandlerTest_{Guid.NewGuid():N}";
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        MatchStatistics match = new()
        {
            MatchID = 888,
            ServerID = 1,
            HostAccountName = "HostGuy",
            Map = "caldavar",
            GameMode = "normal",
            TimestampRecorded = TimeProvider.System.GetUtcNow()
        };
        await db.MatchStatistics.AddAsync(match);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        IMatchHistoryRepository matchRepo = new MatchHistoryRepository(db);
        Mock<IReplayStorageProvider> storageMock = new();
        storageMock.Setup(s => s.UploadReplayAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://replays.kongor.net/M888.honreplay");

        ReplayHandler handler = new(NullLogger<ReplayHandler>.Instance, storageMock.Object, matchRepo);

        byte[] fakeFileBytes = [1, 2, 3, 4, 5];
        FormFile formFile = new(new MemoryStream(fakeFileBytes), 0, fakeFileBytes.Length, "file", "M888.honreplay");
        FormFileCollection files = [formFile];

        DefaultHttpContext httpContext = new();
        httpContext.Items["SessionAccountName"] = "HostGuy";
        httpContext.Request.ContentType = "multipart/form-data";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            { "match_id", "888" },
            { "cookie", "replay_cookie" }
        }, files);

        IActionResult result = await handler.HandleRequestAsync(httpContext);
        Assert.IsType<PhpResult>(result);

        MatchStatistics? updatedMatch = await matchRepo.GetMatchByIdAsync(888);
        Assert.NotNull(updatedMatch);
        Assert.Equal("https://replays.kongor.net/M888.honreplay", updatedMatch.ReplayUrl);
        Assert.Equal(5, updatedMatch.FileSize);
    }

    [Fact]
    public async Task MatchHistoryRepository_RankedCampaignQuery_CleanlyExcludesMidwars()
    {
        string dbName = $"MidwarsExclusionTest_{Guid.NewGuid():N}";
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        // Match 1: Caldavar ranked match
        MatchStatistics msCaldavar = new() { MatchID = 901, Map = "caldavar", GameMode = "normal", TimestampRecorded = TimeProvider.System.GetUtcNow() };
        PlayerStatistics psCaldavar = new() { MatchID = 901, AccountID = 77, AccountName = "RankedPlayer", RankedMatch = 1, PublicMatch = 0, Win = 1 };

        // Match 2: Midwars ranked match (should be excluded from campaign query)
        MatchStatistics msMidwars = new() { MatchID = 902, Map = "midwars", GameMode = "midwars", TimestampRecorded = TimeProvider.System.GetUtcNow() };
        PlayerStatistics psMidwars = new() { MatchID = 902, AccountID = 77, AccountName = "RankedPlayer", RankedMatch = 1, PublicMatch = 0, Win = 1 };

        // Match 3: Casual match
        MatchStatistics msCasual = new() { MatchID = 903, Map = "caldavar", GameMode = "casual", TimestampRecorded = TimeProvider.System.GetUtcNow() };
        PlayerStatistics psCasual = new() { MatchID = 903, AccountID = 77, AccountName = "RankedPlayer", RankedMatch = 0, PublicMatch = 1, Win = 0 };

        await db.MatchStatistics.AddRangeAsync(msCaldavar, msMidwars, msCasual);
        await db.PlayerStatistics.AddRangeAsync(psCaldavar, psMidwars, psCasual);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        IMatchHistoryRepository matchRepo = new MatchHistoryRepository(db);

        // Query campaign / ranked with currentSeason = 1
        IReadOnlyList<PlayerMatchRecordDTO> rankedCampaign = await matchRepo.GetPlayerMatchHistoryOverviewAsync(
            77, "RankedPlayer", "ranked", "1", 100);

        Assert.Single(rankedCampaign);
        Assert.Equal(901, rankedCampaign[0].MatchID);
        Assert.DoesNotContain(rankedCampaign, m => m.MatchID == 902);
    }

    [Fact]
    public async Task FriendsHandler_NewBanned_And_RemoveBanned_EnforcesCapAndDuration()
    {
        string dbName = $"FriendsHandlerBanTest_{Guid.NewGuid():N}";
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        Account a1 = new() { Name = "BanMaster", IsMain = true, User = CreateTestUser("BanMaster") };
        Account a2 = new() { Name = "ToxicRival", IsMain = true, User = CreateTestUser("ToxicRival") };

        await db.Accounts.AddRangeAsync(a1, a2);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        IFriendshipRepository friendRepo = new FriendshipRepository(db);
        IAccountRepository accountRepo = new AccountRepository(db);
        InProcessDistributedCacheStore cache = new();
        await cache.SetAccountNameForSessionCookie("sess_ban", a1.Name);

        FriendsHandler handler = new(friendRepo, accountRepo, cache, NullLogger<FriendsHandler>.Instance);

        // 1. Add Ban via new_banned
        DefaultHttpContext addBanContext = new();
        addBanContext.Request.ContentType = "application/x-www-form-urlencoded";
        addBanContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            { "f", "new_banned" },
            { "cookie", "sess_ban" },
            { "banned_id", a2.ID.ToString(CultureInfo.InvariantCulture) },
            { "reason", "Toxic behavior" }
        });

        IActionResult addResult = await handler.HandleRequestAsync(addBanContext);
        PhpResult phpAddResult = Assert.IsType<PhpResult>(addResult);
        Hashtable? addData = phpAddResult.Data as Hashtable;
        Assert.NotNull(addData);
        Assert.True(addData.ContainsKey("banned_id"));

        // Verify in DB that TimestampExpires is set ~6 months ahead
        Account? reloaded = await accountRepo.GetByIdAsync(a1.ID);
        Assert.NotNull(reloaded);
        AccountPeer? peer = reloaded.Peers.FirstOrDefault(p => p.PeerAccountID == a2.ID && p.Type == PeerType.Banned);
        Assert.NotNull(peer);
        Assert.NotNull(peer.TimestampExpires);
        Assert.True(peer.TimestampExpires.Value > TimeProvider.System.GetUtcNow().AddMonths(5));

        // 2. Remove Ban via remove_banned
        DefaultHttpContext removeBanContext = new();
        removeBanContext.Request.ContentType = "application/x-www-form-urlencoded";
        removeBanContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            { "f", "remove_banned" },
            { "cookie", "sess_ban" },
            { "banned_id", a2.ID.ToString(CultureInfo.InvariantCulture) }
        });

        IActionResult removeResult = await handler.HandleRequestAsync(removeBanContext);
        PhpResult phpRemoveResult = Assert.IsType<PhpResult>(removeResult);
        Hashtable? removeData = phpRemoveResult.Data as Hashtable;
        Assert.NotNull(removeData);

        Account? afterRemove = await accountRepo.GetByIdAsync(a1.ID);
        Assert.NotNull(afterRemove);
        Assert.DoesNotContain(afterRemove.Peers, p => p.PeerAccountID == a2.ID && p.Type == PeerType.Banned);
    }

    [Fact]
    public async Task FriendsHandler_NewIgnored_And_RemoveIgnored_ManagesIgnoreList()
    {
        string dbName = $"FriendsHandlerIgnoreTest_{Guid.NewGuid():N}";
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        Account a1 = new() { Name = "SilentPlayer", IsMain = true, User = CreateTestUser("SilentPlayer") };
        Account a2 = new() { Name = "SpamBot", IsMain = true, User = CreateTestUser("SpamBot") };

        await db.Accounts.AddRangeAsync(a1, a2);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        IFriendshipRepository friendRepo = new FriendshipRepository(db);
        IAccountRepository accountRepo = new AccountRepository(db);
        InProcessDistributedCacheStore cache = new();
        await cache.SetAccountNameForSessionCookie("sess_ignore", a1.Name);

        FriendsHandler handler = new(friendRepo, accountRepo, cache, NullLogger<FriendsHandler>.Instance);

        // 1. Add Ignore via new_ignored
        DefaultHttpContext addIgnoreContext = new();
        addIgnoreContext.Request.ContentType = "application/x-www-form-urlencoded";
        addIgnoreContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            { "f", "new_ignored" },
            { "cookie", "sess_ignore" },
            { "ignored_id", a2.ID.ToString(CultureInfo.InvariantCulture) },
            { "reason", "Mic spammer" }
        });

        IActionResult addResult = await handler.HandleRequestAsync(addIgnoreContext);
        PhpResult phpAddResult = Assert.IsType<PhpResult>(addResult);
        Hashtable? addData = phpAddResult.Data as Hashtable;
        Assert.NotNull(addData);
        Assert.True(addData.ContainsKey("ignored_id"));

        // 2. Remove Ignore via remove_ignored
        DefaultHttpContext removeIgnoreContext = new();
        removeIgnoreContext.Request.ContentType = "application/x-www-form-urlencoded";
        removeIgnoreContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            { "f", "remove_ignored" },
            { "cookie", "sess_ignore" },
            { "ignored_id", a2.ID.ToString(CultureInfo.InvariantCulture) }
        });

        IActionResult removeResult = await handler.HandleRequestAsync(removeIgnoreContext);
        PhpResult phpRemoveResult = Assert.IsType<PhpResult>(removeResult);
        Hashtable? removeData = phpRemoveResult.Data as Hashtable;
        Assert.NotNull(removeData);

        Account? afterRemove = await accountRepo.GetByIdAsync(a1.ID);
        Assert.NotNull(afterRemove);
        Assert.DoesNotContain(afterRemove.Peers, p => p.PeerAccountID == a2.ID && p.Type == PeerType.Ignored);
    }
}

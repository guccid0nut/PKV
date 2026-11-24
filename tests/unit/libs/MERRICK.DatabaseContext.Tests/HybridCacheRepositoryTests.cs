// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Caching;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Domain;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public sealed class HybridCacheRepositoryTests
{
    private sealed class FakeNexusHybridCache : INexusHybridCache
    {
        public List<string> RequestedKeys { get; } = [];
        public List<string> RemovedTags { get; } = [];
        public Dictionary<string, IReadOnlyCollection<string>?> KeyTags { get; } = [];

        public Task<T?> GetOrCreateAsync<T>(
            string key,
            Func<CancellationToken, Task<T?>> factory,
            TimeSpan? l1Expiration = null,
            TimeSpan? l2Expiration = null,
            CancellationToken cancellationToken = default)
        {
            return GetOrCreateAsync(key, factory, null, l1Expiration, l2Expiration, cancellationToken);
        }

        public async Task<T?> GetOrCreateAsync<T>(
            string key,
            Func<CancellationToken, Task<T?>> factory,
            IReadOnlyCollection<string>? tags,
            TimeSpan? l1Expiration = null,
            TimeSpan? l2Expiration = null,
            CancellationToken cancellationToken = default)
        {
            RequestedKeys.Add(key);
            KeyTags[key] = tags;
            return await factory(cancellationToken);
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
        {
            RemovedTags.Add(tag);
            return Task.CompletedTask;
        }

        public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
        {
            RemovedTags.AddRange(tags);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task StoreRepository_GetStoreCatalogAsync_UsesHybridCache()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        db.StoreItems.Add(new StoreItem
        {
            Id = 1,
            Name = "Gold Shield",
            Code = "item_shield",
            LocalContent = "item_shield",
            Price = 100,
            Enabled = true,
            Purchasable = true
        });
        await db.SaveChangesAsync();

        FakeNexusHybridCache fakeCache = new();
        StoreRepository repo = new(db, NullLogger<StoreRepository>.Instance, fakeCache);

        IReadOnlyList<StoreItem> items = await repo.GetStoreCatalogAsync();

        items.Should().HaveCount(1);
        fakeCache.RequestedKeys.Should().Contain("store:catalog:all");
        fakeCache.KeyTags["store:catalog:all"].Should().Contain("store:catalog");
    }

    [Fact]
    public async Task StoreRepository_GetProductByCodeAsync_UsesHybridCache()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        db.StoreItems.Add(new StoreItem
        {
            Id = 2,
            Name = "Silver Sword",
            Code = "item_sword",
            LocalContent = "item_sword",
            Price = 200,
            Enabled = true,
            Purchasable = true
        });
        await db.SaveChangesAsync();

        FakeNexusHybridCache fakeCache = new();
        StoreRepository repo = new(db, NullLogger<StoreRepository>.Instance, fakeCache);

        StoreItem? item = await repo.GetProductByCodeAsync("item_sword");

        item.Should().NotBeNull();
        item!.Code.Should().Be("item_sword");
        fakeCache.RequestedKeys.Should().Contain("store:product:code:item_sword");
        fakeCache.KeyTags["store:product:code:item_sword"].Should().Contain("store:catalog");
    }

    [Fact]
    public async Task ClanRepository_GetClanMembersAsync_UsesHybridCacheWithTag()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Clan clan = new() { ID = 10, Name = "Test Clan", Tag = "TEST", TimestampCreated = TimeProvider.System.GetUtcNow() };
        db.Clans.Add(clan);
        await db.SaveChangesAsync();

        FakeNexusHybridCache fakeCache = new();
        ClanRepository repo = new(db, NullLogger<ClanRepository>.Instance, fakeCache);

        IReadOnlyList<Account> members = await repo.GetClanMembersAsync(10);

        members.Should().BeEmpty();
        fakeCache.RequestedKeys.Should().Contain("clan:roster:10");
        fakeCache.KeyTags["clan:roster:10"].Should().Contain("clan:10");
    }

    [Fact]
    public async Task ClanRepository_AddAndRemoveMemberAsync_InvalidatesClanTag()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Clan clan = new() { ID = 20, Name = "Alpha Clan", Tag = "ALPH", TimestampCreated = TimeProvider.System.GetUtcNow() };
        User user = new()
        {
            ID = 100,
            EmailAddress = "clanuser@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { ID = 1, Name = "User" }
        };
        Account account = new() { ID = 200, UserID = 100, User = user, Name = "ClanMember1" };
        db.Clans.Add(clan);
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        FakeNexusHybridCache fakeCache = new();
        ClanRepository repo = new(db, NullLogger<ClanRepository>.Instance, fakeCache);

        // Act: Add Member
        bool addResult = await repo.AddMemberAsync(account.ID, clan.ID, ClanTier.Member);
        addResult.Should().BeTrue();

        fakeCache.RemovedTags.Should().ContainSingle(t => t == "clan:20");

        // Act: Remove Member
        bool removeResult = await repo.RemoveMemberAsync(account.ID, clan.ID);
        removeResult.Should().BeTrue();

        fakeCache.RemovedTags.Count(t => t == "clan:20").Should().Be(2);
    }
}

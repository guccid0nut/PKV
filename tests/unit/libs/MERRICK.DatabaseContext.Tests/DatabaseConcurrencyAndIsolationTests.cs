// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class DatabaseConcurrencyAndIsolationTests
{
    private static User CreateUser(int id, string email) => new()
    {
        ID = id,
        EmailAddress = email,
        PBKDF2PasswordHash = "pbkdf2_hash",
        SRPPasswordHash = "srp_hash",
        SRPPasswordSalt = "srp_salt",
        Role = new Role { ID = 1, Name = "User" }
    };

    private static Account CreateAccount(int accountId, int userId, User user, string name) => new()
    {
        ID = accountId,
        UserID = userId,
        User = user,
        Name = name
    };

    #region 1. TestDbContextFactory Multi-Instance Strict Isolation

    [Fact]
    public async Task InMemoryContextFactory_DefaultInstances_AreCompletelyIsolated()
    {
        // Two independent contexts created with default parameters (unique dbNames)
        using MerrickContext db1 = TestDbContextFactory.CreateInMemoryContext();
        using MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext();

        User user1 = CreateUser(1, "user1@kongor.net");
        Account acc1 = CreateAccount(100, 1, user1, "AlphaWarrior");
        acc1.AddIPAddress("192.168.1.100");
        acc1.AddMACAddress("00:11:22:33:44:55");
        acc1.AddBlockedPhrase("toxic_word");
        acc1.AddSelectedStoreItem("item_sword");

        db1.Users.Add(user1);
        db1.Accounts.Add(acc1);
        await db1.SaveChangesAsync();

        // db2 must be completely empty and unaware of db1's entities
        List<Account> db2Accounts = await db2.Accounts.ToListAsync();
        List<AccountIpAddress> db2Ips = await db2.AccountIpAddresses.ToListAsync();
        List<AccountMacAddress> db2Macs = await db2.AccountMacAddresses.ToListAsync();
        List<AccountBlockedPhrase> db2Phrases = await db2.AccountBlockedPhrases.ToListAsync();
        List<AccountSelectedStoreItem> db2StoreItems = await db2.AccountSelectedStoreItems.ToListAsync();

        db2Accounts.Should().BeEmpty();
        db2Ips.Should().BeEmpty();
        db2Macs.Should().BeEmpty();
        db2Phrases.Should().BeEmpty();
        db2StoreItems.Should().BeEmpty();
    }

    [Fact]
    public async Task InMemoryContextFactory_HighConcurrencyParallelInstances_ZeroCollision()
    {
        const int concurrentTasks = 30;

        Task[] tasks = Enumerable.Range(1, concurrentTasks).Select(async i =>
        {
            // Each task gets its own in-memory database instance
            using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

            // Deliberately use IDENTICAL IDs across all parallel databases to challenge collision resistance
            User user = CreateUser(1, $"shared_email_{i}@kongor.net");
            Account acc = CreateAccount(1, 1, user, $"CollisionTest_{i}");
            acc.AddIPAddress($"10.0.0.{i}");
            acc.AddMACAddress($"AA:BB:CC:DD:EE:{i:X2}");
            acc.AddBlockedPhrase($"phrase_{i}");
            acc.AddSelectedStoreItem($"store_item_{i}");

            db.Users.Add(user);
            db.Accounts.Add(acc);
            await db.SaveChangesAsync();

            // Read back immediately in same database
            Account? loaded = await db.Accounts
                .Include(a => a.IPAddressCollection)
                .Include(a => a.MACAddressCollection)
                .Include(a => a.BlockedPhrases)
                .Include(a => a.SelectedStoreItems)
                .FirstOrDefaultAsync(a => a.ID == 1);

            loaded.Should().NotBeNull();
            loaded!.Name.Should().Be($"CollisionTest_{i}");
            loaded.IPAddressCollection.Should().ContainSingle(ip => ip.IPAddress == $"10.0.0.{i}");
            loaded.MACAddressCollection.Should().ContainSingle(mac => mac.MACAddress == $"AA:BB:CC:DD:EE:{i:X2}");
            loaded.BlockedPhrases.Should().ContainSingle(p => p.Phrase == $"phrase_{i}");
            loaded.SelectedStoreItems.Should().ContainSingle(s => s.ItemCode == $"store_item_{i}");

            // Verify total row counts in this DB are strictly 1 for each table
            (await db.Accounts.CountAsync()).Should().Be(1);
            (await db.AccountIpAddresses.CountAsync()).Should().Be(1);
            (await db.AccountMacAddresses.CountAsync()).Should().Be(1);
            (await db.AccountBlockedPhrases.CountAsync()).Should().Be(1);
            (await db.AccountSelectedStoreItems.CountAsync()).Should().Be(1);
        }).ToArray();

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task InMemoryContextFactory_SharedDatabaseName_SynchronizesChangesAcrossContexts()
    {
        string sharedDb = Guid.NewGuid().ToString("N");

        using (MerrickContext db1 = TestDbContextFactory.CreateInMemoryContext(sharedDb))
        {
            User u = CreateUser(1, "shared@kongor.net");
            Account acc = CreateAccount(10, 1, u, "SharedAccount");
            db1.Users.Add(u);
            db1.Accounts.Add(acc);
            await db1.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(sharedDb))
        {
            Account? acc = await db2.Accounts.FirstOrDefaultAsync(a => a.ID == 10);
            acc.Should().NotBeNull();
            acc!.Name.Should().Be("SharedAccount");

            acc.AddIPAddress("172.16.1.1");
            await db2.SaveChangesAsync();
        }

        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(sharedDb))
        {
            AccountIpAddress? ip = await db3.AccountIpAddresses.FirstOrDefaultAsync(i => i.AccountID == 10);
            ip.Should().NotBeNull();
            ip!.IPAddress.Should().Be("172.16.1.1");
        }
    }

    #endregion

    #region 2. Multi-Account Concurrent Mutations & Zero Cross-Contamination

    [Fact]
    public async Task MultiAccount_ConcurrentParallelMutations_ZeroCrossContamination()
    {
        string sharedDb = Guid.NewGuid().ToString("N");
        const int accountCount = 10;

        // Seed initial accounts in a single DB
        using (MerrickContext seedDb = TestDbContextFactory.CreateInMemoryContext(sharedDb))
        {
            User user = CreateUser(1, "shared_master@kongor.net");
            seedDb.Users.Add(user);

            for (int i = 1; i <= accountCount; i++)
            {
                seedDb.Accounts.Add(CreateAccount(i, 1, user, $"Account_{i}"));
            }
            await seedDb.SaveChangesAsync();
        }

        // Concurrently mutate each account from distinct context instances targeting the same database
        Task[] mutationTasks = Enumerable.Range(1, accountCount).Select(async accId =>
        {
            using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(sharedDb);

            Account? acc = await db.Accounts
                .Include(a => a.IPAddressCollection)
                .Include(a => a.MACAddressCollection)
                .Include(a => a.BlockedPhrases)
                .Include(a => a.SelectedStoreItems)
                .FirstOrDefaultAsync(a => a.ID == accId);

            acc.Should().NotBeNull();

            // Add accId number of entries to each child collection
            for (int k = 1; k <= accId; k++)
            {
                acc!.AddIPAddress($"10.{accId}.0.{k}");
                acc.AddMACAddress($"00:AA:{accId:X2}:00:00:{k:X2}");
                acc.AddBlockedPhrase($"phrase_acc_{accId}_num_{k}");
                acc.AddSelectedStoreItem($"item_acc_{accId}_code_{k}");
            }

            await db.SaveChangesAsync();
        }).ToArray();

        await Task.WhenAll(mutationTasks);

        // Sequentially verify strict multi-account isolation across all child entities
        using (MerrickContext verifyDb = TestDbContextFactory.CreateInMemoryContext(sharedDb))
        {
            for (int accId = 1; accId <= accountCount; accId++)
            {
                Account? acc = await verifyDb.Accounts
                    .Include(a => a.IPAddressCollection)
                    .Include(a => a.MACAddressCollection)
                    .Include(a => a.BlockedPhrases)
                    .Include(a => a.SelectedStoreItems)
                    .FirstOrDefaultAsync(a => a.ID == accId);

                acc.Should().NotBeNull();
                acc!.IPAddressCollection.Should().HaveCount(accId);
                acc.MACAddressCollection.Should().HaveCount(accId);
                acc.BlockedPhrases.Should().HaveCount(accId);
                acc.SelectedStoreItems.Should().HaveCount(accId);

                // Ensure NO records from other accounts leaked in
                acc.IPAddressCollection.Should().OnlyContain(ip => ip.AccountID == accId && ip.IPAddress.StartsWith($"10.{accId}.0."));
                acc.MACAddressCollection.Should().OnlyContain(mac => mac.AccountID == accId && mac.MACAddress.StartsWith($"00:AA:{accId:X2}:"));
                acc.BlockedPhrases.Should().OnlyContain(bp => bp.AccountID == accId && bp.Phrase.StartsWith($"phrase_acc_{accId}_"));
                acc.SelectedStoreItems.Should().OnlyContain(s => s.AccountID == accId && s.ItemCode.StartsWith($"item_acc_{accId}_"));
            }

            // Verify total row counts equal the sum(1..10) = 55
            const int expectedTotal = 55;
            (await verifyDb.AccountIpAddresses.CountAsync()).Should().Be(expectedTotal);
            (await verifyDb.AccountMacAddresses.CountAsync()).Should().Be(expectedTotal);
            (await verifyDb.AccountBlockedPhrases.CountAsync()).Should().Be(expectedTotal);
            (await verifyDb.AccountSelectedStoreItems.CountAsync()).Should().Be(expectedTotal);
        }
    }

    [Fact]
    public async Task MultiAccount_CascadeDeletionOfOneAccount_LeavesPeersCompletelyIntact()
    {
        string sharedDb = Guid.NewGuid().ToString("N");

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(sharedDb))
        {
            User user = CreateUser(1, "cascade_user@kongor.net");
            Account acc1 = CreateAccount(101, 1, user, "Acc101");
            Account acc2 = CreateAccount(102, 1, user, "Acc102");
            Account acc3 = CreateAccount(103, 1, user, "Acc103");

            acc1.AddIPAddress("192.168.1.101");
            acc1.AddMACAddress("11:11:11:11:11:11");
            acc1.AddBlockedPhrase("block_101");
            acc1.AddSelectedStoreItem("item_101");

            acc2.AddIPAddress("192.168.1.102");
            acc2.AddMACAddress("22:22:22:22:22:22");
            acc2.AddBlockedPhrase("block_102");
            acc2.AddSelectedStoreItem("item_102");

            acc3.AddIPAddress("192.168.1.103");
            acc3.AddMACAddress("33:33:33:33:33:33");
            acc3.AddBlockedPhrase("block_103");
            acc3.AddSelectedStoreItem("item_103");

            db.Users.Add(user);
            db.Accounts.AddRange(acc1, acc2, acc3);
            await db.SaveChangesAsync();
        }

        // Delete Account 102
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(sharedDb))
        {
            Account acc2 = await db.Accounts
                .Include(a => a.IPAddressCollection)
                .Include(a => a.MACAddressCollection)
                .Include(a => a.BlockedPhrases)
                .Include(a => a.SelectedStoreItems)
                .FirstAsync(a => a.ID == 102);

            db.Accounts.Remove(acc2);
            await db.SaveChangesAsync();
        }

        // Verify Account 101 and 103 are completely intact, and 102 child rows are gone
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(sharedDb))
        {
            (await db.Accounts.FindAsync(102)).Should().BeNull();
            (await db.AccountIpAddresses.AnyAsync(i => i.AccountID == 102)).Should().BeFalse();
            (await db.AccountMacAddresses.AnyAsync(m => m.AccountID == 102)).Should().BeFalse();
            (await db.AccountBlockedPhrases.AnyAsync(p => p.AccountID == 102)).Should().BeFalse();
            (await db.AccountSelectedStoreItems.AnyAsync(s => s.AccountID == 102)).Should().BeFalse();

            // Peers are still 100% intact
            Account acc1 = await db.Accounts
                .Include(a => a.IPAddressCollection)
                .Include(a => a.MACAddressCollection)
                .Include(a => a.BlockedPhrases)
                .Include(a => a.SelectedStoreItems)
                .FirstAsync(a => a.ID == 101);

            acc1.IPAddressCollection.Should().ContainSingle(i => i.IPAddress == "192.168.1.101");
            acc1.MACAddressCollection.Should().ContainSingle(m => m.MACAddress == "11:11:11:11:11:11");
            acc1.BlockedPhrases.Should().ContainSingle(p => p.Phrase == "block_101");
            acc1.SelectedStoreItems.Should().ContainSingle(s => s.ItemCode == "item_101");

            Account acc3 = await db.Accounts
                .Include(a => a.IPAddressCollection)
                .Include(a => a.MACAddressCollection)
                .Include(a => a.BlockedPhrases)
                .Include(a => a.SelectedStoreItems)
                .FirstAsync(a => a.ID == 103);

            acc3.IPAddressCollection.Should().ContainSingle(i => i.IPAddress == "192.168.1.103");
            acc3.MACAddressCollection.Should().ContainSingle(m => m.MACAddress == "33:33:33:33:33:33");
            acc3.BlockedPhrases.Should().ContainSingle(p => p.Phrase == "block_103");
            acc3.SelectedStoreItems.Should().ContainSingle(s => s.ItemCode == "item_103");
        }
    }

    #endregion

    #region 3. AccountRepository Boundary Values & Null/Empty Robustness

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-9999)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public async Task AccountRepository_GetByIdAsync_InvalidOrNonExistentIds_ReturnsNullSafely(int invalidId)
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        AccountRepository repo = new(db);

        Account? result = await repo.GetByIdAsync(invalidId);
        result.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task AccountRepository_GetByNicknameAsync_NullOrWhitespace_ReturnsNullSafely(string? invalidNickname)
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        AccountRepository repo = new(db);

        Account? result = await repo.GetByNicknameAsync(invalidNickname!);
        result.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AccountRepository_ExistsByNicknameAsync_NullOrWhitespace_ReturnsFalseSafely(string? invalidNickname)
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        AccountRepository repo = new(db);

        bool exists = await repo.ExistsByNicknameAsync(invalidNickname!);
        exists.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AccountRepository_ExistsByEmailAsync_NullOrWhitespace_ReturnsFalseSafely(string? invalidEmail)
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        AccountRepository repo = new(db);

        bool exists = await repo.ExistsByEmailAsync(invalidEmail!);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task AccountRepository_RecordAccountTelemetryAndSession_HandlesNullAndDuplicatesGracefully()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(1, "telem@kongor.net");
            Account acc = CreateAccount(50, 1, user, "TelemTester");
            db.Users.Add(user);
            db.Accounts.Add(acc);
            await db.SaveChangesAsync();
        }

        // 1. Record session with valid IP and MAC
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository repo = new(db);
            AccountSession session1 = new()
            {
                ID = 1,
                AccountID = 50,
                SessionCookie = "session_1",
                IPAddress = "192.168.1.50"
            };

            await repo.RecordAccountTelemetryAndSessionAsync(
                50, session1, "192.168.1.50", "AA:BB:CC:DD:EE:FF", "SysInfo1", "Hash1");
        }

        // 2. Record duplicate IP/MAC and null values on second session -> should NOT duplicate or crash
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository repo = new(db);
            AccountSession session2 = new()
            {
                ID = 2,
                AccountID = 50,
                SessionCookie = "session_2",
                IPAddress = "192.168.1.50"
            };

            await repo.RecordAccountTelemetryAndSessionAsync(
                50, session2, "192.168.1.50", "AA:BB:CC:DD:EE:FF", null, null);
        }

        // 3. Record with null IP and MAC strings -> safely ignored
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository repo = new(db);
            AccountSession session3 = new()
            {
                ID = 3,
                AccountID = 50,
                SessionCookie = "session_3",
                IPAddress = "0.0.0.0"
            };

            await repo.RecordAccountTelemetryAndSessionAsync(
                50, session3, null, "", "   ", null);
        }

        // Verify account has exactly 1 IP and 1 MAC record
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account? acc = await db.Accounts
                .Include(a => a.IPAddressCollection)
                .Include(a => a.MACAddressCollection)
                .FirstOrDefaultAsync(a => a.ID == 50);

            acc.Should().NotBeNull();
            acc!.IPAddressCollection.Should().HaveCount(1);
            acc.IPAddressCollection[0].IPAddress.Should().Be("192.168.1.50");
            acc.MACAddressCollection.Should().HaveCount(1);
            acc.MACAddressCollection[0].MACAddress.Should().Be("AA:BB:CC:DD:EE:FF");
        }
    }

    [Fact]
    public async Task AccountRepository_UpdateAsync_DisconnectedEntityReconciliation()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(1, "reconcile@kongor.net");
            Account acc = CreateAccount(77, 1, user, "ReconcileWarrior");
            acc.AddSelectedStoreItem("taunt_kongor");
            acc.AddSelectedStoreItem("icon_kongor");
            db.Users.Add(user);
            db.Accounts.Add(acc);
            await db.SaveChangesAsync();
        }

        // Simulate disconnected entity where store items are mutated (removed taunt_kongor, added color_gold)
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository repo = new(db);

            // Load tracked entity in repo context
            Account tracked = await db.Accounts
                .Include(a => a.SelectedStoreItems)
                .FirstAsync(a => a.ID == 77);

            // Create disconnected clone with new desired store items
            Account disconnected = new()
            {
                ID = 77,
                Name = "ReconcileWarrior",
                UserID = 1,
                User = tracked.User,
                SelectedStoreItems =
                [
                    new AccountSelectedStoreItem { AccountID = 77, ItemCode = "icon_kongor" },
                    new AccountSelectedStoreItem { AccountID = 77, ItemCode = "color_gold" }
                ]
            };

            await repo.UpdateAsync(disconnected);
        }

        // Verify persisted state matches reconciled target
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account acc = await db.Accounts
                .Include(a => a.SelectedStoreItems)
                .FirstAsync(a => a.ID == 77);

            acc.SelectedStoreItems.Should().HaveCount(2);
            acc.SelectedStoreItemCodes.Should().BeEquivalentTo(new[] { "icon_kongor", "color_gold" });
            acc.HasSelectedStoreItem("taunt_kongor").Should().BeFalse();
        }
    }

    #endregion

    #region 4. InventoryRepository Boundary Values & Concurrency

    [Fact]
    public async Task InventoryRepository_GetSelectedStoreItemsAsync_NonExistentAccount_ReturnsEmptyList()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        InventoryRepository repo = new(db);

        IReadOnlyList<string> items = await repo.GetSelectedStoreItemsAsync(99999);
        items.Should().NotBeNull();
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task InventoryRepository_SetSelectedStoreItemsAsync_NonExistentAccount_SafelyNoOps()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        InventoryRepository repo = new(db);

        // Setting items for an account that does not exist should safely complete without throwing
        Func<Task> act = async () => await repo.SetSelectedStoreItemsAsync(99999, ["item_a", "item_b"]);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task InventoryRepository_SetSelectedStoreItemsAsync_ClearsAndDeduplicates()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(1, "inv_test@kongor.net");
            Account acc = CreateAccount(88, 1, user, "InventoryPlayer");
            acc.AddSelectedStoreItem("old_item_1");
            acc.AddSelectedStoreItem("old_item_2");
            db.Users.Add(user);
            db.Accounts.Add(acc);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            InventoryRepository repo = new(db);

            // Pass list with duplicates, mixed case, and whitespace
            await repo.SetSelectedStoreItemsAsync(88, ["item_new", "ITEM_NEW", "  item_new  ", "item_unique", "   "]);

            IReadOnlyList<string> selected = await repo.GetSelectedStoreItemsAsync(88);
            selected.Should().HaveCount(2);
            selected.Should().Contain("item_new");
            selected.Should().Contain("item_unique");

            // Empty list should clear all items
            await repo.SetSelectedStoreItemsAsync(88, []);
            IReadOnlyList<string> cleared = await repo.GetSelectedStoreItemsAsync(88);
            cleared.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task InventoryRepository_ConcurrentEquipAcrossAccounts_StrictlyIsolated()
    {
        string sharedDb = Guid.NewGuid().ToString("N");
        const int accountsCount = 5;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(sharedDb))
        {
            User user = CreateUser(1, "equip_user@kongor.net");
            db.Users.Add(user);

            for (int i = 1; i <= accountsCount; i++)
            {
                db.Accounts.Add(CreateAccount(i, 1, user, $"EquipPlayer_{i}"));
            }
            await db.SaveChangesAsync();
        }

        // Concurrently equip distinct store items per account
        Task[] tasks = Enumerable.Range(1, accountsCount).Select(async i =>
        {
            using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(sharedDb);
            InventoryRepository repo = new(db);

            await repo.SetSelectedStoreItemsAsync(i, [$"equip_code_{i}"]);
        }).ToArray();

        await Task.WhenAll(tasks);

        // Verify each account has strictly its own equipped item
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(sharedDb))
        {
            InventoryRepository repo = new(db);

            for (int i = 1; i <= accountsCount; i++)
            {
                IReadOnlyList<string> items = await repo.GetSelectedStoreItemsAsync(i);
                items.Should().ContainSingle(code => code == $"equip_code_{i}");
            }
        }
    }

    #endregion

    #region 5. Entity Domain Helper Boundary Values & Value Equality

    [Fact]
    public void Account_ChildEntities_EqualityAndImplicitConversions()
    {
        // AccountIpAddress
        AccountIpAddress ip1 = new() { AccountID = 1, IPAddress = "192.168.1.1" };
        AccountIpAddress ip2 = new() { AccountID = 2, IPAddress = "192.168.1.1" }; // same IP, different account
        AccountIpAddress ip3 = new() { AccountID = 1, IPAddress = "10.0.0.1" };

        ip1.Equals(ip2).Should().BeTrue();
        ip1.Equals("192.168.1.1").Should().BeTrue();
        ip1.Equals(ip3).Should().BeFalse();
        string ipStr = ip1;
        ipStr.Should().Be("192.168.1.1");
        ip1.ToString().Should().Be("192.168.1.1");
        AccountIpAddress ipFromStr = "127.0.0.1";
        ipFromStr.IPAddress.Should().Be("127.0.0.1");

        // AccountMacAddress
        AccountMacAddress mac1 = new() { AccountID = 1, MACAddress = "AA:BB:CC:DD:EE:FF" };
        AccountMacAddress mac2 = new() { AccountID = 1, MACAddress = "aa:bb:cc:dd:ee:ff" }; // case-insensitive equality
        mac1.Equals(mac2).Should().BeTrue();
        mac1.Equals("AA:BB:CC:DD:EE:FF").Should().BeTrue();
        string macStr = mac1;
        macStr.Should().Be("AA:BB:CC:DD:EE:FF");

        // AccountBlockedPhrase
        AccountBlockedPhrase bp1 = new() { AccountID = 1, Phrase = "SpamWord" };
        AccountBlockedPhrase bp2 = new() { AccountID = 1, Phrase = "spamword" };
        bp1.Equals(bp2).Should().BeTrue();
        string bpStr = bp1;
        bpStr.Should().Be("SpamWord");

        // AccountSelectedStoreItem
        AccountSelectedStoreItem item1 = new() { AccountID = 1, ItemCode = "ai.custom_icon:1" };
        AccountSelectedStoreItem item2 = new() { AccountID = 1, ItemCode = "AI.CUSTOM_ICON:1" };
        item1.Equals(item2).Should().BeTrue();
        string itemStr = item1;
        itemStr.Should().Be("ai.custom_icon:1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Account_DomainHelpers_NullOrWhitespaceInputs_HandledSafely(string? invalidInput)
    {
        Account acc = new()
        {
            ID = 10,
            Name = "EdgeTester",
            User = null!
        };

        acc.AddIPAddress(invalidInput!).Should().BeFalse();
        acc.HasIPAddress(invalidInput!).Should().BeFalse();
        acc.IPAddressCollection.Should().BeEmpty();

        acc.AddMACAddress(invalidInput!).Should().BeFalse();
        acc.HasMACAddress(invalidInput!).Should().BeFalse();
        acc.MACAddressCollection.Should().BeEmpty();

        acc.AddBlockedPhrase(invalidInput!).Should().BeFalse();
        acc.HasBlockedPhrase(invalidInput!).Should().BeFalse();
        acc.RemoveBlockedPhrase(invalidInput!).Should().BeFalse();
        acc.BlockedPhrases.Should().BeEmpty();

        acc.AddSelectedStoreItem(invalidInput!);
        acc.HasSelectedStoreItem(invalidInput!).Should().BeFalse();
        acc.RemoveSelectedStoreItem(invalidInput!).Should().BeFalse();
        acc.SelectedStoreItems.Should().BeEmpty();
    }

    [Fact]
    public void Account_DomainHelpers_CaseInsensitiveDuplicates_RejectedSafely()
    {
        Account acc = new()
        {
            ID = 20,
            Name = "CaseTester",
            User = null!
        };

        // IP duplicates
        acc.AddIPAddress("192.168.1.1").Should().BeTrue();
        acc.AddIPAddress("192.168.1.1").Should().BeFalse();
        acc.IPAddressCollection.Should().HaveCount(1);

        // MAC duplicates
        acc.AddMACAddress("AA:BB:CC:DD:EE:FF").Should().BeTrue();
        acc.AddMACAddress("aa:bb:cc:dd:ee:ff").Should().BeFalse();
        acc.MACAddressCollection.Should().HaveCount(1);

        // Blocked phrases duplicates
        acc.AddBlockedPhrase("Toxicity").Should().BeTrue();
        acc.AddBlockedPhrase("toxicity").Should().BeFalse();
        acc.BlockedPhrases.Should().HaveCount(1);
        acc.HasBlockedPhrase("TOXICITY").Should().BeTrue();

        // Selected store items duplicates
        acc.AddSelectedStoreItem("item.flint_skin");
        acc.AddSelectedStoreItem("ITEM.FLINT_SKIN");
        acc.SelectedStoreItems.Should().HaveCount(1);
        acc.HasSelectedStoreItem("ITEM.FLINT_SKIN").Should().BeTrue();
    }

    #endregion
}

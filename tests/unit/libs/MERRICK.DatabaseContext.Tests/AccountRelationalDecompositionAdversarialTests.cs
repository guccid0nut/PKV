// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Services;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

/// <summary>
/// Empirical stress-test and challenge harness for Milestone 1 (Account Entity 1NF Relational Decomposition).
/// Verifies unique constraints, rapid child additions/removals, cascade deletions, high volume,
/// boundary conditions, and repository integration.
/// </summary>
public class AccountRelationalDecompositionAdversarialTests
{
    private static async Task<(Account Account, MerrickContext Context, string DbName)> CreateTestAccountWithUserAsync(int accountId = 10, string accountName = "ChallengerWarrior")
    {
        string dbName = Guid.NewGuid().ToString("N");
        MerrickContext context = TestDbContextFactory.CreateInMemoryContext(dbName);

        User user = new()
        {
            ID = accountId,
            EmailAddress = $"{accountName.ToLowerInvariant()}@kongor.net",
            PBKDF2PasswordHash = "hash123",
            SRPPasswordHash = "srp_hash",
            SRPPasswordSalt = "srp_salt",
            Role = new Role { ID = 1, Name = "User" }
        };

        Account account = new()
        {
            ID = accountId,
            UserID = user.ID,
            User = user,
            Name = accountName
        };

        context.Users.Add(user);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        return (account, context, dbName);
    }

    // =========================================================================
    // Challenge 1: Unique Constraints & Duplicate Rejection
    // =========================================================================

    [Fact]
    public void UniqueConstraints_DomainDeduplication_ExactAndCaseRejection_EnforcesUniqueness()
    {
        Account account = new() { ID = 1, Name = "DedupeTester", User = null! };

        // 1. IP Addresses (duplicate exact & whitespace)
        account.AddIPAddress("192.168.1.100").Should().BeTrue();
        account.AddIPAddress("192.168.1.100").Should().BeFalse("Duplicate exact IP must be rejected");
        account.AddIPAddress(" 192.168.1.100 ").Should().BeFalse("Duplicate IP with whitespace must be rejected");
        account.IPAddressCollection.Should().HaveCount(1);

        // 2. MAC Addresses (case-insensitivity & whitespace)
        account.AddMACAddress("00-1B-44-11-3A-B7").Should().BeTrue();
        account.AddMACAddress("00-1b-44-11-3a-b7").Should().BeFalse("Lowercase duplicate MAC must be rejected");
        account.AddMACAddress(" 00-1B-44-11-3A-B7 ").Should().BeFalse("Whitespace padded MAC must be rejected");
        account.MACAddressCollection.Should().HaveCount(1);

        // 3. Blocked Phrases (case-insensitivity & whitespace)
        account.AddBlockedPhrase("toxic_troll").Should().BeTrue();
        account.AddBlockedPhrase("TOXIC_TROLL").Should().BeFalse("Uppercase duplicate blocked phrase must be rejected");
        account.AddBlockedPhrase("  toxic_troll  ").Should().BeFalse("Whitespace padded blocked phrase must be rejected");
        account.BlockedPhrases.Should().HaveCount(1);

        // 4. Selected Store Items (exact duplicate)
        account.AddSelectedStoreItem("ai.custom_icon:1");
        account.AddSelectedStoreItem("AI.CUSTOM_ICON:1"); // duplicate should be ignored
        account.SelectedStoreItems.Should().HaveCount(1);

        account.SetSelectedStoreItems(["cc.red", "CC.RED", "t.taunt"]);
        account.SelectedStoreItems.Should().HaveCount(2, "SetSelectedStoreItems must deduplicate case-insensitively for exact strings");
    }

    [Fact]
    public void Bug1_SetSelectedStoreItems_UntrimmedWhitespaceDuplicates_ViolatesUniqueness()
    {
        Account account = new() { ID = 2, Name = "WhitespaceDedupeBug", User = null! };

        // Calling SetSelectedStoreItems with whitespace-padded duplicates: "t.taunt" and "  t.taunt  "
        // EXPECTED: Collection should contain exactly 1 trimmed item "t.taunt"
        // ACTUAL BUG: Distinct(OrdinalIgnoreCase) runs before Trim(), allowing both through and producing duplicate items
        account.SetSelectedStoreItems(["t.taunt", "  t.taunt  "]);

        account.SelectedStoreItems.Should().HaveCount(1,
            "SetSelectedStoreItems must trim strings before deduplicating, otherwise duplicates enter the collection and violate unique constraints");
    }

    [Fact]
    public async Task UniqueConstraints_CrossAccountIsolation_AllowsSameKeyOnDifferentAccounts()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = new()
            {
                ID = 1,
                EmailAddress = "shared@kongor.net",
                PBKDF2PasswordHash = "h",
                SRPPasswordHash = "s",
                SRPPasswordSalt = "salt",
                Role = new Role { ID = 1, Name = "User" }
            };
            Account acc1 = new() { ID = 1, UserID = 1, User = user, Name = "PlayerOne" };
            Account acc2 = new() { ID = 2, UserID = 1, User = user, Name = "PlayerTwo" };

            db.Users.Add(user);
            db.Accounts.AddRange(acc1, acc2);

            // Same IP, MAC, Phrase, and ItemCode on two different accounts
            db.AccountIpAddresses.AddRange(
                new AccountIpAddress { AccountID = 1, IPAddress = "192.168.1.1" },
                new AccountIpAddress { AccountID = 2, IPAddress = "192.168.1.1" }
            );

            db.AccountMacAddresses.AddRange(
                new AccountMacAddress { AccountID = 1, MACAddress = "AA-BB-CC-DD-EE-FF" },
                new AccountMacAddress { AccountID = 2, MACAddress = "AA-BB-CC-DD-EE-FF" }
            );

            db.AccountBlockedPhrases.AddRange(
                new AccountBlockedPhrase { AccountID = 1, Phrase = "toxic_slur" },
                new AccountBlockedPhrase { AccountID = 2, Phrase = "toxic_slur" }
            );

            db.AccountSelectedStoreItems.AddRange(
                new AccountSelectedStoreItem { AccountID = 1, ItemCode = "ai.shared_icon" },
                new AccountSelectedStoreItem { AccountID = 2, ItemCode = "ai.shared_icon" }
            );

            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db2.AccountIpAddresses.CountAsync(i => i.IPAddress == "192.168.1.1")).Should().Be(2);
            (await db2.AccountMacAddresses.CountAsync(m => m.MACAddress == "AA-BB-CC-DD-EE-FF")).Should().Be(2);
            (await db2.AccountBlockedPhrases.CountAsync(p => p.Phrase == "toxic_slur")).Should().Be(2);
            (await db2.AccountSelectedStoreItems.CountAsync(s => s.ItemCode == "ai.shared_icon")).Should().Be(2);
        }
    }

    [Fact]
    public void UniqueConstraints_ModelMetadataAndDDL_VerifyCompositeUniqueConfigurations()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
        IModel model = db.Model;

        // Verify EF Core IModel unique indexes
        var expectedIndexes = new (Type EntityType, string[] Properties, string ConstraintName)[]
        {
            (typeof(AccountSelectedStoreItem), [nameof(AccountSelectedStoreItem.AccountID), nameof(AccountSelectedStoreItem.ItemCode)], "UQ_AccountSelectedStoreItems_AccountID_ItemCode"),
            (typeof(AccountIpAddress), [nameof(AccountIpAddress.AccountID), nameof(AccountIpAddress.IPAddress)], "UQ_AccountIpAddresses_AccountID_IPAddress"),
            (typeof(AccountMacAddress), [nameof(AccountMacAddress.AccountID), nameof(AccountMacAddress.MACAddress)], "UQ_AccountMacAddresses_AccountID_MACAddress"),
            (typeof(AccountBlockedPhrase), [nameof(AccountBlockedPhrase.AccountID), nameof(AccountBlockedPhrase.Phrase)], "UQ_AccountBlockedPhrases_AccountID_Phrase"),
        };

        foreach (var (entityType, properties, constraintName) in expectedIndexes)
        {
            IEntityType entity = model.FindEntityType(entityType)!;
            entity.Should().NotBeNull($"Entity {entityType.Name} must be mapped in model");

            var index = entity.GetIndexes().FirstOrDefault(idx =>
                idx.IsUnique &&
                idx.Properties.Select(p => p.Name).SequenceEqual(properties));

            index.Should().NotBeNull($"Unique index on [{string.Join(", ", properties)}] must exist on {entityType.Name}");
            index!.GetDatabaseName().Should().Be(constraintName);
        }
    }

    // =========================================================================
    // Challenge 2: Rapid Addition & Removal Stress Test
    // =========================================================================

    [Fact]
    public void RapidAddRemove_HighFrequencyChurn_MaintainsConsistencyAndOrder()
    {
        Account account = new() { ID = 50, Name = "ChurnTester", User = null! };

        // 1. Rapidly add 200 blocked phrases
        for (int i = 0; i < 200; i++)
        {
            account.AddBlockedPhrase($"phrase_{i}").Should().BeTrue();
        }
        account.BlockedPhrases.Should().HaveCount(200);

        // 2. Remove 100 phrases in reverse order
        for (int i = 199; i >= 100; i--)
        {
            account.RemoveBlockedPhrase($"phrase_{i}").Should().BeTrue();
            account.RemoveBlockedPhrase($"phrase_{i}").Should().BeFalse("Double remove must return false");
        }
        account.BlockedPhrases.Should().HaveCount(100);

        // 3. Re-add 50 phrases with mixed casing (should succeed because they were removed)
        for (int i = 100; i < 150; i++)
        {
            account.AddBlockedPhrase($"PHRASE_{i}").Should().BeTrue();
        }
        account.BlockedPhrases.Should().HaveCount(150);

        // 4. Rapid store items churn
        for (int i = 0; i < 100; i++)
        {
            account.AddSelectedStoreItem($"item_code_{i}");
        }
        account.SelectedStoreItems.Should().HaveCount(100);

        for (int i = 0; i < 50; i++)
        {
            account.RemoveSelectedStoreItem($"item_code_{i}").Should().BeTrue();
        }
        account.SelectedStoreItems.Should().HaveCount(50);
        account.HasSelectedStoreItem("item_code_0").Should().BeFalse();
        account.HasSelectedStoreItem("item_code_99").Should().BeTrue();
    }

    // =========================================================================
    // Challenge 3: High Volume / Boundary Limit Stress Test
    // =========================================================================

    [Fact]
    public async Task HighVolume_BulkNetworkIdentifiers_PersistsAndReloadsUnderTimeBudget()
    {
        (Account account, MerrickContext db, string dbName) = await CreateTestAccountWithUserAsync(100, "HighVolumeUser");

        Stopwatch sw = Stopwatch.StartNew();
        using (db)
        {
            // Populate 500 IPs and 500 MACs
            List<AccountIpAddress> ips = new(500);
            List<AccountMacAddress> macs = new(500);

            for (int i = 0; i < 500; i++)
            {
                ips.Add(new AccountIpAddress
                {
                    AccountID = account.ID,
                    IPAddress = $"10.{(i / 256) % 256}.{i % 256}.{(i * 3) % 256}"
                });

                macs.Add(new AccountMacAddress
                {
                    AccountID = account.ID,
                    MACAddress = $"00-1A-2B-{((i >> 8) & 0xFF):X2}-{(i & 0xFF):X2}-{(i % 99):X2}"
                });
            }

            db.AccountIpAddresses.AddRange(ips);
            db.AccountMacAddresses.AddRange(macs);
            await db.SaveChangesAsync();
        }

        // Reload in fresh context
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account loaded = await db2.Accounts
                .Include(a => a.IPAddressCollection)
                .Include(a => a.MACAddressCollection)
                .FirstAsync(a => a.ID == account.ID);

            loaded.IPAddressCollection.Should().HaveCount(500);
            loaded.MACAddressCollection.Should().HaveCount(500);
            loaded.IPAddressStrings.Should().HaveCount(500);
            loaded.MACAddressStrings.Should().HaveCount(500);
        }

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "500-item bulk persistence and reload must execute in < 2.0s");
    }

    // =========================================================================
    // Challenge 4: Deep Cascade Deletion Stress Test
    // =========================================================================

    [Fact]
    public async Task DeepCascadeDelete_MultiAccountGraph_ZeroOrphanedRowsAcrossAllTables()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = new()
            {
                ID = 500,
                EmailAddress = "cascade_test@kongor.net",
                PBKDF2PasswordHash = "h",
                SRPPasswordHash = "s",
                SRPPasswordSalt = "salt",
                Role = new Role { ID = 1, Name = "User" }
            };

            Account accA = new() { ID = 501, UserID = 500, User = user, Name = "AccountA" };
            Account accB = new() { ID = 502, UserID = 500, User = user, Name = "AccountB" };
            Account accC = new() { ID = 503, UserID = 500, User = user, Name = "AccountC" };

            db.Users.Add(user);
            db.Accounts.AddRange(accA, accB, accC);

            // Populate 50 rows per child table for each of the 3 accounts (3 * 4 * 50 = 600 child rows)
            foreach (Account acc in new[] { accA, accB, accC })
            {
                for (int i = 0; i < 50; i++)
                {
                    db.AccountIpAddresses.Add(new AccountIpAddress { AccountID = acc.ID, IPAddress = $"192.168.{acc.ID % 100}.{i}" });
                    db.AccountMacAddresses.Add(new AccountMacAddress { AccountID = acc.ID, MACAddress = $"00-AA-{acc.ID % 100:X2}-00-00-{i:X2}" });
                    db.AccountBlockedPhrases.Add(new AccountBlockedPhrase { AccountID = acc.ID, Phrase = $"curse_{acc.ID}_{i}" });
                    db.AccountSelectedStoreItems.Add(new AccountSelectedStoreItem { AccountID = acc.ID, ItemCode = $"item_{acc.ID}_{i}" });
                }
            }

            await db.SaveChangesAsync();
        }

        // Verify initial counts: 150 rows per table (total 600)
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db2.AccountIpAddresses.CountAsync()).Should().Be(150);
            (await db2.AccountMacAddresses.CountAsync()).Should().Be(150);
            (await db2.AccountBlockedPhrases.CountAsync()).Should().Be(150);
            (await db2.AccountSelectedStoreItems.CountAsync()).Should().Be(150);

            // Delete Account B
            Account b = await db2.Accounts
                .Include(a => a.IPAddressCollection)
                .Include(a => a.MACAddressCollection)
                .Include(a => a.BlockedPhrases)
                .Include(a => a.SelectedStoreItems)
                .FirstAsync(a => a.ID == 502);

            db2.Accounts.Remove(b);
            await db2.SaveChangesAsync();
        }

        // Verify zero orphans for Account B, and exactly 100 rows left per table for A & C
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db3.AccountIpAddresses.AnyAsync(i => i.AccountID == 502)).Should().BeFalse();
            (await db3.AccountMacAddresses.AnyAsync(m => m.AccountID == 502)).Should().BeFalse();
            (await db3.AccountBlockedPhrases.AnyAsync(p => p.AccountID == 502)).Should().BeFalse();
            (await db3.AccountSelectedStoreItems.AnyAsync(s => s.AccountID == 502)).Should().BeFalse();

            (await db3.AccountIpAddresses.CountAsync()).Should().Be(100);
            (await db3.AccountMacAddresses.CountAsync()).Should().Be(100);
            (await db3.AccountBlockedPhrases.CountAsync()).Should().Be(100);
            (await db3.AccountSelectedStoreItems.CountAsync()).Should().Be(100);

            // Delete Account A and C
            List<Account> remaining = await db3.Accounts
                .Include(a => a.IPAddressCollection)
                .Include(a => a.MACAddressCollection)
                .Include(a => a.BlockedPhrases)
                .Include(a => a.SelectedStoreItems)
                .ToListAsync();

            db3.Accounts.RemoveRange(remaining);
            await db3.SaveChangesAsync();
        }

        // Verify complete zeroing across all child tables
        using (MerrickContext db4 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db4.AccountIpAddresses.CountAsync()).Should().Be(0);
            (await db4.AccountMacAddresses.CountAsync()).Should().Be(0);
            (await db4.AccountBlockedPhrases.CountAsync()).Should().Be(0);
            (await db4.AccountSelectedStoreItems.CountAsync()).Should().Be(0);
        }
    }

    // =========================================================================
    // Challenge 5: Boundary Conditions & Edge Cases
    // =========================================================================

    [Fact]
    public void BoundaryConditions_NullEmptyWhitespace_GracefullyHandled()
    {
        Account account = new() { ID = 99, Name = "EdgeUser", User = null! };

        // Null / Empty / Whitespace guards
        account.AddIPAddress(null!).Should().BeFalse();
        account.AddIPAddress("").Should().BeFalse();
        account.AddIPAddress("   ").Should().BeFalse();
        account.HasIPAddress(null!).Should().BeFalse();
        account.HasIPAddress("").Should().BeFalse();
        account.IPAddressCollection.Should().BeEmpty();

        account.AddMACAddress(null!).Should().BeFalse();
        account.AddMACAddress("").Should().BeFalse();
        account.AddMACAddress("   ").Should().BeFalse();
        account.HasMACAddress(null!).Should().BeFalse();
        account.HasMACAddress("").Should().BeFalse();
        account.MACAddressCollection.Should().BeEmpty();

        account.AddBlockedPhrase(null!).Should().BeFalse();
        account.AddBlockedPhrase("").Should().BeFalse();
        account.AddBlockedPhrase("   ").Should().BeFalse();
        account.RemoveBlockedPhrase(null!).Should().BeFalse();
        account.RemoveBlockedPhrase("").Should().BeFalse();
        account.RemoveBlockedPhrase("nonexistent").Should().BeFalse();
        account.HasBlockedPhrase(null!).Should().BeFalse();
        account.BlockedPhrases.Should().BeEmpty();

        account.AddSelectedStoreItem(null!);
        account.AddSelectedStoreItem("");
        account.AddSelectedStoreItem("   ");
        account.RemoveSelectedStoreItem(null!).Should().BeFalse();
        account.RemoveSelectedStoreItem("").Should().BeFalse();
        account.RemoveSelectedStoreItem("nonexistent").Should().BeFalse();
        account.HasSelectedStoreItem(null!).Should().BeFalse();
        account.SelectedStoreItems.Should().BeEmpty();

        // Whitespace trimming validation
        account.AddIPAddress("  172.16.10.5  ").Should().BeTrue();
        account.IPAddressCollection.First().IPAddress.Should().Be("172.16.10.5");
        account.HasIPAddress("172.16.10.5").Should().BeTrue();
        account.HasIPAddress("  172.16.10.5  ").Should().BeTrue();

        account.AddMACAddress("  12-34-56-78-9A-BC  ").Should().BeTrue();
        account.MACAddressCollection.First().MACAddress.Should().Be("12-34-56-78-9A-BC");
        account.HasMACAddress("12-34-56-78-9a-bc").Should().BeTrue();

        account.AddBlockedPhrase("  griefing  ").Should().BeTrue();
        account.BlockedPhrases.First().Phrase.Should().Be("griefing");
        account.HasBlockedPhrase("griefing").Should().BeTrue();

        account.AddSelectedStoreItem("  ai.test_icon  ");
        account.SelectedStoreItems.First().ItemCode.Should().Be("ai.test_icon");
        account.HasSelectedStoreItem("ai.test_icon").Should().BeTrue();
    }

    [Fact]
    public void AccountRelationalDecompositionAdversarial_EntityOperatorsAndEquality_BehaveConsistently()
    {
        // 1. AccountIpAddress
        AccountIpAddress ip = new() { AccountID = 1, IPAddress = "10.0.0.1" };
        string ipStr = ip;
        ipStr.Should().Be("10.0.0.1");
        AccountIpAddress ipFromStr = (AccountIpAddress)"10.0.0.1";
        ipFromStr.IPAddress.Should().Be("10.0.0.1");
        ip.ToString().Should().Be("10.0.0.1");
        ip.Equals("10.0.0.1").Should().BeTrue();
        ip.Equals(new AccountIpAddress { IPAddress = "10.0.0.1" }).Should().BeTrue();
        ((string)(AccountIpAddress?)null!).Should().Be(string.Empty);

        // 2. AccountMacAddress
        AccountMacAddress mac = new() { AccountID = 1, MACAddress = "AA-BB-CC-DD-EE-FF" };
        string macStr = mac;
        macStr.Should().Be("AA-BB-CC-DD-EE-FF");
        AccountMacAddress macFromStr = (AccountMacAddress)"AA-BB-CC-DD-EE-FF";
        macFromStr.MACAddress.Should().Be("AA-BB-CC-DD-EE-FF");
        mac.Equals("aa-bb-cc-dd-ee-ff").Should().BeTrue("Case insensitive MAC equality");
        mac.GetHashCode().Should().Be(new AccountMacAddress { MACAddress = "aa-bb-cc-dd-ee-ff" }.GetHashCode());

        // 3. AccountBlockedPhrase
        AccountBlockedPhrase bp = new() { AccountID = 1, Phrase = "Spam" };
        string bpStr = bp;
        bpStr.Should().Be("Spam");
        bp.Equals("SPAM").Should().BeTrue("Case insensitive phrase equality");

        // 4. AccountSelectedStoreItem
        AccountSelectedStoreItem item = new() { AccountID = 1, ItemCode = "ai.icon" };
        string itemStr = item;
        itemStr.Should().Be("ai.icon");
        item.Equals("AI.ICON").Should().BeTrue("Case insensitive item code equality");
    }

    // =========================================================================
    // Challenge 6: Repository Integration & Session Recording
    // =========================================================================

    [Fact]
    public async Task Bug2_AccountRepository_UpdateAsync_DisconnectedEntityReconciliationFails_ProducesDuplicates()
    {
        (Account account, MerrickContext db, string dbName) = await CreateTestAccountWithUserAsync(300, "ReconcileUser");
        AccountRepository repo = new(db);

        // Initial setup: items [A, B, C]
        using (db)
        {
            account.SelectedStoreItems =
            [
                new AccountSelectedStoreItem { AccountID = account.ID, ItemCode = "item.A" },
                new AccountSelectedStoreItem { AccountID = account.ID, ItemCode = "item.B" },
                new AccountSelectedStoreItem { AccountID = account.ID, ItemCode = "item.C" }
            ];
            await repo.UpdateAsync(account);
        }

        // Reconcile: update to [B, D] (A and C removed, D added, B retained)
        // Loaded via GetByIdAsync which uses AsNoTracking() (standard disconnected entity pattern in ASP.NET / RPC services)
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository repo2 = new(db2);
            Account loaded = (await repo2.GetByIdAsync(account.ID))!;
            loaded.SelectedStoreItems =
            [
                new AccountSelectedStoreItem { AccountID = account.ID, ItemCode = "item.B" },
                new AccountSelectedStoreItem { AccountID = account.ID, ItemCode = "item.D" }
            ];

            // EXPECTED: UpdateAsync should reconcile SelectedStoreItems so that only [item.B, item.D] remain.
            // ACTUAL BUG: Because loaded was fetched with AsNoTracking(), ChangeTracker.Entries<Account>() is empty,
            // so existingEntry is null and UpdateAsync falls into db.Accounts.Update(account), which inserts item.B and item.D
            // without removing item.A and item.C. This results in duplicate items and orphaned records!
            await repo2.UpdateAsync(loaded);
        }

        // Verify in third context
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository repo3 = new(db3);
            Account loaded = (await repo3.GetByIdAsync(account.ID))!;

            loaded.SelectedStoreItemCodes.Should().BeEquivalentTo(["item.B", "item.D"],
                "UpdateAsync on a disconnected Account entity must reconcile child SelectedStoreItems, deleting removed items and retaining existing ones");
        }
    }

    [Fact]
    public async Task Repository_RecordAccountSessionMetadata_DeduplicatesIPAndMacOnRepeatedLogins()
    {
        (Account account, MerrickContext db, string dbName) = await CreateTestAccountWithUserAsync(400, "SessionDedupeUser");

        // First login: record IP and MAC
        using (db)
        {
            AccountRepository repo = new(db);
            AccountSession session1 = new()
            {
                AccountID = account.ID,
                IPAddress = "203.0.113.42",
                HWID = "11-22-33-44-55-66",
                SessionCookie = "cookie_1"
            };

            await repo.RecordAccountTelemetryAndSessionAsync(account.ID, session1, "203.0.113.42", "11-22-33-44-55-66", null, null);
        }

        // Second login: same IP and MAC (different session cookie)
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository repo2 = new(db2);
            AccountSession session2 = new()
            {
                AccountID = account.ID,
                IPAddress = "203.0.113.42",
                HWID = "11-22-33-44-55-66",
                SessionCookie = "cookie_2"
            };

            await repo2.RecordAccountTelemetryAndSessionAsync(account.ID, session2, "203.0.113.42", "11-22-33-44-55-66", null, null);
        }

        // Third login: new IP, same MAC
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository repo3 = new(db3);
            AccountSession session3 = new()
            {
                AccountID = account.ID,
                IPAddress = "198.51.100.99",
                HWID = "11-22-33-44-55-66",
                SessionCookie = "cookie_3"
            };

            await repo3.RecordAccountTelemetryAndSessionAsync(account.ID, session3, "198.51.100.99", "11-22-33-44-55-66", null, null);
        }

        // Verify counts: 2 distinct IPs, 1 distinct MAC, 3 sessions
        using (MerrickContext db4 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account refreshed = await db4.Accounts
                .Include(a => a.IPAddressCollection)
                .Include(a => a.MACAddressCollection)
                .Include(a => a.Sessions)
                .FirstAsync(a => a.ID == account.ID);

            refreshed.IPAddressCollection.Should().HaveCount(2);
            refreshed.IPAddressStrings.Should().BeEquivalentTo(["203.0.113.42", "198.51.100.99"]);

            refreshed.MACAddressCollection.Should().HaveCount(1);
            refreshed.MACAddressStrings.Should().BeEquivalentTo(["11-22-33-44-55-66"]);

            refreshed.Sessions.Should().HaveCount(3);
        }
    }

    [Fact]
    public async Task Repository_InventoryEquipStoreItem_ReplacesCategoriesCorrectly()
    {
        (Account account, MerrickContext db, string dbName) = await CreateTestAccountWithUserAsync(450, "InventoryEquipUser");

        using (db)
        {
            InventoryRepository invRepo = new(db);

            // Equip icon (prefix icon. -> ai.)
            await invRepo.EquipStoreItemAsync("InventoryEquipUser", 1, "icon.avatar_pyro");
            // Equip chat color (cc.)
            await invRepo.EquipStoreItemAsync("InventoryEquipUser", 2, "color_gold");
            // Equip taunt (t.)
            await invRepo.EquipStoreItemAsync("InventoryEquipUser", 3, "taunt.kongor_smash");
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            InventoryRepository invRepo2 = new(db2);
            IReadOnlyList<string> items = await invRepo2.GetSelectedStoreItemsAsync(account.ID);
            items.Should().Contain(["ai.avatar_pyro", "cc.color_gold", "t.taunt.kongor_smash"]);

            // Now replace icon with a new icon: older icon must be replaced, not duplicated
            await invRepo2.EquipStoreItemAsync("InventoryEquipUser", 4, "icon.avatar_valkyrie");
        }

        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            InventoryRepository invRepo3 = new(db3);
            IReadOnlyList<string> items = await invRepo3.GetSelectedStoreItemsAsync(account.ID);
            items.Should().Contain("ai.avatar_valkyrie");
            items.Should().NotContain("ai.avatar_pyro", "Old category icon must be replaced");
            items.Should().HaveCount(3);
        }
    }
}

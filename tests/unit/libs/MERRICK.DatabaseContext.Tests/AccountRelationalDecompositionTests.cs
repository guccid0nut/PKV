// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class AccountRelationalDecompositionTests
{
    private static async Task<(Account Account, MerrickContext Context, string DbName)> CreateAccountWithDependenciesAsync()
    {
        string dbName = Guid.NewGuid().ToString("N");
        MerrickContext context = TestDbContextFactory.CreateInMemoryContext(dbName);

        User user = new()
        {
            ID = 1,
            EmailAddress = "testuser@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "srp_h",
            SRPPasswordSalt = "srp_s",
            Role = new Role { ID = 1, Name = "User" }
        };

        Account account = new()
        {
            ID = 10,
            UserID = 1,
            User = user,
            Name = "KongorWarrior"
        };

        context.Users.Add(user);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        return (account, context, dbName);
    }

    // ==========================================
    // 1. AccountIpAddress Relational CRUD Tests
    // ==========================================

    [Fact]
    public async Task AccountIpAddress_CreateAndRead_PersistsRelationalRecord()
    {
        (Account account, MerrickContext db, string dbName) = await CreateAccountWithDependenciesAsync();
        using (db)
        {
            AccountIpAddress ip1 = new() { AccountID = account.ID, IPAddress = "192.168.1.50" };
            AccountIpAddress ip2 = new() { AccountID = account.ID, IPAddress = "10.0.0.1" };

            db.AccountIpAddresses.AddRange(ip1, ip2);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<AccountIpAddress> ips = await db2.AccountIpAddresses
                .Where(ip => ip.AccountID == account.ID)
                .OrderBy(ip => ip.IPAddress)
                .ToListAsync();

            ips.Should().HaveCount(2);
            ips[0].IPAddress.Should().Be("10.0.0.1");
            ips[0].AccountID.Should().Be(account.ID);
            ips[1].IPAddress.Should().Be("192.168.1.50");

            // Verify navigation property and backwards-compatible projections
            Account refreshedAccount = await db2.Accounts
                .Include(a => a.IPAddressCollection)
                .FirstAsync(a => a.ID == account.ID);

            refreshedAccount.IPAddressCollection.Should().HaveCount(2);
            refreshedAccount.IPAddressStrings.Should().Contain(["10.0.0.1", "192.168.1.50"]);
            refreshedAccount.IpAddresses.Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task AccountIpAddress_UpdateAndRemove_MutatesRelationalRecord()
    {
        (Account account, MerrickContext db, string dbName) = await CreateAccountWithDependenciesAsync();
        int recordId;
        using (db)
        {
            AccountIpAddress ip = new() { AccountID = account.ID, IPAddress = "172.16.0.1" };
            db.AccountIpAddresses.Add(ip);
            await db.SaveChangesAsync();
            recordId = ip.ID;
        }

        // Update
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountIpAddress record = await db2.AccountIpAddresses.FirstAsync(i => i.ID == recordId);
            record.IPAddress = "172.16.0.254";
            await db2.SaveChangesAsync();
        }

        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountIpAddress record = await db3.AccountIpAddresses.FirstAsync(i => i.ID == recordId);
            record.IPAddress.Should().Be("172.16.0.254");

            // Delete
            db3.AccountIpAddresses.Remove(record);
            await db3.SaveChangesAsync();
        }

        using (MerrickContext db4 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db4.AccountIpAddresses.AnyAsync(i => i.ID == recordId)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task AccountIpAddress_CascadeDelete_RemovedWhenAccountDeleted()
    {
        (Account account, MerrickContext db, string dbName) = await CreateAccountWithDependenciesAsync();
        using (db)
        {
            db.AccountIpAddresses.AddRange(
                new AccountIpAddress { AccountID = account.ID, IPAddress = "1.1.1.1" },
                new AccountIpAddress { AccountID = account.ID, IPAddress = "1.0.0.1" }
            );
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account acc = await db2.Accounts
                .Include(a => a.IPAddressCollection)
                .FirstAsync(a => a.ID == account.ID);
            db2.Accounts.Remove(acc);
            await db2.SaveChangesAsync();
        }

        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<AccountIpAddress> orphaned = await db3.AccountIpAddresses
                .Where(i => i.AccountID == account.ID)
                .ToListAsync();
            orphaned.Should().BeEmpty("Account deletion must cascade-delete all related AccountIpAddresses");
        }
    }

    [Fact]
    public void AccountIpAddress_DomainHelpers_AddAndQuery()
    {
        Account account = new()
        {
            ID = 5,
            Name = "DomainTester",
            User = null!
        };

        account.AddIPAddress("192.168.1.1").Should().BeTrue();
        account.AddIPAddress("192.168.1.1").Should().BeFalse("Duplicate IP should be ignored");
        account.AddIPAddress("  10.0.0.1  ").Should().BeTrue();

        account.HasIPAddress("192.168.1.1").Should().BeTrue();
        account.HasIPAddress("10.0.0.1").Should().BeTrue();
        account.HasIPAddress("8.8.8.8").Should().BeFalse();

        account.IPAddressStrings.Should().BeEquivalentTo(new[] { "192.168.1.1", "10.0.0.1" });
    }

    // ===========================================
    // 2. AccountMacAddress Relational CRUD Tests
    // ===========================================

    [Fact]
    public async Task AccountMacAddress_CreateAndRead_PersistsRelationalRecord()
    {
        (Account account, MerrickContext db, string dbName) = await CreateAccountWithDependenciesAsync();
        using (db)
        {
            AccountMacAddress mac1 = new() { AccountID = account.ID, MACAddress = "00-1B-44-11-3A-B7" };
            AccountMacAddress mac2 = new() { AccountID = account.ID, MACAddress = "3C-52-82-A4-10-F2" };

            db.AccountMacAddresses.AddRange(mac1, mac2);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<AccountMacAddress> macs = await db2.AccountMacAddresses
                .Where(m => m.AccountID == account.ID)
                .ToListAsync();

            macs.Should().HaveCount(2);
            macs.Select(m => m.MACAddress).Should().Contain(["00-1B-44-11-3A-B7", "3C-52-82-A4-10-F2"]);

            Account refreshedAccount = await db2.Accounts
                .Include(a => a.MACAddressCollection)
                .FirstAsync(a => a.ID == account.ID);

            refreshedAccount.MACAddressCollection.Should().HaveCount(2);
            refreshedAccount.MACAddressStrings.Should().Contain(["00-1B-44-11-3A-B7", "3C-52-82-A4-10-F2"]);
            refreshedAccount.MacAddresses.Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task AccountMacAddress_CascadeDelete_RemovedWhenAccountDeleted()
    {
        (Account account, MerrickContext db, string dbName) = await CreateAccountWithDependenciesAsync();
        using (db)
        {
            db.AccountMacAddresses.Add(new AccountMacAddress { AccountID = account.ID, MACAddress = "DE-AD-BE-EF-00-01" });
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account acc = await db2.Accounts
                .Include(a => a.MACAddressCollection)
                .FirstAsync(a => a.ID == account.ID);
            db2.Accounts.Remove(acc);
            await db2.SaveChangesAsync();
        }

        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db3.AccountMacAddresses.AnyAsync(m => m.AccountID == account.ID)).Should().BeFalse();
        }
    }

    [Fact]
    public void AccountMacAddress_DomainHelpers_AddAndQuery()
    {
        Account account = new()
        {
            ID = 5,
            Name = "MacTester",
            User = null!
        };

        account.AddMACAddress("AA-BB-CC-DD-EE-FF").Should().BeTrue();
        account.AddMACAddress("aa-bb-cc-dd-ee-ff").Should().BeFalse("Duplicate case-insensitive MAC should be rejected");
        account.AddMACAddress("11-22-33-44-55-66").Should().BeTrue();

        account.HasMACAddress("AA-BB-CC-DD-EE-FF").Should().BeTrue();
        account.HasMACAddress("aa-bb-cc-dd-ee-ff").Should().BeTrue();
        account.HasMACAddress("00-00-00-00-00-00").Should().BeFalse();

        account.MACAddressStrings.Should().HaveCount(2);
    }

    // =============================================
    // 3. AccountBlockedPhrase Relational CRUD Tests
    // =============================================

    [Fact]
    public async Task AccountBlockedPhrase_CreateAndRead_PersistsRelationalRecord()
    {
        (Account account, MerrickContext db, string dbName) = await CreateAccountWithDependenciesAsync();
        using (db)
        {
            AccountBlockedPhrase phrase1 = new() { AccountID = account.ID, Phrase = "toxic_slur_1" };
            AccountBlockedPhrase phrase2 = new() { AccountID = account.ID, Phrase = "spam_link_2" };

            db.AccountBlockedPhrases.AddRange(phrase1, phrase2);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<AccountBlockedPhrase> phrases = await db2.AccountBlockedPhrases
                .Where(p => p.AccountID == account.ID)
                .ToListAsync();

            phrases.Should().HaveCount(2);
            phrases.Select(p => p.Phrase).Should().Contain(["toxic_slur_1", "spam_link_2"]);

            Account refreshedAccount = await db2.Accounts
                .Include(a => a.BlockedPhrases)
                .FirstAsync(a => a.ID == account.ID);

            refreshedAccount.BlockedPhrases.Should().HaveCount(2);
            refreshedAccount.BlockedPhraseStrings.Should().Contain(["toxic_slur_1", "spam_link_2"]);
        }
    }

    [Fact]
    public async Task AccountBlockedPhrase_CascadeDelete_RemovedWhenAccountDeleted()
    {
        (Account account, MerrickContext db, string dbName) = await CreateAccountWithDependenciesAsync();
        using (db)
        {
            db.AccountBlockedPhrases.Add(new AccountBlockedPhrase { AccountID = account.ID, Phrase = "bad_word" });
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account acc = await db2.Accounts
                .Include(a => a.BlockedPhrases)
                .FirstAsync(a => a.ID == account.ID);
            db2.Accounts.Remove(acc);
            await db2.SaveChangesAsync();
        }

        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db3.AccountBlockedPhrases.AnyAsync(p => p.AccountID == account.ID)).Should().BeFalse();
        }
    }

    [Fact]
    public void AccountBlockedPhrase_DomainHelpers_AddAndRemove()
    {
        Account account = new()
        {
            ID = 5,
            Name = "PhraseTester",
            User = null!
        };

        account.AddBlockedPhrase("noob").Should().BeTrue();
        account.AddBlockedPhrase("noob").Should().BeFalse("Duplicate phrase should be ignored");
        account.AddBlockedPhrase("uninstall").Should().BeTrue();

        account.HasBlockedPhrase("noob").Should().BeTrue();
        account.HasBlockedPhrase("uninstall").Should().BeTrue();
        account.HasBlockedPhrase("gg").Should().BeFalse();

        account.RemoveBlockedPhrase("noob").Should().BeTrue();
        account.HasBlockedPhrase("noob").Should().BeFalse();
        account.RemoveBlockedPhrase("noob").Should().BeFalse();

        account.BlockedPhraseStrings.Should().BeEquivalentTo(new[] { "uninstall" });
    }

    // =================================================
    // 4. AccountSelectedStoreItem Relational CRUD Tests
    // =================================================

    [Fact]
    public async Task AccountSelectedStoreItem_CreateAndRead_PersistsRelationalRecord()
    {
        (Account account, MerrickContext db, string dbName) = await CreateAccountWithDependenciesAsync();
        using (db)
        {
            AccountSelectedStoreItem item1 = new() { AccountID = account.ID, ItemCode = "ai.custom_icon:1" };
            AccountSelectedStoreItem item2 = new() { AccountID = account.ID, ItemCode = "taunt.kongor" };

            db.AccountSelectedStoreItems.AddRange(item1, item2);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<AccountSelectedStoreItem> items = await db2.AccountSelectedStoreItems
                .Where(i => i.AccountID == account.ID)
                .ToListAsync();

            items.Should().HaveCount(2);
            items.Select(i => i.ItemCode).Should().Contain(["ai.custom_icon:1", "taunt.kongor"]);

            Account refreshedAccount = await db2.Accounts
                .Include(a => a.SelectedStoreItems)
                .FirstAsync(a => a.ID == account.ID);

            refreshedAccount.SelectedStoreItems.Should().HaveCount(2);
            refreshedAccount.SelectedStoreItemCodes.Should().Contain(["ai.custom_icon:1", "taunt.kongor"]);
        }
    }

    [Fact]
    public async Task AccountSelectedStoreItem_CascadeDelete_RemovedWhenAccountDeleted()
    {
        (Account account, MerrickContext db, string dbName) = await CreateAccountWithDependenciesAsync();
        using (db)
        {
            db.AccountSelectedStoreItems.Add(new AccountSelectedStoreItem { AccountID = account.ID, ItemCode = "ai.custom_icon:1" });
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account acc = await db2.Accounts
                .Include(a => a.SelectedStoreItems)
                .FirstAsync(a => a.ID == account.ID);
            db2.Accounts.Remove(acc);
            await db2.SaveChangesAsync();
        }

        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db3.AccountSelectedStoreItems.AnyAsync(s => s.AccountID == account.ID)).Should().BeFalse();
        }
    }

    [Fact]
    public void AccountSelectedStoreItem_DomainHelpers_SetAndMutate()
    {
        Account account = new()
        {
            ID = 5,
            Name = "StoreTester",
            User = null!
        };

        account.SetSelectedStoreItems(["ai.custom_icon:1", "cc.white", "t.Standard"]);
        account.SelectedStoreItemCodes.Should().BeEquivalentTo(new[] { "ai.custom_icon:1", "cc.white", "t.Standard" });

        account.AddSelectedStoreItem("cs.shield");
        account.HasSelectedStoreItem("cs.shield").Should().BeTrue();

        account.RemoveSelectedStoreItem("cc.white").Should().BeTrue();
        account.HasSelectedStoreItem("cc.white").Should().BeFalse();
        account.RemoveSelectedStoreItem("nonexistent").Should().BeFalse();
    }

    // =================================================
    // 5. Cross-Account Relational Isolation Test
    // =================================================

    [Fact]
    public async Task AccountRelationalDecomposition_AccountRelationalChildEntities_IsolatedBetweenAccounts()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = new()
            {
                ID = 1,
                EmailAddress = "shared_user@kongor.net",
                PBKDF2PasswordHash = "p",
                SRPPasswordHash = "h",
                SRPPasswordSalt = "s",
                Role = new Role { ID = 1, Name = "User" }
            };

            Account acc1 = new() { ID = 101, UserID = 1, User = user, Name = "Account101" };
            Account acc2 = new() { ID = 202, UserID = 1, User = user, Name = "Account202" };

            db.Users.Add(user);
            db.Accounts.AddRange(acc1, acc2);

            db.AccountIpAddresses.AddRange(
                new AccountIpAddress { AccountID = 101, IPAddress = "10.10.10.101" },
                new AccountIpAddress { AccountID = 202, IPAddress = "10.10.10.202" }
            );

            db.AccountMacAddresses.AddRange(
                new AccountMacAddress { AccountID = 101, MACAddress = "11-11-11-11-11-11" },
                new AccountMacAddress { AccountID = 202, MACAddress = "22-22-22-22-22-22" }
            );

            db.AccountBlockedPhrases.AddRange(
                new AccountBlockedPhrase { AccountID = 101, Phrase = "phrase_101" },
                new AccountBlockedPhrase { AccountID = 202, Phrase = "phrase_202" }
            );

            db.AccountSelectedStoreItems.AddRange(
                new AccountSelectedStoreItem { AccountID = 101, ItemCode = "item_101" },
                new AccountSelectedStoreItem { AccountID = 202, ItemCode = "item_202" }
            );

            await db.SaveChangesAsync();
        }

        // Delete Account 101
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account acc1 = await db2.Accounts
                .Include(a => a.IPAddressCollection)
                .Include(a => a.MACAddressCollection)
                .Include(a => a.BlockedPhrases)
                .Include(a => a.SelectedStoreItems)
                .FirstAsync(a => a.ID == 101);
            db2.Accounts.Remove(acc1);
            await db2.SaveChangesAsync();
        }

        // Verify Account 202 records are 100% intact
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db3.AccountIpAddresses.AnyAsync(i => i.AccountID == 101)).Should().BeFalse();
            (await db3.AccountMacAddresses.AnyAsync(m => m.AccountID == 101)).Should().BeFalse();
            (await db3.AccountBlockedPhrases.AnyAsync(p => p.AccountID == 101)).Should().BeFalse();
            (await db3.AccountSelectedStoreItems.AnyAsync(s => s.AccountID == 101)).Should().BeFalse();

            AccountIpAddress ip202 = await db3.AccountIpAddresses.FirstAsync(i => i.AccountID == 202);
            ip202.IPAddress.Should().Be("10.10.10.202");

            AccountMacAddress mac202 = await db3.AccountMacAddresses.FirstAsync(m => m.AccountID == 202);
            mac202.MACAddress.Should().Be("22-22-22-22-22-22");

            AccountBlockedPhrase phrase202 = await db3.AccountBlockedPhrases.FirstAsync(p => p.AccountID == 202);
            phrase202.Phrase.Should().Be("phrase_202");

            AccountSelectedStoreItem item202 = await db3.AccountSelectedStoreItems.FirstAsync(s => s.AccountID == 202);
            item202.ItemCode.Should().Be("item_202");
        }
    }

    // =================================================
    // 6. Model Metadata Composite Unique Index Tests
    // =================================================

    [Fact]
    public void AccountChildEntities_ModelUniqueIndexes_AreConfigured()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
        IModel model = db.Model;

        // 1. AccountSelectedStoreItems: unique [AccountID, ItemCode]
        IEntityType storeItemEntity = model.FindEntityType(typeof(AccountSelectedStoreItem))!;
        storeItemEntity.GetIndexes()
            .Should().Contain(idx =>
                idx.IsUnique &&
                idx.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(AccountSelectedStoreItem.AccountID), nameof(AccountSelectedStoreItem.ItemCode) }));

        // 2. AccountIpAddresses: unique [AccountID, IPAddress]
        IEntityType ipEntity = model.FindEntityType(typeof(AccountIpAddress))!;
        ipEntity.GetIndexes()
            .Should().Contain(idx =>
                idx.IsUnique &&
                idx.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(AccountIpAddress.AccountID), nameof(AccountIpAddress.IPAddress) }));

        // 3. AccountMacAddresses: unique [AccountID, MACAddress]
        IEntityType macEntity = model.FindEntityType(typeof(AccountMacAddress))!;
        macEntity.GetIndexes()
            .Should().Contain(idx =>
                idx.IsUnique &&
                idx.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(AccountMacAddress.AccountID), nameof(AccountMacAddress.MACAddress) }));

        // 4. AccountBlockedPhrases: unique [AccountID, Phrase]
        IEntityType phraseEntity = model.FindEntityType(typeof(AccountBlockedPhrase))!;
        phraseEntity.GetIndexes()
            .Should().Contain(idx =>
                idx.IsUnique &&
                idx.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(AccountBlockedPhrase.AccountID), nameof(AccountBlockedPhrase.Phrase) }));
    }
}

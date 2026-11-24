// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Tests.Infrastructure;
using NEXUS.Contracts.Data;
using Microsoft.Extensions.DependencyInjection;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Extensions;
using System.IO;
using System.Reflection;
using FluentAssertions;
using MERRICK.DatabaseContext.Constants;
using MERRICK.DatabaseContext.Entities;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Lore;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Handlers;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class MerrickDatabaseAdversarialTests
{
    private static MerrickContext CreateInMemoryContext(string? dbName = null)
    {
        return Infrastructure.TestDbContextFactory.CreateInMemoryContext(dbName);
    }

    [Fact]
    public void MerrickContext_All40CustomTables_MappedToExactSchemas()
    {
        using MerrickContext context = CreateInMemoryContext();
        IModel model = context.Model;

        Dictionary<Type, (string ExpectedSchema, string ExpectedTable)> expectedMappings = new()
        {
            // Core Schema (15 tables)
            { typeof(Account), (MerrickContext.CoreSchema, "Accounts") },
            { typeof(AccountSelectedStoreItem), (MerrickContext.CoreSchema, "AccountSelectedStoreItems") },
            { typeof(AccountIpAddress), (MerrickContext.CoreSchema, "AccountIpAddresses") },
            { typeof(AccountMacAddress), (MerrickContext.CoreSchema, "AccountMacAddresses") },
            { typeof(AccountBlockedPhrase), (MerrickContext.CoreSchema, "AccountBlockedPhrases") },
            { typeof(AccountPeer), (MerrickContext.CoreSchema, "AccountPeers") },
            { typeof(Clan), (MerrickContext.CoreSchema, "Clans") },
            { typeof(HostApplication), (MerrickContext.CoreSchema, "HostApplications") },
            { typeof(StreamerApplication), (MerrickContext.CoreSchema, "StreamerApplications") },
            { typeof(SystemTelemetryMetrics), (MerrickContext.CoreSchema, "SystemTelemetryMetrics") },
            { typeof(User), (MerrickContext.CoreSchema, "Users") },
            { typeof(EconomyTransaction), (MerrickContext.CoreSchema, "EconomyTransactions") },
            { typeof(Friendship), (MerrickContext.CoreSchema, "Friendships") },
            { typeof(UserInventory), (MerrickContext.CoreSchema, "UserInventory") },
            { typeof(AccountSession), (MerrickContext.CoreSchema, "AccountSessions") },

            // Data / Default Schema (3 tables)
            { typeof(AccountStatistics), (MerrickContext.DefaultSchema, "AccountStatistics") },
            { typeof(MatchmakingQueueWaitEntry), (MerrickContext.DefaultSchema, "MatchmakingQueueWaitEntries") },
            { typeof(StoreItem), (MerrickContext.DefaultSchema, "StoreItems") },

            // Statistics Schema (4 tables)
            { typeof(Mastery), (MerrickContext.StatisticsSchema, "Masteries") },
            { typeof(HeroMasteryExperience), (MerrickContext.StatisticsSchema, "HeroMasteryExperiences") },
            { typeof(MatchStatistics), (MerrickContext.StatisticsSchema, "MatchStatistics") },
            { typeof(PlayerStatistics), (MerrickContext.StatisticsSchema, "PlayerStatistics") },

            // Miscellaneous Schema (16 tables)
            { typeof(HeroDetail), (MerrickContext.MiscellaneousSchema, "HeroDetails") },
            { typeof(HeroDifficultyVote), (MerrickContext.MiscellaneousSchema, "HeroDifficultyVotes") },
            { typeof(HeroGuide), (MerrickContext.MiscellaneousSchema, "HeroGuides") },
            { typeof(HeroGuideItem), (MerrickContext.MiscellaneousSchema, "HeroGuideItems") },
            { typeof(HeroGuideAbility), (MerrickContext.MiscellaneousSchema, "HeroGuideAbilities") },
            { typeof(HeroItem), (MerrickContext.MiscellaneousSchema, "HeroItems") },
            { typeof(MatchEvidence), (MerrickContext.MiscellaneousSchema, "MatchEvidence") },
            { typeof(Notification), (MerrickContext.MiscellaneousSchema, "Notifications") },
            { typeof(PlayerReport), (MerrickContext.MiscellaneousSchema, "PlayerReports") },
            { typeof(RedeemableCode), (MerrickContext.MiscellaneousSchema, "RedeemableCodes") },
            { typeof(UserWebReward), (MerrickContext.MiscellaneousSchema, "UserWebRewards") },
            { typeof(LoreArc), (MerrickContext.MiscellaneousSchema, "LoreArcs") },
            { typeof(LoreComic), (MerrickContext.MiscellaneousSchema, "LoreComics") },
            { typeof(LorePage), (MerrickContext.MiscellaneousSchema, "LorePages") },
            { typeof(LoreCredit), (MerrickContext.MiscellaneousSchema, "LoreCredits") },
            { typeof(LoreRating), (MerrickContext.MiscellaneousSchema, "LoreRatings") },

            // Authentication Schema (2 tables)
            { typeof(Role), (MerrickContext.AuthenticationSchema, "Roles") },
            { typeof(Token), (MerrickContext.AuthenticationSchema, "Tokens") }
        };

        expectedMappings.Count.Should().Be(40, "There must be exactly 40 custom tables configured across the 5 schemas");

        foreach (KeyValuePair<Type, (string ExpectedSchema, string ExpectedTable)> kvp in expectedMappings)
        {
            Type entityType = kvp.Key;
            (string expectedSchema, string expectedTable) = kvp.Value;

            IEntityType? entity = model.FindEntityType(entityType);
            entity.Should().NotBeNull($"Entity type {entityType.Name} must be registered in MerrickContext model");

            string? schema = entity!.GetSchema();
            string? table = entity.GetTableName();

            schema.Should().Be(expectedSchema, $"Entity {entityType.Name} should be mapped to schema '{expectedSchema}'");
            table.Should().Be(expectedTable, $"Entity {entityType.Name} should be mapped to table '{expectedTable}'");
        }
    }

    [Fact]
    public void MerrickContext_ForeignKeyDeleteBehaviors_AreCorrectlyConfigured()
    {
        using MerrickContext context = CreateInMemoryContext();
        IModel model = context.Model;

        // 1. stat.PlayerStatistics -> core.Accounts (Cascade)
        IEntityType playerStats = model.FindEntityType(typeof(PlayerStatistics))!;
        IForeignKey psAccountFk = playerStats.GetForeignKeys()
            .First(fk => fk.PrincipalEntityType.ClrType == typeof(Account));
        psAccountFk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 2. stat.PlayerStatistics -> stat.MatchStatistics (Cascade)
        IForeignKey psMatchFk = playerStats.GetForeignKeys()
            .First(fk => fk.PrincipalEntityType.ClrType == typeof(MatchStatistics));
        psMatchFk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 3. data.AccountStatistics -> core.Accounts (Cascade)
        IEntityType accountStats = model.FindEntityType(typeof(AccountStatistics))!;
        IForeignKey asAccountFk = accountStats.GetForeignKeys()
            .First(fk => fk.PrincipalEntityType.ClrType == typeof(Account));
        asAccountFk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 4. misc.HeroGuides -> core.Accounts (Cascade)
        IEntityType heroGuides = model.FindEntityType(typeof(HeroGuide))!;
        IForeignKey hgAuthorFk = heroGuides.GetForeignKeys()
            .First(fk => fk.PrincipalEntityType.ClrType == typeof(Account));
        hgAuthorFk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 5. misc.PlayerReports -> ReportedAccount (Cascade), ReporterAccount (SetNull), MatchStatistics (Cascade)
        IEntityType playerReports = model.FindEntityType(typeof(PlayerReport))!;
        List<IForeignKey> prAccountFks = playerReports.GetForeignKeys()
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(Account)).ToList();
        prAccountFks.Should().HaveCount(2);
        prAccountFks.First(fk => fk.Properties.Any(p => p.Name == nameof(PlayerReport.ReportedAccountID)))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        prAccountFks.First(fk => fk.Properties.Any(p => p.Name == nameof(PlayerReport.ReporterAccountID)))
            .DeleteBehavior.Should().Be(DeleteBehavior.SetNull);

        IForeignKey prMatchFk = playerReports.GetForeignKeys()
            .First(fk => fk.PrincipalEntityType.ClrType == typeof(MatchStatistics));
        prMatchFk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 6. misc.Notifications -> core.Accounts (Cascade)
        IEntityType notifications = model.FindEntityType(typeof(Notification))!;
        IForeignKey notifAccountFk = notifications.GetForeignKeys()
            .First(fk => fk.PrincipalEntityType.ClrType == typeof(Account));
        notifAccountFk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 7. misc.MatchEvidence -> stat.MatchStatistics (Cascade)
        IEntityType matchEvidence = model.FindEntityType(typeof(MatchEvidence))!;
        IForeignKey meMatchFk = matchEvidence.GetForeignKeys()
            .First(fk => fk.PrincipalEntityType.ClrType == typeof(MatchStatistics));
        meMatchFk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 8. core.StreamerApplications -> User (Restrict), MainAccount (Restrict)
        IEntityType streamerApps = model.FindEntityType(typeof(StreamerApplication))!;
        streamerApps.GetForeignKeys().First(fk => fk.PrincipalEntityType.ClrType == typeof(User))
            .DeleteBehavior.Should().Be(DeleteBehavior.Restrict);
        streamerApps.GetForeignKeys().First(fk => fk.PrincipalEntityType.ClrType == typeof(Account))
            .DeleteBehavior.Should().Be(DeleteBehavior.Restrict);

        // 9. core.EconomyTransactions -> User (Cascade), Account (SetNull)
        IEntityType economyTx = model.FindEntityType(typeof(EconomyTransaction))!;
        economyTx.GetForeignKeys().First(fk => fk.PrincipalEntityType.ClrType == typeof(User))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        economyTx.GetForeignKeys().First(fk => fk.PrincipalEntityType.ClrType == typeof(Account))
            .DeleteBehavior.Should().Be(DeleteBehavior.SetNull);

        // 10. core.Friendships -> Account (Cascade), FriendAccount (Restrict)
        IEntityType friendships = model.FindEntityType(typeof(Friendship))!;
        List<IForeignKey> fAccountFks = friendships.GetForeignKeys()
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(Account)).ToList();
        fAccountFks.Should().HaveCount(2);
        fAccountFks.First(fk => fk.Properties.Any(p => p.Name == nameof(Friendship.AccountID)))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        fAccountFks.First(fk => fk.Properties.Any(p => p.Name == nameof(Friendship.FriendAccountID)))
            .DeleteBehavior.Should().Be(DeleteBehavior.Restrict);

        // 11. core.UserInventory -> User (Cascade), Account (SetNull), StoreItem (SetNull)
        IEntityType userInv = model.FindEntityType(typeof(UserInventory))!;
        userInv.GetForeignKeys().First(fk => fk.PrincipalEntityType.ClrType == typeof(User))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        userInv.GetForeignKeys().First(fk => fk.PrincipalEntityType.ClrType == typeof(Account))
            .DeleteBehavior.Should().Be(DeleteBehavior.SetNull);
        userInv.GetForeignKeys().First(fk => fk.PrincipalEntityType.ClrType == typeof(StoreItem))
            .DeleteBehavior.Should().Be(DeleteBehavior.SetNull);

        // 12. core.AccountSessions -> Account (Cascade)
        IEntityType sessions = model.FindEntityType(typeof(AccountSession))!;
        sessions.GetForeignKeys().First(fk => fk.PrincipalEntityType.ClrType == typeof(Account))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 13. core.AccountSelectedStoreItems -> core.Accounts (Cascade)
        IEntityType storeItemChild = model.FindEntityType(typeof(AccountSelectedStoreItem))!;
        storeItemChild.GetForeignKeys().First(fk => fk.PrincipalEntityType.ClrType == typeof(Account))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 14. core.AccountIpAddresses -> core.Accounts (Cascade)
        IEntityType ipChild = model.FindEntityType(typeof(AccountIpAddress))!;
        ipChild.GetForeignKeys().First(fk => fk.PrincipalEntityType.ClrType == typeof(Account))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 15. core.AccountMacAddresses -> core.Accounts (Cascade)
        IEntityType macChild = model.FindEntityType(typeof(AccountMacAddress))!;
        macChild.GetForeignKeys().First(fk => fk.PrincipalEntityType.ClrType == typeof(Account))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 16. core.AccountBlockedPhrases -> core.Accounts (Cascade)
        IEntityType phraseChild = model.FindEntityType(typeof(AccountBlockedPhrase))!;
        phraseChild.GetForeignKeys().First(fk => fk.PrincipalEntityType.ClrType == typeof(Account))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 17. stat.HeroMasteryExperiences -> stat.Masteries (Cascade)
        IEntityType hmeChild = model.FindEntityType(typeof(HeroMasteryExperience))!;
        hmeChild.GetForeignKeys().First(fk => fk.PrincipalEntityType.ClrType == typeof(Mastery))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 18. misc.HeroGuideItems -> misc.HeroGuides (Cascade)
        IEntityType hgiChild = model.FindEntityType(typeof(HeroGuideItem))!;
        hgiChild.GetForeignKeys().First(fk => fk.PrincipalEntityType.ClrType == typeof(HeroGuide))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        // 19. misc.HeroGuideAbilities -> misc.HeroGuides (Cascade)
        IEntityType hgaChild = model.FindEntityType(typeof(HeroGuideAbility))!;
        hgaChild.GetForeignKeys().First(fk => fk.PrincipalEntityType.ClrType == typeof(HeroGuide))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
    }

    [Fact]
    public void MerrickContext_UniqueConstraints_AreCorrectlyDefined()
    {
        using MerrickContext context = CreateInMemoryContext();
        IModel model = context.Model;

        // 1. data.StoreItems unique on Code
        IEntityType storeItem = model.FindEntityType(typeof(StoreItem))!;
        List<IIndex> storeItemIndexes = storeItem.GetIndexes().Where(i => i.IsUnique).ToList();
        storeItemIndexes.Should().ContainSingle(i => i.Properties.Any(p => p.Name == nameof(StoreItem.Code)));

        // 2. core.Friendships unique on (AccountID, FriendAccountID)
        IEntityType friendship = model.FindEntityType(typeof(Friendship))!;
        List<IIndex> friendshipIndexes = friendship.GetIndexes().Where(i => i.IsUnique).ToList();
        friendshipIndexes.Should().ContainSingle(i =>
            i.Properties.Count == 2 &&
            i.Properties.Any(p => p.Name == nameof(Friendship.AccountID)) &&
            i.Properties.Any(p => p.Name == nameof(Friendship.FriendAccountID)));

        // 3. core.UserInventory unique on (UserID, ItemCode)
        IEntityType userInv = model.FindEntityType(typeof(UserInventory))!;
        List<IIndex> userInvIndexes = userInv.GetIndexes().Where(i => i.IsUnique).ToList();
        userInvIndexes.Should().ContainSingle(i =>
            i.Properties.Count == 2 &&
            i.Properties.Any(p => p.Name == nameof(UserInventory.UserID)) &&
            i.Properties.Any(p => p.Name == nameof(UserInventory.ItemCode)));

        // 4. stat.PlayerStatistics unique on (MatchID, AccountID)
        IEntityType playerStats = model.FindEntityType(typeof(PlayerStatistics))!;
        List<IIndex> playerStatsIndexes = playerStats.GetIndexes().Where(i => i.IsUnique).ToList();
        playerStatsIndexes.Should().ContainSingle(i =>
            i.Properties.Count == 2 &&
            i.Properties.Any(p => p.Name == nameof(PlayerStatistics.MatchID)) &&
            i.Properties.Any(p => p.Name == nameof(PlayerStatistics.AccountID)));

        // 5. data.AccountStatistics unique on (AccountID, Type)
        IEntityType accountStats = model.FindEntityType(typeof(AccountStatistics))!;
        List<IIndex> accountStatsIndexes = accountStats.GetIndexes().Where(i => i.IsUnique).ToList();
        accountStatsIndexes.Should().ContainSingle(i =>
            i.Properties.Count == 2 &&
            i.Properties.Any(p => p.Name == nameof(AccountStatistics.AccountID)) &&
            i.Properties.Any(p => p.Name == nameof(AccountStatistics.Type)));

        // 6. stat.HeroMasteryExperiences unique on (MasteryID, HeroIdentifier)
        IEntityType hme = model.FindEntityType(typeof(HeroMasteryExperience))!;
        List<IIndex> hmeIndexes = hme.GetIndexes().Where(i => i.IsUnique).ToList();
        hmeIndexes.Should().ContainSingle(i =>
            i.Properties.Count == 2 &&
            i.Properties.Any(p => p.Name == nameof(HeroMasteryExperience.MasteryID)) &&
            i.Properties.Any(p => p.Name == nameof(HeroMasteryExperience.HeroIdentifier)));

        // 7. misc.HeroGuideItems unique on (GuideID, SlotType, OrderIndex)
        IEntityType hgi = model.FindEntityType(typeof(HeroGuideItem))!;
        List<IIndex> hgiIndexes = hgi.GetIndexes().Where(i => i.IsUnique).ToList();
        hgiIndexes.Should().ContainSingle(i =>
            i.Properties.Count == 3 &&
            i.Properties.Any(p => p.Name == nameof(HeroGuideItem.GuideID) || p.Name == "GuideID" || p.Name == "HeroGuideID") &&
            i.Properties.Any(p => p.Name == nameof(HeroGuideItem.SlotType)) &&
            i.Properties.Any(p => p.Name == nameof(HeroGuideItem.OrderIndex)),
            "HeroGuideItems must define a composite unique index on [GuideID, SlotType, OrderIndex]");

        // 8. misc.HeroGuideAbilities unique on (GuideID, LevelSequence)
        IEntityType hga = model.FindEntityType(typeof(HeroGuideAbility))!;
        List<IIndex> hgaIndexes = hga.GetIndexes().Where(i => i.IsUnique).ToList();
        hgaIndexes.Should().ContainSingle(i =>
            i.Properties.Count == 2 &&
            i.Properties.Any(p => p.Name == nameof(HeroGuideAbility.GuideID) || p.Name == "GuideID" || p.Name == "HeroGuideID") &&
            i.Properties.Any(p => p.Name == nameof(HeroGuideAbility.LevelSequence)),
            "HeroGuideAbilities must define a composite unique index on [GuideID, LevelSequence]");

        // 9. nexus_auth.Tokens unique on Value (IX_Tokens_Value) and index on UserID (IX_Tokens_UserID)
        IEntityType token = model.FindEntityType(typeof(Token))!;
        List<IIndex> tokenUniqueIndexes = token.GetIndexes().Where(i => i.IsUnique).ToList();
        tokenUniqueIndexes.Should().ContainSingle(i =>
            i.Properties.Any(p => p.Name == nameof(Token.Value)) &&
            i.GetDatabaseName() == "IX_Tokens_Value");

        token.GetIndexes().Should().Contain(i =>
            !i.IsUnique &&
            i.Properties.Any(p => p.Name == nameof(Token.UserID)) &&
            i.GetDatabaseName() == "IX_Tokens_UserID");

        // 10. core.Accounts index on ClanID (IX_Accounts_ClanID)
        IEntityType account = model.FindEntityType(typeof(Account))!;
        account.GetIndexes().Should().Contain(i =>
            i.Properties.Any(p => p.Name == nameof(Account.ClanID)) &&
            i.GetDatabaseName() == "IX_Accounts_ClanID");
    }

    [Fact]
    public void HeroItem_CostProperty_IsStronglyTypedInteger()
    {
        using MerrickContext context = CreateInMemoryContext();
        IModel model = context.Model;

        IEntityType heroItemType = model.FindEntityType(typeof(HeroItem))!;
        heroItemType.Should().NotBeNull("HeroItem must be registered in MerrickContext model");

        IProperty costProp = heroItemType.FindProperty(nameof(HeroItem.Cost))!;
        costProp.Should().NotBeNull("HeroItem must define a Cost property");
        costProp.ClrType.Should().Be<int>("HeroItem.Cost must be strongly-typed as an integer in 1NF schema normalization");

        PropertyInfo? clrProperty = typeof(HeroItem).GetProperty(nameof(HeroItem.Cost));
        clrProperty.Should().NotBeNull();
        clrProperty!.PropertyType.Should().Be<int>("C# entity property HeroItem.Cost must be int, not string");
    }

    [Fact]
    public async Task SeedDataHandlers_SeedStoreItems_PopulatesCatalogWithExpectedItems()
    {
        using MerrickContext context = CreateInMemoryContext();
        await SeedDataHandlers.SeedStoreItems(context, NullLogger.Instance, CancellationToken.None);

        List<StoreItem> items = await context.StoreItems.ToListAsync();
        items.Should().HaveCountGreaterOrEqualTo(25, "Baseline catalog must seed at least 25 items across categories");

        // Check essential categories
        items.Should().Contain(i => i.Code.StartsWith("ai."), "Must contain account icons");
        items.Should().Contain(i => i.Code.StartsWith("cc."), "Must contain chat colors");
        items.Should().Contain(i => i.Code.StartsWith("cs."), "Must contain clan shields");
        items.Should().Contain(i => i.Code.StartsWith("t.") || i.Code.StartsWith("m."), "Must contain taunts");
        items.Should().Contain(i => i.Code.StartsWith("av."), "Must contain announcers");
        items.Should().Contain(i => i.Code.StartsWith("c."), "Must contain couriers");
        items.Should().Contain(i => i.Code.StartsWith("w."), "Must contain wards");
        items.Should().Contain(i => i.Code.StartsWith("te."), "Must contain teleport effects");
        items.Should().Contain(i => i.Code.StartsWith("sc."), "Must contain upgrade circles");

        // Check unique codes
        items.Select(i => i.Code).Distinct().Count().Should().Be(items.Count, "All seeded store item codes must be strictly unique");
    }

    [Fact]
    public void DatabaseInitializer_PostgreSql_DDL_Vulnerability_Audit()
    {
        string? projectRoot = FindProjectRoot();
        projectRoot.Should().NotBeNull("Project root must be discoverable");

        string sourcePath = Path.Combine(projectRoot!, "src", "libs", "database-context", "Services", "DatabaseInitializer.cs");
        File.Exists(sourcePath).Should().BeTrue($"DatabaseInitializer.cs must exist at {sourcePath}");
        string code = File.ReadAllText(sourcePath);

        // Adversarial check: verify that DatabaseInitializer.cs contains no syntax errors or corrupted tokens
        code.Should().NotContain("ID prisoners", "DatabaseInitializer.cs must not contain corrupted 'ID prisoners' typo at line 155");
        code.Should().Contain("\"\"data\"\".\"\"StoreItems\"\"", "DatabaseInitializer.cs must create StoreItems table");
        code.Should().Contain("\"\"core\"\".\"\"EconomyTransactions\"\"", "DatabaseInitializer.cs must create EconomyTransactions table");
        code.Should().NotContain("// Fallback without invalid typo", "DatabaseInitializer.cs must not rely on flawed catch fallback blocks");
    }

    [Fact]
    public void DatabaseInitializer_RLS_Coverage_ContainsAll40Tables()
    {
        string? projectRoot = FindProjectRoot();
        projectRoot.Should().NotBeNull("Project root must be discoverable");

        string sourcePath = Path.Combine(projectRoot!, "src", "libs", "database-context", "Services", "DatabaseInitializer.cs");
        File.Exists(sourcePath).Should().BeTrue();
        string code = File.ReadAllText(sourcePath);

        // Verify that all 40 custom tables are listed in the tables_to_rls array
        string[] expectedTables = [
            "Accounts", "Clans", "HostApplications", "StreamerApplications", "SystemTelemetryMetrics",
            "Users", "EconomyTransactions", "Friendships", "UserInventory", "AccountSessions",
            "AccountSelectedStoreItems", "AccountIpAddresses", "AccountMacAddresses", "AccountBlockedPhrases", "AccountPeers",
            "AccountStatistics", "MatchmakingQueueWaitEntries", "StoreItems",
            "Masteries", "HeroMasteryExperiences", "MatchStatistics", "PlayerStatistics",
            "HeroDetails", "HeroDifficultyVotes", "HeroGuides", "HeroGuideItems", "HeroGuideAbilities", "HeroItems", "MatchEvidence",
            "Notifications", "PlayerReports", "RedeemableCodes", "UserWebRewards",
            "LoreArcs", "LoreComics", "LorePages", "LoreCredits", "LoreRatings",
            "Roles", "Tokens"
        ];

        expectedTables.Length.Should().Be(40);

        foreach (string tableName in expectedTables)
        {
            code.Should().Contain($"'{tableName}'", $"DatabaseInitializer.cs must include table '{tableName}' in tables_to_rls array");
        }
    }

    [Fact]
    public void DatabaseInitializer_RLS_Coverage_ContainsAll52Tables_AndDynamicSafeguard()
    {
        string? projectRoot = FindProjectRoot();
        projectRoot.Should().NotBeNull("Project root must be discoverable");

        string sourcePath = Path.Combine(projectRoot!, "src", "libs", "database-context", "Services", "DatabaseInitializer.cs");
        File.Exists(sourcePath).Should().BeTrue();
        string code = File.ReadAllText(sourcePath);

        // Verify the newly hardened tables are explicitly in tables_to_rls
        string[] newlyHardenedTables = ["InviteSystemAuditLogs", "UserInvites", "MatchRecords"];
        foreach (string tableName in newlyHardenedTables)
        {
            code.Should().Contain($"'{tableName}'", $"DatabaseInitializer.cs must include table '{tableName}' in tables_to_rls array");
        }

        // Verify dynamic schema-wide safeguard loop is present
        code.Should().Contain("table_type = 'BASE TABLE'", "DatabaseInitializer.cs must include dynamic safeguard loop over base tables");
        code.Should().Contain("ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY;", "DatabaseInitializer.cs must dynamically enable RLS across all base tables");

        // Verify Public Read policy for MatchRecords
        code.Should().Contain("\"Public Read MatchRecords\"", "DatabaseInitializer.cs must define Public Read MatchRecords policy");
    }

    [Fact]
    public void DatabaseInitializer_ForeignKeys_ContainsAllRequiredCascades()
    {
        string? projectRoot = FindProjectRoot();
        projectRoot.Should().NotBeNull("Project root must be discoverable");

        string sourcePath = Path.Combine(projectRoot!, "src", "libs", "database-context", "Services", "DatabaseInitializer.cs");
        File.Exists(sourcePath).Should().BeTrue();
        string code = File.ReadAllText(sourcePath);

        string[] requiredFkConstraints = [
            "FK_PlayerStatistics_Accounts_AccountID",
            "FK_PlayerStatistics_MatchStatistics_MatchID",
            "FK_AccountStatistics_Accounts_account_id",
            "FK_HeroGuides_Accounts_AuthorID",
            "FK_HeroGuideItems_HeroGuides_GuideID",
            "FK_HeroGuideAbilities_HeroGuides_GuideID",
            "FK_PlayerReports_Accounts_ReportedAccountID",
            "FK_PlayerReports_Accounts_ReporterAccountID",
            "FK_PlayerReports_MatchStatistics_MatchID",
            "FK_Notifications_Accounts_AccountId",
            "FK_MatchEvidence_MatchStatistics_MatchID"
        ];

        foreach (string fk in requiredFkConstraints)
        {
            code.Should().Contain(fk, $"DatabaseInitializer.cs must configure foreign key constraint '{fk}'");
        }
    }

    [Fact]
    public async Task MerrickContext_EntityNavigations_AreConfiguredAndOperable()
    {
        using MerrickContext context = CreateInMemoryContext();

        User user = new()
        {
            ID = 1,
            EmailAddress = "test@projectkongor.com",
            RoleID = 2,
            PBKDF2PasswordHash = "pbkdf2_hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt"
        };

        Account account = new()
        {
            ID = 10,
            UserID = 1,
            User = user,
            Name = "PlayerOne"
        };

        Account friend = new()
        {
            ID = 20,
            UserID = 1,
            User = user,
            Name = "PlayerTwo"
        };

        StoreItem storeItem = new()
        {
            Id = 100,
            Code = "ai.avatar_test",
            Name = "Avatar Test",
            LocalContent = "test"
        };

        UserInventory inventory = new()
        {
            ID = 1,
            UserID = 1,
            AccountID = 10,
            ItemCode = "ai.avatar_test",
            StoreItemId = 100
        };

        Friendship friendship = new()
        {
            ID = 1,
            AccountID = 10,
            FriendAccountID = 20,
            FriendGroup = "Buddies",
            Status = Enumerations.FriendshipStatus.Accepted
        };

        EconomyTransaction tx = new()
        {
            ID = 1,
            UserID = 1,
            AccountID = 10,
            GoldCoinsDelta = 100,
            TransactionType = "Reward"
        };

        AccountSession session = new()
        {
            ID = 1,
            AccountID = 10,
            UserID = 1,
            IPAddress = "127.0.0.1"
        };

        HeroGuide guide = new()
        {
            ID = 1,
            AuthorID = 10,
            Author = account,
            Name = "Test Guide"
        };

        MatchStatistics match = new()
        {
            ID = 1,
            MatchID = 9999
        };

        PlayerStatistics pStats = new()
        {
            ID = 1,
            AccountID = 10,
            MatchID = 9999
        };

        PlayerReport report = new()
        {
            ID = 1,
            ReporterAccountID = 10,
            ReporterAccountName = "PlayerOne",
            ReportedAccountID = 20,
            ReportedAccountName = "PlayerTwo",
            MatchID = 9999,
            Reason = "Griefing"
        };

        Notification notif = new()
        {
            NotificationId = 1,
            AccountId = 10,
            Content = "Welcome to Kongor"
        };

        MatchEvidence evidence = new()
        {
            ID = 1,
            MatchID = 9999
        };

        context.Users.Add(user);
        context.Accounts.AddRange(account, friend);
        context.StoreItems.Add(storeItem);
        context.UserInventory.Add(inventory);
        context.Friendships.Add(friendship);
        context.EconomyTransactions.Add(tx);
        context.AccountSessions.Add(session);
        context.HeroGuides.Add(guide);
        context.MatchStatistics.Add(match);
        context.PlayerStatistics.Add(pStats);
        context.PlayerReports.Add(report);
        context.Notifications.Add(notif);
        context.MatchEvidence.Add(evidence);

        await context.SaveChangesAsync();

        // Verify Account navigations
        Account loadedAccount = await context.Accounts
            .Include(a => a.User)
            .Include(a => a.Sessions)
            .Include(a => a.Friendships)
            .Include(a => a.ReverseFriendships)
            .Include(a => a.EconomyTransactions)
            .Include(a => a.AuthoredGuides)
            .Include(a => a.ReportsFiled)
            .Include(a => a.ReportsReceived)
            .Include(a => a.Notifications)
            .Include(a => a.MatchParticipations)
            .FirstAsync(a => a.ID == 10);

        loadedAccount.User.Should().NotBeNull();
        loadedAccount.Sessions.Should().HaveCount(1);
        loadedAccount.Friendships.Should().HaveCount(1);
        loadedAccount.EconomyTransactions.Should().HaveCount(1);
        loadedAccount.AuthoredGuides.Should().HaveCount(1);
        loadedAccount.ReportsFiled.Should().HaveCount(1);
        loadedAccount.Notifications.Should().HaveCount(1);
        loadedAccount.MatchParticipations.Should().HaveCount(1);

        // Verify User navigations
        User loadedUser = await context.Users
            .Include(u => u.Accounts)
            .Include(u => u.InventoryItems)
            .Include(u => u.EconomyTransactions)
            .Include(u => u.Sessions)
            .FirstAsync(u => u.ID == 1);

        loadedUser.Accounts.Should().HaveCount(2);
        loadedUser.InventoryItems.Should().HaveCount(1);
        loadedUser.EconomyTransactions.Should().HaveCount(1);
        loadedUser.Sessions.Should().HaveCount(1);

        // Verify StoreItem navigation
        StoreItem loadedStoreItem = await context.StoreItems
            .Include(s => s.UserInventories)
            .FirstAsync(s => s.Id == 100);

        loadedStoreItem.UserInventories.Should().HaveCount(1);

        // Verify PlayerStatistics navigations
        PlayerStatistics loadedPStats = await context.PlayerStatistics
            .Include(p => p.Account)
            .Include(p => p.Match)
            .FirstAsync(p => p.ID == 1);

        loadedPStats.Account.Should().NotBeNull();
        loadedPStats.Match.Should().NotBeNull();

        // Verify PlayerReport navigations
        PlayerReport loadedReport = await context.PlayerReports
            .Include(r => r.ReporterAccount)
            .Include(r => r.ReportedAccount)
            .Include(r => r.Match)
            .FirstAsync(r => r.ID == 1);

        loadedReport.ReporterAccount.Should().NotBeNull();
        loadedReport.ReportedAccount.Should().NotBeNull();
        loadedReport.Match.Should().NotBeNull();

        // Verify Notification navigation
        Notification loadedNotif = await context.Notifications
            .Include(n => n.Account)
            .FirstAsync(n => n.NotificationId == 1);

        loadedNotif.Account.Should().NotBeNull();

        // Verify MatchEvidence navigation
        MatchEvidence loadedEvidence = await context.MatchEvidence
            .Include(e => e.Match)
            .FirstAsync(e => e.ID == 1);

        loadedEvidence.Match.Should().NotBeNull();
    }

    [Fact]
    public void MerrickContext_DualPropertyMappings_AreSynchronizedAndPreserveState()
    {
        // 1. EconomyTransaction dual properties
        EconomyTransaction tx = new()
        {
            ID = 42,
            TransactionType = "Reward",
            GoldDelta = 100,
            SilverDelta = 250,
            PlinkoDelta = 5,
            GoldBalanceAfter = 1100,
            SilverBalanceAfter = 2250,
            PlinkoBalanceAfter = 15
        };

        tx.Id.Should().Be(42);
        tx.GoldCoinsDelta.Should().Be(100);
        tx.SilverCoinsDelta.Should().Be(250);
        tx.PlinkoTicketsDelta.Should().Be(5);
        tx.GoldCoinsBalanceAfter.Should().Be(1100);
        tx.SilverCoinsBalanceAfter.Should().Be(2250);
        tx.PlinkoTicketsBalanceAfter.Should().Be(15);

        tx.GoldCoinsDelta = 300;
        tx.GoldDelta.Should().Be(300);
        tx.SilverCoinsDelta = 400;
        tx.SilverDelta.Should().Be(400);
        tx.PlinkoTicketsDelta = 8;
        tx.PlinkoDelta.Should().Be(8);
        tx.GoldCoinsBalanceAfter = 1500;
        tx.GoldBalanceAfter.Should().Be(1500);
        tx.SilverCoinsBalanceAfter = 3000;
        tx.SilverBalanceAfter.Should().Be(3000);
        tx.PlinkoTicketsBalanceAfter = 20;
        tx.PlinkoBalanceAfter.Should().Be(20);

        // 2. UserInventory dual properties
        UserInventory inv = new()
        {
            ID = 10,
            ItemCode = "cc.emerald",
            StoreItemId = 99
        };
        inv.Id.Should().Be(10);
        inv.ProductCode.Should().Be("cc.emerald");
        inv.StoreItemID.Should().Be(99);

        inv.ProductCode = "ai.champion";
        inv.ItemCode.Should().Be("ai.champion");
        inv.StoreItemID = 101;
        inv.StoreItemId.Should().Be(101);

        // 3. AccountSession dual properties
        AccountSession session = new()
        {
            ID = 7,
            IPAddress = "192.168.1.1",
            HWID = "HWID_ABC123",
            SystemInformation = "RAW_SYS_INFO",
            OperatingSystem = "Windows 11 Pro"
        };
        session.Id.Should().Be(7);
        session.SystemInformationHash.Should().Be("HWID_ABC123");
        session.SystemInformationRaw.Should().Be("RAW_SYS_INFO");
        session.OSType.Should().Be("Windows 11 Pro");

        session.SystemInformationHash = "HWID_NEW";
        session.HWID.Should().Be("HWID_NEW");
        session.SystemInformationRaw = "RAW_SYS_NEW";
        session.SystemInformation.Should().Be("RAW_SYS_NEW");
        session.OSType = "Ubuntu Linux 24.04";
        session.OperatingSystem.Should().Be("Ubuntu Linux 24.04");

        // 4. HeroGuide dual properties
        HeroGuide guide = new()
        {
            ID = 1,
            Name = "Pyromancer Mid Carry",
            Intro = "Guide Introduction"
        };
        guide.GuideTitle.Should().Be("Pyromancer Mid Carry");
        guide.GuideDescription.Should().Be("Guide Introduction");

        guide.GuideTitle = "Updated Title";
        guide.Name.Should().Be("Updated Title");
        guide.GuideDescription = "Updated Intro";
        guide.Intro.Should().Be("Updated Intro");

        // 5. Notification dual properties
        Notification notif = new()
        {
            NotificationId = 33,
            AccountId = 55
        };
        notif.ID.Should().Be(33);
        notif.AccountID.Should().Be(55);

        notif.ID = 34;
        notif.NotificationId.Should().Be(34);
        notif.AccountID = 56;
        notif.AccountId.Should().Be(56);

        // 6. PlayerStatistics dual properties
        PlayerStatistics ps = new()
        {
            HeroKills = 12,
            HeroDeaths = 4,
            HeroAssists = 18,
            GameplayStat0 = 8500.5,
            GameplayStat1 = 22000.0,
            GameplayStat2 = 35.5,
            GameplayStat3 = 75.0,
            GameplayStat4 = 4200.0,
            GameplayStat5 = 6800.0,
            GameplayStat6 = 12.0,
            GameplayStat7 = 44.0,
            GameplayStat8 = 5100.0,
            GameplayStat9 = 150.0
        };

        ps.Kills.Should().Be(12);
        ps.Deaths.Should().Be(4);
        ps.Assists.Should().Be(18);
        ps.HealingDone.Should().Be(8500.5);
        ps.DamageTaken.Should().Be(22000.0);
        ps.CrowdControlDuration.Should().Be(35.5);
        ps.BuffsApplied.Should().Be(75.0);
        ps.PureDamageDealt.Should().Be(4200.0);
        ps.BossDamageDealt.Should().Be(6800.0);
        ps.PowerupsCollected.Should().Be(12.0);
        ps.SkillshotsLanded.Should().Be(44.0);
        ps.ShieldsDamageAbsorbed.Should().Be(5100.0);
        ps.ObjectiveScore.Should().Be(150.0);

        ps.Kills = 15;
        ps.HeroKills.Should().Be(15);
        ps.Deaths = 2;
        ps.HeroDeaths.Should().Be(2);
        ps.Assists = 25;
        ps.HeroAssists.Should().Be(25);
        ps.HealingDone = 9000.0;
        ps.GameplayStat0.Should().Be(9000.0);

        // 7. User & Account coin synchronizations
        User user = new()
        {
            EmailAddress = "coins@test.com",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            GoldCoins = 500,
            SilverCoins = 1000
        };
        Account account = new()
        {
            Name = "CoinsPlayer",
            User = user,
            UserID = user.ID
        };

        account.GoldCoins.Should().Be(500);
        account.SilverCoins.Should().Be(1000);

        account.GoldCoins = 600;
        user.GoldCoins.Should().Be(600);
        account.SilverCoins = 1200;
        user.SilverCoins.Should().Be(1200);
    }

    [Fact]
    public async Task MerrickContext_CascadeDeletions_NoCyclicalCascadeConflicts()
    {
        string dbName = Guid.NewGuid().ToString();
        using (MerrickContext context = CreateInMemoryContext(dbName))
        {
            User user1 = new()
            {
                ID = 1,
                EmailAddress = "u1@kongor.com",
                RoleID = 2,
                PBKDF2PasswordHash = "pbkdf2_1",
                SRPPasswordHash = "srp_h_1",
                SRPPasswordSalt = "srp_s_1"
            };

            User user2 = new()
            {
                ID = 2,
                EmailAddress = "u2@kongor.com",
                RoleID = 2,
                PBKDF2PasswordHash = "pbkdf2_2",
                SRPPasswordHash = "srp_h_2",
                SRPPasswordSalt = "srp_s_2"
            };

            Account acc1 = new() { ID = 10, UserID = 1, User = user1, Name = "PlayerA" };
            Account acc2 = new() { ID = 20, UserID = 2, User = user2, Name = "PlayerB" };
            Account accNode = new() { ID = 30, UserID = 1, User = user1, Name = "HostedNodeA", OwnerAccountID = 10, OwnerAccount = acc1 };

            StoreItem item = new() { Id = 100, Name = "Taunt Kongor", Code = "t.kongor", LocalContent = "{}" };

            UserInventory inv1 = new() { ID = 1, UserID = 1, AccountID = 10, ItemCode = "t.kongor", StoreItemId = 100 };
            EconomyTransaction tx1 = new() { ID = 1, UserID = 1, AccountID = 10, GoldCoinsDelta = 50, TransactionType = "Purchase" };
            AccountSession sess1 = new() { ID = 1, AccountID = 10, UserID = 1, IPAddress = "127.0.0.1" };
            Friendship fr1 = new() { ID = 1, AccountID = 10, FriendAccountID = 20, Status = Enumerations.FriendshipStatus.Accepted };
            Friendship fr2 = new() { ID = 2, AccountID = 20, FriendAccountID = 10, Status = Enumerations.FriendshipStatus.Accepted };
            HeroGuide guide1 = new() { ID = 1, AuthorID = 10, Author = acc1, Name = "Carry Guide" };
            HeroGuideItem guideItem1 = new() { ID = 1, GuideID = 1, Guide = guide1, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_Runes" };
            HeroGuideAbility guideAbil1 = new() { ID = 1, GuideID = 1, Guide = guide1, LevelSequence = 1, AbilityCode = "Ability_1" };
            Notification notif1 = new() { NotificationId = 1, AccountId = 10, Content = "Hello" };
            UserWebReward reward1 = new() { Id = 1, UserID = 1, RewardType = "Icon", RewardCode = "ic.1", Name = "Reward Icon" };

            MatchStatistics match = new() { ID = 1, MatchID = 8888 };
            PlayerStatistics pStat1 = new() { ID = 1, MatchID = 8888, AccountID = 10 };
            PlayerStatistics pStat2 = new() { ID = 2, MatchID = 8888, AccountID = 20 };
            PlayerReport report1 = new()
            {
                ID = 1,
                ReporterAccountID = 10,
                ReporterAccountName = "PlayerA",
                ReportedAccountID = 20,
                ReportedAccountName = "PlayerB",
                MatchID = 8888,
                Reason = "Griefing"
            };
            PlayerReport report2 = new()
            {
                ID = 2,
                ReporterAccountID = 20,
                ReporterAccountName = "PlayerB",
                ReportedAccountID = 10,
                ReportedAccountName = "PlayerA",
                MatchID = 8888,
                Reason = "Abuse"
            };
            MatchEvidence evidence = new() { ID = 1, MatchID = 8888 };

            context.Users.AddRange(user1, user2);
            context.Accounts.AddRange(acc1, acc2, accNode);
            context.StoreItems.Add(item);
            context.UserInventory.Add(inv1);
            context.EconomyTransactions.Add(tx1);
            context.AccountSessions.Add(sess1);
            context.Friendships.AddRange(fr1, fr2);
            context.HeroGuides.Add(guide1);
            context.HeroGuideItems.Add(guideItem1);
            context.HeroGuideAbilities.Add(guideAbil1);
            context.Notifications.Add(notif1);
            context.UserWebRewards.Add(reward1);

            AccountSelectedStoreItem childItem = new() { AccountID = 10, ItemCode = "ai.custom_icon:1" };
            AccountIpAddress childIp = new() { AccountID = 10, IPAddress = "192.168.1.100" };
            AccountMacAddress childMac = new() { AccountID = 10, MACAddress = "AA-BB-CC-DD-EE-FF" };
            AccountBlockedPhrase childPhrase = new() { AccountID = 10, Phrase = "toxic_phrase" };

            context.AccountSelectedStoreItems.Add(childItem);
            context.AccountIpAddresses.Add(childIp);
            context.AccountMacAddresses.Add(childMac);
            context.AccountBlockedPhrases.Add(childPhrase);

            context.MatchStatistics.Add(match);
            context.PlayerStatistics.AddRange(pStat1, pStat2);
            context.PlayerReports.AddRange(report1, report2);
            context.MatchEvidence.Add(evidence);

            await context.SaveChangesAsync();
        }

        // Test 1: Deleting Account 10 cascades its owned child records without cyclical error
        using (MerrickContext context = CreateInMemoryContext(dbName))
        {
            Account acc1 = await context.Accounts
                .Include(a => a.Friendships)
                .Include(a => a.ReverseFriendships)
                .Include(a => a.AuthoredGuides).ThenInclude(g => g.Items)
                .Include(a => a.AuthoredGuides).ThenInclude(g => g.Abilities)
                .Include(a => a.Notifications)
                .Include(a => a.Sessions)
                .Include(a => a.MatchParticipations)
                .Include(a => a.Statistics)
                .Include(a => a.ReportsReceived)
                .Include(a => a.SelectedStoreItems)
                .Include(a => a.IPAddressCollection)
                .Include(a => a.MACAddressCollection)
                .Include(a => a.BlockedPhrases)
                .FirstAsync(a => a.ID == 10);

            context.Accounts.Remove(acc1);

            // Should succeed without cyclical cascade exceptions
            Func<Task> act = async () => await context.SaveChangesAsync();
            await act.Should().NotThrowAsync("Deleting Account must not trigger cyclical cascade exceptions");
        }

        // Verify remaining state after Account 10 deletion
        using (MerrickContext context = CreateInMemoryContext(dbName))
        {
            (await context.Accounts.AnyAsync(a => a.ID == 10)).Should().BeFalse();
            (await context.AccountSelectedStoreItems.AnyAsync(s => s.AccountID == 10)).Should().BeFalse();
            (await context.AccountIpAddresses.AnyAsync(i => i.AccountID == 10)).Should().BeFalse();
            (await context.AccountMacAddresses.AnyAsync(m => m.AccountID == 10)).Should().BeFalse();
            (await context.AccountBlockedPhrases.AnyAsync(p => p.AccountID == 10)).Should().BeFalse();
            (await context.HeroGuides.AnyAsync(g => g.AuthorID == 10)).Should().BeFalse();
            (await context.HeroGuideItems.AnyAsync(i => i.GuideID == 1)).Should().BeFalse();
            (await context.HeroGuideAbilities.AnyAsync(a => a.GuideID == 1)).Should().BeFalse();
            (await context.Notifications.AnyAsync(n => n.AccountId == 10)).Should().BeFalse();
            (await context.AccountSessions.AnyAsync(s => s.AccountID == 10)).Should().BeFalse();
            (await context.Friendships.AnyAsync(f => f.AccountID == 10 || f.FriendAccountID == 10)).Should().BeFalse();

            // MatchStatistics and Account 20 still exist
            (await context.Accounts.AnyAsync(a => a.ID == 20)).Should().BeTrue();
            (await context.MatchStatistics.AnyAsync(m => m.MatchID == 8888)).Should().BeTrue();

            // Report 2 reported Account 10, so it was cascaded; Report 1 reported Account 20 (still exists)
            (await context.PlayerReports.AnyAsync(r => r.ID == 2)).Should().BeFalse();
            (await context.PlayerReports.AnyAsync(r => r.ID == 1)).Should().BeTrue();

            // EconomyTransaction and UserInventory still exist for User 1
            (await context.EconomyTransactions.AnyAsync(e => e.UserID == 1)).Should().BeTrue();
            (await context.UserInventory.AnyAsync(u => u.UserID == 1)).Should().BeTrue();
        }

        // Test 2: Deleting MatchStatistics cascades evidence, player stats, and reports
        using (MerrickContext context = CreateInMemoryContext(dbName))
        {
            MatchStatistics match = await context.MatchStatistics.FirstAsync(m => m.MatchID == 8888);
            List<MatchEvidence> evidenceList = await context.MatchEvidence.Where(e => e.MatchID == 8888).ToListAsync();
            List<PlayerStatistics> pStatsList = await context.PlayerStatistics.Where(p => p.MatchID == 8888).ToListAsync();
            List<PlayerReport> reportsList = await context.PlayerReports.Where(r => r.MatchID == 8888).ToListAsync();

            context.MatchStatistics.Remove(match);
            context.MatchEvidence.RemoveRange(evidenceList);
            context.PlayerStatistics.RemoveRange(pStatsList);
            context.PlayerReports.RemoveRange(reportsList);

            Func<Task> act = async () => await context.SaveChangesAsync();
            await act.Should().NotThrowAsync("Deleting MatchStatistics and its dependent records must succeed without cyclical cascade or constraint exceptions");
        }

        using (MerrickContext context = CreateInMemoryContext(dbName))
        {
            (await context.MatchStatistics.AnyAsync(m => m.MatchID == 8888)).Should().BeFalse();
            (await context.MatchEvidence.AnyAsync(e => e.MatchID == 8888)).Should().BeFalse();
            (await context.PlayerStatistics.AnyAsync(p => p.MatchID == 8888)).Should().BeFalse();
            (await context.PlayerReports.AnyAsync(r => r.MatchID == 8888)).Should().BeFalse();
        }

        // Test 3: Deleting User 1 cascades remaining user artifacts
        using (MerrickContext context = CreateInMemoryContext(dbName))
        {
            User user1 = await context.Users
                .Include(u => u.EconomyTransactions)
                .Include(u => u.InventoryItems)
                .Include(u => u.WebRewards)
                .FirstAsync(u => u.ID == 1);

            context.Users.Remove(user1);

            Func<Task> act = async () => await context.SaveChangesAsync();
            await act.Should().NotThrowAsync("Deleting User must cascade cleanly");
        }

        using (MerrickContext context = CreateInMemoryContext(dbName))
        {
            (await context.Users.AnyAsync(u => u.ID == 1)).Should().BeFalse();
            (await context.EconomyTransactions.AnyAsync(e => e.UserID == 1)).Should().BeFalse();
            (await context.UserInventory.AnyAsync(u => u.UserID == 1)).Should().BeFalse();
            (await context.UserWebRewards.AnyAsync(w => w.UserID == 1)).Should().BeFalse();
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void MerrickContext_NpgsqlModel_GenerateCreateScript_ExecutesWithoutModelErrors()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        DbContextOptionsBuilder<MerrickContext> optionsBuilder = new();
        optionsBuilder.UseNpgsql("Host=localhost;Database=kongor_test;Username=postgres;Password=postgres");

        using MerrickContext context = new(optionsBuilder.Options);
        string ddl = context.Database.GenerateCreateScript();

        ddl.Should().NotBeNullOrWhiteSpace();

        // Check tables in core schema (Npgsql generates schema."Table")
        ddl.Should().Contain("core.\"Accounts\"");
        ddl.Should().Contain("core.\"AccountSelectedStoreItems\"");
        ddl.Should().Contain("core.\"AccountIpAddresses\"");
        ddl.Should().Contain("core.\"AccountMacAddresses\"");
        ddl.Should().Contain("core.\"AccountBlockedPhrases\"");
        ddl.Should().Contain("core.\"Clans\"");
        ddl.Should().Contain("core.\"Users\"");
        ddl.Should().Contain("core.\"EconomyTransactions\"");
        ddl.Should().Contain("core.\"Friendships\"");
        ddl.Should().Contain("core.\"UserInventory\"");
        ddl.Should().Contain("core.\"AccountSessions\"");
        ddl.Should().Contain("core.\"HostApplications\"");
        ddl.Should().Contain("core.\"StreamerApplications\"");
        ddl.Should().Contain("core.\"SystemTelemetryMetrics\"");

        // Check tables in data schema
        ddl.Should().Contain("data.\"StoreItems\"");
        ddl.Should().Contain("data.\"AccountStatistics\"");
        ddl.Should().Contain("data.\"MatchmakingQueueWaitEntries\"");

        // Check tables in stat schema
        ddl.Should().Contain("stat.\"MatchStatistics\"");
        ddl.Should().Contain("stat.\"PlayerStatistics\"");
        ddl.Should().Contain("stat.\"Masteries\"");

        // Check tables in misc schema
        ddl.Should().Contain("misc.\"HeroGuides\"");
        ddl.Should().Contain("misc.\"HeroGuideItems\"");
        ddl.Should().Contain("misc.\"HeroGuideAbilities\"");
        ddl.Should().Contain("misc.\"HeroDetails\"");
        ddl.Should().Contain("misc.\"HeroDifficultyVotes\"");
        ddl.Should().Contain("misc.\"HeroItems\"");
        ddl.Should().Contain("misc.\"MatchEvidence\"");
        ddl.Should().Contain("misc.\"Notifications\"");
        ddl.Should().Contain("misc.\"PlayerReports\"");
        ddl.Should().Contain("misc.\"RedeemableCodes\"");
        ddl.Should().Contain("misc.\"UserWebRewards\"");
        ddl.Should().Contain("misc.\"LoreArcs\"");
        ddl.Should().Contain("misc.\"LoreComics\"");
        ddl.Should().Contain("misc.\"LorePages\"");
        ddl.Should().Contain("misc.\"LoreCredits\"");
        ddl.Should().Contain("misc.\"LoreRatings\"");

        // Check tables in nexus_auth schema
        ddl.Should().Contain("nexus_auth.\"Roles\"");
        ddl.Should().Contain("nexus_auth.\"Tokens\"");
    }

    [Fact]
    public async Task MerrickContext_All40DbSets_AreRegisteredAndQueryable()
    {
        using MerrickContext context = CreateInMemoryContext();

        (await context.Accounts.CountAsync()).Should().Be(0);
        (await context.AccountSelectedStoreItems.CountAsync()).Should().Be(0);
        (await context.AccountIpAddresses.CountAsync()).Should().Be(0);
        (await context.AccountMacAddresses.CountAsync()).Should().Be(0);
        (await context.AccountBlockedPhrases.CountAsync()).Should().Be(0);
        (await context.AccountPeers.CountAsync()).Should().Be(0);
        (await context.AccountStatistics.CountAsync()).Should().Be(0);
        (await context.Clans.CountAsync()).Should().Be(0);
        (await context.HeroGuides.CountAsync()).Should().Be(0);
        (await context.HeroGuideItems.CountAsync()).Should().Be(0);
        (await context.HeroGuideAbilities.CountAsync()).Should().Be(0);
        (await context.HeroDifficultyVotes.CountAsync()).Should().Be(0);
        (await context.HeroDetails.CountAsync()).Should().Be(0);
        (await context.HeroItems.CountAsync()).Should().Be(0);
        (await context.HostApplications.CountAsync()).Should().Be(0);
        (await context.StreamerApplications.CountAsync()).Should().Be(0);
        (await context.MatchStatistics.CountAsync()).Should().Be(0);
        (await context.MatchEvidence.CountAsync()).Should().Be(0);
        (await context.Masteries.CountAsync()).Should().Be(0);
        (await context.HeroMasteryExperiences.CountAsync()).Should().Be(0);
        (await context.Notifications.CountAsync()).Should().Be(0);
        (await context.PlayerReports.CountAsync()).Should().Be(0);
        (await context.PlayerStatistics.CountAsync()).Should().Be(0);
        (await context.RedeemableCodes.CountAsync()).Should().Be(0);
        (await context.Roles.CountAsync()).Should().BeGreaterThanOrEqualTo(0);
        (await context.Tokens.CountAsync()).Should().Be(0);
        (await context.SystemTelemetryMetrics.CountAsync()).Should().Be(0);
        (await context.MatchmakingQueueWaitEntries.CountAsync()).Should().Be(0);
        (await context.Users.CountAsync()).Should().Be(0);
        (await context.UserWebRewards.CountAsync()).Should().Be(0);
        (await context.StoreItems.CountAsync()).Should().Be(0);
        (await context.EconomyTransactions.CountAsync()).Should().Be(0);
        (await context.Friendships.CountAsync()).Should().Be(0);
        (await context.UserInventory.CountAsync()).Should().Be(0);
        (await context.AccountSessions.CountAsync()).Should().Be(0);
        (await context.LoreArcs.CountAsync()).Should().Be(0);
        (await context.LoreComics.CountAsync()).Should().Be(0);
        (await context.LorePages.CountAsync()).Should().Be(0);
        (await context.LoreCredits.CountAsync()).Should().Be(0);
        (await context.LoreRatings.CountAsync()).Should().Be(0);
    }

    private static string? FindProjectRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) || Directory.Exists(Path.Combine(current, "src", "libs", "database-context")))
            {
                return current;
            }
            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }
        return null;
    }

    [Fact]
    public void IdentityColumns_EconomyTransaction_UserInventory_Friendship_PlayerReport_MappedToId()
    {
        using MerrickContext context = CreateInMemoryContext();
        IModel model = context.Model;

        IEntityType econType = model.FindEntityType(typeof(EconomyTransaction))!;
        econType.FindProperty(nameof(EconomyTransaction.ID))!.GetColumnName().Should().Be("Id");

        IEntityType invType = model.FindEntityType(typeof(UserInventory))!;
        invType.FindProperty(nameof(UserInventory.ID))!.GetColumnName().Should().Be("Id");

        IEntityType friendType = model.FindEntityType(typeof(Friendship))!;
        friendType.FindProperty(nameof(Friendship.ID))!.GetColumnName().Should().Be("Id");

        IEntityType reportType = model.FindEntityType(typeof(PlayerReport))!;
        reportType.FindProperty(nameof(PlayerReport.ID))!.GetColumnName().Should().Be("Id");
    }

    [Fact]
    public void MerrickContext_SupportsSqlServerCollation_WithoutIcuErrors()
    {
        DbContextOptionsBuilder<MerrickContext> optionsBuilder = new();
        optionsBuilder.UseSqlServer("Server=localhost;Database=KongorTest;Integrated Security=False;User ID=sa;Password=DummyPassword123!;TrustServerCertificate=True;");
        using MerrickContext context = new(optionsBuilder.Options);

        IModel model = context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        model.Should().NotBeNull();

        IEntityType accountType = model.FindEntityType(typeof(Account))!;
        accountType.FindProperty(nameof(Account.Name))!.GetCollation().Should().Be("SQL_Latin1_General_CP1_CI_AS");
    }


    #region Adversarial Guide, Match History & DI Tests

    private static User CreateTestUser(int id = 1, string email = "user@kongor.net") => new()
    {
        ID = id,
        EmailAddress = email,
        SRPPasswordSalt = "salt",
        SRPPasswordHash = "hash",
        PBKDF2PasswordHash = "pbkdf2",
        Role = new Role { ID = 1, Name = "User" }
    };

    #region 1. Hero Guide Resolution & Voting Stress Tests

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Guide_EmptyDatabase_ReturnsNullAndHandlesWhitespaceGracefully()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
        HeroGuideRepository repo = new(db);

        // 1. GetById on empty DB
        HeroGuide? byId = await repo.GetByIdAsync(999);
        byId.Should().BeNull();

        // 2. GetGuidesForHero on empty DB
        IReadOnlyList<HeroGuide> guides = await repo.GetGuidesForHeroAsync("Hero_Valkyrie", null, null);
        guides.Should().BeEmpty();

        // 3. GetActiveGuideForHero on empty DB
        HeroGuide? active = await repo.GetActiveGuideForHeroAsync("Hero_Valkyrie", null, null, null);
        active.Should().BeNull();

        // 4. GetActiveGuideForHero with whitespace / empty hero string defaults to Hero_Valkyrie and returns null
        HeroGuide? activeEmpty = await repo.GetActiveGuideForHeroAsync("   ", 123, "NonExistent", [1, 2, 3]);
        activeEmpty.Should().BeNull();

        // 5. Vote on non-existent guide
        (int up, int down, float rating) = await repo.VoteGuideAsync(999, 1);
        up.Should().Be(0);
        down.Should().Be(0);
        rating.Should().Be(0f);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Guide_MultipleGuidesSameHero_TierFallbackAndTieBreaking()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User u = CreateTestUser(1);
            Account author1 = new() { ID = 10, Name = "Author1", User = u };
            Account author2 = new() { ID = 20, Name = "Author2", User = u };
            Account authorUser = new() { ID = 30, Name = "PlayerTarget", User = u };

            // Public / Featured guides
            HeroGuide guideUnfeaturedHighRating = new()
            {
                ID = 1,
                HeroIdentifier = "Hero_Pyromancer",
                Name = "Unfeatured 99%",
                Author = author1,
                AuthorID = 10,
                Featured = false,
                Public = true,
                Rating = 0.99f,
                UpVotes = 500
            };
            HeroGuide guideFeaturedLowVotes = new()
            {
                ID = 2,
                HeroIdentifier = "Hero_Pyromancer",
                Name = "Featured 70% 10 votes",
                Author = author1,
                AuthorID = 10,
                Featured = true,
                Public = true,
                Rating = 0.70f,
                UpVotes = 10
            };
            HeroGuide guideFeaturedHighVotes = new()
            {
                ID = 3,
                HeroIdentifier = "Hero_Pyromancer",
                Name = "Featured 70% 50 votes",
                Author = author2,
                AuthorID = 20,
                Featured = true,
                Public = true,
                Rating = 0.70f,
                UpVotes = 50
            };

            // User authored guides (one older, one newer)
            HeroGuide userAuthoredOld = new()
            {
                ID = 4,
                HeroIdentifier = "Hero_Pyromancer",
                Name = "User Authored Old",
                Author = authorUser,
                AuthorID = 30,
                Featured = false,
                Public = false,
                Rating = 0.50f,
                TimestampCreated = TimeProvider.System.GetUtcNow().AddDays(-10)
            };
            HeroGuide userAuthoredNew = new()
            {
                ID = 5,
                HeroIdentifier = "Hero_Pyromancer",
                Name = "User Authored New",
                Author = authorUser,
                AuthorID = 30,
                Featured = false,
                Public = false,
                Rating = 0.60f,
                TimestampCreated = TimeProvider.System.GetUtcNow().AddDays(-1),
                TimestampLastUpdated = TimeProvider.System.GetUtcNow()
            };

            // Designated default guide
            HeroGuide userDesignatedDefault = new()
            {
                ID = 6,
                HeroIdentifier = "Hero_Pyromancer",
                Name = "User Designated Default",
                Author = author1,
                AuthorID = 10,
                Featured = false,
                Public = true,
                Rating = 0.80f
            };

            db.Users.Add(u);
            db.Accounts.AddRange(author1, author2, authorUser);
            db.HeroGuides.AddRange(guideUnfeaturedHighRating, guideFeaturedLowVotes, guideFeaturedHighVotes, userAuthoredOld, userAuthoredNew, userDesignatedDefault);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db);

            // 1. Tier 1: DefaultGuideIDs is provided -> Returns guide #6 regardless of authored or featured guides
            HeroGuide? tier1Result = await repo.GetActiveGuideForHeroAsync("Hero_Pyromancer", 30, "PlayerTarget", [6]);
            tier1Result.Should().NotBeNull();
            tier1Result!.ID.Should().Be(6);

            // 2. Tier 2: No DefaultGuideIDs provided -> Returns most recently updated authored guide #5
            HeroGuide? tier2Result = await repo.GetActiveGuideForHeroAsync("Hero_Pyromancer", 30, "PlayerTarget", null);
            tier2Result.Should().NotBeNull();
            tier2Result!.ID.Should().Be(5);

            // 3. Tier 3: Other user with no defaults and no authored guides -> Featured trumps non-featured, higher upvotes trumps lower
            HeroGuide? tier3Result = await repo.GetActiveGuideForHeroAsync("Hero_Pyromancer", 999, "Stranger", null);
            tier3Result.Should().NotBeNull();
            tier3Result!.ID.Should().Be(3); // Featured #3 (50 votes) beats Featured #2 (10 votes) and Unfeatured #1 (99% rating)
        }
    }

    [Fact]
    public async Task Guide_DefaultGuideDeduplication_RemovesAllSameHeroGuidesAndHandlesToggles()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User u = CreateTestUser(1);
            Account acc = new()
            {
                ID = 100,
                Name = "GuideManager",
                User = u,
                DefaultGuideIDs = [10, 11, 20, 30] // 10 and 11 are Valkyrie, 20 is Flint, 30 is Devourer
            };

            HeroGuide valk1 = new() { ID = 10, HeroIdentifier = "Hero_Valkyrie", Author = acc, AuthorID = 100 };
            HeroGuide valk2 = new() { ID = 11, HeroIdentifier = "Hero_Valkyrie", Author = acc, AuthorID = 100 };
            HeroGuide valk3 = new() { ID = 12, HeroIdentifier = "Hero_Valkyrie", Author = acc, AuthorID = 100 };
            HeroGuide flint = new() { ID = 20, HeroIdentifier = "Hero_FlintBeastwood", Author = acc, AuthorID = 100 };
            HeroGuide dev = new() { ID = 30, HeroIdentifier = "Hero_Devourer", Author = acc, AuthorID = 100 };

            db.Users.Add(u);
            db.Accounts.Add(acc);
            db.HeroGuides.AddRange(valk1, valk2, valk3, flint, dev);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db);

            // Set valk3 (ID 12) as default: should remove 10 and 11, retain 20 and 30, add 12
            int setResult = await repo.SetDefaultGuideAsync(100, 12, true);
            setResult.Should().Be(12);

            Account? accAfter = await db.Accounts.FindAsync(100);
            accAfter.Should().NotBeNull();
            accAfter!.DefaultGuideIDs.Should().BeEquivalentTo([20, 30, 12]);

            // Unset valk3 (ID 12): should remove 12, leaving [20, 30]
            int unsetResult = await repo.SetDefaultGuideAsync(100, 12, false);
            unsetResult.Should().Be(-1);

            Account? accUnset = await db.Accounts.FindAsync(100);
            accUnset!.DefaultGuideIDs.Should().BeEquivalentTo([20, 30]);

            // Non-existent account returns -1
            int missingAcc = await repo.SetDefaultGuideAsync(9999, 12, true);
            missingAcc.Should().Be(-1);
        }
    }

    [Fact]
    public async Task Guide_VotingRatingCalculations_MathematicalPrecision()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User u = CreateTestUser(1);
            Account acc = new() { ID = 200, Name = "Voter", User = u };
            HeroGuide guide = new()
            {
                ID = 555,
                Name = "MathGuide",
                HeroIdentifier = "Hero_Chronos",
                Author = acc,
                AuthorID = 200,
                UpVotes = 0,
                DownVotes = 0,
                Rating = 0.0f
            };

            db.Users.Add(u);
            db.Accounts.Add(acc);
            db.HeroGuides.Add(guide);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db);

            // 1. First vote: +1 (1 up, 0 down -> 1.0)
            (int up1, int down1, float rating1) = await repo.VoteGuideAsync(555, 1);
            up1.Should().Be(1);
            down1.Should().Be(0);
            rating1.Should().Be(1.0f);

            // 2. Second vote: -1 (1 up, 1 down -> 0.50)
            (int up2, int down2, float rating2) = await repo.VoteGuideAsync(555, -1);
            up2.Should().Be(1);
            down2.Should().Be(1);
            rating2.Should().Be(0.50f);

            // 3. Third vote: -1 (1 up, 2 down -> 1/3 = 0.33)
            (int up3, int down3, float rating3) = await repo.VoteGuideAsync(555, -1);
            up3.Should().Be(1);
            down3.Should().Be(2);
            rating3.Should().Be(0.33f);

            // 4. Fourth vote: -1 (1 up, 3 down -> 1/4 = 0.25)
            (int up4, int down4, float rating4) = await repo.VoteGuideAsync(555, 0); // voteVal <= 0 counts as downvote
            up4.Should().Be(1);
            down4.Should().Be(3);
            rating4.Should().Be(0.25f);

            // 5. Fifth vote: +1 (2 up, 3 down -> 2/5 = 0.40)
            (int up5, int down5, float rating5) = await repo.VoteGuideAsync(555, 10);
            up5.Should().Be(2);
            down5.Should().Be(3);
            rating5.Should().Be(0.40f);
        }
    }

    #endregion

    #region 2. Match History Table Filtering & Award Summary Nickname Stress Tests

    [Fact]
    public async Task MatchHistory_TableFilterCases_RankedCampaignMidwarsRiftwarsAndFallbacks()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            // Seed matches
            db.MatchStatistics.AddRange(
                new MatchStatistics { ID = 1, MatchID = 100, Map = "caldavar", GameMode = "caldavar" },
                new MatchStatistics { ID = 2, MatchID = 200, Map = "midwars", GameMode = "midwars" },
                new MatchStatistics { ID = 3, MatchID = 300, Map = "midwars", GameMode = "midwars" },
                new MatchStatistics { ID = 4, MatchID = 400, Map = "caldavar", GameMode = "casual" },
                new MatchStatistics { ID = 5, MatchID = 500, Map = "riftwars", GameMode = "riftwars" }
            );

            // Seed player statistics for account 50
            db.PlayerStatistics.AddRange(
                new PlayerStatistics { ID = 1, MatchID = 100, AccountID = 50, AccountName = "TestPlayer", RankedMatch = 1, PublicMatch = 0, HeroKills = 10 },
                new PlayerStatistics { ID = 2, MatchID = 200, AccountID = 50, AccountName = "TestPlayer", RankedMatch = 1, PublicMatch = 0, HeroKills = 20 },
                new PlayerStatistics { ID = 3, MatchID = 300, AccountID = 50, AccountName = "TestPlayer", RankedMatch = 1, PublicMatch = 0, HeroKills = 30 },
                new PlayerStatistics { ID = 4, MatchID = 400, AccountID = 50, AccountName = "TestPlayer", RankedMatch = 0, PublicMatch = 1, HeroKills = 40 },
                new PlayerStatistics { ID = 5, MatchID = 500, AccountID = 50, AccountName = "TestPlayer", RankedMatch = 0, PublicMatch = 0, HeroKills = 50 }
            );

            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db);

            // 1. "ranked" / "campaign" filter with currentSeason = "1" -> matches only Match 100 (Caldavar ranked)
            IReadOnlyList<PlayerMatchRecordDTO> ranked = await repo.GetPlayerMatchHistoryOverviewAsync(50, "TestPlayer", "ranked", "1");
            ranked.Should().HaveCount(1);
            ranked[0].MatchID.Should().Be(100);

            // 2. "midwars" / "other" filter -> matches Match 200 and 300
            IReadOnlyList<PlayerMatchRecordDTO> midwars = await repo.GetPlayerMatchHistoryOverviewAsync(50, "TestPlayer", "ranked_midwars", null);
            midwars.Should().HaveCount(2);
            midwars.Select(m => m.MatchID).Should().BeEquivalentTo([200, 300]);

            // 3. "riftwars" filter -> matches Match 500
            IReadOnlyList<PlayerMatchRecordDTO> riftwars = await repo.GetPlayerMatchHistoryOverviewAsync(50, "TestPlayer", "riftwars", null);
            riftwars.Should().HaveCount(1);
            riftwars[0].MatchID.Should().Be(500);

            // 4. "casual" / "public" filter -> matches Match 400 (PublicMatch=1) and Match 500 (RankedMatch=0)
            IReadOnlyList<PlayerMatchRecordDTO> casual = await repo.GetPlayerMatchHistoryOverviewAsync(50, "TestPlayer", "public", null);
            casual.Should().HaveCount(2);
            casual.Select(m => m.MatchID).Should().BeEquivalentTo([400, 500]);

            // 5. Unknown table name ("custom_nonexistent") -> maps to nonexistent map, returning 0 matches
            IReadOnlyList<PlayerMatchRecordDTO> unknownTable = await repo.GetPlayerMatchHistoryOverviewAsync(50, "TestPlayer", "custom_nonexistent", null);
            unknownTable.Should().BeEmpty();

            // 6. Null table -> defaults to RankedFoC (Caldavar ranked), returning 1 match
            IReadOnlyList<PlayerMatchRecordDTO> nullTable = await repo.GetPlayerMatchHistoryOverviewAsync(50, "TestPlayer", null, null);
            nullTable.Should().HaveCount(1);

            // 7. Season filter with season_normal -> matches ranked Caldavar match 100
            IReadOnlyList<PlayerMatchRecordDTO> seasonFallback = await repo.GetPlayerMatchHistoryOverviewAsync(50, "TestPlayer", "season_normal", "0");
            seasonFallback.Should().HaveCount(1);
        }
    }

    [Fact]
    public async Task MatchHistory_PlayerAwardSummary_NicknameMatchingPermutations()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            db.PlayerStatistics.AddRange(
                // 1. Exact ID and exact Nickname
                new PlayerStatistics { ID = 1, MatchID = 1, AccountID = 100, AccountName = "KongorHero" },
                // 2. Different AccountID, Clan tag bracket prefix: "[CLAN]KongorHero"
                new PlayerStatistics { ID = 2, MatchID = 2, AccountID = 200, AccountName = "[CLAN]KongorHero" },
                // 3. Different AccountID, Lowercase clan tag and nickname: "[clan]kongorhero"
                new PlayerStatistics { ID = 3, MatchID = 3, AccountID = 300, AccountName = "[clan]kongorhero" },
                // 4. Same AccountID (100) with a previous nickname
                new PlayerStatistics { ID = 4, MatchID = 4, AccountID = 100, AccountName = "OldSmurfName" },
                // 5. Completely different player
                new PlayerStatistics { ID = 5, MatchID = 5, AccountID = 999, AccountName = "UnrelatedGuy" },
                // 6. Name containing nickname as prefix, but not suffix ("KongorHeroExtra") -> should NOT match suffix rule
                new PlayerStatistics { ID = 6, MatchID = 6, AccountID = 888, AccountName = "KongorHeroExtra" }
            );
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db);

            IReadOnlyList<PlayerStatistics> awardStats = await repo.GetPlayerStatisticsForAwardSummaryAsync(
                accountId: 100,
                accountName: "KongorHero",
                cleanName: "KongorHero"
            );

            awardStats.Should().HaveCount(4);
            awardStats.Select(ps => ps.ID).Should().BeEquivalentTo([1, 2, 3, 4]);
            awardStats.Should().NotContain(ps => ps.ID == 5);
            awardStats.Should().NotContain(ps => ps.ID == 6);
        }
    }

    #endregion

    #region 3. DI Scoping Verification of All 8 Repositories

    [Fact]
    public void DI_AddMerrickRepositories_ResolvesAllEightRepositoriesWithScopedLifetime()
    {
        ServiceCollection services = new();
        services.AddScoped(_ => TestDbContextFactory.CreateInMemoryContext());
        services.AddMerrickRepositories();

        ServiceProvider provider = services.BuildServiceProvider();

        using (IServiceScope scope1 = provider.CreateScope())
        {
            IAccountRepository accRepo1 = scope1.ServiceProvider.GetRequiredService<IAccountRepository>();
            IMatchHistoryRepository matchRepo1 = scope1.ServiceProvider.GetRequiredService<IMatchHistoryRepository>();
            IInventoryRepository invRepo1 = scope1.ServiceProvider.GetRequiredService<IInventoryRepository>();
            ISessionRepository sessRepo1 = scope1.ServiceProvider.GetRequiredService<ISessionRepository>();
            IClanRepository clanRepo1 = scope1.ServiceProvider.GetRequiredService<IClanRepository>();
            IFriendshipRepository friendRepo1 = scope1.ServiceProvider.GetRequiredService<IFriendshipRepository>();
            IStoreRepository storeRepo1 = scope1.ServiceProvider.GetRequiredService<IStoreRepository>();
            IGuideRepository guideRepo1 = scope1.ServiceProvider.GetRequiredService<IGuideRepository>();

            accRepo1.Should().BeOfType<AccountRepository>();
            matchRepo1.Should().BeOfType<MatchHistoryRepository>();
            invRepo1.Should().BeOfType<InventoryRepository>();
            sessRepo1.Should().BeOfType<SessionRepository>();
            clanRepo1.Should().BeOfType<ClanRepository>();
            friendRepo1.Should().BeOfType<FriendshipRepository>();
            storeRepo1.Should().BeOfType<StoreRepository>();
            guideRepo1.Should().BeOfType<HeroGuideRepository>();

            // Scoped identity check: resolving again in scope1 yields the same instance
            scope1.ServiceProvider.GetRequiredService<IClanRepository>().Should().BeSameAs(clanRepo1);
            scope1.ServiceProvider.GetRequiredService<IGuideRepository>().Should().BeSameAs(guideRepo1);
            scope1.ServiceProvider.GetRequiredService<IMatchHistoryRepository>().Should().BeSameAs(matchRepo1);
        }

        using (IServiceScope scope2 = provider.CreateScope())
        {
            IClanRepository clanRepo2 = scope2.ServiceProvider.GetRequiredService<IClanRepository>();
            IGuideRepository guideRepo2 = scope2.ServiceProvider.GetRequiredService<IGuideRepository>();
            IMatchHistoryRepository matchRepo2 = scope2.ServiceProvider.GetRequiredService<IMatchHistoryRepository>();

            // Different scopes should have non-null, properly configured instances
            clanRepo2.Should().NotBeNull();
            guideRepo2.Should().NotBeNull();
            matchRepo2.Should().NotBeNull();
        }
    }

    #endregion
    #endregion
}



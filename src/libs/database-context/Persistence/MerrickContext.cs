// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Entities;
using MERRICK.DatabaseContext.Entities.Lore;
using MERRICK.DatabaseContext.Entities.Quests;
using MERRICK.DatabaseContext.Entities.Statistics;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using UserRoles = MERRICK.DatabaseContext.Constants.UserRoles;

namespace MERRICK.DatabaseContext.Persistence;

public sealed class MerrickContext : DbContext
{
    public const string MetadataSchema = "meta";
    public const string DefaultSchema = "data";
    public const string CoreSchema = "core";
    public const string AuthenticationSchema = "nexus_auth";
    public const string StatisticsSchema = "stat";
    public const string MiscellaneousSchema = "misc";

    public MerrickContext(DbContextOptions options) : base(options)
    {
        ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges;
        ChangeTracker.DeleteOrphansTiming = CascadeTiming.OnSaveChanges;

        if (Database.IsRelational())
            Database.SetCommandTimeout(
                60); // 1 Minute - Helps Prevent Migrations From Timing Out When Many Records Need To Update
    }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountPeer> AccountPeers => Set<AccountPeer>();
    public DbSet<AccountSelectedStoreItem> AccountSelectedStoreItems => Set<AccountSelectedStoreItem>();
    public DbSet<AccountIpAddress> AccountIpAddresses => Set<AccountIpAddress>();
    public DbSet<AccountMacAddress> AccountMacAddresses => Set<AccountMacAddress>();
    public DbSet<AccountBlockedPhrase> AccountBlockedPhrases => Set<AccountBlockedPhrase>();
    public DbSet<AccountStatistics> AccountStatistics => Set<AccountStatistics>();
    public DbSet<AccountMapStatistics> AccountMapStatistics => Set<AccountMapStatistics>();
    public DbSet<Clan> Clans => Set<Clan>();
    public DbSet<HeroGuide> HeroGuides => Set<HeroGuide>();
    public DbSet<HeroGuideItem> HeroGuideItems => Set<HeroGuideItem>();
    public DbSet<HeroGuideAbility> HeroGuideAbilities => Set<HeroGuideAbility>();
    public DbSet<AccountGuidePreference> AccountGuidePreferences => Set<AccountGuidePreference>();
    public DbSet<HeroDifficultyVote> HeroDifficultyVotes => Set<HeroDifficultyVote>();
    public DbSet<HeroDetail> HeroDetails => Set<HeroDetail>();
    public DbSet<HeroItem> HeroItems => Set<HeroItem>();
    public DbSet<HostApplication> HostApplications => Set<HostApplication>();
    public DbSet<StreamerApplication> StreamerApplications => Set<StreamerApplication>();
    public DbSet<MatchRecord> MatchRecords => Set<MatchRecord>();
    public DbSet<MatchStatistics> MatchStatistics => Set<MatchStatistics>();
    public DbSet<MatchBan> MatchBans => Set<MatchBan>();
    public DbSet<MatchEvidence> MatchEvidence => Set<MatchEvidence>();
    public DbSet<Mastery> Masteries => Set<Mastery>();
    public DbSet<HeroMasteryExperience> HeroMasteryExperiences => Set<HeroMasteryExperience>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<PlayerReport> PlayerReports => Set<PlayerReport>();
    public DbSet<PlayerStatistics> PlayerStatistics => Set<PlayerStatistics>();
    public DbSet<MatchPlayerInventory> MatchPlayerInventories => Set<MatchPlayerInventory>();
    public DbSet<MatchItemEvent> MatchItemEvents => Set<MatchItemEvent>();
    public DbSet<MatchAbilityEvent> MatchAbilityEvents => Set<MatchAbilityEvent>();
    public DbSet<MatchFrag> MatchFrags => Set<MatchFrag>();
    public DbSet<MatchFragAssist> MatchFragAssists => Set<MatchFragAssist>();
    public DbSet<HeroItemComponent> HeroItemComponents => Set<HeroItemComponent>();
    public DbSet<RedeemableCode> RedeemableCodes => Set<RedeemableCode>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Token> Tokens => Set<Token>();
    public DbSet<SystemTelemetryMetrics> SystemTelemetryMetrics => Set<SystemTelemetryMetrics>();
    public DbSet<DeveloperIssue> DeveloperIssues => Set<DeveloperIssue>();
    public DbSet<MatchmakingQueueWaitEntry> MatchmakingQueueWaitEntries => Set<MatchmakingQueueWaitEntry>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserWebReward> UserWebRewards => Set<UserWebReward>();
    public DbSet<StoreItem> StoreItems => Set<StoreItem>();
    public DbSet<EconomyTransaction> EconomyTransactions => Set<EconomyTransaction>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<UserInventory> UserInventory => Set<UserInventory>();
    public DbSet<UserInventory> UserInventories => Set<UserInventory>();
    public DbSet<AccountSession> AccountSessions => Set<AccountSession>();
    public DbSet<LoreArc> LoreArcs => Set<LoreArc>();
    public DbSet<LoreComic> LoreComics => Set<LoreComic>();
    public DbSet<LorePage> LorePages => Set<LorePage>();
    public DbSet<LoreCredit> LoreCredits => Set<LoreCredit>();
    public DbSet<LoreRating> LoreRatings => Set<LoreRating>();
    public DbSet<UserInvite> UserInvites => Set<UserInvite>();
    public DbSet<InviteSystemAuditLog> InviteSystemAuditLogs => Set<InviteSystemAuditLog>();
    public DbSet<WarEffortCycle> WarEffortCycles => Set<WarEffortCycle>();
    public DbSet<QuestDefinition> QuestDefinitions => Set<QuestDefinition>();
    public DbSet<AccountQuestSlot> AccountQuestSlots => Set<AccountQuestSlot>();
    public DbSet<WarEffortBagMilestone> WarEffortBagMilestones => Set<WarEffortBagMilestone>();
    public DbSet<AccountWarEffortProgress> AccountWarEffortProgress => Set<AccountWarEffortProgress>();
    public DbSet<AccountWarEffortBagClaim> AccountWarEffortBagClaims => Set<AccountWarEffortBagClaim>();
    public DbSet<WarEffortCycleLeaderboardSnapshot> WarEffortCycleLeaderboardSnapshots => Set<WarEffortCycleLeaderboardSnapshot>();

    private void CascadeDeleteReverseFriendships()
    {
        HashSet<int> deletedAccountIds = ChangeTracker.Entries<Account>()
            .Where(e => e.State == EntityState.Deleted && e.Entity.ID != 0)
            .Select(e => e.Entity.ID)
            .ToHashSet();

        if (deletedAccountIds.Count == 0)
            return;

        List<EntityEntry<Friendship>> trackedReverseFriendships = ChangeTracker.Entries<Friendship>()
            .Where(e => e.State != EntityState.Deleted && (deletedAccountIds.Contains(e.Entity.FriendAccountID) || deletedAccountIds.Contains(e.Entity.AccountID)))
            .ToList();

        foreach (EntityEntry<Friendship> rf in trackedReverseFriendships)
        {
            rf.State = EntityState.Deleted;
        }

        // FriendAccountID uses DeleteBehavior.Restrict across providers; remove untracked reverse rows before SaveChanges
        {
            HashSet<int> trackedIds = ChangeTracker.Entries<Friendship>().Select(t => t.Entity.ID).Where(id => id != 0).ToHashSet();
            List<Friendship> untracked = Friendships
                .Where(f => (deletedAccountIds.Contains(f.FriendAccountID) || deletedAccountIds.Contains(f.AccountID)) && !trackedIds.Contains(f.ID))
                .ToList();
            if (untracked.Count > 0)
            {
                Friendships.RemoveRange(untracked);
            }
        }
    }

    public override int SaveChanges()
    {
        CascadeDeleteReverseFriendships();
        return base.SaveChanges();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        CascadeDeleteReverseFriendships();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private async Task CascadeDeleteReverseFriendshipsAsync(CancellationToken cancellationToken)
    {
        HashSet<int> deletedAccountIds = ChangeTracker.Entries<Account>()
            .Where(e => e.State == EntityState.Deleted && e.Entity.ID != 0)
            .Select(e => e.Entity.ID)
            .ToHashSet();

        if (deletedAccountIds.Count == 0)
            return;

        List<EntityEntry<Friendship>> trackedReverseFriendships = ChangeTracker.Entries<Friendship>()
            .Where(e => e.State != EntityState.Deleted && (deletedAccountIds.Contains(e.Entity.FriendAccountID) || deletedAccountIds.Contains(e.Entity.AccountID)))
            .ToList();

        foreach (EntityEntry<Friendship> rf in trackedReverseFriendships)
        {
            rf.State = EntityState.Deleted;
        }

        // FriendAccountID uses DeleteBehavior.Restrict across providers; remove untracked reverse rows before SaveChanges
        {
            HashSet<int> trackedIds = ChangeTracker.Entries<Friendship>().Select(t => t.Entity.ID).Where(id => id != 0).ToHashSet();
            List<Friendship> untracked = await Friendships
                .Where(f => (deletedAccountIds.Contains(f.FriendAccountID) || deletedAccountIds.Contains(f.AccountID)) && !trackedIds.Contains(f.ID))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (untracked.Count > 0)
            {
                Friendships.RemoveRange(untracked);
            }
        }
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await CascadeDeleteReverseFriendshipsAsync(cancellationToken).ConfigureAwait(false);
        return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await CascadeDeleteReverseFriendshipsAsync(cancellationToken).ConfigureAwait(false);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        string? caseInsensitiveCollation = null;

        if (Database.IsSqlServer())
        {
            modelBuilder.UseCollation("SQL_Latin1_General_CP1_CI_AS");
            caseInsensitiveCollation = "SQL_Latin1_General_CP1_CI_AS";
        }
        else if (Database.IsNpgsql())
        {
            modelBuilder.HasCollation("case_insensitive", locale: "und-u-ks-level2", provider: "icu", deterministic: false);
            caseInsensitiveCollation = "case_insensitive";
        }
        else
        {
            // Default fallback for InMemory and test providers
            caseInsensitiveCollation = "case_insensitive";
        }

        bool isNpgsql = Database.IsNpgsql();
        bool isSqlServer = Database.IsSqlServer();

        ConfigureSchemas(modelBuilder);

        ConfigureRoles(modelBuilder.Entity<Role>());
        ConfigureTokens(modelBuilder.Entity<Token>());
        ConfigureUsers(modelBuilder.Entity<User>(), caseInsensitiveCollation, isNpgsql);
        ConfigureUserInvites(modelBuilder.Entity<UserInvite>());
        ConfigureAccounts(modelBuilder.Entity<Account>(), caseInsensitiveCollation, isNpgsql);
        ConfigureAccountPeers(modelBuilder.Entity<AccountPeer>());
        ConfigureAccountSelectedStoreItems(modelBuilder.Entity<AccountSelectedStoreItem>(), caseInsensitiveCollation);
        ConfigureAccountIpAddresses(modelBuilder.Entity<AccountIpAddress>());
        ConfigureAccountMacAddresses(modelBuilder.Entity<AccountMacAddress>());
        ConfigureAccountBlockedPhrases(modelBuilder.Entity<AccountBlockedPhrase>(), caseInsensitiveCollation);
        ConfigureClans(modelBuilder.Entity<Clan>(), caseInsensitiveCollation, isNpgsql);
        ConfigureHeroGuides(modelBuilder.Entity<HeroGuide>(), caseInsensitiveCollation);
        ConfigureHeroGuideItems(modelBuilder.Entity<HeroGuideItem>(), caseInsensitiveCollation);
        ConfigureHeroGuideAbilities(modelBuilder.Entity<HeroGuideAbility>(), caseInsensitiveCollation);
        ConfigureAccountGuidePreferences(modelBuilder.Entity<AccountGuidePreference>());
        ConfigureHeroItems(modelBuilder.Entity<HeroItem>(), caseInsensitiveCollation);
        ConfigureHeroItemComponents(modelBuilder.Entity<HeroItemComponent>(), caseInsensitiveCollation);
        ConfigureMatchmakingQueueWaitEntries(modelBuilder.Entity<MatchmakingQueueWaitEntry>());
        ConfigurePlayerStatistics(modelBuilder.Entity<PlayerStatistics>(), isNpgsql);
        ConfigureMatchPlayerInventories(modelBuilder.Entity<MatchPlayerInventory>(), caseInsensitiveCollation);
        ConfigureMatchItemEvents(modelBuilder.Entity<MatchItemEvent>(), caseInsensitiveCollation);
        ConfigureMatchAbilityEvents(modelBuilder.Entity<MatchAbilityEvent>(), caseInsensitiveCollation);
        ConfigureMatchStatistics(modelBuilder.Entity<MatchStatistics>(), isNpgsql);
        ConfigureMatchFrags(modelBuilder.Entity<MatchFrag>());
        ConfigureMatchFragAssists(modelBuilder.Entity<MatchFragAssist>());
        ConfigureMatchBans(modelBuilder.Entity<MatchBan>(), caseInsensitiveCollation);
        ConfigureAccountStatistics(modelBuilder.Entity<AccountStatistics>());
        ConfigureAccountMapStatistics(modelBuilder.Entity<AccountMapStatistics>(), isNpgsql);
        ConfigurePlayerReports(modelBuilder.Entity<PlayerReport>());
        ConfigureMastery(modelBuilder.Entity<Mastery>());
        ConfigureHeroMasteryExperiences(modelBuilder.Entity<HeroMasteryExperience>(), caseInsensitiveCollation);
        ConfigureHostApplications(modelBuilder.Entity<HostApplication>(), isNpgsql, isSqlServer);
        ConfigureStreamerApplications(modelBuilder.Entity<StreamerApplication>());
        ConfigureUserWebRewards(modelBuilder.Entity<UserWebReward>());
        ConfigureStoreItems(modelBuilder.Entity<StoreItem>(), caseInsensitiveCollation);
        ConfigureEconomyTransactions(modelBuilder.Entity<EconomyTransaction>());
        ConfigureFriendships(modelBuilder.Entity<Friendship>());
        ConfigureUserInventory(modelBuilder.Entity<UserInventory>());
        ConfigureAccountSessions(modelBuilder.Entity<AccountSession>());
        ConfigureNotifications(modelBuilder.Entity<Notification>());
        ConfigureMatchEvidence(modelBuilder.Entity<MatchEvidence>());
        ConfigureWarEffort(modelBuilder);
    }

    private static void ConfigureStreamerApplications(EntityTypeBuilder<StreamerApplication> builder)
    {
        builder.HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserID)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(s => s.MainAccount)
            .WithMany()
            .HasForeignKey(s => s.MainAccountID)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => s.Status).HasDatabaseName("IX_StreamerApplications_Status");
        builder.HasIndex(s => s.MainAccountID).HasDatabaseName("IX_StreamerApplications_MainAccountID");
        builder.HasIndex(s => s.UserID).HasDatabaseName("IX_StreamerApplications_UserID");
    }

    private static void ConfigureHostApplications(EntityTypeBuilder<HostApplication> builder, bool isNpgsql = false, bool isSqlServer = false)
    {
        builder.HasOne(h => h.User)
            .WithMany()
            .HasForeignKey(h => h.UserID)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(h => h.MainAccount)
            .WithMany()
            .HasForeignKey(h => h.MainAccountID)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(h => h.HostAccount)
            .WithMany()
            .HasForeignKey(h => h.HostAccountID)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(h => h.Status).HasDatabaseName("IX_HostApplications_Status");
        builder.HasIndex(h => h.MainAccountID).HasDatabaseName("IX_HostApplications_MainAccountID");

        var approvedIdx = builder.HasIndex(h => new { h.MainAccountID, h.Status })
            .HasDatabaseName("IX_HostApplications_Approved");

        if (isNpgsql)
            approvedIdx.HasFilter("\"Status\" = 2");
        else if (isSqlServer)
            approvedIdx.HasFilter("[Status] = 2");
    }

    private static void ConfigurePlayerReports(EntityTypeBuilder<PlayerReport> builder)
    {
        builder.Property(r => r.ID).HasColumnName("Id");
        builder.HasOne(r => r.ReporterAccount)
            .WithMany(a => a.ReportsFiled)
            .HasForeignKey(r => r.ReporterAccountID)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(r => r.ReportedAccount)
            .WithMany(a => a.ReportsReceived)
            .HasForeignKey(r => r.ReportedAccountID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.Match)
            .WithMany()
            .HasPrincipalKey(m => m.MatchID)
            .HasForeignKey(r => r.MatchID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => r.ReporterAccountID).HasDatabaseName("IX_PlayerReports_ReporterAccountID");
        builder.HasIndex(r => r.ReportedAccountID).HasDatabaseName("IX_PlayerReports_ReportedAccountID");
        builder.HasIndex(r => r.MatchID).HasDatabaseName("IX_PlayerReports_MatchID");
        builder.HasIndex(r => new { r.Status, r.TimestampCreated })
            .HasDatabaseName("IX_PlayerReports_Status_TimestampCreated");
        builder.HasIndex(r => new { r.Reason, r.TimestampCreated })
            .HasDatabaseName("IX_PlayerReports_Reason_TimestampCreated");
    }

    private static void ConfigureClans(EntityTypeBuilder<Clan> builder, string? collation = null, bool isNpgsql = false)
    {
        if (!string.IsNullOrEmpty(collation))
        {
            builder.Property(c => c.Name).UseCollation(collation);
            builder.Property(c => c.Tag).UseCollation(collation);
        }

        builder.HasIndex(c => c.Name).IsUnique().HasDatabaseName("IX_Clans_Name");
        builder.HasIndex(c => c.Tag).IsUnique().HasDatabaseName("IX_Clans_Tag");

        if (isNpgsql)
        {
            builder.HasIndex(c => c.Name, "IX_Clans_Name_Trgm")
                .IsUnique(false)
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops")
                .HasDatabaseName("IX_Clans_Name_Trgm");
        }
    }

    private static void ConfigureHeroGuides(EntityTypeBuilder<HeroGuide> builder, string? collation = null)
    {
        if (!string.IsNullOrEmpty(collation))
        {
            builder.Property(g => g.HeroIdentifier).UseCollation(collation);
            builder.Property(g => g.HeroName).UseCollation(collation);
        }
        builder.HasOne(g => g.Author)
            .WithMany(a => a.AuthoredGuides)
            .HasForeignKey(g => g.AuthorID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(g => g.Items)
            .WithOne(i => i.Guide)
            .HasForeignKey(i => i.GuideID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(g => g.Abilities)
            .WithOne(a => a.Guide)
            .HasForeignKey(a => a.GuideID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(g => g.AuthorID).HasDatabaseName("IX_HeroGuides_AuthorID");
        builder.HasIndex(g => g.HeroIdentifier).HasDatabaseName("IX_HeroGuides_HeroIdentifier");
        builder.HasIndex(g => new { g.HeroIdentifier, g.Public })
            .HasDatabaseName("IX_HeroGuides_HeroIdentifier_Public");
    }

    private static void ConfigureHeroGuideItems(EntityTypeBuilder<HeroGuideItem> builder, string? collation = null)
    {
        if (!string.IsNullOrEmpty(collation))
            builder.Property(i => i.ItemCode).UseCollation(collation);

        builder.Property(i => i.ItemCode)
            .HasMaxLength(100)
            .IsRequired();

        builder.HasOne(i => i.Guide)
            .WithMany(g => g.Items)
            .HasForeignKey(i => i.GuideID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(i => i.GuideID)
            .HasDatabaseName("IX_HeroGuideItems_GuideID");

        builder.HasIndex(i => new { i.GuideID, i.SlotType, i.OrderIndex })
            .IsUnique()
            .HasDatabaseName("UQ_HeroGuideItems_GuideID_SlotType_OrderIndex");
    }

    private static void ConfigureHeroGuideAbilities(EntityTypeBuilder<HeroGuideAbility> builder, string? collation = null)
    {
        if (!string.IsNullOrEmpty(collation))
            builder.Property(a => a.AbilityCode).UseCollation(collation);

        builder.Property(a => a.AbilityCode)
            .HasMaxLength(100);

        builder.HasOne(a => a.Guide)
            .WithMany(g => g.Abilities)
            .HasForeignKey(a => a.GuideID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => a.GuideID)
            .HasDatabaseName("IX_HeroGuideAbilities_GuideID");

        builder.HasIndex(a => new { a.GuideID, a.LevelSequence })
            .IsUnique()
            .HasDatabaseName("UQ_HeroGuideAbilities_GuideID_LevelSequence");
    }

    private static void ConfigureAccountGuidePreferences(EntityTypeBuilder<AccountGuidePreference> builder)
    {
        builder.HasOne(p => p.Account)
            .WithMany(a => a.GuidePreferences)
            .HasForeignKey(p => p.AccountID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(p => p.Guide)
            .WithMany()
            .HasForeignKey(p => p.GuideID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => p.AccountID).HasDatabaseName("IX_AccountGuidePreferences_AccountID");
        builder.HasIndex(p => p.GuideID).HasDatabaseName("IX_AccountGuidePreferences_GuideID");
        builder.HasIndex(p => new { p.AccountID, p.GuideID })
            .IsUnique()
            .HasDatabaseName("UQ_AccountGuidePreferences_AccountID_GuideID");
    }

    private static void ConfigureHeroItems(EntityTypeBuilder<HeroItem> builder, string? collation = null)
    {
        if (!string.IsNullOrEmpty(collation))
        {
            builder.Property(i => i.Code).UseCollation(collation);
            builder.Property(i => i.Name).UseCollation(collation);
        }

        builder.Property(i => i.Cost)
            .IsRequired();

        builder.HasIndex(i => i.Code)
            .IsUnique()
            .HasDatabaseName("IX_HeroItems_Code");

        builder.HasIndex(i => i.Name)
            .HasDatabaseName("IX_HeroItems_Name");

        builder.HasMany(i => i.Components)
            .WithOne(c => c.ParentItem)
            .HasForeignKey(c => c.ParentItemID)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureHeroItemComponents(EntityTypeBuilder<HeroItemComponent> builder, string? collation = null)
    {
        builder.ToTable("HeroItemComponents", MiscellaneousSchema);
        builder.HasKey(c => c.ID);
        builder.Property(c => c.ID).HasColumnName("ID");

        if (!string.IsNullOrEmpty(collation))
            builder.Property(c => c.ComponentItemCode).UseCollation(collation);

        builder.Property(c => c.ComponentItemCode).HasMaxLength(100).IsRequired();

        builder.HasOne(c => c.ParentItem)
            .WithMany(i => i.Components)
            .HasForeignKey(c => c.ParentItemID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => c.ParentItemID).HasDatabaseName("IX_HeroItemComponents_ParentItemID");
        builder.HasIndex(c => c.ComponentItemCode).HasDatabaseName("IX_HeroItemComponents_ComponentItemCode");
        builder.HasIndex(c => new { c.ParentItemID, c.ComponentItemCode })
            .IsUnique()
            .HasDatabaseName("UQ_HeroItemComponents_ParentItemID_ComponentItemCode");
    }

    private static void ConfigureMatchmakingQueueWaitEntries(EntityTypeBuilder<MatchmakingQueueWaitEntry> builder)
    {
        builder.HasIndex(e => new { e.QueueType, e.TimestampMatched })
            .HasDatabaseName("IX_MatchmakingQueueWaitEntries_QueueType_TimestampMatched");
    }

    private static void ConfigureAccountStatistics(EntityTypeBuilder<AccountStatistics> builder)
    {
        builder.Property(s => s.AccountID).ValueGeneratedNever();
        builder.HasIndex(s => s.AccountID).HasDatabaseName("IX_AccountStatistics_AccountID");
        builder.HasOne(s => s.Account)
            .WithMany(a => a.Statistics)
            .HasForeignKey(s => s.AccountID)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureAccountMapStatistics(EntityTypeBuilder<AccountMapStatistics> builder, bool isNpgsql = false)
    {
        builder.HasOne(s => s.Account)
            .WithMany()
            .HasForeignKey(s => s.AccountID)
            .OnDelete(DeleteBehavior.Cascade);

        if (isNpgsql)
        {
            builder.Property(s => s.HeroPlayCountsJson).HasColumnType("jsonb");
            builder.Property(s => s.CustomTotals).HasColumnType("jsonb");
            builder.Property(s => s.BestRecords).HasColumnType("jsonb");
        }
    }

    private static void ConfigureNotifications(EntityTypeBuilder<Notification> builder)
    {
        builder.HasOne(n => n.Account)
            .WithMany(a => a.Notifications)
            .HasForeignKey(n => n.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(n => n.AccountId).HasDatabaseName("IX_Notifications_AccountId");
    }

    private static void ConfigureMatchEvidence(EntityTypeBuilder<MatchEvidence> builder)
    {
        builder.HasOne(e => e.Match)
            .WithMany()
            .HasPrincipalKey(m => m.MatchID)
            .HasForeignKey(e => e.MatchID)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(e => e.MatchID).HasDatabaseName("IX_MatchEvidence_MatchID");
    }

    private static void ConfigureSchemas(ModelBuilder builder)
    {
        builder.HasDefaultSchema(DefaultSchema);

        builder.Entity<Account>().ToTable("Accounts", CoreSchema);
        builder.Entity<AccountSelectedStoreItem>().ToTable("AccountSelectedStoreItems", CoreSchema);
        builder.Entity<AccountIpAddress>().ToTable("AccountIpAddresses", CoreSchema);
        builder.Entity<AccountMacAddress>().ToTable("AccountMacAddresses", CoreSchema);
        builder.Entity<AccountBlockedPhrase>().ToTable("AccountBlockedPhrases", CoreSchema);
        builder.Entity<AccountStatistics>().ToTable("AccountStatistics", DefaultSchema);
        builder.Entity<AccountMapStatistics>().ToTable("AccountMapStatistics", DefaultSchema);
        builder.Entity<Clan>().ToTable("Clans", CoreSchema);
        builder.Entity<HeroGuide>().ToTable("HeroGuides", MiscellaneousSchema);
        builder.Entity<HeroGuideItem>().ToTable("HeroGuideItems", MiscellaneousSchema);
        builder.Entity<HeroGuideAbility>().ToTable("HeroGuideAbilities", MiscellaneousSchema);
        builder.Entity<HeroDifficultyVote>().ToTable("HeroDifficultyVotes", MiscellaneousSchema);
        builder.Entity<HeroDetail>().ToTable("HeroDetails", MiscellaneousSchema);
        builder.Entity<HeroItem>().ToTable("HeroItems", MiscellaneousSchema);
        builder.Entity<HeroItemComponent>().ToTable("HeroItemComponents", MiscellaneousSchema);
        builder.Entity<HostApplication>().ToTable("HostApplications", CoreSchema);
        builder.Entity<StreamerApplication>().ToTable("StreamerApplications", CoreSchema);
        builder.Entity<MatchStatistics>().ToTable("MatchStatistics", StatisticsSchema);
        builder.Entity<MatchFrag>().ToTable("MatchFrags", StatisticsSchema);
        builder.Entity<MatchFragAssist>().ToTable("MatchFragAssists", StatisticsSchema);
        builder.Entity<Mastery>().ToTable("Masteries", StatisticsSchema);
        builder.Entity<HeroMasteryExperience>().ToTable("HeroMasteryExperiences", StatisticsSchema);
        builder.Entity<Notification>().ToTable("Notifications", MiscellaneousSchema);
        builder.Entity<PlayerReport>().ToTable("PlayerReports", MiscellaneousSchema);
        builder.Entity<MatchEvidence>().ToTable("MatchEvidence", MiscellaneousSchema);
        builder.Entity<PlayerStatistics>().ToTable("PlayerStatistics", StatisticsSchema);
        builder.Entity<MatchPlayerInventory>().ToTable("MatchPlayerInventories", StatisticsSchema);
        builder.Entity<MatchItemEvent>().ToTable("MatchItemEvents", StatisticsSchema);
        builder.Entity<MatchAbilityEvent>().ToTable("MatchAbilityEvents", StatisticsSchema);
        builder.Entity<RedeemableCode>().ToTable("RedeemableCodes", MiscellaneousSchema);
        builder.Entity<Role>().ToTable("Roles", AuthenticationSchema);
        builder.Entity<Token>().ToTable("Tokens", AuthenticationSchema);
        builder.Entity<User>().ToTable("Users", CoreSchema);
        builder.Entity<UserWebReward>().ToTable("UserWebRewards", MiscellaneousSchema);
        builder.Entity<StoreItem>().ToTable("StoreItems", DefaultSchema);
        builder.Entity<EconomyTransaction>().ToTable("EconomyTransactions", CoreSchema);
        builder.Entity<Friendship>().ToTable("Friendships", CoreSchema);
        builder.Entity<UserInventory>().ToTable("UserInventory", CoreSchema);
        builder.Entity<AccountSession>().ToTable("AccountSessions", CoreSchema);
        builder.Entity<SystemTelemetryMetrics>().ToTable("SystemTelemetryMetrics", CoreSchema);
        builder.Entity<DeveloperIssue>(b =>
        {
            b.ToTable("DeveloperIssues", CoreSchema);
            b.HasIndex(x => x.Fingerprint).IsUnique().HasDatabaseName("IX_DeveloperIssues_Fingerprint");
            b.HasIndex(x => x.Status).HasDatabaseName("IX_DeveloperIssues_Status");
            b.HasIndex(x => x.Subsystem).HasDatabaseName("IX_DeveloperIssues_Subsystem");
            b.HasIndex(x => new { x.Status, x.Severity }).HasDatabaseName("IX_DeveloperIssues_Status_Severity");
        });
        builder.Entity<MatchmakingQueueWaitEntry>().ToTable("MatchmakingQueueWaitEntries", DefaultSchema);
        builder.Entity<LoreArc>().ToTable("LoreArcs", MiscellaneousSchema);
        builder.Entity<LoreComic>().ToTable("LoreComics", MiscellaneousSchema);
        builder.Entity<LorePage>().ToTable("LorePages", MiscellaneousSchema);
        builder.Entity<LoreCredit>().ToTable("LoreCredits", MiscellaneousSchema);
        builder.Entity<LoreRating>().ToTable("LoreRatings", MiscellaneousSchema);
    }

    private static void ConfigureUserWebRewards(EntityTypeBuilder<UserWebReward> builder)
    {
        builder.HasOne(r => r.User)
            .WithMany(u => u.WebRewards)
            .HasForeignKey(r => r.UserID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => r.UserID).HasDatabaseName("IX_UserWebRewards_UserID");
        builder.HasIndex(r => new { r.UserID, r.RewardType }).HasDatabaseName("IX_UserWebRewards_UserID_RewardType");
        builder.HasIndex(r => new { r.UserID, r.IsEquipped }).HasDatabaseName("IX_UserWebRewards_UserID_IsEquipped");
    }

    private static void ConfigureRoles(EntityTypeBuilder<Role> builder)
    {
        builder.HasData(
            new Role { ID = 1, Name = UserRoles.Administrator },
            new Role { ID = 2, Name = UserRoles.User });
    }

    private static void ConfigureTokens(EntityTypeBuilder<Token> builder)
    {
        builder.HasIndex(t => t.Value).IsUnique().HasDatabaseName("IX_Tokens_Value");
        builder.HasIndex(t => t.UserID).HasDatabaseName("IX_Tokens_UserID");
    }

    private static void ConfigureUsers(EntityTypeBuilder<User> builder, string? collation = null, bool isNpgsql = false)
    {
        if (!string.IsNullOrEmpty(collation))
        {
            builder.Property(user => user.EmailAddress).UseCollation(collation);
            builder.Property(user => user.DiscordUsername).UseCollation(collation);
        }

        builder.HasIndex(user => user.DiscordUsername).HasDatabaseName("IX_Users_DiscordUsername");

        if (isNpgsql)
        {
            builder.Property<uint>("xmin").IsRowVersion();
        }

        if (isNpgsql)
        {
            builder.HasIndex(user => user.DiscordUsername, "IX_Users_DiscordUsername_Trgm")
                .IsUnique(false)
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops")
                .HasDatabaseName("IX_Users_DiscordUsername_Trgm");
        }

        builder.HasOne(u => u.Role)
            .WithMany()
            .HasForeignKey(u => u.RoleID)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(u => u.InvitedByUser)
            .WithMany()
            .HasForeignKey(u => u.InvitedByUserID)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
    }

    private static void ConfigureUserInvites(EntityTypeBuilder<UserInvite> builder)
    {
        builder.HasIndex(i => i.Code).IsUnique();
        builder.HasIndex(i => i.CreatedByUserID);
        builder.HasIndex(i => i.RedeemedByUserID);

        builder.HasOne(i => i.CreatedByUser)
            .WithMany(u => u.SentInvites)
            .HasForeignKey(i => i.CreatedByUserID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(i => i.RedeemedByUser)
            .WithMany()
            .HasForeignKey(i => i.RedeemedByUserID)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(i => i.RedeemedByAccount)
            .WithMany()
            .HasForeignKey(i => i.RedeemedByAccountID)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
    }

    private static void ConfigureAccounts(EntityTypeBuilder<Account> builder, string? collation = null, bool isNpgsql = false)
    {
        if (!string.IsNullOrEmpty(collation))
            builder.Property(account => account.Name).UseCollation(collation);
        builder.HasIndex(account => account.Name).HasDatabaseName("IX_Accounts_Name");
        builder.HasIndex(account => account.UserID).HasDatabaseName("IX_Accounts_UserID");
        builder.HasIndex(account => account.OwnerAccountID).HasDatabaseName("IX_Accounts_OwnerAccountID");
        builder.HasIndex(account => account.ClanID).HasDatabaseName("IX_Accounts_ClanID");

        if (isNpgsql)
        {
            builder.HasIndex(account => account.Name, "IX_Accounts_Name_Trgm")
                .IsUnique(false)
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops")
                .HasDatabaseName("IX_Accounts_Name_Trgm");
        }

        builder.HasOne(account => account.User)
            .WithMany(user => user.Accounts)
            .HasForeignKey(account => account.UserID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(account => account.Clan)
            .WithMany(clan => clan.Members)
            .HasForeignKey(account => account.ClanID)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(account => account.OwnerAccount)
            .WithMany(account => account.HostedNodes)
            .HasForeignKey(account => account.OwnerAccountID)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(account => account.Peers)
            .WithOne(peer => peer.Account)
            .HasForeignKey(peer => peer.AccountID)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureAccountPeers(EntityTypeBuilder<AccountPeer> builder)
    {
        builder.ToTable("AccountPeers", CoreSchema);

        builder.HasOne(p => p.Account)
            .WithMany(a => a.Peers)
            .HasForeignKey(p => p.AccountID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(p => p.PeerAccount)
            .WithMany()
            .HasForeignKey(p => p.PeerAccountID)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => new { p.AccountID, p.PeerAccountID, p.Type })
            .IsUnique()
            .HasDatabaseName("IX_AccountPeers_AccountID_PeerAccountID_Type");

        builder.HasIndex(p => p.PeerAccountID)
            .HasDatabaseName("IX_AccountPeers_PeerAccountID");
    }

    private static void ConfigureStoreItems(EntityTypeBuilder<StoreItem> builder, string? collation = null)
    {
        if (!string.IsNullOrEmpty(collation))
        {
            builder.Property(item => item.Code).UseCollation(collation);
            builder.Property(item => item.Name).UseCollation(collation);
        }
        builder.HasIndex(item => item.Code).IsUnique().HasDatabaseName("IX_StoreItems_Code");
        builder.HasIndex(item => item.UpgradeId).HasDatabaseName("IX_StoreItems_UpgradeId");
        builder.HasIndex(item => new { item.Enabled, item.Purchasable })
            .HasDatabaseName("IX_StoreItems_Enabled_Purchasable");
    }

    private static void ConfigureEconomyTransactions(EntityTypeBuilder<EconomyTransaction> builder)
    {
        builder.Property(e => e.ID).HasColumnName("Id");
        builder.HasOne(e => e.User)
            .WithMany(u => u.EconomyTransactions)
            .HasForeignKey(e => e.UserID)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Account)
            .WithMany(a => a.EconomyTransactions)
            .HasForeignKey(e => e.AccountID)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(e => e.UserID).HasDatabaseName("IX_EconomyTransactions_UserID");
        builder.HasIndex(e => e.AccountID).HasDatabaseName("IX_EconomyTransactions_AccountID");
        builder.HasIndex(e => e.Timestamp).HasDatabaseName("IX_EconomyTransactions_Timestamp");
        builder.HasIndex(e => new { e.UserID, e.Timestamp }).HasDatabaseName("IX_EconomyTransactions_UserID_Timestamp");
        builder.Property(e => e.IdempotencyKey).HasMaxLength(128);
        builder.HasIndex(e => new { e.UserID, e.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL")
            .HasDatabaseName("IX_EconomyTransactions_UserID_IdempotencyKey");
    }

    private static void ConfigureFriendships(EntityTypeBuilder<Friendship> builder)
    {
        builder.HasKey(f => f.ID);
        builder.Property(f => f.ID).HasColumnName("Id");

        builder.HasOne(f => f.Account)
            .WithMany(a => a.Friendships)
            .HasForeignKey(f => f.AccountID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(f => f.FriendAccount)
            .WithMany(a => a.ReverseFriendships)
            .HasForeignKey(f => f.FriendAccountID)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(f => f.AccountID).HasDatabaseName("IX_Friendships_AccountID");
        builder.HasIndex(f => f.FriendAccountID).HasDatabaseName("IX_Friendships_FriendAccountID");
        builder.HasIndex(f => new { f.AccountID, f.FriendAccountID }).IsUnique()
            .HasDatabaseName("UQ_Friendships_AccountID_FriendAccountID");
        builder.HasIndex(f => f.Status).HasDatabaseName("IX_Friendships_Status");
    }

    private static void ConfigureUserInventory(EntityTypeBuilder<UserInventory> builder)
    {
        builder.Property(u => u.ID).HasColumnName("Id");
        builder.HasOne(u => u.User)
            .WithMany(usr => usr.InventoryItems)
            .HasForeignKey(u => u.UserID)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(u => u.Account)
            .WithMany()
            .HasForeignKey(u => u.AccountID)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(u => u.StoreItem)
            .WithMany(s => s.UserInventories)
            .HasForeignKey(u => u.StoreItemId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(u => u.UserID).HasDatabaseName("IX_UserInventory_UserID");
        builder.HasIndex(u => u.AccountID).HasDatabaseName("IX_UserInventory_AccountID");
        builder.HasIndex(u => u.ItemCode).HasDatabaseName("IX_UserInventory_ItemCode");
        builder.HasIndex(u => new { u.UserID, u.ItemCode }).IsUnique()
            .HasDatabaseName("UQ_UserInventory_UserID_ItemCode");
        builder.HasIndex(u => new { u.UserID, u.StoreItemId }).HasDatabaseName("IX_UserInventory_UserID_StoreItemId");
    }

    private static void ConfigureAccountSessions(EntityTypeBuilder<AccountSession> builder)
    {
        builder.HasOne(s => s.Account)
            .WithMany(a => a.Sessions)
            .HasForeignKey(s => s.AccountID)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(s => s.User)
            .WithMany(u => u.Sessions)
            .HasForeignKey(s => s.UserID)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(s => s.AccountID).HasDatabaseName("IX_AccountSessions_AccountID");
        builder.HasIndex(s => s.UserID).HasDatabaseName("IX_AccountSessions_UserID");
        builder.HasIndex(s => s.IPAddress).HasDatabaseName("IX_AccountSessions_IPAddress");
        builder.HasIndex(s => s.HWID).HasDatabaseName("IX_AccountSessions_HWID");
        builder.HasIndex(s => s.TimestampLogin).HasDatabaseName("IX_AccountSessions_TimestampLogin");
        builder.HasIndex(s => s.SessionCookie).HasDatabaseName("IX_AccountSessions_SessionCookie");
    }

    private static void ConfigurePlayerStatistics(EntityTypeBuilder<PlayerStatistics> builder, bool isNpgsql = false)
    {
        builder.HasOne(p => p.Account)
            .WithMany(a => a.MatchParticipations)
            .HasForeignKey(p => p.AccountID)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(p => p.Clan)
            .WithMany()
            .HasForeignKey(p => p.ClanID)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(p => p.Match)
            .WithMany()
            .HasPrincipalKey(m => m.MatchID)
            .HasForeignKey(p => p.MatchID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(statistics => statistics.AccountID).HasDatabaseName("IX_PlayerStatistics_AccountID");
        builder.HasIndex(statistics => statistics.HeroIdentifier).HasDatabaseName("IX_PlayerStatistics_HeroIdentifier");
        builder.HasIndex(statistics => new { statistics.AccountID, statistics.HeroIdentifier })
            .HasDatabaseName("IX_PlayerStatistics_AccountID_HeroIdentifier");

        builder.HasMany(p => p.Inventories)
            .WithOne(i => i.PlayerStatistic)
            .HasForeignKey(i => i.PlayerStatisticID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.ItemEvents)
            .WithOne(e => e.PlayerStatistic)
            .HasForeignKey(e => e.PlayerStatisticID)
            .OnDelete(DeleteBehavior.Cascade);

        // Rec 1: Ensure redundant cosmetic strings are ignored / not mapped to DB columns
        builder.Ignore(p => p.AlternativeAvatarName);
        builder.Ignore(p => p.WardProductName);
        builder.Ignore(p => p.TauntProductName);
        builder.Ignore(p => p.AnnouncerProductName);
        builder.Ignore(p => p.CourierProductName);
        builder.Ignore(p => p.AccountIconProductName);
        builder.Ignore(p => p.ChatColourProductName);

        builder.HasMany(p => p.AbilityEvents)
            .WithOne(e => e.PlayerStatistic)
            .HasForeignKey(e => e.PlayerStatisticID)
            .OnDelete(DeleteBehavior.Cascade);

        if (isNpgsql)
        {
            builder.Property(p => p.CustomStats).HasColumnType("jsonb");
        }
    }

    private static void ConfigureMatchStatistics(EntityTypeBuilder<MatchStatistics> builder, bool isNpgsql = false)
    {
        builder.HasIndex(statistics => statistics.TimestampRecorded)
            .HasDatabaseName("IX_MatchStatistics_TimestampRecorded");

        if (isNpgsql)
        {
            builder.HasIndex(statistics => statistics.TimestampRecorded, "IX_MatchStatistics_TimestampRecorded_Brin")
                .IsUnique(false)
                .HasMethod("brin")
                .HasDatabaseName("IX_MatchStatistics_TimestampRecorded_Brin");
        }

        builder.HasIndex(statistics => statistics.HostAccountName)
            .HasDatabaseName("IX_MatchStatistics_HostAccountName");

        builder.HasIndex(statistics => statistics.Map)
            .HasDatabaseName("IX_MatchStatistics_Map");

        builder.HasMany(statistics => statistics.Frags)
            .WithOne(frag => frag.Match)
            .HasPrincipalKey(statistics => statistics.MatchID)
            .HasForeignKey(frag => frag.MatchID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(statistics => statistics.Bans)
            .WithOne(ban => ban.Match)
            .HasPrincipalKey(statistics => statistics.MatchID)
            .HasForeignKey(ban => ban.MatchID)
            .OnDelete(DeleteBehavior.Cascade);

        if (isNpgsql)
        {
            builder.Property(statistics => statistics.CustomMatchStats).HasColumnType("jsonb");
        }
    }

    private static void ConfigureMatchBans(EntityTypeBuilder<MatchBan> builder, string? collation = null)
    {
        if (!string.IsNullOrEmpty(collation))
            builder.Property(b => b.HeroIdentifier).UseCollation(collation);

        builder.HasOne(b => b.Match)
            .WithMany(m => m.Bans)
            .HasPrincipalKey(m => m.MatchID)
            .HasForeignKey(b => b.MatchID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(b => b.HeroIdentifier).HasDatabaseName("IX_MatchBans_HeroIdentifier");
        builder.HasIndex(b => new { b.MatchID, b.HeroIdentifier })
            .IsUnique()
            .HasDatabaseName("UQ_MatchBans_MatchID_HeroIdentifier");
        builder.HasIndex(b => new { b.MatchID, b.BanOrder })
            .HasDatabaseName("IX_MatchBans_MatchID_BanOrder");
    }

    private static void ConfigureMatchPlayerInventories(EntityTypeBuilder<MatchPlayerInventory> builder, string? collation = null)
    {
        builder.ToTable("MatchPlayerInventories", StatisticsSchema);
        builder.HasKey(i => i.ID);
        builder.Property(i => i.ID).HasColumnName("ID");

        if (!string.IsNullOrEmpty(collation))
            builder.Property(i => i.ItemIdentifier).UseCollation(collation);

        builder.HasOne(i => i.PlayerStatistic)
            .WithMany(p => p.Inventories)
            .HasForeignKey(i => i.PlayerStatisticID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(i => i.PlayerStatisticID).HasDatabaseName("IX_MatchPlayerInventories_PlayerStatisticID");
        builder.HasIndex(i => new { i.PlayerStatisticID, i.SlotIndex })
            .IsUnique()
            .HasDatabaseName("UQ_MatchPlayerInventories_PlayerStatisticID_SlotIndex");
        builder.HasIndex(i => i.ItemIdentifier).HasDatabaseName("IX_MatchPlayerInventories_ItemIdentifier");
        builder.HasIndex(i => new { i.MatchID, i.AccountID }).HasDatabaseName("IX_MatchPlayerInventories_MatchID_AccountID");
    }

    private static void ConfigureMatchItemEvents(EntityTypeBuilder<MatchItemEvent> builder, string? collation = null)
    {
        builder.ToTable("MatchItemEvents", StatisticsSchema);
        builder.HasKey(e => e.ID);
        builder.Property(e => e.ID).HasColumnName("ID");

        if (!string.IsNullOrEmpty(collation))
            builder.Property(e => e.ItemIdentifier).UseCollation(collation);

        builder.HasOne(e => e.PlayerStatistic)
            .WithMany(p => p.ItemEvents)
            .HasForeignKey(e => e.PlayerStatisticID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.PlayerStatisticID).HasDatabaseName("IX_MatchItemEvents_PlayerStatisticID");
        builder.HasIndex(e => e.ItemIdentifier).HasDatabaseName("IX_MatchItemEvents_ItemIdentifier");
        builder.HasIndex(e => new { e.MatchID, e.AccountID, e.GameTimeSeconds })
            .HasDatabaseName("IX_MatchItemEvents_MatchID_AccountID_GameTimeSeconds");
        builder.HasIndex(e => e.EventType).HasDatabaseName("IX_MatchItemEvents_EventType");
    }

    private static void ConfigureMatchAbilityEvents(EntityTypeBuilder<MatchAbilityEvent> builder, string? collation = null)
    {
        builder.ToTable("MatchAbilityEvents", StatisticsSchema);
        builder.HasKey(e => e.ID);
        builder.Property(e => e.ID).HasColumnName("ID");

        if (!string.IsNullOrEmpty(collation))
        {
            builder.Property(e => e.AbilityIdentifier).UseCollation(collation);
            builder.Property(e => e.HeroIdentifier).UseCollation(collation);
        }

        builder.Property(e => e.AbilityIdentifier).HasMaxLength(100).IsRequired();
        builder.Property(e => e.HeroIdentifier).HasMaxLength(100).IsRequired();

        builder.HasOne(e => e.PlayerStatistic)
            .WithMany(p => p.AbilityEvents)
            .HasForeignKey(e => e.PlayerStatisticID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.PlayerStatisticID).HasDatabaseName("IX_MatchAbilityEvents_PlayerStatisticID");
        builder.HasIndex(e => e.AbilityIdentifier).HasDatabaseName("IX_MatchAbilityEvents_AbilityIdentifier");
        builder.HasIndex(e => e.HeroIdentifier).HasDatabaseName("IX_MatchAbilityEvents_HeroIdentifier");
        builder.HasIndex(e => new { e.MatchID, e.AccountID, e.GameTimeSeconds })
            .HasDatabaseName("IX_MatchAbilityEvents_MatchID_AccountID_GameTimeSeconds");
        builder.HasIndex(e => new { e.MatchID, e.SlotIndex, e.Level })
            .HasDatabaseName("IX_MatchAbilityEvents_MatchID_SlotIndex_Level");
    }

    private static void ConfigureMatchFrags(EntityTypeBuilder<MatchFrag> builder)
    {
        builder.ToTable("MatchFrags", StatisticsSchema);
        builder.HasKey(f => f.ID);
        builder.Property(f => f.ID).HasColumnName("ID");

        builder.HasOne(f => f.Match)
            .WithMany(m => m.Frags)
            .HasPrincipalKey(m => m.MatchID)
            .HasForeignKey(f => f.MatchID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(f => f.Assists)
            .WithOne(a => a.Frag)
            .HasForeignKey(a => a.FragID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Ignore(f => f.KillerAccount);
        builder.Ignore(f => f.VictimAccount);

        builder.HasIndex(f => f.MatchID).HasDatabaseName("IX_MatchFrags_MatchID");
        builder.HasIndex(f => f.KillerAccountID).HasDatabaseName("IX_MatchFrags_KillerAccountID");
        builder.HasIndex(f => f.VictimAccountID).HasDatabaseName("IX_MatchFrags_VictimAccountID");
        builder.HasIndex(f => new { f.MatchID, f.GameTimeSeconds })
            .HasDatabaseName("IX_MatchFrags_MatchID_GameTimeSeconds");
        builder.HasIndex(f => new { f.KillerAccountID, f.VictimAccountID })
            .HasDatabaseName("IX_MatchFrags_KillerAccountID_VictimAccountID");
    }

    private static void ConfigureMatchFragAssists(EntityTypeBuilder<MatchFragAssist> builder)
    {
        builder.ToTable("MatchFragAssists", StatisticsSchema);
        builder.HasKey(a => a.ID);
        builder.Property(a => a.ID).HasColumnName("ID");

        builder.HasOne(a => a.Frag)
            .WithMany(f => f.Assists)
            .HasForeignKey(a => a.FragID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Ignore(a => a.AssistingAccount);

        builder.HasIndex(a => a.FragID).HasDatabaseName("IX_MatchFragAssists_FragID");
        builder.HasIndex(a => a.AssistingAccountID).HasDatabaseName("IX_MatchFragAssists_AssistingAccountID");
        builder.HasIndex(a => new { a.FragID, a.AssistingAccountID })
            .IsUnique()
            .HasDatabaseName("UQ_MatchFragAssists_FragID_AssistingAccountID");
    }

    private static void ConfigureMastery(EntityTypeBuilder<Mastery> builder)
    {
        builder.HasIndex(m => m.AccountID)
            .IsUnique()
            .HasDatabaseName("IX_Masteries_AccountID");

        builder.HasOne(m => m.Account)
            .WithOne(a => a.Mastery)
            .HasForeignKey<Mastery>(m => m.AccountID)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureHeroMasteryExperiences(EntityTypeBuilder<HeroMasteryExperience> builder, string? collation = null)
    {
        if (!string.IsNullOrEmpty(collation))
            builder.Property(e => e.HeroIdentifier).UseCollation(collation);

        builder.Property(e => e.HeroIdentifier)
            .HasMaxLength(100)
            .IsRequired();

        builder.HasOne(e => e.Mastery)
            .WithMany(m => m.HeroExperiences)
            .HasForeignKey(e => e.MasteryID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.MasteryID)
            .HasDatabaseName("IX_HeroMasteryExperiences_MasteryID");

        builder.HasIndex(e => e.HeroIdentifier)
            .HasDatabaseName("IX_HeroMasteryExperiences_HeroIdentifier");

        builder.HasIndex(e => new { e.MasteryID, e.HeroIdentifier })
            .IsUnique()
            .HasDatabaseName("UQ_HeroMasteryExperiences_MasteryID_HeroIdentifier");
    }

    private static void ConfigureAccountSelectedStoreItems(EntityTypeBuilder<AccountSelectedStoreItem> builder, string? collation = null)
    {
        if (!string.IsNullOrEmpty(collation))
            builder.Property(i => i.ItemCode).UseCollation(collation);

        builder.Property(i => i.ItemCode)
            .HasMaxLength(100)
            .IsRequired();

        builder.HasOne(i => i.Account)
            .WithMany(a => a.SelectedStoreItems)
            .HasForeignKey(i => i.AccountID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(i => i.AccountID)
            .HasDatabaseName("IX_AccountSelectedStoreItems_AccountID");

        builder.HasIndex(i => new { i.AccountID, i.ItemCode })
            .IsUnique()
            .HasDatabaseName("UQ_AccountSelectedStoreItems_AccountID_ItemCode");
    }

    private static void ConfigureAccountIpAddresses(EntityTypeBuilder<AccountIpAddress> builder)
    {
        builder.Property(ip => ip.IPAddress)
            .HasMaxLength(50)
            .IsRequired();

        builder.HasOne(ip => ip.Account)
            .WithMany(a => a.IPAddressCollection)
            .HasForeignKey(ip => ip.AccountID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(ip => ip.AccountID)
            .HasDatabaseName("IX_AccountIpAddresses_AccountID");

        builder.HasIndex(ip => ip.IPAddress)
            .HasDatabaseName("IX_AccountIpAddresses_IPAddress");

        builder.HasIndex(ip => new { ip.AccountID, ip.IPAddress })
            .IsUnique()
            .HasDatabaseName("UQ_AccountIpAddresses_AccountID_IPAddress");
    }

    private static void ConfigureAccountMacAddresses(EntityTypeBuilder<AccountMacAddress> builder)
    {
        builder.Property(mac => mac.MACAddress)
            .HasMaxLength(50)
            .IsRequired();

        builder.HasOne(mac => mac.Account)
            .WithMany(a => a.MACAddressCollection)
            .HasForeignKey(mac => mac.AccountID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(mac => mac.AccountID)
            .HasDatabaseName("IX_AccountMacAddresses_AccountID");

        builder.HasIndex(mac => mac.MACAddress)
            .HasDatabaseName("IX_AccountMacAddresses_MACAddress");

        builder.HasIndex(mac => new { mac.AccountID, mac.MACAddress })
            .IsUnique()
            .HasDatabaseName("UQ_AccountMacAddresses_AccountID_MACAddress");
    }

    private static void ConfigureAccountBlockedPhrases(EntityTypeBuilder<AccountBlockedPhrase> builder, string? collation = null)
    {
        if (!string.IsNullOrEmpty(collation))
            builder.Property(bp => bp.Phrase).UseCollation(collation);

        builder.Property(bp => bp.Phrase)
            .HasMaxLength(200)
            .IsRequired();

        builder.HasOne(bp => bp.Account)
            .WithMany(a => a.BlockedPhrases)
            .HasForeignKey(bp => bp.AccountID)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(bp => bp.AccountID)
            .HasDatabaseName("IX_AccountBlockedPhrases_AccountID");

        builder.HasIndex(bp => new { bp.AccountID, bp.Phrase })
            .IsUnique()
            .HasDatabaseName("UQ_AccountBlockedPhrases_AccountID_Phrase");
    }

    private static void ConfigureWarEffort(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WarEffortCycle>(b =>
        {
            b.HasIndex(c => c.CycleNumber).IsUnique().HasDatabaseName("UX_WarEffortCycles_CycleNumber");
            b.HasIndex(c => c.IsActive).HasDatabaseName("IX_WarEffortCycles_IsActive");
        });

        modelBuilder.Entity<QuestDefinition>(b =>
        {
            b.HasIndex(q => q.Code).IsUnique().HasDatabaseName("UX_QuestDefinitions_Code");
            b.HasIndex(q => new { q.IsEnabled, q.IsNativeEngineTrigger }).HasDatabaseName("IX_QuestDefinitions_Enabled_Native");
        });

        modelBuilder.Entity<AccountQuestSlot>(b =>
        {
            b.HasIndex(s => new { s.AccountId, s.CycleId, s.SlotIndex })
                .IsUnique()
                .HasDatabaseName("UX_AccountQuestSlots_Account_Cycle_Slot");
            b.HasIndex(s => s.AccountId).HasDatabaseName("IX_AccountQuestSlots_AccountId");
        });

        modelBuilder.Entity<WarEffortBagMilestone>(b =>
        {
            b.HasIndex(m => new { m.CycleId, m.BagIndex })
                .IsUnique()
                .HasDatabaseName("UX_WarEffortBagMilestones_Cycle_BagIndex");
            b.HasIndex(m => m.BagId)
                .IsUnique()
                .HasDatabaseName("UX_WarEffortBagMilestones_BagId");
        });

        modelBuilder.Entity<AccountWarEffortProgress>(b =>
        {
            b.HasIndex(p => new { p.AccountId, p.CycleId })
                .IsUnique()
                .HasDatabaseName("UX_AccountWarEffortProgress_Account_Cycle");
            b.HasIndex(p => new { p.CycleId, p.TotalBagPoints })
                .HasDatabaseName("IX_AccountWarEffortProgress_Cycle_Points");
        });

        modelBuilder.Entity<AccountWarEffortBagClaim>(b =>
        {
            b.HasIndex(c => new { c.AccountId, c.CycleId, c.BagIndex })
                .IsUnique()
                .HasDatabaseName("UX_AccountWarEffortBagClaims_Account_Cycle_Bag");
            b.HasIndex(c => c.IdempotencyKey)
                .IsUnique()
                .HasDatabaseName("UX_AccountWarEffortBagClaims_IdempotencyKey");
        });

        modelBuilder.Entity<WarEffortCycleLeaderboardSnapshot>(b =>
        {
            b.HasIndex(s => new { s.CycleId, s.AccountId })
                .IsUnique()
                .HasDatabaseName("UX_WarEffortCycleLeaderboardSnapshots_Cycle_Account");
            b.HasIndex(s => new { s.CycleId, s.Rank })
                .HasDatabaseName("IX_WarEffortCycleLeaderboardSnapshots_Cycle_Rank");
        });
    }
}

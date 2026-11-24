// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NEXUS.Contracts.Domain;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

/// <summary>
/// Empirical challenge tests authored by teamwork_preview_challenger_m4_2_2 for Milestone 4 Iteration 2.
/// Stress-tests full Account cascade deletion graph and multi-tenant isolation.
/// </summary>
public class EntityCascadeAndIsolationTests
{
    [Trait("Category", "Manual")]
    [Fact]
    public async Task Account_FullCascadeGraphDeletion_PurgesAllChildEntities_PreservesSiblingAccountAndZeroOrphans()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string dbName = Guid.NewGuid().ToString("N");
        int targetAccountId = 1000;
        int siblingAccountId = 2000;
        int peerAccountId = 3000;
        int targetGuideId = 10001;
        int siblingGuideId = 20001;
        int matchId = 9999;

        // Step 1: Seed Target Account, Sibling Account, and Peer Account with full child graphs
        using (MerrickContext context = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User u1 = new() { ID = 100, EmailAddress = "target@kongor.net", PBKDF2PasswordHash = "h1", SRPPasswordHash = "s1", SRPPasswordSalt = "t1" };
            User u2 = new() { ID = 200, EmailAddress = "sibling@kongor.net", PBKDF2PasswordHash = "h2", SRPPasswordHash = "s2", SRPPasswordSalt = "t2" };
            User u3 = new() { ID = 300, EmailAddress = "peer@kongor.net", PBKDF2PasswordHash = "h3", SRPPasswordHash = "s3", SRPPasswordSalt = "t3" };

            Account acc1 = new() { ID = targetAccountId, UserID = 100, User = u1, Name = "TargetPlayer" };
            Account acc2 = new() { ID = siblingAccountId, UserID = 200, User = u2, Name = "SiblingPlayer" };
            Account acc3 = new() { ID = peerAccountId, UserID = 300, User = u3, Name = "PeerPlayer" };

            context.Users.AddRange(u1, u2, u3);
            context.Accounts.AddRange(acc1, acc2, acc3);

            // Match setup
            MatchStatistics match = new() { ID = 1, MatchID = matchId };
            context.MatchStatistics.Add(match);

            // Target Account child entities (all 12+ child entities)
            context.AccountSelectedStoreItems.Add(new AccountSelectedStoreItem { AccountID = targetAccountId, ItemCode = "target.icon:1" });
            context.AccountIpAddresses.Add(new AccountIpAddress { AccountID = targetAccountId, IPAddress = "10.0.0.1" });
            context.AccountMacAddresses.Add(new AccountMacAddress { AccountID = targetAccountId, MACAddress = "00-11-22-33-44-55" });
            context.AccountBlockedPhrases.Add(new AccountBlockedPhrase { AccountID = targetAccountId, Phrase = "target_toxic" });
            context.Notifications.Add(new Notification { NotificationId = 101, AccountId = targetAccountId, Content = "Target Alert" });
            context.AccountSessions.Add(new AccountSession { ID = 101, AccountID = targetAccountId, UserID = 100, IPAddress = "10.0.0.1" });
            context.PlayerStatistics.Add(new PlayerStatistics { ID = 101, MatchID = matchId, AccountID = targetAccountId });
            context.AccountStatistics.Add(new AccountStatistics { AccountID = targetAccountId, Type = AccountStatisticsType.Matchmaking, MatchesPlayed = 50, MatchesWon = 25 });
            context.Friendships.Add(new Friendship { ID = 101, AccountID = targetAccountId, FriendAccountID = peerAccountId, Status = FriendshipStatus.Accepted });
            context.Friendships.Add(new Friendship { ID = 102, AccountID = peerAccountId, FriendAccountID = targetAccountId, Status = FriendshipStatus.Accepted });

            HeroGuide targetGuide = new() { ID = targetGuideId, AuthorID = targetAccountId, Name = "Target Guide" };
            context.HeroGuides.Add(targetGuide);
            context.HeroGuideItems.Add(new HeroGuideItem { ID = 101, GuideID = targetGuideId, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_TargetRunes" });
            context.HeroGuideAbilities.Add(new HeroGuideAbility { ID = 101, GuideID = targetGuideId, LevelSequence = 1, AbilitySlot = 1, AbilityCode = "Ability_Target1" });

            // Player reports involving target
            context.PlayerReports.Add(new PlayerReport { ID = 101, MatchID = matchId, ReporterAccountID = peerAccountId, ReporterAccountName = "PeerPlayer", ReportedAccountID = targetAccountId, ReportedAccountName = "TargetPlayer", Reason = "TargetReported" });
            context.PlayerReports.Add(new PlayerReport { ID = 102, MatchID = matchId, ReporterAccountID = targetAccountId, ReporterAccountName = "TargetPlayer", ReportedAccountID = peerAccountId, ReportedAccountName = "PeerPlayer", Reason = "TargetFiled" });

            // Sibling Account child entities (full graph mirrors target)
            context.AccountSelectedStoreItems.Add(new AccountSelectedStoreItem { AccountID = siblingAccountId, ItemCode = "sibling.icon:2" });
            context.AccountIpAddresses.Add(new AccountIpAddress { AccountID = siblingAccountId, IPAddress = "10.0.0.2" });
            context.AccountMacAddresses.Add(new AccountMacAddress { AccountID = siblingAccountId, MACAddress = "AA-BB-CC-DD-EE-FF" });
            context.AccountBlockedPhrases.Add(new AccountBlockedPhrase { AccountID = siblingAccountId, Phrase = "sibling_toxic" });
            context.Notifications.Add(new Notification { NotificationId = 201, AccountId = siblingAccountId, Content = "Sibling Alert" });
            context.AccountSessions.Add(new AccountSession { ID = 201, AccountID = siblingAccountId, UserID = 200, IPAddress = "10.0.0.2" });
            context.PlayerStatistics.Add(new PlayerStatistics { ID = 201, MatchID = matchId, AccountID = siblingAccountId });
            context.AccountStatistics.Add(new AccountStatistics { AccountID = siblingAccountId, Type = AccountStatisticsType.Matchmaking, MatchesPlayed = 80, MatchesWon = 40 });
            context.Friendships.Add(new Friendship { ID = 201, AccountID = siblingAccountId, FriendAccountID = peerAccountId, Status = FriendshipStatus.Accepted });
            context.Friendships.Add(new Friendship { ID = 202, AccountID = peerAccountId, FriendAccountID = siblingAccountId, Status = FriendshipStatus.Accepted });

            HeroGuide siblingGuide = new() { ID = siblingGuideId, AuthorID = siblingAccountId, Name = "Sibling Guide" };
            context.HeroGuides.Add(siblingGuide);
            context.HeroGuideItems.Add(new HeroGuideItem { ID = 201, GuideID = siblingGuideId, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_SiblingRunes" });
            context.HeroGuideAbilities.Add(new HeroGuideAbility { ID = 201, GuideID = siblingGuideId, LevelSequence = 1, AbilitySlot = 1, AbilityCode = "Ability_Sibling1" });

            context.PlayerReports.Add(new PlayerReport { ID = 201, MatchID = matchId, ReporterAccountID = peerAccountId, ReporterAccountName = "PeerPlayer", ReportedAccountID = siblingAccountId, ReportedAccountName = "SiblingPlayer", Reason = "SiblingReported" });
            context.PlayerReports.Add(new PlayerReport { ID = 202, MatchID = matchId, ReporterAccountID = siblingAccountId, ReporterAccountName = "SiblingPlayer", ReportedAccountID = peerAccountId, ReportedAccountName = "PeerPlayer", Reason = "SiblingFiled" });

            await context.SaveChangesAsync();
        }

        // Step 2: Delete Target Account with complete child graph includes
        using (MerrickContext context = TestDbContextFactory.CreateInMemoryContext(dbName))
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
                .FirstAsync(a => a.ID == targetAccountId);

            context.Accounts.Remove(acc1);

            Func<Task> act = async () => await context.SaveChangesAsync();
            await act.Should().NotThrowAsync("Deleting Account with full child graph must succeed cleanly");
        }

        // Step 3: Verify all Target Account child entities are deleted with ZERO orphans
        using (MerrickContext context = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await context.Accounts.AnyAsync(a => a.ID == targetAccountId)).Should().BeFalse("Target Account must be deleted");
            (await context.AccountSelectedStoreItems.AnyAsync(s => s.AccountID == targetAccountId)).Should().BeFalse("AccountSelectedStoreItems must be cascaded");
            (await context.AccountIpAddresses.AnyAsync(i => i.AccountID == targetAccountId)).Should().BeFalse("AccountIpAddresses must be cascaded");
            (await context.AccountMacAddresses.AnyAsync(m => m.AccountID == targetAccountId)).Should().BeFalse("AccountMacAddresses must be cascaded");
            (await context.AccountBlockedPhrases.AnyAsync(p => p.AccountID == targetAccountId)).Should().BeFalse("AccountBlockedPhrases must be cascaded");
            (await context.Notifications.AnyAsync(n => n.AccountId == targetAccountId)).Should().BeFalse("Notifications must be cascaded");
            (await context.AccountSessions.AnyAsync(s => s.AccountID == targetAccountId)).Should().BeFalse("AccountSessions must be cascaded");
            (await context.PlayerStatistics.AnyAsync(p => p.AccountID == targetAccountId)).Should().BeFalse("PlayerStatistics must be cascaded");
            (await context.AccountStatistics.AnyAsync(s => s.AccountID == targetAccountId)).Should().BeFalse("AccountStatistics must be cascaded");
            (await context.Friendships.AnyAsync(f => f.AccountID == targetAccountId || f.FriendAccountID == targetAccountId)).Should().BeFalse("Friendships must be cascaded");
            (await context.HeroGuides.AnyAsync(g => g.AuthorID == targetAccountId)).Should().BeFalse("AuthoredGuides must be cascaded");
            (await context.HeroGuideItems.AnyAsync(i => i.GuideID == targetGuideId)).Should().BeFalse("HeroGuideItems must be cascaded");
            (await context.HeroGuideAbilities.AnyAsync(a => a.GuideID == targetGuideId)).Should().BeFalse("HeroGuideAbilities must be cascaded");
            (await context.PlayerReports.AnyAsync(r => r.ID == 101)).Should().BeFalse("ReportedAccount report must be cascaded");

            // Step 4: Multi-Tenant Isolation: Verify Sibling Account and 100% of its child entities are intact
            (await context.Accounts.AnyAsync(a => a.ID == siblingAccountId)).Should().BeTrue("Sibling Account must remain intact");
            (await context.AccountSelectedStoreItems.AnyAsync(s => s.AccountID == siblingAccountId)).Should().BeTrue("Sibling AccountSelectedStoreItems must remain intact");
            (await context.AccountIpAddresses.AnyAsync(i => i.AccountID == siblingAccountId)).Should().BeTrue("Sibling AccountIpAddresses must remain intact");
            (await context.AccountMacAddresses.AnyAsync(m => m.AccountID == siblingAccountId)).Should().BeTrue("Sibling AccountMacAddresses must remain intact");
            (await context.AccountBlockedPhrases.AnyAsync(p => p.AccountID == siblingAccountId)).Should().BeTrue("Sibling AccountBlockedPhrases must remain intact");
            (await context.Notifications.AnyAsync(n => n.AccountId == siblingAccountId)).Should().BeTrue("Sibling Notifications must remain intact");
            (await context.AccountSessions.AnyAsync(s => s.AccountID == siblingAccountId)).Should().BeTrue("Sibling AccountSessions must remain intact");
            (await context.PlayerStatistics.AnyAsync(p => p.AccountID == siblingAccountId)).Should().BeTrue("Sibling PlayerStatistics must remain intact");
            (await context.AccountStatistics.AnyAsync(s => s.AccountID == siblingAccountId)).Should().BeTrue("Sibling AccountStatistics must remain intact");
            (await context.Friendships.AnyAsync(f => f.AccountID == siblingAccountId && f.FriendAccountID == peerAccountId)).Should().BeTrue("Sibling Friendship must remain intact");
            (await context.Friendships.AnyAsync(f => f.AccountID == peerAccountId && f.FriendAccountID == siblingAccountId)).Should().BeTrue("Sibling ReverseFriendship must remain intact");
            (await context.HeroGuides.AnyAsync(g => g.AuthorID == siblingAccountId)).Should().BeTrue("Sibling HeroGuide must remain intact");
            (await context.HeroGuideItems.AnyAsync(i => i.GuideID == siblingGuideId)).Should().BeTrue("Sibling HeroGuideItems must remain intact");
            (await context.HeroGuideAbilities.AnyAsync(a => a.GuideID == siblingGuideId)).Should().BeTrue("Sibling HeroGuideAbilities must remain intact");
            (await context.PlayerReports.AnyAsync(r => r.ID == 201)).Should().BeTrue("Sibling ReportedAccount report must remain intact");
            (await context.PlayerReports.AnyAsync(r => r.ID == 202)).Should().BeTrue("Sibling ReporterAccount report must remain intact");

            // MatchStatistics intact
            (await context.MatchStatistics.AnyAsync(m => m.MatchID == matchId)).Should().BeTrue("MatchStatistics must remain intact");
        }
    }

    [Fact]
    public void MerrickContext_ModelMetadata_CascadeForeignKeys_ConfiguredCorrectlyAcrossGraph()
    {
        using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        IModel model = context.Model;

        void AssertForeignKeyCascade(Type dependentType, Type principalType, DeleteBehavior expectedBehavior)
        {
            IEntityType? entity = model.FindEntityType(dependentType);
            entity.Should().NotBeNull($"Entity {dependentType.Name} should exist in EF model");

            IForeignKey? fk = entity!.GetForeignKeys().FirstOrDefault(f => f.PrincipalEntityType.ClrType == principalType);
            fk.Should().NotBeNull($"Foreign key from {dependentType.Name} to {principalType.Name} should exist");
            fk!.DeleteBehavior.Should().Be(expectedBehavior, $"{dependentType.Name} -> {principalType.Name} DeleteBehavior should be {expectedBehavior}");
        }

        // Account 1NF child entities
        AssertForeignKeyCascade(typeof(AccountSelectedStoreItem), typeof(Account), DeleteBehavior.Cascade);
        AssertForeignKeyCascade(typeof(AccountIpAddress), typeof(Account), DeleteBehavior.Cascade);
        AssertForeignKeyCascade(typeof(AccountMacAddress), typeof(Account), DeleteBehavior.Cascade);
        AssertForeignKeyCascade(typeof(AccountBlockedPhrase), typeof(Account), DeleteBehavior.Cascade);

        // HeroGuide child entities
        AssertForeignKeyCascade(typeof(HeroGuide), typeof(Account), DeleteBehavior.Cascade);
        AssertForeignKeyCascade(typeof(HeroGuideItem), typeof(HeroGuide), DeleteBehavior.Cascade);
        AssertForeignKeyCascade(typeof(HeroGuideAbility), typeof(HeroGuide), DeleteBehavior.Cascade);

        // Social and sessions
        AssertForeignKeyCascade(typeof(Friendship), typeof(Account), DeleteBehavior.Cascade);
        AssertForeignKeyCascade(typeof(AccountSession), typeof(Account), DeleteBehavior.Cascade);
        AssertForeignKeyCascade(typeof(Notification), typeof(Account), DeleteBehavior.Cascade);
        AssertForeignKeyCascade(typeof(PlayerStatistics), typeof(Account), DeleteBehavior.Cascade);
        AssertForeignKeyCascade(typeof(AccountStatistics), typeof(Account), DeleteBehavior.Cascade);

        // Mastery
        AssertForeignKeyCascade(typeof(Mastery), typeof(Account), DeleteBehavior.Cascade);
        AssertForeignKeyCascade(typeof(HeroMasteryExperience), typeof(Mastery), DeleteBehavior.Cascade);
    }
}

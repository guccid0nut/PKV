// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Feature09_MatchmakingQueueRetentionTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 1: Feature Coverage (Feature 9: Matchmaking Queue Retention with Progressive MMR)
// Policy: Strict Explicit Typing, Opaque-Box Validation (>= 5 tests)
// ============================================================================

using System.Runtime.CompilerServices;
using FluentAssertions;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;

namespace ASPIRE.Tests.E2E.Tier1_FeatureCoverage;

public sealed class MatchmakingQueueRetentionTests
{
    private static MatchmakingGroupInformation CreateDefaultInfo(
        ChatProtocol.TMMGameType gameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL)
    {
        return new MatchmakingGroupInformation
        {
            ClientVersion = "4.10.2",
            GroupType = ChatProtocol.TMMType.TMM_TYPE_PVP,
            GameType = gameType,
            MapName = "caldavar",
            GameModes = ["ap"],
            GameRegions = ["USE"],
            Ranked = true,
            MatchFidelity = 0,
            BotDifficulty = 0,
            RandomizeBots = false
        };
    }

    private static MatchmakingGroupMember CreateMember(int id, string name, double rating, bool isLeader = false)
    {
        ChatSession session = (ChatSession)RuntimeHelpers.GetUninitializedObject(typeof(ChatSession));
        User user = new()
        {
            ID = id,
            EmailAddress = $"{name}@kongor.net",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s"
        };
        Account account = new()
        {
            ID = id,
            Name = name,
            User = user,
            IsMain = true,
            Type = AccountType.Normal
        };
        session.Account = account;
        return new MatchmakingGroupMember(session)
        {
            Slot = 0,
            IsLeader = isLeader,
            IsReady = true,
            IsInGame = false,
            IsEligibleForMatchmaking = true,
            LoadingPercent = 100,
            Rating = rating,
            GameModeAccess = "true"
        };
    }

    [Fact]
    public void MatchmakingGroup_AverageRating_CalculatesAccurately()
    {
        // Arrange
        MatchmakingGroupMember m1 = CreateMember(1, "Player1", 1500.0, true);
        MatchmakingGroupMember m2 = CreateMember(2, "Player2", 1700.0);

        MatchmakingGroup group = new()
        {
            Members = [m1, m2],
            Information = CreateDefaultInfo()
        };

        // Assert
        group.AverageRating.Should().Be(1600f);
        group.RatingDisparity.Should().Be(200f);
    }

    [Fact]
    public void MatchmakingGroup_QueueStartTime_PreservesTimestampAcrossQueueCycles()
    {
        // Arrange
        DateTimeOffset originalStart = TimeProvider.System.GetUtcNow().AddMinutes(-5);
        MatchmakingGroup group = new()
        {
            Members = [CreateMember(1, "P1", 1500.0, true)],
            Information = CreateDefaultInfo(),
            QueueStartTime = originalStart
        };

        // Act & Assert
        group.QueueStartTime.Should().Be(originalStart);
        double elapsedMinutes = (TimeProvider.System.GetUtcNow() - group.QueueStartTime.Value).TotalMinutes;
        elapsedMinutes.Should().BeGreaterThanOrEqualTo(4.9);
    }

    [Fact]
    public void MatchmakingGroup_LeaderIdentification_ResolvesLeaderCorrectly()
    {
        // Arrange
        MatchmakingGroupMember m1 = CreateMember(10, "Member", 1400.0);
        MatchmakingGroupMember m2 = CreateMember(20, "Leader", 1600.0, true);

        MatchmakingGroup group = new()
        {
            Members = [m1, m2],
            Information = CreateDefaultInfo()
        };

        // Assert
        group.Leader.Should().NotBeNull();
        group.Leader.Account.ID.Should().Be(20);
        group.Leader.Account.Name.Should().Be("Leader");
    }

    [Fact]
    public void MatchmakingGroup_RatingDisparity_ZeroWhenSolo()
    {
        // Arrange
        MatchmakingGroup group = new()
        {
            Members = [CreateMember(1, "Solo", 1850.0, true)],
            Information = CreateDefaultInfo()
        };

        // Assert
        group.AverageRating.Should().Be(1850f);
        group.RatingDisparity.Should().Be(0f);
    }

    [Fact]
    public void MatchmakingGroup_PendingInvites_StoresAndRetrievesInvitedUsers()
    {
        // Arrange
        MatchmakingGroup group = new()
        {
            Members = [CreateMember(1, "Captain", 1500.0, true)],
            Information = CreateDefaultInfo()
        };

        // Act
        group.PendingInvites.TryAdd("FriendPlayer", TimeProvider.System.GetUtcNow());

        // Assert
        group.PendingInvites.ContainsKey("FriendPlayer").Should().BeTrue();
        group.PendingInvites.ContainsKey("friendplayer").Should().BeTrue("Pending invites should be case-insensitive");
    }

    [Fact]
    public void MatchmakingGroup_TeamSizeDifference_ReflectsMissingSlots()
    {
        // Arrange
        MatchmakingGroupInformation info = CreateDefaultInfo();
        MatchmakingGroup group = new()
        {
            Members =
            [
                CreateMember(1, "P1", 1500.0, true),
                CreateMember(2, "P2", 1500.0)
            ],
            Information = info
        };

        // Assert
        group.FullTeamDifference.Should().Be(3);
    }
}

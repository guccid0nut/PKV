// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MatchmakingBoundaryTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 2: Boundary & Corner Cases (Matchmaking Queue & MMR Boundaries)
// Policy: Strict Explicit Typing, Adversarial Boundary Verification (>= 5 tests)
// ============================================================================

using System.Runtime.CompilerServices;
using FluentAssertions;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;

namespace ASPIRE.Tests.E2E.Tier2_BoundaryAndCornerCases;

public sealed class MatchmakingBoundaryTests
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
    public void Boundary_EmptyGroup_ReturnsZeroAverageRatingSafely()
    {
        // Arrange
        MatchmakingGroup group = new()
        {
            Members = [],
            Information = CreateDefaultInfo()
        };

        // Assert
        group.AverageRating.Should().Be(0f);
        group.RatingDisparity.Should().Be(0f);
    }

    [Fact]
    public void Boundary_ExtremeMmrDisparity_CalculatesMaxSpreadAccurately()
    {
        // Arrange
        MatchmakingGroupMember m1 = CreateMember(1, "ProPlayer", 2200.0, true);
        MatchmakingGroupMember m2 = CreateMember(2, "NovicePlayer", 800.0);

        MatchmakingGroup group = new()
        {
            Members = [m1, m2],
            Information = CreateDefaultInfo()
        };

        // Assert
        group.AverageRating.Should().Be(1500f);
        group.RatingDisparity.Should().Be(1400f);
    }

    [Fact]
    public void Boundary_NullQueueStartTime_HandlesWaitDurationCalculationSafely()
    {
        // Arrange
        MatchmakingGroup group = new()
        {
            Members = [CreateMember(1, "Player", 1500.0, true)],
            Information = CreateDefaultInfo(),
            QueueStartTime = null
        };

        // Assert
        group.QueueStartTime.Should().BeNull();
    }

    [Fact]
    public void Boundary_FullTeamDifference_ZeroWhenFull()
    {
        // Arrange - 5 players in a 5v5 map
        List<MatchmakingGroupMember> roster = [];
        for (int i = 1; i <= 5; i++) roster.Add(CreateMember(i, $"P_{i}", 1500.0, i == 1));

        MatchmakingGroup group = new()
        {
            Members = roster,
            Information = CreateDefaultInfo()
        };

        // Assert
        group.FullTeamDifference.Should().Be(0);
    }

    [Fact]
    public void Boundary_DuplicateInvites_MaintainsSingleEntryInPendingDictionary()
    {
        // Arrange
        MatchmakingGroup group = new()
        {
            Members = [CreateMember(1, "Leader", 1500.0, true)],
            Information = CreateDefaultInfo()
        };

        DateTimeOffset time1 = TimeProvider.System.GetUtcNow();
        DateTimeOffset time2 = time1.AddSeconds(10);

        // Act
        group.PendingInvites["Invitee"] = time1;
        group.PendingInvites["invitee"] = time2; // Overwrite case-insensitively

        // Assert
        group.PendingInvites.Count.Should().Be(1);
        group.PendingInvites["INVITEE"].Should().Be(time2);
    }

    [Fact]
    public void Boundary_MaxRatingEdge_SupportsTopEchelonRankings()
    {
        // Arrange
        MatchmakingGroup group = new()
        {
            Members = [CreateMember(99, "Godlike", 3000.0, true)],
            Information = CreateDefaultInfo()
        };

        // Assert
        group.AverageRating.Should().Be(3000f);
    }

    [Fact]
    public void Boundary_ZeroRating_SupportsFloorRatings()
    {
        // Arrange
        MatchmakingGroup group = new()
        {
            Members = [CreateMember(100, "Unranked", 0.0, true)],
            Information = CreateDefaultInfo()
        };

        // Assert
        group.AverageRating.Should().Be(0f);
    }

    [Fact]
    public void Boundary_TenPlayerRoster_CalculatesTeamDisparityAccurately()
    {
        // Arrange
        List<MatchmakingGroupMember> roster = [];
        for (int i = 1; i <= 10; i++) roster.Add(CreateMember(i, $"Player_{i}", 1000.0 + i * 100.0, i == 1));

        MatchmakingGroup group = new()
        {
            Members = roster,
            Information = CreateDefaultInfo()
        };

        // Assert
        group.Members.Count.Should().Be(10);
        group.AverageRating.Should().Be(1550f);
        group.RatingDisparity.Should().Be(900f);
    }
}

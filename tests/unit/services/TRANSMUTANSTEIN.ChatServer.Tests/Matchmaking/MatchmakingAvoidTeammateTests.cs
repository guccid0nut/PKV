// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Enumerations;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Matchmaking;

public sealed class MatchmakingAvoidTeammateTests
{
    private static MatchmakingGroupInformation CreateInformation() => new()
    {
        ClientVersion = "4.10.1.0",
        GroupType = ChatProtocol.TMMType.TMM_TYPE_PVP,
        GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
        MapName = "caldavar",
        GameModes = ["ap"],
        GameRegions = ["use"],
        Ranked = true,
        MatchFidelity = 1,
        BotDifficulty = 0,
        RandomizeBots = false
    };

    private static MatchmakingGroup CreateSoloGroup(int accountId, string name, double rating = 1500.0)
    {
        Account account = new()
        {
            ID = accountId,
            Name = name,
            User = new User
            {
                ID = accountId,
                EmailAddress = $"{name.ToLowerInvariant()}@kongor.net",
                Role = new DbRole { ID = 2, Name = "User" },
                SRPPasswordSalt = "salt",
                SRPPasswordHash = "hash",
                PBKDF2PasswordHash = "hash"
            }
        };

        MatchmakingGroupMember member = new(account)
        {
            Slot = 1,
            IsLeader = true,
            IsReady = true,
            IsInGame = false,
            IsEligibleForMatchmaking = true,
            Rating = rating
        };

        MatchmakingGroup group = new()
        {
            Members = [member],
            Information = CreateInformation()
        };

        return group;
    }

    [Fact]
    public void TryPartitionTeams_WhenNoBanConflicts_PartitionsEvenly()
    {
        MatchmakingGroup g1 = CreateSoloGroup(1, "Player1");
        MatchmakingGroup g2 = CreateSoloGroup(2, "Player2");
        MatchmakingGroup g3 = CreateSoloGroup(3, "Player3");
        MatchmakingGroup g4 = CreateSoloGroup(4, "Player4");

        List<MatchmakingGroup> groups = [g1, g2, g3, g4];

        bool success = MatchmakingService.TryPartitionTeams(groups, teamSize: 2, out List<MatchmakingGroup> team1, out List<MatchmakingGroup> team2);

        success.Should().BeTrue();
        team1.Should().HaveCount(2);
        team2.Should().HaveCount(2);
    }

    [Fact]
    public void TryPartitionTeams_WhenTwoPlayersMutuallyAvoidEachOther_PlacesThemOnOpposingTeams()
    {
        MatchmakingGroup g1 = CreateSoloGroup(1, "Player1");
        MatchmakingGroup g2 = CreateSoloGroup(2, "Player2");
        MatchmakingGroup g3 = CreateSoloGroup(3, "Player3");
        MatchmakingGroup g4 = CreateSoloGroup(4, "Player4");

        // Player1 has banned Player2
        g1.Members[0].Account.Peers.Add(new AccountPeer
        {
            AccountID = 1,
            PeerAccountID = 2,
            Type = PeerType.Banned,
            TimestampCreated = TimeProvider.System.GetUtcNow()
        });

        List<MatchmakingGroup> groups = [g1, g2, g3, g4];

        bool success = MatchmakingService.TryPartitionTeams(groups, teamSize: 2, out List<MatchmakingGroup> team1, out List<MatchmakingGroup> team2);

        success.Should().BeTrue();
        team1.Should().HaveCount(2);
        team2.Should().HaveCount(2);

        // Verify Player1 and Player2 are NOT on the same team
        bool team1HasP1 = team1.Exists(g => g.Members.Exists(m => m.Account.ID == 1));
        bool team1HasP2 = team1.Exists(g => g.Members.Exists(m => m.Account.ID == 2));
        bool team2HasP1 = team2.Exists(g => g.Members.Exists(m => m.Account.ID == 1));
        bool team2HasP2 = team2.Exists(g => g.Members.Exists(m => m.Account.ID == 2));

        ((team1HasP1 && team2HasP2) || (team1HasP2 && team2HasP1)).Should().BeTrue("Player1 and Player2 must be on opposing teams!");
    }

    [Fact]
    public void TryPartitionTeams_WhenTriangularBanPreventsValidTeams_ReturnsFalse()
    {
        MatchmakingGroup g1 = CreateSoloGroup(1, "Player1");
        MatchmakingGroup g2 = CreateSoloGroup(2, "Player2");
        MatchmakingGroup g3 = CreateSoloGroup(3, "Player3");
        MatchmakingGroup g4 = CreateSoloGroup(4, "Player4");

        // All pairs have ban conflicts
        g1.Members[0].Account.Peers.Add(new AccountPeer { AccountID = 1, PeerAccountID = 2, Type = PeerType.Banned, TimestampCreated = TimeProvider.System.GetUtcNow() });
        g2.Members[0].Account.Peers.Add(new AccountPeer { AccountID = 2, PeerAccountID = 3, Type = PeerType.Banned, TimestampCreated = TimeProvider.System.GetUtcNow() });
        g3.Members[0].Account.Peers.Add(new AccountPeer { AccountID = 3, PeerAccountID = 1, Type = PeerType.Banned, TimestampCreated = TimeProvider.System.GetUtcNow() });

        g4.Members[0].Account.Peers.Add(new AccountPeer { AccountID = 4, PeerAccountID = 1, Type = PeerType.Banned, TimestampCreated = TimeProvider.System.GetUtcNow() });
        g4.Members[0].Account.Peers.Add(new AccountPeer { AccountID = 4, PeerAccountID = 2, Type = PeerType.Banned, TimestampCreated = TimeProvider.System.GetUtcNow() });
        g4.Members[0].Account.Peers.Add(new AccountPeer { AccountID = 4, PeerAccountID = 3, Type = PeerType.Banned, TimestampCreated = TimeProvider.System.GetUtcNow() });

        List<MatchmakingGroup> groups = [g1, g2, g3, g4];

        bool success = MatchmakingService.TryPartitionTeams(groups, teamSize: 2, out List<MatchmakingGroup> team1, out List<MatchmakingGroup> team2);

        success.Should().BeFalse("All pairs have ban conflicts, so no valid 2v2 team partition exists.");
    }

    [Fact]
    public void TryPartitionTeams_WhenGroupHasInternalBanConflict_ReturnsFalse()
    {
        MatchmakingGroup group = CreateSoloGroup(1, "Player1");
        Account acc2 = new()
        {
            ID = 2,
            Name = "Player2",
            User = new User
            {
                ID = 2,
                EmailAddress = "player2@kongor.net",
                Role = new DbRole { ID = 2, Name = "User" },
                SRPPasswordSalt = "salt",
                SRPPasswordHash = "hash",
                PBKDF2PasswordHash = "hash"
            }
        };
        MatchmakingGroupMember member2 = new(acc2)
        {
            Slot = 2,
            IsLeader = false,
            IsReady = true,
            IsInGame = false,
            IsEligibleForMatchmaking = true,
            Rating = 1500.0
        };
        group.Members.Add(member2);

        // Player1 has banned Player2
        group.Members[0].Account.Peers.Add(new AccountPeer
        {
            AccountID = 1,
            PeerAccountID = 2,
            Type = PeerType.Banned,
            TimestampCreated = TimeProvider.System.GetUtcNow()
        });

        bool success = MatchmakingService.TryPartitionTeams([group], teamSize: 2, out _, out _);
        success.Should().BeFalse("Group internally contains a ban conflict between members.");
    }

    [Fact]
    public void TryPartitionTeams_5v5WithBannedRivals_PartitionsSuccessfully()
    {
        List<MatchmakingGroup> groups = [];
        for (int i = 1; i <= 10; i++)
        {
            groups.Add(CreateSoloGroup(i, $"Player{i}", 1500.0 + i * 5));
        }

        // Player 1 bans Player 2
        groups[0].Members[0].Account.Peers.Add(new AccountPeer
        {
            AccountID = 1,
            PeerAccountID = 2,
            Type = PeerType.Banned,
            TimestampCreated = TimeProvider.System.GetUtcNow()
        });

        bool success = MatchmakingService.TryPartitionTeams(groups, teamSize: 5, out List<MatchmakingGroup> team1, out List<MatchmakingGroup> team2);

        success.Should().BeTrue();
        team1.Should().HaveCount(5);
        team2.Should().HaveCount(5);

        bool team1HasP1 = team1.Exists(g => g.Members.Exists(m => m.Account.ID == 1));
        bool team1HasP2 = team1.Exists(g => g.Members.Exists(m => m.Account.ID == 2));
        (team1HasP1 ^ team1HasP2).Should().BeTrue("Player 1 and Player 2 must be on opposite teams!");
    }
}

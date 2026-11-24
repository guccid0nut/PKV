// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: RosterViewModelTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using FluentAssertions;
using ReplayStudio.GUI.Models;
using ReplayStudio.GUI.ViewModels;
using Xunit;

namespace ReplayStudio.Tests;

public class RosterViewModelTests
{
    [Fact]
    public void PopulateRoster_10Players_SeparatesLegionAndHellbourneCorrectly()
    {
        RosterViewModel vm = new();
        List<RosterPlayerItem> players = [];

        for (int i = 0; i < 10; i++)
        {
            players.Add(new RosterPlayerItem
            {
                ClientNum = i,
                Team = i < 5 ? 1 : 2,
                PlayerName = $"Player_{i}",
                HeroName = i < 5 ? "Hero_Devourer" : "Hero_Pyromancer",
                CleanHeroName = i < 5 ? "Devourer" : "Pyromancer",
                Level = 10,
                Gold = 5000
            });
        }

        vm.PopulateRoster(players);

        vm.Players.Should().HaveCount(10);
        vm.LegionPlayers.Should().HaveCount(5);
        vm.HellbournePlayers.Should().HaveCount(5);
        vm.LockedHeroSlot.Should().Be(0);
        vm.LockedPovPlayer.Should().Be(players[0]);
    }

    [Fact]
    public void LockPov_SetsActiveGlowOnTargetHero_ClearsOnOthers()
    {
        RosterViewModel vm = new();
        List<RosterPlayerItem> players = [];
        for (int i = 0; i < 10; i++)
        {
            players.Add(new RosterPlayerItem
            {
                ClientNum = i,
                Team = i < 5 ? 1 : 2,
                PlayerName = $"Player_{i}"
            });
        }
        vm.PopulateRoster(players);

        int lockedSlotNotification = -1;
        vm.PovLockChanged += (slot) => lockedSlotNotification = slot;

        // Lock to slot 7 (Hellbourne player)
        RosterPlayerItem target = players[7];
        vm.LockPov(target);

        vm.LockedHeroSlot.Should().Be(7);
        vm.LockedPovPlayer.Should().Be(target);
        lockedSlotNotification.Should().Be(7);

        target.IsPovLocked.Should().BeTrue();
        vm.Players.Where(p => p != target).All(p => !p.IsPovLocked).Should().BeTrue();
    }

    [Fact]
    public void UnlockPov_WhenInvoked_ClearsLockOnAllPlayers()
    {
        RosterViewModel vm = new();
        List<RosterPlayerItem> players = [];
        for (int i = 0; i < 10; i++)
        {
            players.Add(new RosterPlayerItem { ClientNum = i, Team = i < 5 ? 1 : 2 });
        }
        vm.PopulateRoster(players);

        int lockedSlotNotification = 99;
        vm.PovLockChanged += (slot) => lockedSlotNotification = slot;

        vm.UnlockPov();

        vm.LockedHeroSlot.Should().Be(-1);
        vm.LockedPovPlayer.Should().BeNull();
        lockedSlotNotification.Should().Be(-1);
        vm.Players.All(p => !p.IsPovLocked).Should().BeTrue();
    }

    [Fact]
    public void SelectPlayer_WhenInvoked_RaisesPlayerSelectedEvent()
    {
        RosterViewModel vm = new();
        RosterPlayerItem item = new() { ClientNum = 3, PlayerName = "MoonMeander" };

        RosterPlayerItem? selected = null;
        vm.PlayerSelected += (p) => selected = p;

        vm.SelectPlayer(item);

        vm.SelectedPlayer.Should().Be(item);
        selected.Should().Be(item);
    }

    [Fact]
    public void UpdateFromSnapshot_WhenUpdated_UpdatesHeroHpMpAndStatsAccurately()
    {
        RosterViewModel vm = new();
        List<RosterPlayerItem> players = [];
        for (int i = 0; i < 10; i++)
        {
            players.Add(new RosterPlayerItem { ClientNum = i, Team = i < 5 ? 1 : 2, PlayerName = $"P{i}" });
        }
        vm.PopulateRoster(players);

        GameStateSnapshot snap = new()
        {
            TimestampMs = 30000,
            ScoreLegion = 8,
            ScoreHellbourne = 5
        };

        for (int i = 0; i < 10; i++)
        {
            snap.Heroes[i] = new HeroSnapshot
            {
                ClientIndex = i,
                Team = i < 5 ? 1 : 2,
                Health = 750f,
                MaxHealth = 1000f,
                Mana = 300f,
                MaxMana = 500f,
                Level = 8,
                Kills = i,
                Deaths = 2,
                Assists = 4,
                Gold = 3500,
                NetWorth = 4200
            };
        }

        vm.UpdateFromSnapshot(snap);

        vm.LegionScore.Should().Be(8);
        vm.HellbourneScore.Should().Be(5);
        vm.LegionNetWorth.Should().Be(5 * 4200);
        vm.HellbourneNetWorth.Should().Be(5 * 4200);

        RosterPlayerItem p0 = vm.Players[0];
        p0.CurrentHp.Should().Be(750f);
        p0.CurrentMp.Should().Be(300f);
        p0.Level.Should().Be(8);
        p0.Kills.Should().Be(0);
        p0.Deaths.Should().Be(2);
        p0.Assists.Should().Be(4);
    }
}

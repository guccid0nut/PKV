// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: RosterViewModel.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Models;

namespace ReplayStudio.GUI.ViewModels;

/// <summary>
///     ViewModel managing the 10-player roster breakdown (5 Legion vs 5 Hellbourne),
///     live combat statistics, HP/Mana telemetry, and camera POV locking.
/// </summary>
public partial class RosterViewModel : ObservableObject
{
    private readonly ILogger<RosterViewModel> _logger;

    public ObservableCollection<RosterPlayerItem> Players { get; } = [];
    public ObservableCollection<RosterPlayerItem> LegionPlayers { get; } = [];
    public ObservableCollection<RosterPlayerItem> HellbournePlayers { get; } = [];

    [ObservableProperty] private RosterPlayerItem? _selectedPlayer;
    [ObservableProperty] private RosterPlayerItem? _lockedPovPlayer;
    [ObservableProperty] private int _lockedHeroSlot = -1;
    [ObservableProperty] private int _legionScore;
    [ObservableProperty] private int _hellbourneScore;
    [ObservableProperty] private int _legionNetWorth;
    [ObservableProperty] private int _hellbourneNetWorth;

    public event Action<int>? PovLockChanged;
    public event Action<RosterPlayerItem?>? PlayerSelected;

    public RosterViewModel(ILogger<RosterViewModel>? logger = null)
    {
        _logger = logger ?? NullLogger<RosterViewModel>.Instance;
    }

    /// <summary>
    ///     Populates and partitions the 10-player roster into Legion (Team 1) and Hellbourne (Team 2).
    /// </summary>
    public void PopulateRoster(IEnumerable<RosterPlayerItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Players.Clear();
        LegionPlayers.Clear();
        HellbournePlayers.Clear();

        foreach (RosterPlayerItem item in items)
        {
            Players.Add(item);
            if (item.Team == 1)
            {
                LegionPlayers.Add(item);
            }
            else
            {
                HellbournePlayers.Add(item);
            }
        }

        if (Players.Count > 0 && LockedHeroSlot < 0)
        {
            LockPov(Players[0]);
        }

        _logger.LogInformation("[RosterViewModel] Populated roster with {Total} players ({Legion} Legion, {Hellbourne} Hellbourne)",
            Players.Count, LegionPlayers.Count, HellbournePlayers.Count);
    }

    /// <summary>
    ///     Locks the director camera to follow the target player's hero position and heading.
    /// </summary>
    [RelayCommand]
    public void LockPov(RosterPlayerItem? player)
    {
        if (player == null)
        {
            UnlockPov();
            return;
        }

        foreach (RosterPlayerItem p in Players)
        {
            p.IsPovLocked = (p == player);
        }

        LockedPovPlayer = player;
        LockedHeroSlot = player.ClientNum;
        _logger.LogInformation("[RosterViewModel] Locked camera POV to Player {ClientNum} ({PlayerName} - {HeroName})",
            player.ClientNum, player.PlayerName, player.HeroName);

        PovLockChanged?.Invoke(player.ClientNum);
    }

    /// <summary>
    ///     Clears active POV lock on all roster players.
    /// </summary>
    [RelayCommand]
    public void UnlockPov()
    {
        foreach (RosterPlayerItem p in Players)
        {
            p.IsPovLocked = false;
        }

        LockedPovPlayer = null;
        LockedHeroSlot = -1;
        _logger.LogInformation("[RosterViewModel] Unlocked camera POV");
        PovLockChanged?.Invoke(-1);
    }

    /// <summary>
    ///     Selects a player in the roster panel for inspection.
    /// </summary>
    [RelayCommand]
    public void SelectPlayer(RosterPlayerItem? player)
    {
        SelectedPlayer = player;
        PlayerSelected?.Invoke(player);
    }

    /// <summary>
    ///     Updates live hero stats and HP/Mana telemetry from the current game snapshot.
    /// </summary>
    public void UpdateFromSnapshot(GameStateSnapshot snapshot)
    {
        if (snapshot == null) return;

        LegionScore = snapshot.ScoreLegion;
        HellbourneScore = snapshot.ScoreHellbourne;

        int legionNw = 0;
        int hellbourneNw = 0;

        for (int i = 0; i < 10; i++)
        {
            HeroSnapshot hero = snapshot.Heroes[i];
            if (hero == null) continue;

            RosterPlayerItem? player = Players.FirstOrDefault(p => p.ClientNum == hero.ClientIndex);
            if (player != null)
            {
                player.UpdateFromRenderState(
                    ReplaySnapshotEngine.CreateRenderState(hero),
                    hero.Level,
                    hero.Kills,
                    hero.Deaths,
                    hero.Assists,
                    hero.Gold,
                    hero.GoldPerMinute,
                    hero.ExpPerMinute);

                if (hero.Items != null && hero.Items.Length > 0)
                {
                    player.Items = hero.Items;
                }

                if (player.Team == 1)
                {
                    legionNw += hero.NetWorth > 0 ? hero.NetWorth : hero.Gold;
                }
                else
                {
                    hellbourneNw += hero.NetWorth > 0 ? hero.NetWorth : hero.Gold;
                }
            }
        }

        LegionNetWorth = legionNw;
        HellbourneNetWorth = hellbourneNw;
    }
}

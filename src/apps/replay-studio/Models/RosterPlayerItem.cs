// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: RosterPlayerItem.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using CommunityToolkit.Mvvm.ComponentModel;

namespace ReplayStudio.GUI.Models;

/// <summary>
///     Observable ViewModel item for individual players in the 10-player roster breakdown panel.
/// </summary>
public partial class RosterPlayerItem : ObservableObject
{
    [ObservableProperty] private int _clientNum;
    [ObservableProperty] private int _team; // 1 = Legion, 2 = Hellbourne
    [ObservableProperty] private string _playerName = string.Empty;
    [ObservableProperty] private string _heroName = string.Empty;
    [ObservableProperty] private string _cleanHeroName = string.Empty;
    [ObservableProperty] private string _heroIconPath = string.Empty;
    [ObservableProperty] private int _level = 1;
    [ObservableProperty] private int _kills;
    [ObservableProperty] private int _deaths;
    [ObservableProperty] private int _assists;
    [ObservableProperty] private int _gold;
    [ObservableProperty] private int _netWorth;
    [ObservableProperty] private int _gpm;
    [ObservableProperty] private int _xpm;
    [ObservableProperty] private float _currentHp = 1000f;
    [ObservableProperty] private float _maxHp = 1000f;
    [ObservableProperty] private float _hpPercent = 100f;
    [ObservableProperty] private float _currentMp = 500f;
    [ObservableProperty] private float _maxMp = 500f;
    [ObservableProperty] private float _mpPercent = 100f;
    [ObservableProperty] private bool _isAlive = true;
    [ObservableProperty] private int _respawnTimerSeconds;
    [ObservableProperty] private bool _isPovLocked;
    [ObservableProperty] private string[] _items = new string[6];

    public string TeamName => Team == 1 ? "Legion" : "Hellbourne";
    public string KdaFormatted => $"{Kills}/{Deaths}/{Assists}";

    public void UpdateFromRenderState(in HeroRenderState state, int level, int kills, int deaths, int assists, int gold, int gpm, int xpm)
    {
        CurrentHp = state.Health;
        MaxHp = state.MaxHealth;
        HpPercent = state.HealthPercent;
        CurrentMp = state.Mana;
        MaxMp = state.MaxMana;
        MpPercent = state.ManaPercent;
        IsAlive = state.IsAlive;
        Level = level;
        Kills = kills;
        Deaths = deaths;
        Assists = assists;
        Gold = gold;
        Gpm = gpm;
        Xpm = xpm;
        OnPropertyChanged(nameof(KdaFormatted));
        OnPropertyChanged(nameof(TeamName));
    }
}

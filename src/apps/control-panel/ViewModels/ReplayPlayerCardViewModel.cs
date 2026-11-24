// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using CommunityToolkit.Mvvm.ComponentModel;
using NEXUS.ControlPanel.GUI.Services;

namespace NEXUS.ControlPanel.GUI.ViewModels;

public sealed partial class ReplayPlayerCardViewModel(ParsedReplayPlayer model) : ViewModelBase
{
    [ObservableProperty] private bool _isSelected;

    public ParsedReplayPlayer Model { get; } = model;

    public int AccountId => Model.AccountId;
    public int PlayerIndex => Model.PlayerIndex;
    public string Name => Model.Name;
    public string HeroName => Model.HeroName;
    public string HeroDisplayName => Model.HeroDisplayName;
    public string HeroIconUrl => Model.HeroIconUrl;
    public int Team => Model.Team;
    public int Level => Model.Level;
    public int Kills => Model.Kills;
    public int Deaths => Model.Deaths;
    public int Assists => Model.Assists;
    public int Gold => Model.Gold;
    public int HeroDamage => Model.HeroDamage;
    public int CreepKills => Model.CreepKills;
    public int Denies => Model.Denies;
    public int Mmr => Model.Mmr;

    public string KdaDisplay => $"{Kills}/{Deaths}/{Assists}";
    public string LevelDisplay => $"Lv. {Level}";
    public string TeamNameDisplay => Team == 1 ? "Legion" : "Hellbourne";
    public string TeamColorHex => Team == 1 ? "#00F0FF" : "#FF0055";
}

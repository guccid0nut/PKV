// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.DTOs;

public sealed record CustomStatColumnDto(
    string Key,
    string Header,
    string Label,
    string Format,
    bool HigherIsBetter);

public sealed record CustomMatchBannerDto(
    int MatchId,
    string MapSlug,
    string MapDisplayName,
    string SchemaKey,
    short SchemaVersion,
    bool Is10v10,
    bool IsCoOpPvE,
    int PlayerCount,
    int ScoreTeam1,
    int ScoreTeam2,
    string Team1Label,
    string Team2Label,
    string ScoreSummaryText,
    int DurationSeconds,
    string GameMode,
    int? WaveReached = null,
    int? MaxWaves = null,
    string? Difficulty = null,
    int? LegionLivesLeft = null,
    int? HellbourneLivesLeft = null);

public sealed record CustomMatchPlayerDto(
    int AccountId,
    string Nickname,
    string ClanTag,
    int Team,
    int Position,
    bool Win,
    bool Disconnect,
    string HeroIdentifier,
    string HeroDisplayName,
    string HeroIconVfs,
    string? AltAvatarName,
    bool IsCustomUnit,
    int HeroLevel,
    int Kills,
    int Deaths,
    int Assists,
    int CreepKills,
    int Denies,
    int NeutralKills,
    int Gold,
    double Gpm,
    double Xpm,
    int WardsPlaced,
    int HeroDamage,
    int BuildingDamage,
    int Actions,
    double Apm,
    int SecondsPlayed,
    bool Mvp,
    IReadOnlyList<string> Items,
    IReadOnlyDictionary<string, double> CustomValues,
    IReadOnlyDictionary<string, string> FormattedColumns);

public sealed record CustomMatchStatsResponseDto(
    int MatchId,
    CustomMatchBannerDto Banner,
    IReadOnlyList<CustomStatColumnDto> Columns,
    IReadOnlyList<CustomMatchPlayerDto> Players);

public sealed record PlayerMapHeroSummaryDto(
    string HeroIdentifier,
    string HeroDisplayName,
    string HeroIconVfs,
    int MatchesPlayed,
    bool IsCustomUnit);

public sealed record PlayerMapSummaryCardDto(
    string Key,
    string Label,
    string ShortHeader,
    string TotalFormatted,
    string AverageFormatted,
    double RawTotal,
    double RawAverage);

public sealed record PlayerMapRecordItemDto(
    string Key,
    string Label,
    string FormattedValue,
    double RawValue);

public sealed record PlayerMapRecentMatchSummaryDto(
    int MatchId,
    DateTimeOffset PlayedAtUtc,
    int DurationSeconds,
    bool Win,
    string HeroIdentifier,
    string HeroDisplayName,
    string HeroIconVfs,
    int Kills,
    int Deaths,
    int Assists,
    string ScoreSummaryText);

public sealed record PlayerMapStatsResponseDto(
    int AccountId,
    string Nickname,
    string MapSlug,
    string MapDisplayName,
    string SchemaKey,
    short SchemaVersion,
    int MatchesPlayed,
    int MatchesWon,
    int MatchesLost,
    double WinRatePercent,
    long TimePlayedSeconds,
    int HeroKills,
    int Deaths,
    int HeroAssists,
    string? FavoriteHeroIdentifier,
    IReadOnlyList<PlayerMapHeroSummaryDto> TopHeroes,
    IReadOnlyList<PlayerMapSummaryCardDto> SummaryCards,
    IReadOnlyList<PlayerMapRecordItemDto> BestRecords,
    IReadOnlyList<PlayerMapRecentMatchSummaryDto> RecentMatches);

public sealed record PlayerMapOverviewItemDto(
    string MapSlug,
    string MapDisplayName,
    string SchemaKey,
    int MatchesPlayed,
    int MatchesWon,
    int MatchesLost,
    double WinRatePercent,
    DateTimeOffset LastPlayedAtUtc);

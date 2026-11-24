// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Constants;

/// <summary>
///     Defines canonical protocol dictionary keys and descriptive names for the match_stats payload submitted by game
///     servers.
/// </summary>
public static class MatchStatsProtocolKeys
{
    public const string MatchId = "match_id";
    public const string Map = "map";
    public const string MapVersion = "map_version";
    public const string TimePlayed = "time_played";
    public const string FileSize = "file_size";
    public const string FileName = "file_name";
    public const string ConnectionState = "c_state";
    public const string Version = "version";
    public const string AverageSkillRating = "avgpsr";
    public const string AverageSkillRatingTeamOne = "avgpsr_team1";
    public const string AverageSkillRatingTeamTwo = "avgpsr_team2";
    public const string GameMode = "gamemode";
    public const string TeamScoreGoal = "teamscoregoal";
    public const string PlayerScoreGoal = "playerscoregoal";
    public const string NumberOfRounds = "numrounds";
    public const string ReleaseStage = "release_stage";
    public const string BannedHeroes = "banned_heroes";
    public const string EventId = "event_id";
    public const string MatchupId = "matchup_id";
    public const string MostValuablePlayer = "mvp";
}

// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Collections.Specialized;

namespace KONGOR.MasterServer.Models.RequestResponse.Stats;

/// <summary>
///     The match history overview response, containing a variable number of match entries alongside fixed metadata.
/// </summary>
public sealed class MatchHistoryOverviewResponse : IPhpSerializable
{
    /// <summary>
    ///     The match entries, serialised as "m0", "m1", "m2", etc. in the PHP array.
    /// </summary>
    public required List<MatchHistoryOverviewEntry> Entries { get; init; }

    /// <summary>
    ///     The minimum number of matches a free-to-play (trial) account must complete to become verified.
    /// </summary>
    public int VestedThreshold => 5;

    /// <summary>
    ///     Unknown. Set to true on successful response.
    /// </summary>
    public bool Zero => true;

    public void SerializePhp(ref Utf8PhpWriter writer)
    {
        OrderedDictionary result = ToDictionary();
        PhpSerializer.SerializeValue(ref writer, result);
    }

    public OrderedDictionary ToDictionary()
    {
        OrderedDictionary result = [];

        for (int index = 0; index < Entries.Count; index++) result.Add($"m{index}", Entries[index].ToString());

        result.Add("vested_threshold", VestedThreshold);
        result.Add(0, Zero);

        return result;
    }

    /// <summary>
    ///     Serialises this response as a flat PHP associative array with "m{N}" keys for each entry, a "vested_threshold"
    ///     string key, and a 0 integer key.
    /// </summary>
    public string Serialise()
    {
        return PhpSerialization.Serialize(ToDictionary());
    }
}

/// <summary>
///     A single match entry in the match history overview response.
///     Keyed as "m0", "m1", "m2", etc. in the serialised PHP array.
/// </summary>
public partial class MatchHistoryOverviewEntry
{
    [PHPProperty("match_id")] public required string MatchID { get; init; }

    [PHPProperty("wins")] public required string Wins { get; init; }

    [PHPProperty("team")] public required string Team { get; init; }

    [PHPProperty("herokills")] public required string HeroKills { get; init; }

    [PHPProperty("deaths")] public required string Deaths { get; init; }

    [PHPProperty("heroassists")] public required string HeroAssists { get; init; }

    [PHPProperty("hero_id")] public required string HeroID { get; init; }

    [PHPProperty("secs")] public required string SecondsPlayed { get; init; }

    [PHPProperty("map")] public required string Map { get; init; }

    [PHPProperty("mdt")] public required string MatchDatetime { get; init; }

    [PHPProperty("hero_cli_name")] public required string HeroClientName { get; init; }

    public override string ToString()
    {
        return
            $"{MatchID},{Wins},{Team},{HeroKills},{Deaths},{HeroAssists},{HeroID},{SecondsPlayed},{Map},{MatchDatetime},{HeroClientName}";
    }
}

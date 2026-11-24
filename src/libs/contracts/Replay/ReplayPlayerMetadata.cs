// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Replay;

/// <summary>
///     Represents individual player metadata and match statistics extracted from the .honreplay replayinfo XML.
/// </summary>
public sealed class ReplayPlayerMetadata
{
    public string Name { get; set; } = string.Empty;
    public long AccountId { get; set; }
    public int Team { get; set; } // 1 = Legion, 2 = Hellbourne
    public int TeamIndex { get; set; } // 0..4 within team
    public int ClientNum { get; set; } // 0..9 in match
    public string HeroName { get; set; } = string.Empty;
    public string HeroIcon { get; set; } = string.Empty;
    public string Avatar { get; set; } = string.Empty;

    // Detailed Match Statistics
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public long HeroDamage { get; set; }
    public long TowerDamage { get; set; }
    public long CreepDamage { get; set; }
    public int Gold { get; set; }
    public int GoldPerMinute { get; set; }
    public int ExpPerMinute { get; set; }
    public int Level { get; set; }
    public int Denies { get; set; }
    public int Creeps { get; set; }
    public int Actions { get; set; }
    public double APM { get; set; }
    public int Wards { get; set; }
    public int Consumables { get; set; }
    public int Smackdowns { get; set; }
    public int Humiliations { get; set; }
    public int Disconnects { get; set; }

    /// <summary>
    ///     Friendly team name ("Legion" for Team 1, "Hellbourne" for Team 2).
    /// </summary>
    public string TeamName => Team switch
    {
        1 => "Legion",
        2 => "Hellbourne",
        _ => "Spectator"
    };

    public bool IsLegion => Team == 1;
    public bool IsHellbourne => Team == 2;

    /// <summary>
    ///     Clean hero name stripped of "Hero_" prefix (e.g. "Hero_BabaYaga" -> "BabaYaga").
    /// </summary>
    public string CleanHeroName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(HeroName)) return string.Empty;

            if (HeroName.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)) return HeroName.Substring(5);

            return HeroName;
        }
    }

    /// <summary>
    ///     Formatted Kill/Death/Assist string (e.g. "12/2/15").
    /// </summary>
    public string FormattedKda => $"{Kills}/{Deaths}/{Assists}";

    public override string ToString()
    {
        return
            $"Player[ClientNum={ClientNum}, Team={TeamName}({TeamIndex}), Name='{Name}', Hero='{CleanHeroName}', KDA={FormattedKda}, GPM={GoldPerMinute}]";
    }
}

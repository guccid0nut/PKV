// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace MERRICK.DatabaseContext.Entities.Statistics;

/// <summary>
///     Represents an individual hero ban during a competitive or ranked draft phase.
///     Normalized 1NF relational entity mapped to <c>stat.MatchBans</c>.
/// </summary>
[Table("MatchBans", Schema = "stat")]
[Index(nameof(HeroIdentifier))]
[Index(nameof(MatchID), nameof(BanOrder))]
[Index(nameof(MatchID), nameof(HeroIdentifier), IsUnique = true)]
public class MatchBan
{
    [Key]
    public int ID { get; set; }

    [NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    public int Id
    {
        get => ID;
        set => ID = value;
    }

    public int MatchID { get; set; }

    [ForeignKey(nameof(MatchID))]
    [InverseProperty(nameof(MatchStatistics.Bans))]
    [System.Text.Json.Serialization.JsonIgnore]
    public MatchStatistics Match { get; set; } = null!;

    /// <summary>
    ///     The team that issued the ban: 1 = Legion, 2 = Hellbourne (or 0 if unassigned).
    /// </summary>
    public int Team { get; set; }

    /// <summary>
    ///     The sequential ban order in the draft (1-based index: 1, 2, 3, ...).
    /// </summary>
    public int BanOrder { get; set; }

    /// <summary>
    ///     The normalized hero identifier (e.g. "Hero_Pyromancer", "Hero_Devourer").
    /// </summary>
    [MaxLength(50)]
    public string HeroIdentifier { get; set; } = string.Empty;

    public override string ToString() => $"[Match #{MatchID} Ban #{BanOrder} (Team {Team})] {HeroIdentifier}";

    public override bool Equals(object? obj)
    {
        if (obj is MatchBan other)
        {
            return MatchID == other.MatchID &&
                   BanOrder == other.BanOrder &&
                   string.Equals(HeroIdentifier, other.HeroIdentifier, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public override int GetHashCode() =>
        HashCode.Combine(MatchID, BanOrder, StringComparer.OrdinalIgnoreCase.GetHashCode(HeroIdentifier));
}

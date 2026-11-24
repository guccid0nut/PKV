// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MERRICK.DatabaseContext.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace MERRICK.DatabaseContext.Entities.Statistics;

/// <summary>
///     Normalized 1NF relational entity representing an individual kill / frag
///     event in a match. Mapped to <c>stat.MatchFrags</c>.
/// </summary>
[Table("MatchFrags", Schema = "stat")]
[Index(nameof(MatchID), nameof(GameTimeSeconds))]
[Index(nameof(KillerAccountID))]
[Index(nameof(VictimAccountID))]
public class MatchFrag
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
    [System.Text.Json.Serialization.JsonIgnore]
    public MatchStatistics Match { get; set; } = null!;

    public int KillerAccountID { get; set; }

    [ForeignKey(nameof(KillerAccountID))]
    [System.Text.Json.Serialization.JsonIgnore]
    public Account? KillerAccount { get; set; }

    public int VictimAccountID { get; set; }

    [ForeignKey(nameof(VictimAccountID))]
    [System.Text.Json.Serialization.JsonIgnore]
    public Account? VictimAccount { get; set; }

    /// <summary>
    ///     Timestamp in seconds from game start when the frag occurred.
    /// </summary>
    public int GameTimeSeconds { get; set; }

    /// <summary>
    ///     True if this frag was the First Blood of the match.
    /// </summary>
    public bool FirstBlood { get; set; }

    [InverseProperty(nameof(MatchFragAssist.Frag))]
    public List<MatchFragAssist> Assists { get; set; } = [];

    [NotMapped]
    public int SourceID
    {
        get => KillerAccountID;
        set => KillerAccountID = value;
    }

    [NotMapped]
    public int TargetID
    {
        get => VictimAccountID;
        set => VictimAccountID = value;
    }

    [NotMapped]
    public int GameTime
    {
        get => GameTimeSeconds;
        set => GameTimeSeconds = value;
    }

    [NotMapped]
    public List<int>? SupporterIDs
    {
        get => Assists.Select(a => a.AssistingAccountID).ToList();
        set
        {
            Assists.Clear();
            if (value != null)
            {
                foreach (int assistId in value)
                {
                    Assists.Add(new MatchFragAssist
                    {
                        FragID = ID,
                        AssistingAccountID = assistId
                    });
                }
            }
        }
    }
}

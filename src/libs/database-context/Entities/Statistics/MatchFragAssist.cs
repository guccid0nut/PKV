// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MERRICK.DatabaseContext.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace MERRICK.DatabaseContext.Entities.Statistics;

/// <summary>
///     Normalized 1NF relational entity representing an assisting player on a kill / frag event.
///     Mapped to <c>stat.MatchFragAssists</c>.
/// </summary>
[Table("MatchFragAssists", Schema = "stat")]
[Index(nameof(FragID))]
[Index(nameof(AssistingAccountID))]
[Index(nameof(FragID), nameof(AssistingAccountID), IsUnique = true)]
public class MatchFragAssist
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

    public int FragID { get; set; }

    [ForeignKey(nameof(FragID))]
    [System.Text.Json.Serialization.JsonIgnore]
    public MatchFrag Frag { get; set; } = null!;

    public int AssistingAccountID { get; set; }

    [ForeignKey(nameof(AssistingAccountID))]
    [System.Text.Json.Serialization.JsonIgnore]
    public Account? AssistingAccount { get; set; }
}

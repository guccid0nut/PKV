// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Statistics;

/// <summary>
///     Per-account, per-map lifetime career rollup and personal best records for custom maps and MOBA variants.
///     Populated transactionally on match completion without mutating core Ranked FoC/MidWars MMR buckets.
/// </summary>
[Table("AccountMapStatistics", Schema = "data")]
[Index(nameof(AccountID), nameof(MapSlug), IsUnique = true, Name = "UX_AccountMapStatistics_AccountID_MapSlug")]
[Index(nameof(MapSlug), nameof(MatchesWon), Name = "IX_AccountMapStatistics_MapSlug_MatchesWon")]
public class AccountMapStatistics
{
    [Key]
    public long ID { get; set; }

    public int AccountID { get; set; }

    [ForeignKey(nameof(AccountID))]
    public Account? Account { get; set; }

    [MaxLength(50)]
    public required string MapSlug { get; set; }

    [MaxLength(32)]
    public string SchemaKey { get; set; } = "moba";

    public int MatchesPlayed { get; set; }

    public int MatchesWon { get; set; }

    public int MatchesLost { get; set; }

    public int MatchesConceded { get; set; }

    public int Disconnects { get; set; }

    public long TimePlayedSeconds { get; set; }

    public int HeroKills { get; set; }

    public int Deaths { get; set; }

    public int HeroAssists { get; set; }

    [MaxLength(64)]
    public string? FavoriteHeroIdentifier { get; set; }

    /// <summary>
    ///     JSON dictionary mapping raw/canonical hero identifiers played on this map to match counts.
    /// </summary>
    public string HeroPlayCountsJson { get; set; } = "{}";

    /// <summary>
    ///     JSON dictionary mapping cumulative career totals per schema metric key (e.g., <c>goals</c>, <c>hook_hits</c>).
    /// </summary>
    public string CustomTotals { get; set; } = "{}";

    /// <summary>
    ///     JSON dictionary mapping dedicated-server personal best single-match records (e.g., <c>wave_reached</c>, <c>goals</c>).
    /// </summary>
    public string BestRecords { get; set; } = "{}";

    public int? LastMatchID { get; set; }

    public DateTimeOffset LastPlayedAtUtc { get; set; } = TimeProvider.System.GetUtcNow();
}

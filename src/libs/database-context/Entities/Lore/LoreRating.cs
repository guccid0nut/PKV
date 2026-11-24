// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Lore;

[Table("LoreRatings", Schema = "misc")]
[Index(nameof(ComicID), nameof(AccountID), IsUnique = true)]
[Index(nameof(ComicID))]
[Index(nameof(AccountID))]
public class LoreRating
{
    [Key] public int ID { get; set; }

    public int ComicID { get; set; }

    [ForeignKey(nameof(ComicID))] public LoreComic Comic { get; set; } = null!;

    public int AccountID { get; set; }

    [ForeignKey(nameof(AccountID))] public Account Account { get; set; } = null!;

    [Range(1, 5)] public int Score { get; set; }

    public DateTimeOffset Timestamp { get; set; } = TimeProvider.System.GetUtcNow();
}

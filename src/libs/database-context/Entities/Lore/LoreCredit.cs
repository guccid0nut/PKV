// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Lore;

[Table("LoreCredits", Schema = "misc")]
[Index(nameof(ComicID))]
[Index(nameof(AccountID))]
public class LoreCredit
{
    [Key] public int ID { get; set; }

    public int ComicID { get; set; }

    [ForeignKey(nameof(ComicID))] public LoreComic Comic { get; set; } = null!;

    public int? AccountID { get; set; }

    [ForeignKey(nameof(AccountID))] public Account? Account { get; set; }

    [MaxLength(80)] public required string Role { get; set; }

    [MaxLength(100)] public string? CustomNameOverride { get; set; }

    public int OrderIndex { get; set; } = 0;
}

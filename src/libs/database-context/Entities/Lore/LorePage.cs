// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Lore;

[Table("LorePages", Schema = "misc")]
[Index(nameof(ComicID), nameof(PageNumber), IsUnique = true)]
public class LorePage
{
    [Key] public int ID { get; set; }

    public int ComicID { get; set; }

    [ForeignKey(nameof(ComicID))] public LoreComic Comic { get; set; } = null!;

    public int PageNumber { get; set; }

    [MaxLength(500)] public required string ImageUrl { get; set; }

    [MaxLength(4000)] public string? PanelHotspotsJson { get; set; }

    [MaxLength(4000)] public string? DialogueTranscript { get; set; }
}

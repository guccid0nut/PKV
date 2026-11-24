// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Lore;

[Table("LoreComics", Schema = "misc")]
[Index(nameof(Slug), IsUnique = true)]
[Index(nameof(ArcID), nameof(IssueNumber))]
public class LoreComic
{
    [Key] public int ID { get; set; }

    public int ArcID { get; set; }

    [ForeignKey(nameof(ArcID))] public LoreArc Arc { get; set; } = null!;

    [MaxLength(150)] public required string Title { get; set; }

    [MaxLength(150)] public required string Slug { get; set; }

    public int IssueNumber { get; set; } = 1;

    [MaxLength(3000)] public required string Synopsis { get; set; }

    [MaxLength(500)] public required string CoverImageUrl { get; set; }

    [MaxLength(500)] public string? CinematicVideoUrl { get; set; }

    public bool IsPublished { get; set; } = true;

    public int ViewCount { get; set; } = 0;

    public DateTimeOffset TimestampCreated { get; set; } = TimeProvider.System.GetUtcNow();

    public DateTimeOffset? TimestampPublished { get; set; }

    public ICollection<LorePage> Pages { get; set; } = [];

    public ICollection<LoreCredit> Credits { get; set; } = [];

    public ICollection<LoreRating> Ratings { get; set; } = [];
}

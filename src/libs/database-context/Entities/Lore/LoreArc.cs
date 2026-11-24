// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Lore;

[Table("LoreArcs", Schema = "misc")]
[Index(nameof(Slug), IsUnique = true)]
[Index(nameof(OrderIndex))]
public class LoreArc
{
    [Key] public int ID { get; set; }

    [MaxLength(100)] public required string Title { get; set; }

    [MaxLength(100)] public required string Slug { get; set; }

    [MaxLength(2000)] public required string Description { get; set; }

    [MaxLength(500)] public string? BannerImageUrl { get; set; }

    public int OrderIndex { get; set; }

    public bool IsPublished { get; set; } = true;

    public DateTimeOffset TimestampCreated { get; set; } = TimeProvider.System.GetUtcNow();

    public ICollection<LoreComic> Comics { get; set; } = [];
}

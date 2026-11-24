// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Core;

[Table("UserWebRewards")]
[Index(nameof(UserID))]
[Index(nameof(UserID), nameof(RewardType))]
[Index(nameof(UserID), nameof(IsEquipped))]
public class UserWebReward
{
    [Key] public int Id { get; set; }

    public int UserID { get; set; }

    public User User { get; set; } = null!;

    public int? AccountID { get; set; }

    [MaxLength(64)]
    public string RewardType { get; set; } = string.Empty; // "Border", "Title", "Badge", "Theme", "Flair"

    [MaxLength(128)]
    public string RewardCode { get; set; } = string.Empty; // e.g. "border_flaming_kongor", "title_legion_veteran"

    [MaxLength(128)] public string Name { get; set; } = string.Empty; // Display name

    [MaxLength(256)] public string? Description { get; set; }

    public bool IsEquipped { get; set; }

    [MaxLength(64)] public string Source { get; set; } = "Plinko"; // "Plinko", "Event", "Staff", "Achievement"

    public DateTimeOffset TimestampAcquired { get; set; } = TimeProvider.System.GetUtcNow();

    public DateTimeOffset? TimestampExpires { get; set; }

    [MaxLength(512)] public string? MetadataJson { get; set; }
}

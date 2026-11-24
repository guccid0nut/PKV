// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Game;

[Index(nameof(HeroIdentifier), nameof(AccountID), IsUnique = true)]
[Index(nameof(HeroIdentifier))]
public class HeroDifficultyVote
{
    [Key] public int ID { get; set; }

    [MaxLength(50)] public required string HeroIdentifier { get; set; }

    public required int AccountID { get; set; }

    public required int Rating { get; set; }

    public DateTimeOffset Timestamp { get; set; } = TimeProvider.System.GetUtcNow();
}

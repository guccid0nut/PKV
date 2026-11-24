// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Statistics;

public class AbilityEvent
{
    [MaxLength(100)] public string HeroName { get; set; } = string.Empty;

    [MaxLength(100)] public string AbilityName { get; set; } = string.Empty;

    public int GameTimeSeconds { get; set; }

    public byte SlotIndex { get; set; }

    [NotMapped]
    public string AbilityIdentifier
    {
        get => AbilityName;
        set => AbilityName = value;
    }

    [NotMapped]
    public int GameTime
    {
        get => GameTimeSeconds;
        set => GameTimeSeconds = value;
    }

    [NotMapped] public int Level { get; set; }
}

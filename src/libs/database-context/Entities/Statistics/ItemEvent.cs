// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Statistics;

public class ItemEvent
{
    [MaxLength(100)] public string ItemName { get; set; } = string.Empty;

    public int GameTimeSeconds { get; set; }

    public byte EventType { get; set; }

    [NotMapped]
    public string ItemIdentifier
    {
        get => ItemName;
        set => ItemName = value;
    }

    [NotMapped]
    public int GameTime
    {
        get => GameTimeSeconds;
        set => GameTimeSeconds = value;
    }

    [NotMapped]
    public string Action
    {
        get => EventType switch
        {
            1 => "Purchased",
            2 => "Sold",
            3 => "Used",
            _ => EventType.ToString()
        };
        set
        {
            EventType = value switch
            {
                "Purchased" => 1,
                "Sold" => 2,
                "Used" => 3,
                _ => 0
            };
        }
    }
}

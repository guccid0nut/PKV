// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Game;

public class MatchmakingQueueWaitEntry
{
    [Key] public long ID { get; set; }

    [MaxLength(50)] public string QueueType { get; set; } = string.Empty;

    public int PlayerCount { get; set; }

    public double WaitDurationSeconds { get; set; }

    [NotMapped]
    public TimeSpan AverageWaitTime
    {
        get => TimeSpan.FromSeconds(WaitDurationSeconds);
        set => WaitDurationSeconds = value.TotalSeconds;
    }

    public DateTimeOffset TimestampMatched { get; set; } = TimeProvider.System.GetUtcNow();
}

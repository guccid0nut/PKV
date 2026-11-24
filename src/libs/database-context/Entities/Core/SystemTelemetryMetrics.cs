// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Core;

public class SystemTelemetryMetrics
{
    [Key] public int ID { get; set; }

    public int ConcurrentConnectedUsers { get; set; }

    public int ActiveMatchServers { get; set; }

    public int ActiveMatchmakingQueues { get; set; }

    public double AverageWaitTimeSeconds { get; set; }

    [MaxLength(4096)] public string ActiveRegionsJson { get; set; } = "[]";

    public DateTimeOffset TimestampLastUpdated { get; set; } = TimeProvider.System.GetUtcNow();
}

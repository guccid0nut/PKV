// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.Telemetry;

/// <summary>
///     Operational and infrastructure telemetry event classifications.
/// </summary>
public enum TelemetryEventType
{
    ClientConnected,
    ClientDisconnected,
    MatchServerConnected,
    MatchServerDisconnected,
    QueueStateChanged,
    BaselineReset
}

/// <summary>
///     Structured message contract for publishing and subscribing to ecosystem telemetry events.
/// </summary>
[MemoryPackable]
public sealed partial record TelemetryEventMessage(
    TelemetryEventType EventType,
    Guid PublisherInstanceId,
    string? StringIdentifier = null,
    int NumericId = 0,
    string? Location = null,
    DateTimeOffset Timestamp = default
);

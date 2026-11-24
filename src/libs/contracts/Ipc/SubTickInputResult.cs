// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SubTickInputResult.cs
// System: NEXUS Contracts - IPC Sub-Tick Input Protocols
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace NEXUS.Contracts.Ipc;

/// <summary>
///     Result of an injected sub-tick input command, capturing hardware-level microsecond latency.
/// </summary>
public sealed record SubTickInputResult(
    bool Success,
    uint InputSequence,
    float MeasuredLatencyMs,
    long TimestampQpc,
    string? Message = null);

/// <summary>
///     Payload for injecting sub-tick input commands over IPC.
/// </summary>
public sealed class SubTickInputPayloadDto
{
    public string Action { get; set; } = string.Empty;
    public long HardwareTimestampQpc { get; set; }
    public uint InputSequence { get; set; }
}

/// <summary>
///     Response payload for injected sub-tick inputs over IPC.
/// </summary>
public sealed class SubTickResponseDto
{
    public bool Success { get; set; }
    public uint InputSequence { get; set; }
    public float MeasuredLatencyMs { get; set; }
    public long TimestampQpc { get; set; }
    public string? Message { get; set; }
}

/// <summary>
///     Payload for updating dynamic telemetry metrics over IPC.
/// </summary>
public sealed class TelemetryUpdatePayloadDto
{
    public uint DrawCalls { get; set; }
    public uint ActiveEntities { get; set; }
    public uint ParticlesAlive { get; set; }
    public uint PingMicroseconds { get; set; }
    public uint ServerTick { get; set; }
    public float InputLatencyMs { get; set; }
}

/// <summary>
///     Response payload for dynamic telemetry metric updates over IPC.
/// </summary>
public sealed class TelemetryMetricsResponseDto
{
    public bool Success { get; set; }
    public bool TelemetryUpdated { get; set; }
    public string? Message { get; set; }
}


// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.ControlPlane;

/// <summary>
///     The runtime and resource status of an individual dedicated game server worker.
/// </summary>
public sealed class CompelWorkerStatusDto
{
    [JsonPropertyName("instanceIndex")]
    public int InstanceIndex { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("voicePort")]
    public int VoicePort { get; set; }

    [JsonPropertyName("publicGamePort")]
    public int PublicGamePort { get; set; }

    [JsonPropertyName("processId")]
    public int? ProcessId { get; set; }

    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("workingSetBytes")]
    public long WorkingSetBytes { get; set; }

    [JsonPropertyName("workingSetMb")]
    public double WorkingSetMb { get; set; }

    [JsonPropertyName("uptimeSeconds")]
    public double UptimeSeconds { get; set; }

    [JsonPropertyName("crashCount")]
    public int CrashCount { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "Available";

    [JsonPropertyName("tickrateHz")]
    public double TickrateHz { get; set; }

    [JsonPropertyName("tickVarianceMs")]
    public double TickVarianceMs { get; set; }

    [JsonPropertyName("serverName")]
    public string? ServerName { get; set; }

    [JsonPropertyName("ipcConnected")]
    public bool? IpcConnected { get; set; }

    [JsonPropertyName("ipcPath")]
    public string? IpcPath { get; set; }

    [JsonPropertyName("ipcTransport")]
    public string? IpcTransport { get; set; }
}

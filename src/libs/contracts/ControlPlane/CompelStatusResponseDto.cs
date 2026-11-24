// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.ControlPlane;

/// <summary>
///     Comprehensive runtime and configuration status report emitted by the COMPEL server manager.
/// </summary>
public sealed class CompelStatusResponseDto
{
    [JsonPropertyName("application")] public string Application { get; set; } = string.Empty;

    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;

    [JsonPropertyName("serverNamePrefix")] public string ServerNamePrefix { get; set; } = string.Empty;

    [JsonPropertyName("location")] public string Location { get; set; } = string.Empty;

    [JsonPropertyName("serverAddress")] public string? ServerAddress { get; set; }

    [JsonPropertyName("instances")] public int Instances { get; set; }

    [JsonPropertyName("useProxy")] public bool UseProxy { get; set; }

    [JsonPropertyName("portRangeOffset")] public int PortRangeOffset { get; set; }

    [JsonPropertyName("ports")] public CompelPortAllocationDto? Ports { get; set; }

    [JsonPropertyName("distributionVersion")]
    public string? DistributionVersion { get; set; }

    [JsonPropertyName("synchronisationState")]
    public string SynchronisationState { get; set; } = string.Empty;

    [JsonPropertyName("managerRunning")] public bool ManagerRunning { get; set; }

    [JsonPropertyName("proxyRunning")] public bool ProxyRunning { get; set; }

    [JsonPropertyName("pingResponderBound")]
    public bool? PingResponderBound { get; set; }

    [JsonPropertyName("proxyFailedForwarderCount")]
    public int ProxyFailedForwarderCount { get; set; }

    [JsonPropertyName("uptimeSeconds")] public double UptimeSeconds { get; set; }

    [JsonPropertyName("activeWorkers")] public int ActiveWorkers { get; set; }

    [JsonPropertyName("workers")] public List<CompelWorkerStatusDto>? Workers { get; set; }
    [JsonPropertyName("operatingSystem")] public string? OperatingSystem { get; set; }
}

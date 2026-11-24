// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using NEXUS.Contracts.ControlPlane;

namespace COMPEL.Serialisation;

/// <summary>
///     Source-generated serialisation metadata for the control plane's request and response types.
///     Required because the application is published with Native AOT, which strips the reflection-based
///     <see cref="JsonSerializer" /> paths that minimal APIs would otherwise use.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PingResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(PortAllocationResponse))]
[JsonSerializable(typeof(ActionResponse))]
[JsonSerializable(typeof(DedicatedWorkerStatus))]
[JsonSerializable(typeof(DedicatedWorkerStatus[]))]
[JsonSerializable(typeof(List<DedicatedWorkerStatus>))]
[JsonSerializable(typeof(ExecuteCommandRequest))]
[JsonSerializable(typeof(ExecuteCommandResponse))]
[JsonSerializable(typeof(DedicatedServerLogLine))]
[JsonSerializable(typeof(DedicatedServerLogLine[]))]
[JsonSerializable(typeof(List<DedicatedServerLogLine>))]
[JsonSerializable(typeof(DedicatedServerLogsResponse))]
[JsonSerializable(typeof(CompelDedicatedServerLogLineDto))]
[JsonSerializable(typeof(CompelDedicatedServerLogsResponseDto))]
[JsonSerializable(typeof(CompelPingResponseDto))]
[JsonSerializable(typeof(CompelStatusResponseDto))]
[JsonSerializable(typeof(CompelPortAllocationDto))]
[JsonSerializable(typeof(CompelActionResponseDto))]
[JsonSerializable(typeof(LiveServerTickPayload))]
[JsonSerializable(typeof(Manifest))]
[JsonSerializable(typeof(COMPEL.Services.Telemetry.RlStatusSnapshot))]
internal sealed partial class ControlPlaneJSONContext : JsonSerializerContext;

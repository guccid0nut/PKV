// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;
using NEXUS.Contracts.Ipc;
using WILLOWMAKER.Core.Services.IPC;

namespace WILLOWMAKER.Core.Services.Serialization;

/// <summary>
///     Source-generated JsonSerializerContext providing compile-time, zero-reflection
///     Native AOT serialization for Willowmaker launcher settings, IPC envelopes, hot-reload, and telemetry.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LauncherSettingsModel))]
[JsonSerializable(typeof(HotReloadManifest))]
[JsonSerializable(typeof(LiveTelemetryData))]
[JsonSerializable(typeof(global::WILLOWMAKER.Core.Services.IPC.EngineStateDto))]
[JsonSerializable(typeof(global::WILLOWMAKER.Core.Services.DiscordUserProfile))]
[JsonSerializable(typeof(IpcAuthHeader))]
[JsonSerializable(typeof(IpcAuthPayload))]
[JsonSerializable(typeof(IpcAuthResponseDto))]
[JsonSerializable(typeof(CockpitEndpointSyncRequestDto))]
[JsonSerializable(typeof(CockpitEndpointSyncResponseDto))]
[JsonSerializable(typeof(CockpitLauncherStatusDto))]
[JsonSerializable(typeof(global::WILLOWMAKER.Core.Services.Diagnostics.DiagnosticManifestDto))]
[JsonSerializable(typeof(global::WILLOWMAKER.Core.Services.Diagnostics.RedactionAuditCounts))]
[JsonSerializable(typeof(global::WILLOWMAKER.Core.Services.Diagnostics.LaunchReceiptDto))]
[JsonSerializable(typeof(global::WILLOWMAKER.Core.Services.Diagnostics.LaunchReceiptConfigSummaryDto))]
[JsonSerializable(typeof(global::WILLOWMAKER.Core.Services.Diagnostics.LaunchReceiptModuleHashDto))]
[JsonSerializable(typeof(global::WILLOWMAKER.Core.Services.Diagnostics.LaunchReceiptPostBootAuditDto))]
[JsonSerializable(typeof(global::WILLOWMAKER.Core.Services.PlaytestPolicyDto))]
[JsonSerializable(typeof(global::WILLOWMAKER.Core.Services.BuildManifestDto))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string))]
public partial class WillowmakerJsonSerializerContext : JsonSerializerContext
{
}

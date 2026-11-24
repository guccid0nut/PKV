// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Ipc;

/// <summary>
///     Operational command and response codes for WILLOWMAKER IPC messages.
/// </summary>
public enum IpcOpCode : ushort
{
    Ping = 0x0001,
    Pong = 0x0002,
    ExecuteCommand = 0x0003,
    CommandResponse = 0x0004,
    GetEngineState = 0x0005,
    EngineStateResponse = 0x0006,
    LogStreamEvent = 0x0007,
    HotReloadMod = 0x0008,
    DispatchUiEvent = 0x0009,
    GetMatchState = 0x000A,
    TelemetryStream = 0x000B,
    EvalLuaSync = 0x000E,
    EvalLuaResponse = 0x000F,
    Authenticate = 0x0010,
    AuthResponse = 0x0011,
    InjectSubTickInput = 0x0012,
    SubTickInputResponse = 0x0013,
    UpdateTelemetryMetrics = 0x0014,
    TelemetryMetricsResponse = 0x0015,
    DispatchVoiceUiTrigger = 0x0021,
    DispatchVoiceUiTriggerResponse = 0x0022,
    GetVoicePttState = 0x0023,
    GetVoicePttStateResponse = 0x0024
}

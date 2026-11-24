// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// ============================================================================
// File: kongor_pipe_security.h
// System: NativeGuard Command Pipe Access Control & Playtest Hardening
// Purpose: Enforces build-mode command/method filtering on the IPC Named Pipe.
//          In dev builds, all debug, Lua, fixture, and memory commands are allowed.
//          In playtest/retail builds (KONGOR_PLAYTEST_BUILD=1), mutating methods
//          ("execute", "hook_toggle", "apply_server_fixture", "trigger_match_victory",
//           "PEEK_STRUCT", and raw console commands) are rejected with
//           -32601 / 403 PLAYTEST_LOCKDOWN while passive watchdog methods
//           ("auth", "status", "safe_mode", "get_server_world_state") are preserved.
// ============================================================================

#pragma once

#include <cstring>
#include <string_view>

#ifdef _WIN32
#include <windows.h>
#endif

namespace Kongor {

inline bool Kongor_IsPipeMethodAllowedInBuild(const char* methodOrRawCommand, bool isPlaytestBuild) {
    if (!methodOrRawCommand || methodOrRawCommand[0] == '\0') {
        return false;
    }

    // Dev builds allow all methods and console commands
    if (!isPlaytestBuild) {
        return true;
    }

    // Playtest builds ONLY allow passive/watchdog read-only methods:
    // "auth", "status", "safe_mode", "get_server_world_state"
#ifdef _WIN32
    if (_stricmp(methodOrRawCommand, "auth") == 0) return true;
    if (_strnicmp(methodOrRawCommand, "auth ", 5) == 0) return true;
    if (_strnicmp(methodOrRawCommand, "auth\t", 5) == 0) return true;
    if (_stricmp(methodOrRawCommand, "status") == 0) return true;
    if (_stricmp(methodOrRawCommand, "safe_mode") == 0) return true;
    if (_stricmp(methodOrRawCommand, "get_server_world_state") == 0) return true;
#else
    if (strcasecmp(methodOrRawCommand, "auth") == 0) return true;
    if (strncasecmp(methodOrRawCommand, "auth ", 5) == 0) return true;
    if (strncasecmp(methodOrRawCommand, "auth\t", 5) == 0) return true;
    if (strcasecmp(methodOrRawCommand, "status") == 0) return true;
    if (strcasecmp(methodOrRawCommand, "safe_mode") == 0) return true;
    if (strcasecmp(methodOrRawCommand, "get_server_world_state") == 0) return true;
#endif

    // All mutation / eval / memory / raw commands are blocked in playtest builds:
    // "execute", "hook_toggle", "apply_server_fixture", "trigger_match_victory",
    // "PEEK_STRUCT", "Script ...", raw console commands.
    return false;
}

inline bool Kongor_IsBinaryPipeOpCodeAllowedInBuild(unsigned short opCode, bool isPlaytestBuild) {
    // Dev builds allow all binary pipe opcodes
    if (!isPlaytestBuild) {
        return true;
    }

    // Playtest builds allow ONLY passive watchdog, telemetry, prewarm, and launcher voice/input-read opcodes.
    // Blocked in playtest builds: 0x0003 (ExecuteCommand), 0x0009 (DispatchUiEvent), 0x000A (DispatchBackgroundHumanClick),
    // 0x000E (EvalLuaSync), 0x0012 (InjectSubTickInput), 0x0027 (SetRawInputConfig), 0x0041 (SetJobPoolConfig),
    // 0x0051 (SetPredictionConfig), 0x0060 (HookToggleRequest), 0x0062 (PeekStructRequest),
    // 0x0070 (TriggerMatchVictory), 0x0072 (ApplyServerFixture), 0x0080 (DuplexLuaRpc).
    switch (opCode) {
        case 0x0001: // Ping
        case 0x0005: // GetEngineState
        case 0x0007: // GetLuaMemoryMetrics
        case 0x0010: // Authenticate
        case 0x0014: // UpdateTelemetryMetrics
        case 0x0021: // DispatchVoiceUiTrigger
        case 0x0023: // GetVoicePttState
        case 0x0025: // GetRawInputState
        case 0x0030: // PrewarmHero
        case 0x0031: // PrewarmCompetitivePool
        case 0x0032: // GetPrewarmStats
        case 0x0040: // GetJobPoolStats
        case 0x0050: // GetPredictionStats
        case 0x0071: // GetServerWorldState
            return true;
        default:
            return false;
    }
}

} // namespace Kongor

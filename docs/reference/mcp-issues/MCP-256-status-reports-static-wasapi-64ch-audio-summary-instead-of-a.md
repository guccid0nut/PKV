---
id: MCP-256
server: kongor
tool: kongor_game_client_manage
category: telemetry-mismatch
title: "Status reports static WASAPI 64ch audio summary instead of active kongor_audio mode"
status: resolved
codified_in: .agents/rules/mcp_issues.md
verified_by: "f3c7add47 + McpToolContractTests + live NetGuard stdio audit"
superseded_by: ""
supersedes: []
occurrences: 3
date: 2026-11-01
last_seen: 2026-11-17
tags: [kongor, kongor_game_client_manage, telemetry-mismatch]
prevention_rule: "Read games/client/logs/kongor_audio.log and console.log for authoritative FMOD output driver, speaker mode (Stereo vs 7.1), and channel count rather than relying on kongor_game_client_manage status audio.api/channels."
---

# MCP-256: Status reports static WASAPI 64ch audio summary instead of active kongor_audio mode

## Summary
Querying `kongor` / `kongor_game_client_manage` (`action: "status"`) while auditing FMOD / spatial audio configuration returned a static default summary (`audio.api: "WASAPI"`, `audio.channels: 64`) rather than the live FMOD output mode, speaker layout (Stereo vs. 7.1), and `sound_numChannels` negotiated at runtime in `kongor_audio.log` and `console.log`. Tracked as `open`; audio verification workflows must read `games/client/logs/kongor_audio.log` and live CVars (`sound_numChannels`, `sound_output`) for authoritative runtime telemetry.

On 2026-11-17, status returned `running: false` and `ipcConnected: false` alongside `audio.isReady: true`, `channels: 64`, and `sampleRate: 48000`. `EngineTelemetryDiagnostics.GetAudioStatus` initializes these values to defaults before attempting log inspection; this response does not establish that a live audio device is ready.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_client_manage` |
| **Expected** | Populate `audio` status fields from the active engine session's negotiated FMOD driver, speaker mode, sample rate, and `sound_numChannels` CVar / `kongor_audio.log` telemetry. |
| **Actual (Symptom)** | Returns `"audio": {"api": "WASAPI", "channels": 64}` even when the running client initialized with `sound_numChannels 128`, `NOSOUND` headless mode, or 7.1 surround speaker mode in `kongor_audio.log`. |
| **Root Cause** | `GameClientManageHandler` (`src/tools/kongor-cli/Mcp/Handlers/GameClientManageHandler.cs`) constructs the `audio` summary block from static baseline metadata when constructing the `status` envelope instead of parsing the latest session entry from `games/client/logs/kongor_audio.log` or querying live `sound_*` CVars over IPC. |
| **Fix / Remediation** | 1. Parse the active session header in `games/client/logs/kongor_audio.log` (and fallback to `sound_numChannels` / `sound_mixRate` CVars when `ipcConnected == true`) inside `GameClientManageHandler`.<br>2. Until patched, inspect `games/client/logs/kongor_audio.log` and `console.log` directly when verifying audio driver, speaker mode, or channel count changes. |

## Invocation Parameters & Raw Logs
```json
{
  "action": "status"
}
```
```json
{
  "status": "success",
  "running": true,
  "ipcConnected": true,
  "audio": {
    "api": "WASAPI",
    "channels": 64
  }
}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_client_manage
```

## Prevention Invariant
Read `games/client/logs/kongor_audio.log` and `console.log` (or query `sound_numChannels` via `kongor_client_eval_lua`) for authoritative FMOD output driver, speaker mode (Stereo vs 7.1), and channel count rather than relying on `kongor_game_client_manage` `status` `audio.api`/`audio.channels`.

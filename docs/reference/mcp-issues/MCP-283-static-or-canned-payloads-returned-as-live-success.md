---
id: MCP-283
server: kongor
tool: kongor_match_state
category: execution-fidelity
title: "Static or canned payloads returned as live success"
status: resolved
codified_in: "tests/unit/tools/Kongor.Mcp.Tests/ZeroGapClosureTests.cs"
verified_by: "c1ba23a3a + be14b0551 + d3f207806 + tests/unit/tools/Kongor.Mcp.Tests/ZeroGapClosureTests.cs + tests/python/test_tao_mcp_stdio_conformance.py + logs/last_gate_run.json"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_match_state, execution-fidelity]
prevention_rule: "A handler may only return data it read during the call; canned or fallback values must fail or be labelled SimulatedTestDouble."
---

# MCP-283: Static or canned payloads returned as live success

## Summary
During the 2026-11-17 live audit, several `kongor` tools returned fixed or canned data with `status: success` (some with `executionFidelity: LiveVerified`) regardless of the real engine state. Affected: `kongor_match_state`, `kongor_vfs_mount_graph`, `kongor_texture_diff`, `kongor_audio_preview`, `kongor_replay_diff` (file actions), `kongor_game_telemetry`, `kongor_telemetry_stream`, `kongor_game_snapshot` (`world_state`, `reconcile`) and `kongor_audit_stats`. Resolved in Horizon 2.6 (`c1ba23a3a`): `McpToolRegistry.ExecuteToolAsync` enforces a single parsed JSON gate rejecting any `executionFidelity: "SimulatedTestDouble"` payload with `isError: true` (`errorCode: "simulation_not_allowed"`) unless `allowSimulation: true` or `KONGOR_MCP_ALLOW_LEGACY_MOCKS=1`, and `VfsMountGraphHandler` returns `errorCode: "directory_not_found"` on missing directories instead of canned `mods/resources999.s2z` entries.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_match_state` |
| **Expected** | Values read from the engine, archive or file during the call, or an error when they cannot be read. |
| **Actual (Symptom)** | `kongor_match_state` (client) returned the same player `GUEST-11`, Pyromancer level 11, 920/1150 HP and `matchTimeSeconds: 432.8` at the login screen, the main menu and inside a practice match; for a dedicated server started 15 s earlier it returned `matchId: 12345`, 10 connected players and 124.5 s while the server shared memory reported 0 players and phase 0. `kongor_vfs_mount_graph` listed `mods/resources999.s2z` with hashes `sha256:abc12345` and `sha256:def67890`. `kongor_texture_diff` of a texture against itself reported 961 differing pixels (23.46 %). `kongor_audio_preview` described an `.ogg` inside an archive as `RIFF/WAVE (PCM)`, 0.42 s, for `inspect`, `info`, `preview` and `export_wav` alike, and exported nothing. `kongor_game_snapshot world_state` returned `LocalPlayer` level 1 after a 3 s RPC failure while `save` reported level 6. `kongor_game_telemetry` and `kongor_telemetry_stream` reported `pingMs: 12`, `drawCalls: 180`, `particlesAlive: 350`, `audioVoicesActive: 16` for both the client and a dedicated server. |
| **Root Cause** | Not traced in source during the audit. The values do not change with engine state, so they are not read from the engine. `kongor_replay_diff live_compare` already rejects such output as `SimulatedTestDouble`; the other paths do not. |
| **Fix / Remediation** | Fixed in `c1ba23a3a` (Horizon 2.6). Single authoritative JSON gate in `McpToolRegistry.ExecuteToolAsync` blocks `SimulatedTestDouble` payloads unless `allowSimulation: true` or `KONGOR_MCP_ALLOW_LEGACY_MOCKS=1`; `VfsMountGraphHandler` returns structured `directory_not_found` instead of mock `.s2z` data. Verified in `ZeroGapClosureTests` and `test_tao_mcp_stdio_conformance.py`. |

## Invocation Parameters & Raw Logs
```text
Evidence (local, git-ignored): artifacts/audits/tao-mcp-live-audit-2026-11-17/run2_results.jsonl
rows 38, 309, 317, 337 kongor_match_state (client)   -> GUEST-11, matchTimeSeconds 432.8, activeMatch false
row 463 kongor_match_state {target: server}           -> matchId 12345, connectedPlayers 10
row 459 kongor_server_perf_stream {snapshot}          -> connectedPlayers 0, gamePhase 0
rows 144-146 kongor_vfs_mount_graph                   -> mods/resources999.s2z, sha256:abc12345
row 158 kongor_texture_diff (baseline == candidate)   -> identical false, diffPixels 961
rows 164-166, 437 kongor_audio_preview                -> RIFF/WAVE (PCM) for an .ogg
rows 373, 377 kongor_game_snapshot {world_state}      -> stderr: RPC error: Unknown request type: rpc
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_match_state
```

## Prevention Invariant
A handler may only return data it read during the call; canned or fallback values must fail or be labelled SimulatedTestDouble.

---
id: MCP-290
server: kongor
tool: kongor_game_action
category: execution-fidelity
title: "Action tools report success without verifying engine effect"
status: resolved
verified_by: "f3c7add47 + McpToolContractTests.Tranche2 + live NetGuard stdio audit"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_game_action, execution-fidelity]
prevention_rule: "A mutating tool reports success only after reading back the changed engine state; a console ACK alone is status dispatched_unverified."
---

# MCP-290: Action tools report success without verifying engine effect

## Summary
In a live practice match, many mutating `kongor` tools returned `status: success` within milliseconds although the engine state did not change or the claimed state was not reached. Affected: `kongor_game_action`, `kongor_camera_control`, `kongor_replay_playback`, `kongor_scrimmage_launch`, `kongor_match_launch`, `kongor_network_chaos`, `kongor_network_impair_simulate`, `kongor_client_capture_preview`, `kongor_texture_in_game_inspect`, `kongor_game_command`, `kongor_game_lua`, `kongor_client_eval_lua reload_hero`, `kongor_ui_flow click_widget` and `kongor_mod_build`. Found in the 2026-11-17 live audit. Open; no fix applied. Related: `MCP-265`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_action` |
| **Expected** | Success means the effect was read back from the engine. A console acknowledgement alone is reported as dispatched and unverified. |
| **Actual (Symptom)** | `kongor_game_action give_gold 777`: gold 35800 before, 35802 after. `teleport` to map centre: hero still at the fountain in every later capture. `attack_target` with `entity` sent `Order attack`; `order` sent `Order stop`; `execute` without a command sent `echo OK`. `kongor_camera_control`: `get_position` returned the handler's own cached values and captures before and after `set_position`, `set_fov`, `set_pitch` showed the same view. `kongor_replay_playback start`: capture 6 s later still showed the practice match; `speed: 2` sent `ReplaySetPlaybackSpeed 1`. `kongor_scrimmage_launch launch` reported `gamePhase: 3` and team counts after 60 ms while the client showed a loading screen. `kongor_match_launch launch` reported success while the client stayed in the hosted-game lobby. Network impairment reported `LiveVerified` while `net_lag`, `net_drop` and `net_jitter` read back empty and `serverDispatchedLive: true` was returned with no server running. `kongor_client_capture_preview` accepted `Hero_NoSuchHero`. `kongor_client_eval_lua reload_hero` returned success with a Lua error in `response`. `kongor_mod_build` returned `valid: true` for a non-existent source directory and built no archive. |
| **Root Cause** | The handlers treat the pipe or console acknowledgement (`{"success":true,"command":...}`) as the outcome and do not read state back. Several commands are also built incorrectly (for example `GiveGold 777` without a player index, where `ClientPracticeHandler` uses `GiveGold 0 <amount>`). Per-tool root causes were not traced further. |
| **Fix / Remediation** | Not fixed. Until fixed, verify every action with `kongor_client_eval_lua` or `kongor_capture_window` before relying on it. |

## Invocation Parameters & Raw Logs
```text
Evidence (local, git-ignored): artifacts/audits/tao-mcp-live-audit-2026-11-17/run2_results.jsonl and s_match.png, s_final.png
rows 413-417 snapshot save / give_gold 777 / teleport / snapshot save -> gold 35800 -> 35802
rows 323-336 kongor_game_action (14 calls)                          -> all status success, 0-8 ms
rows 344-352 kongor_camera_control                                  -> identical view in captures 341, 352, 384, 405
rows 421-433 kongor_replay_playback                                 -> capture 423 still shows the practice match
row 475 kongor_scrimmage_launch {launch}                            -> gamePhase 3 after 60 ms; capture 477 = loading screen
row 485 kongor_match_launch {launch}                                -> success; capture 488 = hosted-game lobby
rows 389-398, 411 network impair / chaos                            -> cvars read back empty
row 244 kongor_client_capture_preview {hero: Hero_NoSuchHero}       -> success true
row 257 kongor_client_eval_lua {reload_hero}                        -> status success, response "[ERROR] ... attempt to index local '_h'"
rows 447, 448 kongor_mod_build                                      -> action validate, valid true
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_action
```

## Prevention Invariant
A mutating tool reports success only after reading back the changed engine state; a console ACK alone is status dispatched_unverified.

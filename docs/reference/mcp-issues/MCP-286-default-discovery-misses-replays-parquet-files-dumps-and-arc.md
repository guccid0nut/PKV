---
id: MCP-286
server: kongor
tool: kongor_replay_inspect
category: discovery
title: "Default discovery misses replays, parquet files, dumps and archived models"
status: resolved
verified_by: "3379aa36e + McpToolContractTests.Tranche3 + live NetGuard stdio audit (Hero_Pyromancer, Hero_Fayde, Hero_Accursed)"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_replay_inspect, discovery]
prevention_rule: "Discovery defaults must search the same locations the engine and sibling tools use, and report the directories searched when nothing is found."
---

# MCP-286: Default discovery misses replays, parquet files, dumps and archived models

## Summary
Several `kongor` discovery defaults found nothing although matching files exist in the repository: `kongor_replay_inspect list/find/summary`, `kongor_match_analytics list_files`, `kongor_crash_dump_analyze analyze`, `kongor_inspect_model_mdf` and `kongor_game_server_manage logs`. Found in the 2026-11-17 live audit. Open; no fix applied.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_replay_inspect` |
| **Expected** | Defaults find the files that the engine and sibling tools use, or the response lists the directories searched. |
| **Actual (Symptom)** | `kongor_replay_inspect list` returned 0 replays and `summary` by file name returned `Replay file not found`, while replays exist under `games/client/userdata/game/replays/`; `find` with `matchId` answered as `list`. `kongor_match_analytics list_files` returned 0 files with three parquet files in the repository root (found with `directory_path: "."`, 8990 ms). `kongor_crash_dump_analyze analyze` returned `no_artifact` while `list_recent` listed two files. `kongor_inspect_model_mdf` could not resolve an `.mdf` that `kongor_vfs_resolve` finds in `game/resources3.s2z`. `kongor_game_server_manage logs` for a server started seconds earlier returned lines from `Slave-1_2026_09_08.log`. |
| **Root Cause** | Not traced in source. The symptoms point to default search roots that differ from the engine layout and to loose-file-only lookup in `kongor_inspect_model_mdf`. |
| **Fix / Remediation** | Not fixed. Pass explicit repo-relative paths; resolve archive members with `kongor_vfs_resolve` first. |

## Invocation Parameters & Raw Logs
```text
Evidence (local, git-ignored): artifacts/audits/tao-mcp-live-audit-2026-11-17/run2_results.jsonl
rows 132, 173, 174 kongor_replay_inspect {list / summary / find}   -> 0 replays / not found / action list
rows 133, 181 kongor_match_analytics {list_files}                  -> 0 files; with directory_path "." found, 8990 ms
rows 134, 187 kongor_crash_dump_analyze {list_recent / analyze}    -> 2 files / no_artifact
rows 153, 154 kongor_inspect_model_mdf                             -> could not be resolved across any K2 VFS directory mounts
row 455 kongor_game_server_manage {logs, port 11235}               -> Slave-1_2026_09_08.log lines
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_replay_inspect
```

## Prevention Invariant
Discovery defaults must search the same locations the engine and sibling tools use, and report the directories searched when nothing is found.

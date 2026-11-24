---
id: MCP-297
server: kongor
tool: kongor_server_manage
category: lifecycle
title: "Lifecycle state is per-session and stop results are inaccurate"
status: resolved
codified_in: "tests/unit/tools/Kongor.Mcp.Tests/ZeroGapClosureTests.cs"
verified_by: "be14b0551 + tests/unit/tools/Kongor.Mcp.Tests/ZeroGapClosureTests.cs + tests/python/test_tao_mcp_stdio_conformance.py + logs/last_gate_run.json"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_server_manage, lifecycle]
prevention_rule: "Persist managed process and rule registries outside the MCP process and confirm every stop by re-probing the target before reporting success."
---

# MCP-297: Lifecycle state is per-session and stop results are inaccurate

## Summary
Lifecycle bookkeeping in the `kongor` MCP server previously lived only in process memory and stop actions were not confirmed. Resolved in Horizon 2.5 + Horizon 2.6 (`be14b0551`): `ClientProcessRegistry` persists `(Pid, ProcessStartTimeTicks)` across sessions and rejects recycled OS PIDs (`<= 2s` tolerance); `MatchLaunchHandler` refuses to hijack/reposition foreign `hon_x64` processes without `adoptExisting: true` / `repositionExisting: true`, confirms termination up to 5,000 ms (`errorCode: "process_termination_failed"` on survivors), and zero empty `catch` blocks remain around `.Kill(` or `File.Delete(`/`Directory.Delete(`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_server_manage` |
| **Expected** | A second MCP session sees the processes and rules the first one created. A stop reports success only after the target is confirmed down. Stop actions terminate only processes the server launched or the caller names. |
| **Actual (Symptom)** | `kongor_game_client_manage status` showed `totalManagedClients: 0` and `kongor_game_manage status` showed user `AmbientClient` for the client the previous session launched. `kongor_network_chaos heal` in a new session said `wasActive: false` for a rule injected one session earlier. `kongor_server_manage stop target: redis` returned success and `clusterState: Stopped` while the Garnet container was still `Up`. `kongor_match_launch stop` terminated the client by process name. `kongor_server_manage status` named the client IPC as `willowmaker-ipc` while `pipe_status` reported that pipe inactive. `kongor_compel_manage status` took 8.5 s to report a refused loopback connection. A running client (the first one of run 2) exited within seconds of `kongor_server_manage start target: cluster`; no dump or event-log entry was found and the cause was not determined. |
| **Root Cause** | Registries are in-memory per MCP process. `LifecycleService.cs` logs and ignores container stop errors (around lines 1264 and 1289) and then sets the state to `Stopped`. `MatchLaunchHandler.cs` (around lines 722-740) enumerates `hon_x64` by name. The client exit during cluster start was not traced. |
| **Fix / Remediation** | Fixed in `be14b0551` (Horizon 2.6). Persisted `(Pid, ProcessStartTimeTicks)` registry, foreign-client isolation (`foreign_client_running`), 5s confirmed stop re-probe (`process_termination_failed`), and permanent static ratchet test banning empty catches on process kills and file deletions. |

## Invocation Parameters & Raw Logs
```text
Evidence (local, git-ignored): artifacts/audits/tao-mcp-live-audit-2026-11-17/run2_results.jsonl
rows 5, 23 game_client_manage status / game_manage status -> totalManagedClients 0 vs 1, username AmbientClient
rows 397, 408 kongor_network_chaos {reset} then {heal} in a new session -> wasActive false
row 499 kongor_server_manage {stop, target: redis} -> status success; `docker ps` afterwards: garnet-cache ... Up 21 minutes
row 489 kongor_match_launch {stop} -> terminatedProcessIds [client pid]
rows 293, 294 kongor_compel_manage {status / servers} -> 8507 ms / 8189 ms
rows 275, 298 kongor_server_manage {start, cluster} -> next client status: running false
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_server_manage
```

## Prevention Invariant
Persist managed process and rule registries outside the MCP process and confirm every stop by re-probing the target before reporting success.

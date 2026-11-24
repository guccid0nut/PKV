---
id: MCP-285
server: kongor
tool: kongor_fixture_inject
category: ipc
title: "Native bridge rejects rpc request type used by fixture and match tools"
status: resolved
verified_by: "b5f8259 + McpToolContractTests.Tranche1B + live NetGuard stdio audit"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_fixture_inject, ipc]
prevention_rule: "Every request type a handler sends must be listed by kongor_tao_capabilities; check capability before dispatch and fail fast with the missing capability name."
---

# MCP-285: Native bridge rejects rpc request type used by fixture and match tools

## Summary
With a live, IPC-connected client, three `kongor` tools sent a mailbox request of type `rpc` that the native bridge rejected with `Unknown request type: rpc`. `kongor_fixture_inject` then failed and blamed an offline client; `kongor_game_snapshot world_state` waited 3 s and returned fallback data; `kongor_match_launch` waited 5 s and fell back to console commands. Found in the 2026-11-17 live audit. Open; no fix applied.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_fixture_inject` |
| **Expected** | The request is served, or the tool fails immediately naming the unsupported request type. |
| **Actual (Symptom)** | `kongor_fixture_inject` (4 of 4 calls): `Engine target 'client' is offline (IPC not connected, no dedicated server active)` after a 1000 ms timeout on RPC `console.execute`, while `kongor_game_client_manage status` reported `ipcConnected: true`. `kongor_game_snapshot world_state` and `reconcile`: about 3070 ms, then `status: success`. `kongor_match_launch launch`: `Nexus.Match.StartPractice` timed out after 5000 ms, then `status: success` via console fallback. |
| **Root Cause** | The running native bridge does not implement the `rpc` request type (`[NexusMailboxIpcBridge] RPC #... error: Unknown request type: rpc`). Whether the bridge build is older than the handlers or the type was never implemented was not determined. |
| **Fix / Remediation** | Not fixed. Use `kongor_game_lua apply_fixture` or `kongor_game_snapshot apply_fixture`, which dispatch through TAO and succeeded in the same session. |

## Invocation Parameters & Raw Logs
```text
Evidence (local, git-ignored): artifacts/audits/tao-mcp-live-audit-2026-11-17/run2_results.jsonl
rows 338, 340, 342, 343 kongor_fixture_inject -> isError, "Engine target 'client' is offline"
  stderr: [NexusMailboxIpcBridge] RPC #744142582 error: Unknown request type: rpc
  stderr: [FixtureInject] [WARN] Client IPC command dispatch failed: RPC method 'console.execute' (ReqId 1) timed out after 1000ms
rows 373, 377, 378, 467 kongor_game_snapshot {world_state / reconcile} -> ~3070 ms, same stderr, status success
row 485 kongor_match_launch {launch} -> Nexus.Match.StartPractice RPC timed out after 5000ms, falling back to console pipeline
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_fixture_inject
```

## Prevention Invariant
Every request type a handler sends must be listed by kongor_tao_capabilities; check capability before dispatch and fail fast with the missing capability name.

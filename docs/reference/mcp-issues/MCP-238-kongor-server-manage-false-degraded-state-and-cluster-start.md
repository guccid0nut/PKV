---
id: MCP-238
server: kongor
tool: kongor_server_manage
category: runtime-error
title: "KONGOR-SERVER-MANAGE-FALSE-DEGRADED-STATE-AND-CLUSTER-START-FALLBACK"
status: patched
verified_by: "McpToolContractTests.MCP238_LifecycleService_EvaluateClusterState_ReportsRunningWhenMasterAndChatAreHealthy"
superseded_by: ""
supersedes: []
occurrences: 15
date: 2026-10-18
last_seen: 2026-10-18
tags: [kongor, kongor_server_manage, runtime-error, cluster-state, master-server, chat-server]
prevention_rule: "Fix LifecycleService.cs:605-628 and 776-835 so clusterState reports Running when core game services (master-server:55555 + chat-server:11031) are healthy, and action:start target:cluster directly starts master-server and chat-server if ASPIRE.ApplicationHost is unavailable."
---

# MCP-238: KONGOR-SERVER-MANAGE-FALSE-DEGRADED-STATE-AND-CLUSTER-START-FALLBACK

## Summary
During the `2026-10-17`–`2026-10-18` self-healing journeys (Steps `28904`–`29010` and `34165`–`34171`, 15 occurrences), `kongor` / `kongor_server_manage` repeatedly caused multi-step startup confusion:
1. Calling `kongor_server_manage({"action": "start", "target": "cluster"})` killed existing services (`TerminateClusterProcessesFast()`) and tried to launch `ASPIRE.ApplicationHost`, which exited without starting `master-server` (`55555`) or `chat-server` (`11031`), leaving the cluster `Stopped`.
2. Even after starting `master-server` (`55555`, `state: "Running", responding: true`) and `chat-server` (`11031`, `state: "Running", responding: true`) individually, `kongor_server_manage({"action": "status"})` continued to report `clusterState: "Degraded"` because auxiliary web portal services (`web-portal-api` on `5200` and `web-portal-ui` on `5266`) were stopped.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_server_manage` |
| **Expected** | 1. `action: "start", target: "cluster"` should reliably start `master-server` (`55555`) and `chat-server` (`11031`) (plus `compel` when `withGameServers: true`), waiting until their ports respond.<br>2. `action: "status"` should report `clusterState: "Running"` whenever `master-server` (`55555`) and `chat-server` (`11031`) are both `Running` and `responding: true`. |
| **Actual (Symptom)** | 1. `action: "start", target: "cluster"` returns `clusterState: "Stopped"` with both `master-server` and `chat-server` stopped.<br>2. When `master-server` and `chat-server` are started directly, `clusterState` still reports `"Degraded"` with message `"Cluster is running with degraded services. Inspect offline services or restart via 'kongor_server_manage action:restart'."` |
| **Root Cause** | 1. **Contradictory `ClusterState` Check (`src/tools/kongor-cli/Mcp/Services/LifecycleService.cs:605-619`):** Line `606` computes `bool isCoreRunning = isAspireRunning || (isMasterRunning && isChatRunning);`, but line `611` immediately overrides it with `if (!isMasterRunning || !isChatRunning || !isApiRunning || !isUiRunning) _clusterState = "Degraded";`! Because `web-portal-api` (`5200`) and `web-portal-ui` (`5266`) are optional web services not needed by the game client, standalone `master-server` + `chat-server` is permanently flagged as `"Degraded"`.<br>2. **`StartClusterInternalAsync` Does Not Start Standalone `master-server` + `chat-server` (`LifecycleService.cs:776-835`):** `StartClusterInternalAsync` only spawns `ASPIRE.ApplicationHost` and returns immediately without polling readiness or falling back to `StartSingleServiceInternalAsync("master-server")` + `StartSingleServiceInternalAsync("chat-server")` when Aspire Host fails or is not used. |
| **Fix / Remediation** | 1. In [`LifecycleService.cs:605-619`](../../../src/tools/kongor-cli/Mcp/Services/LifecycleService.cs#L605-L619), set `_clusterState = "Running"` whenever `isMasterRunning && isChatRunning` (reporting optional web portal state in `AdditionalInfo` rather than degrading the game cluster).<br>2. In [`LifecycleService.cs:776-835`](../../../src/tools/kongor-cli/Mcp/Services/LifecycleService.cs#L776-L835), have `StartClusterInternalAsync` start `master-server` (`55555`) and `chat-server` (`11031`) directly (or fall back to starting them whenever `ASPIRE.ApplicationHost` does not bring ports `55555` and `11031` online within 3 seconds), and poll `ProbePortAsync` for up to 5 seconds so `action: "start", target: "cluster"` returns with both core services `Running` and `responding: true`. |

## Invocation Parameters & Raw Logs
```text
Step 34167: kongor_server_manage({"action":"start","target":"cluster"})
-> {"status":"success","action":"start","target":"cluster","clusterState":"Stopped","services":{"master-server":{"port":55555,"state":"Stopped","responding":false},"chat-server":{"port":11031,"state":"Stopped","responding":false}}}

Step 34171: kongor_server_manage({"action":"start","target":"chat-server"})
-> {
  "status": "success",
  "action": "start",
  "target": "chat-server",
  "clusterState": "Degraded",
  "message": "Cluster is running with degraded services. Inspect offline services or restart via 'kongor_server_manage action:restart'.",
  "services": {
    "master-server": { "port": 55555, "state": "Running", "processId": 62916, "responding": true },
    "chat-server": { "port": 11031, "state": "Running", "processId": 56908, "responding": true },
    "web-portal-api": { "port": 5200, "state": "Stopped", "processId": 0, "responding": false },
    "web-portal-ui": { "port": 5266, "state": "Stopped", "processId": 0, "responding": false }
  }
}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_server_manage
```

## Prevention Invariant
Fix `LifecycleService.cs:605-628` and `776-835` so `clusterState` reports `Running` when core game services (`master-server:55555` + `chat-server:11031`) are healthy, and `action:start target:cluster` directly starts `master-server` and `chat-server` if `ASPIRE.ApplicationHost` is unavailable.

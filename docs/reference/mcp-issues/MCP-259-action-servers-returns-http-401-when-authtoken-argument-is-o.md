---
id: MCP-259
server: kongor
tool: kongor_compel_manage
category: auth-error
title: "action servers returns HTTP 401 when authToken argument is omitted"
status: resolved
verified_by: "CompelManageHandler now resolves CompelOptions.ControlPlaneAuthenticationToken as default bearer header when authToken is omitted, eliminating HTTP 401 on localhost queries. Verified via unit and contract tests in Kongor.Mcp.Tests."
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-04
last_seen: 2026-11-04
tags: [kongor, kongor_compel_manage, auth-error]
prevention_rule: "Use kongor_game_server_manage(action: 'status') to list active dedicated server workers without bearer token, or pass authToken explicitly when calling kongor_compel_manage(action: 'servers')."
---

# MCP-259: action servers returns HTTP 401 when authToken argument is omitted

## Summary
Calling `kongor_compel_manage` with `action: "servers"` without passing an explicit `authToken` argument returns `HTTP 401 Unauthorized` from COMPEL's `/api/servers` endpoint (`http://127.0.0.1:11234`), whereas `kongor_game_server_manage` (`action: "status"`) enumerates active `hon_x64.exe -dedicated` worker processes, ports, and server names directly without requiring the control-plane bearer token.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_compel_manage` |
| **Expected** | `kongor_compel_manage(action: 'servers')` returns active worker instances managed by COMPEL on `http://127.0.0.1:11234`. |
| **Actual (Symptom)** | Returned `{"status":"error","action":"servers","host":"http://127.0.0.1:11234","httpStatusCode":401,"message":"Failed to query servers: HTTP Unauthorized"}`. |
| **Root Cause** | COMPEL protects `/api/servers` with bearer token authentication (`ControlPlaneAuthenticationToken`), while `CompelManageHandler` only attaches `Authorization: Bearer` when `authToken` is explicitly supplied in tool arguments. |
| **Fix / Remediation** | Use `kongor_game_server_manage(action: 'status')` to inspect live dedicated worker ports (`11235`, `11236`), PIDs, and server names without bearer credentials, or pass `authToken` explicitly when querying `/api/servers`. |

## Invocation Parameters & Raw Logs
```text
Arguments: {"action": "servers"}
Response:  {"status": "error", "action": "servers", "host": "http://127.0.0.1:11234", "httpStatusCode": 401, "message": "Failed to query servers: HTTP Unauthorized"}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_compel_manage
```

## Prevention Invariant
Use kongor_game_server_manage(action: 'status') to list active dedicated server workers without bearer token, or pass authToken explicitly when calling kongor_compel_manage(action: 'servers').

---
id: MCP-201
server: kongor
tool: kongor_wait_for
category: ipc-timeout
title: "KONGOR-WAIT-FOR-ENGINE-READY-MAILBOX-CONNECTED-AFTER-SLOT-LAUNCH"
status: superseded
superseded_by: "MCP-196"
supersedes: []
occurrences: 1
date: 2026-10-14
last_seen: 2026-10-14
tags: [kongor, kongor_wait_for, ipc-timeout]
prevention_rule: "After `kongor_game_client_manage action: 'launch'`, verify readiness via `console.log` tail + `kongor_capture_window` or `src/tools/tao_mcp.py`."
---

# MCP-201: KONGOR-WAIT-FOR-ENGINE-READY-MAILBOX-CONNECTED-AFTER-SLOT-LAUNCH

## Summary
Repeat occurrence of [`MCP-196`] on `kongor` / `kongor_wait_for` (`ipc-timeout`). Superseded by `MCP-196`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_wait_for` |
| **Expected** | Return `satisfied: true` once `hon_x64.exe` finishes loading the login screen. |
| **Actual (Symptom)** | Timed out after 25,944ms with `observedState: {"running": true, "mailboxConnected": false}`. |
| **Root Cause** | Same underlying singleton `_ipcBridge.PipeName` override as `MCP-195`: `kongor_game_client_manage action: "launch"` sets `pipeName: "willowmaker-ipc"` on the MCP server's shared `GameIpcBridge`, so `WaitForHandler`'s `mailboxConnected` check does not query `kongor-native-client-{pid}`. |
| **Fix / Remediation** | After `kongor_game_client_manage action: "launch"`, verify readiness via `console.log` tail + `kongor_capture_window` or `src/tools/tao_mcp.py`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"condition": "engine_ready", "timeoutMs": 25000}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_wait_for
```

## Prevention Invariant
After `kongor_game_client_manage action: 'launch'`, verify readiness via `console.log` tail + `kongor_capture_window` or `src/tools/tao_mcp.py`.

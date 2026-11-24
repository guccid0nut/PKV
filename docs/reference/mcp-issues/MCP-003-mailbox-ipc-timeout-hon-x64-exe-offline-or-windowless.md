---
id: MCP-003
server: kongor
tool: kongor_game_command
category: window-resolution
title: "Mailbox IPC Timeout (hon_x64.exe Offline or Windowless)"
status: resolved
verified_by: "McpToolContractTests.MCP003_And_MCP088_And_MCP210_OfflineClientServerAndHotReloadRemediation"
superseded_by: ""
supersedes: ["MCP-036", "MCP-072", "MCP-107"]
occurrences: 4
date: 2026-09-24
last_seen: 2026-09-26
tags: [kongor, kongor_game_command, window-resolution]
prevention_rule: "Verify client status via `kongor_game_client_manage action: 'status'` before IPC dispatch."
---

# MCP-003: Mailbox IPC Timeout (hon_x64.exe Offline or Windowless)

## Summary
Tracked MCP issue on `kongor` / `kongor_game_command` (category: `window-resolution`, status: `resolved`, occurrences: 4). Quick retrieval of client engine cvar or status. -> Command timed out after 3000ms: `Mailbox IPC request timed out after 3000ms awaiting response from running client.` Followed by: `IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft.`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_command` |
| **Expected** | Quick retrieval of client engine cvar or status. |
| **Actual (Symptom)** | Command timed out after 3000ms: `Mailbox IPC request timed out after 3000ms awaiting response from running client.` Followed by: `IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft.` |
| **Root Cause** | The player game client (`hon_x64.exe`) was not running, or was still initializing before the mailbox IPC channel was established. The server correctly suppressed the intrusive `Ctrl+F8` keystroke fallback to prevent focus theft. |
| **Fix / Remediation** | Automated remediation hint added: `The player game client (hon_x64.exe) is not running or has no active window handle. Launch it first with 'kongor_game_client_manage action:launch'`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "action": "command",
    "command": "fs_userpath"
  }
  

Logs:

  Mailbox IPC request #245604761 timed out after 3000ms awaiting response from running client.
  IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft.
  client_command_errors.ndjson: Id 10928d68, Id 42afddf2, ErrorCategory: ClientOffline
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_command
```

## Prevention Invariant
Verify client status via `kongor_game_client_manage action: 'status'` before IPC dispatch.

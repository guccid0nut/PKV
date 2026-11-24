---
id: MCP-209
server: kongor
tool: kongor_game_client_manage
category: client-offline
title: "KONGOR-GAME-CLIENT-MANAGE-COMMAND-CLIENT-OFFLINE"
status: patched
codified_in: "src/tools/kongor-cli/Mcp/Handlers/GameClientManageHandler.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/OfflineHonestyTests.cs; logs/mcp_tool_outcomes.jsonl (commit 596cae9dc)"
superseded_by: ""
supersedes: []
occurrences: 3
date: 2026-10-14
last_seen: 2026-11-09
tags: [kongor, kongor_game_client_manage, client-offline]
prevention_rule: "Check `kongor_game_client_manage` (`action: 'status'`) before sending `action: 'command'`, and launch `hon_x64.exe` when `running: false`."
---

# MCP-209: KONGOR-GAME-CLIENT-MANAGE-COMMAND-CLIENT-OFFLINE

## Summary
Tracked MCP issue on `kongor` / `kongor_game_client_manage` (category: `client-offline`, status: `documented`, occurrences: 3). Send `UICall` to the headless client launched in the previous turn. -> Returned `KeystrokeFallbackSuppressed` because no `hon_x64.exe` instance was currently running (`running: false`).

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_client_manage` |
| **Expected** | Send `UICall` to the headless client launched in the previous turn. |
| **Actual (Symptom)** | Returned `KeystrokeFallbackSuppressed` because no `hon_x64.exe` instance was currently running (`running: false`). |
| **Root Cause** | The headless client (`PID 32560`) had exited between turns. |
| **Fix / Remediation** | Check `kongor_game_client_manage` (`action: "status"`) before sending `action: "command"`, and launch `hon_x64.exe` when `running: false`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action": "command", "command": "UICall * ..."}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_client_manage
```

## Prevention Invariant
Check `kongor_game_client_manage` (`action: 'status'`) before sending `action: 'command'`, and launch `hon_x64.exe` when `running: false`.

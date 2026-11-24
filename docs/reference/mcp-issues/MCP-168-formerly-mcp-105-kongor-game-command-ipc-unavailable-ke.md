---
id: MCP-168
server: kongor
tool: kongor_game_command
category: ipc-timeout
title: "(formerly MCP-105): KONGOR-GAME-COMMAND-IPC-UNAVAILABLE-KEYSTROKE-SUPPRESSED"
status: patched
codified_in: "src/tools/kongor-cli/Mcp/Handlers/GameCommandHandler.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/OfflineHonestyTests.cs; logs/mcp_tool_outcomes.jsonl (commit 596cae9dc)"
superseded_by: ""
supersedes: []
occurrences: 3
date: 2026-10-10
last_seen: 2026-11-09
tags: [kongor, kongor_game_command, ipc-timeout]
prevention_rule: "Allow client 2-3 seconds to open Named Pipe listener, or dispatch through `kongor_client_navigate` / pre-warm `pipe_status` before sending commands."
---

# MCP-168: (formerly MCP-105): KONGOR-GAME-COMMAND-IPC-UNAVAILABLE-KEYSTROKE-SUPPRESSED

## Summary
Tracked MCP issue on `kongor` / `kongor_game_command` (category: `ipc-timeout`, status: `documented`, occurrences: 3). Sends `Login guccidonut password` through Named Pipe IPC to client. -> Returned error: `IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft.`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_command` |
| **Expected** | Sends `Login guccidonut password` through Named Pipe IPC to client. |
| **Actual (Symptom)** | Returned error: `IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft.` |
| **Root Cause** | The game client was launched via `kongor_game_client_manage` but the Named Pipe (`willowmaker-ipc`) client listener was still initializing or not yet connected to the MCP IPC bridge. |
| **Fix / Remediation** | Allow client 2-3 seconds to open Named Pipe listener, or dispatch through `kongor_client_navigate` / pre-warm `pipe_status` before sending commands. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"command": "Login guccidonut password"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_command
```

## Prevention Invariant
Allow client 2-3 seconds to open Named Pipe listener, or dispatch through `kongor_client_navigate` / pre-warm `pipe_status` before sending commands.

---
id: MCP-179
server: kongor
tool: kongor_game_command
category: runtime-error
title: "IPC-COMMAND-SUPPRESSED (MCP-2026-10-05)"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-05
last_seen: 2026-10-05
tags: [kongor, kongor_game_command, runtime-error]
prevention_rule: "Verify kongor / kongor_game_command readiness and parameters before invocation."
---

# MCP-179: IPC-COMMAND-SUPPRESSED (MCP-2026-10-05)

## Summary
Tracked MCP issue on `kongor` / `kongor_game_command` (category: `runtime-error`, status: `resolved`, occurrences: 1). Command dispatched via named pipe `\\.\pipe\willowmaker-ipc` or keystroke. -> Error: `IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft.`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_command` |
| **Expected** | Command dispatched via named pipe `\\.\pipe\willowmaker-ipc` or keystroke. |
| **Actual (Symptom)** | Error: `IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft.` |
| **Root Cause** | Named pipe was either not ready or mailbox IPC was looking in `userdataKONGOR/extensions/nexus_ipc` instead of the canonical `userdata/Heroes of Newerth x64/KONGOR/extensions/nexus_ipc`. |
| **Fix / Remediation** | - |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"command":"Set vid_pos \"100 100\""}`
* **Expected**: Command dispatched via named pipe `\\.\pipe\willowmaker-ipc` or keystroke.
* **Actual**: Error: `IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft.`
* **Root Cause**: Named pipe was either not ready or mailbox IPC was looking in `userdataKONGOR/extensions/nexus_ipc` instead of the canonical `userdata/Heroes of Newerth x64/KONGOR/extensions/nexus_ipc`.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_command
```

## Prevention Invariant
Verify kongor / kongor_game_command readiness and parameters before invocation.

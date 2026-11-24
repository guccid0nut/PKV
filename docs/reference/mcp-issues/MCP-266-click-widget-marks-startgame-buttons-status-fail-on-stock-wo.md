---
id: MCP-266
server: kongor
tool: kongor_ui_control
category: log-triage-false-positive
title: "click_widget marks StartGame buttons status:fail on stock world-load CFileManager logs"
status: open
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-10
last_seen: 2026-11-10
tags: [kongor, kongor_ui_control, log-triage-false-positive]
prevention_rule: "When clicking a widget that triggers synchronous StartGame world loading (e.g. botmatch_btn_start), verify gamePhase == 3 and map load in console.log rather than treating stock S2 CFileManager::GetFile .clip warnings as UI script errors."
---

# MCP-266: click_widget marks StartGame buttons status:fail on stock world-load CFileManager logs

## Summary
Invoking `kongor_ui_control` (`action: "click_widget"`, `widget: "botmatch_btn_start"`) on a K2 button that synchronously executes `StartGame practice LocalBotsGame ...` succeeds in launching the match, but `UiControlHandler`'s post-actuation `console.log` delta scan reports `status: "fail"` (`clean: false`) because stock S2 world archives (such as `soccer.s2z`) emit benign `CFileManager::GetFile() [/buildings/conq01/wall_01/clips/default_1.clip] - Invalid file type` lines during synchronous world loading.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | `click_widget` on `botmatch_btn_start` returns `status: "success"` after dispatching the click and starting the local bot match. |
| **Actual (Symptom)** | Returns `status: "fail"`, `clean: false`, `elapsedMs: 7043.29`, `errorCount: 53` citing `CFileManager::GetFile() [/buildings/conq01/wall_01/clips/default_1.clip] - Invalid file type`. |
| **Root Cause** | `StartGame` loads the map synchronously on the K2 main thread before the IPC click acknowledgement returns, so all stock map asset warnings/errors emitted during `LoadWorld` fall inside `click_widget`'s log-delta window. |
| **Fix / Remediation** | Documented workflow invariant: verify `gamePhase >= 3` via `kongor_game_client_manage(action: "status")` and inspect `console.log` for Lua/interface errors rather than treating stock S2 `CFileManager::GetFile` `.clip` messages as UI click failures. |

## Invocation Parameters & Raw Logs
```text
{"action":"click_widget","interface":"main","target":"hon_x64","widget":"botmatch_btn_start","highlight":false}
-> {"status":"fail","passed":false,"method":"ipc_dispatch_ui_event","elapsedMs":7043.29,"clean":false,"errorCount":53,"errors":[{"source":"console.log","severity":"ERROR","message":"Error: [00:00:22] CFileManager::GetFile() [/buildings/conq01/wall_01/clips/default_1.clip] - Invalid file type"}]}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
When clicking a widget that triggers synchronous StartGame world loading (e.g. botmatch_btn_start), verify gamePhase == 3 and map load in console.log rather than treating stock S2 CFileManager::GetFile .clip warnings as UI script errors.

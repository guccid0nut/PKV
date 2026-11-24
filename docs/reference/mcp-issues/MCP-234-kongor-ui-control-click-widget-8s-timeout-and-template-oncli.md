---
id: MCP-234
server: kongor
tool: kongor_ui_control
category: runtime-error
title: "KONGOR-UI-CONTROL-CLICK-WIDGET-8S-TIMEOUT-AND-TEMPLATE-ONCLICKLUA-DISPATCH"
status: patched
verified_by: "native/tests/test_eval_lua_shared_mem_and_ui_dispatch.cpp; tests/unit/tools/Kongor.Mcp.Tests/McpWaitForEvalLuaAndUiDispatchTests.cs; artifacts/telemetry/wave5_live_offscreen_eval_and_practice_probe.json; commit 91db60330"
superseded_by: ""
supersedes: []
occurrences: 5
date: 2026-10-18
last_seen: 2026-10-20
tags: [kongor, kongor_ui_control, runtime-error, tao, widget-click]
prevention_rule: "Fix UiControlHandler.cs:293-418 and TaoClient click_widget so K2 widgets and template buttons invoke their onclicklua/onselect/UICmd handlers directly via native pipe without timing out for 8.1s and returning method: none."
---

# MCP-234: KONGOR-UI-CONTROL-CLICK-WIDGET-8S-TIMEOUT-AND-TEMPLATE-ONCLICKLUA-DISPATCH

## Summary
During the `2026-10-18` self-healing journey (Step `34557`), calling `kongor` / `kongor_ui_control` with `action: "click", widget: "playerstats_tab_mastery"` stalled for **8,098.23 ms** and failed with `status: "fail", passed: false, method: "none"`, because `UiControlHandler` delegates widget actuation to `_taoClient.DispatchAsync("click_widget", ...)` (which times out when `TaoClient`'s file/eval mailbox is unresponsive) and suppresses Win32 coordinate fallback unless `hardware_click: true` is passed.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | Actuate the target K2 widget (`playerstats_tab_mastery`, store cards, options category tabs, mod checkboxes, lobby buttons) in `< 100ms` and return `status: "pass", passed: true`. |
| **Actual (Symptom)** | Stalls for `8,098.23ms` and returns `{"status":"fail","passed":false,"found":true,"visible":false,"enabled":false,"widget":"playerstats_tab_mastery","interfaceName":"main","method":"none","elapsedMs":8098.23}`. |
| **Root Cause** | 1. **`_taoClient.DispatchAsync("click_widget")` Stalls When `EvalLuaSync` / `tao_req` Mailbox Times Out (`src/tools/kongor-cli/Mcp/Handlers/UiControlHandler.cs:293-310`):** `HandleClickAsync` calls `_taoClient.DispatchAsync("click_widget", taoPayload, 2000, cancellationToken)` followed by `_errorsSinceHandler.ScanLogs(...)`. Because `TaoClient` relies on `NexusMailboxIpcBridge` (`tao_req`) or `EvalLuaSync` (`0x000E`), when `tao.lua` is not polling or `0x000E` times out (`MCP-231`), `taoResp` fails after multiple retries (`8.1s`) with `method = "none"`.<br>2. **K2 Template Buttons Store Click Logic in Controller Tables or Child Catchers (`games/client/override/ui/scripts/tao.lua`):** Many K2 widgets in `main.interface`, `store_v4.interface`, and `game_options.package` (e.g. `playerstats_tab_mastery`, `store_v4` search items, `HoN_Options` category tabs and checkboxes) either wrap their click handler on a `_catcher` / `_btn` child widget or invoke a controller method (`HoN_Options:SelectCategory`, `HoN_Store:OnClickSearchingAvatarItem`, `HoN_RegionSelect:SelectRegion`). |
| **Fix / Remediation** | 1. In [`UiControlHandler.cs:293-418`](../../../src/tools/kongor-cli/Mcp/Handlers/UiControlHandler.cs#L293-L418), if `_taoClient.DispatchAsync("click_widget")` fails or returns `method == "none"`, immediately dispatch a direct K2 widget actuation script over `_gameIpcBridge.ExecuteCommandAsync` (`0x0003`) that searches `UIManager.GetActiveInterface()` and `UIManager.GetInterface('main')` for `widget`, `widget .. '_btn'`, `widget .. '_catcher'`, and `widget .. '_button'`, and triggers both `w:DoEvent()` / `w:UICmd("DoEvent()")` and any known controller shortcut.<br>2. Once `MCP-231` (`0x000E` sync eval) is fixed, route `TaoClient` over `0x000E` so `click_widget` completes in `< 15ms` instead of `8,098ms`. |

## Invocation Parameters & Raw Logs
```text
Step 34557: kongor_ui_control({"action":"click","widget":"playerstats_tab_mastery"})
-> {
  "status": "fail",
  "passed": false,
  "found": true,
  "visible": false,
  "enabled": false,
  "widget": "playerstats_tab_mastery",
  "interfaceName": "main",
  "method": "none",
  "waitReport": null,
  "errorsSince": { "clean": true, "errorCount": 0, "minidumpCount": 0, "errors": [] },
  "elapsedMs": 8098.23
}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
Fix `UiControlHandler.cs:293-418` and `TaoClient` `click_widget` so K2 widgets and template buttons invoke their `onclicklua`/`onselect`/`UICmd` handlers directly via native pipe without timing out for 8.1s and returning `method: none`.

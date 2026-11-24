---
id: MCP-274
server: kongor
tool: kongor_k2_ui_inspect
category: preflight-false-positive
title: "kongor_k2_ui_inspect static/auto mode and kongor_client_navigate autoBoot blocked by offline client preflight"
status: resolved
verified_by: "McpToolContractTests.MCP_274_K2UiInspectStaticAutoAndClientNavigateAutoBoot_BypassOfflineClientPreflight"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_k2_ui_inspect, kongor_client_navigate, preflight-false-positive]
prevention_rule: "Only enforce hon_x64.exe online preflight on kongor_k2_ui_inspect when mode=='live', and on kongor_client_navigate when autoBoot==false."
---

# MCP-274: kongor_k2_ui_inspect static/auto mode and kongor_client_navigate autoBoot blocked by offline client preflight

## Summary
During live one-by-one MCP verification on Horizon 2, invoking `kongor_k2_ui_inspect` with `{"mode":"static","path":"games/client/override/ui/fe2/store2.package","depth":3}` while `hon_x64.exe` was offline returned `preflightFailed: true` (`Preflight check failed for 'kongor_k2_ui_inspect': K2 game client (hon_x64.exe) and native IPC pipe are offline`) instead of executing offline static XML introspection. Root-cause inspection of `McpToolRegistry.Preflight.cs` also revealed the same defect on `kongor_client_navigate`, which defaults to `autoBoot: true` yet was unconditionally blocked by the offline client preflight gate before `ClientNavigateHandler` could auto-boot the client. Fixed in `McpToolRegistry.Preflight.cs` and locked by `McpToolContractTests`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_k2_ui_inspect` (and `kongor_client_navigate`) |
| **Expected** | `kongor_k2_ui_inspect(mode='static' \| 'auto' \| 'layout')` parses `.interface`/`.package` XML offline when `hon_x64.exe` is not running, and `kongor_client_navigate(autoBoot=true)` reaches `ClientNavigateHandler` to auto-boot `hon_x64.exe`. |
| **Actual (Symptom)** | `{"status":"error","preflightFailed":true,"tool":"kongor_k2_ui_inspect","error":"Preflight check failed for 'kongor_k2_ui_inspect': K2 game client (hon_x64.exe) and native IPC pipe (\\\\.\\pipe\\kongor-native-client) are offline."}` |
| **Root Cause** | `McpToolRegistry.Preflight.cs` listed `kongor_k2_ui_inspect` and `kongor_client_navigate` unconditionally in `isLiveClientTool` without inspecting `mode` (`live` vs `static`/`auto`/`layout`) or `autoBoot` (`false` vs default `true`). |
| **Fix / Remediation** | Updated `McpToolRegistry.Preflight.cs` (`isK2UiInspectLive` & `isClientNavigateWithoutAutoBoot`) so `kongor_k2_ui_inspect` only enforces the live-client preflight when `mode == "live"`, and `kongor_client_navigate` only enforces it when `autoBoot == false`. |

## Invocation Parameters & Raw Logs
```json
{
  "input": {
    "depth": 3,
    "mode": "static",
    "path": "games/client/override/ui/fe2/store2.package"
  },
  "output": {
    "status": "error",
    "preflightFailed": true,
    "tool": "kongor_k2_ui_inspect",
    "error": "Preflight check failed for 'kongor_k2_ui_inspect': K2 game client (hon_x64.exe) and native IPC pipe (\\\\.\\pipe\\kongor-native-client) are offline."
  }
}
```

## Verification / Pre-Flight Oracle
Exact command that verifies this fix:
```powershell
powershell.exe -NoProfile -File scripts/Invoke-KongorTestLock.ps1 -Owner mcp274 -- dotnet test tests/unit/tools/Kongor.Mcp.Tests/ --filter MCP_274
```

## Prevention Invariant
Only enforce hon_x64.exe online preflight on kongor_k2_ui_inspect when mode=='live', and on kongor_client_navigate when autoBoot==false.

---
id: MCP-230
server: kongor
tool: kongor_client_navigate
category: ipc-timeout
title: "KONGOR-CLIENT-NAVIGATE-PIPE-ACK-VERIFY-AND-MISSING-SUBROUTES"
status: patched
codified_in: "src/tools/kongor-cli/Mcp/Handlers/ClientNavigateHandler.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/OfflineHonestyTests.cs; logs/mcp_tool_outcomes.jsonl (commit 596cae9dc)"
superseded_by: ""
supersedes: []
occurrences: 5
date: 2026-10-18
last_seen: 2026-11-02
tags: [kongor, kongor_client_navigate, ipc-timeout, navigation, store, options]
prevention_rule: "Fix ClientNavigateHandler.cs:377-406 to verify target widget visibility via console.log/FlushLogs or sync Lua eval instead of checking the 0x0003 pipe ACK string, and add Store avatar detail, Hosted Games, Match Stats, and HoN_Options.sliding=false sub-routes in K2RouteRegistry.cs."
---

# MCP-230: KONGOR-CLIENT-NAVIGATE-PIPE-ACK-VERIFY-AND-MISSING-SUBROUTES

## Summary
During the `2026-10-17`–`2026-10-18` self-healing journeys, every call to `kongor` / `kongor_client_navigate` (`destination: "main_menu"`, `destination: "store", category: "altavatars"`) hung for **35.7 to 40.3 seconds** and returned `status: "warning", verified: false` even though the K2 UI actually navigated on the first frame. Additionally, `K2RouteRegistry.cs` lacks sub-route parameters for Store hero/avatar detail pages, Hosted Games (`hosted_game` / `hosted_game_create`), Match Stats (`match_stats` with match ID), and Options -> Mods (`HoN_Options.sliding = false` reset before `SelectSubCategory`), forcing agents to fall back to manual `kongor_game_command` Lua snippets over 40 times.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_navigate` |
| **Expected** | Navigate to the requested K2 destination (`main_menu`, `store`, `options`, `hosted_games`, `match_stats`, `compendium`, `region_select`), verify the target widget is visible within `< 500ms`, and return `status: "success", verified: true`. |
| **Actual (Symptom)** | Spins for `35,695ms`–`40,342ms` and returns `status: "warning", verified: false, message: "Navigation commands dispatched to 'store', but widget 'store_container_v4' visibility could not be confirmed within 6000ms."` |
| **Root Cause** | 1. **Broken Pipe ACK Verification (`src/tools/kongor-cli/Mcp/Handlers/ClientNavigateHandler.cs:377-406, 488-515`):** `ExecuteScriptAsync` sends `Script ... Echo('VERIFY_WIDGET|' .. tostring(w:IsVisible()))` via `_gameIpcBridge.ExecuteCommandAsync(formatted, 2500)` (`opCode = 0x0003` on `\\.\pipe\kongor-native-client`). Native `0x0003` queues the command to `CConsole::AddCmdBuffer` and immediately returns `"[OK] Command queued to K2 main thread ring"`—it **never** includes console `Echo(...)` output in the IPC return string. Consequently, `checkResp.Contains("VERIFY_WIDGET|true")` on line `390` is unconditionally `false`, causing the loop to spin until `timeoutMs` expires, followed by a 30s timeout inside `_windowCapturer.CaptureTransientAsync` / `ErrorsSinceHandler`.<br>2. **Missing Sub-Routes (`src/tools/kongor-cli/Mcp/Services/K2RouteRegistry.cs:46-176`):**<br>- `store` route only opens `main_store` and sets `_store_category`; it has no `avatar` / `hero` / `search` parameter to open a specific avatar detail preview (`HoN_Store.HeroDetailPage.AvatarCodeToInit = '<Hero.Alt>'; HoN_Store:NavToHeroAvatarDetailPage(true, true)` or `HoN_Store:OnSearchingAvatar(1, '<query>'); HoN_Store:OnClickSearchingAvatarItem(1); HoN_Store:ShowHeroAvatarPanels(1, <slot>)`).<br>- `options` route (`K2RouteRegistry.cs:111-118`) calls `HoN_Options:Open()` and `HoN_Options:SelectCategory(cat)`, but `HoN_Options:Open()` sets `HoN_Options.sliding = true` for its slide-in animation, which **blocks** immediate `HoN_Options:SelectCategory(6)` / `HoN_Options:SelectSubCategory(6, sub)` calls unless `HoN_Options.sliding = false` is cleared first.<br>- Missing routes for `hosted_games` / `create_game` (`main_SimpleTrigger('hosted_game')` / `Player_Hosted_V2:SwitchCreatePanel(true)`), `match_stats` (setting `_playerstats_match_owner`, `_stats_last_match_id`, `main_SimpleTrigger('match_stats')`, `GetMatchInfo(matchId)`), and `compendium` (`HoN_Main_V2:ToggleCompendium()`). |
| **Fix / Remediation** | 1. In [`ClientNavigateHandler.cs:377-406`](../../../src/tools/kongor-cli/Mcp/Handlers/ClientNavigateHandler.cs#L377-L406), replace `checkResp.Contains("VERIFY_WIDGET|true")` with either synchronous Lua evaluation (`0x000E` once `MCP-231` is fixed) or `Echo('VERIFY_WIDGET|<token>|' .. tostring(w:IsVisible())); Cmd('FlushLogs')` followed by reading the tail of `games/client/userdata/Heroes of Newerth x64/override/console.log` (UTF-16LE).<br>2. In [`K2RouteRegistry.cs:46-176`](../../../src/tools/kongor-cli/Mcp/Services/K2RouteRegistry.cs#L46-L176) and `ClientNavigateHandler.cs`, add support for:<br>- `destination: "store"` optional `avatar` (e.g. `"Hero_Devourer.Alt9"`), `search` (e.g. `"Bramble"`), and `slot` (`1..12`) parameters.<br>- `destination: "options"` automatic `HoN_Options.sliding = false;` before `SelectCategory` / `SelectSubCategory`, plus named category aliases (`"mods"` -> `category: 6`, `"combat"` -> `subCategory: 0`, `"tactical"` -> `subCategory: 1`, `"visuals"` -> `subCategory: 2`, `"audio"` -> `subCategory: 3`).<br>- New destinations: `"hosted_games"`, `"create_game"`, `"match_stats"` (with optional `matchId` and `account`), and `"compendium"`. |

## Invocation Parameters & Raw Logs
```text
Step 29022: kongor_client_navigate({"destination":"main_menu"})
-> {"status":"warning","destination":"main_menu","verified":false,"elapsedMs":39923.81,"message":"Navigation commands dispatched to 'main_menu', but widget 'main_V2_LoggedIn' visibility could not be confirmed within 5000ms."}

Step 34175: kongor_client_navigate({"destination":"main_menu"})
-> {"status":"warning","destination":"main_menu","verified":false,"elapsedMs":35695.01,"message":"Navigation commands dispatched to 'main_menu', but widget 'main_V2_LoggedIn' visibility could not be confirmed within 5000ms."}

Step 34215: kongor_client_navigate({"category":"altavatars","destination":"store"})
-> {"status":"warning","destination":"store","verified":false,"elapsedMs":40342.49,"message":"Navigation commands dispatched to 'store', but widget 'store_container_v4' visibility could not be confirmed within 6000ms."}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_navigate
```

## Prevention Invariant
Fix `ClientNavigateHandler.cs:377-406` to verify target widget visibility via `console.log`/`FlushLogs` or sync Lua eval instead of checking the `0x0003` pipe ACK string, and add Store avatar detail, Hosted Games, Match Stats, and `HoN_Options.sliding=false` sub-routes in `K2RouteRegistry.cs`.

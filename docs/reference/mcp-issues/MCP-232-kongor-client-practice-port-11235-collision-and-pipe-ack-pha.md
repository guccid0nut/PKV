---
id: MCP-232
server: kongor
tool: kongor_client_practice
category: ipc-timeout
title: "KONGOR-CLIENT-PRACTICE-PORT-11235-COLLISION-AND-PIPE-ACK-PHASE-CHECK"
status: patched
codified_in: "src/tools/kongor-cli/Mcp/Handlers/ClientPracticeHandler.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/OfflineHonestyTests.cs; logs/mcp_tool_outcomes.jsonl (commit 596cae9dc)"
superseded_by: ""
supersedes: []
occurrences: 11
date: 2026-10-18
last_seen: 2026-11-09
tags: [kongor, kongor_client_practice, ipc-timeout, practice-mode, port-collision]
prevention_rule: "Fix ClientPracticeHandler.cs:429-595 to bind a non-conflicting local server port (svr_port 11399 + Connect localhost:11399) when COMPEL occupies 11235, flush and read UTF-16LE console.log for GAME_PHASE/HERO_ASSERT instead of checking the 0x0003 pipe ACK string, and execute StartMatch + GiveGold/PurchaseByName."
---

# MCP-232: KONGOR-CLIENT-PRACTICE-PORT-11235-COLLISION-AND-PIPE-ACK-PHASE-CHECK

## Summary
During the `2026-10-18` self-healing journey (Steps `35035`, `35059`, `35067`), all 3 invocations of `kongor` / `kongor_client_practice` (`action: "launch"`, `action: "spawn_hero"`, `action: "full_setup"`, `hero: "Hero_Devourer"`) hung for **22.6 to 34.4 seconds** and failed with `status: "warning", gamePhase: 0, phaseReached: false, entityVerified: false`, leaving the client stranded at the main menu or pre-match setup without spawning the hero or granting items.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_practice` |
| **Expected** | Launch a local Practice Mode match on `caldavar`, transition to active in-game state (`GamePhase >= 3`), spawn `Hero_Devourer`, level up, grant requested items (`Item_Immunity`, `Item_FlamingEye`), and return `status: "success", phaseReached: true, entityVerified: true`. |
| **Actual (Symptom)** | Spins for `22,571ms`–`34,376ms` and returns `status: "warning", gamePhase: 0, phaseReached: false, entityVerified: false, message: "Practice setup commands dispatched for 'Hero_Devourer' on 'caldavar', but entity spawn was not confirmed within timeout (GamePhase=0)."` |
| **Root Cause** | 1. **UDP Port `11235` Collision with Local `COMPEL` Dedicated Server (`src/tools/kongor-cli/Mcp/Handlers/ClientPracticeHandler.cs:450`):** `ClientPracticeHandler` dispatches `Cmd('StartGame practice PracticeGame map:caldavar allheroes:true')` using default `svr_port 11235`. When `COMPEL.exe` dedicated server workers are running locally (bound to UDP `11235`–`11238`), the client's internal `GameHost` fails to bind `11235` or fails handshake unless `Set('svr_port', '11399')` (an unused local port) is set before `StartGame` and followed by `Connect localhost:11399` once the local server initializes.<br>2. **Broken `DetermineGamePhaseAsync` & `HERO_ASSERT` Verification Over `0x0003` (`ClientPracticeHandler.cs:543-585`):** Just like `ClientNavigateHandler` (`MCP-230`), `DetermineGamePhaseAsync` and the `entityQueryLua` loop inspect the return string of `ExecuteScriptAsync` (`"[OK] Command queued to K2 main thread ring"`) for `"GAME_PHASE|"` and `"HERO_ASSERT|true"`, which is **never** present in the `0x0003` pipe ACK! Meanwhile, `TryScrapeLatestHeroAssertFromLog()` reads ASCII/UTF-8 paths without calling `Cmd('FlushLogs')` first and misses the UTF-16LE `games/client/userdata/Heroes of Newerth x64/override/console.log`.<br>3. **Missing `StartMatch` & `PurchaseByName` (`ClientPracticeHandler.cs:486-528`):** After `SpawnHero <hero>; Ready`, Practice Mode remains in pre-game setup unless `StartMatch` is executed, and `GiveItem` requires a server-side entity index whereas `GiveGold 0 99999; PurchaseByName <item>` reliably equips items on the local player's hero. |
| **Fix / Remediation** | 1. In [`ClientPracticeHandler.cs:445-475`](../../../src/tools/kongor-cli/Mcp/Handlers/ClientPracticeHandler.cs#L445-L475), probe UDP/TCP ports or automatically set `Set('svr_port', '11399'); Cmd('StartGame practice PracticeGame map:caldavar allheroes:true');` and, after local map load, ensure `Connect localhost:11399` if not yet connected.<br>2. In [`ClientPracticeHandler.cs:486-535`](../../../src/tools/kongor-cli/Mcp/Handlers/ClientPracticeHandler.cs#L486-L535), dispatch `Cmd('SpawnHero ' .. hero); Cmd('Ready'); Cmd('StartMatch');` and for items dispatch `Cmd('GiveGold 0 99999'); Cmd('PurchaseByName ' .. item)` in addition to `GiveItem`.<br>3. In [`ClientPracticeHandler.cs:543-620`](../../../src/tools/kongor-cli/Mcp/Handlers/ClientPracticeHandler.cs#L543-L620), call `Cmd('FlushLogs')` and read `games/client/userdata/Heroes of Newerth x64/override/console.log` (with `Encoding.Unicode` / UTF-16LE BOM detection) to parse `GAME_PHASE|` and `HERO_ASSERT|true` (or use fixed synchronous `0x000E` Lua eval). |

## Invocation Parameters & Raw Logs
```text
Step 35035: kongor_client_practice({"action":"launch","hero":"Hero_Devourer","level":15,"map":"caldavar","timeoutMs":15000})
-> {"status":"warning","action":"launch","map":"caldavar","hero":"Hero_Devourer","level":15,"gamePhase":0,"phaseReached":false,"entityVerified":false,"elapsedMs":33022.4}

Step 35059: kongor_client_practice({"action":"spawn_hero","hero":"Hero_Devourer","items":["Item_Immunity","Item_FlamingEye"],"level":15,"timeoutMs":10000})
-> {"status":"warning","action":"spawn_hero","map":"caldavar","hero":"Hero_Devourer","level":15,"gamePhase":0,"phaseReached":false,"entityVerified":false,"elapsedMs":22571.67}

Step 35067: kongor_client_practice({"action":"full_setup","hero":"Hero_Devourer","items":["Item_Immunity","Item_FlamingEye"],"level":15,"map":"caldavar","timeoutMs":25000})
-> {"status":"warning","action":"full_setup","map":"caldavar","hero":"Hero_Devourer","level":15,"gamePhase":0,"phaseReached":false,"entityVerified":false,"elapsedMs":34376.47}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_practice
```

## Prevention Invariant
Fix `ClientPracticeHandler.cs:429-595` to bind a non-conflicting local server port (`svr_port 11399` + `Connect localhost:11399`) when `COMPEL` occupies `11235`, flush and read UTF-16LE `console.log` for `GAME_PHASE`/`HERO_ASSERT` instead of checking the `0x0003` pipe ACK string, and execute `StartMatch` + `GiveGold`/`PurchaseByName`.

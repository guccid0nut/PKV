---
id: MCP-291
server: kongor
tool: kongor_client_navigate
category: verification
title: "Visual proof shows previous screen while verified is true"
status: resolved
verified_by: "f3c7add47 + ClientNavigateHandler swapchain settle barrier + live NetGuard stdio audit"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_client_navigate, verification]
prevention_rule: "Capture proof only after the target widget is visible and a new frame has been published; reject frames older than the dispatch time."
---

# MCP-291: Visual proof shows previous screen while verified is true

## Summary
The `visualProof` image attached by `kongor_client_navigate` and `kongor_ui_flow` showed the screen from before the navigation in most calls, while the response said `verified: true`. A separate `kongor_capture_window` call 1.5 s later showed the correct screen every time. Found in the 2026-11-17 live audit against an offscreen client. Open; no fix applied.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_navigate` |
| **Expected** | The proof image shows the destination screen, or the proof is marked stale and `verified` is false. |
| **Actual (Symptom)** | `kongor_client_navigate`: 10 of 13 proofs showed the previous screen (for example the `store` proof showed the login screen and the `match_stats` proof showed the create-game screen). `kongor_ui_flow`: 5 of 9 proofs were stale. `kongor_client_practice launch` reported `gamePhase: 3`, `entityVerified: true` with `heroHealth: 0`, and a capture 4 s later still showed the main menu. All proofs reported `captureMethod: vulkan_offscreen`. Also seen: `learnatorium` and `compendium` render the same screen, `create_game` echoes `destination: lobby`, and `hero_preview` for `Hero_Pyromancer` appeared to show a different hero. |
| **Root Cause** | Not confirmed. `ClientNavigateHandler.cs` (around lines 492-496) captures immediately after the widget check. Either the capture runs before the engine presents the new screen, or the offscreen shared-memory frame returned is the previously published one. Widget visibility and presented pixels are verified independently. |
| **Fix / Remediation** | Not fixed. Until fixed, pass `captureProof: false` and call `kongor_capture_window` at least one second after the navigation returns. |

## Invocation Parameters & Raw Logs
```text
Evidence (local, git-ignored): artifacts/audits/tao-mcp-live-audit-2026-11-17/ (s_nav1.png, s_nav2.png, s_ui1.png, s_match.png, run2_results.jsonl)
rows 48-73 alternate kongor_client_navigate (proof) and kongor_capture_window (control) for 13 destinations
rows 77-91 kongor_ui_flow open_* with captureProof true, each followed by a control capture
row 313 kongor_client_practice {launch} -> phase 3, entityVerified true, heroHealth 0; row 319 capture = main menu
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_navigate
```

## Prevention Invariant
Capture proof only after the target widget is visible and a new frame has been published; reject frames older than the dispatch time.

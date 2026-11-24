---
id: MCP-295
server: kongor
tool: kongor_game_lua
category: response-size
title: "Inspect and reload responses exceed 25 KB by default"
status: resolved
codified_in: "tests/unit/tools/Kongor.Mcp.Tests/ZeroGapClosureTests.cs"
verified_by: "d3f207806 + tests/unit/tools/Kongor.Mcp.Tests/ZeroGapClosureTests.cs + tests/python/test_tao_mcp_stdio_conformance.py + logs/last_gate_run.json"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_game_lua, response-size]
prevention_rule: "Default responses stay under 8 KB; honour depth, filter and limit parameters and return large payloads as an artifact path."
---

# MCP-295: Inspect and reload responses exceed 25 KB by default

## Summary
Several `kongor` tools returned 22-68 KB of text for narrow questions, consuming agent context and exceeding frame limits (`MCP-273`). Resolved in Horizon 2.5 + Horizon 2.6 (`d3f207806`): `ApplyDefaultResponseCapIfNeeded` enforces a hard 8,192-byte UTF-8 default cap across both normal and `isError` payloads (without `IsTestExecution` bypasses), preserves nested JSON keys via bounded recursive compaction, replaces `*Base64*` blobs with explicit omission notices, and spills full payloads to unique `{tool}_{ts}_{guid8}.json` artifacts under `KONGOR_MCP_PAYLOAD_DIR` / `artifacts/mcp_payloads` unless `detail: "full"` or `includeRaw: true` is passed.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_lua` |
| **Expected** | Default responses stay small (a few KB); large trees and dumps are written to an artifact and referenced by path. |
| **Actual (Symptom)** | `kongor_game_lua inspect_ui` 68 KB (and `tao_dispatch` with an unrecognised handler option returned the same 68 KB tree). `kongor_k2_ui_inspect` live 49-51 KB for one named widget or `depth: 1`. `kongor_k2_lint` 41 KB. `kongor_vfs_resolve find` 29 KB. `kongor_reload_and_verify` 27.5 KB, `kongor_hot_reload trigger` 25.7 KB and `kongor_client_eval_lua reload_ui` 25 KB, each carrying a `[NEXUS_GLOBALS]` dump of global names. `kongor_wait_for ui_present` 22 KB tree in `observedState`. `kongor_ui_flow` responses embed the full executed Lua (2-7.5 KB). |
| **Root Cause** | Handlers serialize the complete engine response into the tool result. No shared size budget or truncation layer exists. Not traced further. |
| **Fix / Remediation** | Fixed in `d3f207806` (Horizon 2.6). Enforced universal 8 KB UTF-8 response cap with bounded recursive JSON compaction, unique per-call spillover files, and 24h/500-file pruning in `McpToolRegistry.Preflight.cs`. |

## Invocation Parameters & Raw Logs
```text
Evidence (local, git-ignored): artifacts/audits/tao-mcp-live-audit-2026-11-17/run2_results.jsonl (field text_len)
row 45  kongor_game_lua {inspect_ui}                 68257 bytes
row 122 kongor_game_lua {tao_dispatch}               68267 bytes
row 126 kongor_k2_ui_inspect {live, systembar}       51136 bytes
row 402 kongor_k2_ui_inspect {live, depth 1}         48956 bytes
row 260 kongor_reload_and_verify                     27553 bytes
row 254 kongor_hot_reload {trigger}                  25721 bytes
row 258 kongor_client_eval_lua {reload_ui}           25176 bytes
row 100 kongor_wait_for {ui_present}                 ~22 KB
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_lua
```

## Prevention Invariant
Default responses stay under 8 KB; honour depth, filter and limit parameters and return large payloads as an artifact path.

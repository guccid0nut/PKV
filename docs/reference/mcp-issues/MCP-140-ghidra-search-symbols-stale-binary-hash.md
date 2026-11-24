---
id: MCP-140
server: ghidra
tool: search_symbols_by_name
category: binary-decode
title: "GHIDRA-SEARCH-SYMBOLS-STALE-BINARY-HASH"
status: superseded
superseded_by: "MCP-034"
supersedes: []
occurrences: 1
date: 2026-10-13
last_seen: 2026-10-13
tags: [ghidra, search_symbols_by_name, binary-decode]
prevention_rule: "Always pass the exact short-hash binary identifier from the active Ghidra project (`/k2_x64.dll-64104e`, `/cgame_x64.dll-28e88a`, `/game_shared_x64.dll-c34c36`)."
---

# MCP-140: GHIDRA-SEARCH-SYMBOLS-STALE-BINARY-HASH

## Summary
Repeat occurrence of [`MCP-034`] on `ghidra` / `search_symbols_by_name` (`binary-decode`). Superseded by `MCP-034`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `search_symbols_by_name` |
| **Expected** | Search symbols in `k2_x64.dll`. |
| **Actual (Symptom)** | Returned `Binary k2_x64.dll-039c226259e674d601e02f3bd3ed9a10 not found. Available binaries: ['/game_x64.dll-a6da54', '/game_shared_x64.dll-c34c36', '/cgame_x64.dll-28e88a', '/k2_x64.dll-64104e', '/version.dll-bf38ef', '/GearLink_MsProcess.exe-ebbe6e', '/vid_d3d9_x64.dll-918f17', '/hon_x64.exe-8fde56... |
| **Root Cause** | Used an older full MD5 binary suffix instead of the current Ghidra project short-hash binary name `/k2_x64.dll-64104e`. |
| **Fix / Remediation** | Always pass the exact short-hash binary identifier from the active Ghidra project (`/k2_x64.dll-64104e`, `/cgame_x64.dll-28e88a`, `/game_shared_x64.dll-c34c36`). |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"binary_name": "k2_x64.dll-039c226259e674d601e02f3bd3ed9a10", "query": "FrameLength"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool search_symbols_by_name
```

## Prevention Invariant
Always pass the exact short-hash binary identifier from the active Ghidra project (`/k2_x64.dll-64104e`, `/cgame_x64.dll-28e88a`, `/game_shared_x64.dll-c34c36`).

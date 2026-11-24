---
id: MCP-103
server: ghidra
tool: decompile_function
category: decompiler-timeout
title: "`ghidra` / `decompile_function` Binary 'game-x86_64-server.so' Not Found"
status: superseded
superseded_by: "MCP-008"
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [ghidra, decompile_function, decompiler-timeout]
prevention_rule: "Consult `list_project_binaries` or query Windows counterpart `game_x64.dll` which shares identical C++ codebase logic."
---

# MCP-103: `ghidra` / `decompile_function` Binary 'game-x86_64-server.so' Not Found

## Summary
Repeat occurrence of [`MCP-008`] on `ghidra` / `decompile_function` (`decompiler-timeout`). Superseded by `MCP-008`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `decompile_function` |
| **Expected** | Decompile function at `0xfe650` in Linux server binary `game-x86_64-server.so`. |
| **Actual (Symptom)** | Failed with `Binary game-x86_64-server.so not found. Available binaries: ['/game_x64.dll-a6da54', '/game_shared_x64.dll-c34c36', '/cgame_x64.dll-28e88a', '/k2_x64.dll-64104e', ...]`. |
| **Root Cause** | The Ghidra headless MCP project pre-bakes Windows PE binaries (`game_x64.dll`, `k2_x64.dll`, etc.); Linux ELF `.so` files are not part of the active Windows project database. |
| **Fix / Remediation** | Use `game_x64.dll` for decompilation or disassemble directly using capstone on local Linux ELF. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "game-x86_64-server.so",
    "name_or_address": "0xfe650"
  }
  

Logs:

  Error executing tool decompile_function: Binary game-x86_64-server.so not found. Available binaries: ['/game_x64.dll-a6da54', '/game_shared_x64.dll-c34c36', '/cgame_x64.dll-28e88a', '/k2_x64.dll-64104e', '/GearLink_MsProcess.exe-ebbe6e', '/vid_d3d9_x64.dll-918f17', '/hon_x64.exe-8fde56', '/vid_gl2_x64.dll-c32202']
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool decompile_function
```

## Prevention Invariant
Consult `list_project_binaries` or query Windows counterpart `game_x64.dll` which shares identical C++ codebase logic.

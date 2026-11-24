---
id: MCP-044
server: ghidra
tool: decompile_function
category: decompiler-timeout
title: "Ghidra decompile_function Binary Not Found for Linux ELF (libk2-x86_64-server.so)"
status: superseded
superseded_by: "MCP-008"
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [ghidra, decompile_function, decompiler-timeout]
prevention_rule: "Verify project binaries via `list_project_binaries`; Linux ELF not yet ingested in Ghidra project."
---

# MCP-044: Ghidra decompile_function Binary Not Found for Linux ELF (libk2-x86_64-server.so)

## Summary
Repeat occurrence of [`MCP-008`] on `ghidra` / `decompile_function` (`decompiler-timeout`). Superseded by `MCP-008`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `decompile_function` |
| **Expected** | Decompile function at `0xe1f510` in `libk2-x86_64-server.so`. |
| **Actual (Symptom)** | Failed with `Binary libk2-x86_64-server.so not found. Available binaries: ['/game_x64.dll-a6da54', '/game_shared_x64.dll-c34c36', '/cgame_x64.dll-28e88a', '/k2_x64.dll-64104e', '/GearLink_MsProcess.exe-ebbe6e', '/vid_d3d9_x64.dll-918f17', '/hon_x64.exe-8fde56', '/vid_gl2_x64.dll-c32202']`. |
| **Root Cause** | The Ghidra ProjectKongor repository currently only contains pre-baked Windows PE64 binaries. The Linux ELF binary `libk2-x86_64-server.so` has not been ingested via `import_binary` / `ingest_binaries.py`. |
| **Fix / Remediation** | Confirmed available binaries; analyzed assembly directly via objdump / local tools. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "libk2-x86_64-server.so",
    "name_or_address": "0xe1f510"
  }
  

Logs:

  Error executing tool decompile_function: Binary libk2-x86_64-server.so not found. Available binaries: ['/game_x64.dll-a6da54', ... ]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool decompile_function
```

## Prevention Invariant
Verify project binaries via `list_project_binaries`; Linux ELF not yet ingested in Ghidra project.

---
id: MCP-084
server: ghidra
tool: disassemble
category: binary-decode
title: "`ghidra` disassemble on `libk2-x86_64-server.so` Fails Due to Un-Ingested ELF Binary"
status: codified
codified_in: ".agents/skills/kongor-crash-triage/SKILL.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: []
occurrences: 10
date: 2026-09-25
last_seen: 2026-11-16
tags: [ghidra, disassemble, binary-decode]
prevention_rule: "Before querying Ghidra, verify binary availability in `list_project_binaries`. For Linux ELF binaries, use local `objdump -d` or Python disassemblers via PowerShell scripts."
---

# MCP-084: `ghidra` disassemble on `libk2-x86_64-server.so` Fails Due to Un-Ingested ELF Binary

## Summary
Tracked MCP issue on `ghidra` / `disassemble` (category: `binary-decode`, status: `codified`, occurrences: 10). Disassembly of `0x00CCB3DF` from Linux dedicated server binary `libk2-x86_64-server.so`. -> Errored with `Error executing tool disassemble: Binary libk2-x86_64-server.so not found. Available binaries: ['/game_x64.dll-a6da54', '/game_shared_x64.dll-c34c36', '/cgame_x64.dll-28e88a', '/k2_x64.dll-64104e', '/GearLink_MsProcess.exe-ebbe6e', '/vid_d3d9_x64.dll-918f17', '/hon_x64.exe-8fde56', '/v...

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `disassemble` |
| **Expected** | Disassembly of `0x00CCB3DF` from Linux dedicated server binary `libk2-x86_64-server.so`. |
| **Actual (Symptom)** | Errored with `Error executing tool disassemble: Binary libk2-x86_64-server.so not found. Available binaries: ['/game_x64.dll-a6da54', '/game_shared_x64.dll-c34c36', '/cgame_x64.dll-28e88a', '/k2_x64.dll-64104e', '/GearLink_MsProcess.exe-ebbe6e', '/vid_d3d9_x64.dll-918f17', '/hon_x64.exe-8fde56', '/v... |
| **Root Cause** | The Ghidra project only has the Windows client binaries ingested; Linux ELF binaries (`libk2-x86_64-server.so`) are not imported into the Ghidra project. |
| **Fix / Remediation** | Use Python script with `capstone` or `objdump` on `games/server/linux-x64/libk2-x86_64-server.so`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "address": "0x00CCB3DF",
    "binary_name": "libk2-x86_64-server.so",
    "count": 25
  }
  

Logs:

  Error executing tool disassemble: Binary libk2-x86_64-server.so not found.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool disassemble
```

## Prevention Invariant
Before querying Ghidra, verify binary availability in `list_project_binaries`. For Linux ELF binaries, use local `objdump -d` or Python disassemblers via PowerShell scripts.

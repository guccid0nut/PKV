---
id: MCP-006
server: ghidra
tool: find_function
category: decompiler-timeout
title: "Ghidra MCP Unresolved C++ Symbol ('CHostClient::ReadServerPackets' Not Found)"
status: codified
codified_in: ".agents/skills/kongor-crash-triage/SKILL.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: []
occurrences: 42
date: 2026-09-24
last_seen: 2026-11-17
tags: [ghidra, find_function, decompiler-timeout]
prevention_rule: "Use `search_symbols_by_name` or `search_strings` first; pass virtual address to decompile."
---

# MCP-006: Ghidra MCP Unresolved C++ Symbol ('CHostClient::ReadServerPackets' Not Found)

## Summary
Tracked MCP issue on `ghidra` / `find_function` (category: `decompiler-timeout`, status: `codified`, occurrences: 42). Ghidra locates and decompiles `CHostClient::ReadServerPackets` from `k2_x64.dll` or `hon_x64.exe`. -> Both `find_function` and `decompile_function_by_name_or_addr` threw error: `Function or symbol 'CHostClient::ReadServerPackets' not found.`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `find_function` |
| **Expected** | Ghidra locates and decompiles `CHostClient::ReadServerPackets` from `k2_x64.dll` or `hon_x64.exe`. |
| **Actual (Symptom)** | Both `find_function` and `decompile_function_by_name_or_addr` threw error: `Function or symbol 'CHostClient::ReadServerPackets' not found.` |
| **Root Cause** | In MSVC x64 C++ compilation, method names in PE binaries (`k2_x64.dll`, `hon_x64.exe`) are mangled (e.g. `?ReadServerPackets@CHostClient@@...`), inlined, or assigned generic auto-generated labels (`FUN_180...`). Direct exact demangled string lookups (`Class::Method`) in Ghidra fail unless the symbol was explicitly demangled and mapped during analys... |
| **Fix / Remediation** | Documented two-step discovery protocol and cross-referenced under *Ghidra MCP Reverse-Engineering & Native Triangulation Rules* in `GEMINI.md`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "symbol_or_address": "CHostClient::ReadServerPackets"
  }
  

Logs:

  [23:20:23] [ASPIRE HOST] [ERROR] 2026-09-24 18:20:23,335 [ERROR] [pyghidra_mcp.tools] Error in find_function: Function or symbol 'CHostClient::ReadServerPackets' not found.
  [23:20:23] [ASPIRE HOST] [ERROR] 2026-09-24 18:20:23,338 [ERROR] [pyghidra_mcp.tools] Error in decompile_function_by_name_or_addr: Function or symbol 'CHostClient::ReadServerPackets' not found.
  logs/ghidra_mcp.log: lines 2289-2290
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool find_function
```

## Prevention Invariant
Use `search_symbols_by_name` or `search_strings` first; pass virtual address to decompile.

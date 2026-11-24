---
id: MCP-008
server: ghidra
tool: decompile_function
category: decompiler-timeout
title: "Ghidra MCP Decompilation Timeout on Complex Native Network Function (0x180adf600)"
status: codified
codified_in: ".agents/skills/kongor-crash-triage/SKILL.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: ["MCP-013", "MCP-018", "MCP-024", "MCP-027", "MCP-032", "MCP-038", "MCP-044", "MCP-064", "MCP-068", "MCP-075", "MCP-095", "MCP-103", "MCP-139", "MCP-218", "MCP-223"]
occurrences: 25
date: 2026-09-24
last_seen: 2026-11-17
tags: [ghidra, decompile_function, decompiler-timeout]
prevention_rule: "`FastMCP.call_tool` hook extends default `decompile_function` timeout to 60s."
---

# MCP-008: Ghidra MCP Decompilation Timeout on Complex Native Network Function (0x180adf600)

## Summary
Tracked MCP issue on `ghidra` / `decompile_function` (category: `decompiler-timeout`, status: `codified`, occurrences: 25). Decompiled C code of function at `0x180adf600` containing rate limiter logic. -> Command timed out after 30 seconds: `"error": "Decompilation timed out after 30 seconds"`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `decompile_function` |
| **Expected** | Decompiled C code of function at `0x180adf600` containing rate limiter logic. |
| **Actual (Symptom)** | Command timed out after 30 seconds: `"error": "Decompilation timed out after 30 seconds"`. |
| **Root Cause** | The function at `0x180adf600` is a massive network dispatch / rate limiting routine with dozens of basic blocks and extensive control flow graphs. The default `timeout_sec: 30` in `decompile_function` was exceeded during Ghidra's AST optimization phase. |
| **Fix / Remediation** | Code patch in `src/tools/ghidra/mcp_server.py`: `FastMCP.call_tool` hook intercepts `decompile_function` and increases the default `timeout_sec` from 30s to 60s whenever not explicitly specified by the caller, preventing premature timeouts on complex native functions. - Added unit test in `tests/unit/tools/test_ghidra_mcp.py`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "binary_name": "k2_x64.dll",
    "include_strings": true,
    "name_or_address": "0x180adf600"
  }
  

Logs:

  {
    "name": "0x180adf600",
    "code": "",
    "signature": null,
    "error": "Decompilation timed out after 30 seconds"
  }
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool decompile_function
```

## Prevention Invariant
`FastMCP.call_tool` hook extends default `decompile_function` timeout to 60s.

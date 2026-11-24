---
id: MCP-188
server: frida
tool: frida-mcp
category: runtime-error
title: "FRIDA-MCP-SETUP-AND-FASTMCP-MIGRATION (MCP-2026-10-06)"
status: investigating
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-06
last_seen: 2026-10-06
tags: [frida, frida-mcp, runtime-error]
prevention_rule: "Use `try: from mcp.server.mcpserver import MCPServer as FastMCP except ImportError: from mcp.server.fastmcp import FastMCP` to maintain compatibility across MCP SDK v1 and v2."
---

# MCP-188: FRIDA-MCP-SETUP-AND-FASTMCP-MIGRATION (MCP-2026-10-06)

## Summary
Tracked MCP issue on `frida` / `frida-mcp` (category: `runtime-error`, status: `patched`, occurrences: 1). Frida MCP server launches with 13 instrumentation tools. -> Missing from `mcp_config.json`; when installed from `dnakov/frida-mcp`, crashed with `ModuleNotFoundError: No module named 'mcp.server.fastmcp'`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `frida` / `frida-mcp` |
| **Expected** | Frida MCP server launches with 13 instrumentation tools. |
| **Actual (Symptom)** | Missing from `mcp_config.json`; when installed from `dnakov/frida-mcp`, crashed with `ModuleNotFoundError: No module named 'mcp.server.fastmcp'`. |
| **Root Cause** | Server was absent from config, and upstream `dnakov/frida-mcp` was written for MCP 1.x using deprecated `FastMCP` rather than modern `MCPServer`. |
| **Fix / Remediation** | Use `try: from mcp.server.mcpserver import MCPServer as FastMCP except ImportError: from mcp.server.fastmcp import FastMCP` to maintain compatibility across MCP SDK v1 and v2. |

## Invocation Parameters & Raw Logs
```text
Parameters:
Initial server startup
* **Expected**: Frida MCP server launches with 13 instrumentation tools.
* **Actual**: Missing from `mcp_config.json`; when installed from `dnakov/frida-mcp`, crashed with `ModuleNotFoundError: No module named 'mcp.server.fastmcp'`.
* **Root Cause**: Server was absent from config, and upstream `dnakov/frida-mcp` was written for MCP 1.x using deprecated `FastMCP` rather than modern `MCPServer`.
* **Status**: `[PATCHED]` (Created dedicated virtual environment `src/tools/frida/.venv`, installed `frida-17.22.2` and `frida-mcp`, updated imports to use `MCPServer`, created `src/tools/frida/mcp_server.py`, and added `frida` to all config locations: `<appDataDir>\mcp_config.json`, `antigravity-ide\mcp_config.json`, and `config\mcp_config.json`).
* **Next-Time Prevention Rule**: Use `try: from mcp.server.mcpserver import MCPServer as FastMCP except ImportError: from mcp.server.fastmcp import FastMCP` to maintain compatibility across MCP SDK v1 and v2.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server frida -Tool frida-mcp
```

## Prevention Invariant
Use `try: from mcp.server.mcpserver import MCPServer as FastMCP except ImportError: from mcp.server.fastmcp import FastMCP` to maintain compatibility across MCP SDK v1 and v2.

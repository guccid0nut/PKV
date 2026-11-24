---
id: MCP-189
server: sqlite
tool: sqlite:latest
category: runtime-error
title: "SQLITE-MCP-SERVER-LIST-RESOURCES-ATTRIBUTE-ERROR (MCP-2026-10-06)"
status: investigating
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-06
last_seen: 2026-10-06
tags: [sqlite, sqlite:latest, runtime-error]
prevention_rule: "For older MCP Python packages referencing deprecated low-level `Server` decorators, replace them with clean `MCPServer` implementations."
---

# MCP-189: SQLITE-MCP-SERVER-LIST-RESOURCES-ATTRIBUTE-ERROR (MCP-2026-10-06)

## Summary
Tracked MCP issue on `sqlite` / `sqlite:latest` (category: `runtime-error`, status: `patched`, occurrences: 1). SQLite MCP server starts and enables tools (`read_query`, `write_query`, `create_table`, `list_tables`, `describe_table`). -> `AttributeError: 'Server' object has no attribute 'list_resources'` during `server.py` startup.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `sqlite` / `sqlite:latest` |
| **Expected** | SQLite MCP server starts and enables tools (`read_query`, `write_query`, `create_table`, `list_tables`, `describe_table`). |
| **Actual (Symptom)** | `AttributeError: 'Server' object has no attribute 'list_resources'` during `server.py` startup. |
| **Root Cause** | The PyPI `mcp-server-sqlite` package called the legacy `mcp 1.x` `@server.list_resources()` decorator on the low-level `Server` class, which was removed in `mcp 2.x`. |
| **Fix / Remediation** | For older MCP Python packages referencing deprecated low-level `Server` decorators, replace them with clean `MCPServer` implementations. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`--db-path /workspace/data/kongor_dev.db`
* **Expected**: SQLite MCP server starts and enables tools (`read_query`, `write_query`, `create_table`, `list_tables`, `describe_table`).
* **Actual**: `AttributeError: 'Server' object has no attribute 'list_resources'` during `server.py` startup.
* **Root Cause**: The PyPI `mcp-server-sqlite` package called the legacy `mcp 1.x` `@server.list_resources()` decorator on the low-level `Server` class, which was removed in `mcp 2.x`.
* **Status**: `[PATCHED]` (Built custom `server.py` utilizing high-level `MCPServer` from `mcp 2.x` inside `mcp/sqlite:latest`, which natively supports all 5 database tools and `server/discover` capability negotiation).
* **Next-Time Prevention Rule**: For older MCP Python packages referencing deprecated low-level `Server` decorators, replace them with clean `MCPServer` implementations.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server sqlite -Tool sqlite:latest
```

## Prevention Invariant
For older MCP Python packages referencing deprecated low-level `Server` decorators, replace them with clean `MCPServer` implementations.

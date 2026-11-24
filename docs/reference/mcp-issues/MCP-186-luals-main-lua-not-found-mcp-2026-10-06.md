---
id: MCP-186
server: luals
tool: mcp_server.py
category: binary-decode
title: "LUALS-MAIN-LUA-NOT-FOUND (MCP-2026-10-06)"
status: investigating
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-06
last_seen: 2026-10-06
tags: [luals, mcp_server.py, binary-decode]
prevention_rule: "Always preserve the entire release package tree when distributing language server binaries that depend on external Lua script runtimes."
---

# MCP-186: LUALS-MAIN-LUA-NOT-FOUND (MCP-2026-10-06)

## Summary
Tracked MCP issue on `luals` / `mcp_server.py` (category: `binary-decode`, status: `patched`, occurrences: 1). Lua language server starts and completes LSP handshake. -> Failed with `(bootstrap.lua):3: src/tools/luals/bin/bin/main.lua: No such file or directory`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `luals` / `mcp_server.py` |
| **Expected** | Lua language server starts and completes LSP handshake. |
| **Actual (Symptom)** | Failed with `(bootstrap.lua):3: src/tools/luals/bin/bin/main.lua: No such file or directory`. |
| **Root Cause** | Standalone binary `lua-language-server.exe` was present in `bin/bin`, but the required distribution assets (`main.lua`, `script/`, `meta/`, `locale/`) were missing. |
| **Fix / Remediation** | Always preserve the entire release package tree when distributing language server binaries that depend on external Lua script runtimes. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`--workspace-dir "<workspace>"`
* **Expected**: Lua language server starts and completes LSP handshake.
* **Actual**: Failed with `(bootstrap.lua):3: src/tools/luals/bin/bin/main.lua: No such file or directory`.
* **Root Cause**: Standalone binary `lua-language-server.exe` was present in `bin/bin`, but the required distribution assets (`main.lua`, `script/`, `meta/`, `locale/`) were missing.
* **Status**: `[PATCHED]` (Downloaded official LuaLS v3.19.1 distribution and installed all core runtime assets into `src/tools/luals/bin/`; verified LSP handshake initializes and shuts down cleanly).
* **Next-Time Prevention Rule**: Always preserve the entire release package tree when distributing language server binaries that depend on external Lua script runtimes.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server luals -Tool mcp_server.py
```

## Prevention Invariant
Always preserve the entire release package tree when distributing language server binaries that depend on external Lua script runtimes.

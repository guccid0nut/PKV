---
id: MCP-157
server: frida
tool: get_process_by_name
category: window-resolution
title: "(formerly MCP-094): FRIDA-GET-PROCESS-BY-NAME-MISSING-DEVICE-ID"
status: investigating
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-07
last_seen: 2026-10-07
tags: [frida, get_process_by_name, window-resolution]
prevention_rule: "`src/tools/frida/mcp_server.py` and `frida_mcp/cli.py` now default `device_id='local'` across all device-scoped Frida tools."
---

# MCP-157: (formerly MCP-094): FRIDA-GET-PROCESS-BY-NAME-MISSING-DEVICE-ID

## Summary
Tracked MCP issue on `frida` / `get_process_by_name` (category: `window-resolution`, status: `patched`, occurrences: 1). Locates process by name on local Windows desktop. -> Threw error `Error executing tool get_process_by_name`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `frida` / `get_process_by_name` |
| **Expected** | Locates process by name on local Windows desktop. |
| **Actual (Symptom)** | Threw error `Error executing tool get_process_by_name`. |
| **Root Cause** | The `frida` MCP server defaults to a USB attached mobile device when `device_id` is omitted. On desktop Windows development machines without a mobile USB target, `device_id: "local"` must be explicitly specified. |
| **Fix / Remediation** | `src/tools/frida/mcp_server.py` and `frida_mcp/cli.py` now default `device_id="local"` across all device-scoped Frida tools. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"name": "hon_x64.exe"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server frida -Tool get_process_by_name
```

## Prevention Invariant
`src/tools/frida/mcp_server.py` and `frida_mcp/cli.py` now default `device_id='local'` across all device-scoped Frida tools.

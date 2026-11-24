---
id: MCP-145
server: frida
tool: enumerate_processes
category: parameter-mismatch
title: "(formerly MCP-082): FRIDA-ENUMERATE-PROCESSES-MISSING-DEVICE-ID"
status: investigating
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-06
last_seen: 2026-10-06
tags: [frida, enumerate_processes, parameter-mismatch]
prevention_rule: "`src/tools/frida/mcp_server.py` and `frida_mcp/cli.py` now default `device_id='local'` across all device-scoped Frida tools and automatically fall back to `frida.get_local_device()` when no USB device is attached."
---

# MCP-145: (formerly MCP-082): FRIDA-ENUMERATE-PROCESSES-MISSING-DEVICE-ID

## Summary
Tracked MCP issue on `frida` / `enumerate_processes` (category: `parameter-mismatch`, status: `patched`, occurrences: 1). Return list of processes on the local machine. -> `Error executing tool enumerate_processes` because `device_id` defaults to USB device if not specified, and no USB device is connected.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `frida` / `enumerate_processes` |
| **Expected** | Return list of processes on the local machine. |
| **Actual (Symptom)** | `Error executing tool enumerate_processes` because `device_id` defaults to USB device if not specified, and no USB device is connected. |
| **Root Cause** | The tool schema states "Uses USB device if not specified". In desktop pair programming environments without a connected mobile device, this results in an immediate failure. |
| **Fix / Remediation** | `src/tools/frida/mcp_server.py` and `frida_mcp/cli.py` now default `device_id="local"` across all device-scoped Frida tools and automatically fall back to `frida.get_local_device()` when no USB device is attached. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server frida -Tool enumerate_processes
```

## Prevention Invariant
`src/tools/frida/mcp_server.py` and `frida_mcp/cli.py` now default `device_id='local'` across all device-scoped Frida tools and automatically fall back to `frida.get_local_device()` when no USB device is attached.

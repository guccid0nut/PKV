---
id: MCP-226
server: frida
tool: execute_in_session
category: runtime-error
title: "FRIDA17-MODULE-FINDEXPORTBYNAME-INSTANCE-METHOD"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-16
last_seen: 2026-10-16
tags: [frida, execute_in_session, runtime-error]
prevention_rule: "In Frida 17, invoke Process.findModuleByName(name).findExportByName(exportName) instead of static Module.findExportByName."
---

# MCP-226: FRIDA17-MODULE-FINDEXPORTBYNAME-INSTANCE-METHOD

## Summary
When executing JavaScript probes via `frida/execute_in_session` under Frida 17, calling the legacy static `Module.findExportByName(moduleName, exportName)` throws `TypeError: not a function` because Frida 17 moved `findExportByName` onto the `Module` instance returned by `Process.findModuleByName(name)` or `Process.getModuleByName(name)`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `frida` / `execute_in_session` |
| **Expected** | Resolve the exported symbol address from `version.dll` using `findExportByName`. |
| **Actual (Symptom)** | `{"status": "error", "error": "TypeError: not a function", "stack": "TypeError: not a function\n    at <eval> (<input>:18)"}` |
| **Root Cause** | Frida 17 removed static `Module.findExportByName(moduleName, symbolName)` from the global `Module` object; `findExportByName(symbolName)` is now an instance method on `Module`. |
| **Fix / Remediation** | Query exports via `const mod = Process.findModuleByName("version.dll"); const addr = mod ? mod.findExportByName("Kongor_NativeGuard_GetTextureSlotUsage") : null;`. |

## Invocation Parameters & Raw Logs
```text
Tool: frida/execute_in_session
Error: TypeError: not a function at Module.findExportByName("version.dll", "Kongor_NativeGuard_GetTextureSlotUsage")
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server frida -Tool execute_in_session
```

## Prevention Invariant
In Frida 17, invoke Process.findModuleByName(name).findExportByName(exportName) instead of static Module.findExportByName.

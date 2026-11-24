---
id: MCP-112
server: default_api
tool: view_file
category: binary-decode
title: "`view_file` Unsupported MIME Type `text/plain; charset=utf-16le` on K2 Engine Log"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-09-26
last_seen: 2026-09-26
tags: [default_api, view_file, binary-decode]
prevention_rule: "Read UTF-16 engine logs via PowerShell `Get-Content` (which handles UTF-16 natively) or convert to UTF-8 before viewing."
---

# MCP-112: `view_file` Unsupported MIME Type `text/plain; charset=utf-16le` on K2 Engine Log

## Summary
Tracked MCP issue on `default_api` / `view_file` (category: `binary-decode`, status: `resolved`, occurrences: 1). Display slice of `console.log`. -> Failed with `invalid tool call error (invalid_args) unsupported mime type text/plain; charset=utf-16le`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `default_api` / `view_file` |
| **Expected** | Display slice of `console.log`. |
| **Actual (Symptom)** | Failed with `invalid tool call error (invalid_args) unsupported mime type text/plain; charset=utf-16le`. |
| **Root Cause** | K2 engine outputs Unicode `console.log` in UTF-16LE encoding with BOM. The IDE `view_file` tool does not support UTF-16 text files. |
| **Fix / Remediation** | Use PowerShell `Get-Content` cmdlet to read `console.log`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "AbsolutePath": "c:\\ProjectKongor\\games\\client\\userdata\\Heroes of Newerth x64\\KONGOR\\extensions\\console.log",
    "StartLine": 2480,
    "EndLine": 2505
  }
  

Logs:

  Encountered error in tool execution: declaring permissions: cortex tool view_file: convert tool call for permissions: model output error: invalid tool call error (invalid_args) unsupported mime type text/plain; charset=utf-16le
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server default_api -Tool view_file
```

## Prevention Invariant
Read UTF-16 engine logs via PowerShell `Get-Content` (which handles UTF-16 natively) or convert to UTF-8 before viewing.

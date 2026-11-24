---
id: MCP-159
server: ghidra
tool: list_project_binary_metadata
category: binary-decode
title: "(formerly MCP-096): GHIDRA-LIST-PROJECT-BINARY-METADATA-BINARY-NAME-REQUIRED"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-07
last_seen: 2026-10-07
tags: [ghidra, list_project_binary_metadata, binary-decode]
prevention_rule: "Always pass `binary_name` to `list_project_binary_metadata`."
---

# MCP-159: (formerly MCP-096): GHIDRA-LIST-PROJECT-BINARY-METADATA-BINARY-NAME-REQUIRED

## Summary
Tracked MCP issue on `ghidra` / `list_project_binary_metadata` (category: `binary-decode`, status: `resolved`, occurrences: 1). Retrieve metadata for project binary. -> Threw validation error: `binary_name: Field required [type=missing]`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ghidra` / `list_project_binary_metadata` |
| **Expected** | Retrieve metadata for project binary. |
| **Actual (Symptom)** | Threw validation error: `binary_name: Field required [type=missing]`. |
| **Root Cause** | `list_project_binary_metadata` expects parameter named `binary_name` rather than `binary_path`. |
| **Fix / Remediation** | Always pass `binary_name` to `list_project_binary_metadata`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"binary_path": "/cgame_x64.dll-28e88a"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ghidra -Tool list_project_binary_metadata
```

## Prevention Invariant
Always pass `binary_name` to `list_project_binary_metadata`.

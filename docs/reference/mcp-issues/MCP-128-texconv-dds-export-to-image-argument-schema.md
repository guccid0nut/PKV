---
id: MCP-128
server: texconv
tool: dds_export_to_image
category: parameter-mismatch
title: "TEXCONV-DDS-EXPORT-TO-IMAGE-ARGUMENT-SCHEMA"
status: documented
superseded_by: ""
supersedes: ["MCP-154"]
occurrences: 2
date: 2026-10-11
last_seen: 2026-10-11
tags: [texconv, dds_export_to_image, parameter-mismatch]
prevention_rule: "Always pass `input_path` for `dds_export_to_image`."
---

# MCP-128: TEXCONV-DDS-EXPORT-TO-IMAGE-ARGUMENT-SCHEMA

## Summary
Tracked MCP issue on `texconv` / `dds_export_to_image` (category: `parameter-mismatch`, status: `resolved`, occurrences: 2). Export DDS texture to PNG. -> Validation error: `input_path` field required.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `texconv` / `dds_export_to_image` |
| **Expected** | Export DDS texture to PNG. |
| **Actual (Symptom)** | Validation error: `input_path` field required. |
| **Root Cause** | Parameter is `input_path`, not `file_path`. |
| **Fix / Remediation** | Always pass `input_path` for `dds_export_to_image`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"file_path": "..."}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server texconv -Tool dds_export_to_image
```

## Prevention Invariant
Always pass `input_path` for `dds_export_to_image`.

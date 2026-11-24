---
id: MCP-154
server: texconv
tool: dds_export_to_image
category: parameter-mismatch
title: "(formerly MCP-091): TEXCONV-DDS-EXPORT-PARAMETER-NAME-ERROR"
status: superseded
superseded_by: "MCP-128"
supersedes: []
occurrences: 1
date: 2026-10-07
last_seen: 2026-10-07
tags: [texconv, dds_export_to_image, parameter-mismatch]
prevention_rule: "Verify texconv / dds_export_to_image readiness and parameters before invocation."
---

# MCP-154: (formerly MCP-091): TEXCONV-DDS-EXPORT-PARAMETER-NAME-ERROR

## Summary
Repeat occurrence of [`MCP-128`] on `texconv` / `dds_export_to_image` (`parameter-mismatch`). Superseded by `MCP-128`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `texconv` / `dds_export_to_image` |
| **Expected** | Export DDS to PNG. |
| **Actual (Symptom)** | Validation error: `input_path: Field required`. |
| **Root Cause** | Tool expects `input_path` instead of `file_path`. |
| **Fix / Remediation** | - |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"file_path": "...", "output_format": "png"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server texconv -Tool dds_export_to_image
```

## Prevention Invariant
Verify texconv / dds_export_to_image readiness and parameters before invocation.

---
id: MCP-130
server: texconv
tool: dds_inspect_texture
category: parameter-mismatch
title: "TEXCONV-DDS-INSPECT-TEXTURE-PASCALCASE-PARAM"
status: superseded
superseded_by: "MCP-127"
supersedes: []
occurrences: 1
date: 2026-10-11
last_seen: 2026-10-11
tags: [texconv, dds_inspect_texture, parameter-mismatch]
prevention_rule: "Always verify schema and use `file_path` for `dds_inspect_texture`."
---

# MCP-130: TEXCONV-DDS-INSPECT-TEXTURE-PASCALCASE-PARAM

## Summary
Repeat occurrence of [`MCP-127`] on `texconv` / `dds_inspect_texture` (`parameter-mismatch`). Superseded by `MCP-127`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `texconv` / `dds_inspect_texture` |
| **Expected** | Return metadata for target DDS texture. |
| **Actual (Symptom)** | Validation error: `file_path` field required. |
| **Root Cause** | Tool expects snake_case `file_path`, but PascalCase `FilePath` was passed. |
| **Fix / Remediation** | Always verify schema and use `file_path` for `dds_inspect_texture`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"FilePath": "..."}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server texconv -Tool dds_inspect_texture
```

## Prevention Invariant
Always verify schema and use `file_path` for `dds_inspect_texture`.

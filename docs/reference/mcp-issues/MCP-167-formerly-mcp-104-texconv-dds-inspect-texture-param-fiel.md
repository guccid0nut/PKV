---
id: MCP-167
server: texconv
tool: dds_inspect_texture
category: parameter-mismatch
title: "(formerly MCP-104): TEXCONV-DDS-INSPECT-TEXTURE-PARAM-FIELD-REQUIRED"
status: superseded
superseded_by: "MCP-127"
supersedes: []
occurrences: 1
date: 2026-10-10
last_seen: 2026-10-10
tags: [texconv, dds_inspect_texture, parameter-mismatch]
prevention_rule: "Always pass `file_path` when invoking `texconv` / `dds_inspect_texture`."
---

# MCP-167: (formerly MCP-104): TEXCONV-DDS-INSPECT-TEXTURE-PARAM-FIELD-REQUIRED

## Summary
Repeat occurrence of [`MCP-127`] on `texconv` / `dds_inspect_texture` (`parameter-mismatch`). Superseded by `MCP-127`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `texconv` / `dds_inspect_texture` |
| **Expected** | Inspects DDS texture metadata. |
| **Actual (Symptom)** | Validation error: `file_path: Field required [type=missing]`. |
| **Root Cause** | `texconv / dds_inspect_texture` schema requires parameter named `file_path` rather than `TexturePath`. |
| **Fix / Remediation** | Always pass `file_path` when invoking `texconv` / `dds_inspect_texture`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"TexturePath": "test_1x1.dds"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server texconv -Tool dds_inspect_texture
```

## Prevention Invariant
Always pass `file_path` when invoking `texconv` / `dds_inspect_texture`.

---
id: MCP-127
server: texconv
tool: dds_inspect_texture
category: parameter-mismatch
title: "TEXCONV-DDS-INSPECT-TEXTURE-ARGUMENT-SCHEMA"
status: codified
codified_in: ".agents/rules/mcp_issues.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: ["MCP-167", "MCP-130"]
occurrences: 3
date: 2026-10-11
last_seen: 2026-10-11
tags: [texconv, dds_inspect_texture, parameter-mismatch]
prevention_rule: "Always pass `file_path` (snake_case) when calling `texconv` tools."
---

# MCP-127: TEXCONV-DDS-INSPECT-TEXTURE-ARGUMENT-SCHEMA

## Summary
Tracked MCP issue on `texconv` / `dds_inspect_texture` (category: `parameter-mismatch`, status: `resolved`, occurrences: 3). Inspect DDS texture properties. -> Validation error: `file_path` field required.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `texconv` / `dds_inspect_texture` |
| **Expected** | Inspect DDS texture properties. |
| **Actual (Symptom)** | Validation error: `file_path` field required. |
| **Root Cause** | Parameter is snake_case `file_path`, not PascalCase `FilePath`. |
| **Fix / Remediation** | Always pass `file_path` (snake_case) when calling `texconv` tools. |

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
Always pass `file_path` (snake_case) when calling `texconv` tools.

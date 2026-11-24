---
id: MCP-280
server: rider
tool: apply_patch
category: invocation-error
title: "Rider apply_patch discovery and quoted hunk context failed"
status: resolved
codified_in: .agents/rules/mcp_issues.md
verified_by: "5d8243331 + .agents/rules/mcp_issues.md"
superseded_by: ""
supersedes: []
occurrences: 3
date: 2026-11-17
last_seen: 2026-11-17
tags: [rider, apply_patch, invocation-error]
prevention_rule: "Supply a nonempty input patch and avoid unescaped quotes in command-line patch contexts; verify edits through the IDE."
---

# MCP-280: Rider apply_patch discovery and quoted hunk context failed

## Summary
An operator invocation of the Rider IDE patch tool failed while adding an observation to an MCP issue. This was an input quoting mistake, not a confirmed Rider defect. A second patch using context without embedded quotes succeeded. A later patch also missed because its target issue was already populated; this was stale context rather than a confirmed tool defect.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `rider` / `apply_patch` |
| **Expected** | Apply the supplied hunk to the matching issue document. |
| **Actual (Symptom)** | `0 out of 1 operations applied`; `Hunk context not found`. |
| **Root Cause** | The operator passed context containing unescaped double quotes through a quoted command-line argument. |
| **Fix / Remediation** | Use correctly escaped input or context without embedded quotes; the subsequent patch succeeded. |

## Invocation Parameters & Raw Logs
```text
apply_patch --input with an MCP-256 hunk containing unescaped double quotes
0 out of 1 operations applied. 1 operations failed to be applied due to errors:
docs/reference/mcp-issues/MCP-256-status-reports-static-wasapi-64ch-audio-summary-instead-of-a.md: Hunk context not found
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server rider -Tool apply_patch
```

## Prevention Invariant
Supply a nonempty input patch and avoid unescaped quotes in command-line patch contexts; verify edits through the IDE.

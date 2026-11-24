---
id: FN-000
kind: technique # engine-law | crash-triage | vfs | rendering | netcode | technique
title: "<Short descriptive title>"
binary_or_subsystem: "k2_x64.dll | cgame_x64.dll | override/ui | DXVK | VFS"
rvas: ["0x000000"]
status: verified # verified | superseded | invalidated
supersedes: []
invalidated_by: ""
date: 2026-10-15
tags: [k2, example]
---

# FN-000: <Short Descriptive Title>

## Summary
One-paragraph builder summary naming the exact binary, RVA/function, failure signature, and architectural invariant.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | Exact crash code (`0xC0000005`), visual defect, or VFS failure observed. |
| **Root Cause** | Exact native instruction, vtable offset, or engine behavior (`k2_x64.dll + 0x...`). |
| **Fix / Workaround** | Concrete code pattern, CVar, or script guardrail that eliminates the root cause. |

## Verification Oracle
Exact command or script that proves the invariant holds:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
Actionable rule to follow in future edits so this class of bug never recurs.

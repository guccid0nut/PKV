---
name: kongor-debt
description: >
  Audits and reports deliberate `shortcut:` comments (and legacy `ponytail:` markers)
  as a tracked technical debt ledger across the Project Kongor codebase.
  One-shot report, changes nothing. Use for "kongor debt", "audit shortcuts",
  "list technical debt", "check rot", /kongor-debt.
capability_contract:
  allowed_worker_roles: ["kongor-offline-worker", "kongor-game-worker"]
  allowed_tools: ["run_command", "view_file"]
  requires_worker_lock: false
  max_probes: 3
---

# Kongor Debt

In reverse-engineering, hot-reloading, and engine modding, temporary shims, mock packets,
and placeholder hooks are often necessary. Every deliberate shortcut is marked with a comment:

`shortcut: <the limit / ceiling>, <when to upgrade>`

This skill scans the repository using `scripts/qa/audit_debt.ps1` and collects all shortcuts into
a single structured ledger so temporary workarounds never quietly rot into permanent tech debt.

## Execution

Run the PowerShell audit script:
```powershell
powershell.exe -ExecutionPolicy Bypass -File scripts\qa\audit_debt.ps1
```

To export the results directly to documentation:
```powershell
powershell.exe -ExecutionPolicy Bypass -File scripts\qa\audit_debt.ps1 -OutFile docs\SHORTCUT_DEBT.md
```

## Audit Rules

1. **Extraction:**
   - Markers matching `(#|//|--|/*) shortcut: <ceiling>, <upgrade trigger>` are parsed into file, line, limit, and trigger.
   - Also tracks legacy `ponytail:` comment markers for compatibility.
2. **Rot Detection:**
   - Any shortcut comment that fails to state an explicit upgrade trigger or condition is flagged with `[NO TRIGGER - ROT RISK]`.
3. **Ledger Output:**
   - Grouped by file with total counts and rot risk summaries.
   - Read-only: does not modify code unless asked to persist markdown to `docs/SHORTCUT_DEBT.md`.

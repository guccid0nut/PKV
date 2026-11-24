---
id: MCP-275
server: kongor
tool: kongor_reload_and_verify
category: stale-tao-build
title: "kongor_reload_and_verify fails capabilities_staleness_check when skipBuildSync=true after Lua edits"
status: resolved
verified_by: "Live kongor_reload_and_verify(skipBuildSync=false, canaryDurationSeconds=2)"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_reload_and_verify, stale-tao-build, tao_lua]
prevention_rule: "Leave skipBuildSync=false (the default) on kongor_reload_and_verify so update_tao_build.ps1 refreshes the tao.lua build hash before ReloadInterfaces."
---

# MCP-275: kongor_reload_and_verify fails capabilities_staleness_check when skipBuildSync=true after Lua edits

## Summary
Invoking `kongor_reload_and_verify` with `{"skipBuildSync":true,"canaryDurationSeconds":2}` after modifying Lua files in `games/client/override/` triggers `ReloadInterfaces` cleanly (`4.4 ms`), but then fails the post-reload `capabilities_staleness_check` step because `TAO.BUILD` inside `games/client/override/ui/scripts/tao.lua` still holds the previous SHA-256 hash. Calling `kongor_reload_and_verify` with its default `skipBuildSync: false` updates `TAO.BUILD` in-process, synchronizes VFS mirrors, reloads interfaces, and passes `capabilities_staleness_check` on the first try.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_reload_and_verify` |
| **Expected** | `kongor_reload_and_verify` hot-reloads modified UI scripts and passes all verification steps. |
| **Actual (Symptom)** | `{"status":"failed","stepFailed":"capabilities_staleness_check","error":"Client is running stale TAO build. Engine: '81cf88a4...', Disk: '...tao.lua'."}` |
| **Root Cause** | Passing `skipBuildSync: true` skips updating `TAO.BUILD` in `tao.lua` when `override/` Lua files changed since the last build hash stamp. |
| **Fix / Remediation** | Keep `skipBuildSync: false` (default) when calling `kongor_reload_and_verify` so the in-process build hash sync updates `TAO.BUILD` and VFS mirrors prior to `ReloadInterfaces`. |

## Invocation Parameters & Raw Logs
```json
{
  "failedCall": { "canaryDurationSeconds": 2, "skipBuildSync": true, "skipLint": false },
  "passingCall": { "canaryDurationSeconds": 2, "skipBuildSync": false, "skipLint": false },
  "passingVerdict": { "status": "success", "verdict": "pass", "elapsedMs": 3594.1 }
}
```

## Verification / Pre-Flight Oracle
Exact command that verifies this issue:
```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_reload_and_verify
```

## Prevention Invariant
Leave skipBuildSync=false (the default) on kongor_reload_and_verify so update_tao_build.ps1 refreshes the tao.lua build hash before ReloadInterfaces.

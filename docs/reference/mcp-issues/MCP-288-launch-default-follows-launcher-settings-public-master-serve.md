---
id: MCP-288
server: kongor
tool: kongor_game_client_manage
category: safety
title: "Launch default follows launcher settings public master server not documented loopback"
status: resolved
verified_by: "1ebe5f40d + McpToolContractTests.Tranche1A + live NetGuard stdio audit"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_game_client_manage, safety]
prevention_rule: "Launch defaults must match the advertised schema default (loopback); a non-loopback master server or saved-credential auto-login requires an explicit argument."
---

# MCP-288: Launch default follows launcher settings public master server not documented loopback

## Summary
`kongor_game_client_manage launch` without `masterServer` started `hon_x64.exe` against `masterserver.newbs.io` (the project's Cloudflare Tunnel endpoint, see `MCP-251`) with saved-credential auto-login, although the tool schema documents the default as `127.0.0.1:55555`. An agent asked to test locally therefore sent a login for the default dev account to the public endpoint. Found in the 2026-11-17 live audit (run 1). Open; no fix applied.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_client_manage` |
| **Expected** | Omitting `masterServer` uses the documented default `127.0.0.1:55555`, or the schema states that the default comes from `games/client/launcher_settings.json`. |
| **Actual (Symptom)** | Launch arguments contained `-masterserver masterserver.newbs.io -webserver masterserver.newbs.io -messageserver masterserver.newbs.io`, `+Set login_name`, a redacted `login_password` and `+Set login_autoLogin true`. The client held eight TCP connections to public Cloudflare addresses on port 80 and showed `Login Failed: Account Not Found`. Passing `masterServer: "127.0.0.1:55555"` in run 2 produced loopback-only connections. |
| **Root Cause** | `GameLauncherService.ResolveDefaultMasterServer` (`GameLauncherService.cs`, around lines 728-771) reads `CustomMasterServerAddress` and `MasterServerAddress` with case-sensitive `TryGetProperty`. `games/client/launcher_settings.json` stores the loopback override as camelCase `customMasterServerAddress` (not matched) and also has PascalCase `MasterServerAddress: masterserver.newbs.io` (matched). Callers: `GameClientManageHandler.cs` lines 128-130 (launch, restart) and `MatchLaunchHandler.cs` lines 88-90. Auto-boot paths in `ClientNavigateHandler.cs` and `ClientPracticeHandler.cs` hard-code `127.0.0.1:55555`, so the tools disagree. |
| **Fix / Remediation** | Not fixed. Workaround: always pass `masterServer: "127.0.0.1:55555"` to `kongor_game_client_manage launch/restart` and `kongor_match_launch launch`, and read the echoed launch arguments before issuing further calls. |

## Invocation Parameters & Raw Logs
```text
Evidence (local, git-ignored): artifacts/audits/tao-mcp-live-audit-2026-11-17/run1_results.jsonl and run2_results.jsonl
run 1 row 0: kongor_game_client_manage {action: launch, window_mode: headless}
  -> arguments: -masterserver masterserver.newbs.io ... +Set login_autoLogin true
run 2 row 0: kongor_game_client_manage {action: launch, window_mode: headless, masterServer: 127.0.0.1:55555}
  -> only peer 127.0.0.1:55555
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_game_client_manage
```

## Prevention Invariant
Launch defaults must match the advertised schema default (loopback); a non-loopback master server or saved-credential auto-login requires an explicit argument.

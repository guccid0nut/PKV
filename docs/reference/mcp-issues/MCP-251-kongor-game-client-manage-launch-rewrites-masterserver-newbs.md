---
id: MCP-251
server: kongor
tool: kongor_game_client_manage
category: parameter-override
title: "kongor_game_client_manage Launch Rewrites masterserver.newbs.io to 127.0.0.1:55555"
status: patched
verified_by: "GameToolsTests.NormalizeMasterServerAddress_VariousInputs_ResolvesProperly"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-27
last_seen: 2026-10-27
tags: [kongor, kongor_game_client_manage, parameter-override, cloudflare]
prevention_rule: "GameClientManageHandler.Launch.cs and GameLauncherService.NormalizeMasterServerAddress preserve bare 'masterserver.newbs.io' without rewriting to 127.0.0.1:55555 or appending legacy port :83 so Cloudflare Tunnel (:80/:443) connections succeed."
---

# MCP-251: kongor_game_client_manage Launch Rewrites masterserver.newbs.io to 127.0.0.1:55555

## Summary
Invoking `kongor_game_client_manage` (`action: "launch"`) with `masterServer: "masterserver.newbs.io"` previously failed to connect to the Cloudflare Tunnel endpoint because `GameClientManageHandler.Launch.cs:92` explicitly rejected `"masterserver.newbs.io"` and fell back to `127.0.0.1:55555`, while `GameLauncherService.NormalizeMasterServerAddress` rewrote `"masterserver.newbs.io"` to `"masterserver.newbs.io:83"` (returning HTTP 522 / connect timeout on Cloudflare). Both overrides have been patched and covered by unit tests.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_client_manage` |
| **Expected** | `hon_x64.exe` launches with `-masterserver masterserver.newbs.io -webserver masterserver.newbs.io -messageserver masterserver.newbs.io` (matching Willowmaker's `BuildMasterServerAddress()`). |
| **Actual (Symptom)** | `GameClientManageHandler.Launch.cs` substituted `127.0.0.1:55555`, and `https://masterserver.newbs.io` was rewritten to `masterserver.newbs.io:83` which timed out on Cloudflare. |
| **Root Cause** | Legacy fallback guard `!string.Equals(masterServer, "masterserver.newbs.io")` in `GameClientManageHandler.Launch.cs:92` and legacy `:83` rewrite in `GameLauncherService.cs:715`. |
| **Fix / Remediation** | Removed both legacy overrides in `GameClientManageHandler.Launch.cs` and `GameLauncherService.cs` and updated `GameToolsTests` and `McpConcurrencyHardeningTests`. |

## Invocation Parameters & Raw Logs
```text
{"action":"launch","window_mode":"headless","masterServer":"masterserver.newbs.io"}
-> Previously resolved effectiveMasterServer = "127.0.0.1:55555" or "masterserver.newbs.io:83"
-> Patched: effectiveMasterServer = "masterserver.newbs.io"
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
dotnet test tests/unit/tools/Kongor.Mcp.Tests/Kongor.Mcp.Tests.csproj --filter NormalizeMasterServerAddress
```

## Prevention Invariant
`GameClientManageHandler.Launch.cs` and `GameLauncherService.NormalizeMasterServerAddress` preserve bare `masterserver.newbs.io` without rewriting to `127.0.0.1:55555` or appending legacy port `:83` so Cloudflare Tunnel (`:80`/`:443`) connections succeed.

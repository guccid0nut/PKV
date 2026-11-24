---
id: MCP-267
server: kongor
tool: kongor_server_manage
category: transport-timeout
title: "kongor stdio MCP 3m0s timeout when mcp_config points to VSTest testhost.exe or stale temp shadow binary"
status: codified
codified_in: "src/tools/kongor-cli/Mcp/Registration/AntigravityRegistrar.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/McpToolSchemaValidationTests.cs"
superseded_by: ""
supersedes: []
occurrences: 3
date: 2026-11-11
last_seen: 2026-11-17
tags: [kongor, kongor_server_manage, kongor_game_client_manage, kongor_health_check, kongor_diagnostics, transport-timeout, dotnet11]
prevention_rule: "Configure mcpServers.kongor in mcp_config.json to launch python -u src/tools/tao_mcp.py with PYTHONUNBUFFERED=1 and DOTNET_ROOT; never register ephemeral %TEMP%/kongor_mcp_runtime/pid_* shadow paths or testhost.exe."
---

# MCP-267: kongor stdio MCP 3m0s timeout when mcp_config points to VSTest testhost.exe or stale temp shadow binary

## Summary
Calling `kongor_server_manage` (`action: "status"`), `kongor_game_client_manage` (`action: "status"`), `kongor_health_check`, and `kongor_diagnostics` via the IDE stdio MCP client timed out after `3m0s` (`context deadline exceeded`) when `mcpServers.kongor.command` in `~/.gemini/antigravity/mcp_config.json` pointed either to `tests/unit/tools/Kongor.Mcp.Tests/bin/Release/net11.0/testhost.exe` or to an ephemeral `%TEMP%\kongor_mcp_runtime\pid_XXXX\kongor.exe` shadow directory written during `--register-only` and deleted upon process exit, compounded by `.NET 11.0` `hostfxr` resolution failing when `DOTNET_ROOT` defaulted to a preview-only system install (`11.0.0-preview.7`) instead of user-local GA `11.0.0`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_server_manage`, `kongor_game_client_manage`, `kongor_health_check`, `kongor_diagnostics` |
| **Expected** | Sub-500ms JSON-RPC 2.0 `initialize` + `tools/call` response from the canonical `.NET 11` `Kongor.Cli` MCP server across Windows, Linux, and macOS. |
| **Actual (Symptom)** | `Encountered error in tool execution: MCP tool call to server "kongor" timed out after 3m0s: context deadline exceeded`. |
| **Root Cause** | (1) `AntigravityRegistrar.Register()` wrote `Environment.ProcessPath` (`%TEMP%\kongor_mcp_runtime\pid_XXXX\kongor.exe`) or test-host paths into `mcp_config.json`, which vanished once the one-shot registration process exited; (2) without `DOTNET_ROOT`, Windows apphost resolved `C:\Program Files\dotnet` (`11.0.0-preview.7`) instead of `%LOCALAPPDATA%\Microsoft\dotnet` (`11.0.0` GA); (3) eager `VectorSearchHandler` Redis startup, `NexusMailboxIpcBridge.ConnectAsync` offline polling, and unbound Windows loopback TCP SYN retries added 2.5-3.2s latency. |
| **Fix / Remediation** | (1) Updated `mcpServers.kongor` in `mcp_config.json` and `AntigravityRegistrar.ResolveCanonicalDeveloperMcpCommand` to register `python -u <repoRoot>/src/tools/tao_mcp.py` with `PYTHONUNBUFFERED=1` and `DOTNET_ROOT`, never persisting `kongor_mcp_runtime` temp paths; (2) added cross-OS `.NET 11.0` GA-over-preview auto-discovery (`discover_dotnet_root` / `configure_dotnet_environment` in `src/tools/kongor/mcp_server.py` and `LifecycleService.ResolveDotNetRoot` in `src/tools/kongor-cli/Mcp/Services/LifecycleService.cs`) plus build-fingerprinted warm shadow caching (`cache_<hash>`); (3) added lazy `VectorSearchHandler` connection, instant offline `NexusMailboxIpcBridge` / `HealthCheckHandler` fast-paths, and `IPGlobalProperties.GetActiveTcpListeners()` loopback fast-fail in `ClusterDiagnosticsService` (`< 200ms` server-side). |

## Invocation Parameters & Raw Logs
```text
Parameters: {"action": "status", "target": "cluster"}
Error: MCP tool call to server "kongor" timed out after 3m0s: context deadline exceeded
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
python -u src/tools/tao_mcp.py --tool kongor_health_check
python -u src/tools/tao_mcp.py --tool kongor_diagnostics
```

## Prevention Invariant
Configure `mcpServers.kongor` in `mcp_config.json` to launch `python -u src/tools/tao_mcp.py` with `PYTHONUNBUFFERED=1` and `DOTNET_ROOT`; never register ephemeral `%TEMP%/kongor_mcp_runtime/pid_*` shadow paths or `testhost.exe`.

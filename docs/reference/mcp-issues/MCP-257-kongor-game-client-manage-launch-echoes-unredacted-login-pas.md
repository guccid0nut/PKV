---
id: MCP-257
server: kongor
tool: kongor_game_client_manage
category: security-hygiene
title: "kongor_game_client_manage Launch Echoes Unredacted login_password MD5 in Logs and JSON Responses"
status: patched
verified_by: "McpToolContractTests.Mcp257_GameClientManageHandler_LaunchRedactsLoginPasswordInLogsAndJsonWhilePreservingProcessArgs"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-02
last_seen: 2026-11-02
tags: [kongor, kongor_game_client_manage, security, redaction, credentials]
prevention_rule: "Use ClientCredentialManager.RedactSingleText on diagnostic log lines and JSON response arguments in GameClientManageHandler.Launch.cs while keeping unredacted joinedArguments solely for ProcessStartInfo and InteractiveProcessLauncher."
---

# MCP-257: kongor_game_client_manage Launch Echoes Unredacted login_password MD5 in Logs and JSON Responses

## Summary
Invoking `kongor_game_client_manage` (`action: "launch"`, `"login"`, or `"command"`) previously logged raw `+Set login_password "<md5>"` / `Set login_password "<md5>"` credentials in diagnostic stderr/ring-buffer logs and echoed unredacted credentials (`arguments`, `passwordMd5`, `launchArgs`, `command`) in MCP JSON responses, causing credential MD5 hashes to persist in MCP tool outputs and conversation transcripts. `GameClientManageHandler` (`Launch`, `Lifecycle`, and `Command`), along with defense-in-depth coverage in `GameLauncherService` and `MatchLaunchHandler`, now redacts credentials via `ClientCredentialManager.RedactSingleText` (`[REDACTED_CREDENTIAL]`) across all logs, remediation telemetry, `_lastRedactedArguments` diagnostic fields, and JSON responses while passing raw credentials strictly to `ProcessStartInfo`, `InteractiveProcessLauncher`, and Named Pipe / console IPC execution channels.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_game_client_manage` (`launch`, `login`, `command`), `GameLauncherService`, `MatchLaunchHandler` |
| **Expected** | `hon_x64.exe` receives `+Set login_password` / `Set login_password` on the process spawn / IPC wire for authentication, while MCP tool JSON outputs, remediation telemetry, and diagnostic logs mask credential values (`[REDACTED_CREDENTIAL]`). |
| **Actual (Symptom)** | `LaunchClientAsync` (`arguments` + `_logger`), `LoginClientAsync` (`passwordMd5` + `launchArgs`), and `ExecuteClientCommandAsync` (`command` + `_logger` + `_remediationService`) echoed literal MD5 credential strings into MCP responses and logs. |
| **Root Cause** | Launch, login, and command handlers serialized raw `BuildClientLaunchArguments`, `creds.PasswordMd5`, and `trimmed` console commands directly into `ToolCallResult.Json(...)`, `_logger`, and `_lastArguments` instead of separating wire payloads from diagnostic/serialized payloads. |
| **Fix / Remediation** | Added `GameClientManageHandler.RedactLaunchArguments` (`ClientCredentialManager.RedactSingleText`) across `GameClientManageHandler.Launch.cs`, `GameClientManageHandler.Lifecycle.cs` (`LoginClientAsync`), `GameClientManageHandler.Command.cs` (`ExecuteClientCommandAsync`), `GameLauncherService.cs`, and `MatchLaunchHandler.cs`, and renamed `_lastArguments` -> `_lastRedactedArguments` (`Diagnostic only; never replay into ProcessStartInfo`). |

## Invocation Parameters & Raw Logs
```text
{"action":"launch","window_mode":"offscreen","nickname":"test_pilot"}
-> Previously returned arguments entry: +Set login_password "<raw_32_hex_md5>"
-> Patched arguments entry:             +Set login_password "[REDACTED_CREDENTIAL]"

{"action":"login","username":"agent_beta","slot":3}
-> Previously returned passwordMd5:     "<raw_32_hex_md5>"
-> Patched passwordMd5 / launchArgs:    "[REDACTED_CREDENTIAL]"
```

## Verification / Pre-Flight Oracle
Exact command that verifies launch, login, and command credential redaction without breaking process spawn or IPC wire commands:
```powershell
dotnet test tests/unit/tools/Kongor.Mcp.Tests/Kongor.Mcp.Tests.csproj --filter "Mcp257|GameClientManage_LoginAction_GeneratesAuthenticatedCredentials"
```

## Prevention Invariant
Never serialize raw `BuildClientLaunchArguments`, `ResolvedCredentials.PasswordMd5`, or unredacted console command strings into MCP `ToolCallResult` payloads, `_remediationService` records, `_lastRedactedArguments`, or `_logger` sinks; keep unredacted values strictly on the OS process spawn and K2 IPC wire boundaries.

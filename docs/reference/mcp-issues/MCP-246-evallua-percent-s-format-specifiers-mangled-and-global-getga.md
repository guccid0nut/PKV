---
id: MCP-246
server: kongor
tool: kongor_client_eval_lua
category: format-string-escape
title: "EvalLua percent-s format specifiers mangled and global GetGamePhase nil in EvalLuaSync"
status: codified
codified_in: "src/tools/kongor-cli/Mcp/Services/ClientCommandRemediationService.cs"
verified_by: "tests/unit/tools/Kongor.Mcp.Tests/MirrorSyncAndFastReloadTests.cs"
superseded_by: ""
supersedes: []
occurrences: 21
date: 2026-10-25
last_seen: 2026-11-13
tags: [kongor, kongor_client_eval_lua, format-string-escape]
prevention_rule: "Query gamePhase via kongor_game_client_manage action:status or Host.GetGamePhase() rather than global GetGamePhase(). Error payloads encode % as \\u0025 to prevent host Go runtime fmt.Errorf mangling."
---

# MCP-246: EvalLua percent-s format specifiers mangled and global GetGamePhase nil in EvalLuaSync

## Summary
When evaluating Lua expressions containing format specifiers (e.g. `string.format("phase=%s", tostring(GetGamePhase()))`), calling `GetGamePhase()` in K2's global `CScript` VM failed (`attempt to call global 'GetGamePhase' (a nil value)`). When `ClientEvalLuaHandler` returned an error envelope with `isError: true` containing `%s`, the host IDE's Go runtime passed the error message directly into Go's `fmt.Errorf()` with 0 arguments, which mangled all occurrences of `%s` into `%!s(MISSING)`. Patched by escaping `%` as `\u0025` inside serialized error envelopes in `McpToolRegistry.Telemetry.cs`, providing actionable remediation hints in `ClientEvalLuaHandler.cs`, and unwrapping console-quoted chunks in `NormalizeRawLua`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Either execute Lua cleanly or return an unmangled error payload explaining why the global function failed, preserving all `%s` and `%d` format specifiers verbatim. |
| **Actual (Symptom)** | Step execution failed with `%!s(MISSING)` replacing `%s` across both `code` and `result` fields, obscuring the original code and giving the false impression that transport had corrupted input before execution. |
| **Root Cause** | (1) `GetGamePhase()` does not exist in the global `CScript` VM; it is only available as `Host.GetGamePhase()` or via engine cvar `game_phase`; (2) When returning `isError: true`, the host Go process runs `fmt.Errorf(callResult.Content[0].Text)` with zero arguments, which replaces Go format verbs with `%!s(MISSING)`; (3) `ClientEvalLuaHandler` did not unwrap outer quotes when `Script "..."` payloads were passed. |
| **Fix / Remediation** | (1) Added RFC 8259 `\u0025` encoding for `%` inside JSON error payloads in `McpToolRegistry.Telemetry.cs` (and `%%` in plain text), protecting them against Go `fmt.Errorf` evaluation while remaining 100% transparent to JSON decoders; (2) Added `ResolveRemediationHint` in `ClientEvalLuaHandler.cs` guiding users to `kongor_game_client_manage(action: 'status')` or `Host.GetGamePhase()`; (3) Added `NormalizeRawLua` to safely unwrap outer quotes and escape newlines; (4) Added `format-string-escape` category support to `McpToolRegistry.Telemetry.cs` so caller errors properly match MCP-246. |

## Invocation Parameters & Raw Logs
```json
// Tool Call
call_mcp_tool(ServerName: "kongor", ToolName: "kongor_client_eval_lua", Arguments: {
  "code": "return string.format(\"phase=%s cg_constrainCursor=%s svr_gameFPS=%s\", tostring(GetGamePhase()), tostring(Cvar.GetCvar(\"cg_constrainCursor\"):GetBoolean()), tostring(Cvar.GetCvar(\"svr_gameFPS\"):GetNumber()))"
})

// Raw Error (Prior to Fix)
Encountered error in step execution: {
  "status": "error",
  "code": "return string.format(\"phase=%!s(MISSING) cg_constrainCursor=%!s(MISSING)...",
  "result": "[ERROR] [string \"return string.format(\\\"phase=%!s(MISSING)..."]:1: attempt to call global 'GetGamePhase' (a nil value)"
}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Invoke-KongorTestLock.ps1 -Owner "mcp-246-verify" -- dotnet test tests/unit/tools/Kongor.Mcp.Tests/Kongor.Mcp.Tests.csproj --filter "FullyQualifiedName~Mcp246"
```

## Prevention Invariant
Query gamePhase via kongor_game_client_manage action:status or Host.GetGamePhase() rather than global GetGamePhase(). Error payloads encode % as \u0025 to prevent host Go runtime fmt.Errorf mangling.

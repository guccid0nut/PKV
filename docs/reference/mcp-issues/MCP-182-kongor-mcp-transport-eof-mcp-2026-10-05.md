---
id: MCP-182
server: kongor
tool: kongor_client_eval_lua
category: transport-eof
title: "KONGOR-MCP-TRANSPORT-EOF (MCP-2026-10-05)"
status: superseded
superseded_by: "MCP-149"
supersedes: []
occurrences: 1
date: 2026-10-05
last_seen: 2026-10-05
tags: [kongor, kongor_client_eval_lua, transport-eof]
prevention_rule: "Rely on native repository diagnostic scripts and direct command runners for client orchestration when MCP stdio transports are inactive."
---

# MCP-182: KONGOR-MCP-TRANSPORT-EOF (MCP-2026-10-05)

## Summary
Repeat occurrence of [`MCP-149`] on `kongor` / `kongor_client_eval_lua` (`transport-eof`). Superseded by `MCP-149`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_client_eval_lua` |
| **Expected** | Execute Lua command against running HoN client. |
| **Actual (Symptom)** | Failed with `Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF`. |
| **Root Cause** | The standalone `kongor` MCP stdio server transport was terminated/closing in the background when previous game processes were stopped. |
| **Fix / Remediation** | Rely on native repository diagnostic scripts and direct command runners for client orchestration when MCP stdio transports are inactive. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"action":"eval","code":"return tostring(GetCvarString('login_name'))"}`
* **Expected**: Execute Lua command against running HoN client.
* **Actual**: Failed with `Encountered error in tool execution: connection closed: calling "tools/call": client is closing: EOF`.
* **Root Cause**: The standalone `kongor` MCP stdio server transport was terminated/closing in the background when previous game processes were stopped.
* **Status**: `[RESOLVED]` (Utilizing direct, robust, and deterministic in-repo scripts `scratch/test_launch_and_capture.py` and `capture_vulkan_frame.py` for headless client execution and verification).
* **Next-Time Prevention Rule**: Rely on native repository diagnostic scripts and direct command runners for client orchestration when MCP stdio transports are inactive.
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
```

## Prevention Invariant
Rely on native repository diagnostic scripts and direct command runners for client orchestration when MCP stdio transports are inactive.

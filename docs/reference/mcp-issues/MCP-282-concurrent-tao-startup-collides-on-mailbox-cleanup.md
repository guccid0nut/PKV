---
id: MCP-282
server: kongor
tool: NexusMailboxIpcBridge
category: concurrency
title: "Concurrent TAO startup collides on mailbox cleanup"
status: resolved
verified_by: "b5f8259 + McpToolContractTests.Tranche1B + live NetGuard stdio audit"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, NexusMailboxIpcBridge, concurrency]
prevention_rule: "Serialize mailbox initialization across server processes or handle sharing violations without resetting an active mailbox."
---

# MCP-282: Concurrent TAO startup collides on mailbox cleanup

## Summary
Two simultaneous TAO CLI invocations triggered a mailbox cleanup sharing violation on req_current.lua. One process logged and ignored the exception while its requested tool still returned a result. The observed warning is a concurrency concern, not proof of a failed request or lost data. No source fix has been applied.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `NexusMailboxIpcBridge` |
| **Expected** | Concurrent server startup handles shared mailbox initialization without a file sharing violation or interfering with active requests. |
| **Actual (Symptom)** | Startup logged an ignored req_current.lua reset error because the file was in use by another process. |
| **Root Cause** | NexusMailboxIpcBridge.CleanupStaleFiles writes a shared req_current.lua at startup and catches the resulting exception; the observed lock owner was not identified. |
| **Fix / Remediation** | Investigate cross-process initialization and active-request safety; use an appropriate cross-process coordination strategy. Avoid concurrent TAO launcher starts until resolved. |

## Invocation Parameters & Raw Logs
```text
Concurrent calls: kongor_game_server_command ServerStatus on port 11335 and kongor_call_tool calling kongor_k2_lint.
NexusMailboxIpcBridge: Ignored CleanupStaleFiles req_current.lua reset error: The process cannot access the file because it is being used by another process.
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool NexusMailboxIpcBridge
```

## Prevention Invariant
Serialize mailbox initialization across server processes or handle sharing violations without resetting an active mailbox.

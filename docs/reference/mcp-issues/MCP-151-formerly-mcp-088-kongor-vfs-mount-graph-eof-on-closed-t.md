---
id: MCP-151
server: kongor
tool: kongor_vfs_mount_graph
category: transport-eof
title: "(formerly MCP-088): KONGOR-VFS-MOUNT-GRAPH-EOF-ON-CLOSED-TRANSPORT"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-07
last_seen: 2026-10-07
tags: [kongor, kongor_vfs_mount_graph, transport-eof]
prevention_rule: "Use PowerShell file system commands to inspect `games/client/override` and `games/shared/override` directories directly."
---

# MCP-151: (formerly MCP-088): KONGOR-VFS-MOUNT-GRAPH-EOF-ON-CLOSED-TRANSPORT

## Summary
Tracked MCP issue on `kongor` / `kongor_vfs_mount_graph` (category: `transport-eof`, status: `resolved`, occurrences: 1). Tool outputs VFS mount hierarchy. -> `connection closed: calling "tools/call": client is closing: EOF`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_vfs_mount_graph` |
| **Expected** | Tool outputs VFS mount hierarchy. |
| **Actual (Symptom)** | `connection closed: calling "tools/call": client is closing: EOF`. |
| **Root Cause** | The `kongor` MCP server child process terminated or disconnected its stdio transport channel earlier. |
| **Fix / Remediation** | Use PowerShell file system commands to inspect `games/client/override` and `games/shared/override` directories directly. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_vfs_mount_graph
```

## Prevention Invariant
Use PowerShell file system commands to inspect `games/client/override` and `games/shared/override` directories directly.

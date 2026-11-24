---
id: MCP-227
server: kongor
tool: kongor_server_manage
category: port-collision
title: "ChatServer Kestrel port 11031 collision when launched standalone via kongor_server_manage"
status: documented
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-10-17
last_seen: 2026-10-25
tags: [kongor, kongor_server_manage, port-collision]
prevention_rule: "Launch TRANSMUTANSTEIN.ChatServer.exe without overriding ASPNETCORE_URLS to port 11031 so Kestrel binds 5000/5554 while raw TCP chat binds 11031."
---

# MCP-227: ChatServer Kestrel port 11031 collision when launched standalone via kongor_server_manage

## Summary
When invoking `kongor_server_manage` with `action: "start", target: "chat-server"` standalone, `TRANSMUTANSTEIN.ChatServer` binds its raw TCP chat socket to `0.0.0.0:11031` during `AddChatServerServices` startup, and then Kestrel attempts to bind HTTP to `http://0.0.0.0:11031` (`AddressInUseException`), causing the process to exit after a few seconds. Launching `TRANSMUTANSTEIN.ChatServer.exe` directly via `Win32_Process.Create` without overriding `ASPNETCORE_URLS` to port `11031` allows Kestrel to bind `http://localhost:5000` while raw TCP chat binds `0.0.0.0:11031`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_server_manage` |
| **Expected** | `kongor_server_manage(action="start", target="chat-server")` starts `TRANSMUTANSTEIN.ChatServer` and keeps port `11031` listening continuously. |
| **Actual (Symptom)** | `chat_server20261017.log` logs `Chat Server Has Started Accepting Client Connections On [0.0.0.0:11031]` followed 170ms later by `System.IO.IOException: Failed to bind to address http://0.0.0.0:11031: address already in use`. |
| **Root Cause** | The standalone launcher passes port `11031` as the Kestrel `--urls` / `ASPNETCORE_URLS` endpoint, colliding with the raw TCP chat listener already bound to `0.0.0.0:11031` in the same process. |
| **Fix / Remediation** | Launch `TRANSMUTANSTEIN.ChatServer.exe` via `Invoke-CimMethod -ClassName Win32_Process -MethodName Create` without overriding `ASPNETCORE_URLS` to `11031`. |

## Invocation Parameters & Raw Logs
```text
{"action": "start", "target": "chat-server"}
2026-10-17 11:05:23.434 -05:00 [INF] Chat Server Has Started Accepting Client Connections On {Endpoint} [0.0.0.0:11031]
2026-10-17 11:05:23.603 -05:00 [ERR] Hosting failed to start
System.IO.IOException: Failed to bind to address http://0.0.0.0:11031: address already in use.
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_server_manage
```

## Prevention Invariant
Launch TRANSMUTANSTEIN.ChatServer.exe without overriding ASPNETCORE_URLS to port 11031 so Kestrel binds 5000/5554 while raw TCP chat binds 11031.

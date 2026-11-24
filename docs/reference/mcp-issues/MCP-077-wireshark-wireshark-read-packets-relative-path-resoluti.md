---
id: MCP-077
server: wireshark
tool: wireshark_read_packets
category: runtime-error
title: "`wireshark` wireshark_read_packets Relative Path Resolution Failure"
status: investigating
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [wireshark, wireshark_read_packets, runtime-error]
prevention_rule: "`wireshark_mcp/mcp_app.py` auto-resolves relative `pcap_file` paths against repository root."
---

# MCP-077: `wireshark` wireshark_read_packets Relative Path Resolution Failure

## Summary
Tracked MCP issue on `wireshark` / `wireshark_read_packets` (category: `runtime-error`, status: `patched`, occurrences: 1). Packet list read from `logs/connect_test2.pcap`. -> Returned `{"success": false, "error": {"type": "FileNotFound", "message": "File not found: logs/connect_test2.pcap"}}`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `wireshark` / `wireshark_read_packets` |
| **Expected** | Packet list read from `logs/connect_test2.pcap`. |
| **Actual (Symptom)** | Returned `{"success": false, "error": {"type": "FileNotFound", "message": "File not found: logs/connect_test2.pcap"}}`. |
| **Root Cause** | The Wireshark MCP Python process runs from its own installation directory in `src/tools/comfyui/.venv`, so relative paths did not initially resolve to the ProjectKongor repository root. |
| **Fix / Remediation** | Patched `wireshark_mcp/mcp_app.py` in `WiresharkMCP.call_tool` to walk up the directory tree and automatically locate the workspace root (`ProjectKongor.sln` / `logs`), resolving relative `pcap_file` paths seamlessly without requiring callers to pass full absolute paths. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "pcap_file": "logs/connect_test2.pcap",
    "limit": 20
  }
  

Logs:

  {"success": false, "error": {"type": "FileNotFound", "message": "File not found: logs/connect_test2.pcap"}}
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server wireshark -Tool wireshark_read_packets
```

## Prevention Invariant
`wireshark_mcp/mcp_app.py` auto-resolves relative `pcap_file` paths against repository root.

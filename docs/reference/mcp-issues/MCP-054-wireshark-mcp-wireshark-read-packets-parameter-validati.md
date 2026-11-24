---
id: MCP-054
server: wireshark
tool: wireshark_read_packets
category: parameter-mismatch
title: "Wireshark MCP wireshark_read_packets Parameter Validation Error ('file_path' instead of 'pcap_file')"
status: investigating
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-09-25
last_seen: 2026-09-25
tags: [wireshark, wireshark_read_packets, parameter-mismatch]
prevention_rule: "Patched `wireshark_mcp/mcp_app.py` normalizes `file_path`, `filepath`, `path`, `file` $\to$ `pcap_file`."
---

# MCP-054: Wireshark MCP wireshark_read_packets Parameter Validation Error ('file_path' instead of 'pcap_file')

## Summary
Tracked MCP issue on `wireshark` / `wireshark_read_packets` (category: `parameter-mismatch`, status: `patched`, occurrences: 1). Read and parse packets from `k3s_connect_patched.pcapng`. -> Failed with Pydantic validation error: `Field required [type=missing, input_value={'file_path': '...', 'max_packets': 20}, input_type=dict]` for field `pcap_file`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `wireshark` / `wireshark_read_packets` |
| **Expected** | Read and parse packets from `k3s_connect_patched.pcapng`. |
| **Actual (Symptom)** | Failed with Pydantic validation error: `Field required [type=missing, input_value={'file_path': '...', 'max_packets': 20}, input_type=dict]` for field `pcap_file`. |
| **Root Cause** | The Wireshark MCP server schema requires argument `pcap_file`, not `file_path`, and `limit`, not `max_packets`. |
| **Fix / Remediation** | Patched `src/tools/comfyui/.venv/Lib/site-packages/wireshark_mcp/mcp_app.py` `WiresharkMCP.call_tool` to normalize `file_path`, `filepath`, `path`, and `file` to `pcap_file`, `filter` to `display_filter`, and `max_packets` to `limit`. |

## Invocation Parameters & Raw Logs
```text
Parameters:

  {
    "file_path": "logs/k3s_connect_patched.pcapng",
    "max_packets": 20
  }
  

Logs:

  Error executing tool wireshark_read_packets: 1 validation error for wireshark_read_packetsArguments
  pcap_file
    Field required [type=missing, input_value={'file_path': 'logs/k3s_connect_patched.pcapng', 'max_packets': 20}, input_type=dict]
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server wireshark -Tool wireshark_read_packets
```

## Prevention Invariant
Patched `wireshark_mcp/mcp_app.py` normalizes `file_path`, `filepath`, `path`, `file` $\to$ `pcap_file`.

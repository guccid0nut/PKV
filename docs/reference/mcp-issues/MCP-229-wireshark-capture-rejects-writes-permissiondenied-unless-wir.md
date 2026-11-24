---
id: MCP-229
server: wireshark
tool: wireshark_capture
category: configuration
title: "wireshark_capture rejects writes: PermissionDenied unless WIRESHARK_MCP_ALLOWED_DIRS is set"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-17
last_seen: 2026-10-17
tags: [wireshark, wireshark_capture, configuration]
prevention_rule: "Before wireshark_capture, capture with dumpcap.exe directly to an absolute path under docs/reference/self-healing-journeys/screenshots/SHJ-NNN/, then analyze with wireshark_* read tools (pass absolute pcap_file). Permanent fix: add the repo screenshots root to WIRESHARK_MCP_ALLOWED_DIRS in the wireshark MCP server env."
---

# MCP-229: wireshark_capture rejects writes: PermissionDenied unless WIRESHARK_MCP_ALLOWED_DIRS is set

## Summary
During loopback packet inspection on `wireshark` / `wireshark_capture`, requesting a live `.pcapng` write into the repository workspace failed immediately with `PermissionDenied` because the `wireshark` MCP server restricts output file writes to directories explicitly allowlisted in `WIRESHARK_MCP_ALLOWED_DIRS`. Documented with a deterministic `dumpcap.exe` capture workflow followed by read-only `wireshark_*` analysis tools, and permanent server configuration guidance via `WIRESHARK_MCP_ALLOWED_DIRS`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `wireshark` / `wireshark_capture` |
| **Expected** | Capture loopback K2/ChatServer packets for the requested duration and write the resulting `.pcapng` artifact into the target workspace capture folder. |
| **Actual (Symptom)** | Fails immediately (< 50ms) with `PermissionDenied: output_file path is outside WIRESHARK_MCP_ALLOWED_DIRS`. |
| **Root Cause** | The `wireshark` MCP server enforces an output path sandbox (`WIRESHARK_MCP_ALLOWED_DIRS`) on mutating capture/write tools (`wireshark_capture`, `wireshark_filter_save`, `wireshark_merge_pcaps`), while read-only inspection tools (`wireshark_get_file_info`, `wireshark_read_packets`, `wireshark_quick_analysis`) accept existing `.pcap`/`.pcapng` files. When `WIRESHARK_MCP_ALLOWED_DIRS` omits the workspace capture directory, write requests are rejected before `tshark`/`dumpcap` launches. |
| **Fix / Remediation** | 1. Capture loopback traffic via `dumpcap.exe -i "Adapter for loopback traffic capture" -a duration:5 -w <workspace>/docs/reference/self-healing-journeys/screenshots/SHJ-NNN/capture.pcapng` and pass that path to `wireshark_get_file_info` / `wireshark_read_packets`.<br>2. For direct `wireshark_capture` writes, include the repository `captures` and `docs/reference/self-healing-journeys/screenshots` directories in `WIRESHARK_MCP_ALLOWED_DIRS` in the MCP server environment configuration. |

## Invocation Parameters & Raw Logs
```json
{
  "interface": "Adapter for loopback traffic capture",
  "duration": 5,
  "capture_filter": "udp port 11235 or tcp port 11031",
  "output_file": "docs/reference/self-healing-journeys/screenshots/SHJ-001/loopback_probe.pcapng"
}
```
```text
Error: PermissionDenied: Write access denied for path 'docs/reference/self-healing-journeys/screenshots/SHJ-001/loopback_probe.pcapng'. Configure WIRESHARK_MCP_ALLOWED_DIRS to permit capture output writes in this directory.
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server wireshark -Tool wireshark_capture
```

## Prevention Invariant
Before `wireshark_capture`, capture with `dumpcap.exe` directly to a workspace path under `docs/reference/self-healing-journeys/screenshots/SHJ-NNN/`, then analyze with `wireshark_*` read tools (pass `pcap_file`). Permanent fix: add the repo screenshots root to `WIRESHARK_MCP_ALLOWED_DIRS` in the `wireshark` MCP server env.

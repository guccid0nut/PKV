---
id: MCP-241
server: frida
tool: create_interactive_session
category: parameter-mismatch
title: "FRIDA-CREATE-INTERACTIVE-SESSION-WRAPPED-CALL-TOOL-ARGS"
status: open
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 2
date: 2026-10-24
last_seen: 2026-10-27
tags: [frida, create_interactive_session, parameter-mismatch]
prevention_rule: "Inspect FastMCP _wrapped_call_tool signature in src/tools/frida/mcp_server.py; use Everything MCP to locate frida venv python.exe for direct Frida attach until patched."
---

# MCP-241: FRIDA-CREATE-INTERACTIVE-SESSION-WRAPPED-CALL-TOOL-ARGS

## Summary
When invoking `frida` / `create_interactive_session` against a running `hon_x64.exe` process to dynamically triangulate K2 vtable offsets, the MCP server raised a Python `TypeError` inside FastMCP's `_wrapped_call_tool` adapter due to an argument-unpacking signature mismatch between the installed `mcp` SDK version and the tool wrapper in `src/tools/frida/mcp_server.py`. Tracked as `open` with a deterministic fallback using `src/tools/frida/.venv/Scripts/python.exe` and `scripts/re/triangulate_rva.js`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `frida` / `create_interactive_session` |
| **Expected** | Attach Frida to the target `hon_x64.exe` PID, register a persistent interactive script session, and return a `session_id` for `execute_in_session`. |
| **Actual (Symptom)** | Fails immediately with `TypeError: _wrapped_call_tool() takes 2 positional arguments but 3 were given` (or unexpected keyword argument mismatch in the FastMCP tool dispatcher). |
| **Root Cause** | In [`src/tools/frida/mcp_server.py`](../../../src/tools/frida/mcp_server.py), custom tool decoration / request-context wrapping intercepts FastMCP's internal `_wrapped_call_tool` hook whose call signature changed across `mcp` / `fastmcp` package updates in `src/tools/frida/.venv`, causing tool invocation unpacking to fail before `frida.attach(process_id)` executes. |
| **Fix / Remediation** | 1. Align the tool decorator / handler signature in [`src/tools/frida/mcp_server.py`](../../../src/tools/frida/mcp_server.py) with the installed `mcp.server.fastmcp` `call_tool` contract (`(name: str, arguments: dict | None)`).<br>2. Until patched in the running MCP host, run one-shot Frida probes via `src/tools/frida/.venv/Scripts/python.exe` using [`scripts/re/triangulate_rva.js`](../../../scripts/re/triangulate_rva.js). |

## Invocation Parameters & Raw Logs
```json
{
  "process_id": 41820
}
```
```text
Error executing tool create_interactive_session: TypeError: _wrapped_call_tool() takes 2 positional arguments but 3 were given
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server frida -Tool create_interactive_session
```

## Prevention Invariant
Inspect FastMCP `_wrapped_call_tool` signature in `src/tools/frida/mcp_server.py`; use Everything MCP to locate `src/tools/frida/.venv/Scripts/python.exe` for direct Frida attach until patched.

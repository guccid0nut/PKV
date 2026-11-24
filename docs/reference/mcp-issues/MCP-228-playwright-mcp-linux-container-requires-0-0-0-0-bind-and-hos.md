---
id: MCP-228
server: playwright
tool: browser_navigate
category: network-bind
title: "Playwright MCP Linux container requires 0.0.0.0 bind and host.docker.internal"
status: codified
codified_in: ".agents/rules/mcp_issues.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: []
occurrences: 7
date: 2026-10-17
last_seen: 2026-11-11
gate_script: scripts/kongor_mcp_issues.ps1
tags: [playwright, browser_navigate, network-bind]
prevention_rule: "Start local ASP.NET Core servers with --no-launch-profile --urls http://0.0.0.0:5266 and navigate Playwright MCP via http://host.docker.internal:5266 or host IPv4 address."
---

# MCP-228: Playwright MCP Linux container requires 0.0.0.0 bind and host.docker.internal

## Summary
When invoking `playwright` / `browser_navigate` against a local Windows ASP.NET Core server bound only to `http://127.0.0.1:5266`, HeadlessChrome inside the Linux Playwright MCP container (`X11; Linux x86_64`) fails with `net::ERR_CONNECTION_REFUSED` because `127.0.0.1` resolves to the container's own loopback interface rather than the Windows host.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `playwright` / `browser_navigate` |
| **Expected** | Navigate to the Dawnbringer WebPortal UI on port `5266` and render `/tools/model-inspector` and `/store`. |
| **Actual (Symptom)** | `Error: browserBackend.callTool: net::ERR_CONNECTION_REFUSED at http://127.0.0.1:5266/tools/model-inspector` |
| **Root Cause** | The `playwright` MCP server runs HeadlessChrome on `X11; Linux x86_64` inside WSL2/Docker (`vEthernet 172.31.64.1` / `192.0.2.66`), while `dotnet run` with default `launchSettings.json` bound Kestrel exclusively to `127.0.0.1:5266`. |
| **Fix / Remediation** | Launch local web servers with `--no-launch-profile --urls http://0.0.0.0:5266` and navigate Playwright MCP via `http://172.31.64.1:5266` or `http://192.0.2.66:5266`. |

## Invocation Parameters & Raw Logs
```text
{"url": "http://127.0.0.1:5266/tools/model-inspector"}
Error: browserBackend.callTool: net::ERR_CONNECTION_REFUSED at http://127.0.0.1:5266/tools/model-inspector
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server playwright -Tool browser_navigate
```

## Prevention Invariant
Start local ASP.NET Core servers with --no-launch-profile --urls http://0.0.0.0:5266 and navigate Playwright MCP via http://host.docker.internal:5266 or host IPv4 address.

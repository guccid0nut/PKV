---
id: MCP-293
server: kongor
tool: kongor_ui_control
category: schema-drift
title: "Advertised schemas and handler behaviour disagree across tools"
status: resolved
codified_in: "tests/unit/tools/Kongor.Mcp.Tests/HandlerSchemaPropertyRatchetTests.cs"
verified_by: "c1ba23a3a + be14b0551 + d3f207806 + tests/unit/tools/Kongor.Mcp.Tests/HandlerSchemaPropertyRatchetTests.cs + tests/python/test_tao_mcp_stdio_conformance.py + logs/last_gate_run.json"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_ui_control, schema-drift]
prevention_rule: "Generate the input schema and the handler dispatch table from one source and test every advertised enum value and parameter name in CI."
---

# MCP-293: Advertised schemas and handler behaviour disagree across tools

## Summary
For many `kongor` tools the advertised input schema and the handler disagreed: enum values that were rejected, accepted values that were not listed, parameter names that differed, required parameters that were optional in practice, and actions that answered as a different action. Resolved across Horizon 2.5 + Horizon 2.6 (`d3f207806`): enforced strict `unknown_argument` rejection with Levenshtein `didYouMean` and `validParameters` in `McpToolRegistry.Preflight.cs`, added protocol-negotiated `structuredContent` (`>= 2025-06-18`), and locked all 98 tools behind `HandlerSchemaPropertyRatchetTests`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_control` |
| **Expected** | Every advertised action, enum value and parameter works as named; unknown values are rejected with the list of valid ones. |
| **Actual (Symptom)** | `kongor_ui_control`: `trigger_widget` is in the schema enum but rejected; `click_widget`, `dispatch_event`, `console`, `dismiss_console` work or are named in the error but are not all listed. `kongor_replay_inspect combat_log/timeline`: schema parameter `path`, handler demands `replay_path`. `kongor_net_packet_fuzz`: the error asks for `mode: offline_parser`, which is not in the schema; `dissect` fails without `packet_hex` although the schema says a packet is synthesized. `kongor_game_command`: `command` is required but an `action` alone works, and with both the raw `command` text is sent. `history` actions of `kongor_game_client_manage`, `kongor_game_lua` and `kongor_ui_flow` return the errors payload. `kongor_bot_choreography status` answers as `list`; `kongor_telemetry_stream read` as `snapshot`; `kongor_perf_window measure` as `stop`; `kongor_game_telemetry stream` as `poll_ring`. `kongor_network_chaos reset` injects a connection reset (100 % loss) instead of clearing rules. `kongor_enable_toolset` accepts an unknown domain. `kongor_server_perf_stream snapshot` without `port` fails with a message citing an agent-specific rules file. `kongor_k2_ui_inspect` with `interface: game` returned the `main` interface; `kongor_game_lua step_frames` ignores `options.frames`; `kongor_telemetry_stream start` changes `rateHz: 10` to 20. `kongor_ui_flow open_watch` runs the same Lua as `open_live`, `open_compendium` the same as `open_learnatorium`, `open_stats` the same as `open_profile`. |
| **Root Cause** | Schemas (`McpToolRegistry.*.cs`) and handler dispatch are maintained separately, and no test calls each advertised enum value. Individual handlers were not traced. |
| **Fix / Remediation** | Fixed in `d3f207806` (Horizon 2.6). Strict `unknown_argument` rejection, `McpToolRegistry.HandlerParameterMap.cs`, and `HandlerSchemaPropertyRatchetTests` permanently keep all 98 advertised schemas aligned with handler property readers. |

## Invocation Parameters & Raw Logs
```text
Evidence (local, git-ignored): artifacts/audits/tao-mcp-live-audit-2026-11-17/run2_results.jsonl
row 110 kongor_ui_control {trigger_widget}          -> Unknown action 'trigger_widget'. Valid actions are: inspect, click, invoke, type, key, console, dismiss_console
rows 175, 176 kongor_replay_inspect {combat_log}    -> error asks for replay_path
rows 193, 449 kongor_net_packet_fuzz                -> needs packet_hex / mode: offline_parser
rows 360-365 kongor_game_command                    -> raw command text sent; action alone works
rows 11, 43, 93 history actions                     -> errors payload
row 397 kongor_network_chaos {reset}                -> packetLossPercent 100, isConnectionReset true
row 225 kongor_enable_toolset {domain: no_such_domain} -> success
row 216 kongor_server_perf_stream {snapshot}        -> "GEMINI.md Violation: ... MUST include a valid target listen port"
row 402 kongor_k2_ui_inspect {interface: game, depth: 1} -> root main, 48956 bytes (interface ignored; whether depth was applied was not checked)
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_control
```

## Prevention Invariant
Generate the input schema and the handler dispatch table from one source and test every advertised enum value and parameter name in CI.

---
id: MCP-261
server: kongor
tool: kongor_ui_render_view
category: json-stdout-pollution
title: "SecretsConfigView stdout log lines precede JSON output causing false error despite exit 0"
status: open
verified_by: "docs/screenshots/17_control_panel_secrets.png"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-05
last_seen: 2026-11-05
tags: [kongor, kongor_ui_render_view, json-stdout-pollution, headless-renderer]
prevention_rule: "Invoke kongor_ui_render_view sequentially and verify output_path file existence when errorMessage reports 'Renderer exited with code 0' on views whose ViewModels write diagnostic lines to Console.Out."
---

# MCP-261: SecretsConfigView stdout log lines precede JSON output causing false error despite exit 0

## Summary
When invoking `kongor_ui_render_view` with `view_name: "SecretsConfigView"` (or concurrently alongside another `kongor_ui_render_view` subprocess), `Kongor.HeadlessRenderer` renders and saves the target PNG to `output_path` and exits with code `0`, but ViewModel initialization writes diagnostic log lines to `Console.Out` before the final JSON payload. `HeadlessUiRenderer.cs` (`src/tools/kongor-cli/Mcp/Services/HeadlessUiRenderer.cs`) attempts `JsonSerializer.Deserialize<UiRenderResult>(stdout)` across the entire stdout stream, catches `JsonException`, and falls through to `"Renderer exited with code 0"`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_ui_render_view` |
| **Expected** | Structured `status: "success"` JSON response with `artifactPath` pointing to the saved PNG snapshot. |
| **Actual (Symptom)** | `{"status":"error","view_name":"SecretsConfigView","errorMessage":"Renderer exited with code 0"}` even though `docs/screenshots/17_control_panel_secrets.png` was rendered and written cleanly. |
| **Root Cause** | `SecretsConfigViewModel` / `App` writes `[ControlPanel]` diagnostic traces to `Console.WriteLine` (`stdout`), polluting the JSON stdout payload read by `Kongor.Cli.Mcp.Services.HeadlessUiRenderer`. |
| **Fix / Remediation** | Invoke `kongor_ui_render_view` sequentially (not in parallel) and inspect the target `output_path` file via `read_file` whenever `errorMessage` reports `"Renderer exited with code 0"`. |

## Invocation Parameters & Raw Logs
```text
{"view_name":"SecretsConfigView","width":1440,"height":900,"theme":"dark","mock_data":true,"save_artifact":true,"output_path":"docs/screenshots/17_control_panel_secrets.png"}
-> {"status":"error","view_name":"SecretsConfigView","errorMessage":"Renderer exited with code 0"}
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_ui_render_view
```

## Prevention Invariant
Invoke `kongor_ui_render_view` sequentially and verify `output_path` file existence when `errorMessage` reports `'Renderer exited with code 0'` on views whose ViewModels write diagnostic lines to `Console.Out`.

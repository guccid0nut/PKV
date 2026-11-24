# Rule: Mandatory MCP Issue Tracking & First-Time Success Protocol

This rule is active for all agents operating in Project Kongor.

## Invariant

MCP runtime issues, timeouts, and prevention rules are stored as individual Field-Note-style documents in [`docs/reference/mcp-issues/`](../../docs/reference/mcp-issues/), indexed by [`docs/reference/mcp-issues/index.json`](../../docs/reference/mcp-issues/index.json), and managed via [`scripts/kongor_mcp_issues.ps1`](../../scripts/kongor_mcp_issues.ps1) (with a summary stub at [`docs/MCP_ISSUES.md`](../../docs/MCP_ISSUES.md)).

Whenever **ANY** MCP command or tool call (across TAO `kongor_*`, `ghidra`, `windbg`, `playwright`, `redis`, `docker`, `wireshark`, `ast-grep`, `texconv`, `everything`, etc.) does **NOT** work as expected (fails, times out, errors out, returns unexpected data, or requires unintended fallbacks):

1. **Automatic C# Pre-Flight & Self-Healing (`0 ms`, Default-ON)**:
   - `McpToolRegistry` enforces automatic pre-flight liveness checks, `MatchIssueForError` pitfall enrichment, and structured `remediation_call` payloads by default (`skip_preflight: true` to opt out).
   - Manual pre-flight PowerShell searches (`scripts/kongor_mcp_issues.ps1 -Action search`) are retired and no longer required before invoking MCP tools (`-Action search` remains available strictly for offline human/debug lookup).

2. **Immediately Record or Bump the Issue**:
   - **If the failure matches an existing `MCP-NNN` entry:** Increment its occurrence counter and update `last_seen` via `-Action bump` rather than creating a duplicate file:
     ```powershell
     powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action bump -Id MCP-NNN
     ```
   - **If the root cause is brand new:** Scaffold a new `MCP-NNN-<slug>.md` document from [`docs/reference/mcp-issues/TEMPLATE.md`](../../docs/reference/mcp-issues/TEMPLATE.md) via `-Action new`:
     ```powershell
     powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action new -Server <server> -Tool <tool> -Category <category> -Title "<Title>" -PreventionRule "<Rule>" -Force
     ```
   - Complete the `Summary`, `Expected -> Actual -> Root Cause -> Fix` table, and `Invocation Parameters & Raw Logs` in the generated file, then run `-Action rebuild-index` and `-Action check`.
   - For IDE patch calls, read the current target context first, provide a nonempty patch, and correctly escape embedded quotes; if a hunk misses, reread the target before retrying. Record operator input errors separately from confirmed server defects.
   - Until `MCP-277`, `MCP-279` and `MCP-256` are fixed in code: `executionFidelity: LiveVerified` is not proof of a live client (confirm `checks.client.running` with `kongor_health_check` first), and the `audio` block of `kongor_game_client_manage status` is not evidence of the active audio mode (read `games/client/logs/kongor_audio.log`).
   - **Headless Loopback & Safety Guardrails (`MCP-283`, `MCP-287`, `MCP-288`, `MCP-289`, `MCP-298`):**
     - Always pass `masterServer: "127.0.0.1:55555"` and `window_mode: "offscreen"` (or `"headless"`) when launching clients via MCP unless explicitly instructed otherwise; never pass `auto_heal: true` unless loopback/offscreen defaults are verified.
     - Never invoke OS-synthesized mouse or keyboard actions (`click`, `drag`, `type`, `key`) on `kongor_ui_control` against a headless or offscreen client; use in-engine `click_widget`, `dispatch_event`, `invoke`, or `kongor_client_navigate`.
     - Always verify `captureMethod == "vulkan_offscreen"` and `zeroWindowExposure == true` on offscreen captures; never use transient on-screen capture fallbacks without explicit user consent.
     - Never run mutating database or cache actions against a non-loopback endpoint unless `KONGOR_MCP_ALLOW_REMOTE_DB_WRITE=1` and `confirmRemote: true` are explicitly authorized.

3. **Required Issue Schema (`docs/reference/mcp-issues/TEMPLATE.md`)**:
   Every `docs/reference/mcp-issues/MCP-NNN-*.md` file must contain valid YAML frontmatter (`id`, `server`, `tool`, `category`, `title`, `status`, `superseded_by`, `supersedes`, `occurrences`, `date`, `last_seen`, `tags`, `prevention_rule`) and the structured Field-Notes sections (`Summary`, `Expected -> Actual -> Root Cause -> Fix` table, `Invocation Parameters & Raw Logs`, `Verification / Pre-Flight Oracle`, `Prevention Invariant`).

4. **Mandatory 35-Second Liveness & Anti-Stall Check**:
   - **Never Sit Idle**: Agents and tools must never allow an MCP command, async operation, background task, or child process to run unchecked without verifying status for more than **35 seconds**.
   - **Heartbeat & Triage**: When dispatching long-running MCP commands (e.g. client launch, Ghidra decompilation, Vulkan frame captures, Docker or server cluster startup), actively check on task status (`manage_task Action: 'status'`), inspect recent log lines, or verify process health within 35 seconds.
   - **Deadlock Termination**: If 35 seconds pass with no progress, log emission, or response, treat the operation as stalled:
     1. Terminate or cancel the blocked task immediately (`manage_task Action: 'kill'`).
     2. Inspect stderr, stdout, and recent log outputs to diagnose the deadlock (e.g. pipe deadlock, blocked stdio, unhandled popups, or socket hang).
     3. Immediately record or bump the failure via `scripts/kongor_mcp_issues.ps1` (`-Category ipc-timeout` or `decompiler-timeout`).
     4. Remediate or restart the environment before retrying.

# Rule: MCP Issue Tracking, Headless Safety & 35s Liveness

- **Auto Pre-Flight (`0 ms`):** `McpToolRegistry` handles pre-flight liveness checks, pitfall enrichment, Worker ROE (`KONGOR_WORKER_ROLE`), and 3-strike circuit breaking automatically.
- **Record / Bump Failures:** On any MCP defect or timeout, immediately run `scripts/kongor_mcp_issues.ps1 -Action bump -Id MCP-NNN` or `-Action new ...` followed by `-Action rebuild-index` and `-Action check` (use `-Action ingest-outcomes` for DuckDB/Parquet hotspot analytics).
- **35-Second Liveness Rule:** Never leave an MCP call or background task unchecked for `> 35s`; inspect logs, terminate deadlocks, record the issue, and remediate before retrying.
- **Headless Loopback Guardrails:** Launch MCP clients with `masterServer: "127.0.0.1:55555"` and `window_mode: "offscreen"`; use in-engine widget commands (never OS mouse/keyboard synthesis); assert `zeroWindowExposure == true`; never mutate remote DB/cache without explicit `KONGOR_MCP_ALLOW_REMOTE_DB_WRITE=1` and `confirmRemote: true`.

Deep reference: `docs/agent-playbooks/mcp_issues_full.md`

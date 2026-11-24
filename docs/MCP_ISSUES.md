# Project Kongor MCP Issue & Prevention Knowledge Base (`docs/MCP_ISSUES.md`)

> [!IMPORTANT]
> The monolithic `docs/MCP_ISSUES.md` backlog has been modularized into individual Field-Note-style documents under [`docs/reference/mcp-issues/`](../docs/reference/mcp-issues/) indexed by [`docs/reference/mcp-issues/index.json`](../docs/reference/mcp-issues/index.json) and managed via [`scripts/kongor_mcp_issues.ps1`](../scripts/kongor_mcp_issues.ps1).

---

## Canonical Files & Dashboard

- **Human-Readable Dashboard & Active Table:** [`docs/reference/mcp-issues/README.md`](../docs/reference/mcp-issues/README.md)
- **Machine-Searchable JSON Index:** [`docs/reference/mcp-issues/index.json`](../docs/reference/mcp-issues/index.json)
- **Issue Authoring Template:** [`docs/reference/mcp-issues/TEMPLATE.md`](../docs/reference/mcp-issues/TEMPLATE.md)
- **Management CLI:** [`scripts/kongor_mcp_issues.ps1`](../scripts/kongor_mcp_issues.ps1)

---

## Mandatory First-Time Success Workflow

### 1. Automatic C# Pre-Flight & Self-Healing (`0 ms`, Default-ON)
`McpToolRegistry` enforces automatic pre-flight liveness checks, `MatchIssueForError` pitfall enrichment, and structured `remediation_call` payloads by default (`skip_preflight: true` to opt out). Manual pre-flight PowerShell searches (`scripts/kongor_mcp_issues.ps1 -Action search`) are retired and no longer required before invoking MCP tools (retain `-Action search` only for ad-hoc manual ledger queries).

### 2. Recording or Bumping an MCP Issue
Whenever **any** MCP tool call fails, times out, or behaves unexpectedly:
1. **Check for an existing issue first** using `-Action search -Server <server> -Tool <tool>`.
2. **If the root cause is already documented (`MCP-NNN`):** Bump its occurrence counter and `last_seen` date instead of creating a duplicate file:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action bump -Id MCP-001
   ```
3. **If the root cause is brand new:** Scaffold a new `MCP-NNN-<slug>.md` document from [`TEMPLATE.md`](../docs/reference/mcp-issues/TEMPLATE.md) (use `-Force` if an active entry already exists for the same `server`/`tool` under a different category):
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action new -Server <server> -Tool <tool> -Category <category> -Title "<Short Descriptive Title>" -PreventionRule "<One-sentence rule>" -Force
   ```
   Fill in the `Summary`, `Expected -> Actual -> Root Cause -> Fix` table, and `Invocation Parameters & Raw Logs` in the generated file, then rebuild and verify the index:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action rebuild-index
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action check
   ```
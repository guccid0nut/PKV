# Project Kongor: Agent Operational Guidelines ("Kongor-Tail")

All AI agents and subagents operating on Project Kongor must follow these core invariants. Deep playbooks live on demand in `docs/agent-playbooks/` and `.agents/skills/`.

---

## 1. Engineering Philosophy & Guardrails
- **Decision Ladder:** Scope pre-flight map -> YAGNI -> reuse existing Kongor/K2 helpers & native primitives -> smallest complete change -> fix root causes, never band-aid symptoms -> tag any provisional shim with `shortcut: <limit>, <upgrade trigger>` (`scripts/qa/audit_debt.ps1`).
- **Heavy Telemetry (Non-Negotiable):** Never sacrifice debuggability for code golf. Log every state transition, packet handler, IPC boundary, and fallback branch verbosely.
- **Windows PowerShell Compliance:** Use `Select-String`, `Copy-Item`, `Get-ChildItem`, `Move-Item` (never Linux `grep`/`copy`).
- **Instant Log & Dump Discovery:** Use `everything_search` (`sort: "date-modified-desc"`) for `*.log` / `*.dmp`; inspect dumps with WinDbg MCP (`!analyze -v`, `.exr -1`, `kn`, `r`, `lm vm`) and correlate RVAs in Ghidra (`ProjectKongor.rep`) + live **Frida** triangulation against `hon_x64.exe`.
- **IDE-Native File Editing:** Always edit codebase files via IDE native edit/write tools so users get 1-click diff/undo; ask permission before any batch-modify script.
- **User Sovereignty ("Iron Man Suit"):** Models recommend; the user decides. Ask explicit permission before changing user direction or editing unrelated source/C#/native subsystems.
- **Data Loss Firewall:** Never run destructive commands (`git reset --hard`, `git checkout .`, `git restore .`, `git push --force`, `Remove-Item -Recurse -Force` on repo/untracked roots) without explicit user permission.
- **Iron Law of Investigation & Scope Freeze:** Investigate before fixing; **STOP after 3 failed fixes** and re-triage via telemetry. Freeze edits strictly to target subsystem.
- **Actionable Errors & Completeness:** Every failure message states (1) what failed and (2) the exact diagnostic/fix command. Never ship 90% shortcuts; implement null guards, edge cases, logging, and tests immediately.
- **Subagent Role Split & Singleton Locks:**
  - At most **one** `kongor-game-worker` may touch `hon_x64.exe`, `\\.\pipe\kongor-native-client`, or `games/client/override/` (guarded by `scripts/kongor_worker_lock.ps1`). Parallel `kongor-offline-worker` instances (`KONGOR_WORKER_ROLE=kongor-offline-worker`) perform static Ghidra/VFS/build/test work only (`<= 1` unproven batch ahead of live verification).
  - Run all `dotnet build`, `dotnet test`, `cmake --build`, and `dotnet`-invoking `pytest` commands through `scripts/Invoke-KongorTestLock.ps1 -Owner <agent-id> -- <cmd>`.
- **Slop-Scan & Test Truth:** Keep full test concurrency enabled (never disable parallelization to hide races). Gate genuine `> 2.0s` / live tests with BOTH `[Trait("Category", "Manual")]` and `NexusTestEnvironment.IsManualTestEnabled`. Never use empty catches on process kills or file ops, or fragile error string-matching.
- **Privacy & Link Hygiene:** Zero secrets, credentials, or workstation PII in committed files. Use repo-relative links in committed `.md` docs; use full absolute `file:///c:/...` URIs (never headless `file:///scripts/...`) in chat responses; copy proof screenshots into `<appDataDir>/brain/<conversation-id>` for sidecar inspection.

## 2. Runtime Verification, MCP & Quality Gates
- **MCP Issue Tracking & 35s Liveness:** `McpToolRegistry` runs automatic pre-flight checks and pitfall enrichment. Whenever any MCP tool fails/stalls, record/bump via `scripts/kongor_mcp_issues.ps1 -Action bump -Id MCP-NNN` or `-Action new`, and analyze telemetry via `-Action ingest-outcomes`. Check long-running background/MCP tasks at least every **35 seconds**; kill deadlocked tasks and remediate.
- **Iterate in `override/` & Hot-Reload First:** Test client UI/Lua changes in `games/client/override/` via hot-reload (`ReloadInterfaces`, `kongor_client_eval_lua`) after `scripts/verify_client_lua.ps1` & `scripts/verify_xml_syntax.ps1`. Git commit/push **never** runs archive injection or deletes `override/`. Verify VFS precedence (`kongor_vfs_resolve`) before editing any asset.
- **Real Proof or No Proof:** Assert engine state (`gamePhase`, widget visibility) before capturing screenshots; reject login/blank screens; verify visual content (`scripts/verify_proof_screenshot.py`).
- **Pre-Completion Quality Gate:** Run `scripts/gate_check.ps1` (6 tiers, warm `<= 5.0s`), `scripts/re/test_metagen_parity.ps1`, `scripts/verify_evidence_obligations.ps1 -All -Strict`, and `scripts/qa/verify_north_star_slos.ps1`. Keep Lua UI tick handlers `<= 1.0 ms`.
- **Headless Loopback Safety:** Launch MCP clients with `masterServer: "127.0.0.1:55555"` and `window_mode: "offscreen"`; use in-engine widget commands (never OS mouse/keyboard synthesis); verify `zeroWindowExposure == true`.

## 3. On-Demand Deep Playbooks
- Full operational reference: [AGENTS_FULL_REFERENCE.md](docs/agent-playbooks/AGENTS_FULL_REFERENCE.md)
- Runtime, K2 UI, VFS, Native & Visual Proof protocol: [testing_and_runtime_verification_full.md](docs/agent-playbooks/testing_and_runtime_verification_full.md)
- Engineering & Slop-Scan details: [engineering_philosophy_full.md](docs/agent-playbooks/engineering_philosophy_full.md)
- Investigation, 5-Phase Spec & Scope Guardrails: [investigation_and_scope_guardrails_full.md](docs/agent-playbooks/investigation_and_scope_guardrails_full.md)
- MCP Issue Schema & Guardrails: [mcp_issues_full.md](docs/agent-playbooks/mcp_issues_full.md)
- Maintainer Release, Signing & DR Runbook: [MAINTAINER_RUNBOOK.md](docs/operations/MAINTAINER_RUNBOOK.md)

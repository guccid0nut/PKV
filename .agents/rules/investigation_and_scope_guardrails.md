# Rule: Investigation Discipline, Scope Freezing & Data Loss Firewall

- **Iron Law of Investigation:** Trace callers and form testable hypotheses before editing; **STOP after 3 failed fixes** and re-triage via WinDbg/Frida/Ghidra/logs.
- **Scope Freezing:** Confine edits to the target subsystem; zero unapproved collateral changes.
- **5-Phase Spec Protocol (Non-Trivial Work):** Intent -> Scope/Non-Goals -> Proof-of-Read (exact lines/symbols) -> Spec Draft -> Clean Implementation.
- **Data Loss Firewall:** Require explicit user approval before `git reset --hard`, `git checkout .`, `git restore .`, `git push --force`, or recursive `Remove-Item -Recurse -Force` on repo/untracked roots.
- **Singleton Execution Locks:** Wrap `dotnet build`, `dotnet test`, `cmake --build`, and `dotnet`-invoking `pytest` in `scripts/Invoke-KongorTestLock.ps1`; enforce singleton `kongor-game-worker` vs parallel `kongor-offline-worker`.
- **Slop-Scan:** Never disable test parallelization to hide flakiness; gate `> 2.0s` / live tests with `[Trait("Category", "Manual")]` + `NexusTestEnvironment.IsManualTestEnabled`; zero empty catches on process kills or file ops.

Deep reference: `docs/agent-playbooks/investigation_and_scope_guardrails_full.md`

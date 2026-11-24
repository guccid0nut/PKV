# Rule: Investigation Discipline, Scope Guardrails & Quality Assurance

This directive governs how agents diagnose bugs, restrict edit scopes, plan implementations, explore CLI/engine fixtures, and structure documentation.

---

## 1. The Iron Law of Investigation
> *"No fixes without investigation. Trace data flow, formulate testable hypotheses, and STOP after 3 failed fixes."*

1. **Investigate Before Editing:**
   - Never write speculative or "hopeful" patches.
   - Always trace the data flow from caller to root dispatcher. Formulate an explicit hypothesis (e.g., *"The crash occurs because pointer X is null after vtable dispatch Y"*).
2. **The 3-Strike Hard Ceiling:**
   - If an applied fix or candidate change fails testing/verification **twice**, the agent must pause.
   - If a **third attempt fails**, the agent is **HARD-STOPPED** from making further code edits.
   - Action on Strike 3:
     1. Discard broken candidate edits immediately.
     2. Collect diagnostic telemetry: WinDbg dump triage (`!analyze -v`), Frida hooks, Ghidra symbol correlation, or engine logs.
     3. Present the findings and revised hypothesis to the user before touching code again.

---

## 2. Scope Freezing & Edit Boundaries (The Anti-Contamination Guard)
> *"File modifications are strictly frozen to the designated subsystem or directory in scope."*

1. **Declare Scope Boundary Upfront:**
   - Before editing any file, identify the target subsystem (e.g. `games/client/override/ui/` for UI bugs, or `src/tools/kongor-cli/` for CLI tools).
2. **Zero Collateral Edits:**
   - Never modify adjacent libraries, build scripts, server components, or core utilities unless the user explicitly requested changes to them.
   - If a fix in component A seems to require changing component B, stop and ask the user for confirmation first.
3. **Override Directory Hygiene:**
   - `games/client/override/` has highest K2 VFS mounting precedence. Broken or abandoned files in `override/` poison all subsequent game sessions. Keep `override/` clean of dead experiments.

---

## 3. Multi-Perspective Planning Pipeline
Before implementing non-trivial features or refactors, evaluate through three distinct lenses:

1. **Scope Challenge (Founder / CEO):**
   - What is the absolute narrowest viable wedge?
   - Can we solve 90% of the problem with 20% of the code?
   - Apply YAGNI ruthlessly: reject speculative future abstractions.
2. **Engineering Review (Engineering Manager):**
   - Map data flow, state machine transitions, and async cancellation tokens.
   - Identify edge cases, race conditions, memory bounds, and failure modes.
   - Define a concrete test matrix and verification protocol before writing code.
3. **Developer Experience (DX) Review:**
   - Are CLI command flags and arguments intuitive and self-documenting?
   - Does error output produce actionable JSON with diagnostic details instead of silent failures or raw stack dumps?

---

## 4. Structured Non-Browser & CLI QA Exploration
For verifying non-browser software (K2 engine, `kongor-cli`, bot choreographies, IPC Named Pipes, and headless renderers):

1. **Pre-Flight Declaration:**
   - State the target fixture, binary, command line, and test boundaries upfront.
2. **Systematic Probes:**
   - Test happy path, boundary values, invalid inputs, timeouts, and process cancellation.
   - Log intermediate discovery probes so every test decision has observable evidence.
3. **Red-Green Verification Gate:**
   - A discovered bug must first be confirmed with a reproducible failing probe or test case.
   - Apply the minimal root-cause fix.
   - Verify that the exact reproduction now passes without regression.

---

## 5. Documentation Architecture: The Diataxis Framework
All project documentation under `docs/` must strictly conform to one of the 4 Diataxis quadrants:

| Quadrant | Focus | Orientation | Example in Project Kongor |
| :--- | :--- | :--- | :--- |
| **Tutorials** | Learning | Practical | *First Time Launching Practice Mode & Hot-Reloading* |
| **How-To Guides** | Task/Goal | Practical | *How to Hook a Network Packet Handler with Frida* |
| **Reference** | Information | Theoretical | *CVar Catalog, Entity XML Schema, Ghidra RVA Maps* |
| **Explanation** | Understanding | Theoretical | *K2 VFS Archive Mounting Precedence & 32-Bit ZIP Limits* |

- Never blend tutorials with reference dumps; keep documents focused on their specific quadrant.

---

## 6. The Adversarial Reviewer Subagent Protocol (Doubt-Driven Review)
Before landing non-trivial bug fixes, archive injections, or architectural modifications:

1. **Invoke the Adversarial Reviewer:**
   - Use `invoke_subagent` with Role `"Adversarial Reviewer"`, passing the diff, test logs, and design assumptions.
2. **Reviewer Mandate:**
   - **Stress-Test Assumptions:** Examine edge conditions: 32-bit signed integer limits (< 1.5 GB), null pointers, disconnects, pipe timeouts, hot-reload race conditions.
   - **Scope Verification:** Verify zero collateral changes outside the declared frozen boundary.
   - **Lint & Syntax Gate:** Ensure pre-flight syntax checks (`verify_client_lua.ps1`, `verify_xml_syntax.ps1`) executed with 0 errors.
   - **Decision Ladder Compliance:** Confirm the code reused existing K2/Kongor systems rather than adding speculative abstractions.
3. **Verdict:**
   - The reviewer returns a structured `PASS` or `BLOCKED` with concrete counter-examples and failure scenarios before code is merged or injected into `.s2z`.

---

## 7. Durable Session Ledger (`docs/ACTIVE_SESSION_STATE.md`)
To maintain rock-solid context continuity across conversational turns, subagent invocations, and session restarts:

1. **State Persistence:**
   - Active debugging sessions must maintain [`docs/ACTIVE_SESSION_STATE.md`](../ACTIVE_SESSION_STATE.md).
2. **Mandatory Schema:**
   - **Active Task / Bug:** Single-sentence problem statement.
   - **Frozen Scope Boundary:** Directory and files authorized for modification.
   - **Hypothesis & Strike Count:** Working hypothesis and failed attempt counter (0/3 to 3/3).
   - **Telemetry & Findings:** Crash dumps (analyzed with WinDbg/Everything), logs, and Ghidra RVAs.
   - **Verification Checklist:** Test commands, visual diff checks, and hot-reload verification status.
3. **Session Handoff:**
   - Any agent resuming or inheriting work reads `docs/ACTIVE_SESSION_STATE.md` first to eliminate duplicated discovery steps.

---

## 8. The 5-Phase Executable Spec Protocol
Before implementing complex features, major refactors, or new subsystems, follow this 5-phase discipline:

1. **Phase 1: Intent & Why:**
   - State the problem and user-visible outcome in 1-2 paragraphs.
   - Clarify what pain this solves and what the success state looks like in-game.
2. **Phase 2: Scope & Non-Goals:**
   - Explicitly list what is **in scope** and what is **out of scope**.
   - Establish the frozen boundary before any code is drafted.
3. **Phase 3: Mandatory Code-Reading Gate:**
   - **Hard Rule:** You must search and read existing engine headers, Lua definitions, and VFS archive entries *before* drafting types or functions.
   - Identify existing cvars, widget helpers, or VFS mounts that already solve parts of the problem.
4. **Phase 4: Specification Draft:**
   - Define exact function prototypes, data structures, failure modes, and verification commands.
   - Run the 3-tier challenge (Scope / Eng / DX) to eliminate unrequested abstractions.
5. **Phase 5: File & Implement:**
   - Write the minimal code satisfying the spec.
   - Verify against the test plan before injecting into `.s2z`.

---

## 9. Destructive Command Guardrails (The Data Loss Firewall)
To prevent irreversible data loss and inadvertent deletion of user/reverse-engineering work:

1. **Strictly Blocked / Ask-First Commands:**
   Never run the following commands without explicit prior user consent:
   - `git reset --hard` or `git checkout .` / `git restore .`: Wipes out uncommitted reverse-engineering work or live modifications in `games/client/override/`.
   - `git push --force` / `-f` to default or shared branches.
   - `Remove-Item -Recurse -Force` or `del /s` targeting repository roots, parent workspaces, or system paths.
   - Low-level disk/volume modifications (`Format-Volume`, `Clear-Disk`, `[IO.Directory]::Delete`).
2. **Safe Deletion Protocol:**
   - Single-file removals during `.s2z` eviction must target explicit literal paths (`Remove-Item -LiteralPath <exact_path> -Force`).
   - Broad wildcards (`*.*`) on directory trees are strictly prohibited.

---

## 10. Finite Probe Budgets & Hard Execution Deadlines
When executing exploratory QA, bot choreography validation, or crash investigations:

1. **Finite Probe Ceiling:**
   - Every discovery session is capped at **10 discovery probes**.
   - If an investigation sequence does not locate the root cause within 10 probes, stop exploring and summarize the observed telemetry.
2. **Hard Wall-Clock Deadline:**
   - A single investigative probe run must not exceed **5 minutes** of continuous execution.
3. **Telemetry Receipt Flushing:**
   - Save intermediate probe observations to `artifacts/exploration/exploration-NNN.json`.
   - When the probe budget or execution deadline is reached, halt gracefully, write the final summary, and present clear options to the user rather than looping indefinitely.

---

## 11. Hermetic Scratch Isolation & Hard-Link Protection
To protect the live asset database (`assets.db`), `games/client/override/`, and hard-linked server archives during batch operations or automated test suites:

1. **Mandatory Scratch Root for AssetDB & Batch Scripts:**
   - Never run `tools/assetdb/sync_record.py`, `pytest tools/assetdb`, or `scripts/prebake-assets.ps1` without an explicit isolated scratch directory (`--repo-root <temp_dir>` or `-OutputDir <scratch_dir>`).
2. **Hard-Linked Archive Protection:**
   - `resources0.s2z`, `resources5.s2z`, and `resources98.s2z` are hard-linked between `games/client/game/` and `games/server/game/`. Never perform in-place edits on these archives, `games/client/game/textures.s2z`, or `resources94.s2z` without first inspecting `fsutil hardlink list` and obtaining explicit user authorization.

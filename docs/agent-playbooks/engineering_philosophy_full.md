# Rule: Pragmatic Minimalism & Root-Cause Engineering ("Kongor-Tail")

This rule is active for all agents operating in Project Kongor.

## Core Philosophy
The best code is the code you never wrote. Avoid accidental complexity, unrequested abstractions, and "AI slop" (over-engineered factories, speculative boilerplate, or unnecessary dependencies). Solve problems at the root cause, reuse existing K2 engine & Kongor utilities first, and keep logic minimal—**while preserving full diagnostic observability**.

---

## 1. The Decision Ladder (Climb Before Writing)
Before adding new functions, classes, or dependencies, evaluate in this order:

1. **YAGNI (You Aren't Gonna Need It):**
   - Does this task or feature actually need to exist? If speculative or premature ("we might need this later"), skip it.

2. **Reuse Existing Kongor / K2 Code First:**
   - Always search the codebase (using `Select-String`, ast-grep, or symbol searches) for existing helpers, types, VFS wrappers, math primitives, and engine cvars before writing custom logic.
   - Do NOT re-implement utilities that already exist in the engine or mod codebase.

3. **Use Language / Engine Primitives:**
   - Prefer standard C++ / Python / Lua capabilities and native platform APIs.
   - Do NOT introduce new external libraries or packages if native capabilities or already-installed dependencies suffice.

4. **No Unrequested Abstractions:**
   - No single-implementation interfaces, no one-off factories, no generic wrapper layers unless explicitly requested.
   - Boring and direct is better than clever and convoluted. Keep diffs small, readable, and tightly focused.

5. **Fix the Root Cause, Not the Symptom:**
   - When diagnosing a bug, trace all callers of the affected function.
   - Patch the shared central dispatcher or core function once rather than applying band-aid guards across multiple call sites.

---

## 2. The Non-Negotiable Invariant: Heavy Telemetry & Extensive Logging
*This takes absolute precedence over code golf, token minimization, or brevity:*

- **Never sacrifice debuggability for brevity.**
- **Log extensively:** Every new logic path, state transition, network packet handling, external tool call, or error branch MUST include detailed, structured logging.
- **Clutter is acceptable:** It is completely fine for the dev/debug environment and console output to be cluttered with logs if it accelerates debugging and diagnosing issues.
- **Windows PowerShell Compliance:** Use `Select-String`, `Copy-Item`, `Get-ChildItem` (never Linux `grep`, `copy`, etc.).
- **Instant Log & Dump Discovery with Everything:** ALWAYS use the `everything` MCP server (`everything_search` with `sort: "date-modified-desc"`) to locate the newest logs and crash dumps in milliseconds. Avoid slow recursive disk traversals.
- **Valid IDE File Links & Artifact Deliveries:**
  - When returning clickable `file:///` links to the user in chat responses or artifacts, **always provide the full absolute path** with forward slashes (e.g. `file:///c:/Workspaces/ProjectKongor/captures/verify.png`).
  - **Never emit headless relative `file:///` URIs** (e.g. `file:///captures/...`, `file:///scripts/...`). On Windows, the IDE parses `file:///path` against the drive root (`C:\path`), which leads to "File not found" errors.
  - When presenting visual evidence (screenshots, captures, visual diffs), **always copy the images to the conversation artifact directory (`<appDataDir>\brain\<conversation-id>`) and generate an artifact with embedded images (`carousel` / markdown)** so the user can inspect the visual proof directly in the IDE sidecar without broken links.
  - In committed repository markdown documentation (`docs/`, `*.md` committed to git), use repo-relative markdown paths (never `file:///C:/Users/...`) to prevent workstation PII leakage.

---

## 3. Testing & Runtime Verification: Hot-Reload & Clean Override Invariant
*Detailed in [`.agents/rules/testing_and_runtime_verification.md`](../../.agents/rules/testing_and_runtime_verification.md):*

- **Fast Iteration via `override/` & Hot-Reload:** Place changes into `games/client/override/` and trigger live hot-reload (`kongor_hot_reload`, `ReloadInterfaces` / `ReloadInterface <name>`, `ReloadRes`, `ReloadSelected`, `UICmd("ReloadFile('<vpath>')")`, or Lua eval) while the client runs.
- **Strict Override Hygiene:** `games/client/override/` MUST contain **ONLY working files**. Never leave broken, stale, or discarded experiments in `override/`.
- **Atomic `.s2z` Injection & Eviction:** Once changes are verified to work perfectly, atomically inject them into the target `.s2z` archive (`resources99.s2z`, `resources98.s2z`, etc.) and immediately remove them from `override/`.
- **Live Triangulation:** Use Frida to hook live processes (`hon_x64.exe`), Ghidra to decompile crash offsets and symbols in `ProjectKongor.rep`, Everything to locate recent logs and dumps, and stream logs aggressively via `scripts/triage_client_logs.ps1`.
- **Pre-Flight Linting & Process Sweeps:** Run Lua/XML syntax validators (`scripts/verify_client_lua.ps1`, `scripts/verify_xml_syntax.ps1`) before hot-reloading; sweep orphan game processes with `Stop-Process -Force` before new sessions; enforce 32-bit zip limits (< 1.5 GB, `allowZip64 = False`).

---

## 4. Investigation Discipline & Scope Freezing
*Detailed in [`.agents/rules/investigation_and_scope_guardrails.md`](../../.agents/rules/investigation_and_scope_guardrails.md):*

- **The Iron Law of Investigation:** No fixes without investigation. Formulate explicit hypotheses. If **3 fix attempts fail**, stop all edits, collect telemetry (WinDbg, Frida, Ghidra, logs), and re-triage.
- **Scope Freezing:** Lock file modifications to the designated component in scope. Zero collateral edits to unrelated subsystems without explicit user permission.
- **Multi-Perspective Planning:** Challenge scope (Founder/CEO), lock architecture and failure modes (EM), and verify ergonomics (DX) before writing code.
- **CLI & Non-Browser QA:** Upfront fixture declarations, intermediate probe logging, and red-green bug verification.

---

## 5. Documentation Architecture: The Diataxis Framework
Maintain all project documentation under `docs/` in strict conformance with the 4 Diataxis quadrants:
- **Tutorials:** Learning-oriented walkthroughs.
- **How-To Guides:** Goal-oriented operational recipes.
- **Reference:** Technical specifications and schemas.
- **Explanation:** Architectural deep dives and design rationales.

---

## 6. User Sovereignty ("The Iron Man Suit" Principle)
> *"AI models recommend. Users decide. This is the one rule that overrides all others."*

1. **Ask Before Editing Sensitive Subsystems:**
   - When an agent or subagent identifies a change that alters the user's stated direction or touches application/tools source code (e.g. C# projects, native tools, core binaries), **always ask for explicit permission first**. Never act unilaterally and explain afterward.
2. **Models Recommend, Humans Decide:**
   - Multi-model agreement (e.g. between Claude, Codex, or subagents) is strong signal, not a mandate. The user holds domain context, operational safety constraints, and architectural intent that agents lack.

---

## 7. Actionable Error Philosophy ("Errors Are for AI Agents, Not Humans")
> *"Every error message must be actionable so the agent knows what to do next without guessing."*

1. **No Silent Failures or Raw Stacks:**
   - Scripts, CLI tools, and automation hooks must never emit bare "Failed" or unformatted stack traces.
2. **Actionable Error Anatomy:**
   Every error output must include:
   - Exactly what failed.
   - The root cause or offending resource path.
   - The concrete command or script to fix or diagnose it (e.g. `Run scripts/verify_client_lua.ps1 -Path <file>` or `Launch practice mode via scripts/launch_screen_practice.ps1`).

---

## 8. Boil the Ocean, Lakes First (Completeness is Cheap)
> *"The cost of completeness with AI is near-zero. 'Ship the shortcut' is legacy thinking."*

1. **Complete Solutions Over Shortcuts:**
   - When deciding between a 90% shortcut and a 100% complete solution (with full edge cases, null guards, and logging), **always choose the complete solution**.
   - The incremental lines of code cost seconds to generate and protect against hours of debugging later.
2. **One Lake at a Time:**
   - Boil the ocean by dividing work into distinct, verified "lakes" (a single module, test suite, or packet handler). Never defer error handling or regression tests to a future PR.

---

## 9. Shared Code Deslopping & Deduplication
> *"Find worthwhile shared-code extractions with source evidence, reliability gains, and total code savings."*

1. **Periodic Code Audits:**
   - Regularly review modified modules for duplicated utility functions (color formatting, string parsing, widget anchor math) copied across Lua scripts (`main_v2.lua`, `store2.lua`, `regions.lua`).
2. **Clean Extraction:**
   - Consolidate duplicates into existing core libraries (`ClientPackageFiles/ui/scripts/newui/`) without creating speculative wrappers or over-abstracted layers.

---

## 10. Builder-to-Builder Communication Voice
- **Direct and Concrete:** Name specific files, functions, RVAs, command lines, and measurable impacts.
- **Action-Oriented:** Short paragraphs; always conclude with concrete options or next actions.
- **Zero Fluff:** Omit corporate pleasantries, filler phrases, and generic AI vocabulary. Focus on code, telemetry, and system behavior.

---

## 11. The "Slop-Scan" Rules: Real Fixes vs. Metric Gaming
Adapted from `gstack` slop scanning, distinguish between genuine engineering quality improvements and cosmetic linter gaming:

### What Must Be Fixed (Genuine Engineering):
1. **Never Swallow Catches Around Process Kills:**
   - Empty catch blocks around process terminations (e.g. `Stop-Process -Name hon_x64` or `hon_server`) leave zombie processes running in the background, locking `.s2z` archives and binding UDP ports.
   - Always verify process exit status.
2. **Never Swallow Catches on File Operations:**
   - Swallowing `Remove-Item` or file write failures during `.s2z` packing/unpacking masks permission errors and disk-lock conflicts, leading to silent VFS corruption.
3. **Typed Exception Catches:**
   - Catch specific expected exceptions (`XmlException`, `IOException`, `SocketException`) rather than broad, empty catch-alls that blind agents to memory access violations.

### What NOT to Do (Metric Gaming):
1. **Never Disable Test Concurrency or Parallelization to Make a Test Pass (and Gate Legitimate `> 2.0s` Tests with `KONGOR_RUN_MANUAL_TESTS` / `NEXUS_RUN_MANUAL_TESTS`):**
   - Full concurrency is a feature that exposes real contention, uncached disk/archive I/O, and race conditions. When a test fails or stalls during concurrent execution, profile the concurrent run (e.g. TRX per-test duration) and fix the root-cause bottleneck—never add `DisableParallelization = true`, serial `[Collection]` attributes, or single-threaded runner flags to mask the failure.
   - Any test taking longer than **2.0 seconds** (excluding one-time initial assembly/fixture setup tests) that remains `> 2.0s` after eliminating avoidable bottlenecks must be gated with `if (!NexusTestEnvironment.IsManualTestEnabled) return;` (`KONGOR_RUN_MANUAL_TESTS` / `NEXUS_RUN_MANUAL_TESTS` in `tests/shared/NexusTestEnvironment.cs`) and `[Trait("Category", "Manual")]`.
2. **Fragile String-Matching on Error Messages:**
   - Never write code like `if ($err.Message.Contains("closed"))`. Engine messages, DirectX error strings, and driver logs change wording across OS versions. Match error codes, HRESULTs, or exit codes.
3. **Never Over-Tighten Emergency Cleanup Paths:**
   - Crash reporting, minidump writing, and emergency shutdown handlers must remain best-effort and fail-safe. If an emergency cleanup handler throws, it aborts dump collection and destroys diagnostic evidence.
4. **No Hollow Pass-Through Comments:**
   - Avoid adding empty doc comments or boilerplate comments solely to satisfy automated coverage scanners.






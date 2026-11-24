---
name: kongor-subagent-orchestration
description: Orchestrates subagents using the singleton kongor-game-worker vs. parallel kongor-offline-worker split, filesystem mutex (scripts/kongor_worker_lock.ps1), 1-batch anti-pileup rule, and cold-reader briefing protocol. Use whenever spawning subagents (invoke_subagent), parallelizing Ghidra/VFS/test work alongside a live hon_x64.exe session, or coordinating multi-step debugging across workers.
capability_contract:
  allowed_worker_roles: ["kongor-game-worker", "kongor-offline-worker"]
  allowed_tools: ["invoke_subagent", "send_message", "manage_subagents", "run_command", "view_file"]
  requires_worker_lock: true
  max_probes: 10
---

# Project Kongor Subagent Orchestration (`Game Worker` vs. `Offline Worker`)

Coordinate subagents without racing on `hon_x64.exe`, `\\.\pipe\kongor-native-client`, or `games/client/override/`.

---

## 1. The Two Worker Roles

| Capability / Boundary | `kongor-game-worker` (**SINGLETON**) | `kongor-offline-worker` (**PARALLELIZABLE**) |
| :--- | :--- | :--- |
| **Concurrency** | **Max 1 active instance** across the machine (enforced by `scripts/kongor_worker_lock.ps1`) | **1..N concurrent instances** |
| **Live Game & IPC** | Exclusive owner of `hon_x64.exe`, `hon_server.exe`, `\\.\pipe\kongor-native-client`, `kongor_hot_reload`, `kongor_ui_control`, `kongor_capture_window` | **Zero live game touch.** Never launch/kill `hon_x64.exe` or call live client IPC tools (`$env:KONGOR_WORKER_ROLE = "kongor-offline-worker"`) |
| **File Mutation Scope** | `games/client/override/`, tracked mirrors (`games/client/game/`, `verify_lua/`, `server/`), and `.s2z` injection | Read-only codebase/Ghidra/VFS inspection, scratch scripts, `docs/reference/field-notes/`, `docs/reference/mcp-issues/`, or isolated unit tests |
| **Primary Tools** | `scripts/launch_screen_practice.ps1`, `kongor_hot_reload`, `kongor_client_eval_lua`, Frida, WinDbg | Ghidra (`ProjectKongor.rep`), `scripts/kongor_kb.ps1`, `scripts/kongor_mcp_issues.ps1`, `scripts/vfs/read_archive_entry.ps1`, `LuaVerifier.exe`, `scripts/gate_check.ps1` |
| **Required Turn Output** | Empirical pass/fail per probe + log/screenshot/dump evidence | **Risk-ordered list of live probes** for the `kongor-game-worker` to execute next |

---

## 2. Execution Sequence

### Step 1: Consult the Field-Note KB Before Spawning Workers
Run a fast Field-Note KB lookup before dispatching subagents so workers start with verified RVAs and engine laws (note: manual pre-flight `scripts/kongor_mcp_issues.ps1 -Action search` is retired per `AGENTS.md` §2 because `McpToolRegistry` performs `0 ms` default-ON pre-flight liveness checks and `MatchIssueForError` pitfall enrichment automatically):
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_kb.ps1 -Action search -Query "<subsystem|rva|symptom>"
```
**Completion criterion:** Relevant `FN-NNN` field notes and verified RVAs are cited in the subagent brief under **Confirmed Facts**.

### Step 2: Acquire the Filesystem Mutex Before Any Live Game Batch
Before a `kongor-game-worker` launches `hon_x64.exe`, hot-reloads `override/`, or injects an `.s2z` archive, acquire the singleton lock:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_worker_lock.ps1 -Action acquire -Owner "game-worker-1"
```
When the live batch finishes (or in cleanup), release the lock:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_worker_lock.ps1 -Action release -Owner "game-worker-1"
```
**Completion criterion:** `kongor_worker_lock.ps1 -Action status` confirms lock state transitions cleanly with zero orphan locks.

### Step 2b: Serialize All Build & Test Runs via the `test-runner` Mutex
Parallel `kongor-offline-worker` agents (and orchestrators) may edit disjoint files simultaneously, **but must never run `dotnet build`, `dotnet test`, `pytest`, or `cmake --build` concurrently** in the same workspace (which causes `CS2012` `obj/`/`bin/` lock collisions). Always invoke builds/tests through `scripts/Invoke-KongorTestLock.ps1`:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Invoke-KongorTestLock.ps1 -Owner "offline-worker-1" -- dotnet test tests/unit/libs/NEXUS.Contracts.Tests --nologo
# Inspect active test lock holder at any time:
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_worker_lock.ps1 -Scope test-runner -Action status
```
`Invoke-KongorTestLock.ps1` tracks its own wrapper `$PID`, auto-queues up to `240s` when another agent is running tests, reaps dead/expired locks automatically, and releases `$env:TEMP\kongor_test_runner.lock` in `finally` + `SetConsoleCtrlHandler`.

### Step 3: Enforce the 1-Batch Anti-Pileup Rule & 3-Strike Circuit Breaker
1. **1-Batch Anti-Pileup Rule:** Never queue $> 1$ unproven `kongor-offline-worker` code/override batch ahead of live `kongor-game-worker` verification. Verify Batch $N$ in the live client before staging Batch $N+1$.
2. **3-Strike Live Circuit Breaker:** If a live probe or hot-reload fix fails **3 times** in `kongor-game-worker`, freeze edits immediately, release the game-worker lock, and hand the crash dump / log telemetry back to a `kongor-offline-worker` for static Ghidra + WinDbg triangulation.

---

## 3. Mandatory Cold-Reader Subagent Briefing Template

Every `invoke_subagent` prompt must be self-contained for a cold reader with zero ambient conversation history:

```markdown
## Role: [kongor-game-worker | kongor-offline-worker]
## Worker Owner ID: <unique-id>

### 1. Goal & Done Criteria
- **Objective:** <Concrete deliverable>
- **Done When:** <Exact verifiable command/oracle that must pass>

### 2. Confirmed Facts (With File / RVA Citations)
- <Fact 1 + exact file:line or RVA>
- <Relevant Field Notes: FN-NNN>

### 3. Ruled-Out Dead Ends (Do Not Re-Test)
- <Dead end 1 + why it failed>

### 4. Hard Scope & Lock Rules
- Edit Boundary: <Allowed paths only>
- Worker Lock: <Acquire/Release via scripts/kongor_worker_lock.ps1 if game-worker; set $env:KONGOR_WORKER_ROLE="kongor-offline-worker" if offline-worker>

### 5. Ordered Task Queue
1. <Step 1>
2. <Step 2>
```

---

## 4. Structured Subagent Handoff Packet Schema (`<= 1500` Tokens) & Runtime Guardrails (`#50, #51, #54, #61, #116`)

When a worker completes its turn, hits the soft `90%` probe/deadline threshold (`[BUDGET-WARN-90%]`), or trips the 3-strike loop breaker (`[LOOP-BREAKER]`), it must emit a structured JSON handoff packet (`<= 1500` tokens) generated via `scripts/dev/compact_session_state.ps1` or `scripts/kongor_worker_lock.ps1`:

```json
{
  "schema_version": "1.0",
  "token_budget_limit": 1500,
  "verified_facts": [
    "k2_x64.dll+0x951020 (CModelPanel::Frame) resets StartTime when anim attribute is static"
  ],
  "tombstoned_hypotheses": [
    "DISPROVEN: Per-frame loadfile mailbox polling -> exhausts luaV_concat allocator at RVA 0xCB6477"
  ],
  "risk_ordered_live_probes": [
    {
      "priority": 1,
      "risk": "low",
      "probe": "Verify onloadlua SetAnim('idle') in games/client/override/ui/main_v2.interface"
    }
  ]
}
```

### Runtime Guardrail Commands
```powershell
# Machine-enforced Rules of Engagement check (#54): exits 2 with [ROE-DENIED] if offline worker touches live client/override
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_worker_lock.ps1 -Action check-roe -ToolName "kongor_hot_reload" -TargetPath "games/client/override/ui/store2.lua"

# Record tool call & check 3-strike loop breaker (#116, exit 3) + 90% probe budget warning (#61)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_worker_lock.ps1 -Action record-call -Owner "game-worker-1" -ToolName "kongor_client_eval_lua" -ArgsSha256 "abc123" -ErrorCode "ERR_TIMEOUT" -ProbeCount 9 -MaxProbes 10

# Enqueue non-disruptive mid-run operator steering directive (#50) and drain on heartbeat
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/qa/Send-KongorWorkerSteer.ps1 -Directive focus_rva -TargetValue "k2_x64.dll+0x951020" -Reason "Inspect CModelPanel::Frame"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_worker_lock.ps1 -Action heartbeat -Owner "game-worker-1"

# Compact docs/ACTIVE_SESSION_STATE.md into a <= 1500 token JSON handoff packet (#51, #116)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev/compact_session_state.ps1 -SessionStatePath "docs/ACTIVE_SESSION_STATE.md"
```


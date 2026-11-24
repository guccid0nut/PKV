---
name: kongor-crash-triage
description: >
  Deterministic 4-step crash-to-fix pipeline for Project Kongor native minidumps (*.dmp):
  (1) Sub-second dump discovery via Everything MCP, (2) WinDbg CDB automated extraction
  (!analyze -v, .exr -1, kn, r, lm vm), (3) Exact Ghidra RVA calculation and decompilation
  against ProjectKongor.rep, and (4) Root-cause & thread-safety verification before writing any fix.
  Use whenever hon_x64.exe or hon_server.exe crashes, produces a minidump, or throws 0xC0000005 / 0xE06D7363.
capability_contract:
  allowed_worker_roles: ["kongor-offline-worker", "kongor-game-worker"]
  allowed_tools: ["call_mcp_tool", "view_file", "run_command"]
  requires_worker_lock: false
  max_probes: 10
---

# Kongor Crash Triage (`kongor-crash-triage`)

Execute this deterministic 4-step pipeline whenever `hon_x64.exe`, `hon_server.exe`, or a native proxy DLL (`version.dll`, `Kongor_NativeGuard.dll`, `vid_vulkan_x64.dll`) crashes or generates a `.dmp` minidump.

**Hard Rule (Iron Law of Investigation):** Never guess a crash fix from log tailing alone when a `.dmp` file exists. Extract the exact faulting `RIP`, exception record, thread context, and Ghidra decompilation first.

---

## Step 1: Sub-Second Minidump & Log Discovery (`everything` MCP)

**Never** run `Get-ChildItem -Recurse` across `games/` or `userdata/` to find dumps. Query the NTFS index directly via `everything_search`:

```json
{
  "name": "everything_search",
  "arguments": {
    "query": "ext:dmp|diag_*.txt|console.log",
    "path": "<workspace_root>",
    "sort": "date-modified-desc",
    "max_results": 10
  }
}
```

Identify the newest `minidump_*.dmp` or `crash_*.dmp` matching the session timestamp.

---

## Step 2: WinDbg / CDB Extraction (`windbg` MCP)

1. **Open the dump:**
   ```json
   {
     "name": "open_cdb_dump",
     "arguments": {
       "dump_path": "<absolute_path_to_newest_dmp>"
     }
   }
   ```

2. **Run the mandatory 5-command diagnostic sweep (`run_cdb_command`):**
   - `!analyze -v` — Extracts faulting module, exception code (`0xC0000005` AV, `0xE06D7363` C++ EH, `0xC0000374` heap corruption), and failure bucket.
   - `.exr -1` — Extracts the exact `ExceptionAddress` and whether the fault was a read (`0`), write (`1`), or execute (`8`) violation and at what target offset (e.g., `0x000000000000004c`).
   - `kn 30` — Prints numbered stack frames with offsets across the faulting thread.
   - `~* k` — **(Critical for Concurrency Triage):** Inspects call stacks across **all threads** (especially Thread 0 vs. injected overlay/hook threads like `graphics-hook64.dll` or Named Pipe worker threads) to detect cross-thread races into single-threaded K2 subsystems (`CLuaScriptManager`, `CConsole`, `CUIManager`).
   - `r` and `lm vm <faulting_module>` — Extracts CPU register state (`rax`, `rcx`, `rdx`, `rsi`, `rdi`, `rip`) and the exact loaded `Base Address` of the faulting module (`k2_x64`, `cgame_x64`, `game_x64`, `version`, etc.).

3. **Release Debugger Handles:**
   Always call `close_cdb_session` once offsets and stacks are extracted so file locks on `.dmp` are released.

---

## Step 3: Exact RVA Calculation & Ghidra Decompilation (`ghidra` MCP)

All 64-bit K2 engine DLLs (`k2_x64.dll`, `cgame_x64.dll`, `game_x64.dll`, `game_shared_x64.dll`, `vid_d3d9_x64.dll`) in `src/tools/ghidra/projects/ProjectKongor.rep` use a default Ghidra image base of `0x180000000`.

1. **Compute the Ghidra Address:**
   $$\text{RVA} = \text{FaultingRIP} - \text{ModuleBaseAddress}$$
   $$\text{GhidraAddress} = \text{0x180000000} + \text{RVA}$$

2. **Decompile & Disassemble in Ghidra (Strict $\le 3$ Callee-Per-Turn Budget — `#47`):**
   - Call `decompile_function` on `binary_name` (e.g., `"k2_x64.dll"`) with `name_or_address: "0x180..."`.
   - Call `disassemble` around the exact `GhidraAddress` (`+- 0x40` bytes) to match the faulting assembly instruction (e.g., `mov dword ptr [rax+8], ecx`) against the C decompilation and WinDbg register dump.
   - **Hard Callee Decompilation Budget (`#47` — `callee-budget-and-struct-table`):** Never decompile more than **3 callee functions per turn** (`MAX_CALLEES_PER_TURN = 3`, enforced by `enforce_callee_budget` in `src/tools/ghidra/evidence_dossier.py`). Summarize each callee into the Evidence Dossier before descending deeper to prevent unbounded call-tree context explosion.

3. **Mandatory `struct_hypotheses` Offset Table (`#47`):**
   Before proposing any pointer offset dereference or struct field fix, record a `struct_hypotheses` table in the Evidence Dossier (or crash triage report) mapping every accessed struct offset:

   | Offset (`hex`) | Size (`bytes`) | Field Type | Evidence Source (`static_ghidra` \| `dynamic_frida` \| `windbg_dump`) | Notes / Register State |
   | :--- | :--- | :--- | :--- | :--- |
   | `+0x00` | `8` | `void** (vtable)` | `windbg_dump` | Verified via `r rcx` + `dq @rcx L2` |
   | `+0x4c` | `4` | `uint32_t` | `static_ghidra` | Read at `RIP = k2_x64.dll + 0x...` |

4. **Check Known Kongor Crash Archetypes:**
   - **Archetype A — External Hook / Off-Main-Thread Race (`graphics-hook64.dll` / IPC):**
     - *Symptom:* Fault in `CLuaScriptManager::PushGlobalEnvironment` (`k2_x64.dll + 0x92BB66`), `PushCallback` (`+ 0x92E9A6`), or `CUITrigger::Trigger` on a non-zero thread while Thread 0 is inside `CHost::Execute`.
     - *Fix:* Verify `GetCurrentThreadId() == GetWindowThreadProcessId(hWnd, NULL)` in `WndProc` (re-posting via `PostMessage` if false) and queue commands via `CConsole::AddCmdBuffer` instead of synchronous `CConsole::Execute`.
   - **Archetype B — Per-Frame `loadfile` / Lua Buffer Exhaustion:**
     - *Symptom:* Fault in `VCRUNTIME140!memcpy` called from `luaV_concat` (`k2_x64.dll + 0xCB6477`) with a small struct offset (`rax = 0x4c`) after `luaM_realloc_` returns `NULL`.
     - *Fix:* Eliminate per-frame `loadfile` / string-concatenation polling; switch to CVar change-signal triggers (`tao_req`).
   - **Archetype C — Uninitialized Widget / Teardown Callback:**
     - *Symptom:* Fault in `IWidget::ExecuteLuaCallback` (`k2_x64.dll + 0xC19150`) or `CModelPanel::SetEffect` during game phase transitions or disconnects.
     - *Fix:* Guard pointer dereferences and wrap native trampoline hooks in `__try / __except` (SEH).

---

## Step 4: Ledger Recording & Red-Green Verification

1. **Record in [`docs/ACTIVE_SESSION_STATE.md`](../../../docs/ACTIVE_SESSION_STATE.md):**
   Document the minidump filename, faulting `RIP`, `RVA`, call stack, register state, and root cause under `## 📡 Telemetry & Discovery Findings`.
2. **Enforce the 3-Strike Ceiling:**
   Increment or verify the Hypothesis Strike Count (`0/3` to `3/3`).
3. **Verify Zero New Dumps:**
   After applying the fix, count existing `.dmp` files via `everything_search` (`ext:dmp`) before and after running the reproduction scenario to prove **0 new minidumps** were generated.

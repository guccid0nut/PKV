---
name: kongor-gate
description: >
  Pre-completion verification gate and cross-tree parity check for Project Kongor.
  Runs the sub-5-second 6-tier quality gate (scripts/gate_check.ps1), verifies 0-drift
  parity across override/ and game/verify_lua/server/ClientPackageFiles/, checks metagen
  schema parity (scripts/re/test_metagen_parity.ps1), promotes verified field notes
  (scripts/kongor_kb.ps1), and audits K2 UI/native safety invariants.
  Use before declaring any code, Lua, XML, native hook, or protocol change complete,
  or when asked to "run gate", "check parity", "verify everything", /kongor-gate.
capability_contract:
  allowed_worker_roles: ["kongor-offline-worker", "kongor-game-worker"]
  allowed_tools: ["run_command", "view_file"]
  requires_worker_lock: false
  max_probes: 5
---

# Kongor Inner-Loop Gate & Parity Check (`kongor-gate`)

Run this verification sequence before declaring any feature, bug fix, UI change, or native modification complete. Total execution time is under **5 seconds**.

---

## Step 1: Execute the Sub-5s 6-Tier Quality Gate (`scripts/gate_check.ps1`)

Run the unified inner-loop gate script:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

Confirm all **6 Tiers** report `[PASS]`:
- **Tier 1 — `BannedSymbols`, PowerShell AST, Field-Note KB & MCP-Issue KB Scanner (< 0.8s):** Verifies 0 un-baselined sync-over-async anti-patterns (`Task.Result`, `.Wait()`, `Thread.Sleep()`, `.GetAwaiter().GetResult()`), 0 expired entries in `scripts/.banned_symbols_baseline.json`, 0 PowerShell AST syntax errors across `scripts/*.ps1`, 0 schema/index drift across `docs/reference/field-notes/` (`scripts/kongor_kb.ps1 -Action check`), and 0 schema/index drift across `docs/reference/mcp-issues/` (`scripts/kongor_mcp_issues.ps1 -Action check`).
- **Tier 2 — Client Lua Contract Test Suite (< 1.5s):** Runs `LuaVerifier.exe` across the modernization contract test suite (`test_phase6_modernization_suite.lua`) and verifies `PASSED > 0` and `FAILED == 0`.
- **Tier 3 — XML & VFS Syntax Lint (< 1.0s):** Validates well-formed XML across `games/client/override` and `games/client/ClientPackageFiles/ui`.
- **Tier 4 — NativeGuard (`version.dll` / `libk2_native_guard.so`) & VFS Mount Smoke Check (< 0.5s):** Confirms `version.dll` binary size (`>= 40,960` bytes) and `config/vfs_mounts.json` integrity.
- **Tier 5 — Cross-Tree Asset Parity (< 0.5s):** Verifies 0 drift between `games/client/override/` and tracked mirrors (`games/client/game/`, `games/client/verify_lua/`, `games/server/override/`, `games/server/game/` — hard fail) plus `games/client/ClientPackageFiles/` (advisory WIP), enforcing `trackedPairsCompared >= MinTrackedPairs`.
- **Tier 6 — Type Uniqueness, Symbol Wiring & Notice Verification (< 0.5s):** Verifies 0 un-baselined duplicate public types, required symbols wired, and `THIRD_PARTY_NOTICES.md` integrity.

If `scripts/gate_check.ps1` or `scripts/test_gate_mutants.ps1` was modified, run the hermetic mutant suite:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test_gate_mutants.ps1
```

---

## Step 2: Protocol & Metagen Parity Gate (When Touching Schemas / Shared Memory)

If `data/schemas/protocol.json`, `native/include/kongor_shared_mem.h`, `native/include/kongor_protocol.h`, `KongorProtocol.g.cs`, or `protocol.lua` were modified:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/re/test_metagen_parity.ps1
```

Confirm 0-drift code generation across C++, C#, and Lua contracts.

---

## Step 3: Field-Note & MCP-Issue Knowledge Base Promotion Trigger (`scripts/kongor_kb.ps1` & `scripts/kongor_mcp_issues.ps1`)

Whenever a reverse-engineering hypothesis, crash root cause, or VFS/rendering quirk in `docs/ACTIVE_SESSION_STATE.md` reaches `verified` status with a concrete RVA (`0x...`) or reproducible oracle:
1. Promote it into `docs/reference/field-notes/FN-NNN-*.md`:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_kb.ps1 -Action new -Title "<Title>" -Kind "<engine-law|crash-triage|vfs|rendering|netcode|technique>"
   ```
2. If the discovery invalidates an older note, update the older note's frontmatter (`status: superseded` or `invalidated`, `invalidated_by: "FN-NNN"`).
3. Rebuild and verify both knowledge base indexes:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_kb.ps1 -Action rebuild-index
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_kb.ps1 -Action check
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action check
   ```

---

## Step 4: K2 Architectural Hazard Checklist

Verify that none of the following Kongor-specific regressions were introduced:

1. **Native Hook Thread-Safety (`version.dll` / `NativeGuard` — `FN-001`, `FN-002`):**
   - `WndProc` handlers (`KongorSubclassWndProc`) check `GetCurrentThreadId() == GetWindowThreadProcessId(hWnd, NULL)` and `PostMessage` back if invoked on an external overlay thread (e.g., `graphics-hook64.dll`).
   - Engine commands from IPC/hooks use `CConsole::AddCmdBuffer` (queued to main thread), never synchronous `CConsole::Execute`.
2. **K2 UI & Rendering Footguns (`FN-003`):**
   - **No `textures.s2z` Shadowing:** New or resized UI textures whose legacy path exists in `games/client/game/textures.s2z` use an unshadowable versioned basename (e.g., `tab38_*.dds`).
   - **No `<modelpanel>` Animation Reset Stutter:** `<modelpanel>` tags in `.package`/`.interface` files do **not** use the XML `anim="idle"` attribute or unnecessary `scenebuffer="true"`; animations are initialized once via `onloadlua="self:SetAnim('idle')"` and `onshowlua="self:SetAnim('idle')"`.
3. **Hermetic AssetDB & Hard-Link Safety (`FN-005`, `FN-006`):**
   - AssetDB tools (`tools/assetdb/sync_record.py`, `pytest tools/assetdb`, `scripts/prebake-assets.ps1`) were only invoked with an explicit `--repo-root <temp_dir>` or `-OutputDir <scratch_dir>`.
   - Hard-linked client/server archives (`resources0.s2z`, `resources5.s2z`, `resources98.s2z`) and `textures.s2z` were not modified in-place.


---
name: kongor-review
description: >
  Quality and safety review of a Project Kongor change: verifies logic correctness,
  K2/Vulkan crash resilience, Lua frame-budget compliance (<= 1.0 ms), heavy telemetry/logging,
  VFS precedence, and lean implementation. Reads connected code, callers, and engine headers.
  Findings are structured in an actionable 4-part format. Use for "review this", "review my diff",
  "review override", "kongor review", /kongor-review.
capability_contract:
  allowed_worker_roles: ["kongor-offline-worker", "kongor-game-worker"]
  allowed_tools: ["view_file", "run_command"]
  requires_worker_lock: false
  max_probes: 5
---

# Kongor Review

Review a change like the engine engineer who will be paged when `hon_x64.exe` crashes or hitches.
Order of importance: **Correctness > Crash Safety > Heavy Telemetry > Frame Budget > VFS Integrity > Lean Implementation**.

This is a diagnostic and review report requested by the user. Report findings in full without modifying any code.

## 1. Understand First

- **Target Identification:**
  - If the user names files, an override folder, a branch, or a commit: review that target.
  - If nothing is named: review uncommitted changes in `games/client/override/` and git status (`git diff HEAD`).
- **Read Connected Code:**
  - Read the diff first, then the callers of every changed function (search with `Select-String`).
  - Check VFS mounting hierarchy: verify which archive or directory takes precedence (`override` > `base` > `game` > `ClientPackageFiles`).
  - For native/C++ or Frida hooks: check memory alignment, calling conventions (`__fastcall` / `__cdecl`), and null pointer dereference guards.
  - For Lua UI scripts: check tick update frequencies, event handlers, and global table pollution.

## 2. Review Checklist

1. **Crash & Memory Safety (Must Fix):**
   - Missing null/nil entity checks before indexing or native interop (prevents `0xC0000005` access violations).
   - **Native Hook Main-Thread & SEH Guard:** Any `WndProc` hook (`KongorSubclassWndProc`) or IPC handler in `version.dll` / `NativeGuard` must verify `GetCurrentThreadId() == GetWindowThreadProcessId(hWnd, NULL)` (re-posting via `PostMessage` if called from an injected overlay thread like `graphics-hook64.dll`), queue engine commands via `CConsole::AddCmdBuffer` rather than synchronous `CConsole::Execute`, and wrap trampoline hooks in `__try / __except` (SEH).
   - **No Per-Frame `loadfile` Polling:** Lua IPC/mailbox logic must use CVar change-signals (`tao_req`), never per-frame `loadfile` calls (which exhaust Lua's allocator in `luaV_concat`).
   - Archive injection corruption: verify zip headers, ensure no Zip64 overflow (>1.5 GB), and confirm `.bak` preservation.
   - Zero-deletion rule: ensure no pipeline step deletes or evicts files from `games/client/override/`.
   - **ABI Boundary Ownership Guard:** Ensure no scratch-arena allocated pointer (e.g. from `kongor_arena.h`) is returned or passed to K2 Engine functions that attempt to claim heap ownership (calling `delete` or `K2_Free`).
2. **Heavy Telemetry & Logging (Must Fix):**
   - Every state transition, packet handler, fallback branch, and error condition must have descriptive logging.
   - Code golf that strips logging or debug traces is strictly unacceptable.
3. **Engine Frame Budget & K2 Rendering Footguns (Must Fix / Should Fix):**
   - Lua tick handlers and UI update callbacks must execute within $\le 1.0\text{ ms}$ (to protect the 144 FPS / 6.9 ms frame budget).
   - No heavy string parsing, regex matching, or VFS disk queries inside per-frame update loops.
   - **No `<modelpanel>` XML `anim="..."` Stutter:** `<modelpanel>` tags in `.package`/`.interface` files must not use the static XML `anim="idle"` attribute (which causes `CModelPanel::Frame` at `k2_x64.dll + 0x951020` to call `StartAnim` every tick and freeze/stutter the skeleton) or unnecessary `scenebuffer="true"`; initialize animations once via `onloadlua="self:SetAnim('idle')"` and `onshowlua="self:SetAnim('idle')"`.
4. **VFS, Texture Shadowing & Cross-Tree Parity (Must Fix / Should Fix):**
   - Correct path casing and relative mount paths (K2 VFS is case-insensitive on Windows, but case-sensitive in zip entries).
   - **No `textures.s2z` Shadowing:** When overriding UI textures whose legacy path exists in `games/client/game/textures.s2z` (`00000000/ui/...`), use a versioned, unshadowable basename (e.g. `tab38_*.dds`) so `CResourceManager::RegisterTexture` cannot shadow the override.
   - **Cross-Tree Lua Parity (`override/` $\leftrightarrow$ `ClientPackageFiles/`):** Shared scripts under `ui/scripts/newui/` must remain byte-identical across `override/` and `ClientPackageFiles/` (`Compare-Object` 0 diffs).
   - **Hermetic AssetDB & Hard-Link Guard:** Ensure AssetDB/batch scripts (`sync_record.py`, `prebake-assets.ps1`) require explicit `--repo-root <temp>` or `-OutputDir <scratch>` and never mutate hard-linked client/server archives (`resources0/5/98.s2z`) or `textures.s2z` in-place.
5. **Logic Bugs & Edge Cases:**
   - Empty lists, nil parameters, unhandled network disconnects, mid-game player reconnections.
   - When modifying shared UI functions (e.g. in `newui/`), confirm callers across all interfaces still work.
6. **Lean Implementation (YAGNI):**
   - Delete dead code, unused options, and speculative wrappers.
   - Reuse existing engine cvars and K2 helper functions before writing custom utilities.
7. **Reconstruction Obligations & Evidence Dossiers (Must Fix):**
   - If native K2 binaries, network packets, or memory offsets are touched, verify that an active Evidence Dossier v2 exists in `artifacts/evidence/`.
   - Run `.\scripts\verify_evidence_obligations.ps1 -Files <changed-files>` to confirm that all OBL-XX requirements are satisfied in cleanroom code and covered by tests.
   - Check that no opaque pointers or struct offsets were blindly assumed without dynamic Frida/WinDbg verification (or documented `waived` rationale).

## 3. Finding Format

Every finding must provide a concrete reproducible scenario: *"Under condition X, action Y causes failure Z"*.
Group findings into three numbered categories (skip empty categories):
- **Must fix:** Crash risk, data loss, frame budget hitches, missing error telemetry, VFS precedence collisions.
- **Should fix:** Risky code without assert/validation, duplicate helpers, unnecessary wrappers.
- **Nice to have:** Micro-cleanups, clearer comments.

Every finding MUST follow this exact 4-part structure:

`N.` **<Short Descriptive Title>** (`<path/to/file>:<lines>`)
- **What this is:** What this code does in 1-2 short sentences.
- **Problem:** Exactly what goes wrong and why.
- **Fix:** The smallest complete fix that works (PowerShell native, preserves logging).
- **If we skip it:** The exact runtime impact (e.g., client crash, 15ms frame hitch, silent failure).

## 4. Output Summary

End the review with:
- `Verdict: Ship.` OR `Verdict: Fix <N> and <M> first.`
- `Telemetry: Verified (all error paths and state transitions logged).`
- `Frame Budget: Verified (no hot-loop regressions detected).`
- `Lean: -<N> lines possible (if simplification opportunities exist).`
- `Not checked:` State any engine subsystem, binary, or external tool that could not be verified statically.

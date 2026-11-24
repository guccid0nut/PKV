---
name: kongor-ui-ux
description: >
  K2 Engine UI/UX development, architecture, and live runtime verification playbook.
  Covers .interface and .package structure, XML widget trees, Lua UI script optimization
  (frame budget <= 1.0 ms), event triggers, avoiding <modelpanel> stutter and per-frame
  loadfile OOMs, live hot-reloading (ReloadInterfaces), and golden screen visual diffing.
  Use when developing, modifying, styling, or debugging K2 UI interfaces, widgets, and menus.
capability_contract:
  allowed_worker_roles: ["kongor-offline-worker", "kongor-game-worker"]
  allowed_tools: ["run_command", "view_file", "replace_file_content", "write_to_file"]
  requires_worker_lock: false
  max_probes: 5
---

# K2 Engine UI/UX Development & Runtime Verification (`kongor-ui-ux`)

Governs XML widget authoring, Lua 5.1 frame-budget compliance, `textures.s2z` cross-extension shadow prevention, live hot-reloading, and semantic visual verification across Project Kongor.

---

## 1. K2 UI Architecture: Interfaces, Packages & Widgets

K2 UI combines declarative XML hierarchies with event-driven Lua 5.1 scripts:
- **`.interface` (Root Containers):** Top-level screens (`main_v2.interface`, `game.interface`, `lobby.interface`) declaring `<interface>` bounds and importing `.package` components via `<include>` / `<package>`.
- **`.package` & `<template>`:** Reusable widget trees (`store2.package`, `game_options.package`) instantiated with `<instance name="..." />`.
- **Cross-Tree Parity:** Keep modified UI files synchronized across `games/client/override/ui/`, `games/client/ClientPackageFiles/ui/`, `games/client/game/ui/`, and `games/client/verify_lua/ui/` as checked by `scripts/gate_check.ps1`.

| Widget Tag | Role | Key Practice |
|---|---|---|
| `<panel>` | Layout container | Anchor via `align`/`valign`, flow via `float`, clip via `clip="true"`. |
| `<image>` | 2D texture quad | Use versioned basenames (`icon_v2.png`) whenever `<stem>.dds` exists in `textures.s2z` (`CTextureArchive` strips extensions at `vid_d3d9_x64.dll + 0x4bb80`). |
| `<button>` | Interactive control | Wire `onclicklua` / `onmouseoverlua`. Defer `ClearChildren()` on parent containers via `Sleep(1, ...)` on a persistent static widget so `IWidget::ProcessInputMouseButton` (`k2_x64.dll + 0xC0C545`) unwinds safely without Use-After-Free. |
| `<label>` | Localized text | Bind via `content="<string_key>"` or `:SetText(...)`. |
| `<modelpanel>` | 3D `.mdf` viewport | Initialize animations via `onloadlua="self:SetAnim('idle')"` and `onshowlua="self:SetAnim('idle')"` with `scenebuffer="false"` (avoid static XML `anim="idle"`, which `CModelPanel::Frame` at `k2_x64.dll + 0x951020` restarts every tick — `FN-003`). Keep instances persistent via `:SetVisible(true/false)` + `:SetModel(...)` to avoid DXVK v3.0.2 pipeline stalls. |

---

## 2. Main-Thread Frame Budget & Memory Safety

K2 UI callbacks run synchronously on the main game thread (`144 FPS` = `6.9 ms` total frame budget):
- **$\le 1.0\text{ ms}$ Callback Budget:** Keep individual UI update/tick handlers under $\le 1.0\text{ ms}$; emit telemetry warnings on any hitch exceeding $5.0\text{ ms}$.
- **Event-Driven Updates Only (Zero Per-Frame `loadfile`):** Drive Lua updates via CVar change signals (`tao_req`), `Watch` / `Unwatch` triggers, and widget events. Calling `loadfile()` or `dofile()` per frame exhausts Lua's allocator and triggers a null-dereference crash in `luaV_concat` (`k2_x64.dll + 0xCB6477`).

---

## 3. Zero-Restart Hot-Reloading & Canary Observation

Write candidate edits to `games/client/override/ui/<path>` and hot-reload live via the K2 Developer Console (`Ctrl + F8`, registered in `CInput::Init` at `k2_x64.dll + 0x8F71F0`), the RmlUi Dev Cockpit (`Ctrl + Shift + D`), or MCP IPC:

| Surface | Target | Command |
|---|---|---|
| **Console / IPC** | All Interfaces | `ReloadInterfaces` |
| **Console / IPC** | Single Interface | `ReloadInterface <name>` (e.g. `ReloadInterface main`) |
| **Console / IPC** | Single Package/File | `UICmd("ReloadFile('/ui/fe2/store2.package')")` |
| **MCP Tool** | Active Client UI | `kongor_hot_reload` / `kongor_client_eval_lua` |

- **30–60s Canary Window:** After every hot-reload, inspect `console.log` and `client_lua_debug.log` (or run `scripts/triage_client_logs.ps1`) for 30–60 seconds to catch delayed Lua errors, missing texture lookups, or nil callback references.

---

## 4. Golden Screen Diffing & Semantic Proof ("Real Proof or No Proof")

1. **Assert Engine State Before Capture:** Query `kongor_game_client_manage action: 'status'` or `kongor_client_eval_lua` before calling `kongor_capture_window`:
   - *In-Game HUD:* Require `gamePhase == 5` (`isActiveMatch == true`), hero spawned, and `widget:IsVisible() == true`.
   - *Front-End / Store / Vault:* Require authenticated lobby (`gamePhase == 1`, `GetWidget('main_top_bar'):IsVisible() == true`) and target panel visible.
   - *Modals:* Require `modal:IsVisible() == true` in front.
2. **Inspect & Diff:** Reject any capture showing the Empath login screen, "Login Failed" modal, or blank transition (`python scripts/verify_proof_screenshot.py --image <path> --expected <feature_type>`). Compare before/after captures with `kongor_ui_visual_diff` / `kongor_assert_visual`.

---

## 5. Completion Criteria

Every UI change is complete only when all four offline gates pass with exit code `0`:

```powershell
# 1. XML & Interface Schema Lint
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_xml_syntax.ps1

# 2. Lua 5.1 Bytecode & Contract Verification
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_client_lua.ps1

# 3. UI Texture Stem-Shadowing Audit (Filter to modified UI subpath)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/assets/audit_k2_textures.ps1 -ChannelMode UIRgba -Filter <ui_subpath> -Strict

# 4. Full 6-Tier Repository Quality Gate & Parity Check
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

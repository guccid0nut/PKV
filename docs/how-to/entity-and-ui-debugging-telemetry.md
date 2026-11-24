# How-To: Fast `.entity` & K2 UI Debugging with Built-In Engine Telemetry

This guide provides copy-pasteable workflows for debugging `.entity` abilities/items and `.interface`/`.package`/`.lua` UI changes in seconds without restarting `hon_x64.exe` or flooding `console.log`.

---

## 1. Launch Practice Mode with Scoped Telemetry

[`scripts/launch_screen_practice.ps1`](../../scripts/launch_screen_practice.ps1) exposes opt-in switches backed by verified `k2_x64.dll` CVars:

```powershell
# Live .entity / resource auto-reload on file save (host_dynamicResReload=true)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/launch_screen_practice.ps1 -Hero Hero_Javaras -DynamicResReload

# Scoped UI Lua tracing for ONE interface (avoids global console.log spam and preserves <= 1.0ms frame budget)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/launch_screen_practice.ps1 -DebugUiScriptInterface "main"

# Scoped UI Lua tracing for ONE specific widget + live cursor widget inspector
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/launch_screen_practice.ps1 -DebugUiScriptWidget "store_container2" -DebugHoverWidget
```

> [!WARNING]
> Avoid enabling `Set ui_debugScript true` globally without setting `ui_debugScriptInterface` or `ui_debugScriptWidget`. Global `ui_debugScript` logs every per-frame widget script across all loaded interfaces, creating multi-megabyte `console.log` churn and frame hitches.

---

## 2. Verified Hot-Reload Commands (`k2_x64.dll` & `cgame_x64.dll`)

Live introspection (`cmdlist reload` and `uicmdlist reload` via [`scripts/vfs/sync_k2_engine_catalogs.ps1`](../../scripts/vfs/sync_k2_engine_catalogs.ps1)) confirmed the exact reload commands compiled into our client binaries:

| Command | Scope | What It Reloads |
| :--- | :--- | :--- |
| `ReloadInterface <name>` | Global Console | Reloads a single UI interface (e.g. `ReloadInterface main`) in `< 200ms`. |
| `ReloadInterfaces` | Global Console | Reloads all loaded `.interface`, `.package`, and UI `.lua` scripts. |
| `ReloadRes` | Global Console | Reloads modified engine resources managed by `CResourceManager`. |
| `ReloadSelected` | Global Console (`cgame_x64.dll`) | Reloads entity definitions for the currently selected in-game unit. |
| `UICmd("ReloadFile('<vpath>')")` | UI Script (`uicmdlist`) | Reloads a specific VFS resource path from Lua/UI context. |
| `ReloadModels` / `ReloadMaterials` / `ReloadShaders` / `ReloadTextures` / `ReloadFontMaps` / `ReloadSamples` | Global Console | Reloads specific rendering or audio asset classes in-place. |
| `Set host_dynamicResReload true` | CVar (`k2_x64.dll`) | Automatically hot-reloads modified `.entity` files in `games/client/override/` upon save in Practice Mode (does not auto-reload `.str` stringtables; remake practice match if cached item state persists). |

> [!NOTE]
> Legacy forum guides occasionally mention `ReloadEntity` or `res_checkModified`. Binary UTF-16 scanning and live `cmdlist` / `cvarlist` verification (`scripts/vfs/sync_k2_engine_catalogs.ps1`) confirmed neither symbol exists in `k2_x64.dll`, `cgame_x64.dll`, `game_x64.dll`, or `game_shared_x64.dll` (`+Set res_checkModified false` merely allocates an unused dynamic string CVar `[S A]`). Use `ReloadRes`, `ReloadSelected`, `ReloadInterface`, `ReloadInterfaces`, or `UICmd("ReloadFile(...)")` instead.

---

## 3. UI Widget, Trigger & Lua API Introspection

Open the in-game console (`Ctrl+F8`) or send commands over Named Pipe IPC (`scripts/qa/invoke_k2_lua.ps1`):

- **Live Cursor Widget Inspector:** `Set ui_debugHoverWidget true` displays the exact interface, widget name, template origin, and pixel bounds under the mouse cursor.
- **Dump UI Triggers:** `uitriggerlist <filter>` (e.g. `uitriggerlist HeroSelect` or `uitriggerlist Store`) lists native C++ UI triggers and parameter layouts.
- **Dump UI Commands:** `uicmdlist <filter>` lists all `UICmd(...)` functions exposed to XML/Lua.
- **Dump Lua Functions:** `luafunctions <filter>` lists exposed global/engine Lua bindings.
- **List Active Widgets:** `ListWidgets` / `ListWidgetsInActiveInterface` dumps live widget trees in `k2_x64.dll`.

---

## 4. `.entity` Combat Script & 3D World Debugging

### A. Inline `.entity` Stack & Position Tracing
Drop leaf debug tags inside any `<onimpact>`, `<onframe>`, or `<ondamaged>` handler in `games/client/override/heroes/<alias>/.../*.entity` (see [`k2-entity-xml-schema.md`](../reference/k2-entity-xml-schema.md)):

```xml
<onimpact>
    <printdebuginfo />
    <setvar0 a="target_baseintelligence" b="source_level" op="mult" />
    <printvalue label="scaled_mana_burn" value="var0" />
    <printpos label="impact_pos" value="target_position" />
    <drawdebugline start="source_position" end="target_position" arrow="true" color="red" />
    <drawdebugx pos="target_position" color="yellow" />
</onimpact>
```

### B. 3D Hitbox, Affector & Navigation Overlays
Run these verified CVars in Practice Mode (`cheats true` / `dev true`):

| Console Command / CVar | Binary | Visual Overlay |
| :--- | :--- | :--- |
| `Set cg_drawDebugLines true` | `game_shared_x64.dll` | Renders `<drawdebugline />` and `<drawdebugx />` vectors in 3D world space. |
| `Set d_drawAreaAffectors true` | `game_shared_x64.dll` | Draws exact AoE rings/cones for active `<affector>` entities. |
| `Set d_drawProjectileRadiuses true` | `game_shared_x64.dll` | Draws collision spheres around active `<projectile>` entities. |
| `Set vid_drawModelBounds true` | `k2_x64.dll` | Draws 3D model bounding boxes (`vid_drawBones true` / `vid_drawBoneNames true` for skeleton bones). |
| `Set cg_drawNavGrid true` | `cgame_x64.dll` | Renders pathfinding grid (`cg_drawNavGridTrees`, `cg_drawNavGridCliffs`, `cg_drawNavGridUnits`, `cg_drawNavGridBuildings`). |
| `Set cg_drawCursorWorldPos true` | `cgame_x64.dll` | Displays live 3D terrain coordinates at the cursor. |
| `GameBind Impulse PGUP Ruler` | `k2_x64.dll` | Binds `PageUp` to an interactive in-world distance ruler (`0..N` K2 units). |
| `ClientSpawnUnit Hero_Bubbles.Alt16` | `cgame_x64.dll` | Spawns a client-side unit/Alt Avatar immediately to audition voice lines and model effects. |

---

## 5. Pre-Flight Validation & Troubleshooting Silent Failures

Always run the XML/entity validator before hot-reloading:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_xml_syntax.ps1
```

### Common Silent Failure Modes Caught Automatically
1. **Hero Disappears Silently from Hero Selection Grid:** Caused by malformed XML syntax in `hero.entity` or a broken ability reference (`ability0`..`ability3` pointing to a missing/corrupt `ability.entity`). Run `scripts/verify_xml_syntax.ps1 -Path games/client/override/heroes/<hero>` to pinpoint the exact line.
2. **Child Actions Inside `<damage>` / `<applystate>` Never Execute:** Leaf combat actions (`END_XML_PROCESSOR_NO_CHILDREN`) silently ignore child elements. `verify_xml_syntax.ps1` flags `[K2-ENTITY-LEAF]`.
3. **Single-Line `--` Comment Inside `onloadlua="..."` Breaks Remaining UI Code:** XML attribute normalization collapses newlines into spaces, turning `-- comment` into a rest-of-attribute comment. Always use `--[[ comment ]]` inside XML attributes (`verify_xml_syntax.ps1` flags this automatically).
4. **Blurry Custom Item/Ability Icons on x64 Client:** K2 x64 downscales non-power-of-two textures (`128x135`) with blur artifacts; always author UI/item icons at power-of-two dimensions (`64x64`, `128x128`, `256x256`).

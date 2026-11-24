---
id: FN-013
kind: rendering
title: "RmlUi LayoutEngine Flexbox Positioning and Direct-Nesting Prohibitions"
binary_or_subsystem: "VkLayer_hon_replay_capture.dll | RmlUi LayoutEngine | assets/hud/devtools.rml"
rvas: ["0x000000"]
status: verified
supersedes: []
invalidated_by: ""
date: 2026-10-26
tags: [rmlui, vulkan-layer, flexbox, devtools, overlay, hon-modern-hud]
---

# FN-013: RmlUi LayoutEngine Flexbox Positioning and Direct-Nesting Prohibitions

## Summary
Unlike CSS in web browsers, the embedded RmlUi C++ layout engine compiled into `VkLayer_hon_replay_capture.dll` (`native/deps/rmlui/Source/Core/LayoutEngine.cpp` & `LayoutFlex.cpp`) aborts formatting with `0x0` geometry—producing completely invisible UI subtrees even when the document is visible and hotkeys fire—whenever an element combines `display: flex` (or `table`) with `position: absolute`/`fixed` or `float`, places `display: flex` on `<body>`, or nests a `display: flex` child directly inside another `display: flex` parent without an intermediate `display: block` wrapper `<div>`. Additionally, `OverlayManager::Initialize` in `native/vulkan-layer/rmlui/overlay_manager.cpp` requires `HON_MODERN_HUD=1` (or `HON_DEVTOOLS=1` when `KONGOR_HEADLESS!=1`) in the child process environment to initialize RmlUi and bind `Ctrl+Shift+D`.

## Symptom -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **Symptom** | Pressing `Ctrl+Shift+D` in `hon_x64.exe` logs `Hotkey Ctrl+Shift+D detected! Toggling DevTools overlay.` and `Document 'devtools' toggled to 1`, yet nothing renders on screen (`0x0` geometry) and `rmlui_overlay.log` / `hon_vulkan_layer.log` emits `[WARN] [RmlUi] Flex elements cannot be absolutely positioned... Element will not be formatted: div#devtools-window` or `Element with display type 'flex' cannot be nested inside a flex container`. Separately, MCP/CLI foreground launches that set `HON_DEVTOOLS=1` without `HON_MODERN_HUD=1` skipped `OverlayManager::Initialize()` entirely. |
| **Root Cause** | (1) `LayoutEngine::FormatElement` (`LayoutEngine.cpp:188-220`) rejects any `Flex` or `Table` element with `Position::Absolute`, `Position::Fixed`, or `Float != None` and returns early before layout. (2) `LayoutFlex::Format` (`LayoutFlex.cpp:832`) invokes root-level `LayoutEngine::FormatElement(item.element, ...)` -> `ValidateTopLevelElement(item.element)` (`LayoutEngine.cpp:60-85`), which aborts layout on `<body>` with `display: flex` and on any direct child of a `display: flex` parent that also has `display: flex`. (3) `OverlayManager::SwitchTab` (`overlay_manager.cpp:1394-1400`) overwrites tab button classes via `SetClassNames("tab-btn")`, stripping positional utility classes unless anchored by ID (`#devtab-btn-actions`). (4) `HonLayerLog` (`layer_manifest.h:98`) buffered `INFO`/`WARN` lines in CRT stdio until exit. |
| **Fix / Workaround** | (1) Make `#devtools-window` `position: absolute; display: block;` and host the outer flex column on an inner `.devtools-shell` (`width: 100%; height: 100%; display: flex; flex-direction: column;`). (2) Strictly alternate `flex -> block -> flex -> block` across all RmlUi layouts using `display: block` slot wrappers (`.header-slot`, `.tabs-slot`, `.tab-pane`, `.row-slot`, `.deck-slot`, `.footer-slot`) and `display: block; flex: 1;` leaf cards/buttons. (3) Unify `HON_MODERN_HUD` + `HON_DEVTOOLS` (with `KONGOR_HEADLESS` guard) in `OverlayManager::Initialize` and `GameLauncherService.ConfigureVulkanOverlayEnvironment`, isolate child `lpEnvironment` construction in `InteractiveProcessLauncher.BuildUnicodeEnvironmentBlock`, and flush non-`DEBUG` lines immediately in `HonLayerLog`. |

## Verification Oracle
Exact commands that prove the RmlUi AST layout invariants, Vulkan layer env unification, and `kongor-cli` launch environment rules hold:
```powershell
python -m unittest tests/unit/test_native_rmlui_dev_hud.py -v
dotnet test tests/unit/tools/Kongor.Mcp.Tests/Kongor.Mcp.Tests.csproj --filter "FullyQualifiedName~VulkanOverlayLaunchEnvTests"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

## Prevention Invariant
1. Never combine `display: flex` (or `display: table`) with `position: absolute`, `position: fixed`, or `float` in any `.rml`/`.rcss` file, and never nest a `display: flex` element directly inside another `display: flex` element without an intermediate `display: block` wrapper `<div>`.
2. Keep CSS `gap`, `box-shadow`, `letter-spacing`, and non-ASCII (`>= 128`) characters out of RmlUi `.rml`/`.rcss` files (`FontEngine_STB` rasterizes ASCII `32..127` only), and duplicate styles inside `<head><style>` when live `ReloadDocuments()` support is needed (`StyleSheetFactory` caches external `.rcss` sheets).
3. Always run `python -m unittest tests/unit/test_native_rmlui_dev_hud.py` after editing any RmlUi HUD asset to verify 3-way mirror parity (`assets/hud/`, `games/client/Assets/hud/`, `games/client/windows-x64/assets/hud/`) and `LayoutEngine` tree compliance across all tab states.

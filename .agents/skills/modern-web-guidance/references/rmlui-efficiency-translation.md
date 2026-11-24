# RmlUi Efficiency Translation & `FN-013` Engine Firewall

Project Kongor embeds **RmlUi** inside `VkLayer_hon_replay_capture.dll` (`native/vulkan-layer/rmlui/*` + 3-way mirrored HUD assets at `assets/hud/`, `games/client/Assets/hud/`, and `games/client/windows-x64/assets/hud/`).

RmlUi implements a custom C++ layout and Vulkan 1.3 render pipeline (`native/deps/rmlui/Source/Core/LayoutEngine.cpp`), **NOT** Chromium/Blink. Applying browser-specific HTML/CSS APIs directly to `.rml`/`.rcss` files causes silent `0x0` geometry aborts (`FN-013`). Use the translation matrix below whenever optimizing RmlUi overlays.

---

## 1. Hard Syntax Prohibitions (`FN-013` Firewall)

Never emit any of the following browser patterns inside `.rml` or `.rcss` files:

| Browser Feature (`modern-web-guidance`) | RmlUi Engine Behavior | Safe RmlUi Pattern |
| :--- | :--- | :--- |
| `position: absolute`/`fixed` or `float` on `display: flex` (or `table`) | `LayoutEngine::FormatElement` aborts formatting early (`0x0` invisible subtree). | Keep outer positioned window `position: absolute; display: block;` and put `display: flex` on an inner `.shell` `<div>`. |
| Direct `display: flex` child inside `display: flex` parent (or `body { display: flex }`) | `ValidateTopLevelElement` aborts child layout (`0x0` geometry). | Strictly alternate `flex -> block -> flex -> block` using `display: block` slot wrappers (`.row-slot`, `.header-slot`, `.tab-pane`). |
| `<dialog>`, `popover`, `anchor-name`, `@container`, `display: grid`, `subgrid`, `:has()`, `@starting-style`, `content-visibility` | Unrecognized tags/at-rules/properties; silently ignored or layout-broken. | Manage visibility in `OverlayManager::SetDocumentVisible` / `SwitchTab` and position overlays via `position: absolute` `display: block` containers. |
| CSS `gap`, `box-shadow`, `letter-spacing` | Unsupported/prohibited in Kongor RmlUi RCSS (`test_native_rmlui_dev_hud.py` fails). | Use explicit `margin-right` / `margin-bottom` (with `margin-right: 0` on `-last` items) and `border-width` / `border-color`. |
| Non-ASCII UTF-8 characters (`>= 128`: emojis, `•`, `—`, arrows) | `FontEngine_STB` rasterizes ASCII `32..127` only; non-ASCII glyphs drop or render blank. | Restrict text in `.rml` and `OverlayManager` strings to printable ASCII `32..126` (`EscapeRmlText`). |
| External-only `.rcss` edits expecting live hot-reload | `StyleSheetFactory` caches external `.rcss` sheets across `ReloadDocuments()`. | Mirror active rules inside `<head><style>` in `.rml` AND keep all 3 asset mirrors (`assets/hud/`, `games/client/Assets/hud/`, `games/client/windows-x64/assets/hud/`) byte-identical. |

---

## 2. Rendering & Process Efficiency Translations (Browser -> Vulkan RmlUi)

While browser *syntax* differs, the underlying **rendering-pipeline physics** from `modern-web-guidance` translate directly into high-leverage C++ / Vulkan RmlUi wins on the `144–500+ FPS` (`<= 6.94 ms`) swapchain thread:

### A. `efficient-background-processing` & `defer-rendering-heavy-content` -> Active-Document & Active-Tab Gating
- **Browser Principle:** Pause off-screen WebGL/DOM work (`IntersectionObserver`, `content-visibility: auto`, `document.visibilityState`).
- **RmlUi Equivalent (`overlay_manager.cpp`):**
  1. Fast-exit `OverlayManager::UpdateAndRender` via `HasAnyVisibleDocument()` before acquiring fences or recording Vulkan command buffers when all overlays are hidden.
  2. Inside `OverlayManager::UpdateTelemetryUI()`, gate per-tab `GetElementById` + `SetInnerRML` mutations behind `m_activeTab` so hidden tabs (`display: none`, e.g., `#devtab-audio`, `#tab-fleet`, `#tab-vulkan`) incur **zero DOM/layout work** until activated—hydrating immediately inside `SwitchTab()` on tab change.

### B. `interactions-in-complex-layouts` -> Dirty-Value & Sequence Diffing Before `SetInnerRML`
- **Browser Principle:** Avoid unnecessary DOM mutations and forced synchronous layout reflows in data-dense dashboards.
- **RmlUi Equivalent (`overlay_manager.cpp`):**
  - Calling `Element::SetInnerRML()` unconditionally at `5 Hz` destroys child nodes, runs RmlUi's XML parser, re-shapes glyphs in `FontEngine_STB`, invalidates compiled geometry handles in `RmlUi_RenderInterface_VK`, and triggers `LayoutEngine::FormatElement` even when text has not changed.
  - Cache the last written string/class per element ID (`SetInnerRMLIfChanged` / `SetClassNamesIfChanged`) and compare a monotonic log sequence ID before rebuilding `#tao-log-terminal`. Static ticks drop to **zero layout passes and zero Vulkan vertex re-uploads**.

### C. `move-dom-element-without-losing-state` / Incremental DOM -> Bounded Child Append Over `GetInnerRML()`
- **Browser Principle:** Preserve existing DOM nodes instead of serializing and re-parsing entire containers (`innerHTML += ...`).
- **RmlUi Equivalent (`ExecuteCommandDirect`):**
  - Replace `curRml = elTerm->GetInnerRML(); curRml += ...; elTerm->SetInnerRML(curRml)` ($O(N)$ full serialization + XML re-parse) with incremental child element creation (`AppendChild`) and ring eviction (`RemoveChild(GetFirstChild())` above line cap).

### D. `individual-transform-properties` & `motion` -> Vulkan Uniform `transform` Over Box-Model `width`
- **Browser Principle:** Animate/update composited transform properties (`scaleX`, `translate`) rather than layout-triggering geometry (`width`, `height`, `left`, `top`).
- **RmlUi Equivalent (`rmlui_render_vk.cpp` + `.rcss`):**
  - Mutating `elBar->SetProperty("width", pct + "%")` dirties box-model layout. Fixing `width: 100%; transform-origin: left center;` on a block bar and updating `transform: scaleX(0.XX)` maps directly to `m_descriptorSetTransform` (`Rml::Matrix4f`) in `RmlUi_RenderInterface_VK` with **zero `LayoutEngine` reflow**.

---

## 3. Mandatory Verification Gate for Any RmlUi Change

```powershell
python -m unittest tests/unit/test_native_rmlui_dev_hud.py tests/unit/test_native_rmlui_motd.py -v
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate_check.ps1
```

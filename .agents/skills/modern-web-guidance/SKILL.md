---
name: modern-web-guidance
description: >
  Chrome Modern Web Guidance search and modernization workflow for Project Kongor web UIs
  (DAWNBRINGER src/services/web-ui, tools/spectator-console, tools/ui-playground) plus
  engine-safe rendering efficiency translations for Vulkan RmlUi overlays. Use FIRST when
  building, auditing, or optimizing HTML, Razor .cshtml, CSS/Tailwind, client-side JS/WebGL,
  modals/dialogs, popovers, forms, tables, animations, Core Web Vitals (LCP, INP, CLS),
  content-visibility, or RmlUi overlay rendering efficiency. Do NOT apply browser HTML/CSS
  syntax to K2 .interface/.package XML (use kongor-ui-ux) or raw RmlUi without FN-013 translation.
capability_contract:
  allowed_worker_roles: ["kongor-offline-worker", "kongor-game-worker"]
  allowed_tools: ["run_command", "view_file", "replace_file_content", "write_to_file"]
  requires_worker_lock: false
  max_probes: 10
---

# Modern Web Guidance (Kongor Web & RmlUi Efficiency Edition)

Embeds Google Chrome's **Modern Web Guidance** (124+ guides across 99 modern web features) into Project Kongor with `< 50 ms` offline lookup and a strict 3-runtime boundary so browser APIs never corrupt native RmlUi or K2 UI layouts.

---

## 0. Mandatory Runtime Router (Check Target First)

Before retrieving or applying any guide, classify the target file path:

1. **Browser Web Targets (Full Chrome `modern-web-guidance` Applies):**
   - **DAWNBRINGER Web Portal:** `src/services/web-ui/` (`.cshtml` Razor views, `Styles/input.css`, `wwwroot/css/*.css`, `wwwroot/js/*.js`)
   - **Browser Companion Apps:** `tools/spectator-console/`, `tools/ui-playground/static/`
   - Follow **Primary Workflow (Steps 1–4)** below in full.
2. **In-Game Vulkan RmlUi Overlay (`native/vulkan-layer/rmlui/*`, `assets/hud/*.{rml,rcss}`):**
   - RmlUi is a custom C++/Vulkan layout engine, **NOT** Chromium. Browser tags/CSS (`<dialog>`, `popover`, `@container`, `grid`, `:has()`, `gap`, `box-shadow`, `flex->flex`, `position: absolute` + `display: flex`, non-ASCII `>= 128`) abort layout with **`0x0` invisible geometry (`FN-013`)**.
   - Read [references/rmlui-efficiency-translation.md](references/rmlui-efficiency-translation.md) first, retrieve performance/motion guides (`efficient-background-processing`, `defer-rendering-heavy-content`, `interactions-in-complex-layouts`, `individual-transform-properties`) for conceptual physics only, and implement strictly via the engine-safe C++/RCSS patterns in that reference.
3. **K2 Native XML / Lua 5.1 UI (`games/client/override/ui/*.interface`, `*.package`, `*.lua`):**
   - Stop using this skill and route to `kongor-ui-ux` and `performance-optimization`.

---

## 1. Primary Workflow (Browser Web Targets)

### Step 1: Search Use Cases (`< 50 ms` Offline Wrapper)

Query the local guidance index using a **2–5 word generic capability query** (`<verb> + <UI/platform primitive>`, omitting framework or domain nouns like `Razor`, `Tailwind`, `Dawnbringer`, `Hero`):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev/query_modern_web_guidance.ps1 -Action search -Query "<query>"
```

- Split multi-feature tasks into separate `-Action search` calls (e.g., `"light dismiss dialog"`, `"defer rendering heavy content"`, `"pause offscreen canvas"`).
- Name implied UI primitives (e.g., search `"confirmation modal dialog"` rather than `"confirm store purchase"`).
- If results are empty or weak, rephrase once with the concrete HTML/CSS noun (`dialog`, `popover`, `table`, `form`, `scrollbar`) or list all IDs via:
  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev/query_modern_web_guidance.ps1 -Action list
  ```

### Step 2: Retrieve Best Practices

Fetch full Markdown guides for matching use-case IDs **plus** relevant discipline guides (`html`, `css`, `css-layout`, `forms`, `accessibility`, `color`, `performance`, `security`, `motion`):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev/query_modern_web_guidance.ps1 -Action retrieve -Id "<id1>,<id2>"
```

If output exceeds 9.5 KB and writes to a temporary file path, read that file in full with `read_file`.

### Step 3: Apply & Verify Kongor Browser Policy Compliance

Cross-check your implementation against the retrieved guide and Project Kongor's web constraints:
- **Kongor Browser Baseline Policy (`ADR-0004` Zero-npm Rule):**
  - **Baseline Widely Available** features (`<dialog>`, `:has()`, `@container` size queries, `loading="lazy"`, individual `translate`/`scale`/`rotate`, `:user-invalid`, `IntersectionObserver`, `ResizeObserver`) are approved without fallbacks.
  - **Baseline Newly Available / Limited** features (`popover`, `content-visibility`, `fetchpriority`, `@starting-style`, `transition-behavior: allow-discrete`, `<dialog closedby="any">`, `@view-transition`, `<script type="speculationrules">`, CSS Anchor Positioning, Invoker Commands `commandfor`/`command`, `scheduler.yield()`, `Element.setHTML()`) are encouraged via **progressive enhancement** (safe no-op ignore where native degradation is harmless, or `@supports` / `if ('feature' in ...)` with `<= 20` lines of vanilla fallback). **Never** introduce `package.json` or `node_modules` polyfills into `src/services/web-ui/`.
- **Tailwind CSS v4 Rebuild:** Whenever classes or custom utilities change in `src/services/web-ui/`, rebuild `wwwroot/css/tailwind.css` via `./kongor.ps1 tailwind build`.

### Step 4: Disclose Skill Usage

End any response that delivers web or RmlUi changes informed by this skill with a `### Modern Web Guidance` section:
- One concise bullet (`<= 75` words) per applied `<id>`:
  `- **\`<id>\`**: <APIs, CSS overrides, or engine-safe translations applied and pitfall prevented> ("<short verbatim quote of the specific rule or gotcha from the retrieved guide>").`
- Never guess `<id>`s or quotes from memory; re-run `-Action retrieve` if needed.

---

## 2. Codebase Modernization & Legacy Pattern Scan

Only when asked for a broad, open-ended codebase audit or legacy-pattern scan across a web directory (never for single-component edits, and never on `.rml`/`.interface` directories), run the 4-step modernization flow:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev/query_modern_web_guidance.ps1 -Action legacy-scan -Path src/services/web-ui -Json
```

1. **Scan:** Run `-Action legacy-scan -Path <web_dir> -Json` (covers deterministic rules for ~20% of the catalog) AND supplement with targeted `-Action search` / `-Action retrieve` across `performance`, `ui-components`, `ui-behaviors`, `css`, `forms`, `accessibility`, and `security` for the remaining ~80%.
2. **Verify:** Check every static finding against its `keepWhen` / `dropWhen` rubric; discard false positives (vendor bundles, intentional progressive-enhancement fallbacks).
3. **Report:** Present ranked, verified findings with exact file/line citations, `<id>` mappings, and measurable runtime impact.
4. **Modernize (once approved):** Retrieve each finding's guide, apply the replacement, re-run `-Action legacy-scan` on modified files, and pass the target test suite.

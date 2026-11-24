# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
test_native_rmlui_dev_hud.py
Unit test suite for the RmlUi In-Game Developer & Spectator Cockpit (Ctrl+Shift+D).
Verifies:
1. Template Synchronization: devtools.rml and devtools.rcss match across root and client asset paths.
2. Structure & Tabs: 5 distinct tabs (Engine Perf, Spatial Audio, Entity Inspector, Spectator/Cam, Quick Tools).
3. Data Binding Targets: FPS, latency, RAM, 256-voice WASAPI audio telemetry, entity inspector bars.
4. Vulkan Layer Binary: VkLayer_hon_replay_capture.dll compiled and deployed to client directories.
"""

import os
import re
import unittest
import xml.etree.ElementTree as ET

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

DEVTOOLS_RML_PATHS = [
    os.path.join(REPO_ROOT, "assets", "hud", "devtools.rml"),
    os.path.join(REPO_ROOT, "games", "client", "Assets", "hud", "devtools.rml"),
    os.path.join(REPO_ROOT, "games", "client", "windows-x64", "assets", "hud", "devtools.rml"),
]

DEVTOOLS_RCSS_PATHS = [
    os.path.join(REPO_ROOT, "assets", "hud", "devtools.rcss"),
    os.path.join(REPO_ROOT, "games", "client", "Assets", "hud", "devtools.rcss"),
    os.path.join(REPO_ROOT, "games", "client", "windows-x64", "assets", "hud", "devtools.rcss"),
]

TAO_CONSOLE_RML_PATHS = [
    os.path.join(REPO_ROOT, "assets", "hud", "tao_console.rml"),
    os.path.join(REPO_ROOT, "games", "client", "Assets", "hud", "tao_console.rml"),
    os.path.join(REPO_ROOT, "games", "client", "windows-x64", "assets", "hud", "tao_console.rml"),
]

TAO_CONSOLE_RCSS_PATHS = [
    os.path.join(REPO_ROOT, "assets", "hud", "tao_console.rcss"),
    os.path.join(REPO_ROOT, "games", "client", "Assets", "hud", "tao_console.rcss"),
    os.path.join(REPO_ROOT, "games", "client", "windows-x64", "assets", "hud", "tao_console.rcss"),
]

VK_LAYER_PATHS = [
    os.path.join(REPO_ROOT, "games", "client", "VkLayer_hon_replay_capture.dll"),
    os.path.join(REPO_ROOT, "games", "client", "windows-x64", "VkLayer_hon_replay_capture.dll"),
]

class TestRmlUiDevHud(unittest.TestCase):
    def test_01_template_files_exist_and_synchronized(self):
        """Verify that devtools.rml and devtools.rcss exist and are bit-for-bit synchronized."""
        for path in DEVTOOLS_RML_PATHS:
            self.assertTrue(os.path.exists(path), f"Missing RML file: {path}")

        with open(DEVTOOLS_RML_PATHS[0], "rb") as f:
            primary_rml = f.read()
        for path in DEVTOOLS_RML_PATHS[1:]:
            with open(path, "rb") as f:
                self.assertTrue(
                    primary_rml == f.read(),
                    f"Desynchronized RML file: {path} — Copy-Item assets/hud/devtools.rml to client asset paths.",
                )

        for path in DEVTOOLS_RCSS_PATHS:
            self.assertTrue(os.path.exists(path), f"Missing RCSS file: {path}")

        with open(DEVTOOLS_RCSS_PATHS[0], "rb") as f:
            primary_rcss = f.read()
        for path in DEVTOOLS_RCSS_PATHS[1:]:
            with open(path, "rb") as f:
                self.assertTrue(
                    primary_rcss == f.read(),
                    f"Desynchronized RCSS file: {path} — Copy-Item assets/hud/devtools.rcss to client asset paths.",
                )

    def test_02_rml_structure_and_tabs(self):
        """Verify that all 5 developer cockpit tabs and panes are defined."""
        tree = ET.parse(DEVTOOLS_RML_PATHS[0])
        root = tree.getroot()
        self.assertEqual(root.tag, "rml")

        element_ids = {elem.attrib["id"] for elem in root.iter() if "id" in elem.attrib}

        required_tabs = [
            ("devtab-btn-perf", "devtab-perf"),
            ("devtab-btn-audio", "devtab-audio"),
            ("devtab-btn-inspect", "devtab-inspect"),
            ("devtab-btn-spectator", "devtab-spectator"),
            ("devtab-btn-actions", "devtab-actions"),
        ]

        for btn_id, pane_id in required_tabs:
            self.assertIn(btn_id, element_ids, f"Missing tab button ID: {btn_id}")
            self.assertIn(pane_id, element_ids, f"Missing tab pane ID: {pane_id}")

    def test_03_telemetry_data_binding_targets_present(self):
        """Verify that all live DOM data binding IDs targeted by UpdateMetrics exist."""
        tree = ET.parse(DEVTOOLS_RML_PATHS[0])
        root = tree.getroot()
        element_ids = {elem.attrib["id"] for elem in root.iter() if "id" in elem.attrib}

        expected_telemetry_ids = [
            # Performance Tab
            "metric-fps",
            "metric-ft",
            "metric-res",
            "metric-ram",
            # Spatial Audio Tab
            "audio-stat-voices",
            "audio-stat-lat",
            "audio-stat-hrtf",
            "audio-stat-pos",
            "audio-meter-bar",
            # Hero / Entity Inspector Tab
            "inspect-hero-name",
            "inspect-hp-val",
            "inspect-hp-bar",
            "inspect-mana-val",
            "inspect-mana-bar",
            "inspect-ms",
            "inspect-range",
            "inspect-armor",
            "inspect-marmor",
            # Header / Footer Action Controls
            "btn-close-devtools",
            "btn-tao-console",
        ]

        for target_id in expected_telemetry_ids:
            self.assertIn(target_id, element_ids, f"Missing data binding ID in RML: {target_id}")

    def test_04_vulkan_layer_binaries_deployed(self):
        """Verify that the modernized Vulkan Layer binary is deployed to game client folders."""
        for vk_dll in VK_LAYER_PATHS:
            self.assertTrue(os.path.exists(vk_dll), f"Vulkan layer missing at {vk_dll}")
            size = os.path.getsize(vk_dll)
            self.assertGreater(size, 1000000, f"Vulkan layer binary abnormally small: {size} bytes")

    def test_05_rcss_no_unsupported_properties_or_non_ascii_glyphs(self):
        """
        Protects: RmlUi clean stylesheet parsing and FontEngine_STB ASCII (32..127) SDF glyph rendering.
        Fails when: Unsupported CSS properties (box-shadow, letter-spacing, flex gap) or non-ASCII
        characters (e.g. degree symbol) are introduced into devtools.rml or devtools.rcss.
        """
        with open(DEVTOOLS_RML_PATHS[0], "r", encoding="utf-8") as f:
            rml_text = f.read()
        with open(DEVTOOLS_RCSS_PATHS[0], "r", encoding="utf-8") as f:
            rcss_text = f.read()

        non_ascii_rml = [ch for ch in rml_text if ord(ch) >= 128]
        non_ascii_rcss = [ch for ch in rcss_text if ord(ch) >= 128]
        self.assertEqual(
            non_ascii_rml,
            [],
            f"Non-ASCII glyphs found in devtools.rml (renders blank in FontEngine_STB): {non_ascii_rml[:5]} — "
            "Replace with pure ASCII equivalents in assets/hud/devtools.rml.",
        )
        self.assertEqual(
            non_ascii_rcss,
            [],
            f"Non-ASCII bytes found in devtools.rcss: {non_ascii_rcss[:5]} — "
            "Keep assets/hud/devtools.rcss pure ASCII.",
        )

        unsupported_props = ("box-shadow:", "letter-spacing:", "row-gap:", "column-gap:")
        for prop in unsupported_props:
            self.assertNotIn(
                prop,
                rcss_text,
                f"Unsupported RmlUi property '{prop}' in devtools.rcss causes [RmlUi][WARN] or ignored layout.",
            )
            self.assertNotIn(
                prop,
                rml_text,
                f"Unsupported RmlUi property '{prop}' in devtools.rml inline <style> causes [RmlUi][WARN].",
            )
        self.assertIsNone(
            re.search(r"(?<![a-zA-Z-])gap\s*:", rcss_text),
            "CSS 'gap:' is ignored by RmlUi LayoutFlex.cpp; use explicit margin-right / margin-bottom instead.",
        )

    def test_06_full_viewport_responsive_layout_and_typography_floor(self):
        """
        Protects: Full-screen (95% x 95%) responsive flexbox layout, SwitchTab-safe .pane-flex wrappers,
        hot-reloadable inline <style>, @media scaling rules, and readable large typography floor.
        Fails when: #devtools-window reverts to tiny fixed pixel sizing, .tab-pane omits .pane-flex,
        @media breakpoints are dropped, or base 1080p title/tab/button font sizes shrink back to tiny values.
        """
        tree = ET.parse(DEVTOOLS_RML_PATHS[0])
        root = tree.getroot()

        # 1. Inline <style> must exist in <head> so ReloadDocuments() updates styles live without process restart.
        style_el = root.find("./head/style")
        self.assertIsNotNone(
            style_el,
            "Missing <head><style> in devtools.rml — required so ReloadDocuments() bypasses StyleSheetFactory cache.",
        )

        # 2. Every .tab-pane must wrap contents in .pane-flex because C++ SwitchTab sets inline display: block.
        for pane_id in ("devtab-perf", "devtab-audio", "devtab-inspect", "devtab-spectator", "devtab-actions"):
            pane = root.find(f".//*[@id='{pane_id}']")
            self.assertIsNotNone(pane, f"Missing pane #{pane_id}")
            children = list(pane)
            self.assertGreaterEqual(len(children), 1, f"Pane #{pane_id} has no child container")
            self.assertIn(
                "pane-flex",
                children[0].attrib.get("class", "").split(),
                f"Pane #{pane_id} must wrap contents in <div class='pane-flex'> so SwitchTab display:block preserves flex.",
            )

        with open(DEVTOOLS_RCSS_PATHS[0], "r", encoding="utf-8") as f:
            rcss_text = f.read()

        # 3. Verify full-screen viewport sizing on #devtools-window and inner .devtools-shell flex column.
        window_match = re.search(r"#devtools-window\s*\{([^}]+)\}", rcss_text)
        self.assertIsNotNone(window_match, "Missing #devtools-window rule in devtools.rcss")
        window_body = window_match.group(1)
        self.assertIn("width: 95%", window_body, "#devtools-window must span 95% viewport width")
        self.assertIn("height: 95%", window_body, "#devtools-window must span 95% viewport height")
        self.assertIn("display: block", window_body, "#devtools-window must be display: block (RmlUi LayoutEngine.cpp:204)")

        shell_match = re.search(r"\.devtools-shell\s*\{([^}]+)\}", rcss_text)
        self.assertIsNotNone(shell_match, "Missing .devtools-shell rule in devtools.rcss")
        self.assertIn("display: flex", shell_match.group(1), ".devtools-shell must host the outer flexbox column")

        for media_query in (
            "@media (min-width: 2200px)",
            "@media (min-width: 3200px)",
            "@media (max-height: 860px)",
        ):
            self.assertIn(media_query, rcss_text, f"Missing responsive breakpoint '{media_query}' in devtools.rcss")
            self.assertIn(media_query, style_el.text or "", f"Missing '{media_query}' in devtools.rml <style>")

        # 4. Base 1080p typography floor (before @media blocks) must keep title, tabs, and buttons large.
        base_rcss = rcss_text.split("@media", 1)[0]

        def extract_font_px(selector_regex: str) -> int:
            block = re.search(selector_regex + r"\s*\{([^}]+)\}", base_rcss)
            self.assertIsNotNone(block, f"Missing base rule for {selector_regex} in devtools.rcss")
            px_match = re.search(r"font-size:\s*(\d+)px", block.group(1))
            self.assertIsNotNone(px_match, f"Missing font-size in base rule for {selector_regex}")
            return int(px_match.group(1))

        self.assertGreaterEqual(extract_font_px(r"\.devtools-title"), 24, "Header title font-size too small")
        self.assertGreaterEqual(extract_font_px(r"\.tab-btn"), 18, "Navigation tab font-size too small")
        self.assertGreaterEqual(extract_font_px(r"\.btn"), 18, "Base action button font-size too small")
        self.assertGreaterEqual(extract_font_px(r"\.btn-label"), 20, "Action tile label font-size too small")
        self.assertGreaterEqual(extract_font_px(r"\.hero-metric-val-green"), 44, "Hero FPS readout font-size too small")

    def test_07_rmlui_layout_engine_formatting_invariants(self):
        """
        Protects: RmlUi C++ LayoutEngine::FormatElement & ValidateTopLevelElement invariants
        (native/deps/rmlui/Source/Core/LayoutEngine.cpp:60-85, 188-220 and LayoutFlex.cpp:832).
        Fails when:
          1. Any element combines display: flex/table with position: absolute/fixed or float != none
             (causes 'Element will not be formatted' 0x0 invisible window).
          2. Any element with display: flex/table is placed on <body> or as a direct child of another
             display: flex container without an intermediate display: block wrapper <div>.
          3. OverlayManager::SwitchTab resetting class='tab-btn' strips right-margin reset on #devtab-btn-actions.
        """
        with open(DEVTOOLS_RCSS_PATHS[0], "r", encoding="utf-8") as f:
            rcss_raw = f.read()

        # Strip CSS comments and @media wrappers so rules inside @media are also audited.
        clean_css = re.sub(r"/\*.*?\*/", "", rcss_raw, flags=re.DOTALL)
        while "@media" in clean_css:
            clean_css = re.sub(r"@media[^{]+\{((?:[^{}]|\{[^{}]*\})*)\}", r"\1", clean_css)

        rules = []
        for order_idx, match in enumerate(re.finditer(r"([^{}]+)\{([^{}]+)\}", clean_css)):
            selectors_str, decls_str = match.group(1).strip(), match.group(2).strip()
            props = {}
            for decl in decls_str.split(";"):
                if ":" in decl:
                    k, v = decl.split(":", 1)
                    props[k.strip().lower()] = v.strip().lower()
            for raw_sel in selectors_str.split(","):
                sel = raw_sel.strip()
                if not sel or ":" in sel:
                    # Pseudo-classes (:hover, :last-child) are dynamic; #devtab-btn-actions must not rely solely on :last-child
                    continue
                rules.append((order_idx, sel, props))

        def matches_simple(elem: ET.Element, simple_sel: str) -> bool:
            tag = elem.tag.lower()
            el_id = elem.attrib.get("id", "")
            classes = set(elem.attrib.get("class", "").split())
            tokens = re.findall(r"[#.]?[a-zA-Z0-9_-]+", simple_sel)
            if not tokens:
                return False
            for tok in tokens:
                if tok.startswith("#"):
                    if el_id != tok[1:]:
                        return False
                elif tok.startswith("."):
                    if tok[1:] not in classes:
                        return False
                else:
                    if tag != tok.lower():
                        return False
            return True

        def selector_specificity(sel: str, ancestors: list, elem: ET.Element):
            parts = sel.split()
            if not matches_simple(elem, parts[-1]):
                return None
            anc_idx = len(ancestors) - 1
            for part in reversed(parts[:-1]):
                while anc_idx >= 0 and not matches_simple(ancestors[anc_idx], part):
                    anc_idx -= 1
                if anc_idx < 0:
                    return None
                anc_idx -= 1
            ids = sel.count("#")
            cls = sel.count(".")
            tags = len([t for p in parts for t in re.findall(r"(?<![#.a-zA-Z0-9_-])[a-zA-Z][a-zA-Z0-9_-]*", p)])
            return (ids, cls, tags)

        def compute_styles(elem: ET.Element, ancestors: list) -> dict:
            default_display = "inline" if elem.tag.lower() == "span" else "block"
            computed = {"display": default_display, "position": "static", "float": "none"}
            matched = []
            for order_idx, sel, props in rules:
                spec = selector_specificity(sel, ancestors, elem)
                if spec is not None:
                    matched.append((spec, order_idx, props))
            matched.sort(key=lambda item: (item[0], item[1]))
            for _, _, props in matched:
                computed.update(props)
            inline_style = elem.attrib.get("style", "")
            for decl in inline_style.split(";"):
                if ":" in decl:
                    k, v = decl.split(":", 1)
                    computed[k.strip().lower()] = v.strip().lower()
            return computed

        tree = ET.parse(DEVTOOLS_RML_PATHS[0])
        root = tree.getroot()
        body = root.find("./body")
        self.assertIsNotNone(body, "Missing <body> in devtools.rml")

        violations = []

        def audit_tree(elem: ET.Element, ancestors: list, parent_styles: dict | None):
            styles = compute_styles(elem, ancestors)
            disp = styles.get("display", "block")
            pos = styles.get("position", "static")
            flt = styles.get("float", "none")
            addr = f"{elem.tag}#{elem.attrib['id']}" if "id" in elem.attrib else f"{elem.tag}.{'.'.join(elem.attrib.get('class', '').split())}"

            if elem.tag.lower() == "body" and disp in ("flex", "table"):
                violations.append(f"<body> cannot use display: {disp} (LayoutEngine.cpp:67)")

            if disp in ("flex", "table"):
                if pos in ("absolute", "fixed"):
                    violations.append(
                        f"{addr} combines display: {disp} with position: {pos} — RmlUi refuses to format (LayoutEngine.cpp:204)"
                    )
                if flt != "none":
                    violations.append(
                        f"{addr} combines display: {disp} with float: {flt} — RmlUi refuses to format (LayoutEngine.cpp:215)"
                    )
                if parent_styles and parent_styles.get("display") == "flex":
                    parent_el = ancestors[-1]
                    p_addr = (
                        f"{parent_el.tag}#{parent_el.attrib['id']}"
                        if "id" in parent_el.attrib
                        else f"{parent_el.tag}.{'.'.join(parent_el.attrib.get('class', '').split())}"
                    )
                    violations.append(
                        f"{addr} (display: {disp}) is nested directly inside flex container {p_addr} without a display: block wrapper (LayoutEngine.cpp:73)"
                    )

            next_ancestors = ancestors + [elem]
            for child in list(elem):
                audit_tree(child, next_ancestors, styles)

        # Audit initial DOM state and simulated SwitchTab state across all 5 tabs
        audit_tree(body, [root], None)

        dev_tabs = [
            ("devtab-btn-perf", "devtab-perf"),
            ("devtab-btn-audio", "devtab-audio"),
            ("devtab-btn-inspect", "devtab-inspect"),
            ("devtab-btn-spectator", "devtab-spectator"),
            ("devtab-btn-actions", "devtab-actions"),
        ]
        for active_btn_id, active_pane_id in dev_tabs:
            for btn_id, pane_id in dev_tabs:
                is_active = pane_id == active_pane_id
                btn_el = root.find(f".//*[@id='{btn_id}']")
                pane_el = root.find(f".//*[@id='{pane_id}']")
                btn_el.attrib["class"] = "tab-btn active" if is_active else "tab-btn"
                pane_el.attrib["class"] = "tab-pane active" if is_active else "tab-pane"
                pane_el.attrib["style"] = f"display: {'block' if is_active else 'none'};"
            audit_tree(body, [root], None)

            actions_btn = root.find(".//*[@id='devtab-btn-actions']")
            actions_styles = compute_styles(actions_btn, [root, body])
            self.assertIn(
                actions_styles.get("margin-right"),
                ("0", "0px"),
                "OverlayManager::SwitchTab overwrites class='tab-btn'; #devtab-btn-actions must keep margin-right: 0 via ID selector.",
            )

        self.assertEqual(
            violations,
            [],
            "RmlUi LayoutEngine formatting violations detected (causes 0x0 invisible elements):\n  - "
            + "\n  - ".join(violations),
        )

    def test_08_vulkan_layer_env_unification_and_log_flush(self):
        """
        Protects: Ctrl+Shift+D initialization under both HON_MODERN_HUD=1 and HON_DEVTOOLS=1
        (non-headless) launches, plus immediate flushing of INFO/WARN/ERROR lines to hon_vulkan_layer.log.
        Fails when: OverlayManager::Initialize ignores HON_DEVTOOLS or layer_manifest.h only flushes ERROR/FATAL.
        """
        overlay_mgr_cpp = os.path.join(REPO_ROOT, "native", "vulkan-layer", "rmlui", "overlay_manager.cpp")
        layer_manifest_h = os.path.join(REPO_ROOT, "native", "vulkan-layer", "layer_manifest.h")

        with open(overlay_mgr_cpp, "r", encoding="utf-8") as f:
            mgr_src = f.read()
        with open(layer_manifest_h, "r", encoding="utf-8") as f:
            manifest_src = f.read()

        self.assertTrue(
            'getenv("HON_MODERN_HUD")' in mgr_src,
            "Missing getenv(\"HON_MODERN_HUD\") in native/vulkan-layer/rmlui/overlay_manager.cpp.",
        )
        self.assertTrue(
            'getenv("HON_DEVTOOLS")' in mgr_src,
            "Missing getenv(\"HON_DEVTOOLS\") in native/vulkan-layer/rmlui/overlay_manager.cpp — "
            "Update OverlayManager::Initialize and run cmake --build native/vulkan-layer/build --config Release.",
        )
        self.assertTrue(
            'getenv("KONGOR_HEADLESS")' in mgr_src,
            "Missing getenv(\"KONGOR_HEADLESS\") guard in native/vulkan-layer/rmlui/overlay_manager.cpp.",
        )
        self.assertTrue(
            'strcmp(level, "DEBUG") != 0' in manifest_src,
            "HonLayerLog in native/vulkan-layer/layer_manifest.h must flush non-DEBUG log messages immediately.",
        )
        # Verify fflush(s_LogFile) is guarded by non-DEBUG rather than ERROR/FATAL-only
        self.assertIsNone(
            re.search(r'strcmp\(level,\s*"ERROR"\)\s*==\s*0\s*\|\|\s*strcmp\(level,\s*"FATAL"\)', manifest_src),
            "layer_manifest.h still restricts fflush(s_LogFile) to ERROR/FATAL, hiding INFO/WARN startup telemetry.",
        )

    def _audit_rml_layout_invariants(self, rml_path: str, rcss_path: str, tabs: list, last_tab_btn_id: str):
        with open(rcss_path, "r", encoding="utf-8") as f:
            rcss_raw = f.read()

        clean_css = re.sub(r"/\*.*?\*/", "", rcss_raw, flags=re.DOTALL)
        while "@media" in clean_css:
            clean_css = re.sub(r"@media[^{]+\{((?:[^{}]|\{[^{}]*\})*)\}", r"\1", clean_css)

        rules = []
        for order_idx, match in enumerate(re.finditer(r"([^{}]+)\{([^{}]+)\}", clean_css)):
            selectors_str, decls_str = match.group(1).strip(), match.group(2).strip()
            props = {}
            for decl in decls_str.split(";"):
                if ":" in decl:
                    k, v = decl.split(":", 1)
                    props[k.strip().lower()] = v.strip().lower()
            for raw_sel in selectors_str.split(","):
                sel = raw_sel.strip()
                if not sel or ":" in sel:
                    continue
                rules.append((order_idx, sel, props))

        def matches_simple(elem: ET.Element, simple_sel: str) -> bool:
            tag = elem.tag.lower()
            el_id = elem.attrib.get("id", "")
            classes = set(elem.attrib.get("class", "").split())
            tokens = re.findall(r"[#.]?[a-zA-Z0-9_-]+", simple_sel)
            if not tokens:
                return False
            for tok in tokens:
                if tok.startswith("#"):
                    if el_id != tok[1:]:
                        return False
                elif tok.startswith("."):
                    if tok[1:] not in classes:
                        return False
                else:
                    if tag != tok.lower():
                        return False
            return True

        def selector_specificity(sel: str, ancestors: list, elem: ET.Element):
            parts = sel.split()
            if not matches_simple(elem, parts[-1]):
                return None
            anc_idx = len(ancestors) - 1
            for part in reversed(parts[:-1]):
                while anc_idx >= 0 and not matches_simple(ancestors[anc_idx], part):
                    anc_idx -= 1
                if anc_idx < 0:
                    return None
                anc_idx -= 1
            ids = sel.count("#")
            cls = sel.count(".")
            tags = len([t for p in parts for t in re.findall(r"(?<![#.a-zA-Z0-9_-])[a-zA-Z][a-zA-Z0-9_-]*", p)])
            return (ids, cls, tags)

        def compute_styles(elem: ET.Element, ancestors: list) -> dict:
            default_display = "inline" if elem.tag.lower() == "span" else "block"
            computed = {"display": default_display, "position": "static", "float": "none"}
            matched = []
            for order_idx, sel, props in rules:
                spec = selector_specificity(sel, ancestors, elem)
                if spec is not None:
                    matched.append((spec, order_idx, props))
            matched.sort(key=lambda item: (item[0], item[1]))
            for _, _, props in matched:
                computed.update(props)
            inline_style = elem.attrib.get("style", "")
            for decl in inline_style.split(";"):
                if ":" in decl:
                    k, v = decl.split(":", 1)
                    computed[k.strip().lower()] = v.strip().lower()
            return computed

        tree = ET.parse(rml_path)
        root = tree.getroot()
        body = root.find("./body")
        self.assertIsNotNone(body, f"Missing <body> in {rml_path}")

        violations = []

        def audit_tree(elem: ET.Element, ancestors: list, parent_styles: dict | None):
            styles = compute_styles(elem, ancestors)
            disp = styles.get("display", "block")
            pos = styles.get("position", "static")
            flt = styles.get("float", "none")
            addr = (
                f"{elem.tag}#{elem.attrib['id']}"
                if "id" in elem.attrib
                else f"{elem.tag}.{'.'.join(elem.attrib.get('class', '').split())}"
            )

            if elem.tag.lower() == "body" and disp in ("flex", "table"):
                violations.append(f"<body> cannot use display: {disp} (LayoutEngine.cpp:67)")

            if disp in ("flex", "table"):
                if pos in ("absolute", "fixed"):
                    violations.append(
                        f"{addr} combines display: {disp} with position: {pos} — RmlUi refuses to format (LayoutEngine.cpp:204)"
                    )
                if flt != "none":
                    violations.append(
                        f"{addr} combines display: {disp} with float: {flt} — RmlUi refuses to format (LayoutEngine.cpp:215)"
                    )
                if parent_styles and parent_styles.get("display") == "flex":
                    parent_el = ancestors[-1]
                    p_addr = (
                        f"{parent_el.tag}#{parent_el.attrib['id']}"
                        if "id" in parent_el.attrib
                        else f"{parent_el.tag}.{'.'.join(parent_el.attrib.get('class', '').split())}"
                    )
                    violations.append(
                        f"{addr} (display: {disp}) is nested directly inside flex container {p_addr} without a display: block wrapper (LayoutEngine.cpp:73)"
                    )

            next_ancestors = ancestors + [elem]
            for child in list(elem):
                audit_tree(child, next_ancestors, styles)

        audit_tree(body, [root], None)

        for _, active_pane_id in tabs:
            for btn_id, pane_id in tabs:
                is_active = pane_id == active_pane_id
                btn_el = root.find(f".//*[@id='{btn_id}']")
                pane_el = root.find(f".//*[@id='{pane_id}']")
                btn_el.attrib["class"] = "tab-btn active" if is_active else "tab-btn"
                pane_el.attrib["class"] = "tab-pane active" if is_active else "tab-pane"
                pane_el.attrib["style"] = f"display: {'block' if is_active else 'none'};"
            self_heal = root.find(".//*[@id='btn-self-heal']")
            if self_heal is not None:
                self_heal.attrib["class"] = "btn btn-action btn-danger"
            audit_tree(body, [root], None)

            last_btn = root.find(f".//*[@id='{last_tab_btn_id}']")
            last_styles = compute_styles(last_btn, [root, body])
            self.assertIn(
                last_styles.get("margin-right"),
                ("0", "0px"),
                f"OverlayManager::SwitchTab overwrites class='tab-btn'; #{last_tab_btn_id} must keep margin-right: 0 via ID selector.",
            )

        self.assertEqual(
            violations,
            [],
            f"RmlUi LayoutEngine formatting violations detected in {os.path.basename(rml_path)}:\n  - "
            + "\n  - ".join(violations),
        )

    def test_09_tao_console_responsive_layout_back_button_and_invariants(self):
        """
        Protects: Full-screen (95% x 95%) responsive TAO Diagnostics Console layout, Back-to-Cockpit
        navigation controls, 3-way HUD asset synchronization, pure-ASCII glyphs, and RmlUi LayoutEngine rules.
        Fails when: tao_console.rml/.rcss drifts across client asset paths, lacks BackToDevTools buttons,
        reverts to small float-based sizing, or violates RmlUi LayoutEngine flex/block constraints.
        """
        for path in TAO_CONSOLE_RML_PATHS:
            self.assertTrue(os.path.exists(path), f"Missing TAO RML file: {path}")
        with open(TAO_CONSOLE_RML_PATHS[0], "rb") as f:
            primary_rml = f.read()
        for path in TAO_CONSOLE_RML_PATHS[1:]:
            with open(path, "rb") as f:
                self.assertTrue(
                    primary_rml == f.read(),
                    f"Desynchronized TAO RML file: {path} — Copy-Item assets/hud/tao_console.rml to client asset paths.",
                )

        for path in TAO_CONSOLE_RCSS_PATHS:
            self.assertTrue(os.path.exists(path), f"Missing TAO RCSS file: {path}")
        with open(TAO_CONSOLE_RCSS_PATHS[0], "rb") as f:
            primary_rcss = f.read()
        for path in TAO_CONSOLE_RCSS_PATHS[1:]:
            with open(path, "rb") as f:
                self.assertTrue(
                    primary_rcss == f.read(),
                    f"Desynchronized TAO RCSS file: {path} — Copy-Item assets/hud/tao_console.rcss to client asset paths.",
                )

        rml_text = primary_rml.decode("utf-8")
        rcss_text = primary_rcss.decode("utf-8")

        self.assertEqual([c for c in rml_text if ord(c) >= 128], [], "Non-ASCII glyphs found in tao_console.rml")
        self.assertEqual([c for c in rcss_text if ord(c) >= 128], [], "Non-ASCII glyphs found in tao_console.rcss")

        for prop in ("box-shadow:", "letter-spacing:", "row-gap:", "column-gap:"):
            self.assertNotIn(prop, rcss_text, f"Unsupported RmlUi property '{prop}' in tao_console.rcss")
            self.assertNotIn(prop, rml_text, f"Unsupported RmlUi property '{prop}' in tao_console.rml")
        self.assertIsNone(re.search(r"(?<![a-zA-Z-])gap\s*:", rcss_text), "CSS 'gap:' is ignored by RmlUi LayoutFlex.cpp")

        tree = ET.parse(TAO_CONSOLE_RML_PATHS[0])
        root = tree.getroot()
        style_el = root.find("./head/style")
        self.assertIsNotNone(style_el, "Missing <head><style> in tao_console.rml for live ReloadDocuments() support")

        element_ids = {elem.attrib["id"] for elem in root.iter() if "id" in elem.attrib}
        required_tao_ids = [
            # Back & Close Navigation
            "btn-back-devtools",
            "btn-back-devtools-footer",
            "btn-close-console",
            "btn-close-console-footer",
            # Service Health Strip
            "badge-master", "dot-master", "text-master",
            "badge-chat", "dot-chat", "text-chat",
            "badge-gameserver", "dot-gameserver", "text-gameserver",
            "badge-network", "dot-network", "text-network",
            "badge-engine", "dot-engine", "text-engine",
            # Tabs & Panes
            "tab-btn-overview", "tab-overview",
            "tab-btn-fleet", "tab-fleet",
            "tab-btn-vulkan", "tab-vulkan",
            "tab-btn-vfs", "tab-vfs",
            "tab-btn-console", "tab-console",
            # Live C++ Telemetry & Terminals
            "tao-stat-ipc", "tao-stat-res", "tao-stat-fps", "tao-stat-net",
            "tao-stat-tick", "tao-stat-vfs", "tao-stat-audio", "tao-stat-fault",
            "tao-log-terminal", "tao-console-terminal", "tao-input-cmd", "btn-exec-cmd",
            "btn-refresh-trace", "btn-self-heal",
        ]
        for target_id in required_tao_ids:
            self.assertIn(target_id, element_ids, f"Missing required ID '{target_id}' in tao_console.rml")

        for back_id in ("btn-back-devtools", "btn-back-devtools-footer"):
            back_el = root.find(f".//*[@id='{back_id}']")
            self.assertEqual(back_el.attrib.get("onclick"), "BackToDevTools", f"#{back_id} must dispatch BackToDevTools")

        window_match = re.search(r"#tao-console-window\s*\{([^}]+)\}", rcss_text)
        self.assertIsNotNone(window_match, "Missing #tao-console-window rule in tao_console.rcss")
        self.assertIn("width: 95%", window_match.group(1), "#tao-console-window must span 95% width")
        self.assertIn("height: 95%", window_match.group(1), "#tao-console-window must span 95% height")
        self.assertIn("display: block", window_match.group(1), "#tao-console-window must be display: block")

        shell_match = re.search(r"\.tao-shell\s*\{([^}]+)\}", rcss_text)
        self.assertIsNotNone(shell_match, "Missing .tao-shell rule in tao_console.rcss")
        self.assertIn("display: flex", shell_match.group(1), ".tao-shell must host the outer flexbox column")

        base_rcss = rcss_text.split("@media", 1)[0]

        def extract_font_px(selector_regex: str) -> int:
            block = re.search(selector_regex + r"\s*\{([^}]+)\}", base_rcss)
            self.assertIsNotNone(block, f"Missing base rule for {selector_regex} in tao_console.rcss")
            px_match = re.search(r"font-size:\s*(\d+)px", block.group(1))
            self.assertIsNotNone(px_match, f"Missing font-size in base rule for {selector_regex}")
            return int(px_match.group(1))

        self.assertGreaterEqual(extract_font_px(r"\.console-title"), 24, "TAO header title font-size too small")
        self.assertGreaterEqual(extract_font_px(r"\.tab-btn"), 18, "TAO navigation tab font-size too small")
        self.assertGreaterEqual(extract_font_px(r"\.stat-label"), 17, "TAO telemetry label font-size too small")
        self.assertGreaterEqual(extract_font_px(r"\.stat-val"), 18, "TAO telemetry value font-size too small")
        self.assertGreaterEqual(extract_font_px(r"\.btn"), 17, "TAO footer/action button font-size too small")

        tao_tabs = [
            ("tab-btn-overview", "tab-overview"),
            ("tab-btn-fleet", "tab-fleet"),
            ("tab-btn-vulkan", "tab-vulkan"),
            ("tab-btn-vfs", "tab-vfs"),
            ("tab-btn-console", "tab-console"),
        ]
        self._audit_rml_layout_invariants(TAO_CONSOLE_RML_PATHS[0], TAO_CONSOLE_RCSS_PATHS[0], tao_tabs, "tab-btn-console")

    def test_10_tao_console_native_back_action_and_rml_log_escaping(self):
        """
        Protects: Instant BackToDevTools action handling and XML entity escaping in SetInnerRML log streams.
        Fails when: overlay_manager.cpp lacks BackToDevTools or injects unescaped '<div ...>' log messages
        into #tao-log-terminal causing '[RmlUi] Closing tag body mismatched' XML parse errors.
        """
        overlay_mgr_cpp = os.path.join(REPO_ROOT, "native", "vulkan-layer", "rmlui", "overlay_manager.cpp")
        with open(overlay_mgr_cpp, "r", encoding="utf-8") as f:
            mgr_src = f.read()

        self.assertTrue(
            '"BackToDevTools"' in mgr_src and '"btn-back-devtools"' in mgr_src,
            "Missing BackToDevTools / btn-back-devtools handler in native/vulkan-layer/rmlui/overlay_manager.cpp.",
        )
        self.assertTrue(
            "EscapeRmlText" in mgr_src,
            "Missing EscapeRmlText XML sanitizer in native/vulkan-layer/rmlui/overlay_manager.cpp (causes RmlUi XML parse errors on log lines containing '<div').",
        )

if __name__ == "__main__":
    unittest.main()

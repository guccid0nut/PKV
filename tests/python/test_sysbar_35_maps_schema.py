# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""Static and .s2z schema contracts for SysBar 35-map paginated drawer, Sandbox Mode,
Test+++ frozen Vanilla 4.10 O(1) baseline filter, export_audit_log.ps1, and H2b mirror parity.

Value Bar:
  protects:   All 35 shipped maps under games/client/game/maps/*.s2z are reachable in the 3-tab
              SysBar drawer with valid worldconfig/game_info overrides, Test+++ filters only exact
              frozen Vanilla 4.10 baseline entries, export_audit_log.ps1 preserves all 10 suite
              summaries distinctly, and every VFS mirror matches override/ byte-for-byte.
  fails_when: A map .s2z is added/renamed without updating MAP_PRACTICE_CONFIG/PRACTICE_TABS/XML,
              a noheroselect="true" map omits forcePick=true, truedevo uses forcepick or loses
              allowduplicate/Hero_Devourer, audit_known_baseline.lua uses wildcard prefixes or loses
              exact counts (120 effects / 114 heroes / 98 entity pairs), or any mirror drifts.
  seam:       Real games/client/game/maps/*.s2z zip worldconfig + game_info.entity XML, real
              Lua/XML/PS1/JSON source artifacts, and luac51 bytecode checks (< 2.0s).
"""

from __future__ import annotations

import hashlib
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
MAPS_DIR = REPO_ROOT / "games" / "client" / "game" / "maps"
LUAC51_CANDIDATES = (
    REPO_ROOT / "native" / "build" / "luac51_build" / "Release" / "luac51.exe",
    REPO_ROOT / "native" / "tools" / "luac51" / "build" / "Release" / "luac51.exe",
    REPO_ROOT / "artifacts" / "native-binaries" / "windows-x64" / "luac51.exe",
    REPO_ROOT / ".tools" / "bin" / "lua" / "luac51.exe",
)
LUA_VERIFIER_CANDIDATES = (
    REPO_ROOT / "src" / "tools" / "lua-verifier" / "bin" / "Release" / "net11.0" / "LuaVerifier.exe",
    REPO_ROOT / "src" / "tools" / "lua-verifier" / "bin" / "Debug" / "net11.0" / "LuaVerifier.exe",
)

SYSBAR_PKG = REPO_ROOT / "games" / "client" / "override" / "ui" / "fe2" / "newui" / "system_bar_v2.package"
SYSBAR_CTRL = REPO_ROOT / "games" / "client" / "override" / "ui" / "fe2" / "scripts" / "sysbar_controller.lua"
SYSBAR_SHIM = REPO_ROOT / "games" / "client" / "override" / "ui" / "fe2" / "scripts" / "system_bar.lua"
TEST_SUITE_LUA = REPO_ROOT / "games" / "client" / "override" / "ui" / "test_suite" / "test_suite.lua"
TEST_SUITE_PKG = REPO_ROOT / "games" / "client" / "override" / "ui" / "test_suite" / "test_suite.package"
AUDIT_BASELINE_LUA = REPO_ROOT / "games" / "client" / "override" / "ui" / "test_suite" / "audit_known_baseline.lua"
EXPORT_AUDIT_PS1 = REPO_ROOT / "scripts" / "qa" / "export_audit_log.ps1"
BASELINE_JSON = REPO_ROOT / "data" / "audits" / "baseline.json"

MIRROR_MATRIX: dict[Path, list[Path]] = {
    REPO_ROOT / "games/client/override/ui/fe2/newui/system_bar_v2.package": [
        REPO_ROOT / "games/client/ClientPackageFiles/ui/fe2/newui/system_bar_v2.package",
        REPO_ROOT / "games/client/game/ui/fe2/newui/system_bar_v2.package",
    ],
    REPO_ROOT / "games/client/override/ui/fe2/scripts/sysbar_controller.lua": [
        REPO_ROOT / "games/client/ClientPackageFiles/ui/fe2/scripts/sysbar_controller.lua",
        REPO_ROOT / "games/client/game/ui/fe2/scripts/sysbar_controller.lua",
        REPO_ROOT / "games/server/override/ui/fe2/scripts/sysbar_controller.lua",
        REPO_ROOT / "games/server/game/ui/fe2/scripts/sysbar_controller.lua",
    ],
    REPO_ROOT / "games/client/override/ui/fe2/scripts/system_bar.lua": [
        REPO_ROOT / "games/client/ClientPackageFiles/ui/fe2/scripts/system_bar.lua",
        REPO_ROOT / "games/server/override/ui/fe2/scripts/system_bar.lua",
    ],
    REPO_ROOT / "games/client/override/ui/test_suite/test_suite.lua": [
        REPO_ROOT / "games/client/ClientPackageFiles/ui/test_suite/test_suite.lua",
        REPO_ROOT / "games/client/game/ui/test_suite/test_suite.lua",
    ],
    REPO_ROOT / "games/client/override/ui/test_suite/test_suite.package": [
        REPO_ROOT / "games/client/ClientPackageFiles/ui/test_suite/test_suite.package",
        REPO_ROOT / "games/client/game/ui/test_suite/test_suite.package",
    ],
    REPO_ROOT / "games/client/override/ui/test_suite/audit_known_baseline.lua": [
        REPO_ROOT / "games/client/ClientPackageFiles/ui/test_suite/audit_known_baseline.lua",
        REPO_ROOT / "games/client/game/ui/test_suite/audit_known_baseline.lua",
    ],
}


def _extract_lua_braced_block(text: str, marker: str) -> str:
    idx = text.find(marker)
    assert idx != -1, f"Marker not found: {marker}"
    brace_start = text.find("{", idx)
    assert brace_start != -1, f"Opening '{{' not found after {marker}"
    depth = 0
    in_str = None
    escaped = False
    i = brace_start
    while i < len(text):
        ch = text[i]
        if in_str:
            if escaped:
                escaped = False
            elif ch == "\\":
                escaped = True
            elif ch == in_str:
                in_str = None
        else:
            if ch in ("'", '"'):
                in_str = ch
            elif ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
                if depth == 0:
                    return text[brace_start : i + 1]
        i += 1
    raise AssertionError(f"Unbalanced braces for {marker}")


def _parse_map_practice_config(ctrl_text: str) -> dict[str, str]:
    block = _extract_lua_braced_block(ctrl_text, "SysBarController.MAP_PRACTICE_CONFIG =")
    entries: dict[str, str] = {}
    for m in re.finditer(r"^\s{1,8}([a-zA-Z0-9_]+)\s*=\s*\{", block, re.MULTILINE):
        key = m.group(1)
        sub = _extract_lua_braced_block(block[m.start() :], f"{key} =")
        entries[key] = sub
    return entries


def _parse_practice_tabs(ctrl_text: str) -> list[list[str]]:
    block = _extract_lua_braced_block(ctrl_text, "SysBarController.PRACTICE_TABS =")
    pages: list[list[str]] = []
    pos = 0
    while True:
        idx = block.find("maps = {", pos)
        if idx == -1:
            break
        maps_blk = _extract_lua_braced_block(block[idx:], "maps =")
        pages.append(re.findall(r"""['"]([a-zA-Z0-9_]+)['"]""", maps_blk))
        pos = idx + len(maps_blk)
    return pages


def _inspect_s2z_map(s2z_path: Path) -> dict[str, str]:
    info: dict[str, str] = {"stem": s2z_path.stem}
    with zipfile.ZipFile(s2z_path, "r") as zf:
        names = zf.namelist()
        assert "worldconfig" in names, f"{s2z_path.name} missing worldconfig"
        wc_raw = zf.read("worldconfig").decode("utf-8", errors="replace")
        wc_root = ET.fromstring(wc_raw)
        for k, v in wc_root.attrib.items():
            info[f"wc.{k}"] = v
        gi_candidates = [n for n in names if n.lower().endswith("game_info.entity")]
        if gi_candidates:
            gi_raw = zf.read(gi_candidates[0]).decode("utf-8", errors="replace")
            gi_root = ET.fromstring(gi_raw)
            for k, v in gi_root.attrib.items():
                info[f"gi.{k}"] = v
    return info


def test_35_s2z_maps_complete_coverage_and_tab_pagination() -> None:
    s2z_files = sorted(MAPS_DIR.glob("*.s2z"))
    assert len(s2z_files) == 35, f"Expected 35 maps in {MAPS_DIR}, found {len(s2z_files)}"
    s2z_stems = {p.stem for p in s2z_files}

    ctrl_text = SYSBAR_CTRL.read_text(encoding="utf-8")
    pkg_text = SYSBAR_PKG.read_text(encoding="utf-8")
    shim_text = SYSBAR_SHIM.read_text(encoding="utf-8")

    # PRACTICE_TABS: 12 + 12 + 11 == 35
    tab_pages = _parse_practice_tabs(ctrl_text)
    assert [len(p) for p in tab_pages] == [12, 12, 11], f"Unexpected tab sizes: {[len(p) for p in tab_pages]}"
    tab_maps = [m for page in tab_pages for m in page]
    assert len(tab_maps) == len(set(tab_maps)) == 35
    assert set(tab_maps) == s2z_stems

    # MAP_PRACTICE_CONFIG covers every .s2z map
    cfg_entries = _parse_map_practice_config(ctrl_text)
    assert set(cfg_entries.keys()) == s2z_stems

    # XML drawer has exact 35 sysbar_practice_map_btn instances across pages 1..3 + controls
    xml_maps = re.findall(r'<instance\s+name="sysbar_practice_map_btn"[^>]*\bmap="([a-zA-Z0-9_]+)"', pkg_text)
    assert len(xml_maps) == len(set(xml_maps)) == 35
    assert set(xml_maps) == s2z_stems
    assert "PRACTICE &amp; SANDBOX (35 MAPS)" in pkg_text
    assert "35 MAPS" in pkg_text
    for required_widget in (
        "sysbar_practice_drawer",
        "sysbar_prac_tab_1",
        "sysbar_prac_tab_2",
        "sysbar_prac_tab_3",
        "sysbar_practice_page_1",
        "sysbar_practice_page_2",
        "sysbar_practice_page_3",
        "sysbar_prac_prev_btn",
        "sysbar_prac_page_label",
        "sysbar_prac_next_btn",
        "sysbar_prac_sandbox",
        "ui_sysbarSandboxMode",
    ):
        assert required_widget in pkg_text, f"Missing {required_widget} in system_bar_v2.package"

    for fn in (
        "SysBar_RefreshPracticeDrawer",
        "SysBar_SetPracticeTab",
        "SysBar_PrevPracticePage",
        "SysBar_NextPracticePage",
        "SysBar_ToggleSandboxMode",
        "SysBar_LaunchPractice",
    ):
        assert f"function {fn}" in ctrl_text, f"Missing {fn} in sysbar_controller.lua"
        assert f"function {fn}" in shim_text, f"Missing {fn} shim in system_bar.lua"


def test_35_s2z_worldconfig_and_per_map_rules() -> None:
    ctrl_text = SYSBAR_CTRL.read_text(encoding="utf-8")
    cfg_entries = _parse_map_practice_config(ctrl_text)

    s2z_meta = {p.stem: _inspect_s2z_map(p) for p in sorted(MAPS_DIR.glob("*.s2z"))}
    assert len(s2z_meta) == 35

    noheroselect_maps = {
        stem
        for stem, meta in s2z_meta.items()
        if meta.get("gi.noheroselect", meta.get("wc.noheroselect", "")).lower() == "true"
    }
    expected_noheroselect = {
        "archers",
        "arena",
        "colosseum_duel",
        "empath_maze",
        "enfos",
        "pebbles_duel",
        "truedevo",
        "truedevo_10v10",
        "valkwars",
    }
    assert noheroselect_maps == expected_noheroselect, (
        f"game_info/worldconfig noheroselect mismatch: {sorted(noheroselect_maps)} != {sorted(expected_noheroselect)}"
    )

    # Custom-mode noheroselect maps keep forcePick=true and sandboxHero=nil; truedevo* uses Hero_Devourer + forcePick=false
    for stem in noheroselect_maps - {"truedevo", "truedevo_10v10"}:
        block = cfg_entries[stem]
        assert re.search(r"\bforcePick\s*=\s*true\b", block), f"{stem} must set forcePick = true"
        assert re.search(r"\bsandboxHero\s*=\s*nil\b", block), f"{stem} must keep sandboxHero = nil"

    # truedevo & truedevo_10v10 rules (no typo 'forcepick', normal + allowduplicate + Hero_Devourer)
    for stem, teamsize in (("truedevo", 5), ("truedevo_10v10", 10)):
        block = cfg_entries[stem]
        assert "forcepick" not in block, f"{stem} must use camelCase forcePick, never forcepick"
        assert re.search(r"""mode\s*=\s*["']normal["']""", block)
        assert f"teamsize:{teamsize}" in block
        assert "allowduplicate:true" in block
        assert "allheroes:true" in block
        assert re.search(r"""sandboxHero\s*=\s*["']Hero_Devourer["']""", block)
        assert re.search(r"\bforcePick\s*=\s*false\b", block)

    # nightmarish_coop rules
    nc_block = cfg_entries["nightmarish_coop"]
    assert re.search(r"\bautoJoinTeam\s*=\s*2\b", nc_block)
    assert re.search(r"""sandboxHero\s*=\s*["']Hero_Soulstealer["']""", nc_block)
    assert re.search(r"\bspawnSandboxBots\s*=\s*false\b", nc_block)

    # 1v1 / arena enemy bot seeding
    for stem, expected_bot in (
        ("valkwars", "ValkyrieBot"),
        ("pebbles_duel", "RockyBot"),
        ("colosseum_duel", "GladiatorBot"),
    ):
        block = cfg_entries[stem]
        assert re.search(rf"""seedEnemyBot\s*=\s*["']{expected_bot}["']""", block), (
            f"{stem} missing seedEnemyBot='{expected_bot}'"
        )
    assert "teamsize:1" in cfg_entries["pebbles_duel"]
    assert "AddBot 2 " in ctrl_text
    assert 'string.format("Team %d 0", joinTeam)' in ctrl_text or "Team %d 0" in ctrl_text

    # Dev maps
    for stem in ("spotlight", "tutorial"):
        block = cfg_entries[stem]
        assert "[DEV]" in block
        assert re.search(r"\bspawnSandboxBots\s*=\s*false\b", block)

    # Explicit picker skips seasonal remap
    assert "skipSeasonal" in ctrl_text and "ResolveSeasonalMap" in ctrl_text


def test_audit_known_baseline_exact_o1_contracts_and_export_ratchet() -> None:
    baseline_lua = AUDIT_BASELINE_LUA.read_text(encoding="utf-8")
    test_suite_lua = TEST_SUITE_LUA.read_text(encoding="utf-8")
    test_suite_pkg = TEST_SUITE_PKG.read_text(encoding="utf-8")
    export_ps1 = EXPORT_AUDIT_PS1.read_text(encoding="utf-8")
    baseline_data = json.loads(BASELINE_JSON.read_text(encoding="utf-8"))

    # Zero wildcard / prefix patterns allowed in frozen baseline tables
    effect_blk = _extract_lua_braced_block(baseline_lua, "AuditKnownBaseline.KNOWN_EFFECT_PATHS =")
    hero_blk = _extract_lua_braced_block(baseline_lua, "AuditKnownBaseline.KNOWN_HERO_EFFECTS =")
    ref_blk = _extract_lua_braced_block(baseline_lua, "AuditKnownBaseline.KNOWN_ENTITY_REF_PAIRS =")

    for name, blk in (("KNOWN_EFFECT_PATHS", effect_blk), ("KNOWN_HERO_EFFECTS", hero_blk), ("KNOWN_ENTITY_REF_PAIRS", ref_blk)):
        assert "*" not in blk and "?" not in blk, f"Wildcard character detected in {name}"
        assert "string.find" not in blk and "string.match" not in blk

    effect_paths = re.findall(r'\["(/[^"]+\.effect)"\]\s*=\s*true', effect_blk)
    assert len(effect_paths) == 120, f"Expected 120 exact effect paths, got {len(effect_paths)}"

    hero_keys = re.findall(r'^\t\["(hero_[^"]+)"\]\s*=\s*\{', hero_blk, re.MULTILINE)
    assert len(hero_keys) == 114, f"Expected 114 hero/alt keys, got {len(hero_keys)}"

    src_keys = re.findall(r'^\t\["(/[^"]+\.entity)"\]\s*=\s*\{', ref_blk, re.MULTILINE)
    assert len(src_keys) == 53, f"Expected 53 source entity keys, got {len(src_keys)}"
    pair_count = len(re.findall(r'\["([^"/]+)"\]\s*=\s*true', ref_blk))
    assert pair_count == 98, f"Expected 98 entity ref pairs, got {pair_count}"

    # Package & Lua integration checks
    assert '<lua file="/ui/test_suite/audit_known_baseline.lua" />' in test_suite_pkg
    assert 'checkbox="audit_filter_vanilla_baseline"' in test_suite_pkg
    assert 'cvar="ui_testSuiteFilterVanillaBaseline"' in test_suite_pkg
    assert "ui_testSuiteFilterVanillaBaseline" in test_suite_lua
    assert "IsKnownMissingEntityRef" in test_suite_lua
    assert "IsKnownMissingEffect" in test_suite_lua

    # Ensure export_audit_log.ps1 disambiguates entity_script_refs vs heroes_and_alts & supports -Strict
    assert "entity_script_refs" in export_ps1
    assert "heroes_and_alts" in export_ps1
    assert "[switch]$Strict" in export_ps1
    assert "unexpectedErrors" in export_ps1

    summaries = baseline_data.get("summaries", {})
    assert len(summaries) == 10, f"Expected 10 distinct suite summaries in baseline.json, got {len(summaries)}: {list(summaries.keys())}"
    assert "entity_script_refs" in summaries
    assert "heroes_and_alts" in summaries
    assert summaries["heroes_and_alts"]["passed"] == 1533
    assert summaries["heroes_and_alts"]["total"] == 1647
    assert summaries["name colors"]["passed"] == 48
    assert summaries["name colors"]["total"] == 48


def test_h2b_luac51_compile_and_mirror_byte_parity() -> None:
    luac_bin = next((p for p in LUAC51_CANDIDATES if p.exists()), None)
    assert luac_bin is not None, f"luac51.exe not found in {LUAC51_CANDIDATES}"
    for lua_path in (SYSBAR_CTRL, SYSBAR_SHIM, TEST_SUITE_LUA, AUDIT_BASELINE_LUA):
        proc = subprocess.run([str(luac_bin), "-p", str(lua_path)], capture_output=True, text=True, check=False)
        assert proc.returncode == 0, f"luac51 -p failed on {lua_path}: {proc.stderr}"

    for src, targets in MIRROR_MATRIX.items():
        assert src.exists(), f"Primary missing: {src}"
        src_sha = hashlib.sha256(src.read_bytes()).hexdigest()
        for dst in targets:
            assert dst.exists(), f"Mirror missing: {dst}"
            dst_sha = hashlib.sha256(dst.read_bytes()).hexdigest()
            assert dst_sha == src_sha, f"Mirror drift between {src} ({src_sha}) and {dst} ({dst_sha})"


def test_runtime_audit_baseline_and_sysbar_lua_behavior(tmp_path: Path) -> None:
    verifier_bin = next((p for p in LUA_VERIFIER_CANDIDATES if p.exists()), None)
    assert verifier_bin is not None, f"LuaVerifier.exe not found in {LUA_VERIFIER_CANDIDATES}"

    probe_lua = tmp_path / "probe_h2b_runtime.lua"
    baseline_posix = AUDIT_BASELINE_LUA.as_posix()
    ctrl_posix = SYSBAR_CTRL.as_posix()
    shim_posix = SYSBAR_SHIM.as_posix()

    probe_script = f"""
local baseline = dofile("{baseline_posix}")
assert(type(baseline) == "table" and baseline.COUNTS.effectPaths == 120)
assert(baseline.IsKnownMissingEffect("Hero_Arachna.Alt7", "/heroes/arachna/alt7/effects/body.effect") == true)
assert(baseline.IsKnownMissingEffect("Hero_Arachna.Alt7", "heroes\\\\Arachna\\\\Alt7\\\\effects\\\\BODY.EFFECT") == true)
assert(baseline.IsKnownMissingEffect("Hero_Pyromancer", "/heroes/arachna/alt7/effects/body.effect") == false)
assert(baseline.IsKnownMissingEffect("Hero_Arachna.Alt7", "/heroes/arachna/alt7/effects/head.effect") == false)
assert(baseline.IsKnownMissingEntityRef("/reborn_assets/reborn_heroes/artesia/ability_01/ability.entity", "Reborn_Gadget_Artesia_Ability1_Reveal") == true)
assert(baseline.IsKnownMissingEntityRef("/reborn_assets/reborn_heroes/custom/ability.entity", "Reborn_Gadget_Artesia_Ability1_Reveal") == false)

local cvars = {{}}
local function makeCvar(name, initVal)
    local c = {{ _v = initVal }}
    function c:GetString() return tostring(self._v) end
    function c:GetNumber() return tonumber(self._v) or 0 end
    function c:GetBool()
        if type(self._v) == "boolean" then return self._v end
        local s = string.lower(tostring(self._v))
        return s == "true" or s == "1"
    end
    function c:Set(v) self._v = v end
    function c:SetBool(v) self._v = (v == true) end
    function c:SetSave() end
    cvars[name] = c
    return c
end
Cvar = {{
    GetCvar = function(name) return cvars[name] end,
    CreateCvar = function(name, _, val) return makeCvar(name, val) end,
}}
Set = function(name, val)
    if not cvars[name] then makeCvar(name, val) else cvars[name]:Set(val) end
end
GetCvarBool = function(name) return cvars[name] and cvars[name]:GetBool() or false end
GetCvarNumber = function(name) return cvars[name] and cvars[name]:GetNumber() or 0 end
GetCvarString = function(name) return cvars[name] and cvars[name]:GetString() or "" end

local widgets = {{}}
local function getMockWidget(name)
    if not widgets[name] then
        widgets[name] = {{
            _name = name, _visible = false, _text = "", _color = "", _border = "",
            SetVisible = function(self, v) self._visible = v end,
            IsVisible = function(self) return self._visible end,
            SetText = function(self, t) self._text = t end,
            SetColor = function(self, c) self._color = c end,
            SetBorderColor = function(self, c) self._border = c end,
            DoEvent = function() end,
            FadeIn = function(self) self._visible = true end,
            FadeOut = function(self) self._visible = false end,
        }}
    end
    return widgets[name]
end

local watches = {{}}
main = {{
    RegisterWatch = function(_, evt, fn)
        watches[evt] = watches[evt] or {{}}
        table.insert(watches[evt], fn)
    end,
    GetWidget = function(_, name) return getMockWidget(name) end,
}}
UIManager = {{
    GetInterface = function(iface)
        if iface == "main" or iface == "main_v2" then return main end
        return nil
    end,
}}
GetWidget = function(name) return getMockWidget(name) end
Echo = function() end
Trigger = function() end
local cmds = {{}}
Cmd = function(c) table.insert(cmds, c) end
dofile("{ctrl_posix}")
dofile("{shim_posix}")

-- Verify TestSuite baseline filter CVars & helpers load and toggle cleanly
ui_testSuiteLoaded = nil
Testing = {{ EntityExists = function() return false end, FileExists = function() return false end }}
Translate = function(k) return tostring(k) end
explode = function(_, s) return {{ tostring(s) }} end
dofile("{TEST_SUITE_LUA.as_posix()}")
assert(TestSuite.IsVanillaBaselineFilterEnabled() == true, "Filter must default ON")
TestSuite.SetVanillaBaselineFilter(false)
assert(TestSuite.IsVanillaBaselineFilterEnabled() == false, "SetVanillaBaselineFilter(false) must turn OFF")
TestSuite.SetVanillaBaselineFilter(true)
assert(TestSuite.IsVanillaBaselineFilterEnabled() == true, "SetVanillaBaselineFilter(true) must restore ON")

SysBar_SetPracticeTab(1)
assert(widgets["sysbar_practice_page_1"]._visible == true)
assert(widgets["sysbar_practice_page_2"]._visible == false)
assert(widgets["sysbar_prac_page_label"]._text == "Page 1/3")
SysBar_NextPracticePage()
assert(widgets["sysbar_practice_page_2"]._visible == true)
assert(widgets["sysbar_prac_page_label"]._text == "Page 2/3")
SysBar_PrevPracticePage()
SysBar_PrevPracticePage()
assert(widgets["sysbar_practice_page_3"]._visible == true)
assert(widgets["sysbar_prac_page_label"]._text == "Page 3/3")

SysBar_ToggleSandboxMode()
assert(SysBar_IsSandboxModeEnabled() == true)
assert(widgets["sysbar_prac_sandbox_border"]._color == "#fde047")
assert(widgets["sysbar_prac_caldavar_border"]._color == "#f59e0b")
SysBar_ToggleSandboxMode()
assert(SysBar_IsSandboxModeEnabled() == false)
assert(widgets["sysbar_prac_sandbox_border"]._color == "#64748b")
assert(widgets["sysbar_prac_caldavar_border"]._color == "#475569")

local seasonalCalled = 0
KongorMusicController = {{
    ResolveSeasonalMap = function(m)
        seasonalCalled = seasonalCalled + 1
        return m .. "_halloween"
    end,
}}
local resExplicit = SysBar_LaunchPractice("caldavar", true, true)
assert(seasonalCalled == 0, "explicitFromPicker=true must skip ResolveSeasonalMap")
assert(resExplicit.map == "caldavar" and resExplicit.isSandbox == true)

local resAuto = SysBar_LaunchPractice("caldavar", false, false)
assert(seasonalCalled == 1 and resAuto.map == "caldavar_halloween")

local resDevo = SysBar_LaunchPractice("truedevo", true, true)
assert(SysBarController.MAP_PRACTICE_CONFIG["truedevo"].forcePick == false)
assert(SysBarController.MAP_PRACTICE_CONFIG["truedevo"].sandboxHero == "Hero_Devourer")
assert(resDevo.mode == "normal" and string.find(resDevo.command, "allowduplicate:true", 1, true) ~= nil)

cmds = {{}}
local resCoop = SysBar_LaunchPractice("nightmarish_coop", false, true)
assert(resCoop.team == 2 and SysBarController.MAP_PRACTICE_CONFIG["nightmarish_coop"].sandboxHero == "Hero_Soulstealer" and resCoop.spawnSandboxBots == false)
for _, fn in ipairs(watches["GamePhase"] or {{}}) do fn(nil, 1) end
local foundTeam2 = false
for _, c in ipairs(cmds) do if c == "Team 2 0" then foundTeam2 = true end end
assert(foundTeam2, "nightmarish_coop must emit Team 2 0")

cmds = {{}}
local resDuel = SysBar_LaunchPractice("pebbles_duel", false, true)
assert(SysBarController.MAP_PRACTICE_CONFIG["pebbles_duel"].forcePick == true and SysBarController.MAP_PRACTICE_CONFIG["pebbles_duel"].sandboxHero == nil and resDuel.seedEnemyBot == "RockyBot")
for _, fn in ipairs(watches["GamePhase"] or {{}}) do fn(nil, 1) end
local foundBot = false
for _, c in ipairs(cmds) do if c == "AddBot 2 RockyBot" then foundBot = true end end
assert(foundBot, "pebbles_duel must emit AddBot 2 RockyBot")

print("RUNTIME_H2B_PROBE_PASS")
"""
    probe_lua.write_text(probe_script, encoding="utf-8")
    proc = subprocess.run([str(verifier_bin), str(probe_lua)], capture_output=True, text=True, check=False)
    assert proc.returncode == 0, f"LuaVerifier failed:\\nSTDOUT:\\n{proc.stdout}\\nSTDERR:\\n{proc.stderr}"
    assert "RUNTIME_H2B_PROBE_PASS" in proc.stdout


# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""Unit and contract tests for cross-match map & Test++ CVar isolation (Fix 3.5H).

protects: Cross-match gameplay CVar baseline resets in client & server game_settings.cfg
          so map-specific overrides (e.g. soccer.s2z sv_spawnCreeps "false", midwars.s2z
          hero_goldLossPerLevel "0") and Practice Test++ toggles never bleed into subsequent matches.
fails_when: Any map_settings.cfg in games/client/game/maps/*.s2z or spawn/cheat toggle in
            test_suite.package mutates a registered K2 gameplay CVar that lacks a baseline reset
            in game_settings.cfg, or client/server game_settings.cfg mirrors drift out of sync.
seam: Real games/{client,server}/{game,override}/game_settings.cfg files, real *.s2z map archives,
      real test_suite.package XML, and native K2 engine binaries (when present locally).
"""

from __future__ import annotations

import pathlib
import re
import unittest
import zipfile

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]

GAME_SETTINGS_PATHS = (
    REPO_ROOT / "games/client/game/game_settings.cfg",
    REPO_ROOT / "games/client/override/game_settings.cfg",
    REPO_ROOT / "games/server/game/game_settings.cfg",
    REPO_ROOT / "games/server/override/game_settings.cfg",
)

# Known obsolete legacy S2 map_settings.cfg keys that do not exist in 64-bit K2 binaries
# (adding these to game_settings.cfg would emit 'Unknown CVar' console warnings).
OBSOLETE_UNREGISTERED_MAP_CVARS = frozenset(
    {
        "hero_expbountyperlevel",
        "hero_expunsharedbountyperlevel",
        "hero_expunsharedbountyperlevel_casual",
        "sv_heroallbantransitiontime",
        "sv_herobantransitiontime",
        "hero_respawntimeperlevel",
        "hero_respawntimeperlevel_casual",
        "cg_altlockcamera",
        "cg_lockcameragame",
        "gfx_foliage",
        "scene_foliagedrawdistance",
        "cg_lockcameramod",
        "cam_smoothpositionhalflife",
        "cam_smoothangleshalflife",
        "g_powerups",
        "sv_heroselecttimeap",
    }
)

REQUIRED_BASELINE_RESETS = {
    "sv_spawncreeps": "true",
    "sv_spawntrees": "true",
    "sv_spawnpowerups": "true",
    "sv_spawncritters": "true",
    "g_spawnneutrals": "true",
    "g_spawnbosses": "true",
    "sv_disableaiunitcontrol": "false",
    "g_disablefogofwar": "false",
    "g_noitemdrop": "false",
    "g_altinfoplayercolor": "false",
    "hero_goldlossperlevel": "30",
    "hero_goldlossperlevel_casual": "0",
}

SET_LINE_RE = re.compile(r'^[Ss]et(?:Save)?\s+"?([^\s"]+)"?\s+"?([^"]*)"?')


def decode_k2_cfg(raw: bytes) -> str:
    if raw[:2] == b"\xff\xfe" or b"\x00" in raw[:40]:
        return raw.decode("utf-16-le", errors="replace").lstrip("\ufeff")
    return raw.decode("utf-8", errors="replace").lstrip("\ufeff")


def parse_cfg_cvars(text: str) -> dict[str, str]:
    parsed: dict[str, str] = {}
    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("//"):
            continue
        match = SET_LINE_RE.match(line)
        if match:
            parsed[match.group(1).lower()] = match.group(2)
    return parsed


def find_unisolated_map_cvars(
    baseline_cvars: dict[str, str],
    map_cvar_mutations: dict[str, list[tuple[str, str]]],
    obsolete_allowlist: frozenset[str] = OBSOLETE_UNREGISTERED_MAP_CVARS,
) -> dict[str, list[tuple[str, str]]]:
    missing: dict[str, list[tuple[str, str]]] = {}
    for cvar_lower, origins in map_cvar_mutations.items():
        if cvar_lower in obsolete_allowlist:
            continue
        if cvar_lower not in baseline_cvars:
            missing[cvar_lower] = origins
    return missing


class MapCvarIsolationTests(unittest.TestCase):
    def test_all_four_game_settings_mirrors_exist_and_have_required_resets(self) -> None:
        normalized_texts: dict[str, str] = {}
        for cfg_path in GAME_SETTINGS_PATHS:
            self.assertTrue(
                cfg_path.is_file(),
                f"Missing game_settings.cfg mirror at {cfg_path}. "
                f"Sync all 4 client/server game_settings.cfg files for cross-match CVar isolation.",
            )
            raw = cfg_path.read_bytes()
            self.assertNotEqual(
                raw[:2],
                b"\xff\xfe",
                f"{cfg_path} should be UTF-8 without UTF-16LE BOM for cross-tree parity.",
            )
            txt = decode_k2_cfg(raw).replace("\r\n", "\n").strip()
            normalized_texts[str(cfg_path)] = txt
            parsed = parse_cfg_cvars(txt)
            for key_lower, expected_val in REQUIRED_BASELINE_RESETS.items():
                self.assertIn(
                    key_lower,
                    parsed,
                    f"{cfg_path} is missing baseline reset for '{key_lower}'.",
                )
                self.assertEqual(
                    parsed[key_lower],
                    expected_val,
                    f"{cfg_path} reset '{key_lower}' to '{parsed[key_lower]}', expected '{expected_val}'.",
                )

        unique_contents = set(normalized_texts.values())
        self.assertEqual(
            len(unique_contents),
            1,
            "All 4 client/server game_settings.cfg mirrors must be text-identical (0 cross-tree drift).",
        )

    def test_every_map_settings_and_test_suite_cvar_is_isolated(self) -> None:
        baseline_cfg = REPO_ROOT / "games/client/override/game_settings.cfg"
        baseline_cvars = parse_cfg_cvars(decode_k2_cfg(baseline_cfg.read_bytes()))

        maps_dir = REPO_ROOT / "games/client/game/maps"
        if maps_dir.is_dir():
            s2z_archives = sorted(maps_dir.glob("*.s2z"))
            self.assertGreaterEqual(len(s2z_archives), 8, "Expected at least 8 map .s2z archives.")
            mutations: dict[str, list[tuple[str, str]]] = {}
            for s2z in s2z_archives:
                with zipfile.ZipFile(s2z) as zf:
                    for entry in zf.namelist():
                        if entry.lower().endswith("map_settings.cfg"):
                            cfg_txt = decode_k2_cfg(zf.read(entry))
                            for k, v in parse_cfg_cvars(cfg_txt).items():
                                mutations.setdefault(k, []).append((f"{s2z.name}::{entry}", v))

            missing = find_unisolated_map_cvars(baseline_cvars, mutations)
            self.assertEqual(
                missing,
                {},
                f"Registered map_settings.cfg CVars missing baseline resets in game_settings.cfg: {missing}. "
                f"Add retail default Set lines to games/{{client,server}}/{{game,override}}/game_settings.cfg.",
            )

        test_suite_pkg = REPO_ROOT / "games/client/override/ui/test_suite/test_suite.package"
        if test_suite_pkg.is_file():
            pkg_text = test_suite_pkg.read_text(encoding="utf-8", errors="replace")
            checkbox_cvars = re.findall(
                r'test_suite_(?:basic_)?checkbox[^>]*\bcvar="([^"]+)"', pkg_text
            )
            self.assertGreaterEqual(len(checkbox_cvars), 6)
            for cvar_name in checkbox_cvars:
                if cvar_name.lower().startswith(("sv_spawn", "g_spawn", "g_disablefogofwar")):
                    self.assertIn(
                        cvar_name.lower(),
                        baseline_cvars,
                        f"Test++ checkbox CVar '{cvar_name}' in test_suite.package lacks baseline reset in game_settings.cfg.",
                    )

    def test_negative_control_detects_unisolated_map_cvar_mutation(self) -> None:
        baseline = {"sv_spawncreeps": "true"}
        mutations = {
            "sv_spawncreeps": [("soccer.s2z::map_settings.cfg", "false")],
            "sv_spawnpowerups": [("custom.s2z::map_settings.cfg", "false")],
        }
        missing = find_unisolated_map_cvars(baseline, mutations)
        self.assertEqual(list(missing.keys()), ["sv_spawnpowerups"])


if __name__ == "__main__":
    unittest.main()

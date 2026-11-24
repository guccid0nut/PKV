# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Unit and regression tests for Issue #5: Ensure `lobby_ready` button is always visible
on the Hero Selection screen across ALL maps and modes.

Value-Weighted Testing Gate:
  - protects:
      1. Issue #5 contract: `lobby_ready` button visibility across all maps and modes:
         - The `<button name="lobby_ready">` widget must exist as a direct child of
           `<panel name="lobby_picker">` (placed right after `lobby_picker_herobuttons`'s closing
           panel tag and before `lobby_picker_filter_root`). It must NOT be inside
           `<panel name="lobby_picker_herobuttons">` because `Lobby_V2:OpenAvatars` (`lobby_v2.lua:3212`)
           explicitly hides `lobby_picker_herobuttons` whenever a hero is selected (including automatically
           at phase start in All Random `ar`). Direct placement under `lobby_picker` guarantees the button
           remains visible across all modes even while the Alt Avatar selection carousel is active.
         - The button must NOT carry `watch="LobbyGameInfo"` or `ontriggerlua="self:SetVisible(param8 == '-1')"`,
           which previously suppressed the ready button in Public, Matchmaking, and P2P matches,
           restricting it exclusively to practice mode (`param8 == '-1'`).
         - The button must have `visible="1"`, maintain native `onclick="Ready();"`, and include
           descriptive telemetry for player click actions with auto-close logic for `lobby_picker_avatars`.
      2. Lua client controller contract:
         - `lobby_v2.lua` dynamically controls button interaction state via
           `GetWidget('lobby_ready'):SetEnabled(not isReady and hasSelectedHero)`.
         - `lobby_v2.lua` never calls `GetWidget('lobby_ready'):SetVisible(false)`,
           ensuring the button stays visible throughout the hero selection phase.
      3. Cross-tree mirror parity:
         - Byte-identical SHA-256 hash parity between `games/client/override/ui/fe2/newui/lobby_v2.package`
           and `games/client/ClientPackageFiles/ui/fe2/newui/lobby_v2.package`.
  - fails_when:
      1. `<button name="lobby_ready">` is missing or nested under a conditional mode/map filter panel.
      2. `<button name="lobby_ready">` retains `watch="LobbyGameInfo"` or any `param8 == '-1'` visibility constraint.
      3. `<button name="lobby_ready">` is missing `onclick="Ready();"` or has `visible="0"`.
      4. Any synced mirror of `lobby_v2.package` exhibits SHA-256 drift.
      5. `lobby_v2.lua` fails to enable/disable `lobby_ready` based on selection state or suppresses visibility via `SetVisible(false)`.
  - seam:
      Real filesystem I/O, exact XML AST validation via `xml.etree.ElementTree`, SHA-256 byte hashing,
      and regex-based Lua script validation.
"""

from __future__ import annotations

import hashlib
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET

REPO_ROOT = Path(__file__).resolve().parents[2]

OVERRIDE_PACKAGE = REPO_ROOT / "games" / "client" / "override" / "ui" / "fe2" / "newui" / "lobby_v2.package"
CLIENT_PKG_PACKAGE = REPO_ROOT / "games" / "client" / "ClientPackageFiles" / "ui" / "fe2" / "newui" / "lobby_v2.package"
GAME_PACKAGE = REPO_ROOT / "games" / "client" / "game" / "ui" / "fe2" / "fe2" / "newui" / "lobby_v2.package"

LOBBY_V2_LUA_OVERRIDE = REPO_ROOT / "games" / "client" / "override" / "ui" / "scripts" / "newui" / "lobby_v2.lua"


def sha256_file(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as f:
        while chunk := f.read(65536):
            hasher.update(chunk)
    return hasher.hexdigest()


class TestLobbyReadyButtonAllModes(unittest.TestCase):
    """Regression test suite for Issue #5: lobby_ready button visibility in all maps and modes."""

    def test_lobby_ready_button_hierarchy_and_attributes(self):
        """Assert <button name="lobby_ready"> is directly inside lobby_picker (not lobby_picker_herobuttons)."""
        self.assertTrue(OVERRIDE_PACKAGE.exists(), f"Missing {OVERRIDE_PACKAGE}")
        tree = ET.parse(OVERRIDE_PACKAGE)
        root = tree.getroot()

        parent_map = {child: parent for parent in root.iter() for child in parent}
        ready_buttons = [elem for elem in root.iter() if elem.tag == "button" and elem.attrib.get("name") == "lobby_ready"]

        self.assertEqual(len(ready_buttons), 1, "Expected exactly one active <button name='lobby_ready'> in lobby_v2.package")
        btn = ready_buttons[0]

        # 1. Direct parent must be lobby_picker (NOT lobby_picker_herobuttons, which OpenAvatars hides at lobby_v2.lua:3212)
        parent = parent_map.get(btn)
        self.assertIsNotNone(parent, "lobby_ready must have a parent panel")
        self.assertEqual(parent.tag, "panel", "lobby_ready parent must be a <panel>")
        self.assertEqual(
            parent.attrib.get("name"),
            "lobby_picker",
            f"lobby_ready direct parent must be 'lobby_picker' to survive OpenAvatars, but got '{parent.attrib.get('name')}'"
        )

        # 2. Direct parent must not restrict visibility by game mode (HeroSelectInfo / param2 ~= 'ar')
        self.assertIsNone(
            parent.attrib.get("watch"),
            f"lobby_ready direct parent should not watch HeroSelectInfo, found watch='{parent.attrib.get('watch')}'"
        )
        self.assertNotIn(
            "param2 ~= 'ar'",
            parent.attrib.get("ontriggerlua", ""),
            "lobby_ready parent must not filter on param2 ~= 'ar'"
        )

        # 3. Button must not carry watch="LobbyGameInfo" or param8 == '-1' (practice-only restriction)
        watch_attr = btn.attrib.get("watch")
        self.assertNotEqual(
            watch_attr,
            "LobbyGameInfo",
            "lobby_ready must NOT watch 'LobbyGameInfo' (which hid it outside practice mode)"
        )
        ontrigger_attr = btn.attrib.get("ontriggerlua", "")
        self.assertNotIn(
            "param8 == '-1'",
            ontrigger_attr,
            "lobby_ready must NOT contain ontriggerlua with 'param8 == -1'"
        )

        # 4. Button core attributes
        self.assertEqual(btn.attrib.get("visible"), "1", "lobby_ready must have visible='1'")
        self.assertEqual(btn.attrib.get("onclick"), "Ready();", "lobby_ready must trigger Ready(); on click")
        self.assertIn("Echo", btn.attrib.get("onclicklua", ""), "lobby_ready should have click telemetry")
        self.assertIn("CloseAvatars", btn.attrib.get("onclicklua", ""), "lobby_ready onclicklua must auto-close avatar carousel if open")

    def test_lobby_package_mirror_parity(self):
        """Assert byte-for-byte SHA-256 parity across all synced lobby_v2.package mirrors."""
        self.assertTrue(OVERRIDE_PACKAGE.exists(), f"Missing override package: {OVERRIDE_PACKAGE}")
        self.assertTrue(CLIENT_PKG_PACKAGE.exists(), f"Missing ClientPackageFiles package: {CLIENT_PKG_PACKAGE}")
        override_hash = sha256_file(OVERRIDE_PACKAGE)
        client_pkg_hash = sha256_file(CLIENT_PKG_PACKAGE)

        self.assertEqual(
            override_hash,
            client_pkg_hash,
            f"Hash drift detected between override and ClientPackageFiles:\n"
            f"  override:           {override_hash}\n"
            f"  ClientPackageFiles: {client_pkg_hash}"
        )
        if GAME_PACKAGE.exists():
            game_hash = sha256_file(GAME_PACKAGE)
            self.assertEqual(
                override_hash,
                game_hash,
                f"Hash drift detected between override and game:\n"
                f"  override: {override_hash}\n"
                f"  game:     {game_hash}"
            )

    def test_lobby_v2_lua_readiness_logic(self):
        """Assert lobby_v2.lua enables/disables lobby_ready based on state and never hides it."""
        self.assertTrue(LOBBY_V2_LUA_OVERRIDE.exists(), f"Missing {LOBBY_V2_LUA_OVERRIDE}")
        lua_content = LOBBY_V2_LUA_OVERRIDE.read_text(encoding="utf-8", errors="replace")

        # 1. Must contain SetEnabled based on isReady and hasSelectedHero (either direct or via cached readyW widget handle)
        enabled_match = re.search(
            r"(?:GetWidget\(['\"]lobby_ready['\"]\)|readyW):SetEnabled\([^\r\n]*hasSelectedHero[^\r\n]*\)",
            lua_content
        )
        self.assertIsNotNone(
            enabled_match,
            "lobby_v2.lua must call GetWidget('lobby_ready'):SetEnabled(...) conditioned on hasSelectedHero"
        )

        # 2. Must NEVER call SetVisible(false) on lobby_ready
        hidden_match = re.search(
            r"GetWidget\(['\"]lobby_ready['\"]\):SetVisible\(\s*false\s*\)",
            lua_content
        )
        self.assertIsNone(
            hidden_match,
            "lobby_v2.lua must never call GetWidget('lobby_ready'):SetVisible(false)"
        )


if __name__ == "__main__":
    unittest.main()

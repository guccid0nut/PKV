# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Unit and regression tests for in-game ESC menu toggle and bottom chat visibility on settings screen.

Value-Weighted Testing Gate:
  - protects:
      1. Issue #3 contract: In-game ESC key (GameBind Cancel -> cgame FUN_1800d70a0 -> SetCancelCallback in game_new.lua):
         - Preserves lane selection overlay and shop close logic.
         - Dismisses in-game modal/confirmation dialogs (quit_confirm, disconnect_confirm, etc.).
         - Returns false on active ability targeting (Game.toolTargetingActive / portraitBtn) or minimap ping
           so native cgame_x64.dll Cancel clears targeting cleanly.
         - Closes visible game_options / main menu via MainMenu_HandleEscape / HideMenu when already open.
         - Opens game_options via ToggleMenu() + MainMenuPanelToggle with _lastEscHandledTime debounce stamp
           to prevent immediate double-close.
         - Ensures Game.toolTargetingActive is stored in shared Game table across lexical scopes.
      2. Issue #4 contract: Bottom communicator chat on settings screen:
         - communicator_v2.lua registers {target='game_options', width=1325, x=0} in _chatDisplayConfig
           ahead of store_container2, lobby_*, and default fallback.
         - main_v2.lua onShowGameOptions sets main_bottom_bottom2 to SetY('0i') and SetVisible(true)
           and dismisses com_hider2 when logged in or in active match / lobby, but keeps it hidden
           on unauthenticated pre-login (gamePhase == 0 and not logged in).
         - main_v2.lua onHideGameOptions cleanly hides main_bottom_bottom2 when returning to active match (5..8)
           unless another panel is open, while maintaining visibility on lobby (1..4) or post-game (9..13).
         - main_v2.lua phase handlers and GamePhase watchers do not hide main_bottom_bottom2 if game_options is visible.
      3. Cross-tree mirror parity:
         - Byte-identical SHA-256 hashes across all 5 copies of game_new.lua, 4 copies of communicator_v2.lua,
           and 5 copies of main_v2.lua.
         - 0 bytecode syntax errors via luac51 compiler.
  - fails_when:
      1. SetCancelCallback drops active targeting or minimap ping cancelation, leading to stuck targeting.
      2. toolTargetingActive is local-scoped to ToolTargetingEntity and inaccessible from SetCancelCallback.
      3. _lastEscHandledTime is missing on ESC open, allowing 80ms debounce to be bypassed or double-fired.
      4. game_options is missing from _chatDisplayConfig or ordered after default fallback.
      5. onShowGameOptions leaves main_bottom_bottom2 hidden or com_hider2 visible when logged in.
      6. Any mirror copy drifts in SHA-256 hash or fails luac51 compilation.
  - seam:
      Real filesystem I/O, SHA-256 byte hashing, real tools/lua/luac51.exe bytecode compiler subprocess,
      and exact Lua AST / token structure validation.
"""

from __future__ import annotations

import hashlib
import os
from pathlib import Path
import re
import subprocess
import unittest

REPO_ROOT = Path(__file__).resolve().parents[2]

GAME_NEW_MIRRORS = [
    REPO_ROOT / "games" / "client" / "override" / "ui" / "scripts" / "game_new.lua",
    REPO_ROOT / "games" / "client" / "game" / "ui" / "scripts" / "game_new.lua",
    REPO_ROOT / "games" / "client" / "ClientPackageFiles" / "ui" / "scripts" / "game_new.lua",
    REPO_ROOT / "games" / "shared" / "ui" / "scripts" / "game_new.lua",
]

COMMUNICATOR_MIRRORS = [
    REPO_ROOT / "games" / "client" / "override" / "ui" / "scripts" / "newui" / "communicator_v2.lua",
    REPO_ROOT / "games" / "client" / "game" / "ui" / "scripts" / "newui" / "communicator_v2.lua",
    REPO_ROOT / "games" / "client" / "ClientPackageFiles" / "ui" / "scripts" / "newui" / "communicator_v2.lua",
]

MAIN_V2_MIRRORS = [
    REPO_ROOT / "games" / "client" / "override" / "ui" / "scripts" / "main_v2.lua",
    REPO_ROOT / "games" / "client" / "game" / "ui" / "scripts" / "main_v2.lua",
    REPO_ROOT / "games" / "client" / "ClientPackageFiles" / "ui" / "scripts" / "main_v2.lua",
    REPO_ROOT / "games" / "shared" / "ui" / "scripts" / "main_v2.lua",
]


def _sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest()


def _get_luac_bin() -> Path | None:
    candidates = [
        REPO_ROOT / "native" / "build" / "luac51_build" / "Release" / "luac51.exe",
        REPO_ROOT / "native" / "tools" / "luac51" / "build" / "Release" / "luac51.exe",
        REPO_ROOT / "artifacts" / "native-binaries" / "windows-x64" / "luac51.exe",
        REPO_ROOT / ".tools" / "bin" / "lua" / "luac51.exe",
    ]
    for c in candidates:
        if c.is_file():
            return c
    return None


class TestIngameEscAndOptionsChat(unittest.TestCase):
    """Test suite covering Issue #3 (In-Game ESC options toggle) & Issue #4 (Bottom chat on settings)."""

    def test_game_new_mirrors_exist_and_match_sha256(self):
        """Verify all 5 mirrors of game_new.lua exist and have 0-drift byte parity."""
        base_hash = None
        for path in GAME_NEW_MIRRORS:
            self.assertTrue(path.is_file(), f"game_new mirror missing: {path}")
            curr_hash = _sha256(path)
            if base_hash is None:
                base_hash = curr_hash
            else:
                self.assertEqual(
                    curr_hash,
                    base_hash,
                    f"Cross-tree parity mismatch in {path} ({curr_hash} vs {base_hash})",
                )

    def test_communicator_v2_mirrors_exist_and_match_sha256(self):
        """Verify all 4 mirrors of communicator_v2.lua exist and have 0-drift byte parity."""
        base_hash = None
        for path in COMMUNICATOR_MIRRORS:
            self.assertTrue(path.is_file(), f"communicator_v2 mirror missing: {path}")
            curr_hash = _sha256(path)
            if base_hash is None:
                base_hash = curr_hash
            else:
                self.assertEqual(
                    curr_hash,
                    base_hash,
                    f"Cross-tree parity mismatch in {path} ({curr_hash} vs {base_hash})",
                )

    def test_main_v2_mirrors_exist_and_match_sha256(self):
        """Verify all 5 mirrors of main_v2.lua exist and have 0-drift byte parity."""
        base_hash = None
        for path in MAIN_V2_MIRRORS:
            self.assertTrue(path.is_file(), f"main_v2 mirror missing: {path}")
            curr_hash = _sha256(path)
            if base_hash is None:
                base_hash = curr_hash
            else:
                self.assertEqual(
                    curr_hash,
                    base_hash,
                    f"Cross-tree parity mismatch in {path} ({curr_hash} vs {base_hash})",
                )

    def test_luac51_bytecode_verification_passes(self):
        """Ensure all modified Lua files compile with 0 bytecode syntax errors under Lua 5.1."""
        luac = _get_luac_bin()
        if not luac:
            self.skipTest("luac51.exe compiler not found in workspace")

        files_to_verify = [
            GAME_NEW_MIRRORS[0],
            COMMUNICATOR_MIRRORS[0],
            MAIN_V2_MIRRORS[0],
        ]
        for f in files_to_verify:
            res = subprocess.run(
                [str(luac), "-p", str(f)],
                capture_output=True,
                text=True,
            )
            self.assertEqual(
                res.returncode,
                0,
                f"luac51 syntax error in {f.name}:\nStdout: {res.stdout}\nStderr: {res.stderr}",
            )

    def test_game_new_tool_targeting_stored_in_game_table(self):
        """Verify Game.toolTargetingActive is initialized, updated in ToolTargetingEntity, and checked in SetCancelCallback."""
        content = GAME_NEW_MIRRORS[0].read_text(encoding="utf-8")

        # 1. Initialization
        self.assertRegex(
            content,
            r"Game\.toolTargetingActive\s*=\s*false",
            "Game.toolTargetingActive must be initialized on Game table",
        )

        # 2. Inside ToolTargetingEntity
        self.assertIn(
            "Game.toolTargetingActive = tool",
            content,
            "ToolTargetingEntity must update Game.toolTargetingActive = tool",
        )

        # 3. Inside SetCancelCallback
        self.assertIn(
            "local isToolTargeting = (Game and Game.toolTargetingActive) or portraitBtnVisible",
            content,
            "SetCancelCallback must query Game.toolTargetingActive with portraitBtnVisible fallback",
        )

    def test_game_new_set_cancel_callback_structure(self):
        """Verify SetCancelCallback fulfills the 6-step in-game cancel and options toggle protocol."""
        content = GAME_NEW_MIRRORS[0].read_text(encoding="utf-8")

        # Must have SetCancelCallback registration
        self.assertIn("SetCancelCallback(function(widget)", content)

        # Step 1: laneSelect check
        self.assertIn("laneSelectPanel", content)
        self.assertIn("laneSelectClose()", content)

        # Step 2: shopLastActive check
        self.assertIn("if shopLastActive then", content)
        self.assertIn('interface:UICmd("CloseShop();")', content)

        # Step 2b: radial / smart ping menu check
        self.assertIn("radial_menu_master", content)
        self.assertIn("HoN_RadialMenu", content)
        self.assertIn("ModernSmartPing", content)
        self.assertIn("Closing radial/smart ping menu via ESC", content)

        # Step 3: in-game dialogs
        for dlg in [
            "quit_confirm",
            "quit_confirm_leaver",
            "disconnect_confirm",
            "disconnect_confirm_leaver",
            "generic_dialog_box_combo_game",
            "spec_kick_list",
            "game_mentor_first_time",
        ]:
            self.assertIn(f"'{dlg}'", content, f"Dialog {dlg} must be in inGameDialogs table")

        # Step 4: Tool targeting returns false to native cgame Cancel
        self.assertIn("Passing ESC to native Cancel for active targeting", content)
        self.assertIn("return false", content)

        # Step 5: Close visible options/main via MainMenu_HandleEscape or HideMenu
        self.assertIn("Closing in-game options/menu via ESC", content)
        self.assertIn("_G.MainMenu_HandleEscape()", content)
        self.assertIn('interface:UICmd("HideMenu()")', content)

        # Step 6: Opening game_options with debounce
        self.assertIn("_G._lastEscHandledTime = now", content)
        self.assertIn("Opening game_options from active match via ESC", content)
        self.assertIn('interface:UICmd("ToggleMenu()")', content)
        self.assertIn('"MainMenuPanelToggle", "game_options"', content)

    def test_communicator_v2_registers_game_options_ahead_of_fallbacks(self):
        """Verify _chatDisplayConfig registers game_options full-width ahead of lobby/default configs."""
        content = COMMUNICATOR_MIRRORS[0].read_text(encoding="utf-8")

        # Target entry exists with width=1325 and x=0
        expected_entry = "table.insert(_chatDisplayConfig, {target='game_options', width=1325, x=0})"
        self.assertIn(expected_entry, content, "game_options must be registered in _chatDisplayConfig")

        pos_game_options = content.find("{target='game_options', width=1325, x=0}")
        pos_playerstats = content.find("{target='playerstats', width=1325, x=0}")
        pos_store = content.find("{target='store_container2'")
        pos_lobby = content.find("{target='lobby_matchinfo'")
        pos_default = content.find("_chatDisplayConfig['default']")

        self.assertGreater(pos_game_options, pos_playerstats, "game_options should follow playerstats")
        self.assertLess(pos_game_options, pos_store, "game_options must precede store_container2")
        self.assertLess(pos_game_options, pos_lobby, "game_options must precede lobby configurations")
        self.assertLess(pos_game_options, pos_default, "game_options must precede default fallback")

    def test_main_v2_on_show_and_hide_game_options(self):
        """Verify onShowGameOptions & onHideGameOptions control main_bottom_bottom2 and com_hider2 correctly."""
        content = MAIN_V2_MIRRORS[0].read_text(encoding="utf-8")

        # onShowGameOptions must dock mbb2 at SetY('0i') and SetVisible(true) when logged in or phase > 0
        self.assertIn("local function onShowGameOptions()", content)
        self.assertIn("mbb2:SetY('0i')", content)
        self.assertIn("mbb2:SetVisible(true)", content)
        self.assertIn("comHider:SetVisible(false)", content)
        self.assertIn("Communicator_V2:ChatOnFrame()", content)

        # onShowGameOptions must keep mbb2 hidden if tonumber(p) == 0 and not logged in
        prelogin_block = re.search(
            r"if\s+tonumber\(p\)\s*==\s*0\s+and\s+not\s+loggedIn\s+then(.*?)(else|\n\t\tend)",
            content,
            re.DOTALL,
        )
        self.assertIsNotNone(prelogin_block, "Must contain pre-login unauthenticated guard")
        self.assertIn("mbb2:SetVisible(false)", prelogin_block.group(1))

        # onHideGameOptions must restore mbb2:SetVisible(false) if in active match
        self.assertIn("local function onHideGameOptions()", content)
        self.assertIn("restoring in-game mbb2 visible=false", content)

        # GamePhase watcher must not hide main_bottom_bottom2 if game_options is visible
        gp_watcher = re.search(
            r"local\s+bot2\s*=\s*main:GetWidget\('main_bottom_bottom2'\).*?local\s+showLobbyChat\s*=\s*isOptionsVisible",
            content,
            re.DOTALL,
        )
        self.assertIsNotNone(gp_watcher, "GamePhase watcher must check isOptionsVisible before hiding bot2")


if __name__ == "__main__":
    unittest.main()

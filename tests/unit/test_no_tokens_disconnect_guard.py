# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Unit regression test suite for Phase C:
- Eliminates disconnect_no_tokens across all game modes (CanAccessGameMode native hooks + m.allmodes.pass).
- Deterministic version.dll build flags & path stripping in CMakeLists.txt.
- Elimination of svr_login boolean false / 'false' pollution.
- Auto-dismissal of stale login/disconnect dialogs when GamePhase > 0.
- SHA-256 byte-for-byte mirror parity across override and games/ trees.
"""

import hashlib
import os
import re
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent


def get_file_sha256(path: Path) -> str:
    hasher = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(65536):
            hasher.update(chunk)
    return hasher.hexdigest()


class TestNoTokensDisconnectGuard(unittest.TestCase):
    """
    Protects:
      1. Game mode access unlock on client and dedicated server.
      2. Deterministic version.dll build flags and workstation path stripping.
      3. MasterServer m.allmodes.pass auto-entitlement.
      4. Zero svr_login boolean false / 'false' pollution.
      5. Auto-dismiss of disconnect/login popups during match (GamePhase > 0).
      6. Cross-tree mirror parity.
    """

    def test_native_guard_can_access_gamemode_hooks(self):
        """
        Verifies dllmain.cpp defines Hooked_CClientAccount_CanAccessGameMode and
        Hooked_CClientConnection_CanAccessGameMode with exact 17-byte prologues,
        and installs them before IsK2ClientModule(hK2).
        """
        dllmain_path = REPO_ROOT / "native" / "proxies" / "native_guard" / "dllmain.cpp"
        self.assertTrue(dllmain_path.is_file(), f"Missing {dllmain_path}")
        guard_dir = REPO_ROOT / "native" / "proxies" / "native_guard"
        content = "\n".join(
            (guard_dir / n).read_text(encoding="utf-8", errors="replace")
            for n in ("dllmain.cpp", "vfs_and_hooks.cpp")
            if (guard_dir / n).is_file()
        )

        # 1. Check Hooked replacement definitions
        self.assertIn("Hooked_CClientAccount_CanAccessGameMode", content)
        self.assertIn("Hooked_CClientConnection_CanAccessGameMode", content)

        # 2. Check 17-byte prologues
        expected_account_prologue = "0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x83, 0xB9, 0x90, 0x00, 0x00, 0x00, 0x03"
        expected_conn_prologue = "0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x83, 0xB9, 0x20, 0x01, 0x00, 0x00, 0x03"
        self.assertIn(expected_account_prologue, content)
        self.assertIn(expected_conn_prologue, content)

        # 3. Check installation in InstallK2SafeHooks BEFORE IsK2ClientModule
        idx_install_func = content.find("InstallK2SafeHooks(HMODULE hK2)")
        self.assertGreater(idx_install_func, 0, "InstallK2SafeHooks not found")
        install_body = content[idx_install_func:]

        idx_account_hook = install_body.find("CClientAccount::CanAccessGameMode")
        idx_conn_hook = install_body.find("CClientConnection::CanAccessGameMode")
        idx_is_client_check = install_body.find("if (!IsK2ClientModule(hK2))")

        self.assertGreater(idx_account_hook, 0, "Account CanAccessGameMode hook missing from InstallK2SafeHooks")
        self.assertGreater(idx_conn_hook, 0, "Connection CanAccessGameMode hook missing from InstallK2SafeHooks")
        self.assertGreater(idx_is_client_check, 0, "IsK2ClientModule check missing from InstallK2SafeHooks")

        self.assertLess(
            idx_account_hook,
            idx_is_client_check,
            "CClientAccount::CanAccessGameMode must be installed BEFORE !IsK2ClientModule so server receives it too!",
        )
        self.assertLess(
            idx_conn_hook,
            idx_is_client_check,
            "CClientConnection::CanAccessGameMode must be installed BEFORE !IsK2ClientModule so server receives it too!",
        )

    def test_native_guard_cmake_build_flags(self):
        """
        Verifies native_guard CMakeLists.txt contains KONGOR_DEPLOY_NATIVE_GUARD,
        /Brepro, /d1trimfile:, and /PDBALTPATH:%_PDB%.
        """
        cmake_path = REPO_ROOT / "native" / "proxies" / "native_guard" / "CMakeLists.txt"
        self.assertTrue(cmake_path.is_file(), f"Missing {cmake_path}")
        content = cmake_path.read_text(encoding="utf-8", errors="replace")

        self.assertIn("KONGOR_DEPLOY_NATIVE_GUARD", content)
        self.assertIn("/Brepro", content)
        self.assertIn("/d1trimfile:", content)
        self.assertIn("/PDBALTPATH:%_PDB%", content)

    def test_masterserver_allmodes_pass_entitlement(self):
        """
        Verifies ServerRequesterController.Authentication.cs injects "m.allmodes.pass"
        into ConnectClientResponse.MyUpgrades.
        """
        cs_path = (
            REPO_ROOT
            / "src"
            / "services"
            / "master-server"
            / "Controllers"
            / "ServerRequesterController"
            / "ServerRequesterController.Authentication.cs"
        )
        self.assertTrue(cs_path.is_file(), f"Missing {cs_path}")
        content = cs_path.read_text(encoding="utf-8", errors="replace")

        self.assertIn("m.allmodes.pass", content)
        # Check that it is appended to MyUpgrades
        match = re.search(r"MyUpgrades\s*=\s*.*m\.allmodes\.pass.*", content)
        self.assertIsNotNone(match, "m.allmodes.pass not chained into MyUpgrades")

    def test_zero_svr_login_boolean_pollution(self):
        """
        Verifies zero occurrences of Set('svr_login', false), Set('svr_login', 'false'),
        or Set svr_login false in games/client/override/ui/ or target test scripts.
        """
        bad_patterns = [
            re.compile(r"Set\s*\(\s*['\"]svr_login['\"]\s*,\s*false", re.IGNORECASE),
            re.compile(r"Set\s*\(\s*['\"]svr_login['\"]\s*,\s*['\"]false['\"]", re.IGNORECASE),
            re.compile(r"Set\s+svr_login\s+false", re.IGNORECASE),
        ]

        override_ui_dir = REPO_ROOT / "games" / "client" / "override" / "ui"
        for root, _, files in os.walk(override_ui_dir):
            for file in files:
                if file.endswith((".lua", ".package", ".interface")):
                    file_path = Path(root) / file
                    text = file_path.read_text(encoding="utf-8", errors="replace")
                    for pat in bad_patterns:
                        matches = pat.findall(text)
                        self.assertEqual(
                            matches,
                            [],
                            f"File {file_path} contains svr_login boolean pollution: {matches}",
                        )

        # Target scripts
        scripts_to_check = [
            REPO_ROOT / "scripts" / "launch_screen_practice.ps1",
            REPO_ROOT / "scripts" / "qa" / "test_map_smoke.ps1",
            REPO_ROOT / "scripts" / "qa" / "coordinator_capture.ps1",
            REPO_ROOT / "scripts" / "qa" / "test_phase2_midwars.ps1",
        ]
        for script_path in scripts_to_check:
            self.assertTrue(script_path.is_file(), f"Missing {script_path}")
            text = script_path.read_text(encoding="utf-8", errors="replace")
            for pat in bad_patterns:
                matches = pat.findall(text)
                self.assertEqual(
                    matches,
                    [],
                    f"Script {script_path} contains svr_login boolean pollution: {matches}",
                )

    def test_dialog_auto_hide_and_mirror_parity(self):
        """
        Verifies mm_popup_disconnected in main_dialogs_v2.package and login_error_dialog
        in main_v2.interface auto-hide when GamePhase > 0, and mirrors match byte-for-byte.
        """
        # 1. main_dialogs_v2.package
        override_dialogs = REPO_ROOT / "games" / "client" / "override" / "ui" / "fe2" / "newui" / "main_dialogs_v2.package"
        client_dialogs = REPO_ROOT / "games" / "client" / "ClientPackageFiles" / "ui" / "fe2" / "newui" / "main_dialogs_v2.package"
        game_dialogs = REPO_ROOT / "games" / "client" / "game" / "ui" / "fe2" / "fe2" / "newui" / "main_dialogs_v2.package"

        self.assertTrue(override_dialogs.is_file(), f"Missing {override_dialogs}")
        dialogs_text = override_dialogs.read_text(encoding="utf-8", errors="replace")
        self.assertIn('pannelname="mm_popup_disconnected"', dialogs_text)
        self.assertIn('watch="GamePhase" ontrigger="If(param gt 0, SetVisible(false));"', dialogs_text)

        hash_override_dialogs = get_file_sha256(override_dialogs)
        self.assertEqual(
            hash_override_dialogs,
            get_file_sha256(client_dialogs),
            "ClientPackageFiles main_dialogs_v2.package mirror hash mismatch!",
        )
        if game_dialogs.is_file():
            self.assertEqual(
                hash_override_dialogs,
                get_file_sha256(game_dialogs),
                "game/.../main_dialogs_v2.package mirror hash mismatch!",
            )

        # 2. main_v2.interface
        override_interface = REPO_ROOT / "games" / "client" / "override" / "ui" / "fe2" / "main_v2.interface"
        client_interface = REPO_ROOT / "games" / "client" / "ClientPackageFiles" / "ui" / "fe2" / "main_v2.interface"
        game_interface = REPO_ROOT / "games" / "client" / "game" / "ui" / "fe2" / "main_v2.interface"

        self.assertTrue(override_interface.is_file(), f"Missing {override_interface}")
        interface_text = override_interface.read_text(encoding="utf-8", errors="replace")

        self.assertIn('watch1="GamePhase" ontrigger1="If(param gt 0, SetVisible(false));"', interface_text)
        self.assertIn(
            "local isFailure = (param0 == 'failure' and (not GetCurrentGamePhase or GetCurrentGamePhase() == 0))",
            interface_text,
        )

        hash_override_interface = get_file_sha256(override_interface)
        for mirror_path in [client_interface, game_interface]:
            if mirror_path.is_file():
                self.assertEqual(
                    hash_override_interface,
                    get_file_sha256(mirror_path),
                    f"{mirror_path} hash mismatch with override main_v2.interface!",
                )


if __name__ == "__main__":
    unittest.main()

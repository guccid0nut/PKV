# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Unit & seam tests for staged .s2z sanitization and VFS parity verification in tools/export/verify_vfs_parity.py.

Value-Weighted Testing Gate:
  - protects: Stage 3 (`sanitize-staged`) + Stage 5 (`pack-tier`) + Stage 8 (`verify-parity` & `scan-secrets`)
    contract for scrubbing developer PII/paths inside staged 32-bit K2 `.s2z` archives without mutating
    hard-linked source archives or breaking `export_playtest_bundle.ps1` loose-file Stage 3 checks.
  - fails_when:
      (1) `.s2z` text entries (`main_account.lua`, `store.lua`, orphan `scripts/main_v2.lua`, `main_v2.interface`)
          leak `'canary_dev_user'`, `'testdev'`, or `C:/Users/...` / `C:\\Users\\...` paths into Gate #2 (`scan-secrets`);
      (2) `.s2z` records are mixed into `"records"` instead of `"archiveRecords"` (breaking `verify_client_lua.ps1`);
      (3) `cmd_verify_parity` applies a lower-tier `.s2z` sanitized CRC over a clean higher-tier `override/` winner
          or picks `resources0.s2z` over `resources99.s2z`;
      (4) a hard-linked source archive (`st_nlink > 1`) is mutated in-place; or
      (5) `luac51.exe -p` / `ElementTree` fails to block invalid rewritten Lua/XML syntax.
  - seam: Real `zipfile.ZipFile` (`allowZip64=False`), real `luac51.exe -p` bytecode compiler subprocess,
    real `xml.etree.ElementTree`, real `os.link` NTFS hard-links, and full CLI `main([...])` pipeline.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
import zipfile

REPO_ROOT = Path(__file__).resolve().parents[2]
EXPORT_TOOLS_DIR = REPO_ROOT / "tools" / "export"
if str(EXPORT_TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(EXPORT_TOOLS_DIR))

import verify_vfs_parity as vvp  # noqa: E402


def _write_clean_staged_configs(staged_client: Path) -> None:
    cfg_dir = staged_client / "KONGOR" / "configuration"
    cfg_dir.mkdir(parents=True, exist_ok=True)
    (cfg_dir / "init.cfg").write_text("setsave vid_maxFPS 1000\n", encoding="utf-8")
    (cfg_dir / "autoexec.cfg").write_text(
        'SetSave "releaseStage_stable" "true"\n', encoding="utf-8"
    )


def _build_32bit_s2z(arch_path: Path, entries: list[tuple[str, bytes, int]]) -> None:
    arch_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(
        arch_path,
        mode="w",
        compression=zipfile.ZIP_DEFLATED,
        allowZip64=False,
        compresslevel=6,
    ) as zf:
        for arcname, payload, ctype in entries:
            info = zipfile.ZipInfo(filename=arcname, date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = ctype
            zf.writestr(info, payload, compress_type=ctype)


class TestExportS2zSanitize(unittest.TestCase):
    def test_staged_s2z_sanitizes_all_pii_and_paths_with_full_parity(self) -> None:
        with tempfile.TemporaryDirectory(prefix="kongor_s2z_san_") as tmp_dir:
            tmp_path = Path(tmp_dir)
            src_client = tmp_path / "source" / "games" / "client"
            bundle_root = tmp_path / "staged" / "ProjectKongor"
            stg_client = bundle_root / "games" / "client"
            reports_dir = tmp_path / "staged" / "_export_reports"

            dirty_acct_r0 = (
                "local function CheckForAutoLoginR0()\n"
                "\tlocal user = GetCvarString('login_name')\n"
                "\tif Empty(user) or user == 'UnnamedNewbie' or user == 'Player' or string.lower(user) == 'canary_dev_user' then\n"
                "\t\tuser = 'testdev'\n"
                "\t\tSet('login_name', user)\n"
                "\tend\n"
                "\treturn user\n"
                "end\n"
                "return CheckForAutoLoginR0\n"
            ).encode("utf-8")

            dirty_acct_r99 = (
                "local function CheckForAutoLoginR99()\n"
                "\tlocal user = GetCvarString('login_name')\n"
                "\tif Empty(user) or user == 'UnnamedNewbie' or user == 'Player' or string.lower(user) == 'canary_dev_user' then\n"
                "\t\tuser = 'testdev'\n"
                "\t\tSet('login_name', user)\n"
                "\t\tSet('p2p_nickname', user)\n"
                "\tend\n"
                "\treturn user\n"
                "end\n"
                "return CheckForAutoLoginR99\n"
            ).encode("utf-8")

            dirty_orphan_main_v2 = (
                "local function OrphanMain()\n"
                "\tlocal nick = GetCvarString('p2p_nickname')\n"
                "\tif Empty(nick) or string.lower(nick) == 'canary_dev_user' then\n"
                "\t\tnick = 'Player'\n"
                "\tend\n"
                "\treturn nick\n"
                "end\n"
                "return OrphanMain\n"
            ).encode("utf-8")

            dirty_interface = (
                '<?xml version="1.0" encoding="UTF-8"?>\n'
                '<interface name="main_v2">\n'
                "\t<panel name=\"p\" onloadlua=\"CreateString('p2p_nickname', 'testdev'); if string.lower(nick) == 'canary_dev_user' then nick = 'testdev' end\"/>\n"
                "</interface>\n"
            ).encode("utf-8")

            dirty_store_r99 = (
                "local Store = {}\n"
                "function Store.WriteStatus()\n"
                '\tlocal f = io.open("C:/Users/TestDeveloper/PycharmProjects/ProjectKongor/scratch/lua_status_report.json", "w")\n'
                "\tif not f then\n"
                '\t\tf = io.open("C:\\\\Users\\\\TestDeveloper\\\\PycharmProjects\\\\ProjectKongor\\\\scratch\\\\win_report.json", "w")\n'
                "\tend\n"
                "\treturn f\n"
                "end\n"
                "return Store\n"
            ).encode("utf-8")

            dds_payload = b"DDS " + (b"\x00" * 124) + b"\xff\xaa\x55\x00" * 16

            _build_32bit_s2z(
                src_client / "game" / "resources0.s2z",
                [
                    ("textures/test_icon.dds", dds_payload, zipfile.ZIP_STORED),
                    ("scripts/main_v2.lua", dirty_orphan_main_v2, zipfile.ZIP_DEFLATED),
                    ("ui/scripts/main_account.lua", dirty_acct_r0, zipfile.ZIP_DEFLATED),
                    ("ui/fe2/main_v2.interface", dirty_interface, zipfile.ZIP_DEFLATED),
                ],
            )
            _build_32bit_s2z(
                src_client / "game" / "resources99.s2z",
                [
                    ("ui/scripts/main_account.lua", dirty_acct_r99, zipfile.ZIP_DEFLATED),
                    ("ui/scripts/store.lua", dirty_store_r99, zipfile.ZIP_DEFLATED),
                    ("ui/scripts/main_v2.lua", dirty_orphan_main_v2, zipfile.ZIP_DEFLATED),
                ],
            )

            # Clean loose override for store.lua (shadows dirty resources99.s2z::ui/scripts/store.lua)
            clean_override_store = (
                "local StoreOverride = { clean = true }\nreturn StoreOverride\n"
            )
            (src_client / "override" / "ui" / "scripts").mkdir(
                parents=True, exist_ok=True
            )
            (src_client / "override" / "ui" / "scripts" / "store.lua").write_text(
                clean_override_store, encoding="utf-8"
            )

            # Dirty loose override for main_v2.lua (shadows dirty resources99.s2z::ui/scripts/main_v2.lua)
            dirty_override_main_v2 = (
                "local function ActiveMainV2()\n"
                "\tlocal user = GetCvarString('login_name')\n"
                "\tif Empty(user) or user == 'UnnamedNewbie' or user == 'Player' or string.lower(user) == 'canary_dev_user' then\n"
                "\t\tuser = 'testdev'\n"
                "\t\tSet('login_name', user)\n"
                "\tend\n"
                "\treturn user\n"
                "end\n"
                "return ActiveMainV2\n"
            )
            (src_client / "override" / "ui" / "scripts" / "main_v2.lua").write_text(
                dirty_override_main_v2, encoding="utf-8"
            )

            shutil.copytree(src_client, stg_client)
            _write_clean_staged_configs(stg_client)

            # Pre-check: scan-secrets MUST detect leaks before sanitize-staged runs
            pre_scan_json = reports_dir / "pre_scan.json"
            rc_pre = vvp.main(
                [
                    "scan-secrets",
                    "--bundle-root",
                    str(bundle_root),
                    "--json-out",
                    str(pre_scan_json),
                ]
            )
            self.assertEqual(rc_pre, 1)
            pre_report = json.loads(pre_scan_json.read_text(encoding="utf-8"))
            self.assertGreaterEqual(pre_report["findingCount"], 6)

            # Stage 3: sanitize-staged
            san_manifest = reports_dir / "sanitized_manifest.json"
            rc_san = vvp.main(
                [
                    "sanitize-staged",
                    "--staged-client",
                    str(stg_client),
                    "--json-out",
                    str(san_manifest),
                ]
            )
            self.assertEqual(rc_san, 0)

            san_data = json.loads(san_manifest.read_text(encoding="utf-8"))
            # "records" MUST strictly contain only loose files so export_playtest_bundle.ps1 Stage 3 succeeds
            self.assertEqual(
                [r["vpath"] for r in san_data["records"]],
                ["ui/scripts/main_v2.lua"],
            )
            self.assertEqual(san_data["records"][0]["tier"], "override")
            self.assertEqual(san_data["archiveSanitizedCount"], 6)
            self.assertEqual(len(san_data["archiveRecords"]), 6)

            # Verify strict 32-bit K2 headers, entry ordering, ZIP_STORED media preservation, and dev user removal
            vvp.assert_strict_32bit_s2z(stg_client / "game" / "resources0.s2z")
            vvp.assert_strict_32bit_s2z(stg_client / "game" / "resources99.s2z")

            with zipfile.ZipFile(stg_client / "game" / "resources0.s2z", "r") as z0:
                infos0 = z0.infolist()
                self.assertEqual(
                    [i.filename for i in infos0],
                    [
                        "textures/test_icon.dds",
                        "scripts/main_v2.lua",
                        "ui/scripts/main_account.lua",
                        "ui/fe2/main_v2.interface",
                    ],
                )
                self.assertEqual(infos0[0].compress_type, zipfile.ZIP_STORED)

            with zipfile.ZipFile(stg_client / "game" / "resources99.s2z", "r") as z99:
                scrubbed_acct = z99.read("ui/scripts/main_account.lua").decode("utf-8")
                self.assertNotIn("testdev", scrubbed_acct)
                self.assertNotIn("canary_dev_user", scrubbed_acct.lower())
                scrubbed_store = z99.read("ui/scripts/store.lua").decode("utf-8")
                self.assertIn('"scratch/lua_status_report.json"', scrubbed_store)
                self.assertIn('"scratch\\\\win_report.json"', scrubbed_store)
                self.assertNotIn("canary_dev_user", scrubbed_store.lower())

            # Stage 5: pack-tier on staged override/ (packs clean store.lua + sanitized main_v2.lua into resources00.s2z)
            rc_pack = vvp.main(
                [
                    "pack-tier",
                    "--tier-dir",
                    str(stg_client / "override"),
                    "--tier-name",
                    "override",
                    "--suffix-style",
                    "digits",
                ]
            )
            self.assertEqual(rc_pack, 0)
            self.assertTrue((stg_client / "override" / "resources00.s2z").is_file())

            # Stage 8 Gate #2: scan-secrets MUST pass with 0 findings
            post_scan_json = reports_dir / "post_scan.json"
            rc_sec = vvp.main(
                [
                    "scan-secrets",
                    "--bundle-root",
                    str(bundle_root),
                    "--json-out",
                    str(post_scan_json),
                ]
            )
            self.assertEqual(rc_sec, 0)
            post_report = json.loads(post_scan_json.read_text(encoding="utf-8"))
            self.assertEqual(post_report["findingCount"], 0)

            # Stage 8 Gate #1: verify-parity MUST pass with 0 CRC/tier mismatches across loose + s2z winners
            parity_json = reports_dir / "parity.json"
            rc_par = vvp.main(
                [
                    "verify-parity",
                    "--source-client",
                    str(src_client),
                    "--staged-client",
                    str(stg_client),
                    "--sanitized-manifest",
                    str(san_manifest),
                    "--json-out",
                    str(parity_json),
                ]
            )
            self.assertEqual(rc_par, 0)
            parity_report = json.loads(parity_json.read_text(encoding="utf-8"))
            self.assertTrue(parity_report["passed"])
            self.assertEqual(parity_report["crcMismatchCount"], 0)
            self.assertEqual(parity_report["tierMismatchCount"], 0)

    def test_hardlinked_archive_guard_blocks_in_place_source_mutation(self) -> None:
        with tempfile.TemporaryDirectory(prefix="kongor_s2z_hl_") as tmp_dir:
            tmp_path = Path(tmp_dir)
            client_arch = tmp_path / "games" / "client" / "game" / "resources0.s2z"
            server_arch = tmp_path / "games" / "server" / "game" / "resources0.s2z"
            server_arch.parent.mkdir(parents=True, exist_ok=True)

            dirty_lua = b"if string.lower(user) == 'canary_dev_user' then user = 'Player' end\n"
            _build_32bit_s2z(
                client_arch,
                [("ui/scripts/main_account.lua", dirty_lua, zipfile.ZIP_DEFLATED)],
            )
            orig_bytes = client_arch.read_bytes()
            os.link(client_arch, server_arch)
            self.assertEqual(client_arch.stat().st_nlink, 2)

            with self.assertRaisesRegex(
                RuntimeError, "Refusing to rewrite hard-linked archive"
            ):
                vvp.main(
                    ["sanitize-staged", "--staged-client", str(tmp_path / "games" / "client")]
                )

            self.assertEqual(client_arch.read_bytes(), orig_bytes)
            self.assertEqual(server_arch.read_bytes(), orig_bytes)

    def test_lua_syntax_regression_fails_closed_while_legacy_orphan_is_scrubbed(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory(prefix="kongor_s2z_luac_") as tmp_dir:
            tmp_path = Path(tmp_dir)
            stg_client = tmp_path / "staged" / "games" / "client"
            # Valid Lua BEFORE sanitization (orig_rc == 0), but nested `end` causes
            # LUA_DEV_AUTOLOGIN_USER_RE replacement to leave a dangling `end` (new_rc != 0).
            valid_nested_lua = (
                "local user = ''\n"
                "if Empty(user) or user == 'UnnamedNewbie' or string.lower(user) == 'canary_dev_user' then\n"
                "\tif true then\n"
                "\t\tuser = 'Player'\n"
                "\tend\n"
                "end\n"
            ).encode("utf-8")
            arch_path = stg_client / "game" / "resources99.s2z"
            _build_32bit_s2z(
                arch_path,
                [("ui/scripts/main_account.lua", valid_nested_lua, zipfile.ZIP_DEFLATED)],
            )
            orig_bytes = arch_path.read_bytes()

            with self.assertRaisesRegex(
                RuntimeError, "failed Lua 5.1 bytecode compilation"
            ):
                vvp.main(["sanitize-staged", "--staged-client", str(stg_client)])

            self.assertEqual(arch_path.read_bytes(), orig_bytes)
            self.assertFalse(arch_path.with_name("resources99.s2z.san_tmp").exists())

            # Conversely, a pre-existing broken orphan entry (like resources0.s2z::scripts/main_v2.lua)
            # whose pre-existing error is untouched outside scrubbed lines is scrubbed cleanly.
            orphan_arch = tmp_path / "orphan_bundle" / "games" / "client" / "game" / "resources0.s2z"
            pre_broken_orphan = (
                "pcall(function() end)\n"
                "end)\n"
                "if string.lower(nick) == 'canary_dev_user' then nick = 'Player' end\n"
            ).encode("utf-8")
            _build_32bit_s2z(
                orphan_arch,
                [("scripts/main_v2.lua", pre_broken_orphan, zipfile.ZIP_DEFLATED)],
            )
            rc_orphan = vvp.main(
                [
                    "sanitize-staged",
                    "--staged-client",
                    str(tmp_path / "orphan_bundle" / "games" / "client"),
                ]
            )
            self.assertEqual(rc_orphan, 0)
            rc_sec = vvp.main(
                ["scan-secrets", "--bundle-root", str(tmp_path / "orphan_bundle")]
            )
            self.assertEqual(rc_sec, 0)

    def test_shadercache_excluded_from_parity_and_banned_in_bundle_scan(self) -> None:
        with tempfile.TemporaryDirectory(prefix="kongor_s2z_cache_") as tmp_dir:
            tmp_path = Path(tmp_dir)
            src_client = tmp_path / "source" / "games" / "client"
            bundle_root = tmp_path / "staged" / "ProjectKongor"
            stg_client = bundle_root / "games" / "client"
            reports_dir = tmp_path / "staged" / "_export_reports"

            # Source workspace has dev-generated shadercache, texturecache, and override logs
            (src_client / "game" / "shadercache").mkdir(parents=True, exist_ok=True)
            (src_client / "game" / "shadercache" / "ps_3_0_cached.bin").write_bytes(b"\x01\x02\x03\x04")
            (src_client / "game" / "shadercache0.s2z").write_bytes(b"PK\x05\x06" + b"\x00" * 18)
            (src_client / "override" / "logs").mkdir(parents=True, exist_ok=True)
            (src_client / "override" / "logs" / "client_lua_debug.log").write_text(
                "debug line\n", encoding="utf-8"
            )
            (src_client / "override" / "ui").mkdir(parents=True, exist_ok=True)
            (src_client / "override" / "ui" / "clean.lua").write_text(
                "return { ok = true }\n", encoding="utf-8"
            )

            # Staged client omits shadercache / logs and keeps clean.lua
            (stg_client / "override" / "ui").mkdir(parents=True, exist_ok=True)
            shutil.copy2(
                src_client / "override" / "ui" / "clean.lua",
                stg_client / "override" / "ui" / "clean.lua",
            )
            _write_clean_staged_configs(stg_client)

            parity_json = reports_dir / "cache_parity.json"
            rc_par = vvp.main(
                [
                    "verify-parity",
                    "--source-client",
                    str(src_client),
                    "--staged-client",
                    str(stg_client),
                    "--json-out",
                    str(parity_json),
                ]
            )
            self.assertEqual(rc_par, 0)
            parity_report = json.loads(parity_json.read_text(encoding="utf-8"))
            self.assertTrue(parity_report["passed"])
            self.assertEqual(parity_report["missingInStagedCount"], 0)

            # Leaking shadercache into staged bundle MUST fail scan-secrets
            (stg_client / "game" / "shadercache").mkdir(parents=True, exist_ok=True)
            (stg_client / "game" / "shadercache" / "leaked.bin").write_bytes(b"\xde\xad\xbe\xef")
            scan_json = reports_dir / "cache_scan.json"
            rc_sec = vvp.main(
                [
                    "scan-secrets",
                    "--bundle-root",
                    str(bundle_root),
                    "--json-out",
                    str(scan_json),
                ]
            )
            self.assertEqual(rc_sec, 1)
            scan_report = json.loads(scan_json.read_text(encoding="utf-8"))
            rules = {f["rule"] for f in scan_report["findings"]}
            self.assertIn("forbidden_shader_or_texture_cache", rules)


if __name__ == "__main__":
    unittest.main()

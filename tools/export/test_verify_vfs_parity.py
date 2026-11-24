#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)
"""
Hermetic unit tests for tools/export/verify_vfs_parity.py.

Value-Weighted Test Contract:
  - protects: K2 32-bit .s2z 2-char suffix precedence (resources00..99 / resourcesa0..z9),
              FN-003 textures.s2z loose override preservation, 100% VFS CRC32 parity,
              developer auto-login hash sanitization, and deep .s2z + loose secret blocking.
  - fails_when: Archive suffix breaks CFileManager::RegisterArchives (k2_x64.dll+0x891100),
                textures.s2z-shadowed override is packed into .s2z, any asset CRC32 drifts,
                or any credential/uploader/.pdb/.cs leaks into loose files or inside .s2z.
  - seam: Real filesystem trees + real zipfile 32-bit .s2z archives in tempfile.TemporaryDirectory().
"""

from __future__ import annotations

import json
from pathlib import Path
import shutil
import tempfile
import unittest
import zipfile

from tools.export import verify_vfs_parity as vfs_tool


class VerifyVfsParityTests(unittest.TestCase):
    def _seed_source_workspace(self, root: Path) -> tuple[Path, Path]:
        src_client = root / "source" / "games" / "client"
        src_shared = root / "source" / "games" / "shared"

        (src_client / "base").mkdir(parents=True, exist_ok=True)
        (src_client / "game" / "heroes" / "devourer").mkdir(parents=True, exist_ok=True)
        (src_client / "game" / "core" / "shaders" / "common").mkdir(
            parents=True, exist_ok=True
        )
        (src_client / "game" / "core" / "shaders" / "vs_3_0").mkdir(
            parents=True, exist_ok=True
        )
        (src_client / "game" / "core" / "shaders" / "ps_3_0").mkdir(
            parents=True, exist_ok=True
        )
        (src_client / "game" / "videos").mkdir(parents=True, exist_ok=True)
        (src_client / "ClientPackageFiles" / "ui" / "elements").mkdir(
            parents=True, exist_ok=True
        )
        (src_client / "ClientPackageFiles" / "core" / "shaders" / "ps_3_0").mkdir(
            parents=True, exist_ok=True
        )
        (src_client / "override" / "ui" / "fe2").mkdir(parents=True, exist_ok=True)
        (src_client / "override" / "ui" / "scripts").mkdir(parents=True, exist_ok=True)
        (src_client / "override" / "core" / "shaders" / "ps_3_0").mkdir(
            parents=True, exist_ok=True
        )
        (src_shared / "scripts").mkdir(parents=True, exist_ok=True)

        # Base tier: resources0.s2z + loose core.resources
        (src_client / "base" / "core.resources").write_text(
            "<resources/>", encoding="utf-8"
        )
        with zipfile.ZipFile(
            src_client / "base" / "resources0.s2z",
            mode="w",
            compression=zipfile.ZIP_DEFLATED,
            allowZip64=False,
        ) as zf:
            zf.writestr("core/null/null.mdf", "<model/>")

        # Game tier: textures.s2z (FN-003) + resources99.s2z + loose override of hero.entity + native dll + shader .h
        with zipfile.ZipFile(
            src_client / "game" / "textures.s2z",
            mode="w",
            compression=zipfile.ZIP_STORED,
            allowZip64=False,
        ) as zf:
            zf.writestr("00000000/ui/fe2/tab_shadowed.dds", b"LEGACY_TEX_BYTES")

        with zipfile.ZipFile(
            src_client / "game" / "resources99.s2z",
            mode="w",
            compression=zipfile.ZIP_DEFLATED,
            allowZip64=False,
        ) as zf:
            zf.writestr("heroes/devourer/hero.entity", "<hero version='old_s2z'/>")
            zf.writestr("heroes/devourer/icon.tga", b"TGA_FROM_99")

        # Loose file in game/ MUST win over game/resources99.s2z both before and after packing into resourcesa0.s2z!
        (src_client / "game" / "heroes" / "devourer" / "hero.entity").write_text(
            "<hero version='loose_game_winner'/>", encoding="utf-8"
        )
        (src_client / "game" / "cgame_x64.dll").write_bytes(b"MZ_FAKE_CGAME")
        (src_client / "game" / "8bit").write_text("8bit", encoding="utf-8")
        (src_client / "game" / "core" / "shaders" / "common" / "common.h").write_text(
            "// HLSL shader header\nfloat4 calc() { return 0; }\n", encoding="utf-8"
        )
        (src_client / "game" / "core" / "shaders" / "vs_3_0" / "simple.vsh").write_text(
            '#include "../common/common.h"\nfloat4 VS() : POSITION { return calc(); }\n',
            encoding="utf-8",
        )
        (src_client / "game" / "core" / "shaders" / "ps_3_0" / "terrain_color.psh").write_text(
            "float4 PS() : COLOR0 { return float4(0.2, 0.2, 0.2, 1.0); }\n",
            encoding="utf-8",
        )
        (
            src_client
            / "ClientPackageFiles"
            / "core"
            / "shaders"
            / "ps_3_0"
            / "terrain_color.psh"
        ).write_text(
            "float4 PS() : COLOR0 { return float4(0.5, 0.5, 0.5, 1.0); }\n",
            encoding="utf-8",
        )
        (
            src_client / "override" / "core" / "shaders" / "ps_3_0" / "terrain_color.psh"
        ).write_text(
            "float4 PS() : COLOR0 { return float4(1.0, 1.0, 1.0, 1.0); }\n",
            encoding="utf-8",
        )
        # Dev clutter in source that should be ignored
        (src_client / "game" / "autoexec.cfg").write_text(
            'SetSave "login_password" "dev_secret_hash"\n', encoding="utf-8"
        )
        (src_client / "game" / "videos" / "canary_dev_user_2.mp4").write_bytes(b"BIG_VIDEO")

        # Shared tier
        (src_shared / "scripts" / "shared_helper.lua").write_text(
            "return { ok = true }\n", encoding="utf-8"
        )

        # ClientPackageFiles tier: stored .dds payloads to exercise multi-shard splitting (resources00.s2z, resources01.s2z)
        for idx in range(6):
            (
                src_client / "ClientPackageFiles" / "ui" / "elements" / f"panel_{idx}.dds"
            ).write_bytes((f"DDS_HEADER_{idx}_".encode("ascii")) + (b"\xAB" * 16384))

        # Override tier: FN-003 texture + main_v2.lua containing dev auto-login block
        (src_client / "override" / "ui" / "fe2" / "tab_shadowed.dds").write_bytes(
            b"OVERRIDE_DDS_NEW_PIXELS"
        )
        (src_client / "override" / "ui" / "scripts" / "main_v2.lua").write_text(
            "local function CheckForAutoLogin()\n"
            "\tlocal user = GetCvarString('login_name')\n"
            "\tif Empty(user) or user == 'UnnamedNewbie' or user == 'Player' or string.lower(user) == 'canary_dev_user' then\n"
            "\t\tuser = 'canary_dev_user'\n"
            "\t\tSet('login_name', user)\n"
            "\tend\n"
            "\tlocal savedPass = GetCvarString('login_password')\n"
            "\tif (Empty(savedPass) or savedPass == 'd41d8cd98f00b204e9800998ecf8427e') and user == 'canary_dev_user' then\n"
            "\t\tsavedPass = '0123456789abcdef0123456789abcdef'\n"
            "\t\tSet('login_password', savedPass)\n"
            "\tend\n"
            "end\n",
            encoding="utf-8",
        )
        return src_client, src_shared

    def _stage_clean_copy(
        self, src_client: Path, src_shared: Path, bundle_root: Path
    ) -> tuple[Path, Path]:
        stg_client = bundle_root / "games" / "client"
        stg_shared = bundle_root / "games" / "shared"

        shutil.copytree(src_shared, stg_shared)
        for sub in ("base", "ClientPackageFiles", "override"):
            shutil.copytree(src_client / sub, stg_client / sub)

        (stg_client / "game").mkdir(parents=True, exist_ok=True)
        for item in (src_client / "game").iterdir():
            if item.name in {"autoexec.cfg", "videos"}:
                continue
            dest = stg_client / "game" / item.name
            if item.is_dir():
                shutil.copytree(item, dest)
            else:
                shutil.copy2(item, dest)

        cfg_dir = stg_client / "KONGOR" / "configuration"
        cfg_dir.mkdir(parents=True, exist_ok=True)
        (cfg_dir / "init.cfg").write_text(
            '// clean playtest init.cfg\nSet terrain_chunksize "32"\n',
            encoding="utf-8",
        )
        (cfg_dir / "autoexec.cfg").write_text(
            "// clean playtest autoexec.cfg\n", encoding="utf-8"
        )
        return stg_client, stg_shared

    def test_end_to_end_sanitize_pack_parity_and_secret_scan_passes(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_root = Path(tmp)
            src_client, src_shared = self._seed_source_workspace(tmp_root)
            bundle_root = tmp_root / "dist" / "ProjectKongor"
            reports_dir = tmp_root / "reports"
            stg_client, stg_shared = self._stage_clean_copy(
                src_client, src_shared, bundle_root
            )

            san_json = reports_dir / "sanitized.json"
            self.assertEqual(
                0,
                vfs_tool.main([
                    "sanitize-staged",
                    "--staged-client",
                    str(stg_client),
                    "--staged-shared",
                    str(stg_shared),
                    "--json-out",
                    str(san_json),
                ]),
            )

            # Pack shared, ClientPackageFiles (small shard limit -> multiple shards), override (FN-003), and game ('alpha' -> resourcesa0.s2z)
            self.assertEqual(
                0,
                vfs_tool.main([
                    "pack-tier",
                    "--tier-dir",
                    str(stg_shared),
                    "--tier-name",
                    "shared",
                    "--suffix-style",
                    "digits",
                ]),
            )
            self.assertEqual(
                0,
                vfs_tool.main([
                    "pack-tier",
                    "--tier-dir",
                    str(stg_client / "ClientPackageFiles"),
                    "--tier-name",
                    "ClientPackageFiles",
                    "--suffix-style",
                    "digits",
                    "--max-archive-bytes",
                    "110000",
                ]),
            )
            self.assertEqual(
                0,
                vfs_tool.main([
                    "pack-tier",
                    "--tier-dir",
                    str(stg_client / "override"),
                    "--tier-name",
                    "override",
                    "--suffix-style",
                    "digits",
                    "--textures-s2z",
                    str(stg_client / "game" / "textures.s2z"),
                ]),
            )
            self.assertEqual(
                0,
                vfs_tool.main([
                    "pack-tier",
                    "--tier-dir",
                    str(stg_client / "game"),
                    "--tier-name",
                    "game",
                    "--suffix-style",
                    "alpha",
                ]),
            )

            # Verify expected physical artifacts after packing
            self.assertTrue((stg_client / "game" / "resourcesa0.s2z").is_file())
            self.assertTrue((stg_client / "game" / "cgame_x64.dll").is_file())
            self.assertTrue((stg_client / "game" / "8bit").is_file())
            self.assertFalse(
                (stg_client / "game" / "heroes").exists(),
                "Empty packed parent directory game/heroes must be pruned after pack-tier!",
            )
            self.assertTrue(
                (stg_client / "game" / "core" / "shaders" / "common" / "common.h").is_file(),
                "core/shaders/common/common.h must stay loose for D3DX #include resolution!",
            )
            self.assertTrue(
                (stg_client / "game" / "core" / "shaders" / "vs_3_0" / "simple.vsh").is_file(),
                "core/shaders/*.vsh must stay loose for CFileManager::Exists(..., 0x100000)!",
            )
            self.assertTrue(
                (
                    stg_client
                    / "ClientPackageFiles"
                    / "core"
                    / "shaders"
                    / "ps_3_0"
                    / "terrain_color.psh"
                ).is_file(),
                "ClientPackageFiles/core/shaders/*.psh must stay loose across tiers!",
            )
            self.assertTrue(
                (
                    stg_client
                    / "override"
                    / "core"
                    / "shaders"
                    / "ps_3_0"
                    / "terrain_color.psh"
                ).is_file(),
                "override/core/shaders/*.psh must stay loose so FILE_NOARCHIVES picks up overrides!",
            )
            self.assertTrue(
                (stg_client / "override" / "ui" / "fe2" / "tab_shadowed.dds").is_file(),
                "FN-003 texture must stay loose in staged override/!",
            )
            self.assertTrue((stg_client / "override" / "resources00.s2z").is_file())
            cpf_shards = sorted(
                p.name for p in (stg_client / "ClientPackageFiles").glob("resources*.s2z")
            )
            self.assertGreaterEqual(len(cpf_shards), 2)

            # Write manifests
            self.assertEqual(
                0,
                vfs_tool.main([
                    "write-manifests",
                    "--bundle-root",
                    str(bundle_root),
                    "--build-id",
                    "playtest-unit-001",
                    "--git-commit",
                    "4aa420482",
                    "--platform",
                    "win-x64",
                    "--binaries-pre-patched",
                ]),
            )
            manifest_data = json.loads(
                (bundle_root / "build_manifest.json").read_text(encoding="utf-8")
            )
            self.assertEqual("playtest-unit-001", manifest_data["buildId"])
            self.assertIn("__gameplay_entity_crc32__", manifest_data["archiveHashes"])

            # Gate 1: VFS CRC32 Parity must pass 100%
            parity_json = reports_dir / "parity.json"
            self.assertEqual(
                0,
                vfs_tool.main([
                    "verify-parity",
                    "--source-client",
                    str(src_client),
                    "--source-shared",
                    str(src_shared),
                    "--staged-client",
                    str(stg_client),
                    "--staged-shared",
                    str(stg_shared),
                    "--sanitized-manifest",
                    str(san_json),
                    "--json-out",
                    str(parity_json),
                ]),
            )

            # Gate 2: Secret & Leak Scanner must pass 100% (MD5 hash & 'canary_dev_user' were scrubbed!)
            secret_json = reports_dir / "secrets.json"
            self.assertEqual(
                0,
                vfs_tool.main([
                    "scan-secrets",
                    "--bundle-root",
                    str(bundle_root),
                    "--json-out",
                    str(secret_json),
                ]),
            )

    def test_verify_parity_detects_crc_corruption_and_invalid_three_char_archive_name(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_root = Path(tmp)
            src_client, src_shared = self._seed_source_workspace(tmp_root)
            bundle_root = tmp_root / "dist" / "ProjectKongor"
            stg_client, stg_shared = self._stage_clean_copy(
                src_client, src_shared, bundle_root
            )

            # Corrupt a file in staged shared/ AND add an invalid 3-char suffix archive resources100.s2z
            (stg_shared / "scripts" / "shared_helper.lua").write_text(
                "return { ok = false }\n", encoding="utf-8"
            )
            with zipfile.ZipFile(
                stg_client / "game" / "resources100.s2z",
                mode="w",
                compression=zipfile.ZIP_DEFLATED,
                allowZip64=False,
            ) as zf:
                zf.writestr("dummy.txt", "bad_suffix_length")

            parity_json = tmp_root / "parity_fail.json"
            rc = vfs_tool.main([
                "verify-parity",
                "--source-client",
                str(src_client),
                "--source-shared",
                str(src_shared),
                "--staged-client",
                str(stg_client),
                "--staged-shared",
                str(stg_shared),
                "--json-out",
                str(parity_json),
            ])
            self.assertEqual(1, rc)
            report = json.loads(parity_json.read_text(encoding="utf-8"))
            self.assertFalse(report["passed"])
            self.assertGreaterEqual(report["crcMismatchCount"], 1)
            self.assertEqual(1, len(report["archiveNamingViolations"]))

    def test_scan_secrets_catches_credentials_inside_s2z_and_forbidden_binaries(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            bundle_root = Path(tmp) / "ProjectKongor"
            game_dir = bundle_root / "games" / "client" / "game"
            game_dir.mkdir(parents=True, exist_ok=True)

            # 1. Legacy crash uploader binary
            (bundle_root / "games" / "client" / "hon_crash_uploader_x64.exe").write_bytes(
                b"MZ_UPLOADER"
            )
            # 2. Stray .pdb symbol file
            (bundle_root / "WILLOWMAKER.pdb").write_bytes(b"PDB_SYMBOLS")
            # 3. Secret hidden inside an .s2z archive!
            with zipfile.ZipFile(
                game_dir / "resources99.s2z",
                mode="w",
                compression=zipfile.ZIP_DEFLATED,
                allowZip64=False,
            ) as zf:
                zf.writestr(
                    "configuration/leaked.cfg",
                    'SetSave "login_password" "5f4dcc3b5aa765d61d8327deb882cf99"\n',
                )

            secret_json = Path(tmp) / "secret_fail.json"
            rc = vfs_tool.main([
                "scan-secrets",
                "--bundle-root",
                str(bundle_root),
                "--json-out",
                str(secret_json),
            ])
            self.assertEqual(1, rc)
            report = json.loads(secret_json.read_text(encoding="utf-8"))
            self.assertFalse(report["passed"])
            rules_hit = {f["rule"] for f in report["findings"]}
            self.assertIn("forbidden_legacy_uploader_or_updater_binary", rules_hit)
            self.assertIn("forbidden_extension_.pdb", rules_hit)
            self.assertIn("k2_cfg_login_password", rules_hit)

    def test_binary_pii_scanner_vendor_immunity_and_detection(self) -> None:
        """
        Protects:
          - Zero false positives on actual upstream vendor binaries (k2_x64.dll, hon_x64.exe, etc.)
          - Zero false positives on Chromium's 2-byte ASCII sequence table ('A' + u..~)
          - Immediate blocking of leaked developer OS username 'canary_dev_user', PII paths, nexus-private, etc.
        """
        # 1. Test live vendor binaries in repo if present
        repo_root = Path(__file__).resolve().parents[2]
        vendor_binaries = [
            repo_root / "games" / "client" / "k2_x64.dll",
            repo_root / "games" / "client" / "hon_x64.exe",
            repo_root / "games" / "client" / "cgame_x64.dll",
            repo_root / "games" / "client" / "vid_d3d9_x64.dll",
            repo_root / "games" / "client" / "fmodex64_core.dll",
        ]
        for vbin in vendor_binaries:
            if not vbin.is_file():
                continue
            findings: list[dict[str, Any]] = []
            vfs_tool.scan_binary_payload(vbin.name, vbin.read_bytes(), findings)
            self.assertEqual(
                0,
                len(findings),
                f"Vendor binary '{vbin.name}' triggered false positive PII leaks: {findings}",
            )

        # 2. Test synthetic binary payloads in a staged directory
        with tempfile.TemporaryDirectory() as tmp:
            bundle_root = Path(tmp) / "bundle"
            bundle_root.mkdir(parents=True, exist_ok=True)

            # Clean binary with vendor build paths and Chromium 2-byte sequence
            clean_payload = (
                b"MZ_CLEAN_HEADER"
                + b"C:\\Users\\Administrator\\Desktop\\Agent\\_work\\13\\s\\src\\k2\\c_clientSRP.cpp\x00"
                + b"C:\\Users\\HyperXewl\\Desktop\\cef_binary_85.0.0\\libcef.dll\x00"
                + b"..." + b"".join(b"A" + bytes([c]) for c in range(ord("u"), ord("~") + 1)) + b"A!...\x00"
            )
            (bundle_root / "clean_vendor_x64.dll").write_bytes(clean_payload)

            sec_json = Path(tmp) / "clean_sec.json"
            rc = vfs_tool.main([
                "scan-secrets",
                "--bundle-root",
                str(bundle_root),
                "--json-out",
                str(sec_json),
            ])
            self.assertEqual(0, rc)
            rep = json.loads(sec_json.read_text(encoding="utf-8"))
            self.assertTrue(rep["passed"])

            # Dirty binary with ASCII and UTF-16LE leaks
            dirty_ascii = b"MZ_DIRTY\x00C:\\Users\\canary_dev_user\\PycharmProjects\\ProjectKongor\\native\\hook.cpp\x00"
            dirty_u16 = "D:\\Users\\johndoe\\repo\\secret.cpp".encode("utf-16le")
            dirty_boundary = b"some_prefix/canary_dev_user/some_suffix\x00"
            dirty_private = b"git clone git@github.com:ProjectKongor/nexus-private.git\x00"

            (bundle_root / "dirty_x64.dll").write_bytes(
                dirty_ascii + dirty_u16 + dirty_boundary + dirty_private
            )

            rc_dirty = vfs_tool.main([
                "scan-secrets",
                "--bundle-root",
                str(bundle_root),
                "--json-out",
                str(sec_json),
            ])
            self.assertEqual(1, rc_dirty)
            rep_dirty = json.loads(sec_json.read_text(encoding="utf-8"))
            self.assertFalse(rep_dirty["passed"])
            rules_hit = {f["rule"] for f in rep_dirty["findings"]}
            self.assertIn("binary_users_canary_dev_user", rules_hit)
            self.assertIn("binary_pycharmprojects", rules_hit)
            self.assertIn("binary_local_user_path_leak", rules_hit)
            self.assertIn("binary_boundary_canary_dev_user", rules_hit)
            self.assertIn("binary_nexus_private", rules_hit)

    def test_json_secret_value_scanner(self) -> None:
        """
        Protects against secret/token/password values leaked inside staged .json files,
        while allowing safe placeholders like "" and "...", and avoiding false-positives
        on numeric/gameplay token counts like maxTokens or gameTokens.
        """
        # Confirm {"maxTokens": 4096, "gameTokens": 0, "authToken": ""} produces 0 findings
        # while {"authToken": "real-secret-123", "password": "hunter2"} produces 2 findings.
        numeric_safe_payload = {"maxTokens": 4096, "gameTokens": 0, "authToken": ""}
        numeric_findings: list[dict[str, object]] = []
        vfs_tool.scan_json_payload("memory_check.json", numeric_safe_payload, numeric_findings)
        self.assertEqual(0, len(numeric_findings))

        secrets_payload = {"authToken": "real-secret-123", "password": "hunter2"}
        secrets_findings: list[dict[str, object]] = []
        vfs_tool.scan_json_payload("memory_check.json", secrets_payload, secrets_findings)
        self.assertEqual(2, len(secrets_findings))
        self.assertEqual(
            {"json_secret_value_detected"},
            {f["rule"] for f in secrets_findings},
        )

        with tempfile.TemporaryDirectory() as tmp:
            bundle_root = Path(tmp) / "bundle"
            bundle_root.mkdir(parents=True, exist_ok=True)

            safe_json = {
                "serverName": "Kongor Playtest",
                "maxTokens": 4096,
                "gameTokens": 0,
                "auth": {
                    "apiKey": "",
                    "authToken": "",
                    "password": "...",
                    "connectionString": "...",
                },
                "port": 11235,
            }
            (bundle_root / "config_safe.json").write_text(
                json.dumps(safe_json), encoding="utf-8"
            )

            sec_json = Path(tmp) / "sec.json"
            self.assertEqual(
                0,
                vfs_tool.main([
                    "scan-secrets",
                    "--bundle-root",
                    str(bundle_root),
                    "--json-out",
                    str(sec_json),
                ]),
            )

            # Add dirty JSON with leaked API token
            dirty_json = {
                "production": {
                    "api_key": "live_sec_1234567890abcdef",
                    "basicAuth": "admin:supersecret",
                }
            }
            (bundle_root / "config_dirty.json").write_text(
                json.dumps(dirty_json), encoding="utf-8"
            )

            self.assertEqual(
                1,
                vfs_tool.main([
                    "scan-secrets",
                    "--bundle-root",
                    str(bundle_root),
                    "--json-out",
                    str(sec_json),
                ]),
            )
            rep = json.loads(sec_json.read_text(encoding="utf-8"))
            rules_hit = {f["rule"] for f in rep["findings"]}
            self.assertIn("json_secret_value_detected", rules_hit)

    def test_cinematic_video_default_inclusion_and_legacy_dev_video_blocking(self) -> None:
        """
        Protects:
          - canary_dev_user_2.* is always flagged as forbidden_legacy_dev_video_name
          - kongor_cinematic.* is allowed by default
          - kongor_cinematic.* is flagged as excluded_cinematic_video_present when --skip-cinematic-videos is passed
          - mods_state.json is excluded in is_dev_excluded_source_file
        """
        self.assertTrue(vfs_tool.is_dev_excluded_source_file("mods/mods_state.json"))
        self.assertTrue(vfs_tool.is_dev_excluded_source_file("videos/canary_dev_user_2.avi"))
        self.assertFalse(
            vfs_tool.is_dev_excluded_source_file(
                "videos/kongor_cinematic.avi", skip_cinematic_videos=False
            )
        )
        self.assertTrue(
            vfs_tool.is_dev_excluded_source_file(
                "videos/kongor_cinematic.avi", skip_cinematic_videos=True
            )
        )

        with tempfile.TemporaryDirectory() as tmp:
            bundle_root = Path(tmp) / "bundle"
            videos_dir = bundle_root / "games" / "client" / "game" / "videos"
            videos_dir.mkdir(parents=True, exist_ok=True)

            (videos_dir / "kongor_cinematic.avi").write_bytes(b"CINEMATIC_VIDEO")
            (videos_dir / "kongor_cinematic.mp3").write_bytes(b"CINEMATIC_AUDIO")

            sec_json = Path(tmp) / "sec.json"
            # Default: kongor_cinematic.* is permitted!
            self.assertEqual(
                0,
                vfs_tool.main([
                    "scan-secrets",
                    "--bundle-root",
                    str(bundle_root),
                    "--json-out",
                    str(sec_json),
                ]),
            )

            # When --skip-cinematic-videos is passed: flagged!
            self.assertEqual(
                1,
                vfs_tool.main([
                    "scan-secrets",
                    "--bundle-root",
                    str(bundle_root),
                    "--skip-cinematic-videos",
                    "--json-out",
                    str(sec_json),
                ]),
            )
            rep = json.loads(sec_json.read_text(encoding="utf-8"))
            rules_hit = {f["rule"] for f in rep["findings"]}
            self.assertIn("excluded_cinematic_video_present", rules_hit)

            # canary_dev_user_2.* is ALWAYS flagged
            (videos_dir / "canary_dev_user_2.mp4").write_bytes(b"OLD_DEV_VIDEO")
            self.assertEqual(
                1,
                vfs_tool.main([
                    "scan-secrets",
                    "--bundle-root",
                    str(bundle_root),
                    "--json-out",
                    str(sec_json),
                ]),
            )
            rep2 = json.loads(sec_json.read_text(encoding="utf-8"))
            rules_hit2 = {f["rule"] for f in rep2["findings"]}
            self.assertIn("forbidden_legacy_dev_video_name", rules_hit2)

    def test_posix_binary_pii_scanner_and_vendor_allowlist(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            bundle_root = Path(tmp) / "bundle"
            bundle_root.mkdir(parents=True, exist_ok=True)
            sec_json = Path(tmp) / "sec.json"

            # 1. Leaking extensionless ELF binary
            elf_file = bundle_root / "WILLOWMAKER"
            elf_file.write_bytes(b"\x7fELF\x02\x01\x01\x00/home/localdev/work/main.c\x00")
            self.assertEqual(
                1,
                vfs_tool.main([
                    "scan-secrets",
                    "--bundle-root",
                    str(bundle_root),
                    "--json-out",
                    str(sec_json),
                ]),
            )
            rep1 = json.loads(sec_json.read_text(encoding="utf-8"))
            rules1 = {f["rule"] for f in rep1["findings"]}
            self.assertIn("binary_local_user_path_leak", rules1)
            elf_file.unlink()

            # 2. Leaking extensionless Mach-O binary
            macho_file = bundle_root / "HoN64"
            macho_file.write_bytes(b"\xcf\xfa\xed\xfe\x07\x00\x00\x01/Users/janedoe/src/app.mm\x00")
            self.assertEqual(
                1,
                vfs_tool.main([
                    "scan-secrets",
                    "--bundle-root",
                    str(bundle_root),
                    "--json-out",
                    str(sec_json),
                ]),
            )
            rep2 = json.loads(sec_json.read_text(encoding="utf-8"))
            rules2 = {f["rule"] for f in rep2["findings"]}
            self.assertIn("binary_local_user_path_leak", rules2)
            macho_file.unlink()

            # 3. Upstream vendor prefixes must pass with 0 findings
            vendor_elf = bundle_root / "hon-x86_64"
            vendor_elf.write_bytes(
                b"\x7fELF\x02\x01\x01\x00"
                b"/home/michael/build/k2/main.cpp\x00"  # kongor-security-ignore
                b"/home/runner/work/repo\x00"
                b"/home/dev/src/engine.c\x00"
                b"/home/developer/lib/util.c\x00"
            )
            vendor_macho = bundle_root / "cgame.dylib"
            vendor_macho.write_bytes(
                b"\xcf\xfa\xed\xfe\x07\x00\x00\x01"
                b"/Users/hyperxewl/Desktop/cef_binary_3\x00"  # kongor-security-ignore
                b"/Users/hyperxewl/Desktop/curl/lib/vtls/openssl.c\x00"  # kongor-security-ignore
                b"/Users/hyperxewl/Desktop/libxml2/xpath.c\x00"  # kongor-security-ignore
                b"/Users/hyperxewl/Downloads/speex-1.2.0/libspeex/bits.c\x00"  # kongor-security-ignore
                b"/Users/hyperxewl/Downloads/speexdsp-1.2rc3/libspeexdsp/jitter.c\x00"  # kongor-security-ignore
                b"/Users/adam/src/foo.c\x00"  # kongor-security-ignore
                b"/Users/admin/build/game.mm\x00"  # kongor-security-ignore
                b"/Users/administrator/desktop/agent/_work/1/s\x00"  # kongor-security-ignore
                b"/Users/nico/src/gl.c\x00"  # kongor-security-ignore
                b"/Users/raymond/build/snd.c\x00"  # kongor-security-ignore
            )
            # Standalone RFC 6762 admin@kongor.local bootstrap literal in control-panel DLL passes
            bootstrap_dll = bundle_root / "NEXUS.ControlPanel.GUI.dll"
            bootstrap_dll.write_bytes(
                b"MZ\x90\x00" + "Admin Username: Admin (or admin@kongor.local)".encode("utf-16le")
            )
            self.assertEqual(
                0,
                vfs_tool.main([
                    "scan-secrets",
                    "--bundle-root",
                    str(bundle_root),
                    "--json-out",
                    str(sec_json),
                ]),
            )
            rep3 = json.loads(sec_json.read_text(encoding="utf-8"))
            self.assertEqual(0, rep3["findingCount"])

            # 4. Non-cef_binary_ path under /Users/hyperxewl/ MUST be flagged (strict scoping)  # kongor-security-ignore
            non_cef_macho = bundle_root / "non_cef.dylib"
            non_cef_macho.write_bytes(
                b"\xcf\xfa\xed\xfe\x07\x00\x00\x01"
                b"/Users/hyperxewl/PrivateRepo/secret.c\x00"  # kongor-security-ignore
            )
            self.assertEqual(
                1,
                vfs_tool.main([
                    "scan-secrets",
                    "--bundle-root",
                    str(bundle_root),
                    "--json-out",
                    str(sec_json),
                ]),
            )
            rep4 = json.loads(sec_json.read_text(encoding="utf-8"))
            self.assertIn("binary_local_user_path_leak", {f["rule"] for f in rep4["findings"]})

    def test_create_posix_zip_preserves_mode_bits(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            bundle_root = Path(tmp) / "ProjectKongor"
            bundle_root.mkdir(parents=True, exist_ok=True)

            sub_linux = bundle_root / "launcher-linux-x64"
            sub_linux.mkdir(parents=True, exist_ok=True)
            sub_mac = bundle_root / "games" / "client" / "macos-x64"
            sub_mac.mkdir(parents=True, exist_ok=True)
            sub_libs = bundle_root / "games" / "server_p2p" / "libs-x86_64"
            sub_libs.mkdir(parents=True, exist_ok=True)
            empty_userdata = bundle_root / "games" / "client" / "userdata"
            empty_userdata.mkdir(parents=True, exist_ok=True)

            # Executable entries (0o755)
            (bundle_root / "hon-x86_64").write_bytes(b"\x7fELF_FAKE_BIN")
            (bundle_root / "HoN64").write_bytes(b"\xcf\xfa\xed\xfe_MACHO_BIN")
            (bundle_root / "macos-self-sign.command").write_bytes(b"#!/bin/sh\necho ok\n")
            (sub_linux / "hon-wrapper.sh").write_bytes(b"#!/bin/bash\n./hon\n")
            (sub_mac / "libk2.dylib").write_bytes(b"\xfe\xed\xfa\xcf_DYLIB")
            (sub_libs / "libcurl.so.4.5.0").write_bytes(b"\x7fELF_SO")

            # Non-executable entries (0o644)
            (bundle_root / "game_settings.cfg").write_text("set svr_port 11235\n", encoding="utf-8")
            (bundle_root / "WILLOWMAKER.exe").write_bytes(b"MZ_PE_FILE")
            (bundle_root / "resources0.s2z").write_bytes(b"PK\x03\x04_DUMMY_S2Z")

            zip_out = Path(tmp) / "ProjectKongor.zip"
            ret = vfs_tool.main([
                "create-posix-zip",
                "--bundle-root",
                str(bundle_root),
                "--zip-out",
                str(zip_out),
            ])
            self.assertEqual(0, ret)
            self.assertTrue(zip_out.exists())

            with zipfile.ZipFile(zip_out, "r") as zf:
                name_map = {info.filename: info for info in zf.infolist()}

                # All entries must be prefixed with "ProjectKongor/"
                for name, info in name_map.items():
                    self.assertTrue(
                        name.startswith("ProjectKongor/"),
                        f"Entry '{name}' does not start with bundle_root.name prefix",
                    )
                    mode_bits = (info.external_attr >> 16) & 0o777
                    if name.endswith("/"):
                        self.assertEqual(
                            0o755,
                            mode_bits,
                            f"Directory entry '{name}' expected 0o755 mode bits, got {oct(mode_bits)}",
                        )
                    elif any(
                        exe_name in name
                        for exe_name in [
                            "hon-x86_64",
                            "HoN64",
                            "macos-self-sign.command",
                            "hon-wrapper.sh",
                            "libk2.dylib",
                            "libcurl.so.4.5.0",
                        ]
                    ):
                        self.assertEqual(
                            0o755,
                            mode_bits,
                            f"Entry '{name}' expected 0o755 mode bits, got {oct(mode_bits)}",
                        )
                    else:
                        self.assertEqual(
                            0o644,
                            mode_bits,
                            f"Entry '{name}' expected 0o644 mode bits, got {oct(mode_bits)}",
                        )

                # Storage compression test
                self.assertEqual(
                    zipfile.ZIP_STORED,
                    name_map["ProjectKongor/resources0.s2z"].compress_type,
                )
                self.assertEqual(
                    zipfile.ZIP_DEFLATED,
                    name_map["ProjectKongor/game_settings.cfg"].compress_type,
                )
                self.assertIn("ProjectKongor/games/client/userdata/", name_map)

    def test_readme_template_renders_cleanly_and_passes_secret_gate(self) -> None:
        template_path = Path(__file__).resolve().parent / "README.txt.template"
        self.assertTrue(
            template_path.is_file(),
            f"Missing tester README template at '{template_path}'.",
        )
        raw_template = template_path.read_text(encoding="utf-8")
        rendered = (
            raw_template.replace("{{PLATFORM_VERSION}}", "5.0.1")
            .replace("{{BUILD_ID}}", "playtest-20261027-unit")
            .replace("{{PLATFORM}}", "all")
            .replace("{{GIT_COMMIT_SHORT}}", "49a6a4771")
            .replace("{{BUILT_UTC}}", "2026-10-27T18:00:00Z")
        )
        self.assertNotIn("{{", rendered)
        self.assertNotIn("}}", rendered)
        self.assertIn("NO DOCKER NEEDED", rendered)
        self.assertIn("DOCKER REQUIRED ONLY FOR SELF-HOSTED FULL STACK", rendered)
        self.assertIn("macos-self-sign.command", rendered)
        self.assertIn("tools-win-x64\\kongor.exe refresh", rendered)
        self.assertIn("tools-win-x64\\kongor.exe install", rendered)
        self.assertIn("double-check the `.zip` contents before sending", rendered)
        self.assertIn("DEVELOPER & AI AGENT REFERENCE", rendered)
        self.assertIn("kongor_server_manage", rendered)
        self.assertIn("svr_login", rendered)
        self.assertLess(
            rendered.index("INSTALLING THE `kongor` CLI"),
            rendered.index("DEVELOPER & AI AGENT REFERENCE"),
        )
        self.assertLess(
            rendered.index("TESTER SHORTCUTS & LOCAL DIAGNOSTIC BUNDLES"),
            rendered.index("DEVELOPER & AI AGENT REFERENCE"),
        )

        with tempfile.TemporaryDirectory() as tmp:
            bundle_root = Path(tmp) / "ProjectKongor"
            bundle_root.mkdir(parents=True, exist_ok=True)
            (bundle_root / "README.txt").write_text(rendered, encoding="utf-8")
            (bundle_root / "README_PLAYTEST.txt").write_text(rendered, encoding="utf-8")

            report_out = Path(tmp) / "readme_secret_scan.json"
            ret = vfs_tool.main(
                [
                    "scan-secrets",
                    "--bundle-root",
                    str(bundle_root),
                    "--json-out",
                    str(report_out),
                ]
            )
            self.assertEqual(0, ret)
            report = json.loads(report_out.read_text(encoding="utf-8"))
            self.assertTrue(report["passed"])
            self.assertEqual(0, report["findingCount"])

    def test_scoped_vendor_build_prefix_allowlist_and_portable_audio_dxvk_configs(
        self,
    ) -> None:
        # 1. Scoped OSS CI build prefixes pass binary scan
        clean_oss_findings: list[dict[str, object]] = []
        vfs_tool.scan_binary_payload(
            "launcher-linux-x64/WILLOWMAKER",
            (
                b"\x7fELF\x00"
                b"/home/kekekeks/Projects/MicroCom/src/MicroCom.Runtime/obj/Release/net5.0/MicroCom.Runtime.pdb\x00"  # kongor-security-ignore
                b"/Users/runner/work/1/s/native/Avalonia.Native/src/OSX/window.mm\x00"
            ),
            clean_oss_findings,
        )
        self.assertEqual([], clean_oss_findings)

        # 2. Out-of-scope paths under the same usernames or developer username MUST still fail!
        for bad_payload in (
            b"\x7fELF\x00/home/kekekeks/PrivateRepo/secret.c\x00",
            b"\xcf\xfa\xed\xfe\x00/Users/runner/Desktop/local_build/foo.mm\x00",
            b"\x7fELF\x00/home/canary_dev_user/ProjectKongor/foo.c\x00",
            b"\xcf\xfa\xed\xfe\x00/Users/canary_dev_user/ProjectKongor/foo.mm\x00",
        ):
            bad_findings: list[dict[str, object]] = []
            vfs_tool.scan_binary_payload("WILLOWMAKER", bad_payload, bad_findings)
            self.assertGreater(
                len(bad_findings),
                0,
                f"Expected binary scan failure for {bad_payload!r}",
            )

        # 3. Portable staged alsoft.ini & dxvk.conf pass scan-secrets while local user paths fail
        with tempfile.TemporaryDirectory() as tmp:
            bundle_root = Path(tmp) / "ProjectKongor"
            client_dir = bundle_root / "games" / "client"
            client_dir.mkdir(parents=True, exist_ok=True)
            (client_dir / "alsoft.ini").write_text(
                "[general]\ndrivers = wasapi,dsound\nhrtf = true\nhrtf-paths = ./hrtf\n",
                encoding="utf-8",
            )
            (client_dir / "dxvk.conf").write_text(
                "dxvk.hud =\ndxvk.stateCache = true\nd3d9.maxFrameRate = 0\n"
                "dxvk.enableGraphicsPipelineLibrary = true\ndxvk.allowFse = false\n",
                encoding="utf-8",
            )
            clean_report = Path(tmp) / "clean_cfg.json"
            self.assertEqual(
                0,
                vfs_tool.main([
                    "scan-secrets",
                    "--bundle-root",
                    str(bundle_root),
                    "--json-out",
                    str(clean_report),
                ]),
            )

            # Injecting developer path into dxvk.conf must fail Gate #2
            (client_dir / "dxvk.conf").write_text(
                "dxvk.stateCachePath = C:\\Users\\canary_dev_user\\AppData\\Local\\dxvk\n",
                encoding="utf-8",
            )
            dirty_report = Path(tmp) / "dirty_cfg.json"
            self.assertEqual(
                1,
                vfs_tool.main([
                    "scan-secrets",
                    "--bundle-root",
                    str(bundle_root),
                    "--json-out",
                    str(dirty_report),
                ]),
            )

    def test_sanitize_staged_scrubs_dev_clutter_from_resources99_while_preserving_vanilla_resources2(
        self,
    ) -> None:
        """
        Protects:
          - Stage 3 physically strips historic dev clutter (.log, .hotreload_manifest.json,
            moved_to_vault_j.txt, *.bak_*) from custom archives (resources99.s2z) even without
            UI login replacement hits, while leaving untouched Frostburn resources2.s2z (.material.backup)
            exempt in both Gate #1 (verify-parity) and Gate #2 (scan-secrets).
        Fails when:
          - sanitize_staged_s2z_archive leaves logs/client_lua_debug.log or *.bak_* in resources99.s2z,
            or Gate #1 / Gate #2 flags vanilla resources2.s2z.
        Seam:
          - Real ZipFile .s2z archives + CLI entrypoints sanitize-staged, verify-parity, scan-secrets.
        """
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            src_client = root / "src" / "client"
            src_shared = root / "src" / "shared"
            stg_root = root / "bundle"
            stg_client = stg_root / "games" / "client"
            stg_shared = stg_root / "games" / "shared"

            for base_client, base_shared in ((src_client, src_shared), (stg_client, stg_shared)):
                (base_client / "game").mkdir(parents=True, exist_ok=True)
                base_shared.mkdir(parents=True, exist_ok=True)

            cfg_dir = stg_client / "KONGOR" / "configuration"
            cfg_dir.mkdir(parents=True, exist_ok=True)
            (cfg_dir / "init.cfg").write_text('Set terrain_chunksize "32"\n', encoding="utf-8")
            (cfg_dir / "autoexec.cfg").write_text("// clean autoexec\n", encoding="utf-8")

            for base_client, base_shared in ((src_client, src_shared), (stg_client, stg_shared)):

                with zipfile.ZipFile(
                    base_client / "game" / "resources2.s2z",
                    mode="w",
                    compression=zipfile.ZIP_DEFLATED,
                    allowZip64=False,
                ) as zf2:
                    zf2.writestr("heroes/gemini/alt4/model.mdf", "<model/>")
                    zf2.writestr(
                        "heroes/gemini/alt4/effects/back_energy/back_energy.material.backup",
                        "<material/>",
                    )

                with zipfile.ZipFile(
                    base_client / "game" / "resources99.s2z",
                    mode="w",
                    compression=zipfile.ZIP_DEFLATED,
                    allowZip64=False,
                ) as zf99:
                    zf99.writestr("heroes/pollywogpriest/hero.entity", "<hero/>")
                    zf99.writestr("logs/client_lua_debug.log", "dev log line\n")
                    zf99.writestr(".hotreload_manifest.json", '{"files":[]}')
                    zf99.writestr("moved_to_vault_j.txt", "vault note")
                    zf99.writestr(
                        "ui/fe2/newui/res/communicator/tab_line.png.bak_20261011_tab38",
                        b"\x89PNG",
                    )

            # Before Stage 3 scrubbing, Gate #2 MUST catch the dev artifacts inside resources99.s2z
            pre_sec = root / "pre_sec.json"
            self.assertEqual(
                1,
                vfs_tool.main([
                    "scan-secrets",
                    "--bundle-root",
                    str(stg_root),
                    "--json-out",
                    str(pre_sec),
                ]),
            )

            # Run Stage 3 sanitize-staged on the staged client
            san_manifest = root / "sanitized_manifest.json"
            self.assertEqual(
                0,
                vfs_tool.main([
                    "sanitize-staged",
                    "--staged-client",
                    str(stg_client),
                    "--staged-shared",
                    str(stg_shared),
                    "--json-out",
                    str(san_manifest),
                ]),
            )

            with zipfile.ZipFile(stg_client / "game" / "resources99.s2z", "r") as zf99_after:
                names = set(zf99_after.namelist())
            self.assertEqual({"heroes/pollywogpriest/hero.entity"}, names)

            # Both Gate #1 (verify-parity) and Gate #2 (scan-secrets) must now PASS with 0 findings!
            parity_out = root / "parity.json"
            self.assertEqual(
                0,
                vfs_tool.main([
                    "verify-parity",
                    "--source-client",
                    str(src_client),
                    "--source-shared",
                    str(src_shared),
                    "--staged-client",
                    str(stg_client),
                    "--staged-shared",
                    str(stg_shared),
                    "--sanitized-manifest",
                    str(san_manifest),
                    "--json-out",
                    str(parity_out),
                ]),
            )
            post_sec = root / "post_sec.json"
            self.assertEqual(
                0,
                vfs_tool.main([
                    "scan-secrets",
                    "--bundle-root",
                    str(stg_root),
                    "--json-out",
                    str(post_sec),
                ]),
            )

    def test_verify_parity_blocks_packed_core_shaders_violation(self) -> None:
        """
        Protects:
          - K2 D3D9 cold-boot shader compilation invariant (vid_d3d9_x64.dll+0x7c1a0/0x7dcf0 &
            k2_x64.dll CFileManager::Exists+0x8967c0 with flag 0x100000 = FILE_NOARCHIVES):
            loose source shaders under core/shaders/** MUST remain loose on disk in their winning
            staged tier so cold empty-userdata/ launches never crash at vid_d3d9_x64.dll+0x2918c.
        Fails when:
          - pack-tier packs core/shaders/** into an .s2z archive or verify-parity fails to reject
            a staged bundle where a winning core/shaders/** entry is s2z-backed instead of loose.
        Seam:
          - Real ZipFile .s2z archive + CLI entrypoint verify-parity + JSON parity report.
        """
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            src_client = root / "src" / "client"
            src_shared = root / "src" / "shared"
            stg_client = root / "stg" / "client"
            stg_shared = root / "stg" / "shared"

            (src_client / "game" / "core" / "shaders" / "vs_3_0").mkdir(
                parents=True, exist_ok=True
            )
            (stg_client / "game").mkdir(parents=True, exist_ok=True)
            src_shared.mkdir(parents=True, exist_ok=True)
            stg_shared.mkdir(parents=True, exist_ok=True)

            cfg_dir = stg_client / "KONGOR" / "configuration"
            cfg_dir.mkdir(parents=True, exist_ok=True)
            (cfg_dir / "init.cfg").write_text('Set terrain_chunksize "32"\n', encoding="utf-8")
            (cfg_dir / "autoexec.cfg").write_text("// clean autoexec\n", encoding="utf-8")

            shader_payload = "float4 VS() : POSITION { return float4(0, 0, 0, 1); }\n"
            (src_client / "game" / "core" / "shaders" / "vs_3_0" / "simple.vsh").write_text(
                shader_payload, encoding="utf-8"
            )

            # Simulate broken export that packed core/shaders/vs_3_0/simple.vsh into resourcesa0.s2z
            with zipfile.ZipFile(
                stg_client / "game" / "resourcesa0.s2z",
                mode="w",
                compression=zipfile.ZIP_DEFLATED,
                allowZip64=False,
            ) as zf:
                zf.writestr("core/shaders/vs_3_0/simple.vsh", shader_payload)

            parity_out = root / "shader_violation_parity.json"
            rc = vfs_tool.main([
                "verify-parity",
                "--source-client",
                str(src_client),
                "--source-shared",
                str(src_shared),
                "--staged-client",
                str(stg_client),
                "--staged-shared",
                str(stg_shared),
                "--json-out",
                str(parity_out),
            ])
            self.assertEqual(
                1,
                rc,
                "verify-parity MUST fail when loose source core/shaders/** is packed into .s2z!",
            )
            report = json.loads(parity_out.read_text(encoding="utf-8"))
            self.assertFalse(report["passed"])
            self.assertEqual(1, report["looseShaderViolationCount"])
            self.assertIn("simple.vsh", report["looseShaderViolations"][0])

    def test_kongor_updates_textures_s2z_shard1_parity_and_fn003_union(self) -> None:
        """
        Protects:
          - KONGOR/updates/textures.s2z (Shard 1) full VFS indexing in build_vfs_winner_map().
          - Repeatable --textures-s2z union across game/textures.s2z + KONGOR/updates/textures.s2z
            (including 00000001/* mip prefixes) in pack-tier and verify-parity FN-003 checks.
        Fails when:
          - Missing staged KONGOR/updates/textures.s2z slips past verify-parity, or an override
            texture colliding with KONGOR/updates/textures.s2z is packed into resources00.s2z.
        Seam:
          - Real ZipFile .s2z Shard 1 + Shard 2 archives + CLI pack-tier & verify-parity.
        """
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            src_client = root / "src" / "client"
            src_shared = root / "src" / "shared"
            stg_client = root / "stg" / "client"
            stg_shared = root / "stg" / "shared"

            for c_root, s_root in ((src_client, src_shared), (stg_client, stg_shared)):
                (c_root / "game").mkdir(parents=True, exist_ok=True)
                (c_root / "KONGOR" / "updates").mkdir(parents=True, exist_ok=True)
                (c_root / "KONGOR" / "configuration").mkdir(parents=True, exist_ok=True)
                (c_root / "override" / "ui" / "fe2").mkdir(parents=True, exist_ok=True)
                (c_root / "override" / "ui" / "common").mkdir(parents=True, exist_ok=True)
                s_root.mkdir(parents=True, exist_ok=True)

            (stg_client / "KONGOR" / "configuration" / "init.cfg").write_text(
                'Set terrain_chunksize "32"\n', encoding="utf-8"
            )
            (stg_client / "KONGOR" / "configuration" / "autoexec.cfg").write_text(
                "// clean\n", encoding="utf-8"
            )

            for c_root in (src_client, stg_client):
                with zipfile.ZipFile(
                    c_root / "game" / "textures.s2z",
                    mode="w",
                    compression=zipfile.ZIP_STORED,
                    allowZip64=False,
                ) as zf_game:
                    zf_game.writestr("00000000/ui/fe2/shard2_only.dds", b"SHARD2")

                with zipfile.ZipFile(
                    c_root / "KONGOR" / "updates" / "textures.s2z",
                    mode="w",
                    compression=zipfile.ZIP_STORED,
                    allowZip64=False,
                ) as zf_upd:
                    zf_upd.writestr("00000000/ui/fe2/shard1_tab.dds", b"SHARD1_TAB")
                    zf_upd.writestr("00000001/ui/common/miku_bg.dds", b"SHARD1_MIP1")
                    zf_upd.writestr("trees/pine_01.dds", b"SHARD1_TREE")

                (c_root / "override" / "ui" / "fe2" / "shard1_tab.dds").write_bytes(
                    b"OVERRIDE_SHARD1_TAB"
                )
                (c_root / "override" / "ui" / "common" / "miku_bg.dds").write_bytes(
                    b"OVERRIDE_SHARD1_MIP1"
                )
                (c_root / "override" / "ui" / "fe2" / "brand_new.dds").write_bytes(
                    b"OVERRIDE_BRAND_NEW"
                )

            # Pack staged override/ with repeatable --textures-s2z (both Shard 2 & Shard 1)
            self.assertEqual(
                0,
                vfs_tool.main([
                    "pack-tier",
                    "--tier-dir",
                    str(stg_client / "override"),
                    "--tier-name",
                    "override",
                    "--suffix-style",
                    "digits",
                    "--textures-s2z",
                    str(stg_client / "game" / "textures.s2z"),
                    "--textures-s2z",
                    str(stg_client / "KONGOR" / "updates" / "textures.s2z"),
                ]),
            )

            # Shard 1 collisions (both 00000000/* and 00000001/*) MUST remain loose; brand_new.dds is packed
            self.assertTrue(
                (stg_client / "override" / "ui" / "fe2" / "shard1_tab.dds").is_file()
            )
            self.assertTrue(
                (stg_client / "override" / "ui" / "common" / "miku_bg.dds").is_file()
            )
            self.assertFalse(
                (stg_client / "override" / "ui" / "fe2" / "brand_new.dds").exists()
            )
            self.assertTrue((stg_client / "override" / "resources00.s2z").is_file())

            # Parity MUST pass when staged KONGOR/updates/textures.s2z is present
            parity_ok = root / "parity_shard1_ok.json"
            self.assertEqual(
                0,
                vfs_tool.main([
                    "verify-parity",
                    "--source-client",
                    str(src_client),
                    "--source-shared",
                    str(src_shared),
                    "--staged-client",
                    str(stg_client),
                    "--staged-shared",
                    str(stg_shared),
                    "--json-out",
                    str(parity_ok),
                ]),
            )

            # Removing staged KONGOR/updates/textures.s2z MUST fail verify-parity (Issue 8A regression guard)
            updates_tex = stg_client / "KONGOR" / "updates" / "textures.s2z"
            backup_bytes = updates_tex.read_bytes()
            updates_tex.unlink()
            parity_missing = root / "parity_shard1_missing.json"
            rc_missing = vfs_tool.main([
                "verify-parity",
                "--source-client",
                str(src_client),
                "--source-shared",
                str(src_shared),
                "--staged-client",
                str(stg_client),
                "--staged-shared",
                str(stg_shared),
                "--json-out",
                str(parity_missing),
            ])
            self.assertEqual(1, rc_missing)
            rep_missing = json.loads(parity_missing.read_text(encoding="utf-8"))
            self.assertFalse(rep_missing["passed"])
            self.assertGreaterEqual(rep_missing["missingInStagedCount"], 3)
            self.assertIn("trees/pine_01.dds", rep_missing["missingInStagedSamples"])

            # Restore Shard 1 textures.s2z and verify FN-003 catches packed Shard 1 override collision
            updates_tex.write_bytes(backup_bytes)
            miku_loose = stg_client / "override" / "ui" / "common" / "miku_bg.dds"
            miku_bytes = miku_loose.read_bytes()
            miku_loose.unlink()
            with zipfile.ZipFile(
                stg_client / "override" / "resources00.s2z",
                mode="a",
                compression=zipfile.ZIP_STORED,
                allowZip64=False,
            ) as zf_ov:
                zf_ov.writestr("ui/common/miku_bg.dds", miku_bytes)

            parity_fn003 = root / "parity_shard1_fn003.json"
            rc_fn003 = vfs_tool.main([
                "verify-parity",
                "--source-client",
                str(src_client),
                "--source-shared",
                str(src_shared),
                "--staged-client",
                str(stg_client),
                "--staged-shared",
                str(stg_shared),
                "--json-out",
                str(parity_fn003),
            ])
            self.assertEqual(1, rc_fn003)
            rep_fn003 = json.loads(parity_fn003.read_text(encoding="utf-8"))
            self.assertGreaterEqual(rep_fn003["fn003ViolationCount"], 1)

    def test_inspect_vpath_and_bots_core_winner_override_precedence(self) -> None:
        """
        Protects:
          - Stage 7/8 verification that bots/core.lua wins from override (not vanilla game/resources0.s2z
            or game/resources98.s2z) and contains function core.IsMidwarsMap.
        Fails when:
          - inspect-vpath or botsCoreWinner fails to resolve packed override/resources00.s2z or silently
            accepts vanilla game/resources98.s2z lacking IsMidwarsMap.
        Seam:
          - Real ZipFile .s2z archives across game/ and override/ + CLI inspect-vpath & verify-parity.
        """
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            src_client = root / "src" / "client"
            src_shared = root / "src" / "shared"
            stg_client = root / "stg" / "client"
            stg_shared = root / "stg" / "shared"

            for c_root, s_root in ((src_client, src_shared), (stg_client, stg_shared)):
                (c_root / "game").mkdir(parents=True, exist_ok=True)
                (c_root / "KONGOR" / "configuration").mkdir(parents=True, exist_ok=True)
                (c_root / "override" / "bots").mkdir(parents=True, exist_ok=True)
                s_root.mkdir(parents=True, exist_ok=True)

            (stg_client / "KONGOR" / "configuration" / "init.cfg").write_text(
                'Set terrain_chunksize "32"\n', encoding="utf-8"
            )
            (stg_client / "KONGOR" / "configuration" / "autoexec.cfg").write_text(
                "// clean\n", encoding="utf-8"
            )

            vanilla_core = "-- vanilla core\nlocal core = {}\nreturn core\n"
            override_core = (
                "-- Kongor override core\n"
                "local core = {}\n"
                "function core.IsMidwarsMap(sMapOrVars)\n"
                "\treturn true\n"
                "end\n"
            )

            for c_root in (src_client, stg_client):
                with zipfile.ZipFile(
                    c_root / "game" / "resources0.s2z",
                    mode="w",
                    compression=zipfile.ZIP_DEFLATED,
                    allowZip64=False,
                ) as zf0:
                    zf0.writestr("bots/core.lua", vanilla_core)
                with zipfile.ZipFile(
                    c_root / "game" / "resources98.s2z",
                    mode="w",
                    compression=zipfile.ZIP_DEFLATED,
                    allowZip64=False,
                ) as zf98:
                    zf98.writestr("bots/core.lua", vanilla_core)
                (c_root / "override" / "bots" / "core.lua").write_text(
                    override_core, encoding="utf-8"
                )

            self.assertEqual(
                0,
                vfs_tool.main([
                    "pack-tier",
                    "--tier-dir",
                    str(stg_client / "override"),
                    "--tier-name",
                    "override",
                    "--suffix-style",
                    "digits",
                ]),
            )

            inspect_ok = root / "inspect_ok.json"
            self.assertEqual(
                0,
                vfs_tool.main([
                    "inspect-vpath",
                    "--staged-client",
                    str(stg_client),
                    "--staged-shared",
                    str(stg_shared),
                    "--vpath",
                    "bots/core.lua",
                    "--expected-tier",
                    "override",
                    "--require-text",
                    "function core.IsMidwarsMap",
                    "--json-out",
                    str(inspect_ok),
                ]),
            )
            insp_rep = json.loads(inspect_ok.read_text(encoding="utf-8"))
            self.assertTrue(insp_rep["passed"])
            self.assertEqual("override", insp_rep["tier"])
            self.assertEqual("s2z:resources00.s2z", insp_rep["origin"])
            self.assertTrue(insp_rep["matchedRequireText"])

            parity_json = root / "parity_bots.json"
            self.assertEqual(
                0,
                vfs_tool.main([
                    "verify-parity",
                    "--source-client",
                    str(src_client),
                    "--source-shared",
                    str(src_shared),
                    "--staged-client",
                    str(stg_client),
                    "--staged-shared",
                    str(stg_shared),
                    "--json-out",
                    str(parity_json),
                ]),
            )
            par_rep = json.loads(parity_json.read_text(encoding="utf-8"))
            self.assertEqual("override", par_rep["botsCoreWinner"]["tier"])

            # Removing override/resources00.s2z falls back to game/resources98.s2z and MUST fail inspect-vpath
            (stg_client / "override" / "resources00.s2z").unlink()
            inspect_fail = root / "inspect_fail.json"
            rc_fail = vfs_tool.main([
                "inspect-vpath",
                "--staged-client",
                str(stg_client),
                "--staged-shared",
                str(stg_shared),
                "--vpath",
                "bots/core.lua",
                "--expected-tier",
                "override",
                "--require-text",
                "function core.IsMidwarsMap",
                "--json-out",
                str(inspect_fail),
            ])
            self.assertEqual(1, rc_fail)
            insp_fail_rep = json.loads(inspect_fail.read_text(encoding="utf-8"))
            self.assertFalse(insp_fail_rep["passed"])
            self.assertEqual("game", insp_fail_rep["tier"])
            self.assertEqual("s2z:resources98.s2z", insp_fail_rep["origin"])


if __name__ == "__main__":
    unittest.main()

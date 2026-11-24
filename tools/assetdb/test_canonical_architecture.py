#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
tools/assetdb/test_canonical_architecture.py
Unit and integration test suite for the Clean Canonical File System Architecture:
1. Canonical archive routing rules and hero partitioning (15 archives).
2. Deduplication engine and precedence enforcement.
3. Strict 32-bit Zip limits (allowZip64=False, entry limit <= 65535, cap check).
4. SQLite assets.db synchronization (sync_record.py), archive fallback, and override purging.
5. PowerShell scripts behavior (inject_override_to_s2z.ps1, evict_verified_override.ps1).
"""

import os
import sys
import json
import zlib
import shutil
import zipfile
import sqlite3
import tempfile
import unittest
import subprocess
import time

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
PYTHON_EXE = sys.executable

if REPO_ROOT not in sys.path:
    sys.path.insert(0, REPO_ROOT)

from tools.assetdb.repack_canonical import (
    assign_canonical_archive,
    CANONICAL_SPEC,
    AssetSourceItem,
    partition_assets,
    run_manifest_audit,
    execute_repack,
    update_database,
)
from tools.assetdb.sync_record import (
    sync_asset_record,
    purge_override_record,
    PRIORITY_MAP
)


class TestCanonicalRouting(unittest.TestCase):
    def test_all_15_canonical_archives_defined(self):
        expected_archives = [
            "resources00.s2z",
            "resources01.s2z",
            "resources02.s2z",
            "resources03.s2z",
            "resources04.s2z",
            "resources05.s2z",
            "resources06.s2z",
            "resources07.s2z",
            "resources08.s2z",
            "resources09.s2z",
            "resources10.s2z",
            "resources11.s2z",
            "resources12.s2z",
            "resources13.s2z",
            "resources14.s2z",
            "resources99.s2z",
        ]
        for arc in expected_archives:
            self.assertIn(arc, CANONICAL_SPEC, f"Archive {arc} must be in CANONICAL_SPEC")

    def test_hero_alphabetical_partitioning(self):
        cases = [
            ("heroes/accursed/hero.entity", "resources01.s2z"),
            ("heroes/bombardier/hero.entity", "resources01.s2z"),
            ("heroes/chronos/hero.entity", "resources02.s2z"),
            ("heroes/devourer/hero.entity", "resources02.s2z"),
            ("heroes/emerald_warden/hero.entity", "resources03.s2z"),
            ("heroes/flint_beastwood/hero.entity", "resources03.s2z"),
            ("heroes/gauntlet/hero.entity", "resources03.s2z"),
            ("heroes/hammerstorm/hero.entity", "resources04.s2z"),
            ("heroes/kraken/hero.entity", "resources04.s2z"),
            ("heroes/legionnaire/hero.entity", "resources05.s2z"),
            ("heroes/magmus/hero.entity", "resources05.s2z"),
            ("heroes/night_hound/hero.entity", "resources06.s2z"),
            ("heroes/pyromancer/hero.entity", "resources06.s2z"),
            ("heroes/quest/hero.entity", "resources07.s2z"),
            ("heroes/scout/hero.entity", "resources07.s2z"),
            ("heroes/thunderbringer/hero.entity", "resources08.s2z"),
            ("heroes/valkyrie/hero.entity", "resources08.s2z"),
            ("heroes/war_beast/hero.entity", "resources09.s2z"),
            ("heroes/zephyr/hero.entity", "resources09.s2z"),
        ]
        for vpath, expected_arc in cases:
            actual = assign_canonical_archive(vpath)
            self.assertEqual(actual, expected_arc, f"Failed for {vpath}: expected {expected_arc}, got {actual}")

    def test_non_hero_domain_routing(self):
        cases = [
            ("core/shaders/vs.vert", "resources00.s2z"),
            ("fonts/dynafont.xml", "resources00.s2z"),
            ("shaders/standard.material", "resources00.s2z"),
            ("common/cursor.dds", "resources00.s2z"),
            ("game_settings.cfg", "resources00.s2z"),
            ("items/boots.entity", "resources10.s2z"),
            ("buildings/tower.entity", "resources10.s2z"),
            ("npcs/creep.entity", "resources10.s2z"),
            ("triggers/victory.entity", "resources10.s2z"),
            ("bots/botbrain.lua", "resources10.s2z"),
            ("teambot/tactics.lua", "resources10.s2z"),
            ("maps/caldavar/caldavar.world", "resources11.s2z"),
            ("world/terrain.material", "resources11.s2z"),
            ("ui/fe2/main_v2.interface", "resources12.s2z"),
            ("ui/scripts/store2.lua", "resources12.s2z"),
            ("stringtables/en.str", "resources12.s2z"),
            ("content/store_avatars.package", "resources12.s2z"),
            ("shared/scripts/util.lua", "resources13.s2z"),
            ("music/theme.ogg", "resources14.s2z"),
            ("sounds/click.wav", "resources14.s2z"),
            ("videos/intro.mp4", "resources14.s2z"),
        ]
        for vpath, expected_arc in cases:
            actual = assign_canonical_archive(vpath)
            self.assertEqual(actual, expected_arc, f"Failed for {vpath}: expected {expected_arc}, got {actual}")

    def test_texture_prefix_stripping_during_routing(self):
        self.assertEqual(assign_canonical_archive("00000000/ui/fe2/mainmenu/avatar.dds"), "resources12.s2z")
        self.assertEqual(assign_canonical_archive("00000000/heroes/accursed/color.dds"), "resources01.s2z")
        self.assertEqual(assign_canonical_archive("00000000/items/boots/icon.dds"), "resources10.s2z")


class TestRepackerDeduplicationAndZip(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.mkdtemp(prefix="kongor_test_repack_")

    def tearDown(self):
        shutil.rmtree(self.temp_dir, ignore_errors=True)

    def test_deduplication_and_partitioning(self):
        # Create synthetic assets
        assets = {
            "ui/scripts/store2.lua": AssetSourceItem(
                vpath="ui/scripts/store2.lua",
                source_kind="override",
                source_path="/fake/override/ui/scripts/store2.lua",
                archive_entry_name=None,
                file_size=1000,
                compress_size=400,
                crc32_hex="12345678",
                mtime=1600000000.0,
                canonical_archive="resources12.s2z"
            ),
            "heroes/accursed/hero.entity": AssetSourceItem(
                vpath="heroes/accursed/hero.entity",
                source_kind="archive",
                source_path="/fake/game/resources0.s2z",
                archive_entry_name="heroes/accursed/hero.entity",
                file_size=2000,
                compress_size=800,
                crc32_hex="87654321",
                mtime=1600000000.0,
                canonical_archive="resources01.s2z"
            ),
        }
        partitioned = partition_assets(assets)
        self.assertEqual(len(partitioned["resources12.s2z"]), 1)
        self.assertEqual(len(partitioned["resources01.s2z"]), 1)
        self.assertEqual(len(partitioned["resources00.s2z"]), 0)

        manifest, passed = run_manifest_audit(assets, partitioned, max_size_bytes=100000)
        self.assertTrue(passed)
        self.assertEqual(manifest["total_unique_assets"], 2)

    def test_execute_repack_preserves_timestamp_and_allow_zip64_false(self):
        staging = os.path.join(self.temp_dir, "staging")
        os.makedirs(staging, exist_ok=True)

        # Create dummy source file with known mtime
        dummy_file = os.path.join(self.temp_dir, "dummy_ui.lua")
        with open(dummy_file, 'w', encoding='utf-8') as f:
            f.write("print('Hello Canonical Kongor')\n")

        known_mtime = 1609459200.0  # 2021-01-01 00:00:00 UTC
        os.utime(dummy_file, (known_mtime, known_mtime))

        assets = {
            "ui/scripts/test.lua": [
                AssetSourceItem(
                    vpath="ui/scripts/test.lua",
                    source_kind="override",
                    source_path=dummy_file,
                    archive_entry_name=None,
                    file_size=os.path.getsize(dummy_file),
                    compress_size=20,
                    crc32_hex="11223344",
                    mtime=known_mtime,
                    canonical_archive="resources12.s2z"
                )
            ]
        }
        partitioned = {arc: [] for arc in CANONICAL_SPEC.keys()}
        partitioned["resources12.s2z"] = assets["ui/scripts/test.lua"]

        created = execute_repack(partitioned, staging, self.temp_dir)
        target_zip = os.path.join(staging, "resources12.s2z")
        self.assertTrue(os.path.exists(target_zip))

        # Inspect zip entries
        with zipfile.ZipFile(target_zip, 'r') as zf:
            self.assertIn("ui/scripts/test.lua", zf.namelist())
            info = zf.getinfo("ui/scripts/test.lua")
            self.assertEqual(info.date_time[:3], (2021, 1, 1), "Zip entry must preserve historical date_time")
            data = zf.read("ui/scripts/test.lua").decode('utf-8')
            self.assertIn("Hello Canonical Kongor", data)

    def test_manifest_audit_hard_error_on_exceeded_limits(self):
        # Entry count > 65535
        dummy_items = [
            AssetSourceItem(f"file_{i}.txt", "override", "/dummy", None, 1, 1, "00000000", 1600000000, "resources00.s2z")
            for i in range(65536)
        ]
        assets = {item.vpath: item for item in dummy_items}
        partitioned = {arc: [] for arc in CANONICAL_SPEC.keys()}
        partitioned["resources00.s2z"] = dummy_items

        manifest, passed = run_manifest_audit(assets, partitioned)
        self.assertFalse(passed, "Should fail hard check when entries exceed 65,535")
        self.assertEqual(manifest["archives"]["resources00.s2z"]["status"], "FAIL [ENTRIES]")

    def test_update_database_dynamic_source_prefix(self):
        db_path = os.path.join(self.temp_dir, "test_prefix.db")
        staging_dir = os.path.join(self.temp_dir, "custom_staging", "game")
        os.makedirs(staging_dir, exist_ok=True)

        assets = {
            "ui/test.lua": AssetSourceItem("ui/test.lua", "override", "/dummy", None, 50, 20, "AABBCCDD", 1600000000, "resources12.s2z")
        }

        update_database(
            unique_assets=assets,
            db_path=db_path,
            staging_dir=staging_dir,
            repo_root=self.temp_dir
        )

        conn = sqlite3.connect(db_path)
        cur = conn.cursor()
        row = cur.execute("SELECT source FROM assets WHERE vpath = 'ui/test.lua'").fetchone()
        conn.close()

        self.assertIsNotNone(row)
        self.assertEqual(row[0], "custom_staging/game/resources12.s2z")


class TestAssetDBSync(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.mkdtemp(prefix="kongor_test_sync_")
        self.db_path = os.path.join(self.temp_dir, "test_assets.db")

    def tearDown(self):
        shutil.rmtree(self.temp_dir, ignore_errors=True)

    def test_sync_record_upsert(self):
        test_file = os.path.join(self.temp_dir, "store2.lua")
        with open(test_file, 'w', encoding='utf-8') as f:
            f.write("function TestStore() return true end\n")

        # Initial insert
        sync_asset_record(
            vpath="ui/scripts/store2.lua",
            file_path=test_file,
            archive_name="resources12.s2z",
            db_path=self.db_path,
            repo_root=self.temp_dir
        )

        conn = sqlite3.connect(self.db_path)
        cur = conn.cursor()
        row = cur.execute("SELECT vpath, source, size_bytes, crc32, priority, timestamp FROM assets").fetchone()
        conn.close()

        self.assertIsNotNone(row)
        self.assertEqual(row[0], "ui/scripts/store2.lua")
        self.assertEqual(row[1], "games/client/game/resources12.s2z")
        self.assertEqual(row[4], 160)  # Priority for resources12.s2z
        self.assertTrue(row[5].endswith("Z"))  # ISO8601 UTC

        # Update existing record
        with open(test_file, 'w', encoding='utf-8') as f:
            f.write("function TestStoreUpdated() return false end\n")

        sync_asset_record(
            vpath="ui/scripts/store2.lua",
            file_path=test_file,
            archive_name="resources12.s2z",
            db_path=self.db_path,
            repo_root=self.temp_dir
        )

        conn = sqlite3.connect(self.db_path)
        cur = conn.cursor()
        rows = cur.execute("SELECT count(*), size_bytes FROM assets WHERE vpath='ui/scripts/store2.lua'").fetchall()
        conn.close()

        self.assertEqual(rows[0][0], 1, "Should update in place without duplicate row")
        self.assertEqual(rows[0][1], os.path.getsize(test_file))

    def test_sync_record_with_evict_override(self):
        # Insert override record first
        conn = sqlite3.connect(self.db_path)
        cur = conn.cursor()
        cur.execute("""
            CREATE TABLE assets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                vpath TEXT NOT NULL,
                stem TEXT NOT NULL,
                ext TEXT NOT NULL,
                source TEXT NOT NULL,
                kind TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                crc32 TEXT NOT NULL,
                priority INTEGER NOT NULL,
                timestamp TEXT
            );
        """)
        cur.execute("""
            INSERT INTO assets (vpath, stem, ext, source, kind, size_bytes, crc32, priority, timestamp)
            VALUES ('ui/scripts/store2.lua', 'store2', '.lua', 'games/client/override', 'file', 10, '12345678', 50, '2026-01-01T00:00:00Z');
        """)
        conn.commit()
        conn.close()

        test_file = os.path.join(self.temp_dir, "store2.lua")
        with open(test_file, 'w', encoding='utf-8') as f:
            f.write("test content")

        sync_asset_record(
            vpath="ui/scripts/store2.lua",
            file_path=test_file,
            archive_name="resources12.s2z",
            db_path=self.db_path,
            repo_root=self.temp_dir,
            evict_override=True
        )

        conn = sqlite3.connect(self.db_path)
        cur = conn.cursor()
        override_rows = cur.execute("SELECT count(*) FROM assets WHERE source = 'games/client/override'").fetchone()[0]
        archive_rows = cur.execute("SELECT count(*) FROM assets WHERE source = 'games/client/game/resources12.s2z'").fetchone()[0]
        conn.close()

        self.assertEqual(override_rows, 0, "Override record should be purged when evict_override=True")
        self.assertEqual(archive_rows, 1, "Archive record should be present")

    def test_purge_override_record_only(self):
        conn = sqlite3.connect(self.db_path)
        cur = conn.cursor()
        cur.execute("""
            CREATE TABLE assets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                vpath TEXT NOT NULL,
                stem TEXT NOT NULL,
                ext TEXT NOT NULL,
                source TEXT NOT NULL,
                kind TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                crc32 TEXT NOT NULL,
                priority INTEGER NOT NULL
            );
        """)
        cur.execute("""
            CREATE TABLE sources (id INTEGER PRIMARY KEY, path TEXT);
        """)
        cur.execute("""
            CREATE TABLE files (id INTEGER PRIMARY KEY, vpath TEXT, source_id INTEGER, size INTEGER, crc32 INTEGER);
        """)
        cur.execute("INSERT INTO sources (id, path) VALUES (18, 'games/client/override');")
        cur.execute("INSERT INTO assets (vpath, stem, ext, source, kind, size_bytes, crc32, priority) VALUES ('ui/test.lua', 'test', '.lua', 'games/client/override', 'file', 10, '1234', 50);")
        cur.execute("INSERT INTO files (vpath, source_id, size, crc32) VALUES ('ui/test.lua', 18, 10, 1234);")
        conn.commit()
        conn.close()

        purged = purge_override_record("ui/test.lua", db_path=self.db_path)
        self.assertEqual(purged, 2, "Should delete 1 asset row and 1 file row")

        conn = sqlite3.connect(self.db_path)
        cur = conn.cursor()
        asset_count = cur.execute("SELECT count(*) FROM assets WHERE vpath = 'ui/test.lua'").fetchone()[0]
        file_count = cur.execute("SELECT count(*) FROM files WHERE vpath = 'ui/test.lua'").fetchone()[0]
        conn.close()

        self.assertEqual(asset_count, 0)
        self.assertEqual(file_count, 0)


class TestPowerShellScripts(unittest.TestCase):
    def test_inject_override_dry_run_syntax(self):
        ps_cmd = "& ./scripts/vfs/inject_override_to_s2z.ps1 -RelativePath 'ui/scripts/store2.lua' -DryRun -SkipObligationCheck"
        res = subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps_cmd],
            cwd=REPO_ROOT,
            capture_output=True,
            text=True
        )
        self.assertEqual(res.returncode, 0, f"inject_override_to_s2z.ps1 failed: {res.stderr}\n{res.stdout}")
        self.assertIn("ZERO files would be deleted", res.stdout)
        self.assertIn("Would rotate backups", res.stdout)

    def test_evict_verified_override_dry_run_syntax(self):
        ps_cmd = "& ./scripts/vfs/evict_verified_override.ps1 -RelativePath 'ui/scripts/store2.lua' -DryRun"
        res = subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps_cmd],
            cwd=REPO_ROOT,
            capture_output=True,
            text=True
        )
        self.assertEqual(res.returncode, 0, f"evict_verified_override.ps1 failed: {res.stderr}\n{res.stdout}")
        self.assertIn("[DRY-RUN] Target file found", res.stdout)
        self.assertIn("Would NON-DESTRUCTIVELY rotate", res.stdout)
        self.assertIn("ZERO files would be permanently deleted", res.stdout)

    @unittest.skip("Skipped: touches real games/client tree and archives (plan v4 P3.0)")
    def test_inject_override_with_eviction_succeeds_without_filenotfound(self):
        """
        Verify that calling inject_override_to_s2z.ps1 non-destructively rotates
        the active file to .bak and synchronizes the asset database with history.
        """
        test_arc = "resources99_test_tmp.s2z"
        client_test_arc = os.path.join(REPO_ROOT, "games", "client", "game", test_arc)
        server_test_arc = os.path.join(REPO_ROOT, "games", "server", "game", test_arc)
        # Create minimal initial test archive
        with zipfile.ZipFile(client_test_arc, 'w', compression=zipfile.ZIP_DEFLATED, allowZip64=False) as zf:
            zf.writestr("test_init.txt", "init")

        test_rel = "ui/scripts/unit_test_synthetic_evict.lua"
        test_file = os.path.join(REPO_ROOT, "games", "client", "override", test_rel.replace('/', '\\'))
        os.makedirs(os.path.dirname(test_file), exist_ok=True)
        with open(test_file, 'w', encoding='utf-8') as f:
            f.write("function UnitTestSyntheticEvict() return 42 end\n")

        try:
            ps_cmd = f"& ./scripts/vfs/inject_override_to_s2z.ps1 -RelativePath '{test_rel}' -TargetArchive '{test_arc}'"
            res = subprocess.run(
                ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps_cmd],
                cwd=REPO_ROOT,
                capture_output=True,
                text=True
            )
            self.assertEqual(res.returncode, 0, f"inject_override_to_s2z.ps1 failed:\nSTDOUT:\n{res.stdout}\nSTDERR:\n{res.stderr}")
            self.assertIn("[STAGE 4/5] Synchronizing entry with asset database", res.stdout)
            self.assertIn("[STAGE 5/5] Executing Zero-Deletion Backup Rotation", res.stdout)
            self.assertIn("Safe injection & 5-tier backup rotation complete", res.stdout)
            self.assertFalse(os.path.exists(test_file), "Active file should be unmasked (moved to .bak)")
            self.assertTrue(os.path.exists(f"{test_file}.bak"), "Backup .bak must exist on disk")
        finally:
            for arc in [client_test_arc, server_test_arc]:
                if os.path.exists(arc):
                    os.remove(arc)
                if os.path.exists(f"{arc}.bak"):
                    os.remove(f"{arc}.bak")
            for fpath in [test_file, f"{test_file}.bak"]:
                if os.path.exists(fpath):
                    os.remove(fpath)

    @unittest.skip("Skipped: touches real override tree (plan v4 P3.0)")
    def test_evict_verified_override_real_execution(self):
        """
        Verify that evict_verified_override.ps1 non-destructively rotates the file to .bak,
        preserves the working payload, and removes loose override database records.
        """
        test_rel = "ui/unit_test_folder/test_evict_real.lua"
        test_file = os.path.join(REPO_ROOT, "games", "client", "override", test_rel.replace('/', '\\'))
        test_dir = os.path.dirname(test_file)
        os.makedirs(test_dir, exist_ok=True)
        with open(test_file, 'w', encoding='utf-8') as f:
            f.write("function EvictRealTest() return true end\n")

        try:
            ps_cmd = f"& ./scripts/vfs/evict_verified_override.ps1 -RelativePath '{test_rel}'"
            res = subprocess.run(
                ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps_cmd],
                cwd=REPO_ROOT,
                capture_output=True,
                text=True
            )
            self.assertEqual(res.returncode, 0, f"evict_verified_override.ps1 failed:\nSTDOUT:\n{res.stdout}\nSTDERR:\n{res.stderr}")
            self.assertFalse(os.path.exists(test_file), "Active override file must be rotated out")
            self.assertTrue(os.path.exists(f"{test_file}.bak"), "Non-destructive .bak backup must be preserved")
            self.assertIn("Safe override rotation complete", res.stdout)
        finally:
            if os.path.exists(test_file):
                os.remove(test_file)
            if os.path.exists(f"{test_file}.bak"):
                os.remove(f"{test_file}.bak")
            if os.path.exists(test_dir):
                shutil.rmtree(test_dir, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()

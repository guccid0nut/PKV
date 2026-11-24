#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
tools/assetdb/test_kpatch_preview_guards.py
Hermetic unit test suite for .kpatch preview, dry-run, and fail-closed safety guards:
1. Preview detection of missing chunks.
2. Baseline CRC mismatch detection and fail-closed prevention of archive clobbering.
3. Clean temporary archive rollback on chunk assembly failures.
4. Preview report structure and CLI JSON serialization.
"""

import os
import sys
import json
import zlib
import sqlite3
import zipfile
import tempfile
import unittest
import subprocess

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))

if REPO_ROOT not in sys.path:
    sys.path.insert(0, REPO_ROOT)

from tools.assetdb.cas import (
    init_cas_schema,
    chunk_and_hash_data,
    save_chunk_to_cas,
    register_blob_in_db,
    create_manifest_in_db,
)
from tools.assetdb.patch_engine import create_kpatch, preview_kpatch, apply_kpatch


class TestKpatchPreviewGuards(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.root = self.temp_dir.name
        self.game_dir = os.path.join(self.root, "game")
        os.makedirs(self.game_dir, exist_ok=True)

        # Create baseline resources01.s2z
        self.arc_path = os.path.join(self.game_dir, "resources01.s2z")
        self.orig_content = b"original file content for testing"
        self.orig_crc = zlib.crc32(self.orig_content) & 0xFFFFFFFF
        with zipfile.ZipFile(self.arc_path, 'w', compression=zipfile.ZIP_DEFLATED, allowZip64=False) as zf:
            zinfo = zipfile.ZipInfo("ui/test_baseline.txt", date_time=(2026, 1, 1, 0, 0, 0))
            zf.writestr(zinfo, self.orig_content)

    def tearDown(self):
        self.temp_dir.cleanup()

    def _create_sample_kpatch(self, new_content, expected_old_crc=None, omit_chunk=False):
        kpatch_path = os.path.join(self.root, "test.kpatch")
        new_crc = zlib.crc32(new_content) & 0xFFFFFFFF
        chunk_hash = "deadbeefcafebabe0123456789abcdef0123456789abcdef0123456789abcdef"

        manifest = {
            "old_manifest_id": "man_v1",
            "new_manifest_id": "man_v2",
            "added": {},
            "modified": {
                "ui/test_baseline.txt": [
                    "old_hash_placeholder",
                    chunk_hash,
                    "resources01.s2z",
                    expected_old_crc if expected_old_crc is not None else self.orig_crc
                ]
            },
            "removed": {},
            "required_chunks": [chunk_hash]
        }

        with zipfile.ZipFile(kpatch_path, 'w', compression=zipfile.ZIP_DEFLATED) as pzf:
            pzf.writestr("patch_manifest.json", json.dumps(manifest, indent=2).encode('utf-8'))
            if not omit_chunk:
                comp_data = zlib.compress(new_content)
                pzf.writestr(f"chunks/{chunk_hash}", comp_data)

        return kpatch_path

    def test_preview_matching_baseline_succeeds(self):
        new_data = b"new updated content"
        kpatch = self._create_sample_kpatch(new_data, expected_old_crc=self.orig_crc)

        res = preview_kpatch(kpatch, self.game_dir, as_json=False)
        self.assertTrue(res["ok"])
        self.assertEqual(res["summary"]["missing_chunks_count"], 0)
        self.assertEqual(res["summary"]["baseline_crc_mismatches"], 0)
        self.assertEqual(res["summary"]["modified"], 1)

    def test_preview_detects_missing_chunk(self):
        new_data = b"new updated content"
        kpatch = self._create_sample_kpatch(new_data, expected_old_crc=self.orig_crc, omit_chunk=True)

        res = preview_kpatch(kpatch, self.game_dir, as_json=False)
        self.assertFalse(res["ok"])
        self.assertEqual(res["summary"]["missing_chunks_count"], 1)

    def test_preview_detects_baseline_crc_mismatch(self):
        new_data = b"new updated content"
        # Expect an old CRC that does not match the actual archive entry
        kpatch = self._create_sample_kpatch(new_data, expected_old_crc=0x12345678)

        res = preview_kpatch(kpatch, self.game_dir, as_json=False)
        self.assertFalse(res["ok"])
        self.assertEqual(res["summary"]["baseline_crc_mismatches"], 1)
        arc_info = res["archives"]["resources01.s2z"]
        self.assertEqual(len(arc_info["crc_mismatches"]), 1)
        self.assertEqual(arc_info["crc_mismatches"][0]["path"], "ui/test_baseline.txt")

    def test_apply_fails_closed_on_missing_chunk(self):
        new_data = b"new updated content"
        kpatch = self._create_sample_kpatch(new_data, expected_old_crc=self.orig_crc, omit_chunk=True)

        with self.assertRaises(RuntimeError) as ctx:
            apply_kpatch(kpatch, self.game_dir)
        self.assertIn("required CAS chunks are missing", str(ctx.exception))

        # Archive must not have been modified or had .bak created
        self.assertFalse(os.path.exists(f"{self.arc_path}.bak.1"))
        with zipfile.ZipFile(self.arc_path, 'r') as zf:
            self.assertEqual(zf.read("ui/test_baseline.txt"), self.orig_content)

    def test_apply_fails_closed_on_baseline_mismatch_without_force(self):
        new_data = b"new updated content"
        kpatch = self._create_sample_kpatch(new_data, expected_old_crc=0x12345678)

        with self.assertRaises(RuntimeError) as ctx:
            apply_kpatch(kpatch, self.game_dir, force=False)
        self.assertIn("Baseline state mismatch", str(ctx.exception))

        # Archive must not have been modified
        with zipfile.ZipFile(self.arc_path, 'r') as zf:
            self.assertEqual(zf.read("ui/test_baseline.txt"), self.orig_content)

    def test_apply_succeeds_and_creates_backup_when_valid(self):
        new_data = b"new verified content"
        kpatch = self._create_sample_kpatch(new_data, expected_old_crc=self.orig_crc)

        res = apply_kpatch(kpatch, self.game_dir)
        self.assertTrue(res)

        # Archive has updated content and rotated backup exists
        self.assertTrue(os.path.exists(f"{self.arc_path}.bak.1"))
        with zipfile.ZipFile(self.arc_path, 'r') as zf:
            self.assertEqual(zf.read("ui/test_baseline.txt"), new_data)
        with zipfile.ZipFile(f"{self.arc_path}.bak.1", 'r') as zf:
            self.assertEqual(zf.read("ui/test_baseline.txt"), self.orig_content)

    def _seed_cas_manifests(self):
        cas_dir = os.path.join(self.root, ".cas")
        db_path = os.path.join(self.root, "assets.db")
        conn = sqlite3.connect(db_path)
        init_cas_schema(conn)

        b1_hash, b1_sz, b1_crc, b1_cnt, b1_ch, b1_rec = chunk_and_hash_data(self.orig_content)
        for ch_hash, _, _, _, _, comp_bytes in b1_rec:
            save_chunk_to_cas(cas_dir, ch_hash, comp_bytes)
        register_blob_in_db(conn, b1_hash, b1_sz, b1_crc, b1_cnt, b1_ch, b1_rec)
        create_manifest_in_db(
            conn,
            "v1",
            [("ui/test_baseline.txt", b1_hash, "resources01.s2z", b1_crc, b1_sz)],
            manifest_id="man_old",
        )

        v2_bytes = b"updated v2 payload requiring cas chunk"
        b2_hash, b2_sz, b2_crc, b2_cnt, b2_ch, b2_rec = chunk_and_hash_data(v2_bytes)
        for ch_hash, _, _, _, _, comp_bytes in b2_rec:
            save_chunk_to_cas(cas_dir, ch_hash, comp_bytes)
        register_blob_in_db(conn, b2_hash, b2_sz, b2_crc, b2_cnt, b2_ch, b2_rec)
        create_manifest_in_db(
            conn,
            "v2",
            [("ui/test_baseline.txt", b2_hash, "resources01.s2z", b2_crc, b2_sz)],
            manifest_id="man_new",
        )
        conn.close()
        return db_path, cas_dir

    def test_create_kpatch_raises_and_exits_nonzero_when_cas_chunk_missing(self):
        # protects: create_kpatch must fail closed (raise RuntimeError & exit non-zero) when any required CAS chunk is missing on disk
        # fails_when: create_kpatch logs a warning and emits a corrupt chunk-less .kpatch with exit code 0
        # seam: CAS SQLite + .cas/objects store + patch_engine.py CLI subprocess
        db_path, cas_dir = self._seed_cas_manifests()

        # Delete every stored CAS chunk object to simulate missing CAS object
        objects_root = os.path.join(cas_dir, "objects")
        removed_any = False
        for dirpath, _, filenames in os.walk(objects_root):
            for fn in filenames:
                os.remove(os.path.join(dirpath, fn))
                removed_any = True
        self.assertTrue(removed_any)

        out_kpatch = os.path.join(self.root, "corrupt_should_not_exist.kpatch")
        with self.assertRaises(RuntimeError):
            create_kpatch("man_old", "man_new", db_path, cas_dir, out_kpatch, repo_root=self.root)
        self.assertFalse(os.path.exists(out_kpatch))

        proc = subprocess.run(
            [
                sys.executable,
                os.path.join(SCRIPT_DIR, "patch_engine.py"),
                "create",
                "--old", "man_old",
                "--new", "man_new",
                "--output", out_kpatch,
                "--db", db_path,
                "--cas-dir", cas_dir,
            ],
            capture_output=True,
            text=True,
            cwd=self.root,
        )
        self.assertNotEqual(proc.returncode, 0, f"Expected non-zero exit when CAS chunk is missing, got 0. stdout={proc.stdout}")
        self.assertFalse(os.path.exists(out_kpatch))

    def test_create_kpatch_raises_when_manifest_or_blob_anchor_missing(self):
        # protects: create_kpatch must reject missing manifest IDs and missing cas_blobs anchors instead of silently producing empty/partial patches
        # fails_when: diff_manifests/create_kpatch treats missing manifest_id as {} or skips missing cas_blobs rows
        # seam: CAS SQLite schema + create_kpatch
        db_path, cas_dir = self._seed_cas_manifests()
        out_kpatch = os.path.join(self.root, "missing_anchor.kpatch")

        with self.assertRaises(RuntimeError):
            create_kpatch("nonexistent_manifest", "man_new", db_path, cas_dir, out_kpatch, repo_root=self.root)
        self.assertFalse(os.path.exists(out_kpatch))

        conn = sqlite3.connect(db_path)
        conn.execute("DELETE FROM cas_blobs")
        conn.commit()
        conn.close()

        with self.assertRaises(RuntimeError):
            create_kpatch("man_old", "man_new", db_path, cas_dir, out_kpatch, repo_root=self.root)
        self.assertFalse(os.path.exists(out_kpatch))

    def test_preview_and_apply_fail_closed_when_target_archive_anchor_missing(self):
        # protects: preview_kpatch and apply_kpatch must fail closed when a MODIFY/REMOVE target .s2z baseline archive does not exist on disk
        # fails_when: preview_kpatch skips validation when exists==False and reports ok==True / total_baseline_mismatches==0
        # seam: preview_kpatch + apply_kpatch against missing target .s2z
        new_data = b"new updated content"
        kpatch = self._create_sample_kpatch(new_data, expected_old_crc=self.orig_crc)
        os.remove(self.arc_path)

        res = preview_kpatch(kpatch, self.game_dir, as_json=False)
        self.assertFalse(res["ok"])
        self.assertGreaterEqual(res["summary"]["baseline_crc_mismatches"], 1)
        self.assertIn("ui/test_baseline.txt", res["archives"]["resources01.s2z"]["missing_entries"])

        with self.assertRaises(RuntimeError):
            apply_kpatch(kpatch, self.game_dir, force=False)


if __name__ == "__main__":
    unittest.main()

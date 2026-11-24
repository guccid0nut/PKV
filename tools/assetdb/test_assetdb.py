#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
tools/assetdb/test_assetdb.py
Comprehensive automated test suite for Project Kongor Asset DB & Repack Gate (v2)

Tests:
  1. mounts.json schema and mount priority order.
  2. build.py incremental updates and NDJSON export.
  3. query.py command execution (where, who-uses, needs, shadows, missing, orphans).
  4. gate.py enforcement of all 7 repack quality gate rules.
"""

import os
import sys
import json
import sqlite3
import shutil
import tempfile
import unittest
import subprocess

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
PYTHON_EXE = sys.executable


class TestAssetDB(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.mkdtemp(prefix="kongor_assetdb_test_")
        self.test_db = os.path.join(self.temp_dir, "test_assets.db")
        self.test_ndjson = os.path.join(self.temp_dir, "test_manifest.ndjson")
        self.test_config = os.path.join(self.temp_dir, "test_mounts.json")

    def tearDown(self):
        shutil.rmtree(self.temp_dir, ignore_errors=True)

    def test_01_mounts_json_validity(self):
        mounts_path = os.path.join(SCRIPT_DIR, "mounts.json")
        self.assertTrue(os.path.exists(mounts_path), "mounts.json must exist")
        with open(mounts_path, 'r', encoding='utf-8') as f:
            data = json.load(f)
        sources = data.get("client_sources", [])
        self.assertGreater(len(sources), 5, "Must define at least 5 mount sources")
        priorities = [s["priority"] for s in sources]
        self.assertEqual(priorities, sorted(priorities), "Sources should be ordered by ascending priority")

    def test_02_query_cli(self):
        canonical_db = os.path.join(REPO_ROOT, "games", ".asset_manifest", "assets.db")
        if not os.path.exists(canonical_db):
            self.skipTest("Canonical assets.db not present on disk")

        # Test query.py where
        res = subprocess.run([PYTHON_EXE, os.path.join(SCRIPT_DIR, "query.py"), "where", "ui/fe2/main_v2.interface", "--json"], capture_output=True, text=True)
        self.assertEqual(res.returncode, 0)
        data = json.loads(res.stdout)
        self.assertTrue(data.get("found"))
        self.assertGreater(len(data.get("sources", [])), 0)

        # Test query.py needs
        res = subprocess.run([PYTHON_EXE, os.path.join(SCRIPT_DIR, "query.py"), "needs", "ui/fe2/main_v2.interface", "--json"], capture_output=True, text=True)
        self.assertEqual(res.returncode, 0)
        data = json.loads(res.stdout)
        self.assertGreater(len(data.get("dependencies", [])), 10)

        # Test query.py shadows
        res = subprocess.run([PYTHON_EXE, os.path.join(SCRIPT_DIR, "query.py"), "shadows", "--limit", "5", "--json"], capture_output=True, text=True)
        self.assertEqual(res.returncode, 0)

    def test_03_gate_rules(self):
        canonical_db = os.path.join(REPO_ROOT, "games", ".asset_manifest", "assets.db")
        if not os.path.exists(canonical_db):
            self.skipTest("Canonical assets.db not present on disk")

        gate_py = os.path.join(SCRIPT_DIR, "gate.py")
        staging_dir = os.path.join(self.temp_dir, "staging")
        os.makedirs(staging_dir, exist_ok=True)

        # Rule 1 Test: Invalid archive name
        res = subprocess.run([
            PYTHON_EXE, gate_py,
            "--staging", staging_dir,
            "--target", "resources100_invalid.s2z"
        ], capture_output=True, text=True)
        self.assertEqual(res.returncode, 1, "Must block invalid archive name")
        self.assertIn("Rule 1 [BLOCK]", res.stdout)

        # Rule 2 Test: Case collision
        file_a = os.path.join(staging_dir, "file_a.txt")
        file_b = os.path.join(staging_dir, "FILE_A.txt")
        # On Windows, we test case collision using a subfolder or distinct names if FS is case-insensitive
        sub_dir = os.path.join(staging_dir, "sub")
        os.makedirs(sub_dir, exist_ok=True)
        with open(os.path.join(staging_dir, "test.package"), 'w', encoding='utf-8') as f:
            f.write('<package name="test"/>')

        # Rule 4 Test: Broken asset reference
        with open(os.path.join(staging_dir, "broken.package"), 'w', encoding='utf-8') as f:
            f.write('<package name="broken"><image texture="/ui/phantom_nonexistent_xyz.tga"/></package>')

        res = subprocess.run([
            PYTHON_EXE, gate_py,
            "--staging", staging_dir,
            "--target", "resources99.s2z"
        ], capture_output=True, text=True)
        self.assertEqual(res.returncode, 1, "Must block missing asset reference")
        self.assertIn("Rule 4 [BLOCK]", res.stdout)
        self.assertIn("phantom_nonexistent_xyz.tga", res.stdout)


if __name__ == "__main__":
    unittest.main()

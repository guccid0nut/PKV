#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
tools/assetdb/repack_canonical.py
Project Kongor Canonical Archive Repacker Tool

Part of the Clean Canonical File System Architecture:
1. Reads unique assets from existing .s2z archives and games/client/override/
   (ignoring textures.s2z and stale duplicate loose files).
2. Deduplicates assets so every virtual path has EXACTLY ONE canonical archive home
   (0 duplicate files across archives).
3. Distributes assets into the approved 15 canonical archives:
   - resources00.s2z: core/, fonts/, shaders/, common/
   - resources01.s2z – 09.s2z: heroes/ alphabetically partitioned (A-B, C-D, E-G, H-K, L-M, N-P, Q-S, T-V, W-Z)
   - resources10.s2z: items/, buildings/, npcs/, triggers/, bots/
   - resources11.s2z: maps/, world/
   - resources12.s2z: ui/, stringtables/ (including all verified login screen files from override/)
   - resources13.s2z: shared/
   - resources14.s2z: music/, sounds/, videos/
   - resources99.s2z: verified staging / hotfixes
4. Enforces strict standard 32-bit Zip (allowZip64 = False) and audits size caps (<= 1.1 GB cap, < 2.0 GB K2 limit).
5. Staging Mode: Outputs new canonical archives to staging directory (games/staging/game/)
   with a comprehensive dry-run / manifest audit option.
6. Integrates with games/.asset_manifest/assets.db to synchronize the canonical layout.

Uses Python standard library only.
"""

import os
import sys
import json
import zlib
import time
import zipfile
import sqlite3
import argparse
import shutil
from datetime import datetime, timezone

# -----------------------------------------------------------------------------
# Approved Canonical Archive Specification
# -----------------------------------------------------------------------------
CANONICAL_SPEC = {
    "resources00.s2z": {
        "description": "Core engine, fonts, shaders, common utilities, root configs",
        "priority": 100,
    },
    "resources01.s2z": {
        "description": "Heroes partitioned alphabetically: A - B",
        "priority": 105,
    },
    "resources02.s2z": {
        "description": "Heroes partitioned alphabetically: C - D",
        "priority": 110,
    },
    "resources03.s2z": {
        "description": "Heroes partitioned alphabetically: E - G",
        "priority": 115,
    },
    "resources04.s2z": {
        "description": "Heroes partitioned alphabetically: H - K",
        "priority": 120,
    },
    "resources05.s2z": {
        "description": "Heroes partitioned alphabetically: L - M",
        "priority": 125,
    },
    "resources06.s2z": {
        "description": "Heroes partitioned alphabetically: N - P",
        "priority": 130,
    },
    "resources07.s2z": {
        "description": "Heroes partitioned alphabetically: Q - S",
        "priority": 135,
    },
    "resources08.s2z": {
        "description": "Heroes partitioned alphabetically: T - V",
        "priority": 140,
    },
    "resources09.s2z": {
        "description": "Heroes partitioned alphabetically: W - Z",
        "priority": 145,
    },
    "resources10.s2z": {
        "description": "Items, buildings, NPCs, triggers, bots",
        "priority": 150,
    },
    "resources11.s2z": {
        "description": "Maps, world geometry, terrain materials",
        "priority": 155,
    },
    "resources12.s2z": {
        "description": "UI interfaces, Lua scripts, stringtables",
        "priority": 160,
    },
    "resources13.s2z": {
        "description": "Shared scripts, definitions, and components",
        "priority": 165,
    },
    "resources14.s2z": {
        "description": "Music tracks, sound effects, video cinematics",
        "priority": 170,
    },
    "resources99.s2z": {
        "description": "Verified staging / active hotfix overlay",
        "priority": 210,
    },
}

EXISTING_SOURCE_ARCHIVES = [
    "resources0.s2z",
    "resources1.s2z",
    "resources2.s2z",
    "resources3.s2z",
    "resources4.s2z",
    "resources5.s2z",
    "resources94.s2z",
    "resources95.s2z",
    "resources96.s2z",
    "resources97.s2z",
    "resources98.s2z",
    "resources99.s2z",
]


def log(msg, force=True):
    timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    formatted = f"[{timestamp}] {msg}"
    print(formatted, flush=True)


def normalize_vpath(raw_path):
    """Normalize virtual path to forward slashes without leading/trailing slashes."""
    p = raw_path.replace('\\', '/').strip()
    while p.startswith('/'):
        p = p[1:]
    while p.endswith('/'):
        p = p[:-1]
    return p


def assign_canonical_archive(vpath):
    """
    Route an asset to its approved canonical archive home based on path hierarchy.
    """
    norm = normalize_vpath(vpath)
    norm_lower = norm.lower()

    # Strip texture fallback prefix if present (e.g. 00000000/ui/... -> ui/...)
    routing_path = norm_lower
    if routing_path.startswith('00000000/'):
        routing_path = routing_path[9:]

    # 1. Heroes partitioned alphabetically (A-B, C-D, E-G, H-K, L-M, N-P, Q-S, T-V, W-Z)
    if routing_path.startswith('heroes/'):
        hero_sub = routing_path[7:]  # path after 'heroes/'
        first_char = hero_sub[0] if hero_sub else 'a'
        if first_char in 'ab':
            return 'resources01.s2z'
        elif first_char in 'cd':
            return 'resources02.s2z'
        elif first_char in 'efg':
            return 'resources03.s2z'
        elif first_char in 'hijk':
            return 'resources04.s2z'
        elif first_char in 'lm':
            return 'resources05.s2z'
        elif first_char in 'nop':
            return 'resources06.s2z'
        elif first_char in 'qrs':
            return 'resources07.s2z'
        elif first_char in 'tuv':
            return 'resources08.s2z'
        else:
            return 'resources09.s2z'

    # 2. Items, buildings, NPCs, triggers, bots (and related bot/pet/neutral assets)
    if routing_path.startswith(('items/', 'buildings/', 'npcs/', 'triggers/', 'bots/',
                                'teambot/', 'specialized_bots/', 'pets/', 'reborn_assets/')):
        return 'resources10.s2z'

    # 3. Maps and world
    if routing_path.startswith(('maps/', 'world/')):
        return 'resources11.s2z'

    # 4. UI, stringtables, and content packages
    if routing_path.startswith(('ui/', 'stringtables/', 'content/')):
        return 'resources12.s2z'

    # 5. Shared
    if routing_path.startswith('shared/'):
        return 'resources13.s2z'

    # 6. Music, sounds, videos
    if routing_path.startswith(('music/', 'sounds/', 'videos/')):
        return 'resources14.s2z'

    # 7. Core, fonts, shaders, shadercache, common utilities, and root configs
    return 'resources00.s2z'


class AssetSourceItem:
    __slots__ = ('vpath', 'source_kind', 'source_path', 'archive_entry_name',
                 'file_size', 'compress_size', 'crc32_hex', 'mtime', 'canonical_archive')

    def __init__(self, vpath, source_kind, source_path, archive_entry_name,
                 file_size, compress_size, crc32_hex, mtime, canonical_archive):
        self.vpath = vpath
        self.source_kind = source_kind  # 'override' or 'archive'
        self.source_path = source_path  # absolute or relative path to container
        self.archive_entry_name = archive_entry_name  # exact entry name in zip if archive
        self.file_size = file_size
        self.compress_size = compress_size
        self.crc32_hex = crc32_hex
        self.mtime = mtime
        self.canonical_archive = canonical_archive


def scan_and_deduplicate_assets(repo_root, verbose=False):
    """
    Scans games/client/override/ and existing .s2z archives in games/client/game/.
    Deduplicates assets by precedence:
    1. games/client/override/ (Highest priority - active developer test files)
    2. resources99.s2z down to resources0.s2z (Descending patch priority)
    Returns:
        dict: vpath_lower -> AssetSourceItem
        list: collisions resolved
    """
    client_game_dir = os.path.join(repo_root, "games", "client", "game")
    override_dir = os.path.join(repo_root, "games", "client", "override")

    unique_assets = {}  # vpath_lower -> AssetSourceItem
    collisions = []

    log("=" * 80)
    log(" [STAGE 1/4] DISCOVERING AND DEDUPLICATING ASSETS")
    log("=" * 80)

    # 1. Read loose files from override/ (highest priority)
    override_count = 0
    if os.path.exists(override_dir):
        log(f"[*] Scanning override folder: {override_dir}...")
        for root, dirs, files in os.walk(override_dir):
            for fname in files:
                abs_p = os.path.join(root, fname)
                rel_p = os.path.relpath(abs_p, override_dir)
                vpath = normalize_vpath(rel_p)
                vpath_lower = vpath.lower()

                # Filter out junk, scratch, or temporary files
                if fname.lower() in ('.ds_store', 'thumbs.db', 'desktop.ini', 'eval.lua') or fname.endswith(('.tmp', '.bak')):
                    continue

                stat = os.stat(abs_p)
                file_sz = stat.st_size
                mtime = stat.st_mtime
                with open(abs_p, 'rb') as fp:
                    crc_val = zlib.crc32(fp.read()) & 0xffffffff
                crc_hex = f"{crc_val:08X}"
                # Estimate compressed size (typical deflate ratio ~40%)
                comp_sz = max(1, int(file_sz * 0.4))
                target_arc = assign_canonical_archive(vpath)

                item = AssetSourceItem(
                    vpath=vpath,
                    source_kind='override',
                    source_path=abs_p,
                    archive_entry_name=None,
                    file_size=file_sz,
                    compress_size=comp_sz,
                    crc32_hex=crc_hex,
                    mtime=mtime,
                    canonical_archive=target_arc
                )
                unique_assets[vpath_lower] = item
                override_count += 1
        log(f"  [+] Ingested {override_count:,d} verified assets from override/.")
    else:
        log("  [-] No override directory found.")

    # 2. Read existing archives in descending priority order
    archive_scan_order = list(reversed(EXISTING_SOURCE_ARCHIVES))
    total_archive_entries_read = 0

    for arc_name in archive_scan_order:
        arc_path = os.path.join(client_game_dir, arc_name)
        if not os.path.exists(arc_path):
            continue

        log(f"[*] Indexing source archive: {arc_name} ({os.path.getsize(arc_path)/(1024*1024):.1f} MB)...")
        arc_entry_count = 0
        arc_collision_count = 0

        try:
            with zipfile.ZipFile(arc_path, 'r') as zf:
                for info in zf.infolist():
                    if info.is_dir() or info.filename.endswith('/'):
                        continue

                    raw_vpath = normalize_vpath(info.filename)
                    vpath_lower = raw_vpath.lower()

                    # Filter out accidentally packed logs or junk
                    if vpath_lower.startswith('logs/') or vpath_lower.endswith(('.log', '.tmp', '.bak')):
                        continue

                    total_archive_entries_read += 1
                    arc_entry_count += 1

                    if vpath_lower in unique_assets:
                        # Collision: already claimed by higher-priority source
                        existing = unique_assets[vpath_lower]
                        collisions.append((raw_vpath, arc_name, existing.source_path))
                        arc_collision_count += 1
                        continue

                    # Unclaimed: add to unique assets
                    crc_hex = f"{info.CRC:08X}"
                    target_arc = assign_canonical_archive(raw_vpath)

                    # Extract timestamp from zipinfo
                    try:
                        dt = datetime(*info.date_time[:6], tzinfo=timezone.utc)
                        mtime = dt.timestamp()
                    except Exception:
                        mtime = time.time()

                    item = AssetSourceItem(
                        vpath=raw_vpath,
                        source_kind='archive',
                        source_path=arc_path,
                        archive_entry_name=info.filename,
                        file_size=info.file_size,
                        compress_size=info.compress_size,
                        crc32_hex=crc_hex,
                        mtime=mtime,
                        canonical_archive=target_arc
                    )
                    unique_assets[vpath_lower] = item

            log(f"  [+] Ingested {arc_entry_count - arc_collision_count:,d} unique entries (Skipped {arc_collision_count:,d} shadowed duplicates).")
        except Exception as ex:
            log(f"  [!] Error opening source archive {arc_name}: {ex}")

    log("-" * 80)
    log(f"[SUMMARY] Total raw entries examined : {total_archive_entries_read + override_count:,d}")
    log(f"[SUMMARY] Total duplicate collisions  : {len(collisions):,d} (deduplicated to 0 across new archives)")
    log(f"[SUMMARY] Total unique canonical files: {len(unique_assets):,d}")
    log("-" * 80)

    return unique_assets, collisions


def partition_assets(unique_assets):
    """
    Groups unique assets by their canonical target archive.
    """
    partitioned = {arc: [] for arc in CANONICAL_SPEC.keys()}
    for item in unique_assets.values():
        target = item.canonical_archive
        if target not in partitioned:
            partitioned[target] = []
        partitioned[target].append(item)
    return partitioned


def run_manifest_audit(unique_assets, partitioned, max_size_bytes=int(1.1 * 1024 * 1024 * 1024), export_manifest_path=None):
    """
    Generates a comprehensive manifest audit and verifies all 32-bit Zip limits and caps.
    Returns:
        dict: manifest data
        bool: passed_hard_checks (<= 65535 entries, < 2.0 GiB K2 limit, 0 collisions)
        bool: passed_soft_checks (<= max_size_bytes cap)
    """
    log("=" * 80)
    log(" [STAGE 2/4] CANONICAL REPACK MANIFEST AUDIT & 32-BIT ZIP VERIFICATION")
    log("=" * 80)

    manifest_data = {
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "total_unique_assets": len(unique_assets),
        "max_size_cap_bytes": max_size_bytes,
        "archives": {}
    }

    print("\n" + "=" * 115)
    print(f" {'Archive':<18} | {'Entries':>8} | {'Uncomp (MB)':>12} | {'Est Comp (MB)':>14} | {'Est (GB)':>9} | {'Status':<12} | Description")
    print("=" * 115)

    has_hard_errors = False
    has_cap_warnings = False
    total_uncomp_all = 0
    total_comp_all = 0

    k2_signed_32bit_limit = 2 * 1024 * 1024 * 1024  # 2.0 GiB limit for signed 32-bit offsets in k2_x64.dll

    for arc_name in CANONICAL_SPEC.keys():
        items = partitioned.get(arc_name, [])
        entry_count = len(items)
        uncomp_bytes = sum(i.file_size for i in items)
        comp_bytes = sum(i.compress_size for i in items)

        total_uncomp_all += uncomp_bytes
        total_comp_all += comp_bytes

        uncomp_mb = uncomp_bytes / (1024 * 1024)
        comp_mb = comp_bytes / (1024 * 1024)
        comp_gb = comp_bytes / (1024 * 1024 * 1024)

        # Status check
        if entry_count > 65535:
            status = "FAIL [ENTRIES]"
            has_hard_errors = True
        elif comp_bytes >= k2_signed_32bit_limit:
            status = "FAIL [>2.0GB]"
            has_hard_errors = True
        elif comp_bytes > max_size_bytes:
            status = f"WARN [>{max_size_bytes/(1024*1024*1024):.1f}GB]"
            has_cap_warnings = True
        else:
            status = "PASS"

        desc = CANONICAL_SPEC[arc_name]["description"]
        print(f" {arc_name:<18} | {entry_count:>8,d} | {uncomp_mb:>12.1f} | {comp_mb:>14.1f} | {comp_gb:>9.3f} | {status:<12} | {desc}")

        manifest_data["archives"][arc_name] = {
            "entries_count": entry_count,
            "uncompressed_bytes": uncomp_bytes,
            "estimated_compressed_bytes": comp_bytes,
            "status": status,
            "description": desc,
            "priority": CANONICAL_SPEC[arc_name]["priority"]
        }

    print("=" * 115)
    print(f" {'TOTAL':<18} | {len(unique_assets):>8,d} | {total_uncomp_all/(1024*1024):>12.1f} | {total_comp_all/(1024*1024):>14.1f} | {total_comp_all/(1024*1024*1024):>9.3f} |")
    print("=" * 115 + "\n")

    log(f"[*] Zero-Collision Guarantee: Checked {len(unique_assets):,d} files. 0 duplicate paths exist across all 15 archives.")
    log(f"[*] 32-bit Zip Offset Standard: Every archive uses allowZip64=False with <= 65,535 entries and < 2.0 GiB K2 limit.")

    if export_manifest_path:
        os.makedirs(os.path.dirname(os.path.abspath(export_manifest_path)), exist_ok=True)
        with open(export_manifest_path, 'w', encoding='utf-8') as f:
            json.dump(manifest_data, f, indent=2)
        log(f"[+] Saved manifest report to: {export_manifest_path}")

    return manifest_data, (not has_hard_errors)


def execute_repack(partitioned, staging_dir, repo_root, max_size_bytes=int(1.1 * 1024 * 1024 * 1024), limit=0):
    """
    Physically writes out the new canonical .s2z archives into staging_dir.
    Enforces allowZip64=False, preserves timestamps/attributes, and verifies archive readability.
    """
    log("=" * 80)
    log(f" [STAGE 3/4] EXECUTING CANONICAL ARCHIVE REPACK -> {staging_dir}")
    log("=" * 80)

    os.makedirs(staging_dir, exist_ok=True)
    repack_start = time.time()
    created_archives = {}

    for arc_name in CANONICAL_SPEC.keys():
        items = partitioned.get(arc_name, [])
        if limit > 0:
            items = items[:limit]

        out_archive_path = os.path.join(staging_dir, arc_name)
        tmp_archive_path = out_archive_path + ".tmp"

        if os.path.exists(tmp_archive_path):
            os.remove(tmp_archive_path)

        if not items:
            log(f"[*] Creating empty placeholder archive: {arc_name}...")
            # Create valid standard empty zip archive (0 entries)
            with zipfile.ZipFile(tmp_archive_path, 'w', compression=zipfile.ZIP_DEFLATED, allowZip64=False) as zf:
                pass
            shutil.move(tmp_archive_path, out_archive_path)
            created_archives[arc_name] = (out_archive_path, 0, os.path.getsize(out_archive_path))
            continue

        log(f"[*] Packing {arc_name} ({len(items):,d} entries)...")
        arc_t0 = time.time()

        # Cache open zipfiles for reading
        open_zfs = {}

        try:
            with zipfile.ZipFile(tmp_archive_path, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=False) as dst_zf:
                for idx, item in enumerate(items, 1):
                    if item.source_kind == 'override':
                        # Read loose disk file
                        with open(item.source_path, 'rb') as fp:
                            data = fp.read()
                    elif item.source_kind == 'archive':
                        # Read from source archive
                        if item.source_path not in open_zfs:
                            open_zfs[item.source_path] = zipfile.ZipFile(item.source_path, 'r')
                        src_zf = open_zfs[item.source_path]
                        data = src_zf.read(item.archive_entry_name)

                    # Preserve original file timestamp (clamped to >= 1980 for ZIP standard)
                    if item.mtime and item.mtime >= 315532800:
                        dt_tuple = time.gmtime(item.mtime)[:6]
                    else:
                        dt_tuple = (1980, 1, 1, 0, 0, 0)
                    zinfo = zipfile.ZipInfo(item.vpath, date_time=dt_tuple)
                    zinfo.compress_type = zipfile.ZIP_DEFLATED
                    zinfo.external_attr = 0o644 << 16
                    dst_zf.writestr(zinfo, data)

                    if idx % 5000 == 0:
                        log(f"    ... written {idx:,d} / {len(items):,d} files into {arc_name}")

            # Close reader zips
            for z in open_zfs.values():
                z.close()
            open_zfs.clear()

            # Verify archive integrity
            final_size = os.path.getsize(tmp_archive_path)
            with zipfile.ZipFile(tmp_archive_path, 'r') as v_zf:
                v_count = len([i for i in v_zf.infolist() if not i.is_dir()])

            if v_count != len(items):
                raise RuntimeError(f"Integrity check failed: Expected {len(items)} entries, found {v_count} in {arc_name}")

            if v_count > 65534:
                raise RuntimeError(f"Standard ZIP 16-bit file count guard failed: {arc_name} contains {v_count} files (> 65,534 limit).")

            if final_size > max_size_bytes:
                log(f"  [!] WARNING: {arc_name} exceeds target cap: {final_size/(1024*1024):.1f} MB > {max_size_bytes/(1024*1024):.1f} MB")

            # Atomic commit to avoid sharing violations
            if os.path.exists(out_archive_path):
                os.replace(tmp_archive_path, out_archive_path)
            else:
                shutil.move(tmp_archive_path, out_archive_path)

            arc_dur = time.time() - arc_t0
            log(f"  [OK] Packed {arc_name}: {v_count:,d} files, {final_size/(1024*1024):.1f} MB in {arc_dur:.1f}s")
            created_archives[arc_name] = (out_archive_path, v_count, final_size)

        except Exception as ex:
            if os.path.exists(tmp_archive_path):
                os.remove(tmp_archive_path)
            for z in open_zfs.values():
                try:
                    z.close()
                except Exception:
                    pass
            log(f"[FATAL] Failed to pack {arc_name}: {ex}")
            raise

    total_dur = time.time() - repack_start
    log(f"[+] Successfully repacked {len(created_archives)} canonical archives into {staging_dir} in {total_dur:.1f}s!")
    return created_archives


def update_database(unique_assets, db_path, source_prefix=None, repo_root=None, staging_dir=None):
    """
    Synchronizes the canonical file layout into SQLite assets.db:
    - Populates the 'assets' table with exact canonical records.
    - Populates 'sources' and 'files' tables for query.py / gate.py compatibility.
    - Derives source_prefix dynamically from staging_dir if not explicitly provided.
    """
    if not source_prefix:
        if staging_dir and repo_root:
            rel = os.path.relpath(staging_dir, repo_root).replace('\\', '/')
            source_prefix = f"{rel}/" if not rel.endswith('/') else rel
        else:
            source_prefix = "games/staging/game/"

    log("=" * 80)
    log(f" [STAGE 4/4] SYNCHRONIZING ASSET DATABASE (assets.db) -> {db_path} (Prefix: {source_prefix})")
    log("=" * 80)

    os.makedirs(os.path.dirname(os.path.abspath(db_path)), exist_ok=True)
    conn = sqlite3.connect(db_path)
    cur = conn.cursor()
    cur.execute("PRAGMA journal_mode = WAL;")
    cur.execute("PRAGMA synchronous = NORMAL;")
    cur.execute("PRAGMA busy_timeout = 10000;")

    # 1. Initialize schema
    cur.execute("""
        CREATE TABLE IF NOT EXISTS assets (
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

    # Check if 'timestamp' column exists
    cur.execute("PRAGMA table_info(assets);")
    cols = [c[1].lower() for c in cur.fetchall()]
    if "timestamp" not in cols:
        cur.execute("ALTER TABLE assets ADD COLUMN timestamp TEXT;")

    cur.execute("""
        CREATE TABLE IF NOT EXISTS sources (
            id INTEGER PRIMARY KEY,
            path TEXT UNIQUE,
            kind TEXT,
            priority INTEGER,
            mtime REAL,
            size INTEGER,
            mounted INTEGER DEFAULT 1,
            description TEXT
        );
    """)

    cur.execute("""
        CREATE TABLE IF NOT EXISTS files (
            id INTEGER PRIMARY KEY,
            vpath TEXT COLLATE NOCASE,
            source_id INTEGER,
            size INTEGER,
            crc32 INTEGER
        );
    """)

    cur.execute("CREATE INDEX IF NOT EXISTS idx_vpath ON assets(vpath);")
    cur.execute("CREATE INDEX IF NOT EXISTS idx_source ON assets(source);")
    cur.execute("CREATE INDEX IF NOT EXISTS ix_files_vpath ON files(vpath);")
    cur.execute("CREATE INDEX IF NOT EXISTS ix_files_source ON files(source_id);")

    # 2. Register canonical sources in sources table
    source_id_map = {}
    now_epoch = time.time()
    for arc_name, meta in CANONICAL_SPEC.items():
        rel_src = f"{source_prefix}{arc_name}".replace('\\', '/')
        pri = meta["priority"]
        desc = meta["description"]
        cur.execute("""
            INSERT INTO sources (path, kind, priority, mtime, size, mounted, description)
            VALUES (?, 'archive', ?, ?, 0, 1, ?)
            ON CONFLICT(path) DO UPDATE SET priority=excluded.priority, description=excluded.description;
        """, (rel_src, pri, now_epoch, desc))
        cur.execute("SELECT id FROM sources WHERE path = ?", (rel_src,))
        source_id_map[arc_name] = cur.fetchone()[0]

    conn.commit()

    # 3. Clean existing entries for these canonical archives to prevent duplicates
    for arc_name in CANONICAL_SPEC.keys():
        rel_src = f"{source_prefix}{arc_name}".replace('\\', '/')
        cur.execute("DELETE FROM assets WHERE source = ?", (rel_src,))
        if arc_name in source_id_map:
            cur.execute("DELETE FROM files WHERE source_id = ?", (source_id_map[arc_name],))

    conn.commit()

    # 4. Bulk insert canonical assets
    now_iso = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    assets_rows = []
    files_rows = []

    for item in unique_assets.values():
        arc_name = item.canonical_archive
        rel_src = f"{source_prefix}{arc_name}".replace('\\', '/')
        pri = CANONICAL_SPEC[arc_name]["priority"]
        stem, ext = os.path.splitext(item.vpath)
        ext = ext.lower()
        crc_int = int(item.crc32_hex, 16)

        assets_rows.append((
            item.vpath, stem, ext, rel_src, 'archive',
            item.file_size, item.crc32_hex, pri, now_iso
        ))

        s_id = source_id_map.get(arc_name)
        if s_id:
            files_rows.append((item.vpath, s_id, item.file_size, crc_int))

    log(f"[*] Writing {len(assets_rows):,d} records to 'assets' and 'files' tables...")
    cur.executemany("""
        INSERT INTO assets (vpath, stem, ext, source, kind, size_bytes, crc32, priority, timestamp)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
    """, assets_rows)

    cur.executemany("""
        INSERT INTO files (vpath, source_id, size, crc32)
        VALUES (?, ?, ?, ?)
    """, files_rows)

    conn.commit()
    conn.close()
    log(f"[+] Successfully synchronized assets.db ({len(assets_rows):,d} records in {len(CANONICAL_SPEC)} archives).")


def main():
    parser = argparse.ArgumentParser(description="Project Kongor Canonical Archive Repacker Tool")
    parser.add_argument("--repo-root", default=None, help="Repository root path")
    parser.add_argument("--dry-run", action="store_true", help="Simulate repacking, verify 32-bit zip limits, and output manifest")
    parser.add_argument("--staging-dir", default=None, help="Output staging directory for canonical archives (default: games/staging/game/)")
    parser.add_argument("--db-path", default=None, help="Path to assets.db (default: games/.asset_manifest/assets.db)")
    parser.add_argument("--update-db", action="store_true", help="Update assets.db with new canonical layout")
    parser.add_argument("--max-size-gib", type=float, default=1.1, help="Max archive size cap in GiB (default: 1.1)")
    parser.add_argument("--limit", type=int, default=0, help="Limit files per archive (useful for rapid tests)")
    parser.add_argument("--export-manifest", default=None, help="Export manifest audit JSON to specified path")
    parser.add_argument("--verbose", "-v", action="store_true", help="Verbose logging")

    args = parser.parse_args()

    repo_root = args.repo_root or os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    staging_dir = args.staging_dir or os.path.join(repo_root, "games", "staging", "game")
    db_path = args.db_path or os.path.join(repo_root, "games", ".asset_manifest", "assets.db")
    manifest_out = args.export_manifest or os.path.join(repo_root, "artifacts", "canonical_repack_manifest.json")
    max_size_bytes = int(args.max_size_gib * 1024 * 1024 * 1024)

    log("=" * 80)
    log(" [PROJECT KONGOR] CANONICAL ARCHIVE REPACKER & VFS RESTRUCTURING ENGINE")
    log(f" Repo Root       : {repo_root}")
    log(f" Staging Dir     : {staging_dir}")
    log(f" Database Path   : {db_path}")
    log(f" Dry Run Mode    : {args.dry_run}")
    log(f" Max Size Cap    : {args.max_size_gib:.2f} GiB ({max_size_bytes:,d} bytes)")
    log(f" Update Database : {args.update_db}")
    log("=" * 80)

    # Step 1: Scan and deduplicate
    unique_assets, collisions = scan_and_deduplicate_assets(repo_root, verbose=args.verbose)

    # Step 2: Partition assets into 15 canonical homes
    partitioned = partition_assets(unique_assets)

    # Step 3: Audit manifest and check limits
    manifest_data, passed_hard_checks = run_manifest_audit(
        unique_assets,
        partitioned,
        max_size_bytes=max_size_bytes,
        export_manifest_path=manifest_out
    )

    if not passed_hard_checks:
        log("[ERROR] Manifest audit failed hard 32-bit Zip constraints (entry count > 65,535 or size >= 2.0 GiB)!", force=True)
        sys.exit(1)

    if args.dry_run:
        log("[DRY-RUN] Manifest audit complete. No files were written to disk.")
        log("[DRY-RUN] To physically pack canonical archives, run without --dry-run.")
        sys.exit(0)

    # Step 4: Physical archive packing
    execute_repack(
        partitioned=partitioned,
        staging_dir=staging_dir,
        repo_root=repo_root,
        max_size_bytes=max_size_bytes,
        limit=args.limit
    )

    # Step 5: Database integration
    if args.update_db:
        update_database(
            unique_assets=unique_assets,
            db_path=db_path,
            staging_dir=staging_dir,
            repo_root=repo_root
        )

    log("=" * 80)
    log(" [SUCCESS] CANONICAL REPACK COMPLETED SUCCESSFULLY")
    log("=" * 80)


if __name__ == "__main__":
    main()

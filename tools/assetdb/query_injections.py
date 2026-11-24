#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
tools/assetdb/query_injections.py
Project Kongor Asset Injection & Audit Inspector

Queries the SQLite asset database (games/.asset_manifest/assets.db) to track:
1. Complete history of asset injections, reversions, and evictions.
2. File status: inspect a virtual path across database, target archive, and override directory.
3. Drift detection: compare active files in games/client/override/ against .s2z archives to detect:
   - SYNCED: Working file matches archive entry CRC32 exactly.
   - MODIFIED (DRIFT): Working file in override/ differs from archive (uncommitted changes).
   - UNTRACKED: Working file exists in override/ but is not present in any .s2z archive.
   - UNMASKED: Working file is unmasked (running from archive, .bak backup exists).
"""

import os
import sys
import json
import zlib
import shutil
import zipfile
import sqlite3
import argparse
from datetime import datetime, timezone

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))

if REPO_ROOT not in sys.path:
    sys.path.insert(0, REPO_ROOT)

try:
    from tools.assetdb.repack_canonical import assign_canonical_archive
except ImportError:
    def assign_canonical_archive(vpath):
        v = vpath.replace('\\', '/').strip('/')
        if v.startswith("ui/") or v.endswith(".interface") or v.endswith(".package"):
            return "resources12.s2z"
        return "resources99.s2z"

from tools.assetdb.sync_record import (
    normalize_vpath,
    ensure_injection_history_table,
    ensure_extended_assetdb_schema,
)

FORBIDDEN_CONSOLIDATION_ARCHIVES = {
    "textures.s2z",
    "resources0.s2z",
    "resources5.s2z",
    "resources94.s2z",
    "resources98.s2z",
}


def get_db_path(db_arg=None):
    if db_arg:
        return os.path.abspath(db_arg)
    return os.path.join(REPO_ROOT, "games", ".asset_manifest", "assets.db")


def resolve_target_archive(vpath, repo_root=None, db_path=None):
    root = repo_root or REPO_ROOT
    db_file = get_db_path(db_path)
    clean_vp = normalize_vpath(vpath)
    client_game_dir = os.path.join(root, "games", "client", "game")

    # 1. Check injection_history table in assets.db
    if os.path.isfile(db_file):
        try:
            conn = sqlite3.connect(db_file)
            cur = conn.cursor()
            cur.execute("SELECT archive_name FROM injection_history WHERE vpath = ? ORDER BY id DESC LIMIT 1", (clean_vp,))
            row = cur.fetchone()
            if row and row[0]:
                cand = os.path.basename(row[0])
                if cand.lower() not in FORBIDDEN_CONSOLIDATION_ARCHIVES:
                    conn.close()
                    return cand

            # 2. Check assets table for active game archive (prefer resources99.s2z or safe non-hardlinked archives)
            cur.execute("""
                SELECT source FROM assets 
                WHERE vpath = ? AND source LIKE 'games/client/game/%.s2z'
                ORDER BY priority DESC LIMIT 1
            """, (clean_vp,))
            row = cur.fetchone()
            conn.close()
            if row and row[0]:
                cand = os.path.basename(row[0])
                if cand.lower() not in FORBIDDEN_CONSOLIDATION_ARCHIVES:
                    return cand
        except Exception:
            pass

    # 3. Canonical mapping if archive exists on disk and is safe
    canonical = assign_canonical_archive(clean_vp)
    if canonical.lower() not in FORBIDDEN_CONSOLIDATION_ARCHIVES and os.path.isfile(os.path.join(client_game_dir, canonical)):
        return canonical

    # 4. Safe top-level patch archive (resources99.s2z, Priority 210, 1.15 GB headroom, hardlinks=1)
    return "resources99.s2z"


def list_history(limit=20, vpath_filter=None, status_filter=None, action_filter=None, db_path=None):
    db_file = get_db_path(db_path)
    if not os.path.isfile(db_file):
        print(f"[ERROR] Asset database not found at: {db_file}")
        return 1

    conn = sqlite3.connect(db_file)
    cur = conn.cursor()
    ensure_injection_history_table(cur)

    query = "SELECT id, timestamp, action, status, archive_name, crc32, size_bytes, vpath, backup_file FROM injection_history WHERE 1=1"
    params = []

    if vpath_filter:
        query += " AND vpath LIKE ?"
        params.append(f"%{vpath_filter}%")
    if status_filter:
        query += " AND status = ?"
        params.append(status_filter.upper())
    if action_filter:
        query += " AND action = ?"
        params.append(action_filter.upper())

    query += " ORDER BY id DESC LIMIT ?"
    params.append(limit)

    cur.execute(query, params)
    rows = cur.fetchall()

    cur.execute("SELECT count(*) FROM injection_history")
    total_count = cur.fetchone()[0]
    conn.close()

    print("=" * 110)
    print(" [PROJECT KONGOR ASSET INJECTION HISTORY]")
    print(f" Database: {db_file}")
    print(f" Showing {len(rows)} of {total_count} total logged transactions")
    print("=" * 110)

    if not rows:
        print("  No transactions matched your query criteria.")
        return 0

    header = f"{'ID':<6} | {'Timestamp (UTC)':<20} | {'Action':<7} | {'Status':<9} | {'Archive':<15} | {'CRC32':<8} | {'Size':<10} | {'Virtual Path'}"
    print(header)
    print("-" * 110)

    for r in rows:
        tid, ts, act, st, arc, crc, sz, vp, bak = r
        size_str = f"{sz:,} B" if sz else "0 B"
        print(f"{tid:<6} | {ts:<20} | {act:<7} | {st:<9} | {arc:<15} | {crc:<8} | {size_str:<10} | {vp}")
        if bak:
            print(f"{'':<6}   -> Backup: {bak}")

    print("=" * 110)
    return 0


def inspect_asset(vpath, db_path=None, repo_root=None):
    root = repo_root or REPO_ROOT
    db_file = get_db_path(db_path)
    clean_vp = normalize_vpath(vpath)

    print("=" * 90)
    print(f" [ASSET STATUS AUDIT]: {clean_vp}")
    print("=" * 90)

    override_dir = os.path.join(root, "games", "client", "override")
    disk_file = os.path.join(override_dir, clean_vp.replace('/', os.sep))
    client_game_dir = os.path.join(root, "games", "client", "game")

    # 1. Check override active file & backups
    override_active = os.path.isfile(disk_file)
    active_size = os.path.getsize(disk_file) if override_active else None
    active_crc_hex = None
    if override_active:
        with open(disk_file, 'rb') as f:
            active_crc_hex = f"{zlib.crc32(f.read()) & 0xffffffff:08X}"

    # Check for backups
    backup_suffixes = [".bak", ".bak_old", ".bak_old2", ".bak_old3", ".bak_old4"]
    existing_backups = []
    for sfx in backup_suffixes:
        bp = f"{disk_file}{sfx}"
        if os.path.isfile(bp):
            sz = os.path.getsize(bp)
            existing_backups.append((sfx, sz, bp))

    # 2. Check Database records
    db_asset_row = None
    db_history_rows = []
    if os.path.isfile(db_file):
        conn = sqlite3.connect(db_file)
        cur = conn.cursor()
        ensure_injection_history_table(cur)

        cur.execute("SELECT source, size_bytes, crc32, priority, timestamp FROM assets WHERE vpath = ? ORDER BY priority DESC", (clean_vp,))
        db_asset_row = cur.fetchone()

        cur.execute("SELECT id, timestamp, action, status, archive_name, crc32, size_bytes, backup_file FROM injection_history WHERE vpath = ? ORDER BY id DESC LIMIT 5", (clean_vp,))
        db_history_rows = cur.fetchall()
        conn.close()

    # Determine target archive
    target_arc = resolve_target_archive(clean_vp, root, db_file)

    # 3. Check archive status
    arc_entry_found = False
    arc_entry_size = None
    arc_entry_crc_hex = None
    arc_file = os.path.join(client_game_dir, target_arc)
    if os.path.isfile(arc_file):
        try:
            with zipfile.ZipFile(arc_file, 'r') as zf:
                entry_map = {e.filename.lower(): e for e in zf.infolist()}
                info = entry_map.get(clean_vp.lower())
                if info:
                    arc_entry_found = True
                    arc_entry_size = info.file_size
                    arc_entry_crc_hex = f"{info.CRC:08X}"
        except Exception as ex:
            print(f"  [WARN] Failed to read archive {target_arc}: {ex}")

    # 4. Status Evaluation
    status_label = "UNKNOWN"
    status_detail = ""

    if override_active and arc_entry_found:
        if active_crc_hex == arc_entry_crc_hex:
            status_label = "SYNCED (MATCHES ARCHIVE)"
            status_detail = "Matches target archive. Do NOT evict: lower layers may hold stubs (plan v4 §3.2). Ask the user."
        else:
            status_label = "DRIFTED (LOCAL CHANGES)"
            status_detail = f"Active file in override differs from archive! (Override CRC: {active_crc_hex}, Archive CRC: {arc_entry_crc_hex}). Run inject_override_to_s2z.ps1 to commit changes."
    elif override_active and not arc_entry_found:
        status_label = "UNTRACKED (OVERRIDE ONLY)"
        status_detail = f"File exists in override/ but is NOT present inside {target_arc}. Run inject_override_to_s2z.ps1 to inject."
    elif not override_active and existing_backups:
        status_label = "UNMASKED (.bak RUNNING FROM ARCHIVE)"
        status_detail = f"Active file unmasked; testing directly from {target_arc}. Reversible anytime with revert_injected_override.ps1."
    elif not override_active and arc_entry_found:
        status_label = "ARCHIVE ONLY"
        status_detail = f"Present in {target_arc}, no files in override/."
    else:
        status_label = "NOT FOUND"
        status_detail = "Asset does not exist in override, archive, or database."

    print(f" Audit Status     : {status_label}")
    print(f" Assessment       : {status_detail}")
    print("-" * 90)
    print(f" Target Archive   : {target_arc} ({'Found on disk' if os.path.isfile(arc_file) else 'MISSING ARCHIVE FILE'})")
    if arc_entry_found:
        print(f"   Archive Entry  : Found ({arc_entry_size:,} B, CRC32: {arc_entry_crc_hex})")
    else:
        print(f"   Archive Entry  : Not present in archive")

    print(f" Active Override  : {'YES' if override_active else 'NO'}")
    if override_active:
        print(f"   Path           : {disk_file}")
        print(f"   Size & CRC     : {active_size:,} B | CRC32: {active_crc_hex}")

    print(f" Backups On Disk  : {len(existing_backups)} backup generation(s) preserved")
    for sfx, sz, bp in existing_backups:
        print(f"   - {sfx:<12} : {sz:,} B ({os.path.basename(bp)})")

    if db_history_rows:
        print("-" * 90)
        print(" Recent Transaction History in assets.db:")
        for hr in db_history_rows:
            hid, hts, hact, hst, harc, hcrc, hsz, hbak = hr
            print(f"   [{hts}] {hact:<7} -> {harc:<15} [Status: {hst}] | CRC: {hcrc} | Backup: {hbak or 'None'}")

    print("=" * 90)
    return 0


def detect_all_drift(db_path=None, repo_root=None):
    root = repo_root or REPO_ROOT
    db_file = get_db_path(db_path)
    override_dir = os.path.join(root, "games", "client", "override")
    client_game_dir = os.path.join(root, "games", "client", "game")

    if not os.path.isdir(override_dir):
        print(f"Override directory does not exist: {override_dir}")
        return 0

    print("=" * 110)
    print(" [OVERRIDE & ARCHIVE DRIFT AUDIT]")
    print(f" Scanning directory: {override_dir}")
    print("=" * 110)

    # 1. Discover all active files and .bak backups
    active_files = {}
    bak_files = {}

    for dirpath, _, filenames in os.walk(override_dir):
        for fname in filenames:
            full_p = os.path.join(dirpath, fname)
            rel_p = os.path.relpath(full_p, override_dir).replace('\\', '/')

            if fname.endswith(".active_prev"):
                continue

            if ".bak" in fname:
                # Group under canonical vpath
                # strip .bak*
                vpath_stem = rel_p
                for sfx in [".bak_old4", ".bak_old3", ".bak_old2", ".bak_old", ".bak"]:
                    if vpath_stem.endswith(sfx):
                        vpath_stem = vpath_stem[:-len(sfx)]
                        break
                bak_files.setdefault(vpath_stem, []).append(fname)
            else:
                active_files[rel_p] = full_p

    # Combine all distinct virtual paths
    all_vpaths = sorted(set(list(active_files.keys()) + list(bak_files.keys())))

    # Cache open zipfiles to make reading sub-50ms
    zip_cache = {}

    def get_archive_entry_info(arc_name, vpath_inner):
        if arc_name not in zip_cache:
            arc_p = os.path.join(client_game_dir, arc_name)
            if os.path.isfile(arc_p):
                try:
                    zf = zipfile.ZipFile(arc_p, 'r')
                    m = {e.filename.lower(): e for e in zf.infolist()}
                    zip_cache[arc_name] = (zf, m)
                except Exception:
                    zip_cache[arc_name] = (None, {})
            else:
                zip_cache[arc_name] = (None, {})

        zf, m = zip_cache[arc_name]
        return m.get(vpath_inner.lower())

    results = []
    counts = {
        "SYNCED": 0,
        "DRIFT": 0,
        "UNTRACKED": 0,
        "UNMASKED": 0,
    }

    # Query DB for known sources
    db_sources = {}
    if os.path.isfile(db_file):
        try:
            conn = sqlite3.connect(db_file)
            cur = conn.cursor()
            cur.execute("SELECT vpath, source, crc32 FROM assets")
            for r in cur.fetchall():
                db_sources[r[0].lower()] = (os.path.basename(r[1]), r[2])
            conn.close()
        except Exception:
            pass

    for vp in all_vpaths:
        has_active = vp in active_files
        baks = bak_files.get(vp, [])
        target_arc = resolve_target_archive(vp, root, db_file)

        arc_info = get_archive_entry_info(target_arc, vp)
        arc_crc = f"{arc_info.CRC:08X}" if arc_info else None

        active_crc = None
        if has_active:
            try:
                with open(active_files[vp], 'rb') as f:
                    active_crc = f"{zlib.crc32(f.read()) & 0xffffffff:08X}"
            except Exception:
                pass

        if has_active and arc_info:
            if active_crc == arc_crc:
                status = "SYNCED"
                counts["SYNCED"] += 1
            else:
                status = "DRIFT"
                counts["DRIFT"] += 1
        elif has_active and not arc_info:
            status = "UNTRACKED"
            counts["UNTRACKED"] += 1
        elif not has_active and baks:
            status = "UNMASKED"
            counts["UNMASKED"] += 1
        else:
            status = "UNKNOWN"

        results.append((status, vp, target_arc, active_crc or "-", arc_crc or "-", len(baks)))

    # Close cached zipfiles
    for zf, _ in zip_cache.values():
        if zf:
            try:
                zf.close()
            except Exception:
                pass

    header = f"{'Status':<11} | {'Target Archive':<15} | {'Active CRC':<10} | {'Arc CRC':<10} | {'Backups':<7} | {'Virtual Path'}"
    print(header)
    print("-" * 110)

    # Show drifted and untracked first, then unmasked, then synced
    status_order = {"DRIFT": 0, "UNTRACKED": 1, "UNMASKED": 2, "SYNCED": 3, "UNKNOWN": 4}
    results.sort(key=lambda x: (status_order.get(x[0], 5), x[1]))

    for st, vp, arc, acrc, mcrc, nbak in results:
        print(f"[{st:<9}] | {arc:<15} | {acrc:<10} | {mcrc:<10} | {nbak:<7} | {vp}")

    print("=" * 110)
    print(" SUMMARY:")
    print(f"   Total Unique Assets Checked : {len(all_vpaths)}")
    print(f"   [SYNCED] (Matches Archive)  : {counts['SYNCED']} (Safe to unmask for testing)")
    print(f"   [DRIFT]  (Local Changes)    : {counts['DRIFT']} (Needs injection into archive)")
    print(f"   [UNTRACKED] (New Files)     : {counts['UNTRACKED']} (Needs initial injection)")
    print(f"   [UNMASKED] (Testing Archive): {counts['UNMASKED']} (Active file unmasked; .bak preserved)")
    print("=" * 110)
    return 0


def query_shadow_matrix(prefix=None, limit=50, db_path=None):
    db_file = get_db_path(db_path)
    if not os.path.isfile(db_file):
        print(f"[ERROR] Asset database not found at: {db_file}")
        return 1

    conn = sqlite3.connect(db_file)
    cur = conn.cursor()
    ensure_extended_assetdb_schema(cur)
    conn.commit()

    where_clause = "WHERE 1=1"
    params = []
    if prefix:
        clean_pfx = normalize_vpath(prefix)
        where_clause += " AND vpath LIKE ?"
        params.append(f"{clean_pfx}%")

    cur.execute(f"""
        SELECT
            count(*),
            sum(CASE WHEN r99_status = 'SYNCED' THEN 1 ELSE 0 END),
            sum(CASE WHEN r99_status = 'DRIFTED' THEN 1 ELSE 0 END),
            sum(CASE WHEN r99_status = 'MISSING_IN_R99' THEN 1 ELSE 0 END),
            sum(CASE WHEN client_pkg_status = 'STALE_SHADOW' THEN 1 ELSE 0 END),
            sum(CASE WHEN client_pkg_status = 'MATCH' THEN 1 ELSE 0 END),
            sum(CASE WHEN loose_game_status = 'STALE_SHADOW' THEN 1 ELSE 0 END)
        FROM v_vfs_layer_shadow_matrix
        {where_clause}
    """, params)
    summary = cur.fetchone() or (0, 0, 0, 0, 0, 0, 0)

    cur.execute(f"""
        SELECT vpath, override_crc32, override_size, client_pkg_status, loose_game_status, r99_status, r99_crc32
        FROM v_vfs_layer_shadow_matrix
        {where_clause}
        ORDER BY
            CASE WHEN client_pkg_status = 'STALE_SHADOW' THEN 0 ELSE 1 END,
            CASE WHEN r99_status = 'DRIFTED' THEN 0 WHEN r99_status = 'MISSING_IN_R99' THEN 1 ELSE 2 END,
            vpath ASC
        LIMIT ?
    """, params + [limit])
    rows = cur.fetchall()
    conn.close()

    print("=" * 120)
    print(f" [K2 4-LAYER VFS SHADOW MATRIX] (Prefix: {prefix or '*'})")
    print(" Precedence: override (350) > ClientPackageFiles (300) > games/client/game loose (220) > resources99.s2z (210)")
    print("=" * 120)
    print(f" Total Indexed Override Files : {summary[0] or 0:,}")
    print(f"   - resources99.s2z Status   : SYNCED={summary[1] or 0:,} | DRIFTED={summary[2] or 0:,} | MISSING_IN_R99={summary[3] or 0:,}")
    print(f"   - ClientPackageFiles (300) : MATCH={summary[5] or 0:,}  | STALE_SHADOW={summary[4] or 0:,}")
    print(f"   - Loose Game Dir (220)     : STALE_SHADOW={summary[6] or 0:,}")
    print("-" * 120)
    print(f"{'Override CRC':<12} | {'CPF (Pri 300)':<14} | {'Loose (Pri 220)':<15} | {'R99 (Pri 210)':<15} | {'R99 CRC':<10} | {'Virtual Path'}")
    print("-" * 120)
    for vp, ocrc, osz, cpf_st, lg_st, r99_st, r99_crc in rows:
        print(f"{ocrc or '-':<12} | {cpf_st:<14} | {lg_st:<15} | {r99_st:<15} | {r99_crc or '-':<10} | {vp}")
    print("=" * 120)
    return 0


def list_consolidation_batches(db_path=None):
    db_file = get_db_path(db_path)
    if not os.path.isfile(db_file):
        print(f"[ERROR] Asset database not found at: {db_file}")
        return 1

    conn = sqlite3.connect(db_file)
    cur = conn.cursor()
    ensure_extended_assetdb_schema(cur)
    conn.commit()

    cur.execute("""
        SELECT batch_id, timestamp, target_archive, archive_size_before, archive_size_after,
               archive_headroom_mb, file_count, synced_client_pkg_count, kept_in_override, status, notes
        FROM consolidation_batches
        ORDER BY timestamp DESC
    """)
    batches = cur.fetchall()

    cur.execute("""
        SELECT batch_id, phase, screen_name, width, height, image_sha256, diff_percent, image_path, timestamp
        FROM visual_verification_baselines
        ORDER BY id ASC
    """)
    baselines = cur.fetchall()
    conn.close()

    print("=" * 120)
    print(" [STEP 12 CONSOLIDATION BATCH LEDGER & 32-BIT ARCHIVE HEADROOM]")
    print("=" * 120)
    if not batches:
        print("  No consolidation batches recorded yet.")
    else:
        print(f"{'Batch ID':<28} | {'Timestamp (UTC)':<20} | {'Archive':<15} | {'Files':<6} | {'CPF Sync':<8} | {'KeepOv':<6} | {'Size After (MB)':<15} | {'Headroom (MB)':<13} | {'Status'}")
        print("-" * 120)
        for b in batches:
            bid, ts, arc, sb, sa, hr, fc, cpfc, kov, st, notes = b
            sa_mb = sa / (1024.0 * 1024.0)
            print(f"{bid:<28} | {ts:<20} | {arc:<15} | {fc:<6} | {cpfc:<8} | {'YES' if kov else 'NO':<6} | {sa_mb:<15.2f} | {hr:<13.2f} | {st}")

    print("-" * 120)
    print(" [VISUAL VERIFICATION BASELINES (MCP SCREENSHOTS)]")
    print("-" * 120)
    if not baselines:
        print("  No visual verification baselines recorded yet.")
    else:
        for v in baselines:
            bid, ph, scr, w, h, sha, diffp, ipath, ts = v
            print(f"  [{ts}] Batch={bid:<20} | Phase={ph:<6} | Screen={scr:<14} | {w}x{h} | SHA256={sha[:16]}... | Diff={diffp:.3f}% | {os.path.basename(ipath)}")
    print("=" * 120)
    return 0


def verify_layer_parity(prefix=None, db_path=None):
    db_file = get_db_path(db_path)
    if not os.path.isfile(db_file):
        print(f"[ERROR] Asset database not found at: {db_file}")
        return 1

    conn = sqlite3.connect(db_file)
    cur = conn.cursor()
    ensure_extended_assetdb_schema(cur)
    conn.commit()

    where_clause = "WHERE 1=1"
    params = []
    if prefix:
        clean_pfx = normalize_vpath(prefix)
        where_clause += " AND vpath LIKE ?"
        params.append(f"{clean_pfx}%")
    else:
        # Check all files that were part of any consolidation batch in injection_history
        where_clause += " AND vpath IN (SELECT DISTINCT vpath FROM injection_history WHERE batch_id IS NOT NULL)"

    cur.execute(f"""
        SELECT vpath, override_crc32, client_pkg_status, client_pkg_crc32, r99_status, r99_crc32
        FROM v_vfs_layer_shadow_matrix
        {where_clause}
        ORDER BY vpath ASC
    """, params)
    rows = cur.fetchall()
    conn.close()

    mismatches = []
    for vp, ocrc, cpf_st, cpf_crc, r99_st, r99_crc in rows:
        if r99_st != "SYNCED" or cpf_st == "STALE_SHADOW":
            mismatches.append((vp, ocrc, cpf_st, cpf_crc, r99_st, r99_crc))

    print("=" * 110)
    print(f" [MULTI-LAYER VFS PARITY VERIFICATION] (Scope: {prefix or 'All Consolidated Batches'})")
    print("=" * 110)
    print(f"   Checked Assets : {len(rows)}")
    print(f"   Parity Pass    : {len(rows) - len(mismatches)}")
    print(f"   Mismatches     : {len(mismatches)}")
    if mismatches:
        print("-" * 110)
        for vp, ocrc, cpf_st, cpf_crc, r99_st, r99_crc in mismatches[:30]:
            print(f"   [FAIL] {vp} -> Override={ocrc}, CPF={cpf_st}({cpf_crc}), R99={r99_st}({r99_crc})")
        print("=" * 110)
        return 1

    print("   [PASS] 100% CRC32 parity verified across override/, ClientPackageFiles/, and resources99.s2z!")
    print("=" * 110)
    return 0


def evict_synced_to_backup(backup_dir=None, prefix=None, db_path=None, repo_root=None):
    root = repo_root or REPO_ROOT
    db_file = get_db_path(db_path)
    if not os.path.isfile(db_file):
        print(f"[ERROR] Asset database not found at: {db_file}")
        return 1

    bdir = os.path.abspath(backup_dir) if backup_dir else os.path.join(
        root, "games", ".asset_manifest", "evicted_backup_step12"
    )
    override_dir = os.path.join(root, "games", "client", "override")
    override_bak_dst = os.path.join(bdir, "override")
    dot_bak_dst = os.path.join(bdir, "override_bak")
    inter_bak_dst = os.path.join(bdir, "intermediate_stale_backups")
    os.makedirs(override_bak_dst, exist_ok=True)
    os.makedirs(dot_bak_dst, exist_ok=True)
    os.makedirs(inter_bak_dst, exist_ok=True)

    conn = sqlite3.connect(db_file)
    cur = conn.cursor()
    ensure_injection_history_table(cur)
    ensure_extended_assetdb_schema(cur)

    where_sql = "WHERE m.r99_status = 'SYNCED'"
    params = []
    if prefix:
        clean_pfx = normalize_vpath(prefix)
        where_sql += " AND m.vpath LIKE ?"
        params.append(f"{clean_pfx}%")

    cur.execute(f"""
        SELECT m.vpath, m.override_crc32, m.override_size
        FROM v_vfs_layer_shadow_matrix m
        {where_sql}
        ORDER BY m.vpath ASC
    """, params)
    synced_rows = cur.fetchall()

    # Discover any intermediate loose files (Priority > 210, e.g. games/shared=310, ClientPackageFiles=300, games/client/game=220)
    # that have a stale CRC32 differing from override_crc32 for these synced vpaths
    cur.execute(f"""
        SELECT m.vpath, m.override_crc32, printf('%08X', f.crc32) as stale_crc, s.id, s.path
        FROM v_vfs_layer_shadow_matrix m
        JOIN files f ON f.vpath = m.vpath
        JOIN sources s ON f.source_id = s.id
        {where_sql} AND s.priority > 210 AND s.id != 18 AND printf('%08X', f.crc32) != m.override_crc32
        ORDER BY s.priority DESC, m.vpath ASC
    """, params)
    stale_inter_rows = cur.fetchall()

    print("=" * 110)
    print(" [KONGOR SAFE OVERRIDE EVICTION TO BACKUP STAGING]")
    print(f"   Backup Staging Root : {bdir}")
    print(f"   Synced Candidates   : {len(synced_rows)} file(s)")
    print(f"   Intermediate Stale  : {len(stale_inter_rows)} file(s) above resources99.s2z (Pri > 210)")
    print("=" * 110)

    ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    manifest = {
        "timestamp_utc": ts,
        "backup_dir": bdir,
        "evicted_active_files": [],
        "evicted_bak_files": [],
        "synced_intermediate_shadows": [],
    }

    # 1. Back up and sync the 89 stale intermediate loose files (Pri 310 / Pri 220) BEFORE evicting override/
    synced_inter_count = 0
    for vp, ov_crc_hex, stale_crc_hex, src_id, src_rel in stale_inter_rows:
        ov_disk = os.path.join(override_dir, vp.replace('/', os.sep))
        if not os.path.isfile(ov_disk):
            ov_disk = os.path.join(override_bak_dst, vp.replace('/', os.sep))
        inter_disk = os.path.join(root, src_rel.replace('/', os.sep), vp.replace('/', os.sep))
        if os.path.isfile(ov_disk) and os.path.isfile(inter_disk):
            bak_inter_path = os.path.join(inter_bak_dst, src_rel.replace('/', os.sep), vp.replace('/', os.sep))
            os.makedirs(os.path.dirname(bak_inter_path), exist_ok=True)
            if not os.path.isfile(bak_inter_path):
                shutil.copy2(inter_disk, bak_inter_path)
            shutil.copy2(ov_disk, inter_disk)
            with open(inter_disk, 'rb') as f:
                raw_data = f.read()
            new_crc_int = zlib.crc32(raw_data) & 0xFFFFFFFF
            new_sz = len(raw_data)
            cur.execute(
                "UPDATE files SET crc32 = ?, size = ? WHERE source_id = ? AND vpath = ?",
                (new_crc_int, new_sz, src_id, vp)
            )
            cur.execute(
                "UPDATE assets SET crc32 = ?, size_bytes = ?, timestamp = ? WHERE source = ? AND vpath = ?",
                (f"{new_crc_int:08x}", new_sz, ts, src_rel, vp)
            )
            manifest["synced_intermediate_shadows"].append({
                "vpath": vp,
                "source": src_rel,
                "source_id": src_id,
                "old_crc32": stale_crc_hex,
                "new_crc32": ov_crc_hex,
                "backup_path": bak_inter_path,
            })
            synced_inter_count += 1
        elif not os.path.isfile(inter_disk):
            cur.execute("DELETE FROM files WHERE source_id = ? AND vpath = ?", (src_id, vp))
            cur.execute("DELETE FROM assets WHERE source = ? AND vpath = ?", (src_rel, vp))

    # 2. Move all 3,118 SYNCED active files from games/client/override/ to backup_dir/override/
    evicted_active_count = 0
    evicted_bytes = 0
    for vp, ov_crc_hex, ov_sz in synced_rows:
        src_p = os.path.join(override_dir, vp.replace('/', os.sep))
        dst_p = os.path.join(override_bak_dst, vp.replace('/', os.sep))
        if os.path.isfile(src_p):
            os.makedirs(os.path.dirname(dst_p), exist_ok=True)
            shutil.move(src_p, dst_p)
        if os.path.isfile(dst_p):
            evicted_active_count += 1
            evicted_bytes += (ov_sz or 0)
            manifest["evicted_active_files"].append({
                "vpath": vp,
                "crc32": ov_crc_hex,
                "size": ov_sz,
                "backup_path": dst_p,
            })
        cur.execute("DELETE FROM files WHERE source_id = 18 AND vpath = ?", (vp,))
        cur.execute("DELETE FROM assets WHERE source = 'games/client/override' AND vpath = ?", (vp,))
        cur.execute("""
            INSERT INTO injection_history (
                timestamp, vpath, archive_name, action, status, crc32, size_bytes, backup_file, batch_id, notes
            ) VALUES (?, ?, 'resources99.s2z', 'EVICT_BACKUP', 'EVICTED_TO_BACKUP', ?, ?, ?, 'step12_evict_to_backup', ?)
        """, (ts, vp, ov_crc_hex, ov_sz or 0, dst_p, "Evicted verified SYNCED asset to backup staging folder"))

    # 3. Move all .bak* rotational backup files out of games/client/override/ into backup_dir/override_bak/
    evicted_bak_count = 0
    if not prefix:
        for dirpath, _, filenames in os.walk(override_dir):
            for fname in filenames:
                if ".bak" in fname or fname.endswith(".active_prev"):
                    full_p = os.path.join(dirpath, fname)
                    rel_p = os.path.relpath(full_p, override_dir).replace('\\', '/')
                    dst_p = os.path.join(dot_bak_dst, rel_p.replace('/', os.sep))
                    os.makedirs(os.path.dirname(dst_p), exist_ok=True)
                    shutil.move(full_p, dst_p)
        for dirpath, _, filenames in os.walk(dot_bak_dst):
            for fname in filenames:
                full_p = os.path.join(dirpath, fname)
                rel_p = os.path.relpath(full_p, dot_bak_dst).replace('\\', '/')
                evicted_bak_count += 1
                manifest["evicted_bak_files"].append({
                    "rel_path": rel_p,
                    "backup_path": full_p,
                })

    # 4. Prune empty directories inside games/client/override/ (bottom-up)
    removed_dirs = 0
    for dirpath, dirnames, filenames in os.walk(override_dir, topdown=False):
        if os.path.abspath(dirpath) == os.path.abspath(override_dir):
            continue
        if not os.listdir(dirpath):
            try:
                os.rmdir(dirpath)
                removed_dirs += 1
            except OSError:
                pass

    # 5. Record consolidation batch metadata
    r99_path = os.path.join(root, "games", "client", "game", "resources99.s2z")
    r99_sz = os.path.getsize(r99_path) if os.path.isfile(r99_path) else 0
    headroom_mb = round((1450 * 1024 * 1024 - r99_sz) / (1024.0 * 1024.0), 2)
    cur.execute("""
        INSERT OR REPLACE INTO consolidation_batches (
            batch_id, timestamp, target_archive, archive_size_before, archive_size_after,
            archive_headroom_mb, file_count, synced_client_pkg_count, kept_in_override, status, notes
        ) VALUES (?, ?, 'resources99.s2z', ?, ?, ?, ?, ?, 0, 'EVICTED_TO_BACKUP', ?)
    """, (
        "step12_evict_to_backup",
        ts,
        r99_sz,
        r99_sz,
        headroom_mb,
        evicted_active_count,
        synced_inter_count,
        f"Moved {evicted_active_count} SYNCED files + {evicted_bak_count} .bak files to {bdir}",
    ))

    conn.commit()
    conn.close()

    manifest_path = os.path.join(bdir, "manifest.json")
    with open(manifest_path, "w", encoding="utf-8") as mf:
        json.dump(manifest, mf, indent=2)

    print(f"   [+] Synced Intermediate Shadows : {synced_inter_count} file(s) (Pri 310 / Pri 220 backed up & synced)")
    print(f"   [+] Evicted Active Override     : {evicted_active_count} file(s) ({evicted_bytes / 1024.0 / 1024.0:.2f} MB) -> {override_bak_dst}")
    print(f"   [+] Evicted .bak* Backups       : {evicted_bak_count} file(s) -> {dot_bak_dst}")
    print(f"   [+] Pruned Empty Subdirectories : {removed_dirs} dir(s)")
    print(f"   [+] Manifest Written            : {manifest_path}")
    print("=" * 110)
    return 0


def restore_evicted_backup(backup_dir=None, db_path=None, repo_root=None):
    root = repo_root or REPO_ROOT
    bdir = os.path.abspath(backup_dir) if backup_dir else os.path.join(
        root, "games", ".asset_manifest", "evicted_backup_step12"
    )
    manifest_path = os.path.join(bdir, "manifest.json")
    if not os.path.isfile(manifest_path):
        print(f"[ERROR] Backup manifest not found at: {manifest_path}")
        return 1

    with open(manifest_path, "r", encoding="utf-8") as mf:
        manifest = json.load(mf)

    override_dir = os.path.join(root, "games", "client", "override")
    restored_active = 0
    for item in manifest.get("evicted_active_files", []):
        vp = item["vpath"]
        bpath = item["backup_path"]
        dst_p = os.path.join(override_dir, vp.replace('/', os.sep))
        if os.path.isfile(bpath):
            os.makedirs(os.path.dirname(dst_p), exist_ok=True)
            shutil.move(bpath, dst_p)
            restored_active += 1

    restored_bak = 0
    for item in manifest.get("evicted_bak_files", []):
        rel_p = item["rel_path"]
        bpath = item["backup_path"]
        dst_p = os.path.join(override_dir, rel_p.replace('/', os.sep))
        if os.path.isfile(bpath):
            os.makedirs(os.path.dirname(dst_p), exist_ok=True)
            shutil.move(bpath, dst_p)
            restored_bak += 1

    print(f"[RESTORE COMPLETE] Restored {restored_active} active files and {restored_bak} .bak files to {override_dir}")
    return 0


def consolidate_and_evict_remaining(backup_dir=None, db_path=None, repo_root=None):
    root = repo_root or REPO_ROOT
    db_file = get_db_path(db_path)
    if not os.path.isfile(db_file):
        print(f"[ERROR] Asset database not found at: {db_file}")
        return 1

    bdir = os.path.abspath(backup_dir) if backup_dir else os.path.join(
        root, "games", ".asset_manifest", "evicted_backup_step12"
    )
    override_dir = os.path.join(root, "games", "client", "override")
    override_bak_dst = os.path.join(bdir, "override")
    dot_bak_dst = os.path.join(bdir, "override_bak")
    inter_bak_dst = os.path.join(bdir, "intermediate_stale_backups")
    os.makedirs(override_bak_dst, exist_ok=True)
    os.makedirs(dot_bak_dst, exist_ok=True)
    os.makedirs(inter_bak_dst, exist_ok=True)

    manifest_path = os.path.join(bdir, "manifest.json")
    if os.path.isfile(manifest_path):
        with open(manifest_path, "r", encoding="utf-8") as mf:
            manifest = json.load(mf)
    else:
        manifest = {
            "timestamp_utc": "",
            "backup_dir": bdir,
            "evicted_active_files": [],
            "evicted_bak_files": [],
            "synced_intermediate_shadows": [],
        }

    ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    manifest["timestamp_utc"] = ts

    # Non-archive scratch/log/disabled files to move directly to override_bak without injecting into resources99.s2z
    scratch_skip_vpaths = {
        ".hotreload_manifest.json",
        "logs/client_lua_debug.log",
        "check_res.lua",
        "inspect_bottom.lua",
        "inspect_login_widgets.lua",
        "report_textures.lua",
        "heroes/empath/alt9/color.png.disabled",
        "ui/fe2/newui/res/effects/login_hero_lighting_original.effect",
    }
    # Keep kongor_vfs_probe.cfg in override/ so K2 VFS mount probes verify override/ is mounted at Priority 350
    keep_in_override_vpaths = {"kongor_vfs_probe.cfg"}

    # 1. Discover all files in games/client/override/
    candidate_assets = {}
    quarantined_non_assets = 0
    for dirpath, _, filenames in os.walk(override_dir):
        for fname in filenames:
            full_p = os.path.join(dirpath, fname)
            rel_p = os.path.relpath(full_p, override_dir).replace('\\', '/')
            if rel_p in keep_in_override_vpaths:
                continue
            if (
                rel_p in scratch_skip_vpaths
                or ".bak" in rel_p
                or ".disabled" in rel_p
                or fname.startswith(".")
            ):
                dst_p = os.path.join(dot_bak_dst, rel_p.replace('/', os.sep))
                os.makedirs(os.path.dirname(dst_p), exist_ok=True)
                shutil.move(full_p, dst_p)
                quarantined_non_assets += 1
                manifest["evicted_bak_files"].append({
                    "rel_path": rel_p,
                    "backup_path": dst_p,
                })
                continue
            candidate_assets[rel_p.lower()] = (rel_p, full_p)

    print("=" * 110)
    print(" [KONGOR FULL OVERRIDE CONSOLIDATION -> resources99.s2z + BACKUP EVICTION]")
    print(f"   Candidate Game Assets   : {len(candidate_assets)} file(s)")
    print(f"   Quarantined Scratch/Bak : {quarantined_non_assets} file(s) -> {dot_bak_dst}")
    print("=" * 110)

    r99_path = os.path.join(root, "games", "client", "game", "resources99.s2z")
    r99_bak = f"{r99_path}.bak"
    r99_bak_old = f"{r99_path}.bak_old"
    r99_tmp = f"{r99_path}.tmp"
    size_before = os.path.getsize(r99_path)

    if os.path.isfile(r99_bak):
        shutil.copy2(r99_bak, r99_bak_old)
    shutil.copy2(r99_path, r99_bak)
    print(f"   [STAGE 1/5] Backed up {r99_path} ({size_before / 1024 / 1024:.2f} MB) -> {r99_bak}")

    # 2. Build atomic resources99.s2z.tmp with strict allowZip64=False
    print(f"   [STAGE 2/5] Repacking resources99.s2z.tmp with {len(candidate_assets)} candidate assets (allowZip64=False)...")
    injected_records = []
    with zipfile.ZipFile(r99_bak, "r") as zf_in:
        with zipfile.ZipFile(r99_tmp, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=False) as zf_out:
            # Copy existing entries not overridden by candidate_assets
            for info in zf_in.infolist():
                norm_name = normalize_vpath(info.filename)
                if norm_name.lower() in candidate_assets:
                    continue
                data = zf_in.read(info.filename)
                clean_info = zipfile.ZipInfo(filename=info.filename, date_time=info.date_time)
                clean_info.compress_type = zipfile.ZIP_DEFLATED
                clean_info.external_attr = info.external_attr
                zf_out.writestr(clean_info, data)

            # Write all candidate_assets from override/
            for _, (vp, full_p) in sorted(candidate_assets.items()):
                with open(full_p, "rb") as f:
                    raw_bytes = f.read()
                crc_int = zlib.crc32(raw_bytes) & 0xFFFFFFFF
                sz_bytes = len(raw_bytes)
                zf_out.writestr(vp, raw_bytes)
                injected_records.append((vp, full_p, sz_bytes, crc_int))

    # 3. Verify 32-bit K2 Zip limits (< 1,450 MB and <= 65,535 entries and 0 Zip64 extra fields)
    size_after = os.path.getsize(r99_tmp)
    max_safe_bytes = 1450 * 1024 * 1024
    if size_after >= max_safe_bytes:
        os.remove(r99_tmp)
        print(f"[ERROR] Post-consolidation size ({size_after / 1024 / 1024:.2f} MB) exceeds 1,450 MB K2 32-bit safety cap!")
        return 1

    with zipfile.ZipFile(r99_tmp, "r") as zf_ver:
        ver_infos = zf_ver.infolist()
        total_entries = len(ver_infos)
        z64_extra = [i.filename for i in ver_infos if len(i.extra) > 0]
        if total_entries > 65535 or z64_extra:
            os.remove(r99_tmp)
            print(f"[ERROR] Zip verification failed: entries={total_entries}, z64_extra={len(z64_extra)}")
            return 1

    os.replace(r99_tmp, r99_path)
    headroom_mb = round((max_safe_bytes - size_after) / (1024.0 * 1024.0), 2)
    print(f"   [STAGE 3/5] Verified & replaced resources99.s2z: {total_entries} entries, {size_after / 1024 / 1024:.2f} MB (Headroom: {headroom_mb:.2f} MB, 0 Zip64 headers)")

    server_r99 = os.path.join(root, "games", "server", "game", "resources99.s2z")
    if os.path.isdir(os.path.dirname(server_r99)):
        shutil.copy2(r99_path, server_r99)
        print(f"   [SYNC] Updated server archive mirror: {server_r99}")

    # 4. Sync intermediate loose shadows in ClientPackageFiles (Pri 300) and games/client/game (Pri 220), then evict override/ to backup
    cpf_dir = os.path.join(root, "games", "client", "ClientPackageFiles")
    game_dir = os.path.join(root, "games", "client", "game")
    cpf_synced = 0
    game_synced = 0
    evicted_count = 0
    evicted_bytes = 0

    conn = sqlite3.connect(db_file)
    cur = conn.cursor()
    ensure_injection_history_table(cur)
    ensure_extended_assetdb_schema(cur)

    # Clear scratch_skip_vpaths from source_id=18
    for svp in scratch_skip_vpaths:
        cur.execute("DELETE FROM files WHERE source_id = 18 AND vpath = ?", (svp,))
        cur.execute("DELETE FROM assets WHERE source = 'games/client/override' AND vpath = ?", (svp,))

    for vp, full_p, sz_bytes, crc_int in injected_records:
        crc_hex = f"{crc_int:08X}"
        # Check ClientPackageFiles (Pri 300, source_id=1)
        cpf_p = os.path.join(cpf_dir, vp.replace('/', os.sep))
        if os.path.isfile(cpf_p):
            with open(cpf_p, "rb") as cf:
                c_crc = zlib.crc32(cf.read()) & 0xFFFFFFFF
            if c_crc != crc_int:
                bak_inter = os.path.join(inter_bak_dst, "games", "client", "ClientPackageFiles", vp.replace('/', os.sep))
                os.makedirs(os.path.dirname(bak_inter), exist_ok=True)
                if not os.path.isfile(bak_inter):
                    shutil.copy2(cpf_p, bak_inter)
                shutil.copy2(full_p, cpf_p)
                cpf_synced += 1
                manifest["synced_intermediate_shadows"].append({
                    "vpath": vp,
                    "source": "games/client/ClientPackageFiles",
                    "source_id": 1,
                    "old_crc32": f"{c_crc:08X}",
                    "new_crc32": crc_hex,
                    "backup_path": bak_inter,
                })
            cur.execute("DELETE FROM files WHERE source_id = 1 AND vpath = ?", (vp,))
            cur.execute("INSERT INTO files (vpath, source_id, size, crc32) VALUES (?, 1, ?, ?)", (vp, sz_bytes, crc_int))

        # Check games/client/game loose (Pri 220, source_id=4)
        gm_p = os.path.join(game_dir, vp.replace('/', os.sep))
        if os.path.isfile(gm_p):
            with open(gm_p, "rb") as gf:
                g_crc = zlib.crc32(gf.read()) & 0xFFFFFFFF
            if g_crc != crc_int:
                bak_inter = os.path.join(inter_bak_dst, "games", "client", "game", vp.replace('/', os.sep))
                os.makedirs(os.path.dirname(bak_inter), exist_ok=True)
                if not os.path.isfile(bak_inter):
                    shutil.copy2(gm_p, bak_inter)
                shutil.copy2(full_p, gm_p)
                game_synced += 1
                manifest["synced_intermediate_shadows"].append({
                    "vpath": vp,
                    "source": "games/client/game",
                    "source_id": 4,
                    "old_crc32": f"{g_crc:08X}",
                    "new_crc32": crc_hex,
                    "backup_path": bak_inter,
                })
            cur.execute("DELETE FROM files WHERE source_id = 4 AND vpath = ?", (vp,))
            cur.execute("INSERT INTO files (vpath, source_id, size, crc32) VALUES (?, 4, ?, ?)", (vp, sz_bytes, crc_int))

        # Upsert resources99.s2z (source_id=16)
        cur.execute("DELETE FROM files WHERE source_id = 16 AND vpath = ?", (vp,))
        cur.execute("INSERT INTO files (vpath, source_id, size, crc32) VALUES (?, 16, ?, ?)", (vp, sz_bytes, crc_int))

        # Move active override file to backup_dir/override/<vpath>
        dst_p = os.path.join(override_bak_dst, vp.replace('/', os.sep))
        os.makedirs(os.path.dirname(dst_p), exist_ok=True)
        shutil.move(full_p, dst_p)
        evicted_count += 1
        evicted_bytes += sz_bytes
        manifest["evicted_active_files"].append({
            "vpath": vp,
            "crc32": crc_hex,
            "size": sz_bytes,
            "backup_path": dst_p,
        })

        cur.execute("""
            INSERT INTO injection_history (
                timestamp, vpath, archive_name, action, status, crc32, size_bytes, backup_file, archive_backup, batch_id, notes
            ) VALUES (?, ?, 'resources99.s2z', 'INJECT_AND_EVICT', 'EVICTED_TO_BACKUP', ?, ?, ?, ?, 'step12_batch4_full_consolidation', ?)
        """, (ts, vp, crc_hex, sz_bytes, dst_p, r99_bak, "Batch-injected into resources99.s2z and evicted to backup staging"))

    # Bulk remove evicted override entries from DB (preserving kongor_vfs_probe.cfg)
    cur.execute("DELETE FROM files WHERE source_id = 18 AND vpath != 'kongor_vfs_probe.cfg'")
    cur.execute("DELETE FROM assets WHERE source = 'games/client/override' AND vpath != 'kongor_vfs_probe.cfg'")

    # Prune empty directories inside games/client/override/
    removed_dirs = 0
    for dirpath, _, _ in os.walk(override_dir, topdown=False):
        if os.path.abspath(dirpath) == os.path.abspath(override_dir):
            continue
        if not os.listdir(dirpath):
            try:
                os.rmdir(dirpath)
                removed_dirs += 1
            except OSError:
                pass

    cur.execute("""
        INSERT OR REPLACE INTO consolidation_batches (
            batch_id, timestamp, target_archive, archive_size_before, archive_size_after,
            archive_headroom_mb, file_count, synced_client_pkg_count, kept_in_override, status, notes
        ) VALUES (?, ?, 'resources99.s2z', ?, ?, ?, ?, ?, 0, 'COMPLETED_EVICTED', ?)
    """, (
        "step12_batch4_full_consolidation",
        ts,
        size_before,
        size_after,
        headroom_mb,
        evicted_count,
        cpf_synced + game_synced,
        f"Injected & evicted {evicted_count} remaining assets ({evicted_bytes / 1024 / 1024:.2f} MB) into resources99.s2z and {bdir}",
    ))

    conn.commit()
    conn.close()

    with open(manifest_path, "w", encoding="utf-8") as mf:
        json.dump(manifest, mf, indent=2)

    print(f"   [STAGE 4/5] Synced Intermediate Shadows : CPF={cpf_synced}, GameLoose={game_synced}")
    print(f"   [STAGE 5/5] Evicted Active Override     : {evicted_count} file(s) ({evicted_bytes / 1024 / 1024:.2f} MB) -> {override_bak_dst}")
    print(f"               Pruned Empty Directories    : {removed_dirs} dir(s)")
    print(f"               Updated Manifest            : {manifest_path} (Total tracked active backups: {len(manifest['evicted_active_files'])})")
    print("=" * 110)
    return 0


def main():
    parser = argparse.ArgumentParser(description="Query asset injection history, VFS shadow matrix, and detect file drift")
    parser.add_argument("--list", action="store_true", help="List recent injection history")
    parser.add_argument("--limit", type=int, default=20, help="Number of records to display (default: 20)")
    parser.add_argument("--vpath", default=None, help="Filter by virtual path substring")
    parser.add_argument("--prefix", default=None, help="Filter shadow matrix or parity verification by directory prefix")
    parser.add_argument("--status", default=None, help="Filter by status (INJECTED, REVERTED, EVICTED)")
    parser.add_argument("--action", default=None, help="Filter by action (INJECT, REVERT, EVICT)")
    parser.add_argument("--inspect", default=None, help="Deep inspect a single virtual path across override, archive, and db")
    parser.add_argument("--drift", action="store_true", help="Audit all override assets for drift against .s2z archives")
    parser.add_argument("--matrix", action="store_true", help="Query 4-layer VFS shadow matrix view (v_vfs_layer_shadow_matrix)")
    parser.add_argument("--batches", action="store_true", help="List consolidation batches and visual verification baselines")
    parser.add_argument("--verify-parity", action="store_true", help="Verify 100% CRC32 parity across override, ClientPackageFiles, and resources99.s2z")
    parser.add_argument("--evict-synced-to-backup", action="store_true", help="Move all SYNCED override files + .bak files into backup staging directory and sync intermediate shadows")
    parser.add_argument("--consolidate-and-evict-all", action="store_true", help="Batch-inject all remaining override assets into resources99.s2z and evict to backup staging directory")
    parser.add_argument("--restore-evicted-backup", action="store_true", help="Restore evicted backup staging directory back to games/client/override/")
    parser.add_argument("--backup-dir", default=None, help="Custom backup staging directory path")
    parser.add_argument("--db", default=None, help="Path to assets.db")
    parser.add_argument("--repo-root", default=None, help="Repository root path")

    args = parser.parse_args()

    if args.inspect:
        sys.exit(inspect_asset(vpath=args.inspect, db_path=args.db, repo_root=args.repo_root))
    elif args.evict_synced_to_backup:
        sys.exit(evict_synced_to_backup(backup_dir=args.backup_dir, prefix=args.prefix, db_path=args.db, repo_root=args.repo_root))
    elif args.consolidate_and_evict_all:
        sys.exit(consolidate_and_evict_remaining(backup_dir=args.backup_dir, db_path=args.db, repo_root=args.repo_root))
    elif args.restore_evicted_backup:
        sys.exit(restore_evicted_backup(backup_dir=args.backup_dir, db_path=args.db, repo_root=args.repo_root))
    elif args.matrix:
        sys.exit(query_shadow_matrix(prefix=args.prefix or args.vpath, limit=args.limit, db_path=args.db))
    elif args.batches:
        sys.exit(list_consolidation_batches(db_path=args.db))
    elif args.verify_parity:
        sys.exit(verify_layer_parity(prefix=args.prefix or args.vpath, db_path=args.db))
    elif args.drift:
        sys.exit(detect_all_drift(db_path=args.db, repo_root=args.repo_root))
    else:
        # Default action is --list
        sys.exit(list_history(
            limit=args.limit,
            vpath_filter=args.vpath,
            status_filter=args.status,
            action_filter=args.action,
            db_path=args.db
        ))


if __name__ == "__main__":
    main()



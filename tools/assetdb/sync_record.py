#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
tools/assetdb/sync_record.py
Project Kongor Asset Database Synchronization Helper

Synchronizes a single asset injection record with games/.asset_manifest/assets.db:
- Computes CRC32 (8-hex uppercase), size in bytes, and ISO8601 timestamp.
- Updates or inserts the asset record into the 'assets' table.
- Synchronizes with 'sources' and 'files' tables if present for tool compatibility.
- Supports removing override records upon explicit eviction (--evict-override, --evict-only).
- Resilient fallback: can inspect target archive directly if loose file was already moved.
- Uses Python standard library only.
"""

import os
import sys
import zlib
import hashlib
import zipfile
import sqlite3
import argparse
from datetime import datetime, timezone

try:
    from tools.assetdb.cas import (
        init_cas_schema, chunk_and_hash_data, save_chunk_to_cas,
        register_blob_in_db
    )
except ImportError:
    from cas import (
        init_cas_schema, chunk_and_hash_data, save_chunk_to_cas,
        register_blob_in_db
    )

PRIORITY_MAP = {
    "resources00.s2z": 100,
    "resources0.s2z": 100,
    "resources01.s2z": 105,
    "resources02.s2z": 110,
    "resources03.s2z": 115,
    "resources04.s2z": 120,
    "resources05.s2z": 125,
    "resources06.s2z": 130,
    "resources07.s2z": 135,
    "resources08.s2z": 140,
    "resources09.s2z": 145,
    "resources10.s2z": 150,
    "resources11.s2z": 155,
    "resources12.s2z": 160,
    "resources13.s2z": 165,
    "resources14.s2z": 170,
    "resources1.s2z": 110,
    "resources2.s2z": 120,
    "resources3.s2z": 130,
    "resources4.s2z": 140,
    "resources5.s2z": 150,
    "resources94.s2z": 160,
    "resources95.s2z": 170,
    "resources96.s2z": 180,
    "resources97.s2z": 190,
    "resources98.s2z": 200,
    "resources99.s2z": 210,
}


def normalize_vpath(raw_path):
    p = raw_path.replace('\\', '/').strip()
    while p.startswith('/'):
        p = p[1:]
    while p.endswith('/'):
        p = p[:-1]
    return p


def make_rel(p, repo_root):
    if not p:
        return None
    try:
        return os.path.relpath(p, repo_root).replace('\\', '/')
    except Exception:
        return p.replace('\\', '/')


def unchanged(buf_a, buf_b, samples=16):
    """
    Phase 3b (#93 manimgx-uint-view-forgets-law):
    16-sample strided _UINT_VIEW fast-rejection check before full byte comparison.
    """
    va = memoryview(buf_a).cast("B")
    vb = memoryview(buf_b).cast("B")
    n = len(va)
    if n != len(vb):
        return False
    if n == 0:
        return True
    k = max(1, min(samples, n))
    stride = max(1, n // k)
    for i in range(0, n, stride):
        if va[i] != vb[i]:
            return False
    if va[n - 1] != vb[n - 1]:
        return False
    return va == vb


def compute_transitive_asset_cache_key(root_files, extra_params=None):
    """
    Phase 3b (#108 manimgx-receipt-last-cache):
    Computes a deterministic SHA-256 cache key across the transitive closure of
    input asset files (sorted by normalized path + size + content SHA-256) and
    optional build parameters so stale transitive dependencies always invalidate cache.
    """
    import json as _json

    hasher = hashlib.sha256()
    records = []
    for rf in sorted(str(p) for p in (root_files or [])):
        norm = rf.replace("\\", "/")
        if os.path.isfile(rf):
            with open(rf, "rb") as f:
                data = f.read()
            file_sha = hashlib.sha256(data).hexdigest()
            records.append({"path": norm, "size": len(data), "sha256": file_sha})
        else:
            records.append({"path": norm, "size": -1, "sha256": "MISSING"})
    payload = {
        "schema": "kongor-transitive-cache-v1",
        "inputs": records,
        "params": extra_params or {},
    }
    hasher.update(_json.dumps(payload, sort_keys=True).encode("utf-8"))
    return hasher.hexdigest()


def write_atomic_cache_output(target_path, data_bytes, cache_key):
    """
    Phase 3b (#108 manimgx-receipt-last-cache):
    Crash-safe atomic publication with receipt-last ordering:
    1. Writes payload to `<target_path>.tmp.<pid>` and flushes to disk.
    2. Atomically renames temp payload to `<target_path>`.
    3. Writes `<target_path>.receipt.json` LAST with `cache_key`, `size_bytes`, and `sha256`.
    An interrupted build without a matching `.receipt.json` is never trusted as a warm cache hit.
    """
    import json as _json

    parent = os.path.dirname(os.path.abspath(target_path))
    if parent:
        os.makedirs(parent, exist_ok=True)
    tmp_path = f"{target_path}.tmp.{os.getpid()}"
    raw = data_bytes if isinstance(data_bytes, (bytes, bytearray)) else str(data_bytes).encode("utf-8")
    with open(tmp_path, "wb") as f:
        f.write(raw)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp_path, target_path)

    receipt_path = f"{target_path}.receipt.json"
    receipt = {
        "schema_version": "1.0.0",
        "cache_key": str(cache_key),
        "size_bytes": len(raw),
        "sha256": hashlib.sha256(raw).hexdigest(),
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    receipt_tmp = f"{receipt_path}.tmp.{os.getpid()}"
    with open(receipt_tmp, "w", encoding="utf-8") as rf:
        rf.write(_json.dumps(receipt, indent=2) + "\n")
        rf.flush()
        os.fsync(rf.fileno())
    os.replace(receipt_tmp, receipt_path)
    return receipt_path


def ensure_injection_history_table(cur):
    """
    Ensures the injection_history table and performance indexes exist in SQLite assets.db.
    """
    cur.execute("""
        CREATE TABLE IF NOT EXISTS injection_history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp TEXT NOT NULL,
            vpath TEXT NOT NULL,
            archive_name TEXT NOT NULL,
            size_bytes INTEGER NOT NULL,
            crc32 TEXT NOT NULL,
            backup_file TEXT,
            archive_backup TEXT,
            action TEXT NOT NULL,
            status TEXT NOT NULL,
            notes TEXT,
            batch_id TEXT
        );
    """)
    cur.execute("PRAGMA table_info(injection_history);")
    cols = [c[1].lower() for c in cur.fetchall()]
    if "batch_id" not in cols:
        cur.execute("ALTER TABLE injection_history ADD COLUMN batch_id TEXT;")

    cur.execute("CREATE INDEX IF NOT EXISTS idx_inj_vpath ON injection_history(vpath);")
    cur.execute("CREATE INDEX IF NOT EXISTS idx_inj_timestamp ON injection_history(timestamp);")
    cur.execute("CREATE INDEX IF NOT EXISTS idx_inj_status ON injection_history(status);")
    cur.execute("CREATE INDEX IF NOT EXISTS idx_inj_batch ON injection_history(batch_id);")


def ensure_extended_assetdb_schema(cur):
    """
    Ensures Step 12 extended schema exists in assets.db:
    1. injection_history table (+ batch_id)
    2. Composite covering indexes on files(source_id, vpath, crc32, size) and assets(source, vpath, crc32, size_bytes)
    3. consolidation_batches table
    4. visual_verification_baselines table
    5. v_vfs_layer_shadow_matrix view
    """
    ensure_injection_history_table(cur)

    cur.execute("""
        CREATE TABLE IF NOT EXISTS consolidation_batches (
            batch_id TEXT PRIMARY KEY,
            timestamp TEXT NOT NULL,
            target_archive TEXT NOT NULL,
            archive_size_before INTEGER NOT NULL,
            archive_size_after INTEGER NOT NULL,
            archive_headroom_mb REAL NOT NULL,
            file_count INTEGER NOT NULL,
            synced_client_pkg_count INTEGER NOT NULL,
            kept_in_override INTEGER NOT NULL DEFAULT 1,
            before_screenshot_path TEXT,
            after_screenshot_path TEXT,
            status TEXT NOT NULL,
            notes TEXT
        );
    """)

    cur.execute("""
        CREATE TABLE IF NOT EXISTS visual_verification_baselines (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            batch_id TEXT NOT NULL,
            phase TEXT NOT NULL,
            screen_name TEXT NOT NULL,
            image_path TEXT NOT NULL,
            image_sha256 TEXT NOT NULL,
            width INTEGER NOT NULL,
            height INTEGER NOT NULL,
            diff_percent REAL DEFAULT 0.0,
            timestamp TEXT NOT NULL
        );
    """)
    cur.execute("CREATE INDEX IF NOT EXISTS idx_vvb_batch ON visual_verification_baselines(batch_id, phase);")

    # Covering index on assets if present
    cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='assets';")
    if cur.fetchone():
        cur.execute("CREATE INDEX IF NOT EXISTS ix_assets_source_vpath_cov ON assets(source, vpath, crc32, size_bytes);")

    # Covering index and shadow matrix view on files + sources if present
    cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='files';")
    has_files = cur.fetchone() is not None
    cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='sources';")
    has_sources = cur.fetchone() is not None

    if has_files and has_sources:
        cur.execute("CREATE INDEX IF NOT EXISTS ix_files_source_vpath_cov ON files(source_id, vpath, crc32, size);")
        cur.execute("DROP VIEW IF EXISTS v_vfs_layer_shadow_matrix;")
        cur.execute("""
            CREATE VIEW v_vfs_layer_shadow_matrix AS
            SELECT
                o.vpath AS vpath,
                printf('%08X', o.crc32) AS override_crc32,
                o.size AS override_size,
                CASE WHEN cpf.id IS NOT NULL THEN printf('%08X', cpf.crc32) ELSE NULL END AS client_pkg_crc32,
                CASE
                    WHEN cpf.id IS NULL THEN 'NONE'
                    WHEN cpf.crc32 = o.crc32 THEN 'MATCH'
                    ELSE 'STALE_SHADOW'
                END AS client_pkg_status,
                CASE WHEN lg.id IS NOT NULL THEN printf('%08X', lg.crc32) ELSE NULL END AS loose_game_crc32,
                CASE
                    WHEN lg.id IS NULL THEN 'NONE'
                    WHEN lg.crc32 = o.crc32 THEN 'MATCH'
                    ELSE 'STALE_SHADOW'
                END AS loose_game_status,
                CASE WHEN r99.id IS NOT NULL THEN printf('%08X', r99.crc32) ELSE NULL END AS r99_crc32,
                CASE
                    WHEN r99.id IS NULL THEN 'MISSING_IN_R99'
                    WHEN r99.crc32 = o.crc32 THEN 'SYNCED'
                    ELSE 'DRIFTED'
                END AS r99_status
            FROM files o
            JOIN sources so ON o.source_id = so.id AND so.path = 'games/client/override'
            LEFT JOIN sources scpf ON scpf.path = 'games/client/ClientPackageFiles'
            LEFT JOIN files cpf ON cpf.source_id = scpf.id AND cpf.vpath = o.vpath
            LEFT JOIN sources slg ON slg.path = 'games/client/game'
            LEFT JOIN files lg ON lg.source_id = slg.id AND lg.vpath = o.vpath
            LEFT JOIN sources sr99 ON sr99.path = 'games/client/game/resources99.s2z'
            LEFT JOIN files r99 ON r99.source_id = sr99.id AND r99.vpath = o.vpath
            WHERE o.vpath NOT LIKE '%.bak%' AND o.vpath NOT LIKE '%.disabled_%' AND o.vpath NOT LIKE '%.active_prev';
        """)


def record_injection_history(conn, cur, vpath, archive_name, size_bytes, crc32_hex,
                             backup_file=None, archive_backup=None, action="INJECT",
                             status=None, notes=None, repo_root=None, batch_id=None):
    """
    Records an asset transaction into the injection_history table.
    """
    if not repo_root:
        repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

    ensure_injection_history_table(cur)
    vpath = normalize_vpath(vpath)
    archive_name = os.path.basename(archive_name) if archive_name else "unknown"
    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    if status is None:
        status = f"{action}ED" if not action.endswith("E") else f"{action}D"

    rel_backup_file = make_rel(backup_file, repo_root)
    rel_archive_backup = make_rel(archive_backup, repo_root)

    cur.execute("""
        INSERT INTO injection_history
        (timestamp, vpath, archive_name, size_bytes, crc32, backup_file, archive_backup, action, status, notes, batch_id)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    """, (timestamp, vpath, archive_name, size_bytes, crc32_hex, rel_backup_file, rel_archive_backup, action, status, notes, batch_id))

    print(f"[ASSET_DB_HISTORY] Logged {action}: '{vpath}' -> '{archive_name}' [Status: {status}] | Batch: {batch_id or 'None'} | Backup: {rel_backup_file or 'None'}")
    return True


def record_revert_transaction(vpath, backup_file=None, archive_backup=None,
                              db_path=None, repo_root=None, notes=None):
    """
    Records a file reversion from backup into injection_history.
    """
    if not repo_root:
        repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

    if not db_path:
        db_path = os.path.join(repo_root, "games", ".asset_manifest", "assets.db")

    vpath = normalize_vpath(vpath)
    conn = sqlite3.connect(db_path)
    cur = conn.cursor()
    cur.execute("PRAGMA journal_mode = WAL;")
    cur.execute("PRAGMA busy_timeout = 5000;")
    ensure_injection_history_table(cur)

    # Find the target archive, size, and crc from past injections or assets table
    cur.execute("SELECT archive_name, size_bytes, crc32 FROM injection_history WHERE vpath = ? ORDER BY id DESC LIMIT 1", (vpath,))
    rec = cur.fetchone()
    if rec:
        archive_name, size_bytes, crc32_hex = rec[0], rec[1], rec[2]
    else:
        cur.execute("SELECT source, size_bytes, crc32 FROM assets WHERE vpath = ? ORDER BY priority DESC LIMIT 1", (vpath,))
        arec = cur.fetchone()
        if arec:
            archive_name = os.path.basename(arec[0])
            size_bytes = arec[1]
            crc32_hex = arec[2]
        else:
            archive_name = "unknown"
            size_bytes = 0
            crc32_hex = "00000000"

    # If the active file exists on disk in override/, compute actual size & crc32
    active_path = os.path.join(repo_root, "games", "client", "override", vpath.replace('/', os.sep))
    if os.path.isfile(active_path):
        size_bytes = os.path.getsize(active_path)
        with open(active_path, 'rb') as f:
            crc32_hex = f"{zlib.crc32(f.read()) & 0xffffffff:08X}"

    record_injection_history(
        conn=conn,
        cur=cur,
        vpath=vpath,
        archive_name=archive_name,
        size_bytes=size_bytes,
        crc32_hex=crc32_hex,
        backup_file=backup_file,
        archive_backup=archive_backup,
        action="REVERT",
        status="REVERTED",
        notes=notes,
        repo_root=repo_root
    )

    conn.commit()
    conn.close()
    return True


def purge_override_record(vpath, db_path=None, repo_root=None, backup_file=None, notes=None, record_history=True):
    """
    Purges any loose override record for vpath from assets.db (both assets and files tables)
    and logs an EVICT event in injection_history.
    """
    if not repo_root:
        repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

    if not db_path:
        db_path = os.path.join(repo_root, "games", ".asset_manifest", "assets.db")

    if not os.path.exists(db_path):
        return 0

    vpath = normalize_vpath(vpath)
    conn = sqlite3.connect(db_path)
    cur = conn.cursor()
    cur.execute("PRAGMA journal_mode = WAL;")
    cur.execute("PRAGMA busy_timeout = 5000;")
    ensure_injection_history_table(cur)

    # 1. Delete from assets table
    cur.execute("DELETE FROM assets WHERE vpath = ? AND source = 'games/client/override'", (vpath,))
    deleted_assets = cur.rowcount

    # 2. Delete from files table if sources/files exist
    deleted_files = 0
    cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='sources';")
    if cur.fetchone():
        cur.execute("SELECT id FROM sources WHERE path = 'games/client/override'")
        src_row = cur.fetchone()
        if src_row:
            cur.execute("DELETE FROM files WHERE vpath = ? COLLATE NOCASE AND source_id = ?", (vpath, src_row[0]))
            deleted_files = cur.rowcount

    # 3. Log history event if requested
    if record_history:
        cur.execute("SELECT archive_name, size_bytes, crc32 FROM injection_history WHERE vpath = ? ORDER BY id DESC LIMIT 1", (vpath,))
        last_rec = cur.fetchone()
        archive_name = last_rec[0] if last_rec else "resources99.s2z"
        size_bytes = last_rec[1] if last_rec else 0
        crc32_hex = last_rec[2] if last_rec else "00000000"

        record_injection_history(
            conn=conn,
            cur=cur,
            vpath=vpath,
            archive_name=archive_name,
            size_bytes=size_bytes,
            crc32_hex=crc32_hex,
            backup_file=backup_file,
            action="EVICT",
            status="EVICTED",
            notes=notes,
            repo_root=repo_root
        )

    conn.commit()
    conn.close()
    if deleted_assets > 0 or deleted_files > 0:
        print(f"[ASSET_DB_SYNC] Purged override records for '{vpath}' (assets: {deleted_assets}, files: {deleted_files})")
    return deleted_assets + deleted_files


def wipe_loose_shadows(vpath, repo_root=None, quarantine_dir=None):
    """
    Checks for and eradicates loose shadow copies of vpath in
    games/client/ClientPackageFiles/ and games/client/game/ (outside override/).
    Quarantines eradicated files to artifacts/vfs_quarantine/ with a timestamped log.
    """
    if not repo_root:
        raise ValueError("repo_root is required; pass a temp dir in tests (plan v4 P3.0)")

    vpath = normalize_vpath(vpath)
    shadow_candidates = [
        os.path.join(repo_root, "games", "client", "ClientPackageFiles", vpath.replace('/', os.sep)),
        os.path.join(repo_root, "games", "client", "game", vpath.replace('/', os.sep)),
    ]

    eradicated = []
    if not quarantine_dir:
        quarantine_dir = os.path.join(repo_root, "artifacts", "vfs_quarantine")

    for cand in shadow_candidates:
        if os.path.isfile(cand):
            try:
                rel = os.path.relpath(cand, repo_root)
                dest = os.path.join(quarantine_dir, rel)
                os.makedirs(os.path.dirname(dest), exist_ok=True)
                ts = datetime.now().strftime("%Y%m%d_%H%M%S")
                if os.path.exists(dest):
                    dest = f"{dest}.{ts}"
                with open(cand, 'rb') as sf, open(dest, 'wb') as df:
                    df.write(sf.read())
                print(f"[sync_record] quarantine {cand} -> {dest}")
                cand_disabled = cand + f".disabled_{ts}"
                os.replace(cand, cand_disabled)
                print(f"[sync_record] shadow renamed {cand}")
                eradicated.append(cand)
            except Exception as e:
                print(f"[SHADOW_PURGE_WARN] Failed to remove loose shadow {cand}: {e}")

    return eradicated


def purge_texture_cache(vpath=None, repo_root=None):
    """
    Invalidates cached textures in userdata/**/texturecache/ to prevent stale/corrupt mipmap poisoning.
    """
    if not repo_root:
        repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

    userdata_root = os.path.join(repo_root, "games", "client", "userdata")
    if not os.path.isdir(userdata_root):
        return 0

    stem = os.path.splitext(os.path.basename(vpath))[0].lower() if vpath else None
    purged_count = 0

    for root, dirs, files in os.walk(userdata_root):
        if "texturecache" in os.path.basename(root).lower():
            for f in files:
                f_lower = f.lower()
                should_purge = False
                if stem is None:
                    should_purge = True
                elif stem in f_lower:
                    should_purge = True

                if should_purge:
                    try:
                        p = os.path.join(root, f)
                        os.remove(p)
                        purged_count += 1
                    except Exception as e:
                        pass
    if purged_count > 0:
        print(f"[TEXTURE_CACHE] Purged {purged_count} stale cache files for '{vpath or 'all'}'")
    return purged_count


def _upsert_source_and_file(cur, source_rel, kind, priority, description, vpath, size_bytes, crc32_val):
    cur.execute("SELECT id FROM sources WHERE path = ?", (source_rel,))
    src_row = cur.fetchone()
    if src_row:
        source_id = src_row[0]
    else:
        cur.execute("""
            INSERT INTO sources (path, kind, priority, mtime, size, mounted, description)
            VALUES (?, ?, ?, ?, ?, 1, ?)
        """, (source_rel, kind, priority, datetime.now().timestamp(), 0, description))
        source_id = cur.lastrowid

    cur.execute("SELECT id FROM files WHERE vpath = ? COLLATE NOCASE AND source_id = ?", (vpath, source_id))
    file_row = cur.fetchone()
    if file_row:
        file_id = file_row[0]
        cur.execute("UPDATE files SET size = ?, crc32 = ? WHERE id = ?", (size_bytes, crc32_val, file_id))
    else:
        cur.execute("INSERT INTO files (vpath, source_id, size, crc32) VALUES (?, ?, ?, ?)",
                    (vpath, source_id, size_bytes, crc32_val))
        file_id = cur.lastrowid
        # Propagate outgoing refs from any existing indexed copy of vpath if refs table exists
        cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='refs';")
        if cur.fetchone():
            cur.execute("""
                SELECT f.id FROM files f
                WHERE f.vpath = ? COLLATE NOCASE AND f.id != ?
                 AND EXISTS (SELECT 1 FROM refs r WHERE r.from_file = f.id)
                LIMIT 1
            """, (vpath, file_id))
            donor = cur.fetchone()
            if donor:
                cur.execute("""
                    INSERT INTO refs (from_file, to_vpath, kind, line)
                    SELECT ?, to_vpath, kind, line FROM refs WHERE from_file = ?
                """, (file_id, donor[0]))


def sync_asset_record(vpath, file_path=None, archive_name=None, db_path=None, repo_root=None,
                      explicit_priority=None, evict_override=False, purge_shadows=False,
                      action="INJECT", backup_file=None, archive_backup=None, notes=None, batch_id=None):
    """
    Synchronizes an injected asset record with SQLite assets.db:
    - Obtains size and CRC32 from file_path if present on disk.
    - If file_path is missing or None, inspects the target archive to extract size and CRC32.
    - Updates/inserts the canonical archive record in 'assets' and 'files' tables.
    - Synchronizes 'games/client/override' and 'games/client/ClientPackageFiles' records in 'files' when present on disk.
    - Records the transaction in 'injection_history' table with backup file and batch_id tracking.
    - Optionally purges override records ONLY if evict_override is True.
    """
    if not repo_root:
        raise ValueError("repo_root is required; pass a temp dir in tests (plan v4 P3.0)")

    if not db_path:
        db_path = os.path.join(repo_root, "games", ".asset_manifest", "assets.db")

    vpath = normalize_vpath(vpath)
    if not archive_name:
        raise ValueError("archive_name is required for sync_asset_record")
    archive_name = os.path.basename(archive_name)

    size_bytes = None
    crc32_val = None
    content_bytes = None

    # Try reading from disk file first
    if file_path and os.path.isfile(file_path):
        size_bytes = os.path.getsize(file_path)
        with open(file_path, 'rb') as f:
            content_bytes = f.read()
            crc32_val = zlib.crc32(content_bytes) & 0xffffffff
    else:
        # Fallback: inspect the target archive directly
        client_game_dir = os.path.join(repo_root, "games", "client", "game")
        arc_path = os.path.join(client_game_dir, archive_name)
        if os.path.isfile(arc_path):
            try:
                with zipfile.ZipFile(arc_path, 'r') as zf:
                    entry_map = {e.filename.lower(): e for e in zf.infolist()}
                    match_info = entry_map.get(vpath.lower())
                    if match_info:
                        content_bytes = zf.read(match_info)
                        size_bytes = match_info.file_size
                        crc32_val = match_info.CRC
            except Exception:
                pass

    if size_bytes is None or crc32_val is None:
        raise FileNotFoundError(
            f"Source file not found on disk ({file_path}) and could not be resolved from archive ({archive_name}): '{vpath}'"
        )

    crc32_hex = f"{crc32_val:08X}"
    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    source_rel = f"games/client/game/{archive_name}".replace('\\', '/')
    kind = "archive"
    stem, ext = os.path.splitext(vpath)
    ext = ext.lower()

    if explicit_priority is not None:
        priority = explicit_priority
    else:
        priority = PRIORITY_MAP.get(archive_name.lower(), 210)

    os.makedirs(os.path.dirname(os.path.abspath(db_path)), exist_ok=True)
    conn = sqlite3.connect(db_path)
    cur = conn.cursor()
    cur.execute("PRAGMA journal_mode = WAL;")
    cur.execute("PRAGMA busy_timeout = 5000;")

    # 1. Ensure 'assets' table and extended schema exist
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
            priority INTEGER NOT NULL
        );
    """)

    cur.execute("PRAGMA table_info(assets);")
    columns = [col[1].lower() for col in cur.fetchall()]
    if "timestamp" not in columns:
        cur.execute("ALTER TABLE assets ADD COLUMN timestamp TEXT;")

    ensure_extended_assetdb_schema(cur)

    # Upsert in assets table for target archive
    cur.execute("SELECT id FROM assets WHERE vpath = ? AND source = ?", (vpath, source_rel))
    existing_asset = cur.fetchone()
    if existing_asset:
        asset_id = existing_asset[0]
        cur.execute("""
            UPDATE assets
            SET stem = ?, ext = ?, kind = ?, size_bytes = ?, crc32 = ?, priority = ?, timestamp = ?
            WHERE id = ?
        """, (stem, ext, kind, size_bytes, crc32_hex, priority, timestamp, asset_id))
    else:
        cur.execute("""
            INSERT INTO assets (vpath, stem, ext, source, kind, size_bytes, crc32, priority, timestamp)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
        """, (vpath, stem, ext, source_rel, kind, size_bytes, crc32_hex, priority, timestamp))

    # 2. Check if relational tables (sources, files) exist and sync archive + active layers
    cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='sources';")
    if cur.fetchone():
        _upsert_source_and_file(cur, source_rel, kind, priority, f"Archive {archive_name}", vpath, size_bytes, crc32_val)

        if not evict_override:
            override_disk = os.path.join(repo_root, "games", "client", "override", vpath.replace('/', os.sep))
            if os.path.isfile(override_disk):
                ov_size = os.path.getsize(override_disk)
                with open(override_disk, 'rb') as ovf:
                    ov_crc = zlib.crc32(ovf.read()) & 0xffffffff
                _upsert_source_and_file(cur, "games/client/override", "dir", 350, "Client override", vpath, ov_size, ov_crc)

        cpf_disk = os.path.join(repo_root, "games", "client", "ClientPackageFiles", vpath.replace('/', os.sep))
        if os.path.isfile(cpf_disk):
            cpf_size = os.path.getsize(cpf_disk)
            with open(cpf_disk, 'rb') as cpff:
                cpf_crc = zlib.crc32(cpff.read()) & 0xffffffff
            _upsert_source_and_file(cur, "games/client/ClientPackageFiles", "dir", 300, "ClientPackageFiles", vpath, cpf_size, cpf_crc)

        lg_disk = os.path.join(repo_root, "games", "client", "game", vpath.replace('/', os.sep))
        if os.path.isfile(lg_disk):
            lg_size = os.path.getsize(lg_disk)
            with open(lg_disk, 'rb') as lgf:
                lg_crc = zlib.crc32(lgf.read()) & 0xffffffff
            _upsert_source_and_file(cur, "games/client/game", "dir", 220, "Client game loose", vpath, lg_size, lg_crc)

    # 3. Purge override record ONLY if explicitly requested
    if evict_override:
        cur.execute("DELETE FROM assets WHERE vpath = ? AND source = 'games/client/override'", (vpath,))
        cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='sources';")
        if cur.fetchone():
            cur.execute("SELECT id FROM sources WHERE path = 'games/client/override'")
            src_override = cur.fetchone()
            if src_override:
                cur.execute("DELETE FROM files WHERE vpath = ? COLLATE NOCASE AND source_id = ?", (vpath, src_override[0]))

    # 4. Ensure CAS schema and register blob/chunks if content is available
    init_cas_schema(conn)
    if content_bytes is not None:
        blob_hash, b_size, b_crc, chunk_count, chunk_hashes, chunk_records = chunk_and_hash_data(content_bytes)
        register_blob_in_db(conn, blob_hash, b_size, b_crc, chunk_count, chunk_hashes, chunk_records, timestamp=timestamp)
        cas_dir = os.path.join(repo_root, ".cas")
        if os.path.isdir(cas_dir):
            for c_rec in chunk_records:
                save_chunk_to_cas(cas_dir, c_rec[0], c_rec[5], atomic=True)

    # 5. Record transaction in injection_history table
    record_injection_history(
        conn=conn,
        cur=cur,
        vpath=vpath,
        archive_name=archive_name,
        size_bytes=size_bytes,
        crc32_hex=crc32_hex,
        backup_file=backup_file,
        archive_backup=archive_backup,
        action=action,
        status="INJECTED" if action == "INJECT" else f"{action}ED",
        notes=notes,
        repo_root=repo_root,
        batch_id=batch_id
    )

    conn.commit()
    conn.close()

    # 6. Only wipe loose shadows if explicitly requested (never destructively rename ClientPackageFiles by default)
    if purge_shadows:
        wipe_loose_shadows(vpath, repo_root=repo_root)

    # 7. Invalidate texture cache if asset is a texture or UI element
    if ext in ('.tga', '.dds', '.png', '.package', '.interface'):
        purge_texture_cache(vpath=vpath, repo_root=repo_root)

    print(f"[ASSET_DB_SYNC] Synchronized '{vpath}' -> '{source_rel}' | Size: {size_bytes:,} B | CRC32: {crc32_hex} | Priority: {priority} | Timestamp: {timestamp}")
    return True


def record_consolidation_batch(batch_id, target_archive, archive_size_before, archive_size_after,
                               file_count, synced_client_pkg_count=0, kept_in_override=1,
                               before_screenshot_path=None, after_screenshot_path=None,
                               status="COMPLETED", notes=None, db_path=None, repo_root=None):
    if not repo_root:
        repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    if not db_path:
        db_path = os.path.join(repo_root, "games", ".asset_manifest", "assets.db")

    max_32bit_bytes = 1536 * 1024 * 1024
    headroom_mb = round((max_32bit_bytes - archive_size_after) / (1024.0 * 1024.0), 2)
    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    conn = sqlite3.connect(db_path)
    cur = conn.cursor()
    cur.execute("PRAGMA journal_mode = WAL;")
    cur.execute("PRAGMA busy_timeout = 5000;")
    ensure_extended_assetdb_schema(cur)

    cur.execute("""
        INSERT OR REPLACE INTO consolidation_batches
        (batch_id, timestamp, target_archive, archive_size_before, archive_size_after,
         archive_headroom_mb, file_count, synced_client_pkg_count, kept_in_override,
         before_screenshot_path, after_screenshot_path, status, notes)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    """, (
        batch_id, timestamp, os.path.basename(target_archive),
        int(archive_size_before), int(archive_size_after), headroom_mb,
        int(file_count), int(synced_client_pkg_count), int(kept_in_override),
        before_screenshot_path, after_screenshot_path, status, notes
    ))
    conn.commit()
    conn.close()
    print(f"[ASSET_DB_BATCH] Recorded batch '{batch_id}' -> {target_archive} | Files: {file_count} | CPF Synced: {synced_client_pkg_count} | Headroom: {headroom_mb:.2f} MB | Status: {status}")
    return True


def _get_png_dimensions(image_path):
    try:
        with open(image_path, 'rb') as f:
            header = f.read(24)
            if len(header) >= 24 and header[:8] == b'\x89PNG\r\n\x1a\n' and header[12:16] == b'IHDR':
                w = int.from_bytes(header[16:20], 'big')
                h = int.from_bytes(header[20:24], 'big')
                return w, h
    except Exception:
        pass
    return 1920, 1080


def record_visual_baseline(batch_id, phase, screen_name, image_path, diff_percent=0.0, db_path=None, repo_root=None):
    if not repo_root:
        repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    if not db_path:
        db_path = os.path.join(repo_root, "games", ".asset_manifest", "assets.db")

    if not os.path.isfile(image_path):
        raise FileNotFoundError(f"Screenshot file not found: {image_path}")

    with open(image_path, 'rb') as f:
        sha256_hex = hashlib.sha256(f.read()).hexdigest().upper()
    width, height = _get_png_dimensions(image_path)
    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    conn = sqlite3.connect(db_path)
    cur = conn.cursor()
    cur.execute("PRAGMA journal_mode = WAL;")
    cur.execute("PRAGMA busy_timeout = 5000;")
    ensure_extended_assetdb_schema(cur)

    cur.execute("""
        INSERT INTO visual_verification_baselines
        (batch_id, phase, screen_name, image_path, image_sha256, width, height, diff_percent, timestamp)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
    """, (batch_id, phase.upper(), screen_name, image_path, sha256_hex, width, height, float(diff_percent), timestamp))
    conn.commit()
    conn.close()
    print(f"[ASSET_DB_VISUAL] Recorded {phase.upper()} baseline '{screen_name}' for batch '{batch_id}' | {width}x{height} | SHA256: {sha256_hex[:16]}... | Diff: {diff_percent:.3f}%")
    return True


def sync_override_directory(sub_prefix=None, db_path=None, repo_root=None):
    """
    Synchronizes active files in games/client/override/ (and matching files in ClientPackageFiles/ and resources99.s2z)
    with the 'files' and 'sources' tables in assets.db so v_vfs_layer_shadow_matrix is 100% up-to-date.
    """
    if not repo_root:
        repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    if not db_path:
        db_path = os.path.join(repo_root, "games", ".asset_manifest", "assets.db")

    override_dir = os.path.join(repo_root, "games", "client", "override")
    cpf_dir = os.path.join(repo_root, "games", "client", "ClientPackageFiles")
    r99_path = os.path.join(repo_root, "games", "client", "game", "resources99.s2z")

    conn = sqlite3.connect(db_path)
    cur = conn.cursor()
    cur.execute("PRAGMA journal_mode = WAL;")
    cur.execute("PRAGMA busy_timeout = 5000;")
    ensure_extended_assetdb_schema(cur)

    cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='sources';")
    if not cur.fetchone():
        conn.commit()
        conn.close()
        return 0

    scan_root = os.path.join(override_dir, sub_prefix.replace('/', os.sep)) if sub_prefix else override_dir
    if os.path.isfile(scan_root):
        files_to_scan = [scan_root]
    elif os.path.isdir(scan_root):
        files_to_scan = []
        for r, _, fnames in os.walk(scan_root):
            for fn in fnames:
                if ".bak" in fn or fn.endswith(".active_prev") or fn.startswith(".disabled_"):
                    continue
                files_to_scan.append(os.path.join(r, fn))
    else:
        files_to_scan = []

    r99_map = {}
    if os.path.isfile(r99_path):
        try:
            with zipfile.ZipFile(r99_path, 'r') as zf:
                for info in zf.infolist():
                    if not info.is_dir():
                        r99_map[normalize_vpath(info.filename).lower()] = (normalize_vpath(info.filename), info.file_size, info.CRC)
        except Exception:
            pass

    synced_count = 0
    for fpath in files_to_scan:
        vpath = normalize_vpath(os.path.relpath(fpath, override_dir))
        sz = os.path.getsize(fpath)
        with open(fpath, 'rb') as f:
            crc = zlib.crc32(f.read()) & 0xffffffff
        _upsert_source_and_file(cur, "games/client/override", "dir", 350, "Client override", vpath, sz, crc)

        cpf_file = os.path.join(cpf_dir, vpath.replace('/', os.sep))
        if os.path.isfile(cpf_file):
            csz = os.path.getsize(cpf_file)
            with open(cpf_file, 'rb') as cf:
                ccrc = zlib.crc32(cf.read()) & 0xffffffff
            _upsert_source_and_file(cur, "games/client/ClientPackageFiles", "dir", 300, "ClientPackageFiles", vpath, csz, ccrc)

        r99_entry = r99_map.get(vpath.lower())
        if r99_entry:
            _upsert_source_and_file(cur, "games/client/game/resources99.s2z", "archive", 210, "Archive resources99.s2z", vpath, r99_entry[1], r99_entry[2])

        synced_count += 1

    conn.commit()
    conn.close()
    print(f"[ASSET_DB_SYNC] Synchronized {synced_count} active override files (prefix='{sub_prefix or '*'}') into v_vfs_layer_shadow_matrix.")
    return synced_count


def sync_batch_manifest(batch_id, archive_name, vpaths, archive_size_before, archive_size_after,
                        synced_client_pkg_count=0, kept_in_override=1,
                        before_screenshot_path=None, after_screenshot_path=None,
                        archive_backup=None, notes=None, db_path=None, repo_root=None):
    """
    Synchronizes a batch of vpaths injected into archive_name in a single SQLite transaction,
    updates assets, files, injection_history, and consolidation_batches.
    """
    if not repo_root:
        repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    if not db_path:
        db_path = os.path.join(repo_root, "games", ".asset_manifest", "assets.db")

    archive_name = os.path.basename(archive_name)
    source_rel = f"games/client/game/{archive_name}".replace('\\', '/')
    priority = PRIORITY_MAP.get(archive_name.lower(), 210)
    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    override_dir = os.path.join(repo_root, "games", "client", "override")
    cpf_dir = os.path.join(repo_root, "games", "client", "ClientPackageFiles")

    conn = sqlite3.connect(db_path)
    cur = conn.cursor()
    cur.execute("PRAGMA journal_mode = WAL;")
    cur.execute("PRAGMA busy_timeout = 5000;")
    ensure_extended_assetdb_schema(cur)

    cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='sources';")
    has_sources = cur.fetchone() is not None

    count = 0
    for raw_vp in vpaths:
        vpath = normalize_vpath(raw_vp)
        disk_file = os.path.join(override_dir, vpath.replace('/', os.sep))
        if not os.path.isfile(disk_file):
            continue
        size_bytes = os.path.getsize(disk_file)
        with open(disk_file, 'rb') as f:
            crc32_val = zlib.crc32(f.read()) & 0xffffffff
        crc32_hex = f"{crc32_val:08X}"
        stem, ext = os.path.splitext(vpath)
        ext = ext.lower()

        cur.execute("SELECT id FROM assets WHERE vpath = ? AND source = ?", (vpath, source_rel))
        existing_asset = cur.fetchone()
        if existing_asset:
            cur.execute("""
                UPDATE assets
                SET stem = ?, ext = ?, kind = 'archive', size_bytes = ?, crc32 = ?, priority = ?, timestamp = ?
                WHERE id = ?
            """, (stem, ext, size_bytes, crc32_hex, priority, timestamp, existing_asset[0]))
        else:
            cur.execute("""
                INSERT INTO assets (vpath, stem, ext, source, kind, size_bytes, crc32, priority, timestamp)
                VALUES (?, ?, ?, ?, 'archive', ?, ?, ?, ?)
            """, (vpath, stem, ext, source_rel, size_bytes, crc32_hex, priority, timestamp))

        if has_sources:
            _upsert_source_and_file(cur, source_rel, "archive", priority, f"Archive {archive_name}", vpath, size_bytes, crc32_val)
            if kept_in_override:
                _upsert_source_and_file(cur, "games/client/override", "dir", 350, "Client override", vpath, size_bytes, crc32_val)
            cpf_file = os.path.join(cpf_dir, vpath.replace('/', os.sep))
            if os.path.isfile(cpf_file):
                csz = os.path.getsize(cpf_file)
                with open(cpf_file, 'rb') as cf:
                    ccrc = zlib.crc32(cf.read()) & 0xffffffff
                _upsert_source_and_file(cur, "games/client/ClientPackageFiles", "dir", 300, "ClientPackageFiles", vpath, csz, ccrc)
            else:
                cur.execute("""
                    DELETE FROM files WHERE vpath = ? COLLATE NOCASE
                    AND source_id IN (SELECT id FROM sources WHERE path = 'games/client/ClientPackageFiles')
                """, (vpath,))

        rel_bak = make_rel(f"{disk_file}.bak", repo_root)
        rel_arc_bak = make_rel(archive_backup, repo_root)
        cur.execute("""
            INSERT INTO injection_history
            (timestamp, vpath, archive_name, size_bytes, crc32, backup_file, archive_backup, action, status, notes, batch_id)
            VALUES (?, ?, ?, ?, ?, ?, ?, 'INJECT', 'INJECTED', ?, ?)
        """, (timestamp, vpath, archive_name, size_bytes, crc32_hex, rel_bak, rel_arc_bak, notes, batch_id))
        count += 1

    max_32bit_bytes = 1536 * 1024 * 1024
    headroom_mb = round((max_32bit_bytes - int(archive_size_after)) / (1024.0 * 1024.0), 2)
    cur.execute("""
        INSERT OR REPLACE INTO consolidation_batches
        (batch_id, timestamp, target_archive, archive_size_before, archive_size_after,
         archive_headroom_mb, file_count, synced_client_pkg_count, kept_in_override,
         before_screenshot_path, after_screenshot_path, status, notes)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'COMPLETED', ?)
    """, (
        batch_id, timestamp, archive_name,
        int(archive_size_before), int(archive_size_after), headroom_mb,
        count, int(synced_client_pkg_count), int(kept_in_override),
        before_screenshot_path, after_screenshot_path, notes
    ))

    conn.commit()
    conn.close()
    print(f"[ASSET_DB_BATCH_SYNC] Committed batch '{batch_id}' ({count} files -> {archive_name}, CPF synced: {synced_client_pkg_count}, headroom: {headroom_mb:.2f} MB)")
    return count


def main():
    parser = argparse.ArgumentParser(description="Synchronize asset injection with SQLite assets.db")
    parser.add_argument("--vpath", default=None, help="Virtual path of the asset (e.g. ui/scripts/store2.lua)")
    parser.add_argument("--file", default=None, help="Path to disk source file (optional if entry in archive)")
    parser.add_argument("--archive", default=None, help="Target archive name (e.g. resources99.s2z)")
    parser.add_argument("--db", default=None, help="Path to assets.db")
    parser.add_argument("--repo-root", default=None, help="Repository root path")
    parser.add_argument("--priority", type=int, default=None, help="Explicit priority override")
    parser.add_argument("--evict-override", action="store_true", help="Remove override entry from assets.db during sync")
    parser.add_argument("--evict-only", action="store_true", help="Only purge override entry from assets.db")
    parser.add_argument("--purge-shadows", action="store_true", help="Purge loose shadows for vpath")
    parser.add_argument("--purge-texturecache", action="store_true", help="Invalidate texture cache for vpath")
    parser.add_argument("--action", default=None, choices=["INJECT", "REVERT", "EVICT"],
                        help="Transaction action type (INJECT, REVERT, EVICT)")
    parser.add_argument("--backup-file", default=None, help="Path to created .bak backup file")
    parser.add_argument("--archive-backup", default=None, help="Path to created .s2z.bak backup file")
    parser.add_argument("--status", default=None, help="Explicit transaction status")
    parser.add_argument("--notes", default=None, help="Optional transaction notes/context")
    parser.add_argument("--batch-id", default=None, help="Consolidation batch ID")
    parser.add_argument("--init-schema", action="store_true", help="Initialize extended Step 12 schema, indexes, and views in assets.db")
    parser.add_argument("--sync-override-dir", default=None, nargs="?", const="", help="Sync override directory (or sub-prefix) with assets.db")
    parser.add_argument("--record-visual", action="store_true", help="Record a visual verification screenshot baseline in assets.db")
    parser.add_argument("--phase", default="BEFORE", help="Visual verification phase (BEFORE or AFTER)")
    parser.add_argument("--screen-name", default="main_login", help="Screen identifier for visual baseline")
    parser.add_argument("--image-path", default=None, help="Path to PNG screenshot for visual baseline")
    parser.add_argument("--diff-percent", type=float, default=0.0, help="Visual diff percentage vs BEFORE baseline")
    parser.add_argument("--batch-vpaths-file", default=None, help="File containing newline-delimited vpaths for batch sync")
    parser.add_argument("--size-before", type=int, default=0, help="Archive size in bytes before batch")
    parser.add_argument("--size-after", type=int, default=0, help="Archive size in bytes after batch")
    parser.add_argument("--cpf-synced-count", type=int, default=0, help="Count of ClientPackageFiles synced in batch")
    parser.add_argument("--before-screenshot", default=None, help="Path to BEFORE screenshot")
    parser.add_argument("--after-screenshot", default=None, help="Path to AFTER screenshot")

    args = parser.parse_args()
    try:
        repo_root = args.repo_root or os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
        db_path = args.db or os.path.join(repo_root, "games", ".asset_manifest", "assets.db")

        if args.init_schema:
            conn = sqlite3.connect(db_path)
            cur = conn.cursor()
            cur.execute("PRAGMA journal_mode = WAL;")
            cur.execute("PRAGMA busy_timeout = 5000;")
            ensure_extended_assetdb_schema(cur)
            conn.commit()
            conn.close()
            print(f"[ASSET_DB_SCHEMA] Initialized extended schema, covering indexes, and v_vfs_layer_shadow_matrix in {db_path}")
            if args.sync_override_dir is None and not args.record_visual and not args.batch_vpaths_file and not args.vpath:
                sys.exit(0)

        if args.sync_override_dir is not None:
            sync_override_directory(sub_prefix=args.sync_override_dir or None, db_path=db_path, repo_root=repo_root)
            if not args.record_visual and not args.batch_vpaths_file and not args.vpath:
                sys.exit(0)

        if args.record_visual:
            if not args.batch_id or not args.image_path:
                parser.error("--batch-id and --image-path are required with --record-visual")
            record_visual_baseline(
                batch_id=args.batch_id,
                phase=args.phase,
                screen_name=args.screen_name,
                image_path=args.image_path,
                diff_percent=args.diff_percent,
                db_path=db_path,
                repo_root=repo_root
            )
            if not args.batch_vpaths_file and not args.vpath:
                sys.exit(0)

        if args.batch_vpaths_file:
            if not args.batch_id or not args.archive:
                parser.error("--batch-id and --archive are required with --batch-vpaths-file")
            with open(args.batch_vpaths_file, 'r', encoding='utf-8') as bf:
                vpaths = [line.strip() for line in bf if line.strip()]
            sync_batch_manifest(
                batch_id=args.batch_id,
                archive_name=args.archive,
                vpaths=vpaths,
                archive_size_before=args.size_before,
                archive_size_after=args.size_after,
                synced_client_pkg_count=args.cpf_synced_count,
                kept_in_override=0 if args.evict_override else 1,
                before_screenshot_path=args.before_screenshot,
                after_screenshot_path=args.after_screenshot,
                archive_backup=args.archive_backup,
                notes=args.notes,
                db_path=db_path,
                repo_root=repo_root
            )
            sys.exit(0)

        if not args.vpath:
            parser.error("--vpath is required unless --init-schema, --sync-override-dir, --record-visual, or --batch-vpaths-file is specified")

        if args.purge_shadows and not args.archive:
            if not args.repo_root:
                parser.error("--repo-root is required when --purge-shadows is specified (plan v4 P3.0)")
            wipe_loose_shadows(vpath=args.vpath, repo_root=args.repo_root)
            sys.exit(0)

        if args.purge_texturecache and not args.archive:
            purge_texture_cache(vpath=args.vpath, repo_root=args.repo_root)
            sys.exit(0)

        if args.action == "REVERT":
            record_revert_transaction(
                vpath=args.vpath,
                backup_file=args.backup_file,
                archive_backup=args.archive_backup,
                db_path=args.db,
                repo_root=args.repo_root,
                notes=args.notes
            )
            sys.exit(0)

        if args.evict_only or args.action == "EVICT":
            purge_override_record(
                vpath=args.vpath,
                db_path=args.db,
                repo_root=args.repo_root,
                backup_file=args.backup_file,
                notes=args.notes
            )
            sys.exit(0)

        if not args.archive:
            parser.error("--archive is required unless --action REVERT, --evict-only, --purge-shadows, or --purge-texturecache is specified")

        sync_asset_record(
            vpath=args.vpath,
            file_path=args.file,
            archive_name=args.archive,
            db_path=args.db,
            repo_root=repo_root,
            explicit_priority=args.priority,
            evict_override=args.evict_override,
            purge_shadows=args.purge_shadows,
            action=args.action or "INJECT",
            backup_file=args.backup_file,
            archive_backup=args.archive_backup,
            notes=args.notes,
            batch_id=args.batch_id
        )
        sys.exit(0)
    except Exception as ex:
        print(f"[ERROR] Failed to synchronize asset database: {ex}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()

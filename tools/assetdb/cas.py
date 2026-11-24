# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
tools/assetdb/cas.py
Project Kongor Content-Addressed Storage (CAS) & Merkle Manifest Engine

Implements the Lore architecture for Project Kongor:
1. Hybrid chunking: 1-to-1 direct mapping for files < 1MB; fixed 1MB slices for files >= 1MB.
2. Single-pass dual-hashing: Computes SHA-256 (Merkle CAS) and IEEE 802.3 CRC32 (ZIP header) concurrently.
3. Content-addressed storage in .cas/objects/xx/yy... with atomic commits (ReplaceFile / os.replace).
4. Deterministic Merkle tree root calculation across all canonical (vpath, blob_hash) entries.
5. High-speed SQLite schema and bulk indexing helpers.

Uses Python standard library only.
"""

import os
import sys
import json
import zlib
import hashlib
import sqlite3
from datetime import datetime, timezone

CHUNK_SIZE = 1048576  # Fixed 1 MB (1,048,576 bytes) chunk threshold for hybrid slicing


def init_cas_schema(conn):
    """
    Ensures all CAS and Merkle manifest tables and indexes exist in SQLite.
    """
    cur = conn.cursor()
    try:
        cur.execute("PRAGMA journal_mode = WAL;")
        cur.execute("PRAGMA synchronous = NORMAL;")
    except sqlite3.OperationalError:
        pass

    # 1. Chunk Registry
    cur.execute("""
        CREATE TABLE IF NOT EXISTS cas_chunks (
            chunk_hash TEXT PRIMARY KEY,
            size_bytes INTEGER NOT NULL,
            compressed_bytes INTEGER NOT NULL,
            compression_type TEXT NOT NULL,
            crc32 INTEGER NOT NULL,
            ref_count INTEGER DEFAULT 1,
            first_seen_timestamp TEXT NOT NULL
        );
    """)

    # 2. Blob Registry (Logical Files composed of 1 or more Chunks)
    cur.execute("""
        CREATE TABLE IF NOT EXISTS cas_blobs (
            blob_hash TEXT PRIMARY KEY,
            total_size INTEGER NOT NULL,
            crc32 INTEGER NOT NULL,
            chunk_count INTEGER NOT NULL,
            chunk_hashes_json TEXT NOT NULL
        );
    """)

    # 3. Merkle Manifests (Immutable Versioned Root Snapshots)
    cur.execute("""
        CREATE TABLE IF NOT EXISTS cas_manifests (
            manifest_id TEXT PRIMARY KEY,
            version_tag TEXT NOT NULL,
            timestamp TEXT NOT NULL,
            merkle_root TEXT NOT NULL,
            total_files INTEGER NOT NULL,
            total_uncompressed_bytes INTEGER NOT NULL
        );
    """)

    # 4. Manifest Entries (Mapping vpath -> blob in a specific manifest)
    cur.execute("""
        CREATE TABLE IF NOT EXISTS cas_manifest_entries (
            manifest_id TEXT NOT NULL,
            vpath TEXT COLLATE NOCASE NOT NULL,
            blob_hash TEXT NOT NULL,
            canonical_archive TEXT NOT NULL,
            crc32 INTEGER NOT NULL,
            PRIMARY KEY (manifest_id, vpath),
            FOREIGN KEY (manifest_id) REFERENCES cas_manifests(manifest_id),
            FOREIGN KEY (blob_hash) REFERENCES cas_blobs(blob_hash)
        );
    """)

    # Performance Indexes
    cur.execute("CREATE INDEX IF NOT EXISTS ix_cas_entries_archive ON cas_manifest_entries(canonical_archive);")
    cur.execute("CREATE INDEX IF NOT EXISTS ix_cas_entries_vpath ON cas_manifest_entries(vpath);")
    cur.execute("CREATE INDEX IF NOT EXISTS ix_cas_entries_blob ON cas_manifest_entries(blob_hash);")
    cur.execute("CREATE INDEX IF NOT EXISTS ix_cas_manifests_tag ON cas_manifests(version_tag);")
    conn.commit()


def chunk_and_hash_data(data, compress=True):
    """
    Performs hybrid chunking and dual-hash calculation in a single streaming pass.
    - Files < 1MB: 1-to-1 mapping (chunk_count = 1, chunk_hash == blob_hash).
    - Files >= 1MB: Fixed 1MB chunk slicing.

    Returns:
        (blob_hash, total_size, blob_crc32, chunk_count, chunk_hashes, chunk_records)
        where chunk_records is a list of tuples:
        (chunk_hash, size_bytes, compressed_bytes, compression_type, chunk_crc32, chunk_bytes_compressed)
    """
    total_size = len(data)
    blob_sha = hashlib.sha256(data)
    blob_hash = blob_sha.hexdigest()
    blob_crc32 = zlib.crc32(data) & 0xffffffff

    chunk_records = []
    chunk_hashes = []

    if total_size < CHUNK_SIZE:
        # Hybrid 1-to-1 Mapping
        chunk_hash = blob_hash
        chunk_crc32 = blob_crc32
        chunk_size = total_size

        if compress and chunk_size > 0:
            comp_bytes = zlib.compress(data, level=6)
            comp_type = 'deflate'
        else:
            comp_bytes = data
            comp_type = 'raw'

        chunk_records.append((
            chunk_hash,
            chunk_size,
            len(comp_bytes),
            comp_type,
            chunk_crc32,
            comp_bytes
        ))
        chunk_hashes.append(chunk_hash)
    else:
        # Fixed 1 MB Slicing
        offset = 0
        while offset < total_size:
            slice_end = min(offset + CHUNK_SIZE, total_size)
            chunk_slice = data[offset:slice_end]
            offset = slice_end

            c_sha = hashlib.sha256(chunk_slice).hexdigest()
            c_crc = zlib.crc32(chunk_slice) & 0xffffffff
            c_size = len(chunk_slice)

            if compress:
                c_comp = zlib.compress(chunk_slice, level=6)
                c_type = 'deflate'
            else:
                c_comp = chunk_slice
                c_type = 'raw'

            chunk_records.append((
                c_sha,
                c_size,
                len(c_comp),
                c_type,
                c_crc,
                c_comp
            ))
            chunk_hashes.append(c_sha)

    chunk_count = len(chunk_records)
    return blob_hash, total_size, blob_crc32, chunk_count, chunk_hashes, chunk_records


def get_cas_object_path(cas_dir, chunk_hash):
    """
    Returns the two-character partitioned path in .cas/objects/xx/yyyy...
    """
    return os.path.join(cas_dir, "objects", chunk_hash[:2], chunk_hash[2:])


def save_chunk_to_cas(cas_dir, chunk_hash, chunk_payload, atomic=True):
    """
    Stores an immutable chunk payload to disk under .cas/objects/xx/yy...
    Uses atomic ReplaceFile / os.replace commit to avoid sharing violations.
    """
    target_path = get_cas_object_path(cas_dir, chunk_hash)
    if os.path.exists(target_path):
        return target_path  # Already exists (immutable CAS content)

    target_dir = os.path.dirname(target_path)
    os.makedirs(target_dir, exist_ok=True)

    if atomic:
        tmp_path = f"{target_path}.tmp.{os.getpid()}_{int(datetime.now().timestamp() * 1000)}"
        with open(tmp_path, 'wb') as f:
            f.write(chunk_payload)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp_path, target_path)
    else:
        with open(target_path, 'wb') as f:
            f.write(chunk_payload)

    return target_path


def read_chunk_from_cas(cas_dir, chunk_hash, decompress=True):
    """
    Reads and optionally decompresses a chunk from .cas/objects/.
    """
    obj_path = get_cas_object_path(cas_dir, chunk_hash)
    if not os.path.exists(obj_path):
        raise FileNotFoundError(f"CAS chunk not found on disk: {chunk_hash} at {obj_path}")

    with open(obj_path, 'rb') as f:
        data = f.read()

    if decompress:
        try:
            return zlib.decompress(data)
        except zlib.error:
            # Stored as raw uncompressed
            return data
    return data


def compute_merkle_root(entries):
    """
    Computes a deterministic Merkle tree root hash across all (vpath, blob_hash) entries.
    Entries can be an iterable of tuples: (vpath, blob_hash, ...) or dicts.
    Sorts lexicographically by vpath (normalized forward slash, case-insensitive) to ensure determinism.
    """
    sorted_pairs = []
    for item in entries:
        if isinstance(item, (tuple, list)):
            vpath, blob_hash = item[0], item[1]
        elif isinstance(item, dict):
            vpath, blob_hash = item["vpath"], item["blob_hash"]
        else:
            continue
        vpath_norm = vpath.replace('\\', '/').strip('/').lower()
        sorted_pairs.append((vpath_norm, blob_hash.lower()))

    sorted_pairs.sort(key=lambda x: x[0])

    hasher = hashlib.sha256()
    for vpath_norm, blob_hash in sorted_pairs:
        line = f"{vpath_norm}:{blob_hash}\n".encode('utf-8')
        hasher.update(line)

    return hasher.hexdigest()


def register_blob_in_db(conn, blob_hash, total_size, crc32, chunk_count, chunk_hashes, chunk_records, timestamp=None):
    """
    Inserts or updates the chunk registry and blob registry in SQLite assets.db.
    """
    if timestamp is None:
        timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    cur = conn.cursor()

    # 1. Update or Insert Chunks
    for rec in chunk_records:
        c_hash, c_size, c_comp_size, c_comp_type, c_crc, _ = rec
        cur.execute("""
            INSERT INTO cas_chunks (chunk_hash, size_bytes, compressed_bytes, compression_type, crc32, ref_count, first_seen_timestamp)
            VALUES (?, ?, ?, ?, ?, 1, ?)
            ON CONFLICT(chunk_hash) DO UPDATE SET
                ref_count = ref_count + 1
        """, (c_hash, c_size, c_comp_size, c_comp_type, c_crc, timestamp))

    # 2. Insert Blob
    chunk_hashes_json = json.dumps(chunk_hashes)
    cur.execute("""
        INSERT INTO cas_blobs (blob_hash, total_size, crc32, chunk_count, chunk_hashes_json)
        VALUES (?, ?, ?, ?, ?)
        ON CONFLICT(blob_hash) DO NOTHING
    """, (blob_hash, total_size, crc32, chunk_count, chunk_hashes_json))


def create_manifest_in_db(conn, version_tag, entries, manifest_id=None, timestamp=None):
    """
    Creates a new immutable Merkle manifest snapshot and records all entries.
    entries: list of (vpath, blob_hash, canonical_archive, crc32, size_bytes)
    """
    if timestamp is None:
        timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    if manifest_id is None:
        manifest_id = f"manifest_{version_tag}_{datetime.now().strftime('%Y%m%d_%H%M%S')}"

    merkle_root = compute_merkle_root(entries)
    total_files = len(entries)
    total_bytes = sum(e[4] if len(e) > 4 else 0 for e in entries)

    cur = conn.cursor()
    cur.execute("""
        INSERT INTO cas_manifests (manifest_id, version_tag, timestamp, merkle_root, total_files, total_uncompressed_bytes)
        VALUES (?, ?, ?, ?, ?, ?)
    """, (manifest_id, version_tag, timestamp, merkle_root, total_files, total_bytes))

    entry_rows = []
    for item in entries:
        vpath, blob_hash, archive, crc = item[0], item[1], item[2], item[3]
        vpath_clean = vpath.replace('\\', '/').strip('/')
        entry_rows.append((manifest_id, vpath_clean, blob_hash, archive, crc))

    cur.executemany("""
        INSERT INTO cas_manifest_entries (manifest_id, vpath, blob_hash, canonical_archive, crc32)
        VALUES (?, ?, ?, ?, ?)
        ON CONFLICT(manifest_id, vpath) DO UPDATE SET
            blob_hash = excluded.blob_hash,
            canonical_archive = excluded.canonical_archive,
            crc32 = excluded.crc32
    """, entry_rows)

    conn.commit()
    return manifest_id, merkle_root, total_files, total_bytes


def diff_manifests(conn, manifest_id_old, manifest_id_new):
    """
    Compares two manifest snapshots and returns the delta:
    - added: dict of vpath -> (new_blob_hash, new_archive, crc32)
    - removed: dict of vpath -> (old_blob_hash, old_archive, crc32)
    - modified: dict of vpath -> (old_blob_hash, new_blob_hash, archive, old_crc32)
    - required_chunks: list of chunk hashes needed to assemble modified/added files
    - blob_chunks: dict of blob_hash -> [chunk_hashes...]

    Raises RuntimeError if either manifest anchor is missing from cas_manifests or if any
    required blob anchor is missing from cas_blobs.
    """
    cur = conn.cursor()

    cur.execute(
        "SELECT manifest_id FROM cas_manifests WHERE manifest_id IN (?, ?)",
        (manifest_id_old, manifest_id_new),
    )
    existing_manifests = {row[0] for row in cur.fetchall()}
    missing_manifests = [
        mid for mid in (manifest_id_old, manifest_id_new) if mid not in existing_manifests
    ]
    if missing_manifests:
        raise RuntimeError(
            f"Missing manifest anchor(s) in cas_manifests: {', '.join(missing_manifests)}. "
            f"Verify ingested manifest snapshots in assets.db before running patch_engine.py create."
        )

    cur.execute("SELECT vpath, blob_hash, canonical_archive, crc32 FROM cas_manifest_entries WHERE manifest_id = ?", (manifest_id_old,))
    old_entries = {row[0]: (row[1], row[2], row[3]) for row in cur.fetchall()}

    cur.execute("SELECT vpath, blob_hash, canonical_archive, crc32 FROM cas_manifest_entries WHERE manifest_id = ?", (manifest_id_new,))
    new_entries = {row[0]: (row[1], row[2], row[3]) for row in cur.fetchall()}

    added = {}
    removed = {}
    modified = {}
    needed_blobs = set()

    for vpath, (new_hash, new_arch, new_crc) in new_entries.items():
        if vpath not in old_entries:
            added[vpath] = (new_hash, new_arch, new_crc)
            needed_blobs.add(new_hash)
        else:
            old_hash, old_arch, old_crc = old_entries[vpath]
            if new_hash != old_hash:
                modified[vpath] = (old_hash, new_hash, new_arch, old_crc)
                needed_blobs.add(new_hash)

    for vpath, (old_hash, old_arch, old_crc) in old_entries.items():
        if vpath not in new_entries:
            removed[vpath] = (old_hash, old_arch, old_crc)

    # Collect required chunk hashes for all added and modified blobs
    required_chunk_hashes = set()
    blob_chunks = {}
    if needed_blobs:
        cur.execute(
            f"SELECT blob_hash, chunk_hashes_json FROM cas_blobs WHERE blob_hash IN ({','.join(['?']*len(needed_blobs))})",
            list(needed_blobs),
        )
        rows = cur.fetchall()
        found_blobs = {r[0] for r in rows}
        missing_blobs = sorted(needed_blobs - found_blobs)
        if missing_blobs:
            raise RuntimeError(
                f"Missing {len(missing_blobs)} blob anchor(s) in cas_blobs (first: {missing_blobs[0]}). "
                f"Re-ingest target archives into CAS before running patch_engine.py create."
            )
        for b_hash, chunks_json in rows:
            chunks = json.loads(chunks_json) if chunks_json else []
            if not chunks:
                raise RuntimeError(
                    f"Blob '{b_hash}' in cas_blobs has empty chunk_hashes_json; cannot assemble delta patch."
                )
            blob_chunks[b_hash] = chunks
            required_chunk_hashes.update(chunks)

    return {
        "manifest_old": manifest_id_old,
        "manifest_new": manifest_id_new,
        "old_manifest_id": manifest_id_old,
        "new_manifest_id": manifest_id_new,
        "added_count": len(added),
        "removed_count": len(removed),
        "modified_count": len(modified),
        "added": added,
        "removed": removed,
        "modified": modified,
        "blob_chunks": blob_chunks,
        "required_chunks": sorted(required_chunk_hashes)
    }

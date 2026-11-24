#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
tools/assetdb/build.py
Project Kongor Master Asset Database Builder (v2)

Indexes all VFS mount sources (archives and loose directories) defined in mounts.json
into an authoritative SQLite database (games/.asset_manifest/assets.db) and generates
the legacy NDJSON export (games/.asset_manifest/manifest.ndjson).

Uses Python standard library only.
"""

import os
import sys
import json
import sqlite3
import zlib
import re
import time
import zipfile
import hashlib
import argparse
from datetime import datetime

try:
    from tools.assetdb.cas import (
        init_cas_schema, chunk_and_hash_data, save_chunk_to_cas,
        register_blob_in_db, create_manifest_in_db, compute_merkle_root
    )
    from tools.assetdb.repack_canonical import assign_canonical_archive
except ImportError:
    from cas import (
        init_cas_schema, chunk_and_hash_data, save_chunk_to_cas,
        register_blob_in_db, create_manifest_in_db, compute_merkle_root
    )
    from repack_canonical import assign_canonical_archive

REF_EXTENSIONS = {
    '.package', '.interface', '.entity', '.mdf',
    '.material', '.effect', '.lua', '.xml', '.str'
}

# Regex to detect XML/attribute references
ATTR_REF_RE = re.compile(
    r'\b(?:texture|file|model|clip|vs|ps|material|icon|effect|path|anim|src|sample)\s*=\s*["\']([^"\']+)["\']',
    re.IGNORECASE
)
# Regex to detect include directives
INCLUDE_REF_RE = re.compile(
    r'<include\s+(?:file|package)\s*=\s*["\']([^"\']+)["\']',
    re.IGNORECASE
)
# Regex to detect Lua absolute path literals
LUA_PATH_RE = re.compile(
    r'["\'](/[^"\'\r\n]+\.[a-zA-Z0-9_]+)["\']'
)
# Regex to detect Lua dofile calls
LUA_DOFILE_RE = re.compile(
    r'\bdofile\s*\(\s*["\']([^"\']+)["\']\s*\)',
    re.IGNORECASE
)
# Regex to detect dynamic string concatenation warning in Lua
LUA_CONCAT_RE = re.compile(
    r'(\.\.\s*["\']/[^"\'\r\n]+["\'])|(["\']/[^"\'\r\n]+["\']\s*\.\.)'
)
# Texture archive prefix: e.g. 00000000/path/to/texture.dds
TEXTURE_PREFIX_RE = re.compile(r'^[0-9a-fA-F]{8}/')
# K2 archive mounting regex: resources with 1 or 2 digits
K2_MOUNTABLE_ARCHIVE_RE = re.compile(r'^resources\d{1,2}\.s2z$', re.IGNORECASE)


class Logger:
    def __init__(self, log_path=None, verbose=False):
        self.log_path = log_path
        self.verbose = verbose
        self.log_file = None
        if log_path:
            os.makedirs(os.path.dirname(os.path.abspath(log_path)), exist_ok=True)
            self.log_file = open(log_path, 'w', encoding='utf-8')

    def log(self, msg, force_print=True):
        timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        formatted = f"[{timestamp}] {msg}"
        if force_print or self.verbose:
            print(formatted)
        if self.log_file:
            self.log_file.write(formatted + "\n")
            self.log_file.flush()

    def close(self):
        if self.log_file:
            self.log_file.close()


def normalize_vpath(raw_path):
    """Normalize virtual path to forward slashes without leading/trailing slashes."""
    p = raw_path.replace('\\', '/').strip()
    while p.startswith('/'):
        p = p[1:]
    while p.endswith('/'):
        p = p[:-1]
    return p


def resolve_ref_target(referrer_vpath, raw_ref):
    """Resolve a raw reference string against the referrer's virtual directory."""
    raw_ref = raw_ref.replace('\\', '/').strip()
    if not raw_ref or raw_ref.startswith('#') or raw_ref.startswith('$') or raw_ref.startswith('{'):
        return None
    
    # Must have a file extension or contain a path separator to be an asset path
    base, ext = os.path.splitext(raw_ref)
    if not ext and '/' not in raw_ref:
        return None

    # Strip protocol prefix if present
    if raw_ref.startswith('/'):
        clean = raw_ref.lstrip('/')
    else:
        # Relative reference: resolve relative to referrer's parent virtual directory
        parent_dir = os.path.dirname(referrer_vpath).replace('\\', '/')
        if parent_dir:
            clean = f"{parent_dir}/{raw_ref}"
        else:
            clean = raw_ref

    # Collapse relative segments (e.g. dir/../file)
    parts = []
    for seg in clean.split('/'):
        if seg == '.' or not seg:
            continue
        if seg == '..':
            if parts:
                parts.pop()
        else:
            parts.append(seg)
    resolved = "/".join(parts)
    r_base, r_ext = os.path.splitext(resolved)
    if not r_ext:
        return None
    return resolved


def init_db(db_path):
    os.makedirs(os.path.dirname(os.path.abspath(db_path)), exist_ok=True)
    conn = sqlite3.connect(db_path)
    cur = conn.cursor()
    cur.execute("PRAGMA journal_mode = WAL;")
    cur.execute("PRAGMA synchronous = NORMAL;")
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
    cur.execute("CREATE INDEX IF NOT EXISTS ix_files_vpath ON files(vpath);")
    cur.execute("CREATE INDEX IF NOT EXISTS ix_files_source ON files(source_id);")
    cur.execute("""
        CREATE TABLE IF NOT EXISTS refs (
            from_file INTEGER,
            to_vpath TEXT COLLATE NOCASE,
            kind TEXT,
            line INTEGER
        );
    """)
    cur.execute("CREATE INDEX IF NOT EXISTS ix_refs_to ON refs(to_vpath);")
    cur.execute("CREATE INDEX IF NOT EXISTS ix_refs_from ON refs(from_file);")
    conn.commit()

    # Ensure CAS and Merkle manifest tables are initialized
    init_cas_schema(conn)

    return conn


def extract_references_from_text(referrer_vpath, text_content, ext):
    """Extracts outgoing asset references from text files."""
    refs = []  # tuples of (to_vpath, kind, line_number)
    lines = text_content.splitlines()

    for line_idx, line in enumerate(lines, 1):
        # Attribute references (XML, package, interface, entity, mdf, material, effect)
        for match in ATTR_REF_RE.finditer(line):
            val = match.group(1).strip()
            target = resolve_ref_target(referrer_vpath, val)
            if target:
                refs.append((target, "xml_attr", line_idx))

        for match in INCLUDE_REF_RE.finditer(line):
            val = match.group(1).strip()
            target = resolve_ref_target(referrer_vpath, val)
            if target:
                refs.append((target, "include", line_idx))

        # Lua specific
        if ext == '.lua':
            for match in LUA_DOFILE_RE.finditer(line):
                val = match.group(1).strip()
                target = resolve_ref_target(referrer_vpath, val)
                if target:
                    refs.append((target, "lua_dofile", line_idx))

            for match in LUA_PATH_RE.finditer(line):
                val = match.group(1).strip()
                target = resolve_ref_target(referrer_vpath, val)
                if target:
                    refs.append((target, "lua_path_literal", line_idx))

            if LUA_CONCAT_RE.search(line):
                # Tag dynamic string concat warning
                refs.append((f"__WARN_DYNAMIC_CONCAT__:{line.strip()[:80]}", "lua_dynamic_concat", line_idx))

    return refs


def build_database(repo_root, config_path, db_path, export_ndjson, incremental=False, extract_refs=True, verbose=False, cas_manifest=None, cas_dir=None, build_cas=False):
    timestamp_str = datetime.now().strftime("%Y%m%d_%H%M%S")
    log_file = os.path.join(repo_root, "logs", "assetdb", f"build_{timestamp_str}.log")
    logger = Logger(log_file, verbose)

    logger.log("================================================================================")
    logger.log(" [PROJECT KONGOR] MASTER ASSET DATABASE BUILDER (v2)")
    logger.log(f" Repo Root       : {repo_root}")
    logger.log(f" Mounts Config   : {config_path}")
    logger.log(f" Database Path   : {db_path}")
    logger.log(f" Export NDJSON   : {export_ndjson or 'Disabled'}")
    logger.log(f" Incremental Mode: {incremental}")
    logger.log(f" Extract Refs    : {extract_refs}")
    logger.log(f" CAS Manifest    : {cas_manifest or 'Disabled'}")
    logger.log(f" CAS Directory   : {cas_dir or 'Disabled'}")
    logger.log("================================================================================")

    repo_root = os.path.abspath(repo_root)
    # B8: refuse to write outside repo_root
    for p_name, p_val in [("Database Path", db_path), ("Export NDJSON", export_ndjson), ("CAS Directory", cas_dir)]:
        if p_val:
            abs_p = os.path.abspath(p_val)
            try:
                common = os.path.commonpath([repo_root, abs_p])
                if common != repo_root and not abs_p.startswith(repo_root):
                    logger.log(f"[FATAL] Refusing to write {p_name} outside repo_root: {abs_p} (repo_root: {repo_root})")
                    logger.close()
                    return 1
            except ValueError:
                logger.log(f"[FATAL] Refusing to write {p_name} outside repo_root: {abs_p}")
                logger.close()
                return 1

    if not os.path.exists(config_path):
        logger.log(f"[FATAL] Mount configuration file not found: {config_path}")
        logger.close()
        return 1

    with open(config_path, 'r', encoding='utf-8') as f:
        config = json.load(f)

    sources_spec = config.get("client_sources", [])
    conn = init_db(db_path)
    cur = conn.cursor()

    total_sources_scanned = 0
    total_sources_skipped = 0
    total_files_indexed = 0
    total_refs_extracted = 0
    start_total_sw = time.time()

    for src in sources_spec:
        rel_path = src["path"]
        full_path = os.path.normpath(os.path.join(repo_root, rel_path))
        kind = src.get("kind", "directory")
        priority = int(src.get("priority", 100))
        description = src.get("description", "")
        ignore_exts = set(src.get("ignore_extensions", []))
        is_texture_archive = bool(src.get("is_texture_archive", False))
        is_mounted = bool(src.get("mounted", True))

        if not os.path.exists(full_path):
            logger.log(f"[-] Source does not exist on disk, skipping: {rel_path}")
            continue

        stat = os.stat(full_path)
        mtime = stat.st_mtime
        size = stat.st_size

        # Check if archive meets K2 1-or-2 digit naming specification
        if kind == "archive":
            basename = os.path.basename(full_path)
            if not K2_MOUNTABLE_ARCHIVE_RE.match(basename) and not is_texture_archive:
                logger.log(f"[!] Warning: Archive '{basename}' does not match K2 2-digit format (resources\\d{{1,2}}.s2z). Marking as UNMOUNTED in VFS.")
                is_mounted = False

        # Query existing source record for incremental bypass
        cur.execute("SELECT id, mtime, size, mounted FROM sources WHERE path = ?", (rel_path,))
        existing_source = cur.fetchone()

        if incremental and existing_source:
            s_id, s_mtime, s_size, s_mounted = existing_source
            if abs(s_mtime - mtime) < 0.001 and s_size == size and s_mounted == (1 if is_mounted else 0):
                cur.execute("UPDATE sources SET priority = ?, description = ? WHERE id = ?", (priority, description, s_id))
                conn.commit()
                logger.log(f"[*] [UNCHANGED] Incremental skip: {rel_path} (ID {s_id}, priority {priority})")
                total_sources_skipped += 1
                continue

        # Register or update source record
        if existing_source:
            source_id = existing_source[0]
            cur.execute("""
                UPDATE sources SET kind = ?, priority = ?, mtime = ?, size = ?, mounted = ?, description = ?
                WHERE id = ?
            """, (kind, priority, mtime, size, 1 if is_mounted else 0, description, source_id))
            # Delete stale files and references for this source
            cur.execute("DELETE FROM refs WHERE from_file IN (SELECT id FROM files WHERE source_id = ?)", (source_id,))
            cur.execute("DELETE FROM files WHERE source_id = ?", (source_id,))
            conn.commit()
        else:
            cur.execute("""
                INSERT INTO sources (path, kind, priority, mtime, size, mounted, description)
                VALUES (?, ?, ?, ?, ?, ?, ?)
            """, (rel_path, kind, priority, mtime, size, 1 if is_mounted else 0, description))
            source_id = cur.lastrowid
            conn.commit()

        total_sources_scanned += 1
        src_start_time = time.time()
        files_batch = [] # (vpath, source_id, size, crc32)
        refs_by_vpath = {} # vpath -> list of (to_vpath, kind, line)

        if kind == "archive":
            logger.log(f"[+] Indexing archive [Pri {priority:3d}]: {rel_path} ({size / (1024*1024):.1f} MB)...")
            try:
                with zipfile.ZipFile(full_path, 'r') as zf:
                    for info in zf.infolist():
                        if info.is_dir() or info.filename.endswith('/'):
                            continue
                        raw_vpath = normalize_vpath(info.filename)
                        
                        # Handle texture archive stripping: 00000000/<path> -> <path>
                        if is_texture_archive:
                            raw_vpath = TEXTURE_PREFIX_RE.sub('', raw_vpath)
                        
                        files_batch.append((raw_vpath, source_id, info.file_size, info.CRC))

                        if extract_refs:
                            ext = os.path.splitext(raw_vpath)[1].lower()
                            if ext in REF_EXTENSIONS:
                                try:
                                    raw_text = zf.read(info).decode('utf-8', errors='ignore')
                                    extracted = extract_references_from_text(raw_vpath, raw_text, ext)
                                    if extracted:
                                        refs_by_vpath[raw_vpath] = extracted
                                except Exception as ex:
                                    logger.log(f"    [!] Failed to parse references in {raw_vpath}: {ex}", force_print=False)
            except Exception as ex:
                logger.log(f"[!] Error opening archive {rel_path}: {ex}")
                continue

        elif kind == "directory":
            logger.log(f"[+] Indexing loose directory [Pri {priority:3d}]: {rel_path}...")
            for root, dirs, fnames in os.walk(full_path):
                for fname in fnames:
                    ext = os.path.splitext(fname)[1].lower()
                    if ext in ignore_exts:
                        continue
                    file_full_path = os.path.join(root, fname)
                    file_rel_path = os.path.relpath(file_full_path, full_path)
                    vpath = normalize_vpath(file_rel_path)

                    try:
                        f_stat = os.stat(file_full_path)
                        f_size = f_stat.st_size
                        with open(file_full_path, 'rb') as f_in:
                            content = f_in.read()
                            f_crc = zlib.crc32(content) & 0xffffffff
                        files_batch.append((vpath, source_id, f_size, f_crc))

                        if extract_refs and ext in REF_EXTENSIONS:
                            try:
                                text_content = content.decode('utf-8', errors='ignore')
                                extracted = extract_references_from_text(vpath, text_content, ext)
                                if extracted:
                                    refs_by_vpath[vpath] = extracted
                            except Exception as ex:
                                logger.log(f"    [!] Failed to parse references in {vpath}: {ex}", force_print=False)
                    except Exception as ex:
                        logger.log(f"    [!] Error reading file {file_full_path}: {ex}")

        # Bulk insert files for this source
        cur.executemany("""
            INSERT INTO files (vpath, source_id, size, crc32) VALUES (?, ?, ?, ?)
        """, files_batch)
        conn.commit()

        # Map file IDs for reference insertion
        if refs_by_vpath:
            cur.execute("SELECT id, vpath FROM files WHERE source_id = ?", (source_id,))
            vpath_to_id = {row[1]: row[0] for row in cur.fetchall()}
            refs_batch = []
            for vpath, reflist in refs_by_vpath.items():
                from_id = vpath_to_id.get(vpath)
                if from_id:
                    for to_vpath, r_kind, r_line in reflist:
                        refs_batch.append((from_id, to_vpath, r_kind, r_line))
            if refs_batch:
                cur.executemany("""
                    INSERT INTO refs (from_file, to_vpath, kind, line) VALUES (?, ?, ?, ?)
                """, refs_batch)
                conn.commit()
                total_refs_extracted += len(refs_batch)

        elapsed = time.time() - src_start_time
        total_files_indexed += len(files_batch)
        logger.log(f"    -> Indexed {len(files_batch):,d} files ({len(refs_by_vpath):,d} with outgoing refs) in {elapsed:.2f}s")

    # Optional NDJSON export
    if export_ndjson:
        logger.log(f"[+] Writing NDJSON manifest export to: {export_ndjson}...")
        os.makedirs(os.path.dirname(os.path.abspath(export_ndjson)), exist_ok=True)
        cur.execute("""
            SELECT f.vpath, s.path, s.kind, f.size, f.crc32, s.priority
            FROM files f
            JOIN sources s ON f.source_id = s.id
            ORDER BY f.vpath, s.priority DESC
        """)
        with open(export_ndjson, 'w', encoding='utf-8') as ndf:
            for row in cur.fetchall():
                entry = {
                    "vpath": row[0],
                    "source": row[1],
                    "kind": row[2],
                    "size_bytes": row[3],
                    "crc32": f"{row[4]:08X}",
                    "priority": row[5]
                }
                ndf.write(json.dumps(entry) + "\n")
        logger.log(f"    -> Exported NDJSON manifest successfully.")

    # CAS Merkle Manifest Generation
    if cas_manifest:
        logger.log(f"[+] Generating CAS Merkle Manifest for version tag: '{cas_manifest}'...")
        # Query winning files across mounted sources in VFS priority order
        cur.execute("""
            SELECT f.vpath, f.size, f.crc32, s.path, s.kind
            FROM files f
            JOIN sources s ON f.source_id = s.id
            WHERE s.mounted = 1
            ORDER BY f.vpath, s.priority DESC
        """)
        all_files = cur.fetchall()

        seen_vpaths = set()
        manifest_entries = []

        cur.execute("SELECT crc32, total_size, blob_hash FROM cas_blobs")
        blob_lookup = {(r[0], r[1]): r[2] for r in cur.fetchall()}

        open_zips = {}
        zip_vpath_map = {}

        try:
            for row in all_files:
                vpath, f_size, f_crc, s_path, s_kind = row
                vpath_norm = vpath.replace('\\', '/').strip('/')
                vpath_key = vpath_norm.lower()
                if vpath_key in seen_vpaths:
                    continue
                seen_vpaths.add(vpath_key)

                canon_arch = assign_canonical_archive(vpath_norm)
                blob_hash = blob_lookup.get((f_crc, f_size))

                if not blob_hash:
                    file_full = os.path.normpath(os.path.join(repo_root, s_path))
                    content_bytes = None
                    if s_kind == "archive" and os.path.isfile(file_full):
                        if file_full not in open_zips:
                            try:
                                zf = zipfile.ZipFile(file_full, 'r')
                                open_zips[file_full] = zf
                                vmap = {}
                                is_tex = "textures" in os.path.basename(file_full).lower()
                                for info in zf.infolist():
                                    if info.is_dir() or info.filename.endswith('/'):
                                        continue
                                    nv = normalize_vpath(info.filename)
                                    if is_tex:
                                        nv = TEXTURE_PREFIX_RE.sub('', nv)
                                    vmap[nv.lower()] = info
                                zip_vpath_map[file_full] = vmap
                            except Exception:
                                open_zips[file_full] = None
                                zip_vpath_map[file_full] = {}

                        zf = open_zips.get(file_full)
                        matched_info = zip_vpath_map.get(file_full, {}).get(vpath_key)
                        if zf and matched_info:
                            try:
                                content_bytes = zf.read(matched_info)
                            except Exception:
                                pass
                    elif s_kind == "directory":
                        disk_path = os.path.join(file_full, vpath_norm.replace('/', os.sep))
                        if os.path.isfile(disk_path):
                            try:
                                with open(disk_path, 'rb') as f_in:
                                    content_bytes = f_in.read()
                            except Exception:
                                pass

                    if content_bytes is not None:
                        blob_hash, b_sz, b_crc, c_count, c_hashes, c_recs = chunk_and_hash_data(content_bytes)
                        register_blob_in_db(conn, blob_hash, b_sz, b_crc, c_count, c_hashes, c_recs)
                        blob_lookup[(b_crc, b_sz)] = blob_hash
                        if cas_dir:
                            for crec in c_recs:
                                save_chunk_to_cas(cas_dir, crec[0], crec[5], atomic=True)
                    else:
                        logger.log(f"    -> [CAS WARNING] Unreadable file content for {vpath_norm} from {s_path}; recording UNREADABLE")
                        blob_hash = "UNREADABLE"

                manifest_entries.append((vpath_norm, blob_hash, canon_arch, f_crc, f_size))
        finally:
            for zf in open_zips.values():
                if zf is not None:
                    try:
                        zf.close()
                    except Exception:
                        pass

        manifest_id, merkle_root, m_count, m_bytes = create_manifest_in_db(conn, cas_manifest, manifest_entries)
        logger.log(f"    -> [CAS SUCCESS] Created Manifest: '{manifest_id}'")
        logger.log(f"    -> Version Tag    : {cas_manifest}")
        logger.log(f"    -> Merkle Root    : {merkle_root}")
        logger.log(f"    -> Total Files    : {m_count:,d}")
        logger.log(f"    -> Total Bytes    : {m_bytes / (1024*1024):.2f} MB")

    total_time = time.time() - start_total_sw
    cur.execute("SELECT COUNT(*) FROM files;")
    db_total_files = cur.fetchone()[0]
    cur.execute("SELECT COUNT(*) FROM refs;")
    db_total_refs = cur.fetchone()[0]
    cur.execute("SELECT COUNT(DISTINCT vpath) FROM files;")
    db_unique_vpaths = cur.fetchone()[0]

    logger.log("================================================================================")
    logger.log(" [ASSET DB BUILD COMPLETE SUMMARY]")
    logger.log(f"  Sources Processed   : {total_sources_scanned} scanned, {total_sources_skipped} skipped (incremental)")
    logger.log(f"  Total DB Files      : {db_total_files:,d} entries across all mounts")
    logger.log(f"  Unique VPaths       : {db_unique_vpaths:,d} distinct virtual paths")
    logger.log(f"  References Indexed  : {db_total_refs:,d} cross-asset edges")
    logger.log(f"  Total Build Time    : {total_time:.2f}s")
    logger.log(f"  Authoritative DB    : {db_path}")
    logger.log("================================================================================")

    conn.close()
    logger.close()
    return 0


def main():
    parser = argparse.ArgumentParser(description="Project Kongor Master Asset Database Builder (v2)")
    parser.add_argument("--repo-root", default=None, help="Root path of ProjectKongor repository")
    parser.add_argument("--config", default=None, help="Path to mounts.json")
    parser.add_argument("--db", default=None, help="Path to output assets.db SQLite database")
    parser.add_argument("--export-ndjson", default=None, help="Path to export manifest.ndjson")
    parser.add_argument("--incremental", action="store_true", help="Skip sources whose size and mtime are unchanged")
    parser.add_argument("--no-refs", action="store_true", help="Skip reference extraction")
    parser.add_argument("--verbose", action="store_true", help="Verbose console logging")
    # Single-pass streaming CAS indexing with per-archive ZipFile and texture prefix stripping cache
    parser.add_argument("--build-cas", action="store_true", help="Ingest files into CAS chunk and blob store")
    parser.add_argument("--cas-manifest", default=None, help="Generate an immutable Merkle manifest snapshot with specified version tag")
    parser.add_argument("--cas-dir", default=None, help="Directory for CAS object chunks (default: .cas in repo root)")
    args = parser.parse_args()

    current_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = args.repo_root or os.path.abspath(os.path.join(current_dir, "..", ".."))
    config_path = args.config or os.path.join(current_dir, "mounts.json")
    db_path = args.db or os.path.join(repo_root, "games", ".asset_manifest", "assets.db")
    ndjson_path = args.export_ndjson or os.path.join(repo_root, "games", ".asset_manifest", "manifest.ndjson")
    cas_dir = args.cas_dir or (os.path.join(repo_root, ".cas") if (args.build_cas or args.cas_manifest) else None)

    return build_database(
        repo_root=repo_root,
        config_path=config_path,
        db_path=db_path,
        export_ndjson=ndjson_path,
        incremental=args.incremental,
        extract_refs=not args.no_refs,
        verbose=args.verbose,
        cas_manifest=args.cas_manifest,
        cas_dir=cas_dir,
        build_cas=args.build_cas
    )


if __name__ == "__main__":
    sys.exit(main())

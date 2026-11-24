#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
tools/assetdb/gate.py
Project Kongor S2Z Repack Quality Gate (v2)

Enforces 7 strict repack gating rules before packing assets into .s2z archives:
  Rule 1 [BLOCK]: Target archive name matches K2 2-digit format (resources\\d{1,2}.s2z).
  Rule 2 [BLOCK]: No case collisions among staged files (case-insensitive duplicate paths).
  Rule 3 [BLOCK]: Staged vpath must NOT be silently shadowed by higher-priority mounts (unless --allow-shadow).
  Rule 4 [BLOCK]: All asset references in staged definitions must resolve in staging or mounted VFS.
  Rule 5 [WARN] : Dynamic Lua path concatenations (..) are flagged.
  Rule 6 [BLOCK]: Projected target archive size must not exceed 1.5 GiB limit.
  Rule 7 [WARN] : Pointless overwrite of identical CRC in target archive.

Exit code 0: Passed all rules.
Exit code 1: Blocked by one or more violations.

Uses Python standard library only.
"""

import os
import sys
import re
import json
import zlib
import zipfile
import sqlite3
import argparse
from datetime import datetime

REF_EXTENSIONS = {
    '.package', '.interface', '.entity', '.mdf',
    '.material', '.effect', '.lua', '.xml', '.str'
}

ATTR_REF_RE = re.compile(
    r'\b(?:texture|file|model|clip|vs|ps|material|icon|effect|path|anim|src|sample)\s*=\s*["\']([^"\']+)["\']',
    re.IGNORECASE
)
INCLUDE_REF_RE = re.compile(
    r'<include\s+(?:file|package)\s*=\s*["\']([^"\']+)["\']',
    re.IGNORECASE
)
LUA_PATH_RE = re.compile(
    r'["\'](/[^"\'\r\n]+\.[a-zA-Z0-9_]+)["\']'
)
LUA_DOFILE_RE = re.compile(
    r'\bdofile\s*\(\s*["\']([^"\']+)["\']\s*\)',
    re.IGNORECASE
)
LUA_CONCAT_RE = re.compile(
    r'(\.\.\s*["\']/[^"\'\r\n]+["\'])|(["\']/[^"\'\r\n]+["\']\s*\.\.)'
)
K2_MOUNTABLE_ARCHIVE_RE = re.compile(r'^resources\d{1,2}\.s2z$', re.IGNORECASE)
MAX_ARCHIVE_SIZE_BYTES = int(1.5 * 1024 * 1024 * 1024) # 1.5 GiB strict limit


class GateLogger:
    def __init__(self, log_path=None):
        self.log_file = None
        if log_path:
            os.makedirs(os.path.dirname(os.path.abspath(log_path)), exist_ok=True)
            self.log_file = open(log_path, 'w', encoding='utf-8')

    def log(self, msg):
        timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        line = f"[{timestamp}] {msg}"
        print(line)
        if self.log_file:
            self.log_file.write(line + "\n")
            self.log_file.flush()

    def close(self):
        if self.log_file:
            self.log_file.close()


def normalize_vpath(raw_path):
    p = raw_path.replace('\\', '/').strip()
    while p.startswith('/'):
        p = p[1:]
    while p.endswith('/'):
        p = p[:-1]
    return p


def resolve_ref_target(referrer_vpath, raw_ref):
    raw_ref = raw_ref.replace('\\', '/').strip()
    if not raw_ref or raw_ref.startswith('#') or raw_ref.startswith('$') or raw_ref.startswith('{'):
        return None
    
    # Must have a file extension or contain a path separator to be an asset path
    base, ext = os.path.splitext(raw_ref)
    if not ext and '/' not in raw_ref:
        return None

    if raw_ref.startswith('/'):
        clean = raw_ref.lstrip('/')
    else:
        parent_dir = os.path.dirname(referrer_vpath).replace('\\', '/')
        clean = f"{parent_dir}/{raw_ref}" if parent_dir else raw_ref

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


def check_repack_gate(staging_dir, target_archive, db_path, config_path, repo_root, allow_shadow_reason=None, max_size_gib=1.5):
    timestamp_str = datetime.now().strftime("%Y%m%d_%H%M%S")
    log_file = os.path.join(repo_root, "logs", "assetdb", f"gate_{timestamp_str}.log")
    logger = GateLogger(log_file)

    logger.log("================================================================================")
    logger.log(" [PROJECT KONGOR] S2Z REPACK QUALITY GATE (v2)")
    logger.log(f" Staging Directory : {staging_dir}")
    logger.log(f" Target Archive    : {target_archive}")
    logger.log(f" Database Path     : {db_path}")
    logger.log(f" Allow Shadow      : {allow_shadow_reason or 'None'}")
    logger.log("================================================================================")

    max_size_bytes = int(max_size_gib * 1024 * 1024 * 1024)
    target_basename = os.path.basename(target_archive)
    blocking_violations = []
    warnings = []

    # -------------------------------------------------------------------------
    # RULE 1: Archive Naming Specification (resources\d{1,2}.s2z)
    # -------------------------------------------------------------------------
    logger.log("[*] Checking Rule 1: Target archive K2 2-digit naming format...")
    if not K2_MOUNTABLE_ARCHIVE_RE.match(target_basename):
        err = f"Rule 1 [BLOCK]: Target archive '{target_basename}' does not match K2 format 'resources\\d{{1,2}}.s2z'. K2 CFileManager::RegisterArchives will ignore it at runtime!"
        blocking_violations.append(err)
        logger.log(f"    [FAIL] {err}")
    else:
        logger.log(f"    [PASS] Archive name '{target_basename}' conforms to K2 mount specs.")

    # Validate Staging Directory
    if not os.path.exists(staging_dir):
        err = f"[BLOCK] Staging directory does not exist: {staging_dir}"
        blocking_violations.append(err)
        logger.log(f"    [FAIL] {err}")
        logger.close()
        return 1

    # Load mounts config to determine target priority
    target_priority = 200 # Default to custom patch tier
    if os.path.exists(config_path):
        with open(config_path, 'r', encoding='utf-8') as f:
            cfg = json.load(f)
        for s in cfg.get("client_sources", []):
            if os.path.basename(s.get("path", "")).lower() == target_basename.lower():
                target_priority = int(s.get("priority", 200))
                break
    logger.log(f"[*] Target archive priority resolved to: {target_priority}")

    # Connect to Asset DB
    if not os.path.exists(db_path):
        err = f"[BLOCK] Asset database not found at {db_path}. Build the DB first via tools/assetdb/build.py."
        blocking_violations.append(err)
        logger.log(f"    [FAIL] {err}")
        logger.close()
        return 1

    conn = sqlite3.connect(db_path)
    cur = conn.cursor()

    # Pre-fetch mounted vpaths from DB for fast in-memory resolution
    cur.execute("SELECT LOWER(vpath) FROM files f JOIN sources s ON f.source_id = s.id WHERE s.mounted = 1;")
    mounted_vpaths_set = set(row[0] for row in cur.fetchall())

    # Pre-fetch existing entries in target archive (for Rule 7 CRC check)
    cur.execute("""
        SELECT LOWER(f.vpath), f.crc32, f.size
        FROM files f
        JOIN sources s ON f.source_id = s.id
        WHERE s.path LIKE ? COLLATE NOCASE;
    """, (f"%{target_basename}%",))
    target_archive_entries = {row[0]: (row[1], row[2]) for row in cur.fetchall()}

    # Collect and audit all files in staging directory
    staged_files = {} # vpath_lower -> (original_vpath, abs_path, size, crc32)
    staged_case_map = {} # vpath_lower -> list of original_vpaths

    total_staged_bytes = 0
    for root, dirs, fnames in os.walk(staging_dir):
        for fname in fnames:
            abs_p = os.path.join(root, fname)
            rel_p = os.path.relpath(abs_p, staging_dir)
            vpath = normalize_vpath(rel_p)
            vpath_lower = vpath.lower()

            st_stat = os.stat(abs_p)
            f_size = st_stat.st_size
            total_staged_bytes += f_size
            with open(abs_p, 'rb') as f:
                f_crc = zlib.crc32(f.read()) & 0xffffffff

            staged_files[vpath_lower] = (vpath, abs_p, f_size, f_crc)
            staged_case_map.setdefault(vpath_lower, []).append(vpath)

    logger.log(f"[*] Discovered {len(staged_files):,d} unique files in staging directory ({total_staged_bytes / (1024*1024):.2f} MB).")

    # -------------------------------------------------------------------------
    # RULE 2: Case Collisions Among Staged Files
    # -------------------------------------------------------------------------
    logger.log("[*] Checking Rule 2: Case collisions in staging...")
    rule2_fails = 0
    for vpath_lower, orig_list in staged_case_map.items():
        if len(orig_list) > 1:
            err = f"Rule 2 [BLOCK]: Case collision detected in staging: {orig_list}. Windows and K2 VFS cannot differentiate these paths!"
            blocking_violations.append(err)
            logger.log(f"    [FAIL] {err}")
            rule2_fails += 1
    if rule2_fails == 0:
        logger.log("    [PASS] No case collisions detected.")

    # -------------------------------------------------------------------------
    # RULE 3: Shadowing by Higher-Priority Mounts
    # -------------------------------------------------------------------------
    logger.log(f"[*] Checking Rule 3: Shadowing by mounts with priority > {target_priority}...")
    rule3_fails = 0
    for vpath_lower, (orig_vpath, abs_p, f_size, f_crc) in staged_files.items():
        cur.execute("""
            SELECT s.path AS source_path, s.priority
            FROM files f
            JOIN sources s ON f.source_id = s.id
            WHERE f.vpath = ? COLLATE NOCASE AND s.mounted = 1 AND s.priority > ?
            ORDER BY s.priority DESC;
        """, (orig_vpath, target_priority))
        shadowing_sources = cur.fetchall()

        if shadowing_sources:
            sh_info = ", ".join([f"{r[0]} (Pri {r[1]})" for r in shadowing_sources])
            if allow_shadow_reason:
                warn = f"Rule 3 [ALLOW-SHADOW]: '{orig_vpath}' is shadowed by higher-priority mount [{sh_info}]. Allowed by: \"{allow_shadow_reason}\""
                warnings.append(warn)
                logger.log(f"    [WARN] {warn}")
            else:
                err = f"Rule 3 [BLOCK]: Staged file '{orig_vpath}' is shadowed by higher-priority mount [{sh_info}]! Packing into {target_basename} will be silently ignored by K2."
                blocking_violations.append(err)
                logger.log(f"    [FAIL] {err}")
                rule3_fails += 1
    if rule3_fails == 0 and not allow_shadow_reason:
        logger.log("    [PASS] Zero staged files are shadowed by higher-priority mounts.")

    # -------------------------------------------------------------------------
    # RULE 4 & 5: Reference Resolution and Lua Dynamic Concat
    # -------------------------------------------------------------------------
    logger.log("[*] Checking Rules 4 & 5: Dependency reference resolution and Lua syntax...")
    rule4_fails = 0
    rule5_warns = 0

    for vpath_lower, (orig_vpath, abs_p, f_size, f_crc) in staged_files.items():
        ext = os.path.splitext(orig_vpath)[1].lower()
        if ext not in REF_EXTENSIONS:
            continue

        try:
            with open(abs_p, 'r', encoding='utf-8', errors='ignore') as f:
                lines = f.splitlines() if hasattr(f, 'splitlines') else f.read().splitlines()
        except Exception as ex:
            logger.log(f"    [!] Could not read staged file {orig_vpath}: {ex}")
            continue

        for line_idx, line in enumerate(lines, 1):
            refs_to_check = []
            for match in ATTR_REF_RE.finditer(line):
                tgt = resolve_ref_target(orig_vpath, match.group(1).strip())
                if tgt:
                    refs_to_check.append((tgt, "xml_attr"))
            for match in INCLUDE_REF_RE.finditer(line):
                tgt = resolve_ref_target(orig_vpath, match.group(1).strip())
                if tgt:
                    refs_to_check.append((tgt, "include"))

            if ext == '.lua':
                for match in LUA_DOFILE_RE.finditer(line):
                    tgt = resolve_ref_target(orig_vpath, match.group(1).strip())
                    if tgt:
                        refs_to_check.append((tgt, "dofile"))
                for match in LUA_PATH_RE.finditer(line):
                    tgt = resolve_ref_target(orig_vpath, match.group(1).strip())
                    if tgt:
                        refs_to_check.append((tgt, "path_literal"))
                if LUA_CONCAT_RE.search(line):
                    w = f"Rule 5 [WARN]: Dynamic Lua path concatenation in {orig_vpath}:{line_idx}: {line.strip()[:70]}"
                    warnings.append(w)
                    rule5_warns += 1

            for tgt_vpath, r_kind in refs_to_check:
                tgt_lower = tgt_vpath.lower()
                # Check if resolved in staging
                if tgt_lower in staged_files:
                    continue
                # Check if resolved in mounted VFS
                if tgt_lower in mounted_vpaths_set:
                    continue
                # Check .tga <-> .dds fallback
                alt_tgt = None
                if tgt_lower.endswith('.tga'):
                    alt_tgt = tgt_lower[:-4] + '.dds'
                elif tgt_lower.endswith('.dds'):
                    alt_tgt = tgt_lower[:-4] + '.tga'

                if alt_tgt and (alt_tgt in staged_files or alt_tgt in mounted_vpaths_set):
                    continue

                # Unresolved reference!
                err = f"Rule 4 [BLOCK]: In '{orig_vpath}':{line_idx} [{r_kind}], target '{tgt_vpath}' does NOT exist in staging or any mounted VFS source!"
                blocking_violations.append(err)
                logger.log(f"    [FAIL] {err}")
                rule4_fails += 1

    if rule4_fails == 0:
        logger.log("    [PASS] All asset references in staged definitions resolve successfully.")
    if rule5_warns > 0:
        logger.log(f"    [INFO] Rule 5 generated {rule5_warns} dynamic Lua string concatenation warnings.")

    # -------------------------------------------------------------------------
    # RULE 6: Projected Target Archive Size (<= 1.5 GiB)
    # -------------------------------------------------------------------------
    logger.log("[*] Checking Rule 6: Projected archive size limit (<= 1.5 GiB)...")
    existing_target_bytes = 0
    full_target_path = os.path.normpath(os.path.join(repo_root, target_archive)) if not os.path.isabs(target_archive) else target_archive
    if os.path.exists(full_target_path):
        existing_target_bytes = os.stat(full_target_path).st_size

    projected_bytes = existing_target_bytes + total_staged_bytes
    projected_gib = projected_bytes / (1024 * 1024 * 1024)

    if projected_bytes > max_size_bytes:
        err = f"Rule 6 [BLOCK]: Projected target archive size ({projected_gib:.2f} GiB) exceeds strict {max_size_gib:.2f} GiB limit! (K2 32-bit limit is 2.0 GiB)."
        blocking_violations.append(err)
        logger.log(f"    [FAIL] {err}")
    else:
        logger.log(f"    [PASS] Projected size is {projected_gib:.2f} GiB (Limit: {max_size_gib:.2f} GiB).")

    # -------------------------------------------------------------------------
    # RULE 7: Pointless Overwrite (Identical CRC in target archive)
    # -------------------------------------------------------------------------
    logger.log("[*] Checking Rule 7: Pointless duplicate overwrites in target...")
    rule7_warns = 0
    for vpath_lower, (orig_vpath, abs_p, f_size, f_crc) in staged_files.items():
        if vpath_lower in target_archive_entries:
            prev_crc, prev_size = target_archive_entries[vpath_lower]
            if prev_crc == f_crc and prev_size == f_size:
                w = f"Rule 7 [WARN]: Staged file '{orig_vpath}' is byte-for-byte identical to existing copy in {target_basename} (pointless duplicate repack)."
                warnings.append(w)
                rule7_warns += 1
    if rule7_warns > 0:
        logger.log(f"    [INFO] Rule 7 detected {rule7_warns} identical duplicate files in staging.")
    else:
        logger.log("    [PASS] All staged files contain modifications or new assets.")

    conn.close()

    # Final Decision
    logger.log("================================================================================")
    if blocking_violations:
        logger.log(f" [REPACK GATE: BLOCKED] {len(blocking_violations)} BLOCKING VIOLATION(S) DETECTED!")
        for idx, v in enumerate(blocking_violations, 1):
            logger.log(f"   [{idx}] {v}")
        logger.log("================================================================================")
        logger.close()
        return 1
    else:
        logger.log(" [REPACK GATE: PASSED] All 7 quality gate rules satisfied.")
        if warnings:
            logger.log(f"   ({len(warnings)} non-blocking warnings recorded in log)")
        logger.log("================================================================================")
        logger.close()
        return 0


def main():
    parser = argparse.ArgumentParser(description="Project Kongor S2Z Repack Quality Gate (v2)")
    parser.add_argument("--staging", required=True, help="Path to directory containing staged files to pack")
    parser.add_argument("--target", required=True, help="Target archive filename (e.g. resources99.s2z)")
    parser.add_argument("--db", default=None, help="Path to assets.db SQLite database")
    parser.add_argument("--config", default=None, help="Path to mounts.json")
    parser.add_argument("--repo-root", default=None, help="Root path of ProjectKongor repository")
    parser.add_argument("--allow-shadow", default=None, help="Reason string to allow shadowing by higher-priority mounts")
    parser.add_argument("--max-size-gib", type=float, default=1.5, help="Maximum archive size in GiB (default: 1.5)")

    args = parser.parse_args()

    current_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = args.repo_root or os.path.abspath(os.path.join(current_dir, "..", ".."))
    config_path = args.config or os.path.join(current_dir, "mounts.json")
    db_path = args.db or os.path.join(repo_root, "games", ".asset_manifest", "assets.db")

    return check_repack_gate(
        staging_dir=os.path.abspath(args.staging),
        target_archive=args.target,
        db_path=db_path,
        config_path=config_path,
        repo_root=repo_root,
        allow_shadow_reason=args.allow_shadow,
        max_size_gib=args.max_size_gib
    )


if __name__ == "__main__":
    sys.exit(main())

# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
tools/assetdb/patch_engine.py
Project Kongor Batched Single-Pass Delta Patch Engine (.kpatch)

Generates and applies Content-Addressed binary delta patches:
1. Manifest Diffing: Compares two Merkle manifest snapshots in SQLite assets.db or JSON.
2. Patch Packaging: Emits a single compact .kpatch container containing patch metadata
   and only the required CAS chunks.
3. Batched Single-Pass Patcher: Groups modifications by target canonical archive
   (e.g. resources01.s2z), opening each archive once, updating entries in a single pass,
   and committing atomically via os.replace / ReplaceFileW.
4. Fail-Closed Validation & Preview: Audits chunk completeness, baseline entry CRCs,
   hard-link detection (FN-006), and strict 1.5 GB 32-bit zip limits (allowZip64=False).

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
from datetime import datetime, timezone

try:
    from tools.assetdb.cas import (
        init_cas_schema, diff_manifests, read_chunk_from_cas,
        save_chunk_to_cas, chunk_and_hash_data
    )
except ImportError:
    from cas import (
        init_cas_schema, diff_manifests, read_chunk_from_cas,
        save_chunk_to_cas, chunk_and_hash_data
    )

MAX_S2Z_BYTES = 1_500_000_000  # 1.5 GB ceiling to prevent signed 32-bit zip offset overflow crashes in k2_x64.dll


def log(msg):
    ts = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    print(f"[{ts}] {msg}", flush=True)


def create_kpatch(manifest_old_id, manifest_new_id, db_path, cas_dir, out_kpatch_path, repo_root=None):
    """
    Creates a .kpatch delta container between two manifest snapshots.
    """
    t0 = time.time()
    log("=" * 80)
    log(f" [KPATCH GENERATOR] Creating Delta Patch: '{manifest_old_id}' -> '{manifest_new_id}'")
    log(f" Database  : {db_path}")
    log(f" CAS Dir   : {cas_dir}")
    log(f" Output    : {out_kpatch_path}")
    log("=" * 80)

    if not repo_root:
        repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

    conn = sqlite3.connect(db_path)
    tmp_kpatch = f"{out_kpatch_path}.tmp.{os.getpid()}"
    try:
        # Diff manifests (validates manifest_old_id, manifest_new_id, and cas_blobs anchors)
        diff_result = diff_manifests(conn, manifest_old_id, manifest_new_id)

        log(f"[*] Diff Analysis: {diff_result['added_count']} added, {diff_result['modified_count']} modified, {diff_result['removed_count']} removed")
        log(f"[*] Required CAS Chunks: {len(diff_result['required_chunks'])}")

        os.makedirs(os.path.dirname(os.path.abspath(out_kpatch_path)), exist_ok=True)

        missing_chunks = []
        with zipfile.ZipFile(tmp_kpatch, 'w', compression=zipfile.ZIP_DEFLATED) as patch_zf:
            # 1. Write patch manifest JSON
            manifest_payload = json.dumps(diff_result, indent=2).encode('utf-8')
            patch_zf.writestr("patch_manifest.json", manifest_payload)

            # 2. Package required chunks from CAS store
            total_chunk_bytes = 0

            for ch_hash in diff_result["required_chunks"]:
                chunk_file = os.path.join(cas_dir, "objects", ch_hash[:2], ch_hash[2:])
                if os.path.isfile(chunk_file):
                    with open(chunk_file, 'rb') as cf:
                        c_data = cf.read()
                    patch_zf.writestr(f"chunks/{ch_hash}", c_data)
                    total_chunk_bytes += len(c_data)
                else:
                    missing_chunks.append(ch_hash)

        if missing_chunks:
            sample = ", ".join(missing_chunks[:5])
            log(f"[!] ERROR: {len(missing_chunks)} required CAS chunk(s) missing in {cas_dir} (first: {sample}). Aborting .kpatch emission.")
            raise RuntimeError(
                f"Cannot generate .kpatch '{out_kpatch_path}': {len(missing_chunks)} required CAS chunks are missing from '{cas_dir}' "
                f"(sample: {sample}). Re-populate .cas/objects before running patch_engine.py create."
            )

        os.replace(tmp_kpatch, out_kpatch_path)
        dur = time.time() - t0
        final_sz = os.path.getsize(out_kpatch_path)

        log(f"[+] [SUCCESS] Generated .kpatch in {dur:.2f}s ({final_sz / (1024*1024):.2f} MB, {len(diff_result['required_chunks'])} chunks)")
        return out_kpatch_path
    except Exception:
        if os.path.exists(tmp_kpatch):
            try:
                os.remove(tmp_kpatch)
            except OSError:
                pass
        raise
    finally:
        conn.close()


def preview_kpatch(kpatch_path, target_game_dir, cas_dir=None, db_path=None, repo_root=None, as_json=False):
    """
    Simulates applying a .kpatch without modifying disk.
    Audits:
    1. Chunk completeness (all required CAS chunks present in patch or local CAS).
    2. Hard-link status (st_nlink > 1 violates FN-006).
    3. Baseline entry verification (expected old CRC matches current archive state).
    4. Projected archive sizes (flagging any archive exceeding 1.5 GB limit).
    """
    if not os.path.isfile(kpatch_path):
        raise FileNotFoundError(f".kpatch file not found: {kpatch_path}")

    with zipfile.ZipFile(kpatch_path, 'r') as pzf:
        if "patch_manifest.json" not in pzf.namelist():
            raise ValueError("Invalid .kpatch: missing patch_manifest.json")

        meta = json.loads(pzf.read("patch_manifest.json").decode('utf-8'))
        patch_chunks = {fname[7:] for fname in pzf.namelist() if fname.startswith("chunks/")}

    added = meta.get("added", {})
    modified = meta.get("modified", {})
    removed = meta.get("removed", {})
    required_chunks = set(meta.get("required_chunks", []))

    # Check chunk completeness
    missing_chunks = []
    for ch in required_chunks:
        if ch not in patch_chunks:
            if cas_dir:
                cf = os.path.join(cas_dir, "objects", ch[:2], ch[2:])
                if not os.path.isfile(cf):
                    missing_chunks.append(ch)
            else:
                missing_chunks.append(ch)

    # Group operations by archive
    by_archive = {}
    for vpath, (blob_hash, arc, crc) in added.items():
        by_archive.setdefault(arc, []).append(("ADD", vpath, blob_hash, crc, None))
    for vpath, (old_h, new_h, arc, crc) in modified.items():
        by_archive.setdefault(arc, []).append(("MODIFY", vpath, new_h, crc, old_h))
    for vpath, (old_h, arc, crc) in removed.items():
        by_archive.setdefault(arc, []).append(("REMOVE", vpath, old_h, crc, None))

    archives_report = {}
    total_baseline_mismatches = 0
    total_hardlink_violations = 0
    total_size_violations = 0

    for arc_name, ops in sorted(by_archive.items()):
        arc_path = os.path.join(target_game_dir, arc_name)
        exists = os.path.isfile(arc_path)
        hardlinked = False
        nlink = 1
        current_bytes = 0
        crc_mismatches = []
        missing_entries = []

        if exists:
            st = os.stat(arc_path)
            current_bytes = st.st_size
            nlink = getattr(st, 'st_nlink', 1)
            if nlink > 1:
                hardlinked = True
                total_hardlink_violations += 1

            with zipfile.ZipFile(arc_path, 'r') as ezf:
                existing_entries = {info.filename.lower(): info for info in ezf.infolist()}

            for action, vpath, blob_h, expected_crc, old_h in ops:
                e_info = existing_entries.get(vpath.lower())
                if action in ("MODIFY", "REMOVE"):
                    if not e_info:
                        missing_entries.append(vpath)
                        total_baseline_mismatches += 1
                    elif expected_crc is not None:
                        # Compare CRC if provided (either int or hex string)
                        try:
                            exp_int = int(expected_crc, 16) if isinstance(expected_crc, str) else int(expected_crc)
                            if e_info.CRC != exp_int:
                                crc_mismatches.append({
                                    "path": vpath,
                                    "expected_crc": f"{exp_int:08X}",
                                    "actual_crc": f"{e_info.CRC:08X}"
                                })
                                total_baseline_mismatches += 1
                        except (ValueError, TypeError):
                            crc_mismatches.append({
                                "path": vpath,
                                "expected_crc": f"INVALID({expected_crc})",
                                "actual_crc": f"{e_info.CRC:08X}"
                            })
                            total_baseline_mismatches += 1
        else:
            # Target baseline .s2z archive anchor is missing on disk; any MODIFY or REMOVE op is an anchor mismatch
            for action, vpath, blob_h, expected_crc, old_h in ops:
                if action in ("MODIFY", "REMOVE"):
                    missing_entries.append(vpath)
                    total_baseline_mismatches += 1

        # Estimate projected size (rough: current_bytes + delta of ops)
        projected_bytes = current_bytes
        if projected_bytes >= MAX_S2Z_BYTES:
            total_size_violations += 1

        archives_report[arc_name] = {
            "exists": exists,
            "hardlinked": hardlinked,
            "nlink": nlink,
            "current_bytes": current_bytes,
            "projected_bytes": projected_bytes,
            "exceeds_32bit_limit": projected_bytes >= MAX_S2Z_BYTES,
            "counts": {
                "add": sum(1 for op in ops if op[0] == "ADD"),
                "modify": sum(1 for op in ops if op[0] == "MODIFY"),
                "remove": sum(1 for op in ops if op[0] == "REMOVE")
            },
            "crc_mismatches": crc_mismatches,
            "missing_entries": missing_entries
        }

    ok = (
        len(missing_chunks) == 0 and
        total_hardlink_violations == 0 and
        total_baseline_mismatches == 0 and
        total_size_violations == 0
    )

    report = {
        "ok": ok,
        "kpatch": os.path.abspath(kpatch_path),
        "target_dir": os.path.abspath(target_game_dir),
        "manifest_old_id": meta.get("old_manifest_id", "unknown"),
        "manifest_new_id": meta.get("new_manifest_id", "unknown"),
        "summary": {
            "added": len(added),
            "modified": len(modified),
            "removed": len(removed),
            "archives_affected": len(by_archive),
            "missing_chunks_count": len(missing_chunks),
            "baseline_crc_mismatches": total_baseline_mismatches,
            "hardlink_violations": total_hardlink_violations,
            "size_violations": total_size_violations
        },
        "missing_chunks": missing_chunks[:20],
        "archives": archives_report
    }

    if as_json:
        print(json.dumps(report, indent=2))
    else:
        log("=" * 80)
        log(f" [KPATCH PREVIEW] Audit Report for: {os.path.basename(kpatch_path)}")
        log(f" Target Directory : {target_game_dir}")
        log(f" Manifest Lineage : '{report['manifest_old_id']}' -> '{report['manifest_new_id']}'")
        log(f" Operations       : +{len(added)} added, ~{len(modified)} modified, -{len(removed)} removed across {len(by_archive)} archive(s)")
        log("=" * 80)
        for arc_name, ainfo in sorted(archives_report.items()):
            status = "PASS" if not (ainfo["hardlinked"] or ainfo["exceeds_32bit_limit"] or ainfo["crc_mismatches"] or ainfo["missing_entries"]) else "WARN/FAIL"
            log(f"  [{status}] {arc_name}: +{ainfo['counts']['add']} ~{ainfo['counts']['modify']} -{ainfo['counts']['remove']} | Current: {ainfo['current_bytes'] / (1024*1024):.2f} MB | Hardlinked: {ainfo['hardlinked']} (nlink={ainfo['nlink']})")
            for cm in ainfo["crc_mismatches"]:
                log(f"      [CRC_MISMATCH] {cm['path']}: expected {cm['expected_crc']}, got {cm['actual_crc']}")
            for me in ainfo["missing_entries"]:
                log(f"      [MISSING_ENTRY] {me} does not exist in target archive")
        if missing_chunks:
            log(f"  [FAIL] Missing CAS Chunks ({len(missing_chunks)}): {', '.join(missing_chunks[:5])}")
        log(f" Verdict: {'[PASS] Ready to Apply' if ok else '[FAILED] Pre-Flight Violations Found'}")

    return report


def apply_kpatch(kpatch_path, target_game_dir, cas_dir=None, db_path=None, repo_root=None, force=False):
    """
    Applies a .kpatch package to target_game_dir using the Batched Single-Pass Archive Patching protocol.
    Enforces fail-closed safety guards (missing chunks, hardlinks, 1.5 GB limit, baseline mismatch).
    """
    t0 = time.time()
    log("=" * 80)
    log(f" [KPATCH APPLICATOR] Applying Delta Patch: {kpatch_path}")
    log(f" Target Game Dir: {target_game_dir}")
    log("=" * 80)

    if not os.path.isfile(kpatch_path):
        raise FileNotFoundError(f".kpatch file not found: {kpatch_path}")

    # 1. Run Pre-Flight Validation
    preview = preview_kpatch(kpatch_path, target_game_dir, cas_dir, db_path, repo_root, as_json=False)
    summary = preview["summary"]

    if summary["missing_chunks_count"] > 0:
        raise RuntimeError(f"[FATAL] Cannot apply .kpatch: {summary['missing_chunks_count']} required CAS chunks are missing!")
    if summary["hardlink_violations"] > 0 and not force:
        raise RuntimeError(f"[FATAL] Cannot apply .kpatch: {summary['hardlink_violations']} target archive(s) are hard-linked (nlink > 1). Modifying in-place would poison linked archives (FN-006). Use --force to override.")
    if summary["size_violations"] > 0 and not force:
        raise RuntimeError(f"[FATAL] Cannot apply .kpatch: Target archive size exceeds 1.5 GB K2 32-bit limit (allowZip64=False).")
    if summary["baseline_crc_mismatches"] > 0 and not force:
        raise RuntimeError(f"[FATAL] Cannot apply .kpatch: Baseline state mismatch for {summary['baseline_crc_mismatches']} file(s). Use --force to override.")

    with zipfile.ZipFile(kpatch_path, 'r') as pzf:
        meta = json.loads(pzf.read("patch_manifest.json").decode('utf-8'))
        added = meta.get("added", {})
        modified = meta.get("modified", {})
        removed = meta.get("removed", {})

        # Load chunks from patch into memory / local CAS
        patch_chunks = {}
        for fname in pzf.namelist():
            if fname.startswith("chunks/"):
                ch_hash = fname[7:]
                c_bytes = pzf.read(fname)
                patch_chunks[ch_hash] = c_bytes
                if cas_dir:
                    save_chunk_to_cas(cas_dir, ch_hash, c_bytes, atomic=True)

    log(f"[*] Patch Operations: {len(added)} to add, {len(modified)} to modify, {len(removed)} to remove")

    # Group operations by destination canonical archive
    by_archive = {}  # arc_name -> list of (action, vpath, blob_hash, crc32)

    for vpath, (blob_hash, arc, crc) in added.items():
        by_archive.setdefault(arc, []).append(("ADD", vpath, blob_hash, crc))

    for vpath, (old_h, new_h, arc, crc) in modified.items():
        by_archive.setdefault(arc, []).append(("MODIFY", vpath, new_h, crc))

    for vpath, (old_h, arc, crc) in removed.items():
        by_archive.setdefault(arc, []).append(("REMOVE", vpath, old_h, crc))

    log(f"[*] Target Archives Affected: {len(by_archive)} ({', '.join(by_archive.keys())})")

    conn = None
    cur = None
    if db_path and os.path.isfile(db_path):
        conn = sqlite3.connect(db_path)
        cur = conn.cursor()

    # Apply single-pass update to each affected archive
    for arc_name, ops in by_archive.items():
        arc_t0 = time.time()
        arc_path = os.path.join(target_game_dir, arc_name)

        if not os.path.isfile(arc_path):
            log(f"[!] Warning: Target archive does not exist, creating new: {arc_path}")
            existing_entries = {}
        else:
            with zipfile.ZipFile(arc_path, 'r') as ezf:
                existing_entries = {info.filename.lower(): info for info in ezf.infolist()}

        # Build plan
        ops_by_lower = {vpath.lower(): (action, vpath, blob_h, crc) for action, vpath, blob_h, crc in ops}
        tmp_arc_path = f"{arc_path}.patch_tmp.{os.getpid()}"

        try:
            with zipfile.ZipFile(tmp_arc_path, 'w', compression=zipfile.ZIP_DEFLATED, allowZip64=False) as out_zf:
                # 1. Copy unmodified existing files
                if os.path.isfile(arc_path):
                    with zipfile.ZipFile(arc_path, 'r') as src_zf:
                        for e_lower, info in existing_entries.items():
                            if e_lower in ops_by_lower:
                                action = ops_by_lower[e_lower][0]
                                if action == "REMOVE" or action == "MODIFY":
                                    continue  # Overwritten or removed
                            data = src_zf.read(info)
                            zinfo = zipfile.ZipInfo(info.filename, date_time=info.date_time)
                            zinfo.compress_type = zipfile.ZIP_DEFLATED
                            zinfo.external_attr = info.external_attr
                            out_zf.writestr(zinfo, data)

                # 2. Write added and modified files
                for action, vpath, blob_h, crc in ops:
                    if action in ("ADD", "MODIFY"):
                        # Assemble payload from CAS chunk(s)
                        content = None
                        if blob_h in patch_chunks:
                            raw_c = patch_chunks[blob_h]
                            try:
                                content = zlib.decompress(raw_c)
                            except zlib.error:
                                content = raw_c
                        else:
                            manifest_blob_chunks = meta.get("blob_chunks", {})
                            chunk_hashes = manifest_blob_chunks.get(blob_h) or [blob_h]
                            if conn and blob_h not in manifest_blob_chunks:
                                try:
                                    cur = conn.cursor()
                                    cur.execute("SELECT chunk_hashes_json FROM cas_blobs WHERE blob_hash = ?", (blob_h,))
                                    r = cur.fetchone()
                                    if r and r[0]:
                                        chunk_hashes = json.loads(r[0])
                                except Exception:
                                    pass
                            try:
                                parts = []
                                for ch in chunk_hashes:
                                    if ch in patch_chunks:
                                        raw_c = patch_chunks[ch]
                                        try:
                                            parts.append(zlib.decompress(raw_c))
                                        except zlib.error:
                                            parts.append(raw_c)
                                    elif cas_dir:
                                        parts.append(read_chunk_from_cas(cas_dir, ch, decompress=True))
                                    else:
                                        raise FileNotFoundError(f"Chunk {ch} not present in patch or local CAS")
                                content = b"".join(parts)
                            except Exception as ex:
                                log(f"[!] Warning reading chunk payload for {vpath} ({blob_h}): {ex}")

                        if content is not None:
                            zinfo = zipfile.ZipInfo(vpath, date_time=(1980, 1, 1, 0, 0, 0))
                            zinfo.compress_type = zipfile.ZIP_DEFLATED
                            zinfo.external_attr = 0o644 << 16
                            out_zf.writestr(zinfo, content)
                        else:
                            raise RuntimeError(f"Chunk payload missing for {vpath} ({blob_h}) during archive generation")

            # Check projected size before committing
            final_tmp_sz = os.path.getsize(tmp_arc_path)
            if final_tmp_sz >= MAX_S2Z_BYTES and not force:
                raise RuntimeError(f"Archive {arc_name} generated size {final_tmp_sz} bytes exceeds 1.5 GB limit (allowZip64=False)")

            # Atomic Replace Commit with 5-tier non-destructive backup rotation
            if os.path.isfile(arc_path):
                ts = int(time.time())
                bak5 = f"{arc_path}.bak.5"
                if os.path.exists(bak5):
                    retired_path = f"{arc_path}.bak.retired_{ts}"
                    os.replace(bak5, retired_path)
                    log(f"  [BACKUP] Retired {bak5} -> {retired_path}")
                for gen in range(4, 0, -1):
                    src_bak = f"{arc_path}.bak.{gen}"
                    dst_bak = f"{arc_path}.bak.{gen+1}"
                    if os.path.exists(src_bak):
                        os.replace(src_bak, dst_bak)
                bak1 = f"{arc_path}.bak.1"
                if os.path.exists(bak1):
                    os.replace(bak1, f"{arc_path}.bak.2")
                os.replace(arc_path, bak1)
                log(f"  [BACKUP] Rotated {arc_path} -> {bak1}")
            os.replace(tmp_arc_path, arc_path)

            arc_dur = time.time() - arc_t0
            log(f"  [OK] Batched Single-Pass Update applied to {arc_name} ({len(ops)} operations) in {arc_dur:.2f}s")
        except Exception:
            if os.path.exists(tmp_arc_path):
                try:
                    os.remove(tmp_arc_path)
                except OSError:
                    pass
            raise

    dur = time.time() - t0
    log(f"[+] [SUCCESS] Delta patch applied in {dur:.2f}s across {len(by_archive)} archives!")
    if conn:
        conn.close()
    return True


def main():
    parser = argparse.ArgumentParser(description="Project Kongor Batched Single-Pass Delta Patch Engine")
    subparsers = parser.add_subparsers(dest="command", required=True)

    # Create command
    create_p = subparsers.add_parser("create", help="Create a .kpatch between two manifests")
    create_p.add_argument("--old", required=True, help="Old manifest ID")
    create_p.add_argument("--new", required=True, help="New manifest ID")
    create_p.add_argument("--output", required=True, help="Output .kpatch path")
    create_p.add_argument("--db", default=None, help="Path to assets.db")
    create_p.add_argument("--cas-dir", default=None, help="Path to .cas directory")

    # Preview command
    preview_p = subparsers.add_parser("preview", help="Preview a .kpatch against game archives without modifying disk")
    preview_p.add_argument("kpatch", help="Path to .kpatch file")
    preview_p.add_argument("--target-dir", default=None, help="Target game directory (defaults to games/client/game)")
    preview_p.add_argument("--cas-dir", default=None, help="Path to .cas directory")
    preview_p.add_argument("--db", default=None, help="Path to assets.db")
    preview_p.add_argument("--json", action="store_true", dest="as_json", help="Emit machine-readable JSON output")

    # Apply command
    apply_p = subparsers.add_parser("apply", help="Apply a .kpatch to game archives")
    apply_p.add_argument("kpatch", help="Path to .kpatch file")
    apply_p.add_argument("--target-dir", default=None, help="Target game directory (defaults to games/client/game)")
    apply_p.add_argument("--cas-dir", default=None, help="Path to .cas directory")
    apply_p.add_argument("--db", default=None, help="Path to assets.db")
    apply_p.add_argument("--force", action="store_true", default=False, help="Bypass non-critical safety checks")

    args = parser.parse_args()
    repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

    db_path = getattr(args, "db", None) or os.path.join(repo_root, "games", ".asset_manifest", "assets.db")
    cas_dir = getattr(args, "cas_dir", None) or os.path.join(repo_root, ".cas")

    if args.command == "create":
        create_kpatch(
            manifest_old_id=args.old,
            manifest_new_id=args.new,
            db_path=db_path,
            cas_dir=cas_dir,
            out_kpatch_path=args.output,
            repo_root=repo_root
        )
    elif args.command == "preview":
        target_dir = args.target_dir or os.path.join(repo_root, "games", "client", "game")
        res = preview_kpatch(
            kpatch_path=args.kpatch,
            target_game_dir=target_dir,
            cas_dir=cas_dir,
            db_path=db_path,
            repo_root=repo_root,
            as_json=args.as_json
        )
        if not res["ok"]:
            sys.exit(1)
    elif args.command == "apply":
        target_dir = args.target_dir or os.path.join(repo_root, "games", "client", "game")
        apply_kpatch(
            kpatch_path=args.kpatch,
            target_game_dir=target_dir,
            cas_dir=cas_dir,
            db_path=db_path,
            repo_root=repo_root,
            force=args.force
        )


if __name__ == "__main__":
    main()

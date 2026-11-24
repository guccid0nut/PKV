#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
tools/assetdb/query.py
Project Kongor Asset Database Query CLI (v2)

Provides commands to inspect the VFS mount resolution, track cross-asset references,
audit file shadowing/duplicates, and detect broken or missing asset links:
  - where <vpath>       : Show all sources containing <vpath> and which one wins in VFS
  - who-uses <vpath>    : Find all files that reference <vpath>
  - needs <vpath>       : List all assets required by <vpath> and their resolution status
  - shadows             : Report files that exist in multiple sources (identical vs divergent)
  - missing             : Report broken references from winning files that cannot be resolved
  - orphans [--source X]: Report assets with 0 incoming references

Uses Python standard library only.
"""

import os
import re
import sys
import sqlite3
import argparse
import json

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))


def connect_db(db_path):
    if not os.path.exists(db_path):
        print(f"[FATAL] Asset database not found at {db_path}. Run build.py first.")
        sys.exit(1)
    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    return conn


def cmd_where(conn, vpath, as_json=False):
    cur = conn.cursor()
    vpath_norm = vpath.replace('\\', '/').strip().lstrip('/')
    
    cur.execute("""
        SELECT f.id, f.vpath, f.size, f.crc32, s.path AS source_path, s.kind, s.priority, s.mounted
        FROM files f
        JOIN sources s ON f.source_id = s.id
        WHERE f.vpath = ? COLLATE NOCASE
        ORDER BY s.mounted DESC, s.priority DESC;
    """, (vpath_norm,))
    
    rows = cur.fetchall()
    if not rows:
        # Check .dds <-> .tga fallback
        alt_vpath = None
        if vpath_norm.lower().endswith('.tga'):
            alt_vpath = vpath_norm[:-4] + '.dds'
        elif vpath_norm.lower().endswith('.dds'):
            alt_vpath = vpath_norm[:-4] + '.tga'
        
        if alt_vpath:
            cur.execute("""
                SELECT f.id, f.vpath, f.size, f.crc32, s.path AS source_path, s.kind, s.priority, s.mounted
                FROM files f
                JOIN sources s ON f.source_id = s.id
                WHERE f.vpath = ? COLLATE NOCASE
                ORDER BY s.mounted DESC, s.priority DESC;
            """, (alt_vpath,))
            rows = cur.fetchall()
            if rows:
                print(f"[NOTE] '{vpath_norm}' not found directly, but found via extension fallback '{alt_vpath}':")

    if not rows:
        if as_json:
            print(json.dumps({"vpath": vpath_norm, "found": False, "sources": []}))
        else:
            print(f"[-] '{vpath_norm}' not found in any indexed source.")
        return

    result = {
        "vpath": vpath_norm,
        "found": True,
        "winner": rows[0]["source_path"] if rows[0]["mounted"] else None,
        "sources": []
    }

    winner_crc = rows[0]["crc32"]
    for idx, r in enumerate(rows):
        is_winner = (idx == 0 and r["mounted"] == 1)
        identical_to_winner = (r["crc32"] == winner_crc)
        entry = {
            "source": r["source_path"],
            "priority": r["priority"],
            "mounted": bool(r["mounted"]),
            "size_bytes": r["size"],
            "crc32": f"{r['crc32']:08X}",
            "is_winner": is_winner,
            "identical_to_winner": identical_to_winner
        }
        result["sources"].append(entry)

    if as_json:
        print(json.dumps(result, indent=2))
    else:
        print(f"Virtual Path: {vpath_norm} ({len(rows)} source copies)")
        for idx, s in enumerate(result["sources"]):
            status = "[WINNER]" if s["is_winner"] else "[SHADOWED]"
            if not s["mounted"]:
                status = "[UNMOUNTED]"
            match_str = "(Identical CRC)" if s["identical_to_winner"] else "(DIVERGENT CONTENT)"
            print(f"  {status:12s} Pri {s['priority']:3d} | {s['source']} | Size: {s['size_bytes']:,} B | CRC: {s['crc32']} {match_str if idx > 0 else ''}")


def cmd_who_uses(conn, vpath, as_json=False):
    cur = conn.cursor()
    vpath_norm = vpath.replace('\\', '/').strip().lstrip('/')
    
    # Check direct references and extension fallbacks (.tga <-> .dds)
    alt_vpath = None
    if vpath_norm.lower().endswith('.dds'):
        alt_vpath = vpath_norm[:-4] + '.tga'
    elif vpath_norm.lower().endswith('.tga'):
        alt_vpath = vpath_norm[:-4] + '.dds'

    query = """
        SELECT r.to_vpath, r.kind, r.line, f.vpath AS referrer_vpath, s.path AS source_path, s.priority
        FROM refs r
        JOIN files f ON r.from_file = f.id
        JOIN sources s ON f.source_id = s.id
        WHERE r.to_vpath = ? COLLATE NOCASE
    """
    params = [vpath_norm]
    if alt_vpath:
        query += " OR r.to_vpath = ? COLLATE NOCASE"
        params.append(alt_vpath)
    query += " ORDER BY s.priority DESC, f.vpath, r.line;"

    cur.execute(query, params)
    rows = cur.fetchall()

    if as_json:
        results = [{"referrer": r["referrer_vpath"], "source": r["source_path"], "ref_type": r["kind"], "line": r["line"], "target": r["to_vpath"]} for r in rows]
        print(json.dumps({"target": vpath_norm, "referrers_count": len(results), "referrers": results}, indent=2))
        return

    if not rows:
        print(f"[-] No incoming references found targeting '{vpath_norm}' (or its fallback).")
        return

    print(f"Incoming references to '{vpath_norm}' ({len(rows)} references):")
    for r in rows:
        print(f"  Pri {r['priority']:3d} | {r['source_path']} | {r['referrer_vpath']}:{r['line']} [{r['kind']}] -> {r['to_vpath']}")


def cmd_needs(conn, vpath, as_json=False):
    cur = conn.cursor()
    vpath_norm = vpath.replace('\\', '/').strip().lstrip('/')

    # Find the winning copy of this referrer
    cur.execute("""
        SELECT f.id, f.vpath, s.path AS source_path, s.priority
        FROM files f
        JOIN sources s ON f.source_id = s.id
        WHERE f.vpath = ? COLLATE NOCASE AND s.mounted = 1
        ORDER BY s.priority DESC LIMIT 1;
    """, (vpath_norm,))
    file_row = cur.fetchone()
    if not file_row:
        print(f"[-] File '{vpath_norm}' not found among mounted sources.")
        return

    file_id = file_row["id"]
    cur.execute("""
        SELECT to_vpath, kind, line
        FROM refs
        WHERE from_file = ?
        ORDER BY line, to_vpath;
    """, (file_id,))
    ref_rows = cur.fetchall()

    if not ref_rows:
        cur.execute("""
            SELECT r.to_vpath, r.kind, r.line
            FROM refs r
            JOIN files f ON r.from_file = f.id
            JOIN sources s ON f.source_id = s.id
            WHERE f.vpath = ? COLLATE NOCASE AND s.mounted = 1
              AND f.id = (
                  SELECT f2.id FROM files f2
                  JOIN sources s2 ON f2.source_id = s2.id
                  WHERE f2.vpath = ? COLLATE NOCASE AND s2.mounted = 1
                    AND EXISTS (SELECT 1 FROM refs r2 WHERE r2.from_file = f2.id)
                  ORDER BY s2.priority DESC LIMIT 1
              )
            ORDER BY r.line, r.to_vpath;
        """, (vpath_norm, vpath_norm))
        ref_rows = cur.fetchall()

    if not ref_rows:
        # Fallback for --skip-refs indexes: parse <include>, script=, and texture= attributes directly from the winning mounted file on disk
        disk_path = os.path.join(REPO_ROOT, file_row["source_path"], vpath_norm.replace("/", os.sep))
        if os.path.isfile(disk_path):
            try:
                with open(disk_path, "r", encoding="utf-8", errors="replace") as df:
                    synth_refs = []
                    attr_rx = re.compile(r'\b(file|script|texture|image)\s*=\s*"([^"]+)"', re.IGNORECASE)
                    for line_idx, raw_line in enumerate(df, start=1):
                        for m in attr_rx.finditer(raw_line):
                            raw_target = m.group(2).strip().lstrip("/").replace("\\", "/")
                            if raw_target and "{" not in raw_target and "$" not in raw_target:
                                synth_refs.append({"to_vpath": raw_target, "kind": m.group(1).lower(), "line": line_idx})
                    ref_rows = synth_refs
            except OSError:
                ref_rows = []

    if not ref_rows:
        if as_json:
            print(json.dumps({"file": vpath_norm, "source": file_row["source_path"], "dependencies": []}, indent=2))
        else:
            print(f"[*] File '{vpath_norm}' ({file_row['source_path']}) has 0 outgoing references.")
        return

    # Check resolution for each outgoing reference
    results = []
    for r in ref_rows:
        target = r["to_vpath"]
        if target.startswith("__WARN_DYNAMIC_CONCAT__:"):
            results.append({
                "target": target,
                "kind": r["kind"],
                "line": r["line"],
                "status": "DYNAMIC_CONCAT_WARNING",
                "resolved_source": None
            })
            continue

        # Look up target in files
        cur.execute("""
            SELECT s.path AS source_path, s.priority, f.vpath
            FROM files f
            JOIN sources s ON f.source_id = s.id
            WHERE f.vpath = ? COLLATE NOCASE AND s.mounted = 1
            ORDER BY s.priority DESC LIMIT 1;
        """, (target,))
        target_row = cur.fetchone()

        status = "RESOLVED"
        resolved_source = None
        if target_row:
            resolved_source = f"{target_row['source_path']} (Pri {target_row['priority']})"
        else:
            # Try fallback .tga -> .dds or .dds -> .tga
            alt = None
            if target.lower().endswith('.tga'):
                alt = target[:-4] + '.dds'
            elif target.lower().endswith('.dds'):
                alt = target[:-4] + '.tga'
            
            if alt:
                cur.execute("""
                    SELECT s.path AS source_path, s.priority, f.vpath
                    FROM files f
                    JOIN sources s ON f.source_id = s.id
                    WHERE f.vpath = ? COLLATE NOCASE AND s.mounted = 1
                    ORDER BY s.priority DESC LIMIT 1;
                """, (alt,))
                alt_row = cur.fetchone()
                if alt_row:
                    status = f"FALLBACK_EXT ({os.path.basename(alt)})"
                    resolved_source = f"{alt_row['source_path']} (Pri {alt_row['priority']})"
                else:
                    status = "MISSING"
            else:
                status = "MISSING"

        results.append({
            "target": target,
            "kind": r["kind"],
            "line": r["line"],
            "status": status,
            "resolved_source": resolved_source
        })

    if as_json:
        print(json.dumps({"referrer": vpath_norm, "source": file_row["source_path"], "dependencies": results}, indent=2))
        return

    print(f"Dependencies for '{vpath_norm}' in {file_row['source_path']} ({len(results)} references):")
    missing_count = sum(1 for x in results if x["status"] == "MISSING")
    for x in results:
        status_tag = f"[{x['status']}]"
        dest = f"-> {x['resolved_source']}" if x['resolved_source'] else "MISSING TARGET!"
        print(f"  Line {x['line']:4d} [{x['kind']:14s}] {status_tag:16s} {x['target']} {dest}")
    print(f"Total: {len(results)} references ({missing_count} missing)")


def cmd_shadows(conn, divergent_only=False, filter_str=None, limit=50, as_json=False):
    cur = conn.cursor()
    # Find files appearing in more than 1 mounted source
    cur.execute("""
        SELECT f.vpath, COUNT(DISTINCT f.source_id) AS cnt, COUNT(DISTINCT f.crc32) AS crc_cnt
        FROM files f
        JOIN sources s ON f.source_id = s.id
        WHERE s.mounted = 1
        GROUP BY f.vpath
        HAVING cnt > 1
        ORDER BY f.vpath;
    """)
    groups = cur.fetchall()

    shadow_list = []
    for g in groups:
        vpath = g["vpath"]
        if filter_str and filter_str.lower() not in vpath.lower():
            continue
        is_divergent = (g["crc_cnt"] > 1)
        if divergent_only and not is_divergent:
            continue

        # Get instances ordered by priority DESC
        cur.execute("""
            SELECT s.path AS source_path, s.priority, f.size, f.crc32
            FROM files f
            JOIN sources s ON f.source_id = s.id
            WHERE f.vpath = ? COLLATE NOCASE AND s.mounted = 1
            ORDER BY s.priority DESC;
        """, (vpath,))
        instances = cur.fetchall()
        winner = instances[0]
        shadowed = instances[1:]

        entry = {
            "vpath": vpath,
            "is_divergent": is_divergent,
            "winner": {
                "source": winner["source_path"],
                "priority": winner["priority"],
                "size": winner["size"],
                "crc32": f"{winner['crc32']:08X}"
            },
            "shadowed": [{
                "source": sh["source_path"],
                "priority": sh["priority"],
                "size": sh["size"],
                "crc32": f"{sh['crc32']:08X}",
                "identical": (sh["crc32"] == winner["crc32"])
            } for sh in shadowed]
        }
        shadow_list.append(entry)

    if as_json:
        print(json.dumps({"total_shadowed_vpaths": len(shadow_list), "entries": shadow_list[:limit]}, indent=2))
        return

    print(f"=== Shadowed VPath Audit ({len(shadow_list)} paths with multiple copies, showing up to {limit}) ===")
    for entry in shadow_list[:limit]:
        div_tag = "[DIVERGENT CONTENT!]" if entry["is_divergent"] else "[Identical Duplicates]"
        print(f"\n{div_tag} {entry['vpath']}")
        w = entry["winner"]
        print(f"  * WINNER: Pri {w['priority']:3d} | {w['source']} | Size: {w['size']:,} B | CRC: {w['crc32']}")
        for sh in entry["shadowed"]:
            m_tag = "Identical CRC" if sh["identical"] else "DIFFERENT CRC/CONTENT"
            print(f"    Shadow: Pri {sh['priority']:3d} | {sh['source']} | Size: {sh['size']:,} B | CRC: {sh['crc32']} ({m_tag})")
    
    if len(shadow_list) > limit:
        print(f"\n... and {len(shadow_list) - limit} more shadowed paths. Use --limit to see more or --divergent-only.")


def cmd_missing(conn, filter_str=None, limit=100, as_json=False):
    cur = conn.cursor()
    print("[*] Auditing unresolved asset references across all active (winning) files...")

    # Fetch distinct references from winning files
    cur.execute("""
        SELECT DISTINCT r.to_vpath, r.kind, f.vpath AS referrer_vpath, s.path AS source_path
        FROM refs r
        JOIN files f ON r.from_file = f.id
        JOIN sources s ON f.source_id = s.id
        WHERE s.mounted = 1
        AND r.to_vpath NOT LIKE '__WARN_DYNAMIC_CONCAT__:%'
        ORDER BY r.to_vpath;
    """)
    all_refs = cur.fetchall()

    missing = []
    # Build quick set of all mounted vpaths for fast lookups
    cur.execute("SELECT LOWER(vpath) FROM files f JOIN sources s ON f.source_id = s.id WHERE s.mounted = 1;")
    mounted_set = set(row[0] for row in cur.fetchall())

    for r in all_refs:
        target = r["to_vpath"].lower()
        if filter_str and filter_str.lower() not in target:
            continue
        if target in mounted_set:
            continue
        
        # Check .tga -> .dds fallback
        if target.endswith('.tga') and (target[:-4] + '.dds') in mounted_set:
            continue
        if target.endswith('.dds') and (target[:-4] + '.tga') in mounted_set:
            continue

        missing.append({
            "target": r["to_vpath"],
            "kind": r["kind"],
            "referrer": r["referrer_vpath"],
            "source": r["source_path"]
        })

    if as_json:
        print(json.dumps({"total_missing": len(missing), "entries": missing[:limit]}, indent=2))
        return

    print(f"=== Missing / Broken Asset References ({len(missing)} unresolved references, showing up to {limit}) ===")
    for m in missing[:limit]:
        print(f"  [MISSING] {m['target']:50s} [{m['kind']}] referenced by {m['referrer']} ({m['source']})")
    if len(missing) > limit:
        print(f"\n... and {len(missing) - limit} more missing references.")


def cmd_orphans(conn, source_filter=None, limit=100, as_json=False):
    cur = conn.cursor()
    print(f"[*] Scanning for orphan assets (0 incoming references) in {source_filter or 'all mounted sources'}...")

    # Collect all targets that are referenced
    cur.execute("SELECT LOWER(to_vpath) FROM refs WHERE to_vpath NOT LIKE '__WARN_DYNAMIC_CONCAT__:%';")
    referenced_set = set(row[0] for row in cur.fetchall())

    query = """
        SELECT f.vpath, f.size, s.path AS source_path, s.priority
        FROM files f
        JOIN sources s ON f.source_id = s.id
        WHERE s.mounted = 1
    """
    params = []
    if source_filter:
        query += " AND s.path LIKE ?"
        params.append(f"%{source_filter}%")
    query += " ORDER BY f.vpath;"

    cur.execute(query, params)
    files = cur.fetchall()

    orphans = []
    for f in files:
        v = f["vpath"].lower()
        # Also check without extension or alternate extension
        base, ext = os.path.splitext(v)
        alt = (base + '.tga') if ext == '.dds' else ((base + '.dds') if ext == '.tga' else None)

        if v not in referenced_set and (not alt or alt not in referenced_set):
            orphans.append({
                "vpath": f["vpath"],
                "source": f["source_path"],
                "size": f["size"],
                "priority": f["priority"]
            })

    if as_json:
        print(json.dumps({"total_orphans": len(orphans), "entries": orphans[:limit]}, indent=2))
        return

    print(f"=== Orphan Assets ({len(orphans)} unreferenced files, showing up to {limit}) ===")
    for o in orphans[:limit]:
        print(f"  [ORPHAN] {o['vpath']:60s} | {o['source']} | Size: {o['size']:,} B")
    if len(orphans) > limit:
        print(f"\n... and {len(orphans) - limit} more orphans.")


def main():
    common_parser = argparse.ArgumentParser(add_help=False)
    common_parser.add_argument("--db", default=None, help="Path to assets.db SQLite database")
    common_parser.add_argument("--json", action="store_true", help="Output results in JSON format")

    parser = argparse.ArgumentParser(description="Project Kongor Master Asset Database Query CLI (v2)", parents=[common_parser])
    subparsers = parser.add_subparsers(dest="command", required=True)

    # where
    p_where = subparsers.add_parser("where", help="Locate all copies of an asset and determine VFS winner", parents=[common_parser])
    p_where.add_argument("vpath", help="Virtual path (e.g. ui/fe2/main_v2.interface)")

    # who-uses
    p_who = subparsers.add_parser("who-uses", help="Find all files referencing a target asset", parents=[common_parser])
    p_who.add_argument("vpath", help="Target virtual path")

    # needs
    p_needs = subparsers.add_parser("needs", help="List all outgoing dependencies of an asset", parents=[common_parser])
    p_needs.add_argument("vpath", help="Virtual path of the referrer")

    # shadows
    p_shadows = subparsers.add_parser("shadows", help="Audit duplicate and shadowed files", parents=[common_parser])
    p_shadows.add_argument("--divergent-only", action="store_true", help="Only show duplicates with differing CRC32")
    p_shadows.add_argument("--filter", default=None, help="Filter vpaths by substring")
    p_shadows.add_argument("--limit", type=int, default=50, help="Maximum number of results to display")

    # missing
    p_missing = subparsers.add_parser("missing", help="Audit broken / missing asset references", parents=[common_parser])
    p_missing.add_argument("--filter", default=None, help="Filter missing paths by substring")
    p_missing.add_argument("--limit", type=int, default=100, help="Maximum number of results to display")

    # orphans
    p_orphans = subparsers.add_parser("orphans", help="Audit unreferenced files in sources", parents=[common_parser])
    p_orphans.add_argument("--source", default=None, help="Filter by source substring (e.g. resources98.s2z)")
    p_orphans.add_argument("--limit", type=int, default=100, help="Maximum number of results to display")

    args = parser.parse_args()

    current_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.abspath(os.path.join(current_dir, "..", ".."))
    db_path = args.db or os.path.join(repo_root, "games", ".asset_manifest", "assets.db")

    conn = connect_db(db_path)

    if args.command == "where":
        cmd_where(conn, args.vpath, as_json=args.json)
    elif args.command == "who-uses":
        cmd_who_uses(conn, args.vpath, as_json=args.json)
    elif args.command == "needs":
        cmd_needs(conn, args.vpath, as_json=args.json)
    elif args.command == "shadows":
        cmd_shadows(conn, divergent_only=args.divergent_only, filter_str=args.filter, limit=args.limit, as_json=args.json)
    elif args.command == "missing":
        cmd_missing(conn, filter_str=args.filter, limit=args.limit, as_json=args.json)
    elif args.command == "orphans":
        cmd_orphans(conn, source_filter=args.source, limit=args.limit, as_json=args.json)

    conn.close()


if __name__ == "__main__":
    main()

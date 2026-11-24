#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Project Kongor - Pre-Hook Instruction-Boundary & Prologue Validator (Option Q)
Verifies every hook site in data/patches/native_hook_sites.json against on-disk PE binaries
using scripts/lib/pe_address_math.py and scripts/lib/capstone_analyzer.py.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent
sys.path.insert(0, str(REPO_ROOT))
sys.path.insert(0, str(REPO_ROOT / "scripts" / "lib"))

from pe_address_math import rva_to_file_offset  # noqa: E402
from capstone_analyzer import analyze_bytes  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser(description="Validate native inline hook instruction boundaries (Option Q)")
    parser.add_argument(
        "--manifest",
        default=str(REPO_ROOT / "data" / "patches" / "native_hook_sites.json"),
        help="Path to native_hook_sites.json",
    )
    parser.add_argument(
        "--out-cache",
        default=str(REPO_ROOT / "data" / "patches" / ".hook_sites_verified.json"),
        help="Path to write verified cache artifact",
    )
    args = parser.parse_args()

    manifest_path = Path(args.manifest).resolve()
    if not manifest_path.exists():
        print(
            f"[HOOK-VALIDATOR] [ERROR] Manifest not found: {manifest_path}. "
            "Fix: Restore data/patches/native_hook_sites.json.",
            file=sys.stderr,
        )
        return 1

    manifest_raw = manifest_path.read_bytes()
    manifest_sha256 = hashlib.sha256(manifest_raw).hexdigest()
    manifest = json.loads(manifest_raw.decode("utf-8"))
    sites = manifest.get("sites", [])

    verified_sites = []
    errors = []

    for site in sites:
        name = site.get("name", "unknown")
        binary = site.get("binary", "k2_x64.dll")
        rva_str = str(site.get("rva", "0x0"))
        rva = int(rva_str, 16) if rva_str.lower().startswith("0x") else int(rva_str)
        stolen_bytes = int(site.get("stolen_bytes", 0))
        expected_hex = " ".join(str(site.get("expected_prologue_hex", "")).upper().split())

        if stolen_bytes < 14:
            errors.append(f"{name}: stolen_bytes ({stolen_bytes}) < 14 minimum 64-bit jump size")
            continue

        bin_path = REPO_ROOT / "games" / "client" / binary
        if not bin_path.exists():
            errors.append(f"{name}: binary not found at {bin_path}")
            continue

        calc_offset = rva_to_file_offset(bin_path, rva)
        manifest_offset = site.get("file_offset")
        if manifest_offset is not None and int(manifest_offset) != calc_offset:
            errors.append(f"{name}: file_offset mismatch (manifest={manifest_offset}, calculated={calc_offset})")
            continue

        with bin_path.open("rb") as fp:
            fp.seek(calc_offset)
            raw_bytes = fp.read(max(stolen_bytes, 32))

        actual_hex = " ".join(f"{b:02X}" for b in raw_bytes[:stolen_bytes])
        if actual_hex != expected_hex:
            errors.append(
                f"{name} ({binary} @ {rva_str}): prologue mismatch! "
                f"Expected [{expected_hex}], got [{actual_hex}]"
            )
            continue

        analysis = analyze_bytes(raw_bytes, base_address=rva, min_detour_bytes=14)
        rec_stolen = int(analysis.get("recommended_stolen_bytes", 0))

        # Check that stolen_bytes lands on an exact instruction boundary in the disassembled stream
        cum_len = 0
        boundary_ok = False
        for insn in analysis.get("instruction_boundaries", []):
            cum_len += int(insn.get("size", 0))
            if cum_len == stolen_bytes:
                boundary_ok = True
                break

        if not boundary_ok:
            errors.append(
                f"{name} ({binary} @ {rva_str}): stolen_bytes={stolen_bytes} splits an instruction! "
                f"(Capstone recommended_stolen_bytes={rec_stolen})"
            )
            continue

        verified_sites.append(
            {
                "name": name,
                "group": site.get("group", ""),
                "binary": binary,
                "rva": rva_str,
                "file_offset": calc_offset,
                "stolen_bytes": stolen_bytes,
                "recommended_stolen_bytes": rec_stolen,
                "boundary_exact": boundary_ok,
                "rip_relative_hazards_in_stolen": analysis.get("rip_relative_hazards", []),
            }
        )

    if errors:
        for err in errors:
            print(f"[HOOK-VALIDATOR] [FAIL] {err}", file=sys.stderr)
        print(
            "  -> Fix: Inspect data/patches/native_hook_sites.json and re-run: "
            "python scripts/re/validate_native_hooks.py",
            file=sys.stderr,
        )
        return 1

    out_payload = {
        "schema_version": "1.0.0",
        "manifest_sha256": manifest_sha256,
        "verified_site_count": len(verified_sites),
        "sites": verified_sites,
    }
    out_path = Path(args.out_cache).resolve()
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(out_payload, indent=2) + "\n", encoding="utf-8")
    print(
        f"[HOOK-VALIDATOR] [PASS] Verified {len(verified_sites)} native hook site(s) "
        f"(instruction boundaries + prologues) -> {out_path}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

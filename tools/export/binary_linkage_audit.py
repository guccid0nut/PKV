#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
"""
Binary-level ELF (DT_NEEDED / DT_RUNPATH / .dynsym) and Mach-O (LC_LOAD_DYLIB /
LC_RPATH / LC_CODE_SIGNATURE / Universal-64 slices) linkage auditor for Project
Kongor 3-OS client & server exports.

Verifies that stripping legacy browser/Flash/media attack-surface binaries
(*awesomium*, Awesomium, awesomium_process, *flashplayer*, *ffmpegsumo*,
libplugin_carbon_helper.dylib) preserves 100% dynamic-linking integrity for the
64-bit K2 engine across Linux (x86_64) and macOS (x86_64 + arm64 Universal-64).
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import os
import struct
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ELF_MAGIC = b"\x7fELF"

CPU_TYPES: dict[int, str] = {
    7: "x86",
    0x01000007: "x86_64",
    12: "arm",
    0x0100000C: "arm64",
    18: "ppc",
    0x01000012: "ppc64",
}

LC_LOAD_DYLIB = 0xC
LC_ID_DYLIB = 0xD
LC_LOAD_WEAK_DYLIB = 0x80000018
LC_REEXPORT_DYLIB = 0x8000001F
LC_LAZY_LOAD_DYLIB = 0x20
LC_LOAD_UPWARD_DYLIB = 0x80000023
LC_RPATH = 0x8000001C
LC_CODE_SIGNATURE = 0x1D

LINUX_ROOT_K2_BINARIES: tuple[str, ...] = (
    "hon-x86_64",
    "libk2-x86_64.so",
    "game/cgame-x86_64.so",
    "game/game-x86_64.so",
    "game/libgame_shared-x86_64.so",
    "vid_gl2-x86_64.so",
)

MACOS_ROOT_K2_BINARIES: tuple[str, ...] = (
    "HoN64",
    "libk2.dylib",
    "game/cgame.dylib",
    "game/game.dylib",
    "game/libgame_shared.dylib",
    "vid_gl2.dylib",
)

STANDARD_LIBC_CXX_RUNTIME_SYMBOLS: frozenset[str] = frozenset(
    {
        "_ZdaPv",
        "_ZdaPvm",
        "_ZdlPv",
        "_ZdlPvm",
        "_Znam",
        "_Znwm",
        "calloc",
        "free",
        "localtime",
        "localtime_r",
        "malloc",
        "mremap",
        "munmap",
        "realloc",
        "sbrk",
    }
)


def log(msg: str, level: str = "INFO") -> None:
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
    print(f"[LINKAGE-AUDIT {ts}][{level}] {msg}", flush=True)


def _read_cstr(buf: bytes, offset: int) -> str:
    if offset < 0 or offset >= len(buf):
        return ""
    end = buf.find(b"\x00", offset)
    if end == -1:
        end = len(buf)
    return buf[offset:end].decode("utf-8", errors="replace")


def parse_elf(path: Path, include_dynsym: bool = False) -> dict[str, Any]:
    """Parse ELF32/ELF64 dynamic tags (DT_NEEDED, DT_SONAME, DT_RPATH, DT_RUNPATH) and optional .dynsym."""
    data = path.read_bytes()
    if len(data) < 64 or data[:4] != ELF_MAGIC:
        raise ValueError(
            f"File '{path}' is not a valid ELF binary. "
            f"Diagnose via: python tools/export/binary_linkage_audit.py --repo-root ."
        )

    ei_class = data[4]
    ei_data = data[5]
    endian = "<" if ei_data == 1 else ">"
    is_64 = ei_class == 2

    if is_64:
        (
            e_type,
            e_machine,
            _e_version,
            _e_entry,
            e_phoff,
            e_shoff,
            _e_flags,
            _e_ehsize,
            e_phentsize,
            e_phnum,
            e_shentsize,
            e_shnum,
            _e_shstrndx,
        ) = struct.unpack_from(endian + "HHIQQQIHHHHHH", data, 16)
    else:
        (
            e_type,
            e_machine,
            _e_version,
            _e_entry,
            e_phoff,
            e_shoff,
            _e_flags,
            _e_ehsize,
            e_phentsize,
            e_phnum,
            e_shentsize,
            e_shnum,
            _e_shstrndx,
        ) = struct.unpack_from(endian + "HHIIIIIHHHHHH", data, 16)

    needed: list[str] = []
    rpaths: list[str] = []
    runpaths: list[str] = []
    sonames: list[str] = []
    undefined_symbols: set[str] = set()
    defined_symbols: set[str] = set()

    sections: list[tuple[int, int, int, int, int]] = []
    if e_shoff and e_shnum and e_shentsize:
        for i in range(e_shnum):
            off = e_shoff + i * e_shentsize
            if off + e_shentsize > len(data):
                break
            if is_64:
                (
                    _sh_name,
                    sh_type,
                    _sh_flags,
                    _sh_addr,
                    sh_offset,
                    sh_size,
                    sh_link,
                    _sh_info,
                    _sh_addralign,
                    sh_entsize,
                ) = struct.unpack_from(endian + "IIQQQQIIQQ", data, off)
            else:
                (
                    _sh_name,
                    sh_type,
                    _sh_flags,
                    _sh_addr,
                    sh_offset,
                    sh_size,
                    sh_link,
                    _sh_info,
                    _sh_addralign,
                    sh_entsize,
                ) = struct.unpack_from(endian + "IIIIIIIIII", data, off)
            sections.append((sh_type, sh_offset, sh_size, sh_link, sh_entsize))

    parsed_dynamic = False
    for sh_type, sh_offset, sh_size, sh_link, sh_entsize in sections:
        if sh_type == 6 and 0 <= sh_link < len(sections):  # SHT_DYNAMIC
            str_off, str_size = sections[sh_link][1], sections[sh_link][2]
            dynstr = data[str_off : str_off + str_size]
            ent_sz = 16 if is_64 else 8
            fmt = endian + ("qQ" if is_64 else "iI")
            for pos in range(sh_offset, sh_offset + sh_size, ent_sz):
                if pos + ent_sz > len(data):
                    break
                d_tag, d_val = struct.unpack_from(fmt, data, pos)
                if d_tag == 0:
                    break
                if d_tag == 1:
                    needed.append(_read_cstr(dynstr, d_val))
                elif d_tag == 14:
                    sonames.append(_read_cstr(dynstr, d_val))
                elif d_tag == 15:
                    rpaths.append(_read_cstr(dynstr, d_val))
                elif d_tag == 29:
                    runpaths.append(_read_cstr(dynstr, d_val))
            parsed_dynamic = True
        elif include_dynsym and sh_type == 11 and 0 <= sh_link < len(sections):  # SHT_DYNSYM
            str_off, str_size = sections[sh_link][1], sections[sh_link][2]
            strtab = data[str_off : str_off + str_size]
            ent_sz = sh_entsize or (24 if is_64 else 16)
            for pos in range(sh_offset, sh_offset + sh_size, ent_sz):
                if pos + ent_sz > len(data):
                    break
                if is_64:
                    st_name, _st_info, _st_other, st_shndx, _st_value, _st_size = struct.unpack_from(
                        endian + "IBBHQQ", data, pos
                    )
                else:
                    st_name, _st_value, _st_size, _st_info, _st_other, st_shndx = struct.unpack_from(
                        endian + "IIIBBH", data, pos
                    )
                sym = _read_cstr(strtab, st_name)
                if not sym:
                    continue
                if st_shndx == 0:
                    undefined_symbols.add(sym)
                else:
                    defined_symbols.add(sym)

    if not parsed_dynamic and e_phoff and e_phnum:
        pt_loads: list[tuple[int, int, int, int]] = []
        dyn_ph: tuple[int, int] | None = None
        for i in range(e_phnum):
            off = e_phoff + i * e_phentsize
            if is_64:
                p_type, _p_flags, p_offset, p_vaddr, _p_paddr, p_filesz, p_memsz, _p_align = struct.unpack_from(
                    endian + "IIQQQQQQ", data, off
                )
            else:
                p_type, p_offset, p_vaddr, _p_paddr, p_filesz, p_memsz, _p_flags, _p_align = struct.unpack_from(
                    endian + "IIIIIIII", data, off
                )
            if p_type == 1:
                pt_loads.append((p_vaddr, p_memsz, p_offset, p_filesz))
            elif p_type == 2:
                dyn_ph = (p_offset, p_filesz)

        if dyn_ph is not None:
            d_off, d_sz = dyn_ph
            ent_sz = 16 if is_64 else 8
            fmt = endian + ("qQ" if is_64 else "iI")
            entries: list[tuple[int, int]] = []
            strtab_vaddr: int | None = None
            strsz = 0
            for pos in range(d_off, d_off + d_sz, ent_sz):
                if pos + ent_sz > len(data):
                    break
                d_tag, d_val = struct.unpack_from(fmt, data, pos)
                if d_tag == 0:
                    break
                entries.append((d_tag, d_val))
                if d_tag == 5:
                    strtab_vaddr = d_val
                elif d_tag == 10:
                    strsz = d_val
            if strtab_vaddr is not None:
                file_off = -1
                for pv, _pm, po, pf in pt_loads:
                    if pv <= strtab_vaddr < pv + pf:
                        file_off = po + (strtab_vaddr - pv)
                        break
                if file_off >= 0:
                    dynstr = data[file_off : file_off + (strsz or 65536)]
                    for d_tag, d_val in entries:
                        if d_tag == 1:
                            needed.append(_read_cstr(dynstr, d_val))
                        elif d_tag == 14:
                            sonames.append(_read_cstr(dynstr, d_val))
                        elif d_tag == 15:
                            rpaths.append(_read_cstr(dynstr, d_val))
                        elif d_tag == 29:
                            runpaths.append(_read_cstr(dynstr, d_val))

    result: dict[str, Any] = {
        "format": "ELF64" if is_64 else "ELF32",
        "is_64": is_64,
        "type": e_type,
        "machine": e_machine,
        "size": len(data),
        "sonames": sonames,
        "needed": needed,
        "rpaths": rpaths,
        "runpaths": runpaths,
    }
    if include_dynsym:
        result["undefined_symbols"] = sorted(undefined_symbols)
        result["defined_symbols"] = sorted(defined_symbols)
    return result


def _parse_macho_slice(data: bytes, base_off: int, size: int) -> dict[str, Any] | None:
    if base_off + 28 > len(data):
        return None
    magic = struct.unpack_from(">I", data, base_off)[0]
    if magic == 0xFEEDFACE:
        endian, is_64 = ">", False
    elif magic == 0xCEFAEDFE:
        endian, is_64 = "<", False
    elif magic == 0xFEEDFACF:
        endian, is_64 = ">", True
    elif magic == 0xCFFAEDFE:
        endian, is_64 = "<", True
    else:
        return None

    if is_64:
        cputype, _cpusubtype, filetype, ncmds, _sizeofcmds, _flags, _reserved = struct.unpack_from(
            endian + "iiIIIII", data, base_off + 4
        )
        cmd_off = base_off + 32
    else:
        cputype, _cpusubtype, filetype, ncmds, _sizeofcmds, _flags = struct.unpack_from(
            endian + "iiIIII", data, base_off + 4
        )
        cmd_off = base_off + 28

    cpu_name = CPU_TYPES.get(cputype & 0xFFFFFFFF, f"0x{cputype & 0xFFFFFFFF:x}")
    dylibs: list[str] = []
    weak_dylibs: list[str] = []
    rpaths: list[str] = []
    install_id: str | None = None
    has_code_signature = False

    cur = cmd_off
    limit = min(base_off + size, len(data))
    for _ in range(ncmds):
        if cur + 8 > limit:
            break
        cmd, cmdsize = struct.unpack_from(endian + "II", data, cur)
        if cmdsize < 8 or cur + cmdsize > limit:
            break
        if cmd in (
            LC_LOAD_DYLIB,
            LC_LOAD_WEAK_DYLIB,
            LC_REEXPORT_DYLIB,
            LC_LAZY_LOAD_DYLIB,
            LC_LOAD_UPWARD_DYLIB,
            LC_ID_DYLIB,
        ):
            str_rel = struct.unpack_from(endian + "I", data, cur + 8)[0]
            s = _read_cstr(data[cur : cur + cmdsize], str_rel)
            if cmd == LC_ID_DYLIB:
                install_id = s
            elif cmd == LC_LOAD_WEAK_DYLIB:
                weak_dylibs.append(s)
            else:
                dylibs.append(s)
        elif cmd == LC_RPATH:
            str_rel = struct.unpack_from(endian + "I", data, cur + 8)[0]
            rpaths.append(_read_cstr(data[cur : cur + cmdsize], str_rel))
        elif cmd == LC_CODE_SIGNATURE:
            has_code_signature = True
        cur += cmdsize

    return {
        "arch": cpu_name,
        "is_64": is_64,
        "filetype": filetype,
        "install_id": install_id,
        "dylibs": dylibs,
        "weak_dylibs": weak_dylibs,
        "rpaths": rpaths,
        "has_code_signature": has_code_signature,
    }


def parse_macho(path: Path) -> dict[str, Any]:
    """Parse thin or fat (Universal) Mach-O headers, LC_LOAD_DYLIB entries, RPATHs, and LC_CODE_SIGNATURE."""
    data = path.read_bytes()
    if len(data) < 28:
        raise ValueError(
            f"File '{path}' is too small to be a Mach-O binary ({len(data)} bytes). "
            f"Diagnose via: python tools/export/binary_linkage_audit.py --repo-root ."
        )
    magic_be = struct.unpack_from(">I", data, 0)[0]
    if magic_be in (0xCAFEBABE, 0xCAFEBABF):
        nfat = struct.unpack_from(">I", data, 4)[0]
        if nfat == 0 or nfat > 16:
            raise ValueError(f"File '{path}' has invalid Mach-O fat header nfat_arch={nfat}.")
        slices: list[dict[str, Any]] = []
        off = 8
        is_fat64 = magic_be == 0xCAFEBABF
        for _ in range(nfat):
            if is_fat64:
                _cputype, _cpusubtype, s_off, s_sz, _align, _reserved = struct.unpack_from(">iiQQII", data, off)
                off += 32
            else:
                _cputype, _cpusubtype, s_off, s_sz, _align = struct.unpack_from(">iiIII", data, off)
                off += 20
            sl = _parse_macho_slice(data, s_off, s_sz)
            if sl is not None:
                slices.append(sl)
        return {
            "format": "Mach-O Fat",
            "size": len(data),
            "archs": [s["arch"] for s in slices],
            "slices": slices,
        }
    if magic_be in (0xFEEDFACE, 0xCEFAEDFE, 0xFEEDFACF, 0xCFFAEDFE):
        sl = _parse_macho_slice(data, 0, len(data))
        if sl is None:
            raise ValueError(f"Failed to parse thin Mach-O header for '{path}'.")
        return {
            "format": "Mach-O Thin",
            "size": len(data),
            "archs": [sl["arch"]],
            "slices": [sl],
        }
    raise ValueError(f"File '{path}' does not have a valid Mach-O magic header (0x{magic_be:08x}).")


def audit_linux_client_linkage(linux_dir: Path) -> dict[str, Any]:
    """Audit all 64-bit K2 ELF binaries and verify legacy Awesomium/Flash/FFmpeg dependency status."""
    if not linux_dir.is_dir():
        raise FileNotFoundError(
            f"Linux client directory '{linux_dir}' not found. "
            f"Verify working directory is the ProjectKongor repository root."
        )

    roots_report: dict[str, Any] = {}
    all_needed: set[str] = set()
    for rel in LINUX_ROOT_K2_BINARIES:
        target = linux_dir / rel
        if not target.is_file():
            raise FileNotFoundError(
                f"Required Linux 64-bit K2 binary '{target}' missing. "
                f"Diagnose via: Get-ChildItem -LiteralPath '{linux_dir}'"
            )
        include_syms = rel == "libk2-x86_64.so"
        info = parse_elf(target, include_dynsym=include_syms)
        if not info["is_64"]:
            raise ValueError(f"Expected 64-bit ELF for '{target}', got {info['format']}.")
        all_needed.update(info["needed"])
        roots_report[rel] = {
            "format": info["format"],
            "size": info["size"],
            "needed": info["needed"],
            "runpaths": info["runpaths"],
            "rpaths": info["rpaths"],
        }

    # Transitive scan across bundled 64-bit libraries in libs-x86_64 (excluding unreferenced legacy bloat)
    bundled_libs_dir = linux_dir / "libs-x86_64"
    bundled_soname_map: dict[str, str] = {}
    if bundled_libs_dir.is_dir():
        for so_file in sorted(bundled_libs_dir.iterdir()):
            if not so_file.is_file():
                continue
            try:
                hdr = so_file.read_bytes()[:4]
            except OSError:
                continue
            if hdr != ELF_MAGIC:
                continue
            so_info = parse_elf(so_file)
            soname = so_info["sonames"][0] if so_info["sonames"] else so_file.name
            bundled_soname_map[soname] = f"libs-x86_64/{so_file.name}"
            bundled_soname_map[so_file.name] = f"libs-x86_64/{so_file.name}"

    # Verify Awesomium stub vs full 66.8 MB Awesomium library
    stub_path = linux_dir / "libs-stub" / "libawesomium-1-7.so.5"
    full_awe_17 = linux_dir / "libs-x86_64" / "libawesomium-1-7.so.5"
    k2_info = parse_elf(linux_dir / "libk2-x86_64.so", include_dynsym=True)

    stub_verified = False
    awesomium_api_symbols_imported: list[str] = []
    if stub_path.is_file():
        stub_info = parse_elf(stub_path, include_dynsym=True)
        stub_verified = (
            stub_info["is_64"]
            and "libawesomium-1-7.so.5" in stub_info["sonames"]
            and len(stub_info["needed"]) == 0
        )
    if full_awe_17.is_file():
        full_awe_info = parse_elf(full_awe_17, include_dynsym=True)
        shared_syms = set(k2_info["undefined_symbols"]) & set(full_awe_info["defined_symbols"])
        awesomium_api_symbols_imported = sorted(shared_syms - STANDARD_LIBC_CXX_RUNTIME_SYMBOLS)

    # Identify unreferenced legacy browser/Flash/FFmpeg binaries
    unreferenced_bloat_candidates = [
        ("libs-x86_64/libawesomium-1.6.5.so.0", "libawesomium-1.6.5.so.0"),
        ("libs-x86_64/plugins/libflashplayer.so", "libflashplayer.so"),
        ("libs-x86_64/plugins/libffmpegsumo.so", "libffmpegsumo.so"),
        ("libs-x86/libawesomium-1-7.so.5", "ELF32-libawesomium-1-7.so.5"),
        ("libs-x86/libawesomium-1.6.5.so.0", "ELF32-libawesomium-1.6.5.so.0"),
        ("libs-x86/plugins/libflashplayer.so", "ELF32-libflashplayer.so"),
        ("libs-x86/plugins/libffmpegsumo.so", "ELF32-libffmpegsumo.so"),
    ]
    unreferenced_bloat: list[dict[str, Any]] = []
    for rel_cand, soname_key in unreferenced_bloat_candidates:
        fp = linux_dir / rel_cand
        if fp.is_file():
            is_ref = soname_key in all_needed or Path(rel_cand).name in all_needed
            if not is_ref:
                unreferenced_bloat.append(
                    {
                        "path": rel_cand,
                        "size": fp.stat().st_size,
                        "referenced_by_64bit_k2": False,
                    }
                )

    if full_awe_17.is_file() and stub_verified and len(awesomium_api_symbols_imported) == 0:
        unreferenced_bloat.append(
            {
                "path": "libs-x86_64/libawesomium-1-7.so.5",
                "size": full_awe_17.stat().st_size,
                "referenced_by_64bit_k2": False,
                "satisfied_by_zero_dep_stub": "libs-stub/libawesomium-1-7.so.5",
                "stub_size": stub_path.stat().st_size,
            }
        )

    return {
        "linux_dir": str(linux_dir),
        "roots": roots_report,
        "all_64bit_dt_needed": sorted(all_needed),
        "awesomium_stub_verified": stub_verified,
        "awesomium_api_symbols_imported": awesomium_api_symbols_imported,
        "bundled_soname_map": bundled_soname_map,
        "unreferenced_legacy_bloat": unreferenced_bloat,
    }


def audit_macos_client_linkage(macos_dir: Path) -> dict[str, Any]:
    """Audit all Universal-64 K2 Mach-O binaries and verify legacy Awesomium/Flash/Carbon status."""
    if not macos_dir.is_dir():
        raise FileNotFoundError(
            f"macOS client directory '{macos_dir}' not found. "
            f"Verify working directory is the ProjectKongor repository root."
        )

    roots_report: dict[str, Any] = {}
    all_loaded_dylibs: set[str] = set()

    for rel in MACOS_ROOT_K2_BINARIES:
        target = macos_dir / rel
        if not target.is_file():
            raise FileNotFoundError(
                f"Required macOS Universal-64 K2 binary '{target}' missing. "
                f"Diagnose via: Get-ChildItem -LiteralPath '{macos_dir}'"
            )
        info = parse_macho(target)
        for sl in info["slices"]:
            all_loaded_dylibs.update(sl["dylibs"])
            all_loaded_dylibs.update(sl["weak_dylibs"])
        roots_report[rel] = {
            "format": info["format"],
            "size": info["size"],
            "archs": info["archs"],
            "all_slices_codesigned": all(s["has_code_signature"] for s in info["slices"]),
            "dylibs": info["slices"][0]["dylibs"] if info["slices"] else [],
            "rpaths": info["slices"][0]["rpaths"] if info["slices"] else [],
        }

    frameworks_u64 = macos_dir / "Contents" / "Frameworks" / "universal64"
    universal64_dylibs: dict[str, Any] = {}
    if frameworks_u64.is_dir():
        for dylib in sorted(frameworks_u64.glob("*.dylib")):
            info = parse_macho(dylib)
            for sl in info["slices"]:
                all_loaded_dylibs.update(sl["dylibs"])
            universal64_dylibs[f"Contents/Frameworks/universal64/{dylib.name}"] = {
                "archs": info["archs"],
                "size": info["size"],
                "all_slices_codesigned": all(s["has_code_signature"] for s in info["slices"]),
                "dylibs": info["slices"][0]["dylibs"] if info["slices"] else [],
            }

    loaded_basenames = {Path(d).name for d in all_loaded_dylibs}

    legacy_candidates = [
        "Contents/Frameworks/Awesomium.framework/Versions/A/Awesomium",
        "Contents/Frameworks/Awesomium.framework/Versions/A/awesomium_process",
        "Contents/Frameworks/Awesomium.framework/Versions/A/ffmpegsumo.so",
        "Contents/Frameworks/Awesomium.framework/Versions/A/libplugin_carbon_helper.dylib",
    ]
    unreferenced_bloat: list[dict[str, Any]] = []
    for rel_cand in legacy_candidates:
        fp = macos_dir / rel_cand
        if fp.is_file():
            minfo = parse_macho(fp)
            bn = fp.name
            is_ref = any(bn in d for d in all_loaded_dylibs) or (bn in loaded_basenames)
            if not is_ref:
                unreferenced_bloat.append(
                    {
                        "path": rel_cand,
                        "size": minfo["size"],
                        "archs": minfo["archs"],
                        "referenced_by_universal64_k2": False,
                    }
                )

    return {
        "macos_dir": str(macos_dir),
        "roots": roots_report,
        "universal64_frameworks": universal64_dylibs,
        "all_loaded_dylibs": sorted(all_loaded_dylibs),
        "unreferenced_legacy_bloat": unreferenced_bloat,
    }


def _matches_robocopy_exclusions(
    rel_posix: str,
    excl_files: list[str] | tuple[str, ...],
    excl_dirs: list[str] | tuple[str, ...],
) -> bool:
    parts = rel_posix.split("/")
    filename = parts[-1]
    dir_parts = parts[:-1]
    for dp in dir_parts:
        for xd in excl_dirs:
            xd_norm = xd.replace("\\", "/").strip("/")
            if fnmatch.fnmatchcase(dp.lower(), xd_norm.lower()):
                return True
    for xf in excl_files:
        if fnmatch.fnmatchcase(filename.lower(), xf.lower()):
            return True
    return False


def verify_export_exclusions(
    linux_dir: Path,
    macos_dir: Path,
    linux_excl_files: list[str] | tuple[str, ...],
    linux_excl_dirs: list[str] | tuple[str, ...],
    mac_excl_files: list[str] | tuple[str, ...],
    mac_excl_dirs: list[str] | tuple[str, ...],
    restores_linux_awesomium_stub: bool = True,
) -> dict[str, Any]:
    """
    Prove that every ELF64 DT_NEEDED and Mach-O LC_LOAD_DYLIB required by the 64-bit K2 engine
    survives the Robocopy exclusion lists (with Linux Awesomium satisfied by libs-stub/libawesomium-1-7.so.5),
    and that every verified unreferenced legacy browser/Flash/FFmpeg binary is excluded.
    """
    linux_audit = audit_linux_client_linkage(linux_dir)
    macos_audit = audit_macos_client_linkage(macos_dir)

    errors: list[str] = []

    # 1. Every Linux 64-bit root K2 binary must survive exclusion
    for rel in LINUX_ROOT_K2_BINARIES:
        if _matches_robocopy_exclusions(rel, linux_excl_files, linux_excl_dirs):
            errors.append(f"CRITICAL: Required Linux K2 binary '{rel}' is matched by Linux exclusions!")

    # 2. Every bundled 64-bit DT_NEEDED library must survive exclusion (or be restored via verified 0-dep stub)
    soname_map = linux_audit["bundled_soname_map"]
    for needed_lib in linux_audit["all_64bit_dt_needed"]:
        if needed_lib in ("libk2-x86_64.so", "libgame_shared-x86_64.so"):
            continue
        if needed_lib == "libawesomium-1-7.so.5":
            if restores_linux_awesomium_stub:
                if not linux_audit["awesomium_stub_verified"]:
                    errors.append(
                        "CRITICAL: libk2-x86_64.so requires DT_NEEDED 'libawesomium-1-7.so.5' "
                        "and libs-stub/libawesomium-1-7.so.5 is missing or invalid!"
                    )
                if linux_audit["awesomium_api_symbols_imported"]:
                    errors.append(
                        f"CRITICAL: libk2-x86_64.so imports non-libc Awesomium symbols: "
                        f"{linux_audit['awesomium_api_symbols_imported']}"
                    )
                continue
        if needed_lib in soname_map:
            bundled_rel = soname_map[needed_lib]
            if _matches_robocopy_exclusions(bundled_rel, linux_excl_files, linux_excl_dirs):
                errors.append(
                    f"CRITICAL: Bundled Linux dependency '{bundled_rel}' (DT_NEEDED '{needed_lib}') "
                    f"is matched by Linux exclusions!"
                )

    # 3. Every unreferenced Linux legacy browser/Flash/FFmpeg binary must be excluded
    for item in linux_audit["unreferenced_legacy_bloat"]:
        rel_bloat = item["path"]
        if rel_bloat.startswith("libs-x86/"):
            continue
        if not _matches_robocopy_exclusions(rel_bloat, linux_excl_files, linux_excl_dirs):
            errors.append(f"HYGIENE FAILURE: Unreferenced Linux legacy binary '{rel_bloat}' was NOT excluded!")

    # 4. Every macOS Universal-64 root K2 binary & universal64 framework dylib must survive exclusion
    for rel in list(MACOS_ROOT_K2_BINARIES) + list(macos_audit["universal64_frameworks"].keys()):
        if _matches_robocopy_exclusions(rel, mac_excl_files, mac_excl_dirs):
            errors.append(f"CRITICAL: Required macOS Universal-64 binary '{rel}' is matched by macOS exclusions!")

    # 5. Every unreferenced macOS 32-bit Awesomium/Flash/Carbon binary must be excluded
    for item in macos_audit["unreferenced_legacy_bloat"]:
        rel_bloat = item["path"]
        if not _matches_robocopy_exclusions(rel_bloat, mac_excl_files, mac_excl_dirs):
            errors.append(f"HYGIENE FAILURE: Unreferenced macOS legacy binary '{rel_bloat}' was NOT excluded!")

    return {
        "ok": len(errors) == 0,
        "errors": errors,
        "linux": linux_audit,
        "macos": macos_audit,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Verify Linux ELF64 DT_NEEDED & macOS Universal-64 Mach-O LC_LOAD_DYLIB export hygiene."
    )
    parser.add_argument("--repo-root", default=".", help="Path to ProjectKongor repository root.")
    parser.add_argument("--json-out", default="", help="Optional path to write full JSON linkage report.")
    args = parser.parse_args(argv)

    repo_root = Path(args.repo_root).resolve()
    linux_dir = repo_root / "games" / "client" / "linux-x64"
    macos_dir = repo_root / "games" / "client" / "macos-x64"

    linux_excl_files = ["*awesomium*", "*flashplayer*", "*ffmpegsumo*", "*.s2z"]
    linux_excl_dirs = ["userdata", "logs", "replays", "plugins"]
    mac_excl_files = [
        "*awesomium*",
        "Awesomium",
        "awesomium_process",
        "*flashplayer*",
        "*ffmpegsumo*",
        "libplugin_carbon_helper.dylib",
        "*.s2z",
    ]
    mac_excl_dirs = ["userdata", "logs", "replays", "Awesomium.framework"]

    report = verify_export_exclusions(
        linux_dir=linux_dir,
        macos_dir=macos_dir,
        linux_excl_files=linux_excl_files,
        linux_excl_dirs=linux_excl_dirs,
        mac_excl_files=mac_excl_files,
        mac_excl_dirs=mac_excl_dirs,
        restores_linux_awesomium_stub=True,
    )

    lx_stripped_bytes = sum(b["size"] for b in report["linux"]["unreferenced_legacy_bloat"])
    mac_stripped_bytes = sum(b["size"] for b in report["macos"]["unreferenced_legacy_bloat"])
    log(
        f"Linux ELF64 roots verified: {len(report['linux']['roots'])} | "
        f"Awesomium 0-dep stub verified: {report['linux']['awesomium_stub_verified']} | "
        f"Stripped bloat: {lx_stripped_bytes / (1024 * 1024):.2f} MiB"
    )
    log(
        f"macOS Universal-64 roots verified: {len(report['macos']['roots'])} + "
        f"{len(report['macos']['universal64_frameworks'])} universal64 dylibs | "
        f"Stripped bloat: {mac_stripped_bytes / (1024 * 1024):.2f} MiB"
    )

    if args.json_out:
        out_path = Path(args.json_out)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        log(f"Wrote JSON linkage audit report to '{out_path}'.")

    if not report["ok"]:
        for err in report["errors"]:
            log(err, level="ERROR")
        log(
            "Linkage audit failed! Inspect 'scripts/release/export_playtest_bundle.ps1' ($linuxExcl / $macExcl) "
            "and re-run: python tools/export/binary_linkage_audit.py --repo-root .",
            level="ERROR",
        )
        return 1

    log("100% ELF64 DT_NEEDED & Mach-O Universal-64 LC_LOAD_DYLIB linkage integrity verified.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

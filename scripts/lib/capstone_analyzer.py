# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Capstone x86_64 Static Prologue & Semantic Instruction Analyzer (Options M, P, X).

Provides:
1. Automated hook prologue boundary sizing (>= 14 bytes for 64-bit FF 25 jmp) and
   RIP-relative displacement hazard detection (`[rip + disp32]`, relative `call`/`jmp`/`jcc`)
   for `InstallManagedInlineHook64` (`Option P`).
2. `cs.detail = True` + `cs_insn.regs_access()` register read/write extraction and
   semantic instruction group classification (`CALL`, `JUMP`, `RET`, `SSE`, `AVX`)
   for Evidence Dossier v2 and cleanroom assembly parity (`Option M`).
3. `cs.skipdata = True` resilient disassembly across inline jump-table data embedded in
   `.text` (`Option X`).
"""

from __future__ import annotations

from pathlib import Path
from typing import Any, Union

import capstone
from capstone import x86

from scripts.lib.pe_address_math import (
    DEFAULT_X64_IMAGE_BASE,
    parse_pe_headers,
    read_bytes_at_rva,
    rva_to_file_offset,
)

MIN_INLINE_HOOK_DETOUR_BYTES = 14


def _parse_rva_int(rva: Union[int, str]) -> int:
    if isinstance(rva, int):
        return rva
    clean = rva.strip()
    if clean.lower().startswith("0x"):
        return int(clean, 16)
    return int(clean, 16)


def analyze_bytes(
    raw_bytes: bytes,
    *,
    base_address: int = DEFAULT_X64_IMAGE_BASE,
    min_detour_bytes: int = MIN_INLINE_HOOK_DETOUR_BYTES,
    skipdata: bool = True,
) -> dict[str, Any]:
    """
    Disassembles an x86_64 instruction stream and computes prologue hook boundaries,
    RIP-relative hazards, register access sets, and semantic groups.
    """
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    if skipdata:
        md.skipdata = True

    instructions: list[dict[str, Any]] = []
    recommended_stolen_bytes: int | None = None
    rip_relative_hazards: list[dict[str, Any]] = []
    all_regs_read: set[str] = set()
    all_regs_write: set[str] = set()
    all_semantic_groups: set[str] = set()
    skipped_data_regions: list[dict[str, Any]] = []

    for insn in md.disasm(raw_bytes, base_address):
        offset = int(insn.address - base_address)
        size = int(insn.size)
        end_offset = offset + size
        bytes_hex = " ".join(f"{b:02X}" for b in insn.bytes)

        if insn.id == 0:
            # CS_OPT_SKIPDATA pseudo-instruction (.byte ...)
            skipped_data_regions.append(
                {
                    "offset": offset,
                    "size": size,
                    "address": f"0x{insn.address:X}",
                    "bytes_hex": bytes_hex,
                }
            )
            instructions.append(
                {
                    "offset": offset,
                    "size": size,
                    "address": f"0x{insn.address:X}",
                    "mnemonic": insn.mnemonic,
                    "op_str": insn.op_str,
                    "bytes_hex": bytes_hex,
                    "groups": ["SKIPDATA"],
                    "regs_read": [],
                    "regs_write": [],
                    "is_rip_relative": False,
                    "is_relative_branch": False,
                }
            )
            continue

        regs_r_ids, regs_w_ids = insn.regs_access()
        regs_read = sorted(md.reg_name(r) for r in regs_r_ids if md.reg_name(r))
        regs_write = sorted(md.reg_name(r) for r in regs_w_ids if md.reg_name(r))
        groups = sorted(md.group_name(g).upper() for g in insn.groups if md.group_name(g))

        all_regs_read.update(regs_read)
        all_regs_write.update(regs_write)
        all_semantic_groups.update(groups)

        is_rip_rel = False
        rip_disp = 0
        for op in insn.operands:
            if op.type == x86.X86_OP_MEM and op.mem.base == x86.X86_REG_RIP:
                is_rip_rel = True
                rip_disp = int(op.mem.disp)
                break

        is_rel_branch = any(g in ("JUMP", "CALL", "BRANCH_RELATIVE") for g in groups) and any(
            op.type == x86.X86_OP_IMM for op in insn.operands
        )

        insn_record = {
            "offset": offset,
            "size": size,
            "address": f"0x{insn.address:X}",
            "mnemonic": insn.mnemonic,
            "op_str": insn.op_str,
            "bytes_hex": bytes_hex,
            "groups": groups,
            "regs_read": regs_read,
            "regs_write": regs_write,
            "is_rip_relative": is_rip_rel,
            "is_relative_branch": is_rel_branch,
        }
        instructions.append(insn_record)

        if recommended_stolen_bytes is None and end_offset >= min_detour_bytes:
            recommended_stolen_bytes = end_offset

    if recommended_stolen_bytes is None:
        recommended_stolen_bytes = len(raw_bytes)

    for insn_rec in instructions:
        if insn_rec["offset"] < recommended_stolen_bytes:
            if insn_rec["is_rip_relative"] or insn_rec["is_relative_branch"]:
                hazard_kind = "RIP_MEM_DISP32" if insn_rec["is_rip_relative"] else "REL_BRANCH_DISP32"
                rip_relative_hazards.append(
                    {
                        "offset": insn_rec["offset"],
                        "size": insn_rec["size"],
                        "address": insn_rec["address"],
                        "instruction": f"{insn_rec['mnemonic']} {insn_rec['op_str']}".strip(),
                        "bytes_hex": insn_rec["bytes_hex"],
                        "hazard_type": hazard_kind,
                    }
                )

    stolen_slice = raw_bytes[:recommended_stolen_bytes]
    prologue_hex = " ".join(f"{b:02X}" for b in stolen_slice)
    c_array_initializer = ", ".join(f"0x{b:02X}" for b in stolen_slice)

    return {
        "base_address": f"0x{base_address:X}",
        "min_detour_bytes": min_detour_bytes,
        "recommended_stolen_bytes": recommended_stolen_bytes,
        "prologue_hex": prologue_hex,
        "c_array_initializer": f"{{ {c_array_initializer} }}",
        "is_safe_for_unrelocated_trampoline": len(rip_relative_hazards) == 0,
        "rip_relative_hazards": rip_relative_hazards,
        "regs_read": sorted(all_regs_read),
        "regs_write": sorted(all_regs_write),
        "semantic_groups": sorted(all_semantic_groups),
        "skipped_data_regions": skipped_data_regions,
        "instruction_boundaries": instructions,
    }


def analyze_prologue(
    binary_path: Union[str, Path],
    rva: Union[int, str],
    *,
    max_bytes: int = 64,
    min_detour_bytes: int = MIN_INLINE_HOOK_DETOUR_BYTES,
) -> dict[str, Any]:
    """
    Reads `max_bytes` from `binary_path` at `rva` and performs full Capstone prologue
    sizing, RIP-relative hazard analysis, and register/group extraction.
    """
    path = Path(binary_path)
    rva_int = _parse_rva_int(rva)
    pe_info = parse_pe_headers(path)
    raw = read_bytes_at_rva(path, rva_int, max_bytes)
    va = pe_info.image_base + rva_int

    result = analyze_bytes(
        raw,
        base_address=va,
        min_detour_bytes=min_detour_bytes,
        skipdata=True,
    )
    result["binary_name"] = path.name
    result["rva"] = f"0x{rva_int:X}"
    return result


def verify_cleanroom_asm_parity(
    retail_bytes: bytes,
    cleanroom_bytes: bytes,
    *,
    base_address: int = DEFAULT_X64_IMAGE_BASE,
) -> dict[str, Any]:
    """
    Compares a retail instruction stream against a compiled cleanroom instruction stream
    using Capstone `regs_access()` and semantic instruction groups (`Option M`).
    """
    retail_info = analyze_bytes(retail_bytes, base_address=base_address, skipdata=True)
    clean_info = analyze_bytes(cleanroom_bytes, base_address=base_address, skipdata=True)

    retail_groups = set(retail_info["semantic_groups"])
    clean_groups = set(clean_info["semantic_groups"])
    missing_groups = sorted(retail_groups - clean_groups)
    added_groups = sorted(clean_groups - retail_groups)

    # Check high-signal ABI/control-flow groups (CALL, RET, JUMP, SSE1, SSE2, AVX)
    critical_groups = {"CALL", "RET", "JUMP", "SSE1", "SSE2", "AVX"}
    critical_group_mismatches = sorted((retail_groups ^ clean_groups) & critical_groups)

    return {
        "is_semantically_compatible": len(critical_group_mismatches) == 0,
        "critical_group_mismatches": critical_group_mismatches,
        "missing_groups": missing_groups,
        "added_groups": added_groups,
        "retail_regs_write": retail_info["regs_write"],
        "cleanroom_regs_write": clean_info["regs_write"],
        "retail_instruction_count": len(retail_info["instruction_boundaries"]),
        "cleanroom_instruction_count": len(clean_info["instruction_boundaries"]),
    }


def find_function_bounds(
    binary_path: Union[str, Path],
    rva: Union[int, str],
) -> dict[str, Any]:
    """
    Option E (.pdata + Leaf-Function Hole Safeguard):
    Looks up `rva` in the PE `.pdata` exception table via binary search; if absent (leaf function),
    scans forward up to 256 bytes for `RET` (`C3`) + `CC`/`90` alignment padding.
    """
    import struct as _struct

    path = Path(binary_path)
    rva_int = _parse_rva_int(rva)
    pe_info = parse_pe_headers(path)

    if pe_info.pdata_rva > 0 and pe_info.pdata_size >= 12:
        try:
            pdata_off = rva_to_file_offset(pe_info, pe_info.pdata_rva)
            with open(path, "rb") as f:
                f.seek(pdata_off)
                pdata_bytes = f.read(pe_info.pdata_size)
            count = len(pdata_bytes) // 12
            lo, hi = 0, count - 1
            while lo <= hi:
                mid = (lo + hi) >> 1
                b_rva, e_rva, _u = _struct.unpack_from("<III", pdata_bytes, mid * 12)
                if b_rva <= rva_int < e_rva:
                    return {
                        "start_rva": hex(b_rva),
                        "end_rva": hex(e_rva),
                        "size": e_rva - b_rva,
                        "bounds_source": "pdata",
                    }
                if b_rva < rva_int:
                    lo = mid + 1
                else:
                    hi = mid - 1
        except (_struct.error, ValueError):
            hi = -1

    # Leaf function fallback: scan up to 256 bytes for C3 followed by CC or 90
    raw = read_bytes_at_rva(path, rva_int, 256)
    end_delta = 64
    for idx in range(len(raw) - 1):
        if raw[idx] == 0xC3 and raw[idx + 1] in (0xCC, 0x90):
            end_delta = idx + 1
            break

    return {
        "start_rva": hex(rva_int),
        "end_rva": hex(rva_int + end_delta),
        "size": end_delta,
        "bounds_source": "heuristic",
    }


def generate_shortest_unique_sig(
    binary_path: Union[str, Path],
    rva: Union[int, str],
    max_bytes: int = 48,
) -> dict[str, Any]:
    """
    Option M + Option X (_sigmaker.py pattern):
    Generates a wildcard-masked prologue signature preserving immediate constants while
    masking relocatable RIP/rel32 displacements (`??`).
    """
    path = Path(binary_path)
    rva_int = _parse_rva_int(rva)
    pe_info = parse_pe_headers(path)
    raw = read_bytes_at_rva(path, rva_int, max_bytes)
    va = pe_info.image_base + rva_int

    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    md.skipdata = True
    tokens: list[str] = []
    insn_count = 0
    for insn in md.disasm(raw, va):
        if insn.id == 0:
            break
        ibytes = [f"{b:02X}" for b in insn.bytes]
        if len(ibytes) == 5 and ibytes[0] in ("E8", "E9"):
            ibytes[1:5] = ["??", "??", "??", "??"]
        elif len(ibytes) == 6 and ibytes[0] == "0F" and ibytes[1][:1] == "8":
            ibytes[2:6] = ["??", "??", "??", "??"]
        else:
            for op in insn.operands:
                if op.type == x86.X86_OP_MEM and op.mem.base == x86.X86_REG_RIP:
                    d_off = int(getattr(insn, "disp_offset", 0))
                    d_sz = int(getattr(insn, "disp_size", 0))
                    if d_off > 0 and d_sz == 4 and (d_off + 4) <= len(ibytes):
                        ibytes[d_off : d_off + 4] = ["??", "??", "??", "??"]
                    break
        tokens.extend(ibytes)
        insn_count += 1
        if len(tokens) >= 16:
            break

    return {
        "rva": hex(rva_int),
        "signature": " ".join(tokens),
        "byte_length": len(tokens),
        "instruction_count": insn_count,
    }


# ============================================================================
# Phase 2 (#41 ghidra-symbol-risk-score & #46 entropy-and-crypto-sbox-scan)
# ============================================================================

CRYPTO_AND_COMPRESSION_SIGNATURES: list[dict[str, Any]] = [
    {
        "name": "CRC32_IEEE",
        "category": "checksum",
        "patterns": [b"\x20\x83\xb8\xed", b"\x96\x30\x07\x77"],
        "description": "IEEE 802.3 CRC32 polynomial / lookup table (0xEDB88320 / 0x77073096)",
    },
    {
        "name": "AES_RCON_SBOX",
        "category": "crypto",
        "patterns": [b"\x63\x7c\x77\x7b\xf2\x6b\x6f\xc5"],
        "description": "Rijndael / AES forward S-box header",
    },
    {
        "name": "SHA256_K",
        "category": "hash",
        "patterns": [b"\x98\x2f\x8a\x42", b"\x91\x44\x37\x71"],
        "description": "SHA-256 round constant K[0..1] (0x428A2F98, 0x71374491)",
    },
    {
        "name": "MD5_INIT_T",
        "category": "hash",
        "patterns": [b"\x78\xa4\x6a\xd7", b"\x56\xb7\xc7\xe8"],
        "description": "MD5 sine-table constant T[1..2] (0xD76AA478, 0xE8C7B756)",
    },
    {
        "name": "ZSTD_MAGIC",
        "category": "compression",
        "patterns": [b"\x28\xb5\x2f\xfd"],
        "description": "Zstandard frame magic (0xFD2FB528)",
    },
    {
        "name": "LZ4_FRAME_MAGIC",
        "category": "compression",
        "patterns": [b"\x04\x22\x4d\x18"],
        "description": "LZ4 frame magic (0x184D2204)",
    },
    {
        "name": "ZLIB_DEFLATE",
        "category": "compression",
        "patterns": [b"\x00\x10\x08\x18\x04\x14\x0c\x1c", b"\x78\x9c\xec\xbd"],
        "description": "Zlib/Deflate fixed Huffman table or stream header",
    },
    {
        "name": "TEA_XTEA_DELTA",
        "category": "crypto",
        "patterns": [b"\xb9\x79\x37\x9e"],
        "description": "TEA / XTEA / XXTEA golden-ratio delta (0x9E3779B9)",
    },
    {
        "name": "SHA1_H4",
        "category": "hash",
        "patterns": [b"\xf0\xe1\xd2\xc3"],
        "description": "SHA-1 initialization vector H4 (0xC3D2E1F0)",
    },
    {
        "name": "FNV1A_64",
        "category": "hash",
        "patterns": [b"\x25\x23\x22\x84\xe4\x9c\xf2\xcb", b"\xb3\x01\x00\x00\x00\x01\x00\x00"],
        "description": "FNV-1a 64-bit offset basis / prime (0xCBF29CE484222325 / 0x100000001B3)",
    },
]


def compute_shannon_entropy(data: bytes) -> dict[str, Any]:
    """
    Computes Shannon byte entropy in [0.0, 8.0] bits/byte and classifies the slice (#46).
    """
    import math

    if not data:
        return {
            "entropy_bits": 0.0,
            "byte_length": 0,
            "classification": "empty",
        }

    length = len(data)
    counts = [0] * 256
    for b in data:
        counts[b] += 1

    entropy = 0.0
    for c in counts:
        if c > 0:
            p = c / length
            entropy -= p * math.log2(p)

    entropy_rounded = round(entropy, 4)
    if entropy_rounded < 1.5:
        classification = "sparse_or_zero_padding"
    elif entropy_rounded <= 6.8:
        classification = "native_code_or_structured_data"
    else:
        classification = "compressed_or_encrypted_high_entropy"

    return {
        "entropy_bits": entropy_rounded,
        "byte_length": length,
        "classification": classification,
    }


def scan_crypto_and_compression_signatures(data: bytes) -> list[dict[str, Any]]:
    """
    Scans a binary byte buffer for 10 well-known crypto, checksum, hash, and compression
    constants/S-boxes (#46 entropy-and-crypto-sbox-scan).
    """
    if not data:
        return []

    findings: list[dict[str, Any]] = []
    for sig in CRYPTO_AND_COMPRESSION_SIGNATURES:
        for pat in sig["patterns"]:
            idx = data.find(pat)
            if idx != -1:
                findings.append(
                    {
                        "name": sig["name"],
                        "category": sig["category"],
                        "offset": f"0x{idx:X}",
                        "pattern_hex": pat.hex().upper(),
                        "description": sig["description"],
                    }
                )
                break
    return findings


def compute_function_risk_score(
    raw_bytes: bytes | None = None,
    decompiled_c: str | None = None,
) -> dict[str, Any]:
    """
    Computes a deterministic [0..100] reverse-engineering & memory-safety risk score (#41)
    from x86_64 instructions and/or decompiled C code.
    """
    import re as _re

    score = 0
    factors: list[str] = []

    if raw_bytes:
        info = analyze_bytes(raw_bytes, skipdata=True)
        insns = info.get("instruction_boundaries", [])
        indirect_branches = 0
        stack_allocs = 0
        for insn in insns:
            mnem = str(insn.get("mnemonic", "")).lower()
            op_str = str(insn.get("op_str", "")).lower()
            if mnem in ("call", "jmp") and ("[" in op_str or op_str in ("rax", "rcx", "rdx", "rbx", "rsi", "rdi", "r8", "r9", "r10", "r11")):
                indirect_branches += 1
            if mnem == "sub" and op_str.startswith("rsp,"):
                stack_allocs += 1

        if indirect_branches > 0:
            pts = min(30, indirect_branches * 10)
            score += pts
            factors.append(f"indirect_call_or_vtable({indirect_branches})[+{pts}]")
        if len(info.get("skipped_data_regions", [])) > 0:
            score += 15
            factors.append("inline_skipdata_or_jumptable[+15]")
        if len(info.get("rip_relative_hazards", [])) > 0:
            score += 10
            factors.append("prologue_rip_relative_hazard[+10]")

    if decompiled_c:
        unsafe_calls = _re.findall(
            r"\b(?:memcpy|memmove|strcpy|strncpy|sprintf|vsprintf|_alloca_probe|alloca|realloc)\b",
            decompiled_c,
        )
        if unsafe_calls:
            pts = min(35, len(unsafe_calls) * 15)
            score += pts
            factors.append(f"unsafe_memory_primitives({sorted(set(unsafe_calls))})[+{pts}]")

        raw_ptr_casts = _re.findall(r"\*\s*\(\s*[a-zA-Z0-9_]+\s*\*+\s*\)\s*\(", decompiled_c)
        if raw_ptr_casts:
            pts = min(25, len(raw_ptr_casts) * 5)
            score += pts
            factors.append(f"raw_pointer_offset_casts({len(raw_ptr_casts)})[+{pts}]")

        artifacts = _re.findall(
            r"\b(?:in_RAX|in_RCX|in_RDX|in_R8|in_R9|extraout_[A-Za-z0-9_]+|unaff_[A-Za-z0-9_]+|BADSPACEBASE|halt_baddata)\b",
            decompiled_c,
        )
        if artifacts:
            pts = min(25, len(artifacts) * 10)
            score += pts
            factors.append(f"decompiler_artifacts({sorted(set(artifacts))})[+{pts}]")

    score = min(100, score)
    if score >= 70:
        tier = "CRITICAL"
    elif score >= 45:
        tier = "HIGH"
    elif score >= 20:
        tier = "MEDIUM"
    else:
        tier = "LOW"

    return {
        "risk_score": score,
        "risk_tier": tier,
        "factors": factors,
    }


def wrap_framed_payload(payload_text: str, token_uuid: str | None = None) -> tuple[str, str]:
    """
    Phase 3b (#58 uuid-framing-sentinel):
    Wraps structured subprocess stdout in a unique multi-token UUID sentinel
    (`<<<KONGOR_PAYLOAD_BEGIN_<UUID>>>>` .. `<<<KONGOR_PAYLOAD_END_<UUID>>>>`)
    so JVM/IDA startup banners and trailing log lines cannot corrupt JSON extraction.
    """
    import uuid as _uuid

    uid = (token_uuid or str(_uuid.uuid4())).strip()
    begin_tag = f"<<<KONGOR_PAYLOAD_BEGIN_{uid}>>>"
    end_tag = f"<<<KONGOR_PAYLOAD_END_{uid}>>>"
    framed = f"{begin_tag}\n{payload_text}\n{end_tag}"
    return framed, uid


def extract_framed_payload(stream_text: str, token_uuid: str) -> str:
    """
    Phase 3b (#58 uuid-framing-sentinel):
    Extracts the exact payload bounded by `<<<KONGOR_PAYLOAD_BEGIN_<UUID>>>>`
    and `<<<KONGOR_PAYLOAD_END_<UUID>>>>`, ignoring surrounding stdout chatter.
    Raises ValueError if the sentinel pair is missing or malformed.
    """
    uid = token_uuid.strip()
    begin_tag = f"<<<KONGOR_PAYLOAD_BEGIN_{uid}>>>"
    end_tag = f"<<<KONGOR_PAYLOAD_END_{uid}>>>"
    start_idx = stream_text.find(begin_tag)
    if start_idx < 0:
        raise ValueError(f"Missing framing begin sentinel: {begin_tag}")
    body_start = start_idx + len(begin_tag)
    end_idx = stream_text.find(end_tag, body_start)
    if end_idx < 0:
        raise ValueError(f"Missing framing end sentinel: {end_tag}")
    return stream_text[body_start:end_idx].strip()

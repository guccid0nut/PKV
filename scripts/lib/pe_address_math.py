# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Canonical PE32/PE32+ Address Math Library for Project Kongor (Option CC).

Provides deterministic translation between Relative Virtual Addresses (RVA),
Virtual Addresses (VA), and raw on-disk PE file offsets across all 7 core K2
binaries (k2_x64.dll, cgame_x64.dll, game_x64.dll, game_shared_x64.dll,
vid_d3d9_x64.dll, hon_x64.exe, version.dll).
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import struct
from typing import Union

DEFAULT_X64_IMAGE_BASE = 0x180000000
MZ_MAGIC = 0x5A4D
PE_SIGNATURE = 0x00004550
PE32_MAGIC = 0x10B
PE32_PLUS_MAGIC = 0x20B


@dataclass(frozen=True)
class PESectionHeader:
    name: str
    virtual_size: int
    virtual_address: int
    raw_size: int
    raw_ptr: int
    characteristics: int

    @property
    def mapped_span(self) -> int:
        return max(self.virtual_size, self.raw_size)


@dataclass(frozen=True)
class PEHeaderInfo:
    image_base: int
    time_date_stamp: int
    size_of_image: int
    pdata_rva: int
    pdata_size: int
    sections: tuple[PESectionHeader, ...]


BinarySource = Union[str, Path, bytes, bytearray, memoryview, PEHeaderInfo]


def parse_pe_headers(source: Union[str, Path, bytes, bytearray, memoryview]) -> PEHeaderInfo:
    """
    Parses DOS, COFF, Optional, and Section headers from a PE32/PE32+ file or byte buffer.
    Only reads the first 4 KiB when given a file path for sub-millisecond performance.
    """
    if isinstance(source, (str, Path)):
        path = Path(source)
        if not path.is_file():
            raise FileNotFoundError(
                f"PE binary not found at '{path}'. Verify path or check games/client/."
            )
        with open(path, "rb") as f:
            header_bytes = f.read(4096)
    else:
        header_bytes = bytes(source[:4096])

    if len(header_bytes) < 0x40:
        raise ValueError("Buffer too small to contain a valid DOS header (< 64 bytes).")

    e_magic, e_lfanew = struct.unpack_from("<H58xI", header_bytes, 0)
    if e_magic != MZ_MAGIC:
        raise ValueError(f"Invalid DOS MZ magic: 0x{e_magic:04X} (expected 0x5A4D).")

    if e_lfanew < 0x40 or e_lfanew + 24 > len(header_bytes):
        raise ValueError(f"Invalid e_lfanew offset: 0x{e_lfanew:X}.")

    pe_sig, _machine, num_sections, time_date_stamp, _sym_ptr, _num_syms, size_of_opt, _chars = (
        struct.unpack_from("<IHHIIIHH", header_bytes, e_lfanew)
    )
    if pe_sig != PE_SIGNATURE:
        raise ValueError(f"Invalid PE signature: 0x{pe_sig:08X} (expected 0x00004550 'PE\\0\\0').")

    opt_offset = e_lfanew + 24
    if opt_offset + size_of_opt > len(header_bytes):
        raise ValueError("PE Optional Header extends beyond 4 KiB header read window.")

    opt_magic = struct.unpack_from("<H", header_bytes, opt_offset)[0]
    if opt_magic == PE32_PLUS_MAGIC:
        image_base = struct.unpack_from("<Q", header_bytes, opt_offset + 24)[0]
        size_of_image = struct.unpack_from("<I", header_bytes, opt_offset + 56)[0]
        num_rva_and_sizes = struct.unpack_from("<I", header_bytes, opt_offset + 108)[0]
        data_dir_offset = opt_offset + 112
    elif opt_magic == PE32_MAGIC:
        image_base = struct.unpack_from("<I", header_bytes, opt_offset + 28)[0]
        size_of_image = struct.unpack_from("<I", header_bytes, opt_offset + 56)[0]
        num_rva_and_sizes = struct.unpack_from("<I", header_bytes, opt_offset + 92)[0]
        data_dir_offset = opt_offset + 96
    else:
        raise ValueError(f"Unsupported PE Optional Header magic: 0x{opt_magic:04X}.")

    pdata_rva = 0
    pdata_size = 0
    if num_rva_and_sizes > 3 and (data_dir_offset + 32) <= (opt_offset + size_of_opt):
        pdata_rva, pdata_size = struct.unpack_from("<II", header_bytes, data_dir_offset + (3 * 8))

    sec_table_offset = opt_offset + size_of_opt
    sections: list[PESectionHeader] = []
    for i in range(num_sections):
        s_off = sec_table_offset + (i * 40)
        if s_off + 40 > len(header_bytes):
            break
        raw_name, v_size, v_addr, r_size, r_ptr, _reloc, _lines, _nreloc, _nlines, chars = (
            struct.unpack_from("<8sIIIIIIHHI", header_bytes, s_off)
        )
        sec_name = raw_name.split(b"\x00", 1)[0].decode("ascii", errors="replace")
        sections.append(
            PESectionHeader(
                name=sec_name,
                virtual_size=v_size,
                virtual_address=v_addr,
                raw_size=r_size,
                raw_ptr=r_ptr,
                characteristics=chars,
            )
        )

    return PEHeaderInfo(
        image_base=image_base,
        time_date_stamp=time_date_stamp,
        size_of_image=size_of_image,
        pdata_rva=pdata_rva,
        pdata_size=pdata_size,
        sections=tuple(sections),
    )


def _ensure_header_info(source: BinarySource) -> PEHeaderInfo:
    if isinstance(source, PEHeaderInfo):
        return source
    return parse_pe_headers(source)


def rva_to_file_offset(source: BinarySource, rva: int) -> int:
    """Translates a Relative Virtual Address (RVA) to a raw file offset."""
    if rva < 0:
        raise ValueError(f"RVA cannot be negative: {rva}")
    info = _ensure_header_info(source)
    for sec in info.sections:
        if sec.virtual_address <= rva < (sec.virtual_address + sec.mapped_span):
            delta = rva - sec.virtual_address
            if delta >= sec.raw_size and sec.raw_size > 0:
                raise ValueError(
                    f"RVA 0x{rva:X} falls into uninitialized BSS tail of section '{sec.name}' "
                    f"(delta=0x{delta:X} >= raw_size=0x{sec.raw_size:X})."
                )
            return sec.raw_ptr + delta
    raise ValueError(f"RVA 0x{rva:X} does not map to any section in the PE binary.")


def file_offset_to_rva(source: BinarySource, file_offset: int) -> int:
    """Translates a raw file offset to a Relative Virtual Address (RVA)."""
    if file_offset < 0:
        raise ValueError(f"file_offset cannot be negative: {file_offset}")
    info = _ensure_header_info(source)
    for sec in info.sections:
        if sec.raw_size > 0 and sec.raw_ptr <= file_offset < (sec.raw_ptr + sec.raw_size):
            return sec.virtual_address + (file_offset - sec.raw_ptr)
    raise ValueError(f"File offset 0x{file_offset:X} does not map to any raw section in the PE binary.")


def rva_to_va(rva: int, image_base: int = DEFAULT_X64_IMAGE_BASE) -> int:
    """Converts an RVA to a 64-bit Virtual Address (VA) using the specified ImageBase."""
    if rva < 0:
        raise ValueError(f"RVA cannot be negative: {rva}")
    return image_base + rva


def va_to_rva(va: int, image_base: int = DEFAULT_X64_IMAGE_BASE) -> int:
    """Converts a 64-bit Virtual Address (VA) to an RVA using the specified ImageBase."""
    if va < image_base:
        raise ValueError(f"VA 0x{va:X} is below ImageBase 0x{image_base:X}.")
    return va - image_base


def read_bytes_at_rva(binary_path: Union[str, Path], rva: int, length: int) -> bytes:
    """Seeks directly to the file offset for `rva` and reads `length` bytes."""
    path = Path(binary_path)
    info = parse_pe_headers(path)
    file_off = rva_to_file_offset(info, rva)
    with open(path, "rb") as f:
        f.seek(file_off)
        data = f.read(length)
    if len(data) < length:
        raise ValueError(
            f"Short read at RVA 0x{rva:X} (file offset 0x{file_off:X}): requested {length} bytes, got {len(data)}."
        )
    return data


def _header_rva_to_file_offset(self: PEHeaderInfo, rva: int) -> int | None:
    try:
        return rva_to_file_offset(self, rva)
    except ValueError:
        return None


PEHeaderInfo.rva_to_file_offset = _header_rva_to_file_offset  # type: ignore[attr-defined]
parse_pe_layout = parse_pe_headers


def int_convert(value: Union[int, str]) -> dict[str, Union[int, str, float]]:
    """
    Option CC: Deterministically computes hex, decimal, signed 32/64-bit two's-complement,
    and IEEE-754 float/double bit casts for any integer or hex literal.
    """
    val = int(str(value).strip(), 0) if isinstance(value, str) else int(value)
    u32 = val & 0xFFFFFFFF
    u64 = val & 0xFFFFFFFFFFFFFFFF
    s32 = u32 - 0x100000000 if (u32 & 0x80000000) else u32
    s64 = u64 - 0x10000000000000000 if (u64 & 0x8000000000000000) else u64
    f32 = struct.unpack("<f", struct.pack("<I", u32))[0]
    f64 = struct.unpack("<d", struct.pack("<Q", u64))[0]
    return {
        "input": str(value),
        "hex_u32": f"0x{u32:08x}",
        "hex_u64": f"0x{u64:016x}",
        "unsigned_32": u32,
        "signed_32": s32,
        "signed_32_hex": f"-0x{abs(s32):x}" if s32 < 0 else f"0x{s32:x}",
        "unsigned_64": u64,
        "signed_64": s64,
        "float32_bitcast": round(f32, 7) if (f32 == f32 and abs(f32) < 1e30) else str(f32),
        "float64_bitcast": round(f64, 12) if (f64 == f64 and abs(f64) < 1e100) else str(f64),
    }


def convert_pe_address(
    source: BinarySource,
    value: Union[int, str],
    kind: str = "auto",
) -> dict[str, Union[int, str, None]]:
    """
    Option CC: Deterministically maps an address across VA <-> RVA <-> FileOffset and
    identifies its enclosing PE section.
    """
    info = _ensure_header_info(source)
    addr = int(str(value).strip(), 0) if isinstance(value, str) else int(value)
    if addr < 0:
        return {"error": f"Negative value {value} is a displacement, not a PE address."}

    mode = kind.lower().strip()
    if mode == "auto":
        mode = "va" if addr >= info.image_base else "rva"

    if mode == "va":
        rva_val = va_to_rva(addr, info.image_base)
        va_val = addr
        fo_val = _header_rva_to_file_offset(info, rva_val)
    elif mode == "file_offset":
        fo_val = addr
        rva_val = file_offset_to_rva(info, addr)
        va_val = rva_to_va(rva_val, info.image_base)
    else:
        rva_val = addr
        va_val = rva_to_va(rva_val, info.image_base)
        fo_val = _header_rva_to_file_offset(info, rva_val)

    sec_name = None
    for sec in info.sections:
        if sec.virtual_address <= rva_val < (sec.virtual_address + sec.mapped_span):
            sec_name = sec.name
            break

    return {
        "resolved_kind": mode,
        "image_base": hex(info.image_base),
        "va": hex(va_val),
        "rva": hex(rva_val),
        "file_offset": hex(fo_val) if fo_val is not None else None,
        "section": sec_name,
    }


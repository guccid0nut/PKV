# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth Material & PBR Converter.
Parses HoN .material XML, decodes DDS textures (including RxGB normal maps),
and converts legacy specular/diffuse shaders into modern Principled BSDF v2 and glTF 2.0 PBR.
"""

from __future__ import annotations

import io
import logging
import math
import os
import struct
import xml.etree.ElementTree as ET
import zlib
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple, Union

logger = logging.getLogger("io_scene_hon.material_parser")


@dataclass
class HonMaterialData:
    """Represents resolved textures and PBR parameters for a mesh surface."""
    name: str = ""
    diffuse_texture: Optional[str] = None
    normal_texture: Optional[str] = None
    specular_texture: Optional[str] = None
    glow_texture: Optional[str] = None
    opacity_texture: Optional[str] = None
    metallic: float = 0.05
    roughness: float = 0.85
    has_alpha: bool = False
    is_additive: bool = False
    diffuse_png_bytes: Optional[bytes] = None
    normal_png_bytes: Optional[bytes] = None
    metallic_roughness_png_bytes: Optional[bytes] = None
    glow_png_bytes: Optional[bytes] = None


def _unpack_rgb565(c: int) -> Tuple[int, int, int]:
    """Unpack a 16-bit 5:6:5 integer into 8-bit (R, G, B) tuple."""
    r = ((c >> 11) & 0x1F) * 255 // 31
    g = ((c >> 5) & 0x3F) * 255 // 63
    b = (c & 0x1F) * 255 // 31
    return (r, g, b)


def decode_dds(dds_bytes: bytes) -> Tuple[bytes, int, int]:
    """
    Pure Python DDS texture decoder supporting DXT1, DXT3, DXT5, and uncompressed RGBA.
    
    Returns:
        (rgba_bytes, width, height)
    """
    if len(dds_bytes) < 128:
        raise ValueError("DDS file too small for header (less than 128 bytes)")

    magic = dds_bytes[:4]
    if magic != b"DDS ":
        raise ValueError(f"Invalid DDS magic header: {magic!r}")

    header = dds_bytes[4:128]
    (
        _size, _flags, height, width, _pitch, _depth, _mipmaps,
        *_reserved1_pfsize, pf_flags, fourcc_raw, _rgb_bits, _r_mask, _g_mask, _b_mask, _a_mask
    ) = struct.unpack("<7i44s2i4s5I", header[:104])

    fourcc = fourcc_raw.decode("ascii", errors="replace")
    logger.debug("DDS format: fourcc=%s, width=%d, height=%d, pf_flags=0x%X", fourcc, width, height, pf_flags)

    stream = io.BytesIO(dds_bytes[128:])
    rgba = bytearray(width * height * 4)

    if fourcc == "DXT1":
        _decode_dxt1(stream, rgba, width, height)
    elif fourcc == "DXT3":
        _decode_dxt3(stream, rgba, width, height)
    elif fourcc == "DXT5":
        _decode_dxt5(stream, rgba, width, height)
    else:
        # Fallback uncompressed RGBA or BGRA
        raw = stream.read(width * height * 4)
        if len(raw) == len(rgba):
            rgba[:len(raw)] = raw
        else:
            rgba[:min(len(raw), len(rgba))] = raw[:min(len(raw), len(rgba))]

    return (bytes(rgba), width, height)


def _decode_dxt1(stream: io.BytesIO, rgba: bytearray, width: int, height: int) -> None:
    for by in range(0, height, 4):
        for bx in range(0, width, 4):
            block = stream.read(8)
            if len(block) < 8:
                return
            c0, c1, c_bits = struct.unpack("<HHI", block)
            r0, g0, b0 = _unpack_rgb565(c0)
            r1, g1, b1 = _unpack_rgb565(c1)

            colors = [
                (r0, g0, b0, 255),
                (r1, g1, b1, 255),
                (0, 0, 0, 255),
                (0, 0, 0, 255)
            ]

            if c0 > c1:
                colors[2] = ((2 * r0 + r1) // 3, (2 * g0 + g1) // 3, (2 * b0 + b1) // 3, 255)
                colors[3] = ((r0 + 2 * r1) // 3, (g0 + 2 * g1) // 3, (b0 + 2 * b1) // 3, 255)
            else:
                colors[2] = ((r0 + r1) // 2, (g0 + g1) // 2, (b0 + b1) // 2, 255)
                colors[3] = (0, 0, 0, 0)

            for py in range(4):
                for px in range(4):
                    x = bx + px
                    y = by + py
                    if x < width and y < height:
                        pix_idx = py * 4 + px
                        c_idx = (c_bits >> (pix_idx * 2)) & 3
                        r, g, b, a = colors[c_idx]
                        dst_idx = (y * width + x) * 4
                        rgba[dst_idx] = r
                        rgba[dst_idx + 1] = g
                        rgba[dst_idx + 2] = b
                        rgba[dst_idx + 3] = a


def _decode_dxt3(stream: io.BytesIO, rgba: bytearray, width: int, height: int) -> None:
    for by in range(0, height, 4):
        for bx in range(0, width, 4):
            block = stream.read(16)
            if len(block) < 16:
                return
            alpha_bits, c0, c1, c_bits = struct.unpack("<QHHI", block)
            r0, g0, b0 = _unpack_rgb565(c0)
            r1, g1, b1 = _unpack_rgb565(c1)

            colors = [
                (r0, g0, b0),
                (r1, g1, b1),
                ((2 * r0 + r1) // 3, (2 * g0 + g1) // 3, (2 * b0 + b1) // 3),
                ((r0 + 2 * r1) // 3, (g0 + 2 * g1) // 3, (b0 + 2 * b1) // 3)
            ]

            for py in range(4):
                for px in range(4):
                    x = bx + px
                    y = by + py
                    if x < width and y < height:
                        pix_idx = py * 4 + px
                        a4 = (alpha_bits >> (pix_idx * 4)) & 0xF
                        alpha = (a4 << 4) | a4
                        c_idx = (c_bits >> (pix_idx * 2)) & 3
                        r, g, b = colors[c_idx]
                        dst_idx = (y * width + x) * 4
                        rgba[dst_idx] = r
                        rgba[dst_idx + 1] = g
                        rgba[dst_idx + 2] = b
                        rgba[dst_idx + 3] = alpha


def _decode_dxt5(stream: io.BytesIO, rgba: bytearray, width: int, height: int) -> None:
    for by in range(0, height, 4):
        for bx in range(0, width, 4):
            block = stream.read(16)
            if len(block) < 16:
                return
            a0 = block[0]
            a1 = block[1]
            a_bytes = block[2:8]
            a64 = int.from_bytes(a_bytes, "little")

            alphas = [0] * 8
            alphas[0] = a0
            alphas[1] = a1
            if a0 > a1:
                alphas[2] = (6 * a0 + 1 * a1) // 7
                alphas[3] = (5 * a0 + 2 * a1) // 7
                alphas[4] = (4 * a0 + 3 * a1) // 7
                alphas[5] = (3 * a0 + 4 * a1) // 7
                alphas[6] = (2 * a0 + 5 * a1) // 7
                alphas[7] = (1 * a0 + 6 * a1) // 7
            else:
                alphas[2] = (4 * a0 + 1 * a1) // 5
                alphas[3] = (3 * a0 + 2 * a1) // 5
                alphas[4] = (2 * a0 + 3 * a1) // 5
                alphas[5] = (1 * a0 + 4 * a1) // 5
                alphas[6] = 0
                alphas[7] = 255

            c0, c1, c_bits = struct.unpack("<HHI", block[8:16])
            r0, g0, b0 = _unpack_rgb565(c0)
            r1, g1, b1 = _unpack_rgb565(c1)

            colors = [
                (r0, g0, b0),
                (r1, g1, b1),
                ((2 * r0 + r1) // 3, (2 * g0 + g1) // 3, (2 * b0 + b1) // 3),
                ((r0 + 2 * r1) // 3, (g0 + 2 * g1) // 3, (b0 + 2 * b1) // 3)
            ]

            for py in range(4):
                for px in range(4):
                    x = bx + px
                    y = by + py
                    if x < width and y < height:
                        pix_idx = py * 4 + px
                        a_idx = (a64 >> (pix_idx * 3)) & 7
                        c_idx = (c_bits >> (pix_idx * 2)) & 3
                        alpha = alphas[a_idx]
                        r, g, b = colors[c_idx]
                        dst_idx = (y * width + x) * 4
                        rgba[dst_idx] = r
                        rgba[dst_idx + 1] = g
                        rgba[dst_idx + 2] = b
                        rgba[dst_idx + 3] = alpha


def decode_rxgb_normal(dds_bytes: bytes) -> Tuple[bytes, int, int]:
    """
    Decode HoN RxGB DXT5 compressed normal map texture and unpack into standard tangent-space normal map.
    
    Formula:
      nx = (alpha / 255.0 * 2.0) - 1.0
      ny = (green / 255.0 * 2.0) - 1.0
      nz = sqrt(max(0.0, 1.0 - nx^2 - ny^2))
      R = rx, G = gy, B = (nz * 0.5 + 0.5) * 255, A = 255
    """
    raw_rgba, width, height = decode_dds(dds_bytes)
    out_rgba = bytearray(len(raw_rgba))

    for i in range(0, len(raw_rgba), 4):
        rx = raw_rgba[i + 3] # X-normal in Alpha channel
        gy = raw_rgba[i + 1] # Y-normal in Green channel

        nx = (rx / 255.0 * 2.0) - 1.0
        ny = (gy / 255.0 * 2.0) - 1.0
        nz_sq = 1.0 - (nx * nx) - (ny * ny)
        nz = math.sqrt(max(0.0, nz_sq))

        bz = min(255, max(0, int(((nz * 0.5) + 0.5) * 255.0)))

        out_rgba[i + 0] = rx
        out_rgba[i + 1] = gy
        out_rgba[i + 2] = bz
        out_rgba[i + 3] = 255

    return (bytes(out_rgba), width, height)


def convert_specular_to_pbr(spec_rgba: bytes, width: int, height: int) -> Tuple[bytes, int, int]:
    """
    Convert legacy HoN specular texture into standard glTF PBR Metallic-Roughness map.
    glTF PBR convention: R = (unused), G = Roughness, B = Metallic, A = 255.
    """
    pbr_rgba = bytearray(len(spec_rgba))

    for i in range(0, len(spec_rgba), 4):
        r = spec_rgba[i + 0]
        g = spec_rgba[i + 1]
        b = spec_rgba[i + 2]

        spec_intensity = ((r * 0.299) + (g * 0.587) + (b * 0.114)) / 255.0
        roughness = min(0.95, max(0.10, 1.0 - (spec_intensity * 0.85)))
        metalness = min(0.90, max(0.0, (spec_intensity - 0.35) * 1.4)) if spec_intensity > 0.40 else 0.0

        pbr_rgba[i + 0] = 255
        pbr_rgba[i + 1] = min(255, max(0, int(roughness * 255.0)))
        pbr_rgba[i + 2] = min(255, max(0, int(metalness * 255.0)))
        pbr_rgba[i + 3] = 255

    return (bytes(pbr_rgba), width, height)


def encode_rgba_to_png(rgba: bytes, width: int, height: int, flip_vertical: bool = False) -> bytes:
    """
    Encode uncompressed RGBA pixel bytes into a valid standalone PNG image byte array.
    Uses Python standard library zlib (zero third-party dependencies).
    """
    stream = io.BytesIO()

    # PNG Signature
    stream.write(bytes([137, 80, 78, 71, 13, 10, 26, 10]))

    def write_chunk(chunk_type: bytes, payload: bytes) -> None:
        stream.write(struct.pack(">I", len(payload)))
        stream.write(chunk_type)
        if payload:
            stream.write(payload)
        crc = zlib.crc32(chunk_type + payload)
        stream.write(struct.pack(">I", crc))

    # IHDR Chunk
    ihdr_data = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    write_chunk(b"IHDR", ihdr_data)

    # IDAT Chunk (Raw scanlines with Filter type 0: None)
    row_bytes = width * 4
    scanlines = bytearray(height * (1 + row_bytes))
    dst_offset = 0

    for y in range(height):
        src_y = height - 1 - y if flip_vertical else y
        src_offset = src_y * row_bytes

        scanlines[dst_offset] = 0 # Filter: None
        dst_offset += 1

        scanlines[dst_offset:dst_offset + row_bytes] = rgba[src_offset:src_offset + row_bytes]
        dst_offset += row_bytes

    compressed = zlib.compress(bytes(scanlines), level=9)
    write_chunk(b"IDAT", compressed)

    # IEND Chunk
    write_chunk(b"IEND", b"")

    return stream.getvalue()


def read_material(source: Union[str, bytes]) -> HonMaterialData:
    """
    Parse a HoN .material XML file.
    
    Args:
        source: File path (str) or raw XML bytes/string.
        
    Returns:
        HonMaterialData populated with referenced sampler texture paths and parameters.
    """
    if isinstance(source, str):
        if os.path.exists(source):
            with open(source, "r", encoding="utf-8", errors="replace") as f:
                content = f.read()
            mat_name = os.path.splitext(os.path.basename(source))[0]
        else:
            content = source
            mat_name = "Material"
    elif isinstance(source, bytes):
        content = source.decode("utf-8", errors="replace")
        mat_name = "Material"
    else:
        raise TypeError(f"Unsupported source type for material parser: {type(source)}")

    root = ET.fromstring(content)
    mat = HonMaterialData(name=mat_name)

    # Check phases for samplers
    for phase_el in root.findall(".//phase") + root.findall(".//multipass"):
        phase_name = phase_el.attrib.get("name", "")
        alphatest = phase_el.attrib.get("alphatest", "").lower() in ("true", "1")
        translucent = phase_el.attrib.get("translucent", "").lower() in ("true", "1")
        srcblend = phase_el.attrib.get("srcblend", "")

        if alphatest or translucent:
            mat.has_alpha = True
        if "ADD" in srcblend or "BLEND_ONE" in srcblend and translucent:
            mat.is_additive = True

        for sampler in phase_el.findall("sampler"):
            s_name = sampler.attrib.get("name", "").lower()
            s_tex = sampler.attrib.get("texture", "") or sampler.attrib.get("file", "")

            if not s_tex:
                continue

            if s_name in ("diffuse", "color") and not mat.diffuse_texture:
                mat.diffuse_texture = s_tex
            elif s_name in ("normalmap", "normal") and not mat.normal_texture:
                mat.normal_texture = s_tex
            elif s_name in ("specular", "spec") and not mat.specular_texture:
                mat.specular_texture = s_tex
            elif s_name in ("lightmap", "glow", "emissive") and not mat.glow_texture:
                mat.glow_texture = s_tex
            elif s_name in ("opacity", "alpha") and not mat.opacity_texture:
                mat.opacity_texture = s_tex

    logger.info("Parsed HoN Material '%s': diffuse='%s', normal='%s', spec='%s', glow='%s'",
                mat.name, mat.diffuse_texture, mat.normal_texture, mat.specular_texture, mat.glow_texture)
    return mat

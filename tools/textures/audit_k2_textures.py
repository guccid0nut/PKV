#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
tools/audit_k2_textures.py
Project Kongor K2 Engine Texture Pipeline & Shadowing Auditor.

Performs offline automated static verification across K2 client textures:
1. Shadowing Hazard Detection (vid_d3d9_x64.dll + 0x96040 / +0x4bb80):
   Cross-references loose overrides in games/client/override against legacy entries
   in games/client/game/textures.s2z across all 12 descriptor tiers (00000000-00000011).
   CTextureArchive strips file extensions (Filename_StripExtension) and resolves
   <tier>/<stem>.dds (or <tier>/<stem>_rxgb.dds) BEFORE CBitmap::Load reads loose
   override/ files. Byte-identical vanilla copies are ignored; divergent .dds files
   and modified cross-extension (.png/.tga/.jpg) collisions are flagged.
2. DDS Header & Format Verification:
   Parses DDS headers (DXT1, DXT5, RGBA8, BC7), checks Power-of-Two (POT) dimensions,
   mipmap counts, and detects header corruption.
3. Channel Awareness & Normal Map Pipeline Checks (vid_d3d9_x64.dll + 0x9da30 / +0x982d0):
   - ui-rgba mode: Checks UI textures for valid RGBA channels, POT sizing, and
     halo risk (recommending scripts/assets/sanitize_alpha_texture.ps1).
   - rxgb-normal mode:
     * Loose source .tga/.png normal maps MUST remain standard tangent-space RGB
       (R=Nx, G=Ny, B=Nz, optional specular in Alpha); K2 CPU-swizzles them to RxGB
       (case 0xf) + _s specular (case 0x10) on load and caches them in userdata/.
       Pre-swizzled loose .tga/.png normal maps are flagged (double-swizzle bug).
     * Pre-baked *_rxgb.dds archives use K2's .agb shader layout
       (Red = 255, Green = +Ny, Blue = Nz, Alpha = Nx) in DXT5, NOT DXT1.
"""

from __future__ import annotations

import argparse
import io
import json
import math
import os
import re
import struct
import sys
import time
import zipfile
import zlib
from collections import defaultdict
from typing import Any, Dict, List, NamedTuple, Optional, Set, Tuple

try:
    from PIL import Image
    HAS_PIL = True
except ImportError:
    HAS_PIL = False

try:
    import numpy as _np  # noqa: F401
    HAS_NUMPY = True
except ImportError:
    HAS_NUMPY = False

DDS_MAGIC = b"DDS "


def is_power_of_two(n: int) -> bool:
    """Return True if n is a positive power of two."""
    return n > 0 and (n & (n - 1)) == 0


def normalize_vfs_path(p: str) -> str:
    """Normalize VFS path by standardizing slashes, stripping leading 00000000/, and lowercasing."""
    norm = p.replace("\\", "/").strip().strip("/")
    parts = norm.split("/")
    if parts and (parts[0] == "00000000" or parts[0].isdigit()):
        norm = "/".join(parts[1:])
    return norm.lower()


class DdsInfo:
    """Structured DDS header metadata."""
    def __init__(
        self,
        width: int,
        height: int,
        mipmaps: int,
        fourcc: str,
        is_pot: bool,
        has_alpha: bool,
        is_uncompressed: bool,
        bit_count: int,
        error: Optional[str] = None
    ):
        self.width = width
        self.height = height
        self.mipmaps = mipmaps
        self.fourcc = fourcc
        self.is_pot = is_pot
        self.has_alpha = has_alpha
        self.is_uncompressed = is_uncompressed
        self.bit_count = bit_count
        self.error = error

    def to_dict(self) -> Dict[str, Any]:
        return {
            "width": self.width,
            "height": self.height,
            "mipmaps": self.mipmaps,
            "fourcc": self.fourcc,
            "is_pot": self.is_pot,
            "has_alpha": self.has_alpha,
            "is_uncompressed": self.is_uncompressed,
            "bit_count": self.bit_count,
            "error": self.error,
        }


def parse_dds_header(file_path: str) -> DdsInfo:
    """Parse DDS 128-byte header from file."""
    try:
        with open(file_path, "rb") as f:
            header = f.read(128)
            if len(header) < 128:
                return DdsInfo(0, 0, 0, "UNKNOWN", False, False, False, 0, error="Truncated DDS file (<128 bytes)")
            if header[:4] != DDS_MAGIC:
                return DdsInfo(0, 0, 0, "UNKNOWN", False, False, False, 0, error=f"Invalid DDS magic: {header[:4]!r}")

            size, flags, height, width, pitch, depth, mips = struct.unpack("<IIIIIII", header[4:32])
            pf_size, pf_flags, fourcc_raw, rgb_bits, r_mask, g_mask, b_mask, a_mask = struct.unpack(
                "<II4sIIIII", header[76:108]
            )

            fourcc_str = fourcc_raw.decode("latin1", errors="replace").strip("\x00")
            is_uncompressed = bool(pf_flags & 0x40)  # DDPF_RGB
            has_alpha = bool(pf_flags & 0x1) or (a_mask != 0) or ("DXT5" in fourcc_str) or ("DXT3" in fourcc_str)
            fourcc_display = fourcc_str if fourcc_str else (f"RGB{rgb_bits}" if is_uncompressed else "UNCOMPRESSED")

            return DdsInfo(
                width=width,
                height=height,
                mipmaps=mips,
                fourcc=fourcc_display,
                is_pot=(is_power_of_two(width) and is_power_of_two(height)),
                has_alpha=has_alpha,
                is_uncompressed=is_uncompressed,
                bit_count=rgb_bits,
                error=None
            )
    except Exception as ex:
        return DdsInfo(0, 0, 0, "ERROR", False, False, False, 0, error=str(ex))


FULL_RES_TIERS = ("00000000", "00000001", "00000002", "00000009")
DXT5_CHANNEL_MAE_TOLERANCE = 4.0
DXT5_VFLIP_MAE_TOLERANCE = 5.5
_LUA_CONCAT_PREFIX_RE = re.compile(r"""['"]([^'"]*?(?:ui|fe2)/[^'"]+?)['"]\s*\.\.""", re.IGNORECASE)


class S2zEntry(NamedTuple):
    """Metadata for a single entry inside games/client/game/textures.s2z."""
    tier: str
    inner_name: str
    ext: str
    crc: int
    file_size: int


def compute_file_crc_and_size(file_path: str) -> Tuple[int, int]:
    """Compute CRC32 and byte size of a file on disk."""
    crc = 0
    total = 0
    with open(file_path, "rb") as fh:
        while True:
            chunk = fh.read(65536)
            if not chunk:
                break
            crc = zlib.crc32(chunk, crc)
            total += len(chunk)
    return (crc & 0xFFFFFFFF), total


def is_normal_map_vfs_path(norm_rel: str, is_ui: bool = False) -> bool:
    """
    Return True if normalized VFS path represents a tangent normal map,
    excluding K2 '*_s' companion specular masks (case 0x10 DXT1).
    """
    stem, _ = os.path.splitext(norm_rel)
    base_stem = os.path.basename(stem)
    if base_stem.endswith("_s"):
        return False
    if ("_norm" in base_stem) or ("_normal" in base_stem):
        return True
    if not is_ui and base_stem in ("normal", "normal2"):
        return True
    return False


def analyze_normal_map_channels(file_path: str, dds_info: Optional[DdsInfo]) -> Dict[str, Any]:
    """
    Analyzes whether a normal map conforms to K2's native pipeline
    (vid_d3d9_x64.dll + 0x9da30 / +0x982d0 & mesh_color_unit_team_lightmap_spec.psh:168):

    1. Pre-baked *_rxgb.dds (CTextureArchive / CTextureCache):
       Sampled as tex2D(...).agb in DXT5:
         Red   = 255 (0xFF constant)
         Green = +Ny (centered ~128)
         Blue  = Nz  (~200-255)
         Alpha = Nx  (8-bit interpolated, centered ~128)

    2. Loose source .tga / .png normal maps (override/ & resources*.s2z):
       Loaded via CBitmap::Load and CPU-swizzled at runtime (case 0xf -> RxGB, case 0x10 -> _s):
         Red = Nx (~128), Green = Ny (~128), Blue = Nz (>180), Alpha = specular mask / 255.
       Pre-swizzling a loose source .tga/.png to RxGB causes runtime double-swizzling!
    """
    res = {
        "status": "UNKNOWN",
        "details": "",
        "is_rxgb": False,
        "is_standard_rgb": False,
        "action": ""
    }

    lower_name = os.path.basename(file_path).lower()
    ext = os.path.splitext(lower_name)[1]

    # Check 1: DXT1 lacks 8-bit alpha required for K2 .agb normal sampling
    if dds_info and dds_info.fourcc == "DXT1":
        res["status"] = "INVALID_NORMAL_FORMAT"
        res["details"] = "Normal map saved as DXT1 (BC1). DXT1 lacks alpha channel (8-bit); K2 normal shaders sample .agb from DXT5."
        res["action"] = "Use a standard RGB .tga referenced by .material, or pack DXT5 RxGB (R=255, G=Ny, B=Nz, A=Nx) in textures.s2z."
        return res

    # Check 2: Explicit _rxgb asset (verify channels aren't R/B-swapped if readable on disk)
    if "_rxgb" in lower_name:
        if HAS_PIL and os.path.isfile(file_path):
            try:
                import numpy as np
                with Image.open(file_path) as img:
                    arr = np.array(img.convert("RGBA"))
                    mean_r = float(np.mean(arr[:, :, 0]))
                    mean_b = float(np.mean(arr[:, :, 2]))
                    std_b = float(np.std(arr[:, :, 2]))
                    if mean_r < 200.0 and mean_b > 245.0 and std_b < 2.0:
                        res["status"] = "SWAPPED_RXGB_CHANNELS"
                        res["details"] = (
                            f"Swapped RxGB channels detected (mean R={mean_r:.1f}, B={mean_b:.1f}). "
                            "K2 .agb shaders require Red=255, Green=Ny, Blue=Nz, Alpha=Nx."
                        )
                        res["action"] = "Re-bake with '--swizzle 1gbr' so Red=255, Green=Ny, Blue=Nz, Alpha=Nx."
                        return res
            except Exception as ex:
                res["details"] = f"Explicit '_rxgb' tag present (pixel decode skipped: {ex})."
                res["status"] = "VERIFIED_RXGB_TAG"
                res["is_rxgb"] = True
                return res

        res["status"] = "VERIFIED_RXGB_TAG"
        res["is_rxgb"] = True
        res["details"] = "Explicit '_rxgb' naming convention verified (R=255, G=Ny, B=Nz, A=Nx)."
        return res

    # Check 3: Loose .tga / .png source normal maps MUST be tangent-space RGB (not pre-swizzled)
    if HAS_PIL and ext in (".tga", ".png") and os.path.isfile(file_path):
        try:
            with Image.open(file_path) as img:
                if img.mode in ("RGB", "RGBA"):
                    import numpy as np
                    arr = np.array(img)
                    mean_r = float(np.mean(arr[:, :, 0]))
                    mean_g = float(np.mean(arr[:, :, 1]))
                    mean_b = float(np.mean(arr[:, :, 2]))

                    if mean_r > 245.0 and arr.shape[2] == 4:
                        std_a = float(np.std(arr[:, :, 3]))
                        if std_a > 5.0:
                            res["status"] = "PRE_SWIZZLED_SOURCE_NORMAL"
                            res["details"] = (
                                f"Loose {ext} normal map appears pre-swizzled to RxGB (mean R={mean_r:.1f}, Alpha std={std_a:.1f}). "
                                "CBitmap::Load + FUN_1800982d0(case 0xf) will double-swizzle it at runtime."
                            )
                            res["action"] = "Export loose source normal maps as standard tangent-space RGB (R=Nx, G=Ny, B=Nz)."
                            return res

                    if mean_b > 170.0 and 80.0 < mean_r < 175.0 and 80.0 < mean_g < 175.0:
                        res["status"] = "VALID_SOURCE_RGB_NORMAL"
                        res["is_standard_rgb"] = True
                        res["details"] = (
                            f"Valid K2 source tangent RGB normal map (mean R={mean_r:.1f}, G={mean_g:.1f}, B={mean_b:.1f}); "
                            "CPU-swizzled to RxGB at load time."
                        )
                        return res
        except Exception as ex:
            res["details"] = f"Pixel analysis failed: {ex}"

    # Default for DXT5 .dds
    if dds_info and dds_info.fourcc == "DXT5":
        res["status"] = "COMPLIANT_DXT5"
        res["details"] = "Stored in DXT5 container."
    else:
        res["status"] = "UNVERIFIED"
        res["details"] = "Could not definitively verify normal map channels."

    return res


class K2TextureAuditor:
    """Performs full texture audit and textures.s2z stem/CRC/decoded-RGBA shadowing collision analysis."""
    def __init__(self, client_root: str = "games/client", target_dir: str = "games/client/override"):
        self.client_root = client_root
        self.target_dir = target_dir
        self.textures_s2z_paths = [
            os.path.join(client_root, "KONGOR", "updates", "textures.s2z"),
            os.path.join(client_root, "game", "textures.s2z"),
        ]
        self.textures_s2z_path = self.textures_s2z_paths[1]
        self.s2z_entries: Dict[str, str] = {}  # normalized_vfs -> preferred full-res inner_name
        self.s2z_by_stem: Dict[str, List[S2zEntry]] = defaultdict(list)
        self.vanilla_resource_crcs: Dict[str, Set[Tuple[int, int]]] = defaultdict(set)
        self._ui_ref_index_built: bool = False
        self._ui_static_corpus: str = ""
        self._ui_precache_corpus: str = ""
        self._ui_concat_prefixes: Set[str] = set()
        self._warnings: List[str] = []
        self.index_textures_s2z()
        self.index_vanilla_resources()

    def index_textures_s2z(self) -> None:
        """Read and index entries by stem, tier, CRC32, and byte size from textures.s2z archives (all shards)."""
        for s2z_path in self.textures_s2z_paths:
            if not os.path.exists(s2z_path):
                continue
            try:
                with zipfile.ZipFile(s2z_path, "r") as z:
                    for info in z.infolist():
                        if info.is_dir():
                            continue
                        raw_name = info.filename.replace("\\", "/").strip("/")
                        parts = raw_name.split("/", 1)
                        tier = parts[0] if (len(parts) == 2 and parts[0].isdigit()) else ""
                        norm = normalize_vfs_path(raw_name)
                        stem, ext = os.path.splitext(norm)
                        if ext not in (".dds", ".tga", ".png", ".jpg"):
                            continue

                        entry = S2zEntry(
                            tier=tier,
                            inner_name=info.filename,
                            ext=ext,
                            crc=info.CRC,
                            file_size=info.file_size,
                        )
                        self.s2z_by_stem[stem].append(entry)

                        # Preserve full-res tier (00000000/00000001/00000002/00000009) over downsized tiers
                        if norm not in self.s2z_entries or tier in FULL_RES_TIERS:
                            if norm not in self.s2z_entries or not any(
                                self.s2z_entries[norm].startswith(t + "/") for t in FULL_RES_TIERS
                            ):
                                self.s2z_entries[norm] = info.filename

            except Exception as ex:
                print(f"[WARN] Failed to open textures.s2z ({s2z_path}): {ex}")

        for stem, entries in self.s2z_by_stem.items():
            entries.sort(key=lambda e: (0 if e.tier in FULL_RES_TIERS else 1, e.tier))

    def index_vanilla_resources(self) -> None:
        """Index CRC32 + size of vanilla resources*.s2z textures so unmodified extracts aren't false-flagged."""
        game_dir = os.path.join(self.client_root, "game")
        if not os.path.isdir(game_dir):
            return
        for archive_name in ("resources0.s2z", "resources94.s2z", "resources95.s2z", "resources98.s2z", "resources99.s2z"):
            arch_path = os.path.join(game_dir, archive_name)
            if not os.path.isfile(arch_path):
                continue
            try:
                with zipfile.ZipFile(arch_path, "r") as z:
                    for info in z.infolist():
                        if info.is_dir():
                            continue
                        norm = info.filename.replace("\\", "/").strip("/").lower()
                        if norm.endswith((".dds", ".tga", ".png", ".jpg")):
                            self.vanilla_resource_crcs[norm].add((info.CRC, info.file_size))
            except Exception as ex:
                print(f"[WARN] Failed to index {archive_name}: {ex}")

    def _ensure_ui_ref_index(self) -> None:
        """
        Lazily index static UI text references (.lua, .package, .interface) across client UI trees.
        Separates precache.package from active UI scripts/packages and extracts concatenated path prefixes.
        """
        if self._ui_ref_index_built:
            return
        self._ui_ref_index_built = True

        search_roots = [
            os.path.join(self.client_root, "override", "ui"),
            os.path.join(self.client_root, "game", "ui"),
            os.path.join(self.client_root, "ClientPackageFiles", "ui"),
            self.target_dir,
        ]
        seen_files: Set[str] = set()
        static_chunks: List[str] = []
        precache_chunks: List[str] = []

        for sroot in search_roots:
            if not sroot or not os.path.isdir(sroot):
                continue
            for rdir, _, rfiles in os.walk(sroot):
                for rf in rfiles:
                    rext = os.path.splitext(rf)[1].lower()
                    if rext not in (".lua", ".package", ".interface"):
                        continue
                    abs_f = os.path.abspath(os.path.join(rdir, rf))
                    if abs_f in seen_files:
                        continue
                    seen_files.add(abs_f)
                    try:
                        with open(abs_f, "r", encoding="utf-8", errors="ignore") as fh:
                            txt = fh.read()
                    except Exception as ex:
                        warn_msg = f"Failed to read UI file '{abs_f}' during static reference scan: {ex}"
                        print(f"[WARN] {warn_msg}", file=sys.stderr)
                        self._warnings.append(warn_msg)
                        continue

                    norm_txt = txt.replace("\\", "/").lower()
                    if rf.lower() == "precache.package":
                        precache_chunks.append(norm_txt)
                    else:
                        static_chunks.append(norm_txt)
                        for m in _LUA_CONCAT_PREFIX_RE.finditer(txt):
                            raw_pfx = m.group(1).replace("\\", "/").strip().lstrip("/").lower()
                            if raw_pfx:
                                self._ui_concat_prefixes.add(raw_pfx)

        self._ui_static_corpus = "\n".join(static_chunks)
        self._ui_precache_corpus = "\n".join(precache_chunks)

    def _classify_ui_static_reference(self, norm_rel: str) -> Tuple[str, bool]:
        """
        Advisory static reference check for shadowed UI textures (never alters severity or exit code).
        Returns: (static_ref_status, in_precache)
          - 'direct_ref_found': exact VFS stem appears in .lua/.package/.interface (excluding precache.package)
          - 'dynamic_prefix_ref_found': concatenated path prefix (e.g. 'ui/.../border_' .. idx) covers this stem
          - 'no_static_ref_found': no static or prefix match found in scanned UI files
        """
        self._ensure_ui_ref_index()
        stem, _ = os.path.splitext(norm_rel)
        in_precache = bool(stem and stem in self._ui_precache_corpus)
        if stem and stem in self._ui_static_corpus:
            return "direct_ref_found", in_precache
        for pfx in self._ui_concat_prefixes:
            if len(pfx) >= 4 and stem.startswith(pfx):
                return "dynamic_prefix_ref_found", in_precache
        return "no_static_ref_found", in_precache

    def _compare_decoded_rgba_with_s2z(
        self,
        full_path: str,
        s2z_entry: str,
        s2z_zip: Optional[zipfile.ZipFile],
        allow_dxt5_equivalence: bool,
        allow_vflip_equivalence: bool = False,
    ) -> Tuple[bool, Dict[str, Any]]:
        """
        Decode override image and textures.s2z entry as RGBA and compute per-channel MAE.
        Accounts for K2's bottom-up CTextureArchive .dds storage (s2z_arr[::-1]) when comparing
        cross-extension .tga/.png CBitmap files against .dds archive entries.
        Returns: (is_dxt5_equivalent, pixel_diff_telemetry)
        """
        if not HAS_PIL or s2z_zip is None:
            return False, {
                "decode_status": "skipped_no_decoder",
                "diff_kind": "undecodable",
                "mae_rgb": None,
                "mae_alpha": None,
                "mae_max_channel": None,
            }

        try:
            import numpy as np
            s2z_bytes = s2z_zip.read(s2z_entry)
            with Image.open(full_path) as ov_im, Image.open(io.BytesIO(s2z_bytes)) as s2z_im:
                ov_arr = np.array(ov_im.convert("RGBA"), dtype=np.float32)
                s2z_arr = np.array(s2z_im.convert("RGBA"), dtype=np.float32)
        except Exception as ex:
            return False, {
                "decode_status": "decode_failed",
                "diff_kind": "undecodable",
                "error": str(ex),
                "mae_rgb": None,
                "mae_alpha": None,
                "mae_max_channel": None,
            }

        if ov_arr.shape != s2z_arr.shape:
            return False, {
                "decode_status": "shape_mismatch",
                "diff_kind": "shape_mismatch",
                "override_shape": [int(ov_arr.shape[1]), int(ov_arr.shape[0])],
                "s2z_shape": [int(s2z_arr.shape[1]), int(s2z_arr.shape[0])],
                "mae_rgb": None,
                "mae_alpha": None,
                "mae_max_channel": None,
            }

        abs_diff = np.abs(ov_arr - s2z_arr)
        ch_mae = [float(abs_diff[:, :, c].mean()) for c in range(4)]
        mae_rgb_max = max(ch_mae[:3])
        mae_alpha = ch_mae[3]
        mae_max_ch = max(ch_mae)
        div_px_mask = abs_diff.max(axis=2) > 8.0
        div_px_pct = round(100.0 * float(div_px_mask.mean()), 2)

        if allow_dxt5_equivalence and mae_max_ch <= DXT5_CHANNEL_MAE_TOLERANCE:
            return True, {
                "decode_status": "decoded",
                "diff_kind": "dxt5_equivalent",
                "mae_rgb": round(mae_rgb_max, 2),
                "mae_alpha": round(mae_alpha, 2),
                "mae_max_channel": round(mae_max_ch, 2),
                "divergent_px_pct": div_px_pct,
            }

        if allow_dxt5_equivalence and allow_vflip_equivalence:
            flip_diff = np.abs(ov_arr - s2z_arr[::-1, :, :])
            flip_ch_mae = [float(flip_diff[:, :, c].mean()) for c in range(4)]
            flip_max_ch = max(flip_ch_mae)
            if flip_max_ch <= DXT5_VFLIP_MAE_TOLERANCE:
                flip_div_pct = round(100.0 * float((flip_diff.max(axis=2) > 8.0).mean()), 2)
                return True, {
                    "decode_status": "decoded",
                    "diff_kind": "dxt5_vflipped_equivalent",
                    "mae_rgb": round(max(flip_ch_mae[:3]), 2),
                    "mae_alpha": round(flip_ch_mae[3], 2),
                    "mae_max_channel": round(flip_max_ch, 2),
                    "divergent_px_pct": flip_div_pct,
                }

        if mae_rgb_max <= DXT5_CHANNEL_MAE_TOLERANCE and mae_alpha > DXT5_CHANNEL_MAE_TOLERANCE:
            diff_kind = "alpha_only"
        elif mae_rgb_max > DXT5_CHANNEL_MAE_TOLERANCE and mae_alpha <= DXT5_CHANNEL_MAE_TOLERANCE:
            diff_kind = "rgb_only"
        else:
            diff_kind = "rgb_and_alpha"

        return False, {
            "decode_status": "decoded",
            "diff_kind": diff_kind,
            "mae_rgb": round(mae_rgb_max, 2),
            "mae_alpha": round(mae_alpha, 2),
            "mae_max_channel": round(mae_max_ch, 2),
            "divergent_px_pct": div_px_pct,
        }

    def _check_s2z_shadow(
        self,
        full_path: str,
        rel_path: str,
        norm_rel: str,
        ext: str,
        is_ui: bool,
        is_normal_map: bool,
        s2z_zip: Optional[zipfile.ZipFile] = None,
    ) -> Tuple[bool, bool, bool, bool, Optional[Dict[str, Any]]]:
        """
        Determine whether full_path is genuinely shadowed by textures.s2z.
        Returns: (is_shadowed, is_identical_copy, is_dxt5_equivalent, is_vflipped_equivalent, shadow_info_dict)
        """
        stem, _ = os.path.splitext(norm_rel)
        matched_entries = self.s2z_by_stem.get(stem)
        collision_kind = "same_extension" if ext == ".dds" else "cross_extension"

        if not matched_entries and is_normal_map and not stem.endswith("_rxgb"):
            rxgb_entries = self.s2z_by_stem.get(stem + "_rxgb")
            if rxgb_entries:
                matched_entries = rxgb_entries
                collision_kind = "rxgb_archive_shadow"

        if not matched_entries:
            return False, False, False, False, None

        file_crc, file_size = compute_file_crc_and_size(full_path)

        # 1. Exact CRC32 + byte-size match against any tier in textures.s2z -> harmless vanilla copy
        if any(e.crc == file_crc and e.file_size == file_size for e in matched_entries):
            return False, True, False, False, None

        # 2. Unmodified vanilla source (.tga/.png/.jpg) matching resources*.s2z or sitting beside an identical .dds
        if ext != ".dds":
            if (file_crc, file_size) in self.vanilla_resource_crcs.get(norm_rel, set()):
                return False, True, False, False, None
            base_disk = os.path.splitext(full_path)[0]
            for sibling_dds in (base_disk + ".dds", base_disk + "_rxgb.dds"):
                if os.path.isfile(sibling_dds):
                    sib_crc, sib_size = compute_file_crc_and_size(sibling_dds)
                    if any(e.crc == sib_crc and e.file_size == sib_size for e in matched_entries):
                        return False, True, False, False, None

        # 3. Lazy decoded RGBA comparison (only on CRC miss; suppresses DXT5 compression noise & K2 V-flipped .dds)
        preferred_entry = matched_entries[0].inner_name
        allow_equiv = collision_kind in ("same_extension", "cross_extension")
        allow_vflip = (collision_kind == "cross_extension") and preferred_entry.lower().endswith(".dds")
        is_dxt5_equiv, pixel_diff = self._compare_decoded_rgba_with_s2z(
            full_path=full_path,
            s2z_entry=preferred_entry,
            s2z_zip=s2z_zip,
            allow_dxt5_equivalence=allow_equiv,
            allow_vflip_equivalence=allow_vflip,
        )
        if is_dxt5_equiv:
            is_vflip = pixel_diff.get("diff_kind") == "dxt5_vflipped_equivalent"
            return False, True, True, is_vflip, None

        # 4. Genuine divergent or undecodable override shadowed by CTextureArchive
        severity = "HIGH" if is_ui else "MEDIUM"
        base_no_ext = os.path.splitext(os.path.basename(full_path))[0]
        static_ref_status: Optional[str] = None
        in_precache: Optional[bool] = None
        if is_ui:
            static_ref_status, in_precache = self._classify_ui_static_reference(norm_rel)

        shadow_info = {
            "vfs_path": rel_path,
            "file_path": full_path,
            "s2z_entry": preferred_entry,
            "collision_kind": collision_kind,
            "severity": severity,
            "pixel_diff": pixel_diff,
            "static_ref_status": static_ref_status,
            "in_precache": in_precache,
            "remediation": (
                f"Rename '{os.path.basename(full_path)}' to a versioned basename "
                f"(e.g. '{base_no_ext}_v2{ext}') and update referencing .package/.interface/.material."
            ),
        }
        return True, False, False, False, shadow_info

    def audit(
        self,
        channel_mode: str = "all",
        filter_str: Optional[str] = None
    ) -> Dict[str, Any]:
        """
        Audit textures in target_dir.
        channel_mode: 'all', 'ui-rgba', 'rxgb-normal'
        """
        t0 = time.time()
        self._warnings = []
        results: Dict[str, Any] = {
            "summary": {
                "total_scanned": 0,
                "identical_s2z_copies": 0,
                "dxt5_equivalent_s2z_copies": 0,
                "vflipped_s2z_copies": 0,
                "shadowing_hazards": 0,
                "ui_shadowing_high_risk": 0,
                "npot_count": 0,
                "corrupt_headers": 0,
                "normal_maps_audited": 0,
                "normal_maps_non_rxgb": 0,
                "unused_loose_normal_dds": 0,
                "warnings_count": 0,
            },
            "warnings": [],
            "shadowing_hazards": [],
            "corrupt_textures": [],
            "normal_map_issues": [],
            "unused_loose_normal_dds": [],
            "npot_textures": [],
            "textures": []
        }

        if not os.path.exists(self.target_dir):
            return results

        channel_mode = channel_mode.lower()
        s2z_zip: Optional[zipfile.ZipFile] = None
        if os.path.isfile(self.textures_s2z_path):
            try:
                s2z_zip = zipfile.ZipFile(self.textures_s2z_path, "r")
            except Exception as ex:
                warn_msg = (
                    f"Failed to open '{self.textures_s2z_path}' for decoded RGBA comparison ({ex}); "
                    "falling back to strict CRC32/size shadowing check."
                )
                print(f"[WARN] {warn_msg}", file=sys.stderr)
                self._warnings.append(warn_msg)
                s2z_zip = None

        try:
            for root, _, files in os.walk(self.target_dir):
                for f in files:
                    ext = os.path.splitext(f)[1].lower()
                    if ext not in (".dds", ".png", ".tga", ".jpg"):
                        continue

                    full_path = os.path.join(root, f)
                    rel_path = os.path.relpath(full_path, self.target_dir).replace("\\", "/")
                    raw_first_part = rel_path.split("/", 1)[0]
                    is_raw_tier_dump = raw_first_part.isdigit()
                    norm_rel = normalize_vfs_path(rel_path)

                    if filter_str and filter_str.lower() not in norm_rel:
                        continue

                    is_ui = norm_rel.startswith("ui/") or norm_rel.startswith("fe2/") or ("/ui/" in norm_rel)
                    is_normal_map = is_normal_map_vfs_path(norm_rel, is_ui=is_ui)

                    if channel_mode == "ui-rgba" and not is_ui:
                        continue
                    if channel_mode == "rxgb-normal" and not is_normal_map:
                        continue

                    results["summary"]["total_scanned"] += 1

                    # 1. Shadowing Check against textures.s2z (stem + CRC32 + lazy decoded RGBA MAE)
                    is_shadowed, is_ident_copy, is_dxt5_equiv, is_vflip_equiv, shadow_info = self._check_s2z_shadow(
                        full_path=full_path,
                        rel_path=rel_path,
                        norm_rel=norm_rel,
                        ext=ext,
                        is_ui=is_ui,
                        is_normal_map=is_normal_map,
                        s2z_zip=s2z_zip,
                    )
                    if is_ident_copy:
                        results["summary"]["identical_s2z_copies"] += 1
                        if is_dxt5_equiv:
                            results["summary"]["dxt5_equivalent_s2z_copies"] += 1
                        if is_vflip_equiv:
                            results["summary"]["vflipped_s2z_copies"] += 1
                    elif is_shadowed and shadow_info is not None:
                        results["summary"]["shadowing_hazards"] += 1
                        if is_ui:
                            results["summary"]["ui_shadowing_high_risk"] += 1
                        results["shadowing_hazards"].append(shadow_info)

                    # 2. DDS / Header parse
                    dds_info: Optional[DdsInfo] = None
                    width, height, fourcc = 0, 0, "N/A"
                    if ext == ".dds":
                        dds_info = parse_dds_header(full_path)
                        width, height = dds_info.width, dds_info.height
                        fourcc = dds_info.fourcc
                        if dds_info.error:
                            results["summary"]["corrupt_headers"] += 1
                            results["corrupt_textures"].append({
                                "vfs_path": rel_path,
                                "file_path": full_path,
                                "error": dds_info.error
                            })
                        elif not dds_info.is_pot:
                            results["summary"]["npot_count"] += 1
                            results["npot_textures"].append({
                                "vfs_path": rel_path,
                                "width": width,
                                "height": height,
                                "fourcc": fourcc
                            })
                    elif HAS_PIL:
                        try:
                            with Image.open(full_path) as im:
                                width, height = im.size
                                fourcc = im.mode
                                if not (is_power_of_two(width) and is_power_of_two(height)):
                                    results["summary"]["npot_count"] += 1
                                    results["npot_textures"].append({
                                        "vfs_path": rel_path,
                                        "width": width,
                                        "height": height,
                                        "fourcc": fourcc
                                    })
                        except Exception as ex:
                            results["summary"]["corrupt_headers"] += 1
                            results["corrupt_textures"].append({
                                "vfs_path": rel_path,
                                "file_path": full_path,
                                "error": str(ex)
                            })

                    # 3. Normal Map Pipeline & Channel Check
                    normal_analysis = None
                    if is_normal_map:
                        results["summary"]["normal_maps_audited"] += 1
                        normal_analysis = analyze_normal_map_channels(full_path, dds_info)
                        if not is_ident_copy and normal_analysis["status"] in (
                            "INVALID_NORMAL_FORMAT",
                            "PRE_SWIZZLED_SOURCE_NORMAL",
                            "SWAPPED_RXGB_CHANNELS",
                        ):
                            results["summary"]["normal_maps_non_rxgb"] += 1
                            results["normal_map_issues"].append({
                                "vfs_path": rel_path,
                                "file_path": full_path,
                                "status": normal_analysis["status"],
                                "details": normal_analysis["details"],
                                "action": normal_analysis["action"]
                            })

                        # Advisory check: loose .dds / _rxgb.dds normal maps in override/ when not an identical s2z copy
                        if ext == ".dds" and not is_raw_tier_dump and not is_ident_copy:
                            sibling_tga = os.path.splitext(full_path)[0] + ".tga"
                            if "_rxgb" in f.lower() or os.path.isfile(sibling_tga):
                                results["summary"]["unused_loose_normal_dds"] += 1
                                results["unused_loose_normal_dds"].append({
                                    "vfs_path": rel_path,
                                    "has_tga_sibling": os.path.isfile(sibling_tga),
                                    "note": (
                                        "D3D_Register2DTexture only loads *_rxgb.dds from textures.s2z/CTextureCache "
                                        "and loads .tga sources via CBitmap::LoadTGA; loose override .dds normal is bypassed."
                                    ),
                                })

                    results["textures"].append({
                        "vfs_path": rel_path,
                        "width": width,
                        "height": height,
                        "fourcc": fourcc,
                        "is_ui": is_ui,
                        "is_normal_map": is_normal_map,
                        "is_shadowed": is_shadowed,
                        "is_identical_s2z_copy": is_ident_copy,
                        "normal_analysis": normal_analysis
                    })
        finally:
            if s2z_zip is not None:
                s2z_zip.close()

        results["warnings"] = list(self._warnings)
        results["summary"]["warnings_count"] = len(self._warnings)
        results["elapsed_seconds"] = round(time.time() - t0, 3)
        return results


def _format_hazard_diagnostics(item: Dict[str, Any]) -> str:
    """Format decoded RGBA MAE and advisory static reference telemetry for human logs."""
    parts: List[str] = []
    pd = item.get("pixel_diff") or {}
    dk = pd.get("diff_kind")
    if dk in ("alpha_only", "rgb_only", "rgb_and_alpha"):
        parts.append(f"diff={dk} mae_rgb={pd.get('mae_rgb')} mae_a={pd.get('mae_alpha')}")
    elif dk == "shape_mismatch":
        parts.append(f"diff=shape_mismatch ov={pd.get('override_shape')} s2z={pd.get('s2z_shape')}")
    elif dk:
        parts.append(f"diff={dk}")

    sref = item.get("static_ref_status")
    if sref:
        pre_tag = ", in_precache=true" if item.get("in_precache") else ""
        parts.append(f"ref={sref}{pre_tag}")
    return ", ".join(parts)


def generate_markdown_report(results: Dict[str, Any], output_path: str) -> None:
    """Generate Markdown audit report."""
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    summary = results["summary"]
    with open(output_path, "w", encoding="utf-8") as f:
        f.write("# Project Kongor: K2 Texture Pipeline & Shadowing Audit Report\n\n")
        f.write(f"- **Generated:** {time.strftime('%Y-%m-%d %H:%M:%S')}\n")
        f.write(f"- **Scan Duration:** {results.get('elapsed_seconds', 0.0)}s\n")
        f.write(f"- **Total Textures Scanned:** {summary['total_scanned']}\n\n")

        f.write("## 1. Executive Summary\n\n")
        f.write("| Metric | Count | Status / Risk |\n")
        f.write("|---|---|---|\n")
        f.write(f"| **Divergent / Cross-Ext Shadowing (`textures.s2z`)** | {summary['shadowing_hazards']} | {'HIGH RISK (Overrides Silently Ignored)' if summary['shadowing_hazards'] > 0 else 'PASS (0 Divergent Collisions)'} |\n")
        f.write(f"| -> *UI Overrides at High Risk* | **{summary['ui_shadowing_high_risk']}** | {'Action Required: Rename with versioned basenames' if summary['ui_shadowing_high_risk'] > 0 else 'PASS'} |\n")
        f.write(f"| -> *Byte/DXT5-Equivalent Vanilla S2Z Copies (Harmless)* | {summary.get('identical_s2z_copies', 0)} (DXT5-equiv: {summary.get('dxt5_equivalent_s2z_copies', 0)}) | Informational (No silent drift) |\n")
        f.write(f"| **Corrupted / Truncated Headers** | {summary['corrupt_headers']} | {'FAIL' if summary['corrupt_headers'] > 0 else 'PASS'} |\n")
        f.write(f"| **Non-Power-of-Two (NPOT)** | {summary['npot_count']} | Advisory (Potential sampling hitches) |\n")
        f.write(f"| **Normal Maps Audited** | {summary['normal_maps_audited']} | - |\n")
        f.write(f"| -> *Invalid / Double-Swizzle Normal Maps* | **{summary['normal_maps_non_rxgb']}** | {'Action Required: Fix format/swizzle' if summary['normal_maps_non_rxgb'] > 0 else 'PASS'} |\n")
        f.write(f"| -> *Unused Loose `.dds` Normal Siblings* | {summary.get('unused_loose_normal_dds', 0)} | Advisory (Bypassed when `.material` uses `.tga`) |\n\n")

        # Shadowing Table
        if results["shadowing_hazards"]:
            f.write(f"## 2. Divergent `textures.s2z` Shadowing Hazards ({len(results['shadowing_hazards'])} Found)\n\n")
            f.write("> [!WARNING]\n")
            f.write("> `CTextureArchive` (`vid_d3d9_x64.dll + 0x96040` / `+0x4bb80`) strips file extensions and resolves `<tier>/<stem>.dds` in `textures.s2z` before reading loose `override/` files.\n")
            f.write("> Modified overrides matching entries below **will be silently ignored by the engine** unless renamed to a versioned basename.\n\n")
            f.write("| VFS Path | Kind | Severity | S2Z Archive Match | Diagnostics | Remediation Action |\n")
            f.write("|---|---|---|---|---|---|\n")
            for item in results["shadowing_hazards"][:100]:
                diag = _format_hazard_diagnostics(item) or "-"
                f.write(
                    f"| `{item['vfs_path']}` | `{item.get('collision_kind', 'same_extension')}` | "
                    f"**{item['severity']}** | `{item['s2z_entry']}` | `{diag}` | {item['remediation']} |\n"
                )
            if len(results["shadowing_hazards"]) > 100:
                f.write(f"\n*... and {len(results['shadowing_hazards']) - 100} more shadowed textures.*\n")
            f.write("\n")

        # Normal Map Issues
        if results["normal_map_issues"]:
            f.write(f"## 3. Normal Map Format & Swizzle Defects ({len(results['normal_map_issues'])} Found)\n\n")
            f.write("> [!IMPORTANT]\n")
            f.write("> Loose `.tga`/`.png` normal maps must remain standard tangent-space RGB (`R=Nx, G=Ny, B=Nz`); K2 CPU-swizzles them to RxGB on load.\n")
            f.write("> Pre-baked `*_rxgb.dds` archive textures use K2's `.agb` layout (`Red=255, Green=+Ny, Blue=Nz, Alpha=Nx`) in `DXT5`.\n\n")
            f.write("| VFS Path | Issue | Details | Required Action |\n")
            f.write("|---|---|---|---|\n")
            for item in results["normal_map_issues"]:
                f.write(f"| `{item['vfs_path']}` | **{item['status']}** | {item['details']} | `{item['action']}` |\n")
            f.write("\n")

        # Corrupt Textures
        if results["corrupt_textures"]:
            f.write(f"## 4. Corrupted / Unreadable Textures ({len(results['corrupt_textures'])} Found)\n\n")
            f.write("| VFS Path | Error Message |\n")
            f.write("|---|---|\n")
            for item in results["corrupt_textures"]:
                f.write(f"| `{item['vfs_path']}` | {item['error']} |\n")
            f.write("\n")


def print_console_summary(results: Dict[str, Any], detailed: bool = False) -> None:
    """Print clean ASCII-safe summary to console."""
    summary = results["summary"]
    print("\n" + "=" * 78)
    print(" [KONGOR K2 TEXTURE AUDITOR] Execution Summary")
    print("=" * 78)
    print(f" Total Textures Scanned : {summary['total_scanned']}")
    print(f" Scan Duration          : {results.get('elapsed_seconds', 0.0)}s")
    print("-" * 78)
    print(f" Shadowing Hazards      : {summary['shadowing_hazards']} divergent/cross-ext")
    print(f"   -> High-Risk UI      : {summary['ui_shadowing_high_risk']}")
    print(
        f"   -> Identical Vanilla : {summary.get('identical_s2z_copies', 0)} "
        f"(DXT5-equiv: {summary.get('dxt5_equivalent_s2z_copies', 0)}, "
        f"V-flipped: {summary.get('vflipped_s2z_copies', 0)}, harmless)"
    )
    print(f" Corrupt Headers        : {summary['corrupt_headers']}")
    print(f" Non-Power-of-Two (NPOT): {summary['npot_count']}")
    print(f" Normal Maps Audited    : {summary['normal_maps_audited']}")
    print(f"   -> Invalid/Bad Swizz : {summary['normal_maps_non_rxgb']}")
    print(f"   -> Unused Loose .dds : {summary.get('unused_loose_normal_dds', 0)} (advisory)")
    print("=" * 78)

    if summary["ui_shadowing_high_risk"] > 0:
        print("\n [!] HIGH RISK UI SHADOWING HAZARDS DETECTED:")
        print("     Legacy entries in 'games/client/game/textures.s2z' silently shadow these modified files!")
        ui_hazards = [h for h in results["shadowing_hazards"] if h["severity"] == "HIGH"]
        for h in ui_hazards[:10]:
            diag = _format_hazard_diagnostics(h)
            diag_str = f" [{diag}]" if diag else ""
            print(f"     - {h['vfs_path']} [{h.get('collision_kind', '')}]{diag_str} (Shadowed by: {h['s2z_entry']})")
        if len(ui_hazards) > 10:
            print(f"     ... and {len(ui_hazards) - 10} more UI files.")
        print("     Fix: Rename to a versioned basename (e.g. 'border_gold_v2.png') and update referencing .package/.interface.")

    if summary["normal_maps_non_rxgb"] > 0:
        print("\n [!] NORMAL MAP FORMAT / SWIZZLE VIOLATIONS DETECTED:")
        for nm in results["normal_map_issues"][:8]:
            print(f"     - {nm['vfs_path']} [{nm['status']}]: {nm['details']}")

    if detailed and results["shadowing_hazards"]:
        print("\n [DETAILED SHADOWING LIST]")
        for h in results["shadowing_hazards"][:40]:
            diag = _format_hazard_diagnostics(h)
            diag_str = f" [{diag}]" if diag else ""
            print(f"   [{h['severity']}] {h['vfs_path']} ({h.get('collision_kind', '')}){diag_str} -> {h['s2z_entry']}")


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Audit K2 Engine texture pipeline, formats, and textures.s2z shadowing.")
    parser.add_argument("--client-root", default="games/client", help="Path to games/client root.")
    parser.add_argument("--target-dir", default="games/client/override", help="Directory to audit.")
    parser.add_argument("--channel-mode", default="all", choices=["all", "ui-rgba", "rxgb-normal"], help="Audit mode.")
    parser.add_argument("--report", default="", help="Path to write markdown report.")
    parser.add_argument("--json", default="", help="Path to write JSON telemetry output.")
    parser.add_argument("--filter", default=None, help="Filter VFS path substring.")
    parser.add_argument("--strict", action="store_true", help="Exit code 1 on high-risk shadowing or format errors.")
    parser.add_argument("--detailed", action="store_true", help="Print detailed per-file output.")

    args = parser.parse_args(argv)

    if args.strict and (not HAS_PIL or not HAS_NUMPY):
        missing = [pkg for pkg, ok in (("Pillow", HAS_PIL), ("numpy", HAS_NUMPY)) if not ok]
        print(
            f" [ERROR] Strict K2 texture audit requires {', '.join(missing)} for decoded RGBA shadowing and "
            f"normal-map swizzle verification. Fix: run 'python -m pip install Pillow numpy' and re-run.",
            file=sys.stderr,
        )
        return 2

    auditor = K2TextureAuditor(client_root=args.client_root, target_dir=args.target_dir)
    results = auditor.audit(channel_mode=args.channel_mode, filter_str=args.filter)

    print_console_summary(results, detailed=args.detailed)

    if args.report:
        generate_markdown_report(results, args.report)
        print(f"\n [INFO] Markdown report written to: {args.report}")

    if args.json:
        os.makedirs(os.path.dirname(os.path.abspath(args.json)), exist_ok=True)
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump(results, f, indent=2)
        print(f" [INFO] JSON telemetry written to: {args.json}")

    # Return exit code: enforce high-risk shadowing, corrupt headers, and invalid normal map formats
    if args.strict:
        if (
            results["summary"]["ui_shadowing_high_risk"] > 0
            or results["summary"]["corrupt_headers"] > 0
            or results["summary"]["normal_maps_non_rxgb"] > 0
        ):
            return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())

# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth Standalone Batch glTF/GLB Conversion CLI.
Supports headless batch conversion of HoN assets (.model, .mdf, .clip) to standard glTF/GLB 2.0
without requiring an interactive Blender session or GUI.
"""

from __future__ import annotations

import argparse
import glob
import logging
import os
import sys
from typing import Dict, List, Optional

if sys.platform == "win32":
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    if hasattr(sys.stderr, "reconfigure"):
        try:
            sys.stderr.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass

try:
    from .smdl_parser import read_smdl, HonModelData
    from .clip_parser import read_clip, HonClipData
    from .mdf_parser import read_mdf, resolve_mdf_hierarchy, HonMdfData
    from .material_parser import read_material, decode_dds, decode_rxgb_normal, convert_specular_to_pbr, encode_rgba_to_png, HonMaterialData
    from .gltf_exporter import export_hon_to_gltf
    from .avatar_catalog import get_avatar_display_name, resolve_avatar_query
    from .obj_exporter import export_hon_to_obj_and_mtl, export_hon_to_mixamo_zip
except (ImportError, ValueError):
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from smdl_parser import read_smdl, HonModelData
    from clip_parser import read_clip, HonClipData
    from mdf_parser import read_mdf, resolve_mdf_hierarchy, HonMdfData
    from material_parser import read_material, decode_dds, decode_rxgb_normal, convert_specular_to_pbr, encode_rgba_to_png, HonMaterialData
    from gltf_exporter import export_hon_to_gltf
    from avatar_catalog import get_avatar_display_name, resolve_avatar_query
    from obj_exporter import export_hon_to_obj_and_mtl, export_hon_to_mixamo_zip

logger = logging.getLogger("hon_model_tools.batch_cli")


def setup_logging(verbose: bool = False, log_file: Optional[str] = None) -> None:
    """Configure structured logging for CLI execution."""
    log_level = logging.DEBUG if verbose else logging.INFO
    log_format = "[%(asctime)s] [%(levelname)s] [%(name)s] %(message)s"

    handlers: List[logging.Handler] = [logging.StreamHandler(sys.stdout)]
    if log_file:
        log_dir = os.path.dirname(os.path.abspath(log_file))
        if log_dir:
            os.makedirs(log_dir, exist_ok=True)
        handlers.append(logging.FileHandler(log_file, mode="w", encoding="utf-8"))

    logging.basicConfig(
        level=log_level,
        format=log_format,
        handlers=handlers,
        force=True
    )


def _find_texture_file(base_dir: str, texture_name: str) -> Optional[str]:
    """Search for texture file across .dds, .tga, .png with case-insensitivity."""
    if not texture_name:
        return None

    tex_rel = texture_name.replace("\\", "/").lstrip("/")
    base_name, _ = os.path.splitext(tex_rel)

    candidates = [
        os.path.join(base_dir, tex_rel),
        os.path.join(base_dir, base_name + ".dds"),
        os.path.join(base_dir, base_name + ".tga"),
        os.path.join(base_dir, base_name + ".png"),
        os.path.join(base_dir, os.path.basename(tex_rel)),
        os.path.join(base_dir, os.path.basename(base_name) + ".dds"),
        os.path.join(base_dir, os.path.basename(base_name) + ".tga"),
        os.path.join(base_dir, os.path.basename(base_name) + ".png"),
        os.path.join(base_dir, "textures", os.path.basename(tex_rel)),
        os.path.join(base_dir, "textures", os.path.basename(base_name) + ".dds"),
        os.path.join(base_dir, "textures", os.path.basename(base_name) + ".tga"),
        os.path.join(base_dir, "textures", os.path.basename(base_name) + ".png"),
        os.path.join(os.path.dirname(base_dir), "textures", os.path.basename(tex_rel)),
        os.path.join(os.path.dirname(base_dir), "textures", os.path.basename(base_name) + ".dds"),
        os.path.join(os.path.dirname(base_dir), "textures", os.path.basename(base_name) + ".tga"),
        os.path.join(os.path.dirname(base_dir), "textures", os.path.basename(base_name) + ".png"),
    ]

    for c in candidates:
        if os.path.exists(c):
            return c
    return None


_s2z_cache: Dict[str, Tuple[Any, List[str]]] = {}


def _find_texture_in_s2z(model_dir: str, texture_name: str, repo_root: Optional[str] = None) -> Optional[Tuple[bytes, str]]:
    """Search for texture file within game/textures.s2z archive and return (bytes, internal_path)."""
    if not repo_root:
        repo_root = _find_repo_root(model_dir)

    tex_s2z = os.path.join(repo_root, "games", "client", "game", "textures.s2z")
    if not os.path.exists(tex_s2z):
        return None

    try:
        import zipfile
        if "textures" not in _s2z_cache:
            zf = zipfile.ZipFile(tex_s2z, "r")
            _s2z_cache["textures"] = (zf, zf.namelist())
        zf, names = _s2z_cache["textures"]

        norm_dir = model_dir.replace("\\", "/").lower()
        subpath = None
        for marker in ("/heroes/", "/units/", "/buildings/", "/items/", "/shared/"):
            if marker in norm_dir:
                parts = norm_dir.split(marker)
                subpath = marker.strip("/") + "/" + parts[1].strip("/")
                break
        if not subpath:
            subpath = os.path.basename(norm_dir.strip("/"))

        tex_rel = texture_name.replace("\\", "/").lstrip("/")
        tex_base = os.path.splitext(os.path.basename(tex_rel))[0].lower()
        candidates = [
            f"{subpath}/{tex_rel}".lower(),
            f"{subpath}/{tex_base}.dds".lower(),
            f"{subpath}/{tex_base}_rxgb.dds".lower(),
            f"{subpath}/{tex_base}_s.dds".lower(),
            f"{subpath}/{tex_base}.png".lower(),
            f"{subpath}/{tex_base}.tga".lower(),
        ]
        for c in candidates:
            for n in names:
                if n.lower().endswith(c):
                    return zf.read(n), n
    except Exception as e:
        logger.debug("Failed reading texture from s2z: %s", e)
    return None


def resolve_materials_for_model(model: HonModelData, model_dir: str) -> List[HonMaterialData]:
    """Discover, parse, and decode textures for each mesh in a model."""
    materials: List[HonMaterialData] = []

    for mesh in model.meshes:
        mat_file = mesh.default_material
        mat_data = HonMaterialData(name=mesh.name if mesh.name else "Default")

        if mat_file:
            mat_path = _find_texture_file(model_dir, mat_file if mat_file.endswith(".material") else mat_file + ".material")
            if mat_path and os.path.exists(mat_path):
                try:
                    mat_data = read_material(mat_path)
                except Exception as e:
                    logger.warning("Failed to parse material file '%s': %s", mat_path, e)

        # Look for default textures if not declared in .material
        if not mat_data.diffuse_texture:
            diffuse_candidate = _find_texture_file(model_dir, "color.dds") or _find_texture_file(model_dir, "color.tga") or _find_texture_file(model_dir, "color.png")
            if diffuse_candidate:
                mat_data.diffuse_texture = diffuse_candidate
            else:
                mat_data.diffuse_texture = "color.dds"

        # Decode diffuse texture
        if mat_data.diffuse_texture:
            diffuse_path = _find_texture_file(model_dir, mat_data.diffuse_texture)
            if diffuse_path and diffuse_path.lower().endswith(".dds"):
                try:
                    with open(diffuse_path, "rb") as f:
                        rgba, w, h = decode_dds(f.read())
                        mat_data.diffuse_png_bytes = encode_rgba_to_png(rgba, w, h)
                except Exception as e:
                    logger.warning("Failed to decode diffuse DDS '%s': %s", diffuse_path, e)
            elif diffuse_path and diffuse_path.lower().endswith(".png"):
                try:
                    with open(diffuse_path, "rb") as f:
                        mat_data.diffuse_png_bytes = f.read()
                except Exception as e:
                    logger.warning("Failed to read diffuse PNG '%s': %s", diffuse_path, e)
            elif not diffuse_path:
                # S2Z Fallback
                s2z_match = _find_texture_in_s2z(model_dir, mat_data.diffuse_texture)
                if s2z_match:
                    s2z_bytes, s2z_name = s2z_match
                    if s2z_name.lower().endswith(".dds"):
                        try:
                            rgba, w, h = decode_dds(s2z_bytes)
                            mat_data.diffuse_png_bytes = encode_rgba_to_png(rgba, w, h)
                            logger.info("Decoded diffuse texture from s2z archive: %s (%dx%d)", s2z_name, w, h)
                        except Exception as e:
                            logger.warning("Failed to decode diffuse from s2z '%s': %s", s2z_name, e)
                    elif s2z_name.lower().endswith(".png"):
                        mat_data.diffuse_png_bytes = s2z_bytes

        # Decode normal map (handling RxGB)
        if mat_data.normal_texture:
            norm_path = _find_texture_file(model_dir, mat_data.normal_texture)
            if norm_path and norm_path.lower().endswith(".dds"):
                try:
                    with open(norm_path, "rb") as f:
                        norm_rgba, w, h = decode_rxgb_normal(f.read())
                        mat_data.normal_png_bytes = encode_rgba_to_png(norm_rgba, w, h)
                except Exception as e:
                    logger.warning("Failed to decode normal DDS '%s': %s", norm_path, e)
            elif norm_path and norm_path.lower().endswith(".png"):
                try:
                    with open(norm_path, "rb") as f:
                        mat_data.normal_png_bytes = f.read()
                except Exception as e:
                    logger.warning("Failed to read normal PNG '%s': %s", norm_path, e)
            elif not norm_path:
                # S2Z Fallback
                s2z_match = _find_texture_in_s2z(model_dir, mat_data.normal_texture)
                if s2z_match:
                    s2z_bytes, s2z_name = s2z_match
                    if s2z_name.lower().endswith(".dds"):
                        try:
                            norm_rgba, w, h = decode_rxgb_normal(s2z_bytes)
                            mat_data.normal_png_bytes = encode_rgba_to_png(norm_rgba, w, h)
                            logger.info("Decoded normal map from s2z archive: %s (%dx%d)", s2z_name, w, h)
                        except Exception as e:
                            logger.warning("Failed to decode normal map from s2z '%s': %s", s2z_name, e)
                    elif s2z_name.lower().endswith(".png"):
                        mat_data.normal_png_bytes = s2z_bytes

        # Decode specular & convert to PBR
        if mat_data.specular_texture:
            spec_path = _find_texture_file(model_dir, mat_data.specular_texture)
            if spec_path and spec_path.lower().endswith(".dds"):
                try:
                    with open(spec_path, "rb") as f:
                        spec_raw, w, h = decode_dds(f.read())
                        pbr_rgba, pw, ph = convert_specular_to_pbr(spec_raw, w, h)
                        mat_data.metallic_roughness_png_bytes = encode_rgba_to_png(pbr_rgba, pw, ph)
                except Exception as e:
                    logger.warning("Failed to decode specular DDS '%s': %s", spec_path, e)
            elif spec_path and spec_path.lower().endswith(".png"):
                try:
                    with open(spec_path, "rb") as f:
                        mat_data.metallic_roughness_png_bytes = f.read()
                except Exception as e:
                    logger.warning("Failed to read specular PNG '%s': %s", spec_path, e)
            elif not spec_path:
                s2z_match = _find_texture_in_s2z(model_dir, mat_data.specular_texture)
                if s2z_match:
                    s2z_bytes, s2z_name = s2z_match
                    if s2z_name.lower().endswith(".dds"):
                        try:
                            spec_raw, w, h = decode_dds(s2z_bytes)
                            pbr_rgba, pw, ph = convert_specular_to_pbr(spec_raw, w, h)
                            mat_data.metallic_roughness_png_bytes = encode_rgba_to_png(pbr_rgba, pw, ph)
                        except Exception as e:
                            logger.debug("Failed to decode specular from s2z: %s", e)

        # Decode glow / emissive
        if mat_data.glow_texture:
            glow_path = _find_texture_file(model_dir, mat_data.glow_texture)
            if glow_path and glow_path.lower().endswith(".dds"):
                try:
                    with open(glow_path, "rb") as f:
                        glow_raw, w, h = decode_dds(f.read())
                        mat_data.glow_png_bytes = encode_rgba_to_png(glow_raw, w, h)
                except Exception as e:
                    logger.warning("Failed to decode glow DDS '%s': %s", glow_path, e)
            elif glow_path and glow_path.lower().endswith(".png"):
                try:
                    with open(glow_path, "rb") as f:
                        mat_data.glow_png_bytes = f.read()
                except Exception as e:
                    logger.warning("Failed to read glow PNG '%s': %s", glow_path, e)
            elif not glow_path:
                s2z_match = _find_texture_in_s2z(model_dir, mat_data.glow_texture)
                if s2z_match:
                    s2z_bytes, s2z_name = s2z_match
                    if s2z_name.lower().endswith(".dds"):
                        try:
                            glow_raw, w, h = decode_dds(s2z_bytes)
                            mat_data.glow_png_bytes = encode_rgba_to_png(glow_raw, w, h)
                        except Exception as e:
                            logger.debug("Failed to decode glow from s2z: %s", e)

        materials.append(mat_data)

    return materials


def convert_file(
    input_path: str,
    output_path: str,
    export_format: str = "glb",
    bake_animations: bool = True,
    fps: int = 30,
    export_textures: bool = False,
    mixamo: bool = False,
    base_name: Optional[str] = None,
    strip_accessories: Optional[bool] = None
) -> str:
    """
    Convert a single HoN asset (.mdf, .model, or .clip) to glTF / GLB / OBJ / Mixamo ZIP.
    
    Returns:
        The path to the generated output file.
    """
    input_path = os.path.abspath(input_path)
    ext = os.path.splitext(input_path)[1].lower()
    base_dir = os.path.dirname(input_path)

    is_mixamo = mixamo or export_format.lower() == "mixamo"
    is_obj = export_format.lower() == "obj"

    if strip_accessories is None:
        strip_accessories = True if is_mixamo else False

    if not base_name:
        base_name = os.path.splitext(os.path.basename(output_path or input_path))[0]
        for sfx in ("_mixamo_upload", "_mixamo", "_upload"):
            if base_name.lower().endswith(sfx):
                base_name = base_name[:-len(sfx)]
                break

    # Determine default output path if not specified
    if not output_path:
        if is_mixamo:
            output_path = os.path.splitext(input_path)[0] + "_Mixamo_Upload.zip"
        elif is_obj:
            output_path = os.path.splitext(input_path)[0] + ".obj"
        else:
            out_ext = ".gltf" if export_format.lower() == "gltf" else ".glb"
            output_path = os.path.splitext(input_path)[0] + out_ext

    logger.info("Converting HoN asset '%s' -> '%s' (format: %s, strip_accessories: %s)",
                input_path, output_path, export_format, strip_accessories)

    model_data: Optional[HonModelData] = None
    clips_dict: Dict[str, HonClipData] = {}

    if ext == ".mdf":
        info = resolve_mdf_hierarchy(input_path)
        resolved_model_path = info["model_path"]
        if not resolved_model_path or not os.path.exists(resolved_model_path):
            raise FileNotFoundError(f"MDF '{input_path}' references model '{info['mdf'].model_file}' which was not found.")

        model_data = read_smdl(resolved_model_path)

        if bake_animations and not (is_mixamo or is_obj):
            for anim_name, clip_p in info["clip_paths"].items():
                try:
                    clip = read_clip(clip_p, clip_name=anim_name)
                    clips_dict[anim_name] = clip
                except Exception as e:
                    logger.warning("Failed to parse animation clip '%s': %s", clip_p, e)

    elif ext in (".model", ".k2model"):
        model_data = read_smdl(input_path)

        if bake_animations and not (is_mixamo or is_obj):
            # Auto-discover companion clips in same dir or clips/ subfolder
            clip_candidates = glob.glob(os.path.join(base_dir, "*.clip")) + glob.glob(os.path.join(base_dir, "clips", "*.clip"))
            for clip_p in clip_candidates:
                cname = os.path.splitext(os.path.basename(clip_p))[0]
                try:
                    clip = read_clip(clip_p, clip_name=cname)
                    clips_dict[cname] = clip
                except Exception as e:
                    logger.warning("Failed to parse companion clip '%s': %s", clip_p, e)

    elif ext == ".clip":
        if is_mixamo or is_obj:
            raise ValueError("Mixamo/OBJ export requires a 3D geometry model, not a standalone .clip animation file.")
        # Standalone clip conversion without mesh
        clip = read_clip(input_path)
        model_data = HonModelData(version=3)
        clips_dict[clip.name] = clip

    else:
        raise ValueError(f"Unsupported file extension: '{ext}'. Must be .mdf, .model, .k2model, or .clip")

    # Resolve materials
    materials = resolve_materials_for_model(model_data, base_dir)

    if is_mixamo:
        zip_path = output_path
        if not zip_path.lower().endswith(".zip"):
            if os.path.isdir(zip_path):
                zip_path = os.path.join(zip_path, f"{base_name}_Mixamo_Upload.zip")
            else:
                zip_path = os.path.splitext(zip_path)[0] + "_Mixamo_Upload.zip"
        res = export_hon_to_mixamo_zip(
            model=model_data,
            output_zip_path=zip_path,
            materials=materials,
            base_name=base_name,
            strip_accessories=strip_accessories
        )
        logger.info("Successfully exported Mixamo package: '%s'", res)
        return res

    elif is_obj:
        out_dir = output_path if os.path.isdir(output_path) else os.path.dirname(output_path) or "."
        obj_p, _, _ = export_hon_to_obj_and_mtl(
            model=model_data,
            base_name=base_name,
            output_dir=out_dir,
            materials=materials,
            strip_accessories=strip_accessories
        )
        logger.info("Successfully exported OBJ package: '%s'", obj_p)
        return obj_p

    # Export to glTF / GLB
    export_hon_to_gltf(
        model=model_data,
        clips=clips_dict,
        output_path=output_path,
        materials=materials,
        bake_animations=bake_animations,
        fps=fps,
        export_textures=export_textures
    )

    logger.info("Successfully converted '%s' to '%s' (embedded clips: %d)",
                input_path, output_path, len(clips_dict))
    return output_path


def convert_directory(
    input_dir: str,
    output_dir: str,
    export_format: str = "glb",
    bake_animations: bool = True,
    fps: int = 30,
    export_textures: bool = False,
    mixamo: bool = False,
    avatar_title: Optional[str] = None,
    strip_accessories: Optional[bool] = None
) -> List[str]:
    """
    Recursively scan directory for HoN assets and batch convert them to glTF/GLB/OBJ/Mixamo ZIP.
    
    Returns:
        List of generated output file paths.
    """
    input_dir = os.path.abspath(input_dir)
    output_dir = os.path.abspath(output_dir)
    os.makedirs(output_dir, exist_ok=True)

    is_mixamo = mixamo or export_format.lower() == "mixamo"
    is_obj = export_format.lower() == "obj"

    if is_mixamo:
        # For Mixamo auto-rigging, pick the primary high-poly mesh
        primary_file = None
        for candidate in ("model.mdf", "high.model", "model.model"):
            cand_p = os.path.join(input_dir, candidate)
            if os.path.exists(cand_p):
                primary_file = cand_p
                break

        if not primary_file:
            for root, _, files in os.walk(input_dir):
                for f in files:
                    if f.lower() == "model.mdf":
                        primary_file = os.path.join(root, f)
                        break
                    if f.lower().endswith(".mdf") and not primary_file:
                        primary_file = os.path.join(root, f)
                    elif f.lower() == "high.model" and not primary_file:
                        primary_file = os.path.join(root, f)
                    elif f.lower().endswith((".model", ".k2model")) and not primary_file:
                        primary_file = os.path.join(root, f)
                if primary_file:
                    break

        if not primary_file:
            raise FileNotFoundError(f"No valid HoN model or MDF found in directory '{input_dir}' for Mixamo export.")

        if avatar_title:
            clean_name = avatar_title.replace(" ", "_").replace("/", "_")
        else:
            folder_name = os.path.basename(input_dir)
            parent_name = os.path.basename(os.path.dirname(input_dir))
            if parent_name.lower() not in ("heroes", "units", "shared"):
                clean_name = f"{parent_name}_{folder_name}"
            else:
                clean_name = folder_name

        zip_target = os.path.join(output_dir, f"{clean_name}_Mixamo_Upload.zip")
        res = convert_file(
            input_path=primary_file,
            output_path=zip_target,
            export_format="mixamo",
            mixamo=True,
            base_name=clean_name,
            strip_accessories=strip_accessories
        )
        return [res]

    out_ext = ".obj" if is_obj else (".gltf" if export_format.lower() == "gltf" else ".glb")
    generated_files: List[str] = []

    # Priority 1: Convert all .mdf files
    mdf_files = []
    for root, _, files in os.walk(input_dir):
        for f in files:
            if f.lower().endswith(".mdf"):
                mdf_files.append(os.path.join(root, f))

    converted_model_paths = set()

    for mdf_path in mdf_files:
        rel_path = os.path.relpath(mdf_path, input_dir)
        rel_no_ext = os.path.splitext(rel_path)[0]
        out_path = os.path.join(output_dir, rel_no_ext + out_ext)
        try:
            res = convert_file(mdf_path, out_path, export_format, bake_animations, fps, export_textures, mixamo=is_mixamo, strip_accessories=strip_accessories)
            generated_files.append(res)
            # Record resolved model to avoid duplicate conversion
            info = resolve_mdf_hierarchy(mdf_path)
            if info["model_path"]:
                converted_model_paths.add(os.path.normcase(os.path.abspath(info["model_path"])))
        except Exception as e:
            logger.error("Failed to convert MDF '%s': %s", mdf_path, e)

    # Priority 2: Convert standalone .model files not referenced by an MDF
    for root, _, files in os.walk(input_dir):
        for f in files:
            if f.lower().endswith((".model", ".k2model")):
                full_model_p = os.path.abspath(os.path.join(root, f))
                if os.path.normcase(full_model_p) not in converted_model_paths:
                    rel_path = os.path.relpath(full_model_p, input_dir)
                    rel_no_ext = os.path.splitext(rel_path)[0]
                    out_path = os.path.join(output_dir, rel_no_ext + out_ext)
                    try:
                        res = convert_file(full_model_p, out_path, export_format, bake_animations, fps, export_textures, mixamo=is_mixamo, strip_accessories=strip_accessories)
                        generated_files.append(res)
                    except Exception as e:
                        logger.error("Failed to convert Model '%s': %s", full_model_p, e)

    logger.info("Batch conversion completed: %d files converted", len(generated_files))
    return generated_files


def _dir_has_model_assets(path: str) -> bool:
    """Returns True if directory (or immediate clips/ subdir) contains .model, .k2model, .mdf, or .clip files."""
    if not os.path.isdir(path):
        return False
    for r, _, files in os.walk(path):
        if any(f.lower().endswith((".model", ".k2model", ".mdf", ".clip")) for f in files):
            return True
    return False


def _extract_vfs_dir_from_s2z(vfs_rel_dir: str, repo_root: str) -> Optional[str]:
    """Extracts a VFS directory (e.g. 'heroes/rhapsody/alt') from games/client/game/resources*.s2z in K2 precedence order."""
    import re
    import tempfile
    import zipfile

    norm_prefix = vfs_rel_dir.replace("\\", "/").strip("/").lower() + "/"
    cache_dir = os.path.join(tempfile.gettempdir(), "kongor_model_vfs_cache", *norm_prefix.strip("/").split("/"))
    if _dir_has_model_assets(cache_dir):
        return os.path.abspath(cache_dir)

    game_dir = os.path.join(repo_root, "games", "client", "game")
    if not os.path.isdir(game_dir):
        return None

    archives = sorted(
        glob.glob(os.path.join(game_dir, "resources*.s2z")),
        key=lambda p: int(m.group(1)) if (m := re.search(r"resources(\d+)\.s2z$", os.path.basename(p).lower())) else -1
    )
    if not archives:
        return None

    extracted_count = 0
    for s2z_path in archives:
        try:
            with zipfile.ZipFile(s2z_path, "r") as zf:
                for member in zf.namelist():
                    if member.endswith("/"):
                        continue
                    member_lower = member.replace("\\", "/").lstrip("/").lower()
                    if member_lower.startswith(norm_prefix):
                        sub_rel = member.replace("\\", "/").lstrip("/")[len(norm_prefix):]
                        if ".." in sub_rel.split("/"):
                            continue
                        dest_path = os.path.join(cache_dir, *sub_rel.split("/"))
                        os.makedirs(os.path.dirname(dest_path), exist_ok=True)
                        with open(dest_path, "wb") as out_f:
                            out_f.write(zf.read(member))
                        extracted_count += 1
        except Exception as e:
            logger.debug("Skipping archive '%s' during VFS extraction: %s", s2z_path, e)

    if extracted_count > 0 and _dir_has_model_assets(cache_dir):
        logger.info("Staged %d VFS asset files from resources*.s2z for '%s' -> %s", extracted_count, vfs_rel_dir, cache_dir)
        return os.path.abspath(cache_dir)
    return None


def resolve_input_path(target: str, repo_root: Optional[str] = None) -> Optional[str]:
    """Resolves a file path, directory path, hero name, or alt avatar into an existing filesystem path."""
    if not target:
        return None

    # Check direct path first
    if os.path.exists(target):
        return os.path.abspath(target)

    # Determine repo root
    if not repo_root:
        repo_root = _find_repo_root()

    # 1. Check natural language Alt Avatar catalog
    avatar_match = resolve_avatar_query(target, repo_root)
    if avatar_match:
        rel_dir, key, title = avatar_match
        abs_p = os.path.join(repo_root, rel_dir)
        if os.path.isfile(abs_p) or _dir_has_model_assets(abs_p):
            return os.path.abspath(abs_p)
        for hero_root in (
            os.path.join(repo_root, "games", "client", "override", "heroes", key),
            os.path.join(repo_root, "games", "client", "ClientPackageFiles", "heroes", key),
            os.path.join(repo_root, "games", "shared", "heroes", key),
            os.path.join(repo_root, "games", "client", "game", "heroes", key),
        ):
            if _dir_has_model_assets(hero_root):
                return os.path.abspath(hero_root)
        s2z_staged = _extract_vfs_dir_from_s2z(f"heroes/{key}", repo_root)
        if s2z_staged:
            return s2z_staged

    # Normalize target separators
    target_clean = target.replace("\\", "/").strip("/")
    target_lower = target_clean.lower()

    # Handle common alt avatar aliases (e.g. 'rhapsody_alt' -> 'rhapsody/alt', 'thriller_rhapsody' -> 'rhapsody/alt')
    alias_parts = target_lower.split()
    if len(alias_parts) == 2 and alias_parts[0] in ("thriller", "alt", "disco", "hd"):
        target_clean = f"{alias_parts[1]}/{alias_parts[0]}"
    elif "_" in target_lower and not os.path.exists(os.path.join(repo_root, "games", "shared", "heroes", target_clean)):
        parts = target_clean.split("_", 1)
        if len(parts) == 2 and parts[1].startswith("alt"):
            target_clean = f"{parts[0]}/{parts[1]}"
    target_lower = target_clean.lower()

    candidate_paths = [
        os.path.join(repo_root, target_clean),
        os.path.join(repo_root, "games", "client", "override", "heroes", target_clean),
        os.path.join(repo_root, "games", "client", "ClientPackageFiles", "heroes", target_clean),
        os.path.join(repo_root, "games", "shared", "heroes", target_clean),
        os.path.join(repo_root, "games", "heroes", target_clean),
        os.path.join(repo_root, "games", "shared", "units", target_clean),
        os.path.join(repo_root, "games", "client", "game", "heroes", target_clean),
        os.path.join(repo_root, "games", target_clean)
    ]

    for cand in candidate_paths:
        if os.path.isfile(cand) or _dir_has_model_assets(cand):
            return os.path.abspath(cand)

    # Recursive search under games/
    games_dir = os.path.join(repo_root, "games")
    if os.path.isdir(games_dir):
        for root, dirs, _ in os.walk(games_dir):
            for d in dirs:
                if d.lower() == target_lower or f"{os.path.basename(root).lower()}/{d.lower()}" == target_lower:
                    found = os.path.join(root, d)
                    if _dir_has_model_assets(found):
                        return os.path.abspath(found)

    for vfs_candidate in (f"heroes/{target_clean}", target_clean):
        s2z_staged = _extract_vfs_dir_from_s2z(vfs_candidate, repo_root)
        if s2z_staged:
            return s2z_staged

    return None


def _find_repo_root(start_dir: Optional[str] = None) -> str:
    """Traverses upward until finding repository markers (Directory.Build.props, PROJECT.md)."""
    current = os.path.abspath(start_dir or os.path.dirname(__file__))
    while current and os.path.dirname(current) != current:
        if os.path.exists(os.path.join(current, "Directory.Build.props")) or os.path.exists(os.path.join(current, "PROJECT.md")):
            return current
        current = os.path.dirname(current)
    return os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", ".."))


def discover_exportable_assets(repo_root: Optional[str] = None) -> List[Dict[str, Any]]:
    """Scans the repository for all discoverable exportable 3D models, base heroes, and named alt avatars."""
    if not repo_root:
        repo_root = _find_repo_root()

    search_roots = [
        ("Hero", os.path.join(repo_root, "games", "shared", "heroes")),
        ("Hero", os.path.join(repo_root, "games", "heroes")),
        ("Hero", os.path.join(repo_root, "games", "client", "game", "heroes")),
        ("Building", os.path.join(repo_root, "games", "shared", "buildings")),
        ("Building", os.path.join(repo_root, "games", "client", "game", "buildings")),
        ("NPC", os.path.join(repo_root, "games", "shared", "npcs")),
        ("NPC", os.path.join(repo_root, "games", "client", "game", "npcs")),
        ("Pet", os.path.join(repo_root, "games", "shared", "pets")),
        ("Pet", os.path.join(repo_root, "games", "client", "game", "pets")),
        ("Unit", os.path.join(repo_root, "games", "shared", "units")),
        ("Unit", os.path.join(repo_root, "games", "client", "game", "units")),
        ("Client Asset", os.path.join(repo_root, "games", "client", "game", "heroes"))
    ]

    assets: List[Dict[str, Any]] = []
    seen_paths = set()

    for category, root_dir in search_roots:
        if not os.path.isdir(root_dir):
            continue

        for entry in os.listdir(root_dir):
            entry_path = os.path.join(root_dir, entry)
            if not os.path.isdir(entry_path):
                continue

            canon_path = os.path.normcase(os.path.abspath(entry_path))
            if canon_path in seen_paths:
                continue

            # 1. Main / Base Hero asset
            models = []
            mdfs = []
            clips = []
            textures = []

            for r, _, files in os.walk(entry_path):
                for f in files:
                    fl = f.lower()
                    if fl.endswith((".model", ".k2model")):
                        models.append(f)
                    elif fl.endswith(".mdf"):
                        mdfs.append(f)
                    elif fl.endswith(".clip"):
                        clips.append(f)
                    elif fl.endswith((".dds", ".tga", ".png", ".material")):
                        textures.append(f)

            if models or mdfs:
                seen_paths.add(canon_path)
                rel_p = os.path.relpath(entry_path, repo_root)
                assets.append({
                    "name": entry,
                    "display_name": f"{get_avatar_display_name(entry)} (Base)",
                    "category": category,
                    "model_count": len(models),
                    "mdf_count": len(mdfs),
                    "clip_count": len(clips),
                    "texture_count": len(textures),
                    "rel_path": rel_p.replace("\\", "/"),
                    "sample_model": models[0] if models else (mdfs[0] if mdfs else "")
                })

            # 2. Discover Individual Named Alt Avatars inside this hero
            for sub in os.listdir(entry_path):
                sub_path = os.path.join(entry_path, sub)
                if not os.path.isdir(sub_path):
                    continue

                if sub.lower().startswith("alt") or sub.lower().startswith("hd") or sub.lower() in ("model", "avatar"):
                    sub_models = [f for f in os.listdir(sub_path) if f.lower().endswith((".model", ".k2model"))]
                    sub_mdfs = [f for f in os.listdir(sub_path) if f.lower().endswith(".mdf")]
                    sub_clips_dir = os.path.join(sub_path, "clips")
                    sub_clips = os.listdir(sub_clips_dir) if os.path.isdir(sub_clips_dir) else [f for f in os.listdir(sub_path) if f.lower().endswith(".clip")]
                    sub_tex = [f for f in os.listdir(sub_path) if f.lower().endswith((".dds", ".tga", ".png", ".material"))]

                    if sub_models or sub_mdfs or sub_clips:
                        sub_rel = os.path.relpath(sub_path, repo_root).replace("\\", "/")
                        target_key = f"{entry}/{sub}"
                        avatar_title = get_avatar_display_name(target_key)
                        assets.append({
                            "name": target_key,
                            "display_name": avatar_title,
                            "category": "Alt Avatar",
                            "model_count": len(sub_models),
                            "mdf_count": len(sub_mdfs),
                            "clip_count": len(sub_clips),
                            "texture_count": len(sub_tex),
                            "rel_path": sub_rel,
                            "sample_model": sub_models[0] if sub_models else (sub_mdfs[0] if sub_mdfs else "")
                        })

    assets.sort(key=lambda x: (0 if x["category"] == "Hero" else 1, x["name"].lower()))
    return assets


def print_available_assets(
    assets: List[Dict[str, Any]],
    search_query: Optional[str] = None,
    category_filter: Optional[str] = None
) -> None:
    """Prints a formatted ASCII table of discoverable 3D assets and named alt avatars."""
    filtered = assets
    if category_filter:
        cat_lower = category_filter.lower()
        filtered = [a for a in filtered if a["category"].lower() == cat_lower]

    if search_query:
        s_lower = search_query.lower()
        filtered = [
            a for a in filtered
            if s_lower in a["name"].lower()
            or s_lower in a.get("display_name", "").lower()
            or s_lower in a["rel_path"].lower()
        ]

    print("\n" + "=" * 118)
    print(" 🎨 DISCOVERABLE HEROES & ALT AVATAR 3D ASSETS (HoN Model Tools)")
    print("=" * 118)
    print(f" {'Category':<12} | {'Target Name / ID':<22} | {'Avatar Display Name':<30} | {'Models':<7} | {'Clips':<6} | {'Relative Path'}")
    print("-" * 118)

    category_counts: Dict[str, int] = {}

    for a in filtered:
        cat = a["category"]
        category_counts[cat] = category_counts.get(cat, 0) + 1
        name = a["name"]
        disp_name = a.get("display_name", name)
        m_cnt = a["model_count"]
        c_cnt = a["clip_count"]
        rel_p = a["rel_path"]

        print(f" {cat:<12} | {name:<22} | {disp_name:<30} | {m_cnt:<7} | {c_cnt:<6} | {rel_p}")

    print("=" * 118)

    summary_parts = [f"{count} {cat}s" for cat, count in sorted(category_counts.items())]
    summary_str = ", ".join(summary_parts) if summary_parts else "0 items"
    print(f" Total Assets Listed: {len(filtered)} ({summary_str})")
    print("\n 💡 Quick Export Examples:")
    print("    kongor model_exporter rhapsody                 # Batch export base hero + all 10 alt avatars")
    print("    kongor model_exporter rhapsody/alt             # Export Thriller Rhapsody")
    print("    kongor model_exporter \"thriller rhapsody\"       # Export by Avatar Name")
    print("    kongor model_exporter \"clown devourer\"          # Export Clown Devourer")
    print("    kongor model_exporter \"frost pyromancer\"        # Export Frost Pyromancer\n")


def main(argv: Optional[List[str]] = None) -> int:
    """CLI entry point."""
    if argv is None:
        argv = sys.argv[1:]

    # Handle subcommands: 'export', 'list', 'blender', 'setup'
    if argv:
        first = argv[0].lower()
        if first in ("export", "convert", "save"):
            argv = argv[1:]
        elif first in ("list", "ls", "--list", "-l"):
            search = " ".join(argv[1:]) if len(argv) > 1 and not argv[1].startswith("-") else None
            repo_root = _find_repo_root()
            assets = discover_exportable_assets(repo_root)
            print_available_assets(assets, search_query=search)
            return 0
        elif first in ("install_blender", "install-blender", "setup_blender", "setup-blender", "blender", "setup"):
            try:
                from ..install_blender import main as install_blender_main
            except (ImportError, ValueError):
                from install_blender import main as install_blender_main
            return install_blender_main(argv[1:])

    parser = argparse.ArgumentParser(
        prog="hon-model-tools",
        description="Heroes of Newerth 3D Model, Skeleton & Clip Animation Exporter (Batch glTF/GLB Converter)"
    )

    parser.add_argument(
        "target",
        nargs="*",
        default=[],
        help="Hero name (e.g. 'empath', 'devourer'), 'list' to show all available assets, file path, or directory."
    )
    parser.add_argument(
        "-l", "--list",
        action="store_true",
        help="List all discoverable heroes, units, buildings, and models available for export."
    )
    parser.add_argument(
        "-i", "--input",
        default="",
        help="Explicit input file or directory (alternative to positional target)."
    )
    parser.add_argument(
        "-o", "--output",
        default="",
        help="Output file (.glb, .gltf) or destination directory (default: output/models/<target>/)."
    )
    parser.add_argument(
        "-f", "--format",
        choices=["glb", "gltf", "obj", "mixamo"],
        default="glb",
        help="Target export format (glb = single binary, gltf = JSON + .bin, obj = Wavefront OBJ, mixamo = Mixamo-ready ZIP). Default is glb."
    )
    parser.add_argument(
        "--mixamo",
        action="store_true",
        default=False,
        help="Export character as an unrigged Wavefront OBJ package bundled into a Mixamo-ready ZIP archive for instant auto-rigging."
    )
    parser.add_argument(
        "--bake-animations",
        action="store_true",
        default=True,
        help="Bake companion animation clips into the exported glTF file (default True)."
    )
    parser.add_argument(
        "--no-bake-animations",
        dest="bake_animations",
        action="store_false",
        help="Disable animation baking."
    )
    parser.add_argument(
        "--fps",
        type=int,
        default=30,
        help="Animation framerate for keyframe sampling (default 30)."
    )
    parser.add_argument(
        "--export-textures",
        action="store_true",
        default=False,
        help="Export decoded textures as separate PNG files."
    )
    parser.add_argument(
        "--strip-accessories", "--no-accessories",
        dest="strip_accessories",
        action="store_true",
        default=None,
        help="Strip weapons, shields, and detached accessories from model for humanoid auto-rigging (default True for Mixamo)."
    )
    parser.add_argument(
        "--keep-accessories",
        dest="strip_accessories",
        action="store_false",
        help="Keep weapons and accessories attached to the character model."
    )
    parser.add_argument(
        "-v", "--verbose",
        action="store_true",
        help="Enable verbose debug logging."
    )
    parser.add_argument(
        "--log-file",
        default="",
        help="Path to write structured log file."
    )

    args = parser.parse_args(argv)
    if args.mixamo:
        args.format = "mixamo"

    setup_logging(verbose=args.verbose, log_file=args.log_file if args.log_file else None)

    repo_root = _find_repo_root()

    target_str = " ".join(args.target).strip() if isinstance(args.target, list) else str(args.target or "").strip()

    if args.list or (target_str and target_str.lower() in ("list", "ls")):
        assets = discover_exportable_assets(repo_root)
        print_available_assets(assets)
        return 0

    raw_input = target_str if target_str else args.input
    if not raw_input:
        parser.print_help()
        return 1

    if raw_input.lower() in ("install_blender", "install-blender"):
        try:
            from ..install_blender import main as install_blender_main
        except (ImportError, ValueError):
            from install_blender import main as install_blender_main
        return install_blender_main([])

    avatar_match = resolve_avatar_query(raw_input, repo_root)
    avatar_title = avatar_match[2] if avatar_match else None

    resolved_input = resolve_input_path(raw_input, repo_root)
    if not resolved_input:
        logger.error("Could not resolve target '%s' to a valid HoN hero, unit, model, or directory.", raw_input)
        print("\nTip: Run 'kongor model_exporter list' to see all available exportable heroes and assets.\n")
        return 1

    logger.info("Starting HoN Model Tools CLI with resolved input: %s", resolved_input)

    # Determine smart output directory / file path
    target_name = os.path.basename(resolved_input.rstrip("/\\"))
    if not args.output:
        default_out_root = os.path.join(repo_root, "output", "models")
        os.makedirs(default_out_root, exist_ok=True)
        if os.path.isdir(resolved_input):
            out_path = os.path.join(default_out_root, target_name)
        else:
            base_no_ext, _ = os.path.splitext(target_name)
            if args.format == "mixamo":
                out_path = os.path.join(default_out_root, f"{base_no_ext}_Mixamo_Upload.zip")
            elif args.format == "obj":
                out_path = os.path.join(default_out_root, f"{base_no_ext}.obj")
            else:
                out_path = os.path.join(default_out_root, f"{base_no_ext}.{args.format}")
    else:
        out_path = args.output

    try:
        if os.path.isdir(resolved_input):
            res_files = convert_directory(
                input_dir=resolved_input,
                output_dir=out_path,
                export_format=args.format,
                bake_animations=args.bake_animations,
                fps=args.fps,
                export_textures=args.export_textures,
                mixamo=args.format == "mixamo",
                avatar_title=avatar_title,
                strip_accessories=args.strip_accessories
            )
            logger.info("SUCCESS: Directory conversion finished (%d files). Output saved to: %s", len(res_files), out_path)
            for rf in res_files:
                print(f"Exported: {rf}")
        elif os.path.isfile(resolved_input):
            res_file = convert_file(
                input_path=resolved_input,
                output_path=out_path,
                export_format=args.format,
                bake_animations=args.bake_animations,
                fps=args.fps,
                export_textures=args.export_textures,
                mixamo=args.format == "mixamo",
                base_name=avatar_title.replace(" ", "_") if avatar_title else None,
                strip_accessories=args.strip_accessories
            )
            logger.info("SUCCESS: Conversion finished. Output saved to: %s", res_file)
            print(f"Exported: {res_file}")

        return 0

    except Exception as e:
        logger.exception("Error during batch conversion: %s", e)
        return 2


if __name__ == "__main__":
    sys.exit(main())

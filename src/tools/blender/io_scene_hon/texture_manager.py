# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth Texture & Material Management for Blender.
Handles texture directory scanning, keyword heuristic matching, Principled BSDF node setup,
and DDS vertical flip compensation in Blender's shader graph.
Portions adapted from K2-Blender (MIT License, Copyright (c) 2024-2026 Anton Romanov, K2-Blender Contributors -- see LICENSE).
"""

from __future__ import annotations

import logging
import os
from typing import Any, Dict, List, Optional, Set, Tuple

logger = logging.getLogger("io_scene_hon.texture_manager")

try:
    import bpy
    BPY_AVAILABLE = True
except ImportError:
    bpy = None
    BPY_AVAILABLE = False


# ============================================================================
# Texture Keyword Heuristics & Extensions
# ============================================================================

TEX_KEYWORDS: Dict[str, List[str]] = {
    "color2":   ["_color2", "_diff2", "_team", "_tint"],
    "color3":   ["_color3", "_diff3", "_detail", "_spec", "_specular"],
    "color":    ["_color", "_diff", "_diffuse", "_albedo"],
    "normal":   ["_normal", "_nrm", "_norm"],
    "mrao":     ["_mrao", "_orm"],
    "emissive": ["_emissive", "_emis", "_glow", "_self"],
}

EXT_PRIORITY: List[str] = [".tga", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".dds"]
TEX_EXTENSIONS: Set[str] = set(EXT_PRIORITY)

_dir_cache: Dict[str, Dict[str, Dict[str, str]]] = {}


def scan_texture_dir(base_dir: str) -> Dict[str, Dict[str, str]]:
    """
    Scan a directory and build {prefix: {type: filepath}} preferring .tga / .png over .dds.
    """
    if base_dir in _dir_cache:
        return _dir_cache[base_dir]

    result: Dict[str, Dict[str, str]] = {}
    if not os.path.isdir(base_dir):
        _dir_cache[base_dir] = result
        return result

    try:
        files = os.listdir(base_dir)
    except OSError as e:
        logger.warning("Failed to list directory '%s': %s", base_dir, e)
        _dir_cache[base_dir] = result
        return result

    for f in files:
        name_lower = f.lower()
        stem, ext = os.path.splitext(name_lower)
        if ext not in TEX_EXTENSIONS:
            continue

        matched_type = None
        prefix = stem
        for tex_type, keywords in TEX_KEYWORDS.items():
            for kw in keywords:
                pos = stem.find(kw)
                if pos > 0:
                    matched_type = tex_type
                    prefix = stem[:pos]
                    break
            if matched_type:
                break

        if not matched_type:
            matched_type = "color"
            prefix = stem

        if prefix not in result:
            result[prefix] = {}
        existing = result[prefix].get(matched_type)
        if existing:
            _, old_ext = os.path.splitext(existing.lower())
            old_pri = EXT_PRIORITY.index(old_ext) if old_ext in EXT_PRIORITY else 99
            new_pri = EXT_PRIORITY.index(ext) if ext in EXT_PRIORITY else 99
            if new_pri >= old_pri:
                continue
        result[prefix][matched_type] = os.path.join(base_dir, f)

    _dir_cache[base_dir] = result
    return result


def match_prefix(tex_map: Dict[str, Dict[str, str]], mat_name: str) -> Optional[str]:
    """Find the best matching texture prefix for a material name."""
    mat_lower = mat_name.lower()
    mat_base = mat_lower.rsplit("/", 1)[-1].rsplit("\\", 1)[-1]
    if "." in mat_base:
        parts = mat_base.rsplit(".", 1)
        if parts[1].isdigit():
            mat_base = parts[0]

    if mat_base in tex_map:
        return mat_base

    for prefix in tex_map:
        if prefix.startswith(mat_base) or mat_base.startswith(prefix):
            return prefix

    for prefix in tex_map:
        if mat_base in prefix or prefix in mat_base:
            return prefix

    return None


def find_texture(base_dir: str, mat_name: str, tex_type: str) -> Optional[str]:
    """Find a texture file in base_dir matching the material name and texture type."""
    tex_map = scan_texture_dir(base_dir)
    prefix = match_prefix(tex_map, mat_name)
    if prefix and tex_type in tex_map[prefix]:
        path = tex_map[prefix][tex_type]
        logger.debug("Found %s: %s (prefix='%s')", tex_type, os.path.basename(path), prefix)
        return path
    return None


def clear_cache() -> None:
    """Clear the texture directory scan cache."""
    _dir_cache.clear()


# ============================================================================
# Blender Material & Shader Graph Setup
# ============================================================================

def _create_image_node(tree: Any, image_path: str, label: str, x_pos: float, y_pos: float) -> Any:
    """Create an image texture node and compensate for DDS vertical orientation in Blender."""
    tex = tree.nodes.new("ShaderNodeTexImage")
    tex.location = (x_pos, y_pos)
    tex.label = label
    tex.image = bpy.data.images.load(image_path, check_existing=True)

    # If DDS format, apply a per-node mapping correction (scale Y = -1, loc Y = 1)
    if os.path.splitext(image_path)[1].lower() == ".dds":
        texcoord = tree.nodes.new("ShaderNodeTexCoord")
        texcoord.location = (x_pos - 600, y_pos)
        mapping = tree.nodes.new("ShaderNodeMapping")
        mapping.location = (x_pos - 320, y_pos)
        mapping.inputs["Location"].default_value[1] = 1.0
        mapping.inputs["Scale"].default_value[1] = -1.0
        tree.links.new(texcoord.outputs["UV"], mapping.inputs["Vector"])
        tree.links.new(mapping.outputs["Vector"], tex.inputs["Vector"])

    return tex


def setup_material_from_prefix(mat: Any, type_map: Dict[str, str], tex_flags: Optional[Dict[str, bool]] = None) -> bool:
    """Set up Principled BSDF shader network for a material using pre-resolved texture maps."""
    if not BPY_AVAILABLE or mat is None:
        return False

    if tex_flags is None or tex_flags is True:
        tex_flags = {k: True for k in TEX_KEYWORDS}
    if not any(tex_flags.values()):
        return False

    mat.use_nodes = True
    tree = mat.node_tree
    tree.nodes.clear()

    output = tree.nodes.new("ShaderNodeOutputMaterial")
    output.location = (600, 0)
    bsdf = tree.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.location = (200, 0)
    tree.links.new(bsdf.outputs["BSDF"], output.inputs["Surface"])

    x_off = -600
    y = 400
    loaded = False

    if tex_flags.get("color") and "color" in type_map:
        tex = _create_image_node(tree, type_map["color"], "Color", x_off, y)
        tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
        tree.links.new(tex.outputs["Alpha"], bsdf.inputs["Alpha"])
        y -= 300
        loaded = True

    for ckey, clabel in [("color2", "Color 2"), ("color3", "Color 3")]:
        if tex_flags.get(ckey) and ckey in type_map:
            _create_image_node(tree, type_map[ckey], clabel, x_off, y)
            y -= 300
            loaded = True

    if tex_flags.get("normal") and "normal" in type_map:
        tex = _create_image_node(tree, type_map["normal"], "Normal", x_off, y)
        tex.image.colorspace_settings.name = "Non-Color"
        nmap = tree.nodes.new("ShaderNodeNormalMap")
        nmap.location = (x_off + 300, y)
        tree.links.new(tex.outputs["Color"], nmap.inputs["Color"])
        tree.links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])
        y -= 300
        loaded = True

    if tex_flags.get("mrao") and "mrao" in type_map:
        tex = _create_image_node(tree, type_map["mrao"], "MRAO", x_off, y)
        tex.image.colorspace_settings.name = "Non-Color"
        sep = tree.nodes.new("ShaderNodeSeparateColor")
        sep.location = (x_off + 300, y)
        tree.links.new(tex.outputs["Color"], sep.inputs["Color"])
        tree.links.new(sep.outputs["Red"], bsdf.inputs["Metallic"])
        tree.links.new(sep.outputs["Green"], bsdf.inputs["Roughness"])
        y -= 300
        loaded = True

    if tex_flags.get("emissive") and "emissive" in type_map:
        tex = _create_image_node(tree, type_map["emissive"], "Emissive", x_off, y)
        tree.links.new(tex.outputs["Color"], bsdf.inputs["Emission Color"])
        bsdf.inputs["Emission Strength"].default_value = 1.0
        loaded = True

    return loaded


def setup_material_textures(mat: Any, model_dir: str, mat_name: str, tex_flags: Optional[Dict[str, bool]] = None) -> bool:
    """Auto-detect textures and setup Principled BSDF for an imported material."""
    if not BPY_AVAILABLE or mat is None or not model_dir:
        return False

    tex_map = scan_texture_dir(model_dir)
    prefix = match_prefix(tex_map, mat_name)
    logger.info("Searching textures for material '%s' in %s", mat_name, model_dir)
    if prefix:
        logger.info("Matched prefix: '%s' -> %s", prefix, list(tex_map[prefix].keys()))
        return setup_material_from_prefix(mat, tex_map[prefix], tex_flags)
    return False


def assign_textures_to_objects(objects: List[Any], tex_dir: str, tex_flags: Optional[Dict[str, bool]] = None) -> int:
    """
    Batch assign textures from a directory to mesh materials matching by name or fallback order.
    Returns number of materials configured.
    """
    if not BPY_AVAILABLE:
        return 0

    clear_cache()
    tex_map = scan_texture_dir(tex_dir)
    if not tex_map:
        logger.warning("No texture files found in %s", tex_dir)
        return 0

    prefixes = sorted(tex_map.keys())
    materials: List[Any] = []
    for obj in objects:
        if getattr(obj, "type", "") != "MESH" or not getattr(obj.data, "materials", None):
            continue
        for mat in obj.data.materials:
            if mat and mat not in materials:
                materials.append(mat)

    if not materials:
        logger.info("No materials on selected objects")
        return 0

    assignments: Dict[str, str] = {}
    used_prefixes: Set[str] = set()
    for mat in materials:
        prefix = match_prefix(tex_map, mat.name)
        if prefix:
            assignments[mat.name] = prefix
            used_prefixes.add(prefix)

    unmatched_mats = [m for m in materials if m.name not in assignments]
    remaining_prefixes = [p for p in prefixes if p not in used_prefixes]

    if unmatched_mats and remaining_prefixes:
        for mat, prefix in zip(unmatched_mats, remaining_prefixes):
            assignments[mat.name] = prefix

    count = 0
    for mat in materials:
        prefix = assignments.get(mat.name)
        if not prefix:
            continue
        if setup_material_from_prefix(mat, tex_map[prefix], tex_flags):
            count += 1

    return count

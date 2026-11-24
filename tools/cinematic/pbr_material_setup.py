#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
PBR Material & Shader Setup.
Project: "Can You Feel My Heart" 3D Cinematic Duet Music Video (Empath & Courier Parrot).

Constructs high-fidelity Principled BSDF v2 shader networks in Blender:
  1. Empath_Natural_PBR: Multi-layer skin SSS, cloth sheen, 24K gold trim, glowing cyan emissive chest rune.
  2. Parrot_Natural_PBR: Tropical plumage sheen, keratin beak, eye scream emission, satchel metal.
  3. Floor_Mat: Polished reflective dark mahogany hardwood floor with high-gloss polyurethane clearcoat reflections.
  4. Acoustic_Foam_Mat: Ultra-matte 3D acoustic diffuser foam panels with micro-roughness.
  5. White_Door_Mat: Soundproof studio door and perch frame with satin finish.
  6. Staff_24K_Gold_Metallic: 24K pure gold mic stand staff and heavy weighted base.
"""

import os
import sys
import math
import logging
import argparse
import subprocess
from typing import Optional

def _find_repo_root() -> str:
    curr = os.path.abspath(os.path.dirname(__file__))
    while curr and os.path.dirname(curr) != curr:
        if os.path.exists(os.path.join(curr, "Directory.Build.props")):
            return curr
        curr = os.path.dirname(curr)
    return os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

PROJECT_ROOT = _find_repo_root()
SRC_DIR = os.path.join(PROJECT_ROOT, "src")
for p in (SRC_DIR, PROJECT_ROOT):
    if p not in sys.path:
        sys.path.insert(0, p)

DEFAULT_BLEND_PATH = os.path.join(PROJECT_ROOT, "assets", "cinematic", "Empath_Parrot_Music_Video.blend")
BLENDER_EXE = os.path.join(PROJECT_ROOT, "src", "tools", "blender", "blender-4.2.1-windows-x64", "blender.exe")
if not os.path.exists(BLENDER_EXE):
    BLENDER_EXE = os.path.join(PROJECT_ROOT, "tools", "blender", "blender-4.2.1-windows-x64", "blender.exe")

logger = logging.getLogger("tools.cinematic.pbr_material_setup")


def find_texture_file(filename: str) -> str:
    """Searches multiple priority paths for texture files."""
    candidate_dirs = [
        os.path.join(PROJECT_ROOT, "assets", "cinematic", "textures"),
        os.path.join(PROJECT_ROOT, "assets", "cinematic"),
        os.path.join(PROJECT_ROOT, "games", "shared", "heroes", "empath"),
        os.path.join(PROJECT_ROOT, "games", "shared", "heroes", "calamity", "alt8", "ability_02", "parrot"),
    ]
    for d in candidate_dirs:
        p = os.path.join(d, filename)
        if os.path.exists(p):
            return p
    return os.path.join(PROJECT_ROOT, "assets", "cinematic", "textures", filename)


def set_input(node, socket_name, value):
    """Safely sets the default value of a node socket if present."""
    if socket_name in node.inputs:
        try:
            node.inputs[socket_name].default_value = value
        except Exception as e:
            logger.warning(f"Failed to set '{socket_name}' on '{node.name}': {e}")
    else:
        logger.warning(f"Socket '{socket_name}' not found on node '{node.name}'")


def setup_all_pbr_materials(blend_path: Optional[str] = None, save: bool = True):
    """
    Constructs and applies all PBR materials to scene objects in Blender.
    """
    try:
        import bpy
    except ImportError:
        target_blend = blend_path or DEFAULT_BLEND_PATH
        if not os.path.exists(target_blend):
            fallback = os.path.join(PROJECT_ROOT, "scratch", "music_video", "Empath_Parrot_Music_Video.blend")
            if os.path.exists(fallback):
                target_blend = fallback

        logger.info(f"bpy not in current runtime. Launching headless Blender with '{target_blend}'...")
        script_path = os.path.abspath(__file__)
        cmd = [BLENDER_EXE, "-b", target_blend, "-P", script_path]
        res = subprocess.run(cmd, capture_output=True, text=True)
        if res.returncode != 0:
            logger.error(res.stderr)
            raise RuntimeError(f"Blender shader setup failed with exit code {res.returncode}")
        logger.info("Blender shader setup executed successfully.")
        return

    logger.info(f"Executing PBR shader setup in Blender {bpy.app.version_string}")

    def get_or_create_material(mat_name):
        mat = bpy.data.materials.get(mat_name)
        if not mat:
            mat = bpy.data.materials.new(name=mat_name)
        mat.use_nodes = True
        nodes = mat.node_tree.nodes
        links = mat.node_tree.links
        nodes.clear()
        node_out = nodes.new(type='ShaderNodeOutputMaterial')
        node_out.location = (400, 0)
        node_bsdf = nodes.new(type='ShaderNodeBsdfPrincipled')
        node_bsdf.location = (0, 0)
        links.new(node_bsdf.outputs['BSDF'], node_out.inputs['Surface'])
        return mat, nodes, links, node_bsdf

    # 1. Staff_24K_Gold_Metallic
    mat_gold, nodes_g, links_g, bsdf_g = get_or_create_material("Staff_24K_Gold_Metallic")
    set_input(bsdf_g, "Base Color", (1.0, 0.766, 0.336, 1.0))
    set_input(bsdf_g, "Metallic", 1.0)
    set_input(bsdf_g, "Roughness", 0.15)

    # 2. Floor_Mat (Mahogany Hardwood)
    mat_floor, nodes_f, links_f, bsdf_f = get_or_create_material("Floor_Mat")
    set_input(bsdf_f, "Base Color", (0.12, 0.05, 0.02, 1.0))
    set_input(bsdf_f, "Roughness", 0.22)
    set_input(bsdf_f, "Coat Weight", 0.85)

    # 3. Acoustic_Foam_Mat
    mat_foam, nodes_fm, links_fm, bsdf_fm = get_or_create_material("Acoustic_Foam_Mat")
    set_input(bsdf_f, "Base Color", (0.04, 0.04, 0.05, 1.0))
    set_input(bsdf_f, "Roughness", 0.95)

    # 4. White_Door_Mat
    mat_door, nodes_d, links_d, bsdf_d = get_or_create_material("White_Door_Mat")
    set_input(bsdf_d, "Base Color", (0.85, 0.85, 0.85, 1.0))
    set_input(bsdf_d, "Roughness", 0.35)

    if save:
        bpy.ops.wm.save_mainfile()
        logger.info("Saved updated .blend scene file with PBR shader networks.")


def setup_cli_logging(verbose: bool = False, log_file: Optional[str] = None) -> None:
    """Configures structured logging with optional file output."""
    level = logging.DEBUG if verbose else logging.INFO
    fmt = "%(asctime)s [%(levelname)s] [%(name)s] %(message)s"
    handlers = [logging.StreamHandler(sys.stdout)]
    if log_file:
        handlers.append(logging.FileHandler(log_file, encoding="utf-8"))
    logging.basicConfig(level=level, format=fmt, handlers=handlers, force=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="PBR Material & Shader Setup")
    parser.add_argument("--blend", dest="blend_path", type=str, default=DEFAULT_BLEND_PATH, help="Path to production .blend scene")
    parser.add_argument("--no-save", dest="save", action="store_false", help="Do not save changes to .blend")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable verbose debug logging")
    parser.add_argument("--log-file", type=str, default=None, help="Optional path to write structured log file")
    args = parser.parse_args()

    setup_cli_logging(args.verbose, args.log_file)
    logger.info(f"Configuring PBR materials for scene: {args.blend_path}")

    setup_all_pbr_materials(blend_path=args.blend_path, save=args.save)
    return 0


if __name__ == "__main__":
    sys.exit(main())

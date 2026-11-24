# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth Model Tools Standalone CLI Package.
"""

from __future__ import annotations
import sys
import os

# Ensure tools/blender is on sys.path
_current_dir = os.path.dirname(os.path.abspath(__file__))
_tools_dir = os.path.dirname(_current_dir)
_repo_root = os.path.dirname(_tools_dir)
_blender_tools = os.path.join(_repo_root, "src", "tools", "blender")
if _blender_tools not in sys.path:
    sys.path.insert(0, _blender_tools)

from io_scene_hon.smdl_parser import read_smdl, HonModelData, HonMesh, HonBone, HonVertexBoneLink
from io_scene_hon.smdl_writer import write_smdl
from io_scene_hon.clip_parser import read_clip, HonClipData, HonBoneMotion
from io_scene_hon.clip_writer import write_clip
from io_scene_hon.mdf_parser import read_mdf, resolve_mdf_hierarchy, HonMdfData
from io_scene_hon.material_parser import read_material, decode_dds, decode_rxgb_normal, convert_specular_to_pbr, encode_rgba_to_png, HonMaterialData
from io_scene_hon.gltf_exporter import export_hon_to_gltf
from io_scene_hon.batch_cli import main as cli_main, convert_file, convert_directory

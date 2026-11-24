# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth 3D Model, Skeleton & Clip Animation Pipeline for Blender 4.2+.
Native Addon & Extension package supporting SMDL v1/v3, CLIP v2, MDF, Material/DDS, and glTF 2.0 conversion.
"""

from __future__ import annotations

bl_info = {
    "name": "Heroes of Newerth Model & Animation Pipeline",
    "author": "guccidonut (Project KONGOR V); Portions & Acknowledgments: Anton Romanov (KRAT0Sz / why2k), K2-Blender & KONGOR-TOOL Contributors, RedBear",
    "version": (5, 0, 2),
    "blender": (4, 2, 0),
    "location": "File > Import/Export | View3D > Sidebar > HoN",
    "description": "Import/export Heroes of Newerth SMDL models, skeletons, CLIP animations, and convert to glTF 2.0",
    "category": "Import-Export",
    "doc_url": "https://github.com/guccidonut/PKV",
    "tracker_url": "https://github.com/guccidonut/PKV/issues"
}

# Export public API for Python usage and external tools
from .smdl_parser import read_smdl, HonModelData, HonMesh, HonBone, HonVertexBoneLink, HonSurfaceData
from .smdl_writer import write_smdl
from .clip_parser import read_clip, HonClipData, HonBoneMotion, euler_zxy_to_quaternion, quat_slerp
from .clip_writer import write_clip
from .mdf_parser import read_mdf, resolve_mdf_hierarchy, HonMdfData, HonMdfAnim, HonMdfAttachment
from .material_parser import read_material, decode_dds, decode_rxgb_normal, convert_specular_to_pbr, encode_rgba_to_png, HonMaterialData
from .texture_manager import scan_texture_dir, match_prefix, find_texture, setup_material_textures, assign_textures_to_objects
from .gltf_exporter import export_hon_to_gltf
from .scene_importer import import_hon_model, import_hon_clip
from .scene_exporter import export_hon_model, export_hon_clip, ExportOptions
from .weapon_pairing import (
    pair_weapon_to_socket,
    unpair_weapon,
    resolve_grip_point,
    find_socket_bone,
    find_hero_armature,
    set_object_origin_to_world_point,
    GripSource,
    ParentMode,
    DEFAULT_SOCKET_CANDIDATES,
)
from .ui_panels import register as register_ui, unregister as unregister_ui, BPY_AVAILABLE

if BPY_AVAILABLE:
    import bpy

    def menu_func_import(self, context):
        self.layout.operator("hon.import_model", text="Heroes of Newerth Model (.model, .k2model)")
        self.layout.operator("hon.import_clip", text="Heroes of Newerth Animation (.clip)")
        self.layout.operator("hon.import_mdf", text="Heroes of Newerth Entity (.mdf)")

    def menu_func_export(self, context):
        self.layout.operator("hon.export_gltf", text="Heroes of Newerth to glTF/GLB (.glb, .gltf)")
        self.layout.operator("hon.export_model", text="Heroes of Newerth Model (.model)")
        self.layout.operator("hon.export_clip", text="Heroes of Newerth Clip (.clip)")


def register():
    register_ui()
    if BPY_AVAILABLE:
        bpy.types.TOPBAR_MT_file_import.append(menu_func_import)
        bpy.types.TOPBAR_MT_file_export.append(menu_func_export)


def unregister():
    if BPY_AVAILABLE:
        bpy.types.TOPBAR_MT_file_export.remove(menu_func_export)
        bpy.types.TOPBAR_MT_file_import.remove(menu_func_import)
    unregister_ui()


if __name__ == "__main__":
    register()

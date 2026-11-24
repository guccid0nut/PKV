# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth Scene Importer for Blender 4.2+.
Constructs native Blender armatures, edit bones, meshes, skinning weights,
materials, and animation Actions from HoN SMDL / CLIP data.

Acknowledgments:
    Blender 5.0+ Slotted Actions, bulk foreach_set() keyframing, scale channel
    import, and dynamic child-bone sizing informed by KRAT0Sz / why2k
    (https://github.com/KRAT0Sz/KONGOR-TOOL) and K2 format insights from RedBear.
"""

from __future__ import annotations

import logging
import math
import os
from typing import Any, Dict, List, Optional, Tuple

from .smdl_parser import read_smdl, HonModelData, HonMesh, HonBone, HonSurfaceData
from .clip_parser import read_clip, HonClipData, HonBoneMotion, euler_zxy_to_quaternion
from .texture_manager import setup_material_textures, BPY_AVAILABLE

logger = logging.getLogger("io_scene_hon.scene_importer")

if BPY_AVAILABLE:
    import bpy
    from mathutils import Vector, Matrix, Euler


# ============================================================================
# Math Helpers for Bone Orientations
# ============================================================================

def round_vector(vec: Any, dec: int = 17) -> Any:
    if not BPY_AVAILABLE:
        return vec
    return Vector([round(v, dec) for v in vec])


def round_matrix(mat: Any, dec: int = 17) -> Any:
    if not BPY_AVAILABLE:
        return mat
    return Matrix([round_vector(row, dec) for row in mat])


def vec_roll_to_mat3(vec: Any, roll: float) -> Any:
    """Convert bone direction vector and roll angle to 3x3 rotation matrix."""
    target = Vector((0, 1, 0))
    nor = vec.normalized()
    axis = target.cross(nor)
    if axis.dot(axis) > 1e-6:
        axis.normalize()
        theta = target.angle(nor)
        b_matrix = Matrix.Rotation(theta, 3, axis)
    else:
        updown = 1.0 if target.dot(nor) > 0 else -1.0
        b_matrix = Matrix.Scale(updown, 3)
    r_matrix = Matrix.Rotation(roll, 3, nor)
    return r_matrix @ b_matrix


def mat3_to_vec_roll(mat: Any) -> Tuple[Any, float]:
    """Decompose 3x3 rotation matrix into bone axis direction vector and roll angle."""
    vec = mat.col[1]
    vecmat = vec_roll_to_mat3(mat.col[1], 0.0)
    vecmatinv = vecmat.inverted()
    rollmat = vecmatinv @ mat
    roll = math.atan2(rollmat[0][2], rollmat[2][2])
    return vec, roll


def bone_depth(bone: Any) -> int:
    """Calculate hierarchy depth of a bone."""
    depth = 0
    cur = getattr(bone, "parent", None)
    while cur:
        depth += 1
        cur = getattr(cur, "parent", None)
    return depth


def view_all_in_3d_view() -> bool:
    """Focus all 3D viewports on the scene content."""
    if not BPY_AVAILABLE:
        return False
    for window in bpy.context.window_manager.windows:
        screen = window.screen
        for area in screen.areas:
            if area.type == "VIEW_3D":
                for region in area.regions:
                    if region.type == "WINDOW":
                        override = {
                            "window": window,
                            "screen": screen,
                            "area": area,
                            "region": region,
                            "scene": bpy.context.scene,
                        }
                        try:
                            with bpy.context.temp_override(**override):
                                bpy.ops.view3d.view_all(center=False)
                            return True
                        except Exception:
                            pass
    return False


# ============================================================================
# Main Model Importer
# ============================================================================

def import_hon_model(
    filepath: str,
    flip_uv: bool = True,
    tex_flags: Optional[Dict[str, bool]] = None,
    import_skeleton: bool = True,
    import_skinning: bool = True
) -> Tuple[Optional[Any], Optional[Any]]:
    """
    Import a HoN .model / .k2model file directly into Blender objects.
    
    Returns:
        (last_mesh_object, armature_object)
    """
    if not BPY_AVAILABLE:
        logger.error("Blender bpy environment is not available")
        return None, None

    model_dir = os.path.dirname(os.path.abspath(filepath))
    obj_name = bpy.path.display_name_from_filepath(filepath)
    model: HonModelData = read_smdl(filepath)

    scn = bpy.context.scene
    rig = None
    bone_names: List[str] = []

    # 1. Build Armature
    if import_skeleton and len(model.bones) > 0:
        armature_data = bpy.data.armatures.new(f"{obj_name}_Armature")
        armature_data.display_type = "STICK"
        armature_data.show_names = True

        rig = bpy.data.objects.new(f"{obj_name}_Rig", armature_data)
        scn.collection.objects.link(rig)
        bpy.context.view_layer.objects.active = rig
        rig.select_set(True)

        bpy.ops.object.mode_set(mode="EDIT")
        edit_bones = armature_data.edit_bones
        created_bones = []
        parents: List[int] = []

        # Precompute rest matrices, head positions, and child adjacency for intelligent bone lengths
        bone_mats: List[Any] = []
        positions: List[Any] = []
        children_map: Dict[int, List[int]] = {idx: [] for idx in range(len(model.bones))}

        for b_idx, bone in enumerate(model.bones):
            m = bone.loc_matrix_raw if len(bone.loc_matrix_raw) == 12 else bone.loc_matrix
            if len(m) == 12:
                mat4 = Matrix([
                    (m[0], m[1], m[2], 0.0),
                    (m[3], m[4], m[5], 0.0),
                    (m[6], m[7], m[8], 0.0),
                    (m[9], m[10], m[11], 1.0)
                ])
            else:
                mat4 = Matrix([
                    (m[0], m[1], m[2], m[3]),
                    (m[4], m[5], m[6], m[7]),
                    (m[8], m[9], m[10], m[11]),
                    (m[12], m[13], m[14], m[15])
                ])

            mat4.transpose()
            mat4 = round_matrix(mat4, 4)
            bone_mats.append(mat4)
            positions.append(mat4.translation.copy())
            if 0 <= bone.parent_index < len(model.bones):
                children_map[bone.parent_index].append(b_idx)

        bmin = model.bounding_min
        bmax = model.bounding_max
        model_dim = max(
            abs(bmax[0] - bmin[0]),
            abs(bmax[1] - bmin[1]),
            abs(bmax[2] - bmin[2]),
            0.0,
        )
        if model_dim < 1.0 and positions:
            xs = [p.x for p in positions]
            ys = [p.y for p in positions]
            zs = [p.z for p in positions]
            model_dim = max(max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs), 1.0)
        default_bone_len = max(model_dim * 0.05, 2.0)

        for b_idx, bone in enumerate(model.bones):
            bone_names.append(bone.name)
            parents.append(bone.parent_index)

            mat4 = bone_mats[b_idx]
            pos = positions[b_idx]
            axis, roll = mat3_to_vec_roll(mat4.to_3x3())

            child_dists = [
                (positions[c_idx] - pos).length
                for c_idx in children_map.get(b_idx, [])
                if (positions[c_idx] - pos).length > 0.05
            ]
            if child_dists:
                bone_len = min(child_dists)
            elif 0 <= bone.parent_index < len(positions):
                p_dist = (pos - positions[bone.parent_index]).length
                bone_len = max(p_dist * 0.7, default_bone_len * 0.5) if p_dist > 0.05 else default_bone_len
            else:
                bone_len = default_bone_len
            bone_len = max(bone_len, 0.5)

            axis_dir = axis.normalized() if axis.length > 1e-6 else Vector((0.0, 1.0, 0.0))

            ebone = edit_bones.new(bone.name)
            ebone.head = pos
            ebone.tail = pos + axis_dir * bone_len
            ebone.roll = roll
            created_bones.append(ebone)

        for i, p_idx in enumerate(parents):
            if 0 <= p_idx < len(created_bones):
                created_bones[i].parent = created_bones[p_idx]

        bpy.ops.object.mode_set(mode="OBJECT")
        rig.show_in_front = True
        bpy.context.view_layer.update()

    # 2. Build Meshes
    last_mesh_obj = None
    for mesh in model.meshes:
        if not mesh.vertices:
            continue

        msh = bpy.data.meshes.new(mesh.name or "Mesh")
        faces = [
            (mesh.indices[i], mesh.indices[i + 1], mesh.indices[i + 2])
            for i in range(0, len(mesh.indices) - 2, 3)
        ] if mesh.indices else []

        msh.from_pydata(mesh.vertices, [], faces)
        msh.update()

        # Apply imported custom split normals (preserves hard edges and K2 shading)
        if mesh.normals and len(mesh.normals) == len(mesh.vertices):
            safe_normals = [
                Vector(n).normalized() if (n[0] * n[0] + n[1] * n[1] + n[2] * n[2]) > 1e-12 else Vector((0.0, 0.0, 1.0))
                for n in mesh.normals
            ]
            try:
                msh.normals_split_custom_set_from_vertices(safe_normals)
                logger.debug("Applied %d custom split vertex normals to mesh '%s'", len(safe_normals), mesh.name)
            except Exception as ex:
                logger.warning("Failed to apply custom split normals to mesh '%s': %s", mesh.name, ex)

        # Material setup
        if mesh.default_material:
            mat = bpy.data.materials.new(mesh.default_material)
            msh.materials.append(mat)
            setup_material_textures(mat, model_dir, mesh.default_material, tex_flags)

        # UV setup
        if mesh.texcoords:
            texc = mesh.texcoords
            if flip_uv:
                texc = [(u, 1.0 - v) for u, v in texc]
            uv_layer = msh.uv_layers.new(name=f"UVMain_{mesh.name}")
            for face in msh.polygons:
                for vert_idx, loop_idx in zip(face.vertices, face.loop_indices):
                    if vert_idx < len(texc):
                        uv_layer.data[loop_idx].uv = texc[vert_idx]

        # Vertex Colors
        if mesh.colors:
            col_layer = msh.vertex_colors.new(name="Col") if hasattr(msh, "vertex_colors") else msh.color_attributes.new(name="Col", type="FLOAT_COLOR", domain="POINT")
            for v_idx, col in enumerate(mesh.colors):
                if v_idx < len(col_layer.data):
                    col_layer.data[v_idx].color = col

        mesh_obj = bpy.data.objects.new(f"{mesh.name}_Object", msh)
        scn.collection.objects.link(mesh_obj)
        bpy.context.view_layer.objects.active = mesh_obj
        last_mesh_obj = mesh_obj

        # Assign sprite mode settings
        if hasattr(mesh_obj, "k2_mesh_settings"):
            if mesh.mode == 3:
                mesh_obj.k2_mesh_settings.mesh_type = "SPRITE"
            elif mesh.mode == 4:
                mesh_obj.k2_mesh_settings.mesh_type = "GROUND"

        # Skinning & Vertex Groups
        if import_skinning and rig is not None and len(bone_names) > 0:
            if mesh.bone_links:
                for v_idx, link in enumerate(mesh.bone_links):
                    for w, b_idx in zip(link.weights, link.bone_indices):
                        if w > 0.0 and 0 <= b_idx < len(bone_names):
                            b_name = bone_names[b_idx]
                            grp = mesh_obj.vertex_groups.get(b_name)
                            if not grp:
                                grp = mesh_obj.vertex_groups.new(name=b_name)
                            grp.add([v_idx], w, "REPLACE")
            elif mesh.bone_index >= 0 and mesh.bone_index < len(bone_names):
                b_name = bone_names[mesh.bone_index]
                grp = mesh_obj.vertex_groups.new(name=b_name)
                grp.add(list(range(len(mesh.vertices))), 1.0, "REPLACE")

            mod = mesh_obj.modifiers.new(name="K2_Armature", type="ARMATURE")
            mod.object = rig
            mod.use_bone_envelopes = False
            mod.use_vertex_groups = True

    # 3. Build Collision Surfaces
    for surf in model.surfaces:
        if not surf.points or not surf.tris:
            continue
        surf_msh = bpy.data.meshes.new(f"{obj_name}_surf")
        surf_msh.from_pydata(surf.points, [], surf.tris)
        surf_msh.update()
        surf_obj = bpy.data.objects.new(f"{obj_name}_surf_Object", surf_msh)
        surf_obj.display_type = "WIRE"
        scn.collection.objects.link(surf_obj)
        if hasattr(surf_obj, "k2_mesh_settings"):
            surf_obj.k2_mesh_settings.mesh_type = "COLLISION"

    if rig is not None:
        bpy.context.view_layer.objects.active = rig
        rig.select_set(True)
        bpy.ops.object.mode_set(mode="POSE")
        for b in rig.pose.bones:
            b.rotation_mode = "QUATERNION"
        bpy.ops.object.mode_set(mode="OBJECT")

    view_all_in_3d_view()
    logger.info("Imported HoN model '%s' into scene: %d meshes, %d surfs", obj_name, len(model.meshes), len(model.surfaces))
    return last_mesh_obj, rig


# ============================================================================
# Main Animation Clip Importer
# ============================================================================

def _get_or_create_fcurve(
    action: Any,
    datablock: Any,
    data_path: str,
    index: int,
    group_name: str = ""
) -> Any:
    """
    Ensure an FCurve exists across Blender 5.0+ (Slotted Actions) and Blender 4.2-4.4 (legacy Action.fcurves).
    Pure-Python friendly so API fallback branches can be unit-tested outside Blender.
    """
    if hasattr(action, "fcurve_ensure_for_datablock"):
        return action.fcurve_ensure_for_datablock(datablock, data_path, index=index, group_name=group_name)

    if hasattr(action, "fcurves"):
        fc = action.fcurves.find(data_path=data_path, index=index)
        if fc is None:
            fc = action.fcurves.new(data_path=data_path, index=index, action_group=group_name)
        return fc

    # Manual Slotted Actions API fallback (Blender 4.4 / 5.0+)
    layer = action.layers[0] if len(action.layers) > 0 else action.layers.new(name="Layer")
    strip = layer.strips[0] if len(layer.strips) > 0 else layer.strips.new(type="KEYFRAME")
    if len(action.slots) > 0:
        slot = action.slots[0]
    else:
        try:
            slot = action.slots.new(id_type="OBJECT", name=getattr(datablock, "name", "Object"))
        except TypeError:
            slot = action.slots.new(target_id_type="OBJECT")

    anim_data = getattr(datablock, "animation_data", None)
    if anim_data is not None and hasattr(anim_data, "action_slot") and getattr(anim_data, "action_slot", None) is None:
        anim_data.action_slot = slot

    try:
        cb = strip.channelbag(slot, ensure=True)
    except TypeError:
        cb = strip.channelbag(slot)

    fc = cb.fcurves.find(data_path=data_path, index=index)
    if fc is None:
        fc = cb.fcurves.new(data_path=data_path, index=index)
    return fc


def import_hon_clip(filepath: str, clip_name: str = "") -> Optional[Any]:
    """
    Import a HoN .clip file and bind it as a Blender Action onto the active armature.
    Supports Blender 4.2-4.4 and Blender 5.0+ Slotted Actions via bulk foreach_set keyframing
    across location, quaternion rotation, and scale channels.
    """
    if not BPY_AVAILABLE:
        logger.error("Blender bpy environment is not available")
        return None

    if not clip_name:
        clip_name = bpy.path.display_name_from_filepath(filepath)

    clip: HonClipData = read_clip(filepath, clip_name)

    selected = [o for o in bpy.context.selected_objects if o.type == "ARMATURE"]
    if not selected:
        selected = [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]

    if not selected:
        logger.warning("No armature found in scene to apply animation clip")
        return None

    arm_ob = selected[0]
    if not arm_ob.animation_data:
        arm_ob.animation_data_create()

    action = bpy.data.actions.new(name=clip_name)
    arm_ob.animation_data.action = action
    pose = arm_ob.pose
    armature = arm_ob.data

    num_frames = max(1, clip.num_frames)
    bones_imported = 0

    for motion in clip.bone_motions:
        b_name = motion.bone_name
        if b_name not in armature.bones:
            continue

        bone = armature.bones[b_name]
        bone_rest_matrix = Matrix(bone.matrix_local)
        if bone.parent:
            parent_rest_inv = Matrix(bone.parent.matrix_local).inverted()
            bone_rest_matrix = parent_rest_inv @ bone_rest_matrix
        bone_rest_inv = Matrix(bone_rest_matrix).inverted()

        pbone = pose.bones[b_name]
        pbone.rotation_mode = "QUATERNION"

        loc_keys: List[List[float]] = [[], [], []]
        rot_keys: List[List[float]] = [[], [], [], []]
        scale_keys: List[List[float]] = [[], [], []]

        for frame in range(num_frames):
            pos = motion.translations[frame] if frame < len(motion.translations) else (0.0, 0.0, 0.0)
            rot = motion.euler_rotations[frame] if frame < len(motion.euler_rotations) else (0.0, 0.0, 0.0)
            scl = motion.scales[frame] if frame < len(motion.scales) else (1.0, 1.0, 1.0)

            rot_mat = Euler((math.radians(rot[0]), math.radians(rot[1]), math.radians(rot[2])), "YXZ").to_matrix().to_4x4()
            scale_mat = Matrix.Diagonal(Vector((scl[0], scl[1], scl[2], 1.0)))
            transform = bone_rest_inv @ (Matrix.Translation(Vector(pos)) @ rot_mat @ scale_mat)

            b_loc, b_rot, b_scale = transform.decompose()
            frame_f = float(frame)

            loc_keys[0].extend((frame_f, b_loc.x))
            loc_keys[1].extend((frame_f, b_loc.y))
            loc_keys[2].extend((frame_f, b_loc.z))

            rot_keys[0].extend((frame_f, b_rot.w))
            rot_keys[1].extend((frame_f, b_rot.x))
            rot_keys[2].extend((frame_f, b_rot.y))
            rot_keys[3].extend((frame_f, b_rot.z))

            scale_keys[0].extend((frame_f, b_scale.x))
            scale_keys[1].extend((frame_f, b_scale.y))
            scale_keys[2].extend((frame_f, b_scale.z))

        escaped_name = b_name.replace('"', '\\"')
        path_loc = f'pose.bones["{escaped_name}"].location'
        path_rot = f'pose.bones["{escaped_name}"].rotation_quaternion'
        path_scale = f'pose.bones["{escaped_name}"].scale'

        for i in range(3):
            fc = _get_or_create_fcurve(action, arm_ob, path_loc, i, group_name=b_name)
            fc.keyframe_points.add(num_frames)
            fc.keyframe_points.foreach_set("co", loc_keys[i])
            fc.update()

        for i in range(4):
            fc = _get_or_create_fcurve(action, arm_ob, path_rot, i, group_name=b_name)
            fc.keyframe_points.add(num_frames)
            fc.keyframe_points.foreach_set("co", rot_keys[i])
            fc.update()

        for i in range(3):
            fc = _get_or_create_fcurve(action, arm_ob, path_scale, i, group_name=b_name)
            fc.keyframe_points.add(num_frames)
            fc.keyframe_points.foreach_set("co", scale_keys[i])
            fc.update()

        bones_imported += 1

    bpy.context.scene.frame_start = 0
    bpy.context.scene.frame_end = max(0, num_frames - 1)
    logger.info(
        "Imported HoN clip '%s' (%d frames, %d bones incl. scale channels) onto armature '%s'",
        clip_name, num_frames, bones_imported, arm_ob.name
    )
    return action

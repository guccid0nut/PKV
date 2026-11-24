# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth Scene Exporter for Blender 4.2+.
Exports active Blender scene meshes, collision surfaces, skeletons, and animation actions
directly into authentic HoN SMDL (.model) and CLIP (.clip) binary files,
plus companion .material and .mdf XML files.

Acknowledgments:
    Collision surface (`surf`) convex hull export architecture informed by
    KRAT0Sz / why2k (https://github.com/KRAT0Sz/KONGOR-TOOL) and RedBear.
"""

from __future__ import annotations

import logging
import math
import os
import shutil
from typing import Any, Dict, List, Optional, Set, Tuple

from .smdl_parser import HonModelData, HonMesh, HonBone, HonVertexBoneLink, HonSurfaceData
from .smdl_writer import write_smdl
from .clip_parser import HonClipData, HonBoneMotion
from .clip_writer import write_clip
from .texture_manager import BPY_AVAILABLE

logger = logging.getLogger("io_scene_hon.scene_exporter")

if BPY_AVAILABLE:
    import bpy
    import bmesh
    from mathutils import Vector, Matrix


class ExportOptions:
    """Configuration options container for HoN model and clip exports."""

    def __init__(
        self,
        apply_modifiers: bool = True,
        force_static: bool = False,
        remove_hierarchy: bool = False,
        copy_textures: bool = True,
        export_geometry: bool = True,
        export_model_def: bool = True,
        export_materials: bool = True,
        export_animation: bool = False,
        frame_start: int = 0,
        frame_end: int = 250,
        export_mode: str = "STATIC"
    ):
        self.apply_modifiers = apply_modifiers
        self.force_static = force_static
        self.remove_hierarchy = remove_hierarchy
        self.copy_textures = copy_textures
        self.export_geometry = export_geometry
        self.export_model_def = export_model_def
        self.export_materials = export_materials
        self.export_animation = export_animation
        self.frame_start = frame_start
        self.frame_end = frame_end
        self.export_mode = export_mode

    @classmethod
    def from_scene(cls, scene: Any) -> ExportOptions:
        if not hasattr(scene, "k2_export_settings"):
            return cls()
        s = scene.k2_export_settings
        return cls(
            apply_modifiers=getattr(s, "apply_modifiers", True),
            force_static=(getattr(s, "export_mode", "STATIC") == "STATIC"),
            remove_hierarchy=getattr(s, "remove_hierarchy", False),
            copy_textures=getattr(s, "copy_textures", True),
            export_geometry=getattr(s, "export_geometry", True),
            export_model_def=getattr(s, "export_model_def", True),
            export_materials=getattr(s, "export_materials", True),
            export_animation=getattr(s, "export_animation", False),
            frame_start=getattr(s, "frame_start", 0),
            frame_end=getattr(s, "frame_end", 250),
            export_mode=getattr(s, "export_mode", "STATIC")
        )


def _calc_face_signs(ftexc: List[List[Tuple[float, float]]]) -> List[Tuple[int, int, int]]:
    """Compute tangent sign bit for normal map winding."""
    fsigns = []
    for uv in ftexc:
        cross = ((uv[1][0] - uv[0][0]) * (uv[2][1] - uv[1][1])
                 - (uv[1][1] - uv[0][1]) * (uv[2][0] - uv[1][0]))
        if cross > 0:
            fsigns.append((0, 0, 0))
        else:
            fsigns.append((-1, -1, -1))
    return fsigns


def compute_surface_hull_data(
    points: List[Tuple[float, float, float]],
    tris: List[Tuple[int, int, int]]
) -> Tuple[
    List[Tuple[float, float, float, float]],
    List[Tuple[float, float, float, float, float, float]],
    Tuple[float, float, float],
    Tuple[float, float, float],
]:
    """
    Compute K2 'surf' convex collision hull metadata (planes, deduplicated edges, and AABB bounds)
    purely from vertex points and triangle index triples. Zero bpy dependency.
    """
    if not points:
        return [], [], (0.0, 0.0, 0.0), (0.0, 0.0, 0.0)

    bmin = (
        float(min(p[0] for p in points)),
        float(min(p[1] for p in points)),
        float(min(p[2] for p in points)),
    )
    bmax = (
        float(max(p[0] for p in points)),
        float(max(p[1] for p in points)),
        float(max(p[2] for p in points)),
    )

    planes: List[Tuple[float, float, float, float]] = []
    edges: List[Tuple[float, float, float, float, float, float]] = []
    seen_edges: Set[Tuple[int, int]] = set()
    n_pts = len(points)

    for i0, i1, i2 in tris:
        if i0 >= n_pts or i1 >= n_pts or i2 >= n_pts or i0 < 0 or i1 < 0 or i2 < 0:
            continue
        p0, p1, p2 = points[i0], points[i1], points[i2]

        e1x, e1y, e1z = p1[0] - p0[0], p1[1] - p0[1], p1[2] - p0[2]
        e2x, e2y, e2z = p2[0] - p0[0], p2[1] - p0[1], p2[2] - p0[2]
        nx = e1y * e2z - e1z * e2y
        ny = e1z * e2x - e1x * e2z
        nz = e1x * e2y - e1y * e2x
        n_len = math.sqrt(nx * nx + ny * ny + nz * nz)
        if n_len > 1e-8:
            inv_n = 1.0 / n_len
            nx, ny, nz = nx * inv_n, ny * inv_n, nz * inv_n
            dist = nx * p0[0] + ny * p0[1] + nz * p0[2]
            planes.append((nx, ny, nz, dist))

        for a, b in ((i0, i1), (i1, i2), (i2, i0)):
            key = (a, b) if a < b else (b, a)
            if key in seen_edges:
                continue
            seen_edges.add(key)
            va, vb = points[key[0]], points[key[1]]
            dx, dy, dz = vb[0] - va[0], vb[1] - va[1], vb[2] - va[2]
            d_len = math.sqrt(dx * dx + dy * dy + dz * dz)
            if d_len > 1e-6:
                inv_d = 1.0 / d_len
                dir_vec = (dx * inv_d, dy * inv_d, dz * inv_d)
            else:
                dir_vec = (1.0, 0.0, 0.0)
            edges.append((dir_vec[0], dir_vec[1], dir_vec[2], va[0], va[1], va[2]))

    return planes, edges, bmin, bmax


def _write_companion_material(filepath: str, mat_name: str) -> None:
    lines = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        '<material>',
        '	<parameters shader="/shared/shaders/heroes/hero_diffuse_normal_mrao_team_emissive.shader" vDiffuseColor="1 1 1" fSpecularLevel="3.0" fGlossiness="32" fOpacity="1.0" />',
        '	<phase name="shadow" cull="back" blend="alphatest">',
        '		<sampler name="diffuse" texture="color.tga" />',
        '	</phase>',
        '	<phase name="color" cull="back" blend="alphatest">',
        '		<sampler name="diffuse" texture="color.tga" />',
        '		<sampler name="normalmap" texture="normal.tga" />',
        '		<sampler name="mrao" texture="mrao.tga" />',
        '	</phase>',
        '</material>'
    ]
    with open(filepath, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def _write_companion_mdf(filepath: str, model_filename: str) -> None:
    # Do not overwrite non-empty MDF files
    if os.path.exists(filepath) and os.path.getsize(filepath) > 0:
        return

    dir_name = os.path.dirname(filepath)
    model_mdf = os.path.join(dir_name, "model.mdf")
    rel_model = os.path.basename(model_filename)
    m_name = os.path.splitext(rel_model)[0]

    # If model.mdf exists and has animations, inherit the animation definitions
    if os.path.exists(model_mdf) and os.path.getsize(model_mdf) > 0 and os.path.abspath(filepath) != os.path.abspath(model_mdf):
        with open(model_mdf, "r", encoding="utf-8") as f:
            content = f.read()
        import re
        content = re.sub(
            r'<model\s+[^>]*>',
            f'<model name="{m_name}" file="{rel_model}" type="K2" high="{rel_model}" med="{rel_model}" low="{rel_model}">',
            content,
            count=1
        )
        with open(filepath, "w", encoding="utf-8") as f:
            f.write(content)
        return

    lines = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        f'<model name="{m_name}" file="{rel_model}" type="K2" high="{rel_model}" med="{rel_model}" low="{rel_model}">',
        '</model>'
    ]
    with open(filepath, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def export_hon_model(filepath: str, opts: Optional[ExportOptions] = None) -> bytes:
    """
    Export Blender scene objects to a HoN SMDL (.model) binary file.
    """
    if not BPY_AVAILABLE:
        raise RuntimeError("Blender bpy environment is not available")

    if opts is None:
        opts = ExportOptions()

    model = HonModelData(version=3)
    export_dir = os.path.dirname(os.path.abspath(filepath))
    if export_dir:
        os.makedirs(export_dir, exist_ok=True)

    # Gather selected or scene objects
    objects = bpy.context.selected_objects if bpy.context.selected_objects else bpy.context.scene.objects
    mesh_objs = [o for o in objects if o.type == "MESH"]
    arm_objs = [o for o in objects if o.type == "ARMATURE"]

    armature = arm_objs[0] if arm_objs and not opts.force_static else None
    bone_names: List[str] = []

    orig_pose_position = None
    if armature is not None:
        orig_pose_position = armature.data.pose_position
        armature.data.pose_position = 'REST'
        bpy.context.view_layer.update()

    # 1. Build Bones
    if armature is not None:
        arm_data = armature.data
        for b_idx, bone in enumerate(arm_data.bones):
            bone_names.append(bone.name)
            p_idx = -1
            if bone.parent and not opts.remove_hierarchy:
                if bone.parent.name in arm_data.bones:
                    p_idx = list(arm_data.bones.keys()).index(bone.parent.name)

            if bone.name == "Scene Root":
                loc_12 = (
                    1.0, 0.0, 0.0,
                    0.0, 1.0, 0.0,
                    0.0, 0.0, 1.0,
                    0.0, 0.0, 0.0
                )
                inv_12 = (
                    1.0, 0.0, 0.0,
                    0.0, 1.0, 0.0,
                    0.0, 0.0, 1.0,
                    0.0, 0.0, 0.0
                )
            else:
                loc_mat = bone.matrix_local.transposed()
                inv_mat = bone.matrix_local.inverted().transposed()
                loc_12 = (
                    loc_mat[0][0], loc_mat[0][1], loc_mat[0][2],
                    loc_mat[1][0], loc_mat[1][1], loc_mat[1][2],
                    loc_mat[2][0], loc_mat[2][1], loc_mat[2][2],
                    loc_mat[3][0], loc_mat[3][1], loc_mat[3][2]
                )
                inv_12 = (
                    inv_mat[0][0], inv_mat[0][1], inv_mat[0][2],
                    inv_mat[1][0], inv_mat[1][1], inv_mat[1][2],
                    inv_mat[2][0], inv_mat[2][1], inv_mat[2][2],
                    inv_mat[3][0], inv_mat[3][1], inv_mat[3][2]
                )

            model.bones.append(HonBone(
                index=b_idx,
                parent_index=p_idx,
                name=bone.name,
                loc_matrix_raw=loc_12,
                inv_matrix_raw=inv_12
            ))

    # 2. Build Meshes
    for m_idx, obj in enumerate(mesh_objs):
        print(f"[EXPORT_DEBUG] Processing mesh {m_idx}: {obj.name}, type={obj.type}", flush=True)
        m_type = "NORMAL"
        if hasattr(obj, "k2_mesh_settings"):
            m_type = obj.k2_mesh_settings.mesh_type

        if m_type == "COLLISION":
            # Triangulate collision surface via BMesh and compute full K2 surf planes/edges/bounds
            surf_bm = bmesh.new()
            if opts.apply_modifiers:
                depsgraph = bpy.context.evaluated_depsgraph_get()
                eval_obj = obj.evaluated_get(depsgraph)
                surf_bm.from_object(eval_obj, depsgraph)
            else:
                surf_bm.from_mesh(obj.data)
            bmesh.ops.triangulate(surf_bm, faces=surf_bm.faces[:])
            surf_bm.verts.ensure_lookup_table()
            surf_bm.faces.ensure_lookup_table()

            pts = [(v.co.x, v.co.y, v.co.z) for v in surf_bm.verts]
            tris = [(f.verts[0].index, f.verts[1].index, f.verts[2].index) for f in surf_bm.faces if len(f.verts) == 3]
            surf_bm.free()

            planes, edges, surf_bmin, surf_bmax = compute_surface_hull_data(pts, tris)
            logger.debug(
                "Exported collision surface '%s': %d points, %d tris, %d planes, %d edges",
                obj.name, len(pts), len(tris), len(planes), len(edges)
            )
            model.surfaces.append(HonSurfaceData(
                index=len(model.surfaces),
                planes=planes,
                points=pts,
                edges=edges,
                tris=tris,
                bounding_min=surf_bmin,
                bounding_max=surf_bmax
            ))
            continue

        # Precompute bone links for each raw vertex from obj.data
        raw_bone_links = []
        if not opts.force_static and bone_names and obj.vertex_groups:
            for v in obj.data.vertices:
                weights = [0.0, 0.0, 0.0, 0.0]
                indices_list = [-1, -1, -1, -1]
                slot = 0
                sorted_groups = sorted(v.groups, key=lambda g: g.weight, reverse=True)
                for g in sorted_groups:
                    if slot < 4 and g.group < len(obj.vertex_groups):
                        g_name = obj.vertex_groups[g.group].name
                        if g_name in bone_names:
                            weights[slot] = g.weight
                            indices_list[slot] = bone_names.index(g_name)
                            slot += 1
                total_w = sum(weights)
                if total_w > 0.0:
                    weights = [w / total_w for w in weights]
                else:
                    weights = [1.0, 0.0, 0.0, 0.0]
                    indices_list = [0, -1, -1, -1]
                raw_bone_links.append(HonVertexBoneLink(weights=weights, bone_indices=indices_list))

        # Extract geometry cleanly and safely via BMesh
        # This completely avoids Blender 4.2 RNA cache bugs (CurveMapPoint_location_get segfaults) [AF-012]
        bm = bmesh.new()
        if opts.apply_modifiers:
            depsgraph = bpy.context.evaluated_depsgraph_get()
            eval_obj = obj.evaluated_get(depsgraph)
            bm.from_object(eval_obj, depsgraph)
        else:
            bm.from_mesh(obj.data)

        bmesh.ops.triangulate(bm, faces=bm.faces[:])
        bm.verts.ensure_lookup_table()
        bm.faces.ensure_lookup_table()

        uv_layer = bm.loops.layers.uv.active

        v_map = {}
        out_verts = []
        out_normals = []
        out_texcoords = []
        out_tangents = []
        out_signs = []
        out_bone_links = []
        out_indices = []

        for f in bm.faces:
            # Determine bitangent sign byte (0 = +1, 255 = -1) from K2 UV winding parity
            face_sign = 0
            if uv_layer is not None and len(f.loops) >= 3:
                uv0 = f.loops[0][uv_layer].uv
                uv1 = f.loops[1][uv_layer].uv
                uv2 = f.loops[2][uv_layer].uv
                du1, dv1 = uv1.x - uv0.x, (1.0 - uv1.y) - (1.0 - uv0.y)
                du2, dv2 = uv2.x - uv1.x, (1.0 - uv2.y) - (1.0 - uv1.y)
                cross_uv = du1 * dv2 - dv1 * du2
                face_sign = 0 if cross_uv >= 0.0 else 255

            for loop in f.loops:
                v = loop.vert
                vi = v.index
                pos_x, pos_y, pos_z = v.co.x, v.co.y, v.co.z

                norm = loop.calc_normal()
                norm_len = norm.length
                if norm_len > 1e-6:
                    norm = norm.normalized()
                    nx, ny, nz = round(norm.x, 4), round(norm.y, 4), round(norm.z, 4)
                else:
                    nx, ny, nz = 0.0, 0.0, 1.0

                if uv_layer is not None:
                    uv = loop[uv_layer].uv
                    u = round(uv.x, 5)
                    v_coord = round(1.0 - uv.y, 5)
                else:
                    u, v_coord = 0.0, 0.0

                # Compute orthonormal tangent
                if abs(nx) < 0.9:
                    tx, ty, tz = 0.0, nz, -ny
                else:
                    tx, ty, tz = -nz, 0.0, nx
                t_len = math.sqrt(tx * tx + ty * ty + tz * tz)
                if t_len > 1e-6:
                    inv_t = 1.0 / t_len
                    tx = round(tx * inv_t, 4)
                    ty = round(ty * inv_t, 4)
                    tz = round(tz * inv_t, 4)
                else:
                    tx, ty, tz = 1.0, 0.0, 0.0

                key = (vi, u, v_coord, nx, ny, nz, face_sign)
                if key in v_map:
                    idx = v_map[key]
                else:
                    idx = len(out_verts)
                    v_map[key] = idx
                    out_verts.append((pos_x, pos_y, pos_z))
                    out_normals.append((nx, ny, nz))
                    out_texcoords.append((u, v_coord))
                    out_tangents.append((tx, ty, tz))
                    out_signs.append(face_sign)
                    if raw_bone_links and vi < len(raw_bone_links):
                        out_bone_links.append(raw_bone_links[vi])
                out_indices.append(idx)

        bm.free()

        mat_name = "material"
        if obj.data.materials:
            raw_name = obj.data.materials[0].name
            if not raw_name.lower().startswith("material"):
                mat_name = raw_name
        mode = 1 if out_bone_links else 2
        if m_type == "SPRITE":
            mode = 3
        if len(out_verts) > 65535:
            logger.error(
                "[HF-019] Mesh '%s' has %d deduplicated vertices (> 65,535). "
                "K2 engine Direct3D 9 model loader strictly uses 16-bit indices (D3DFMT_INDEX16); "
                "indices exceeding 65,535 will overflow/truncate and cause catastrophic spiderweb mesh explosion in-engine. "
                "Apply a Decimate modifier in Blender to reduce vertex count below 65,535 before export!",
                obj.name, len(out_verts)
            )

        hon_mesh = HonMesh(
            index=m_idx,
            name=obj.name,
            default_material=mat_name,
            num_vertices=len(out_verts),
            num_faces=len(out_indices) // 3,
            mode=mode,
            vertices=out_verts,
            normals=out_normals,
            texcoords=out_texcoords,
            tangents=out_tangents,
            signs=out_signs,
            indices=out_indices,
            bone_links=out_bone_links
        )
        model.meshes.append(hon_mesh)

    if model.meshes:
        primary = model.meshes[0]
        if primary.vertices:
            model.bounding_min = (
                float(min(v[0] for v in primary.vertices)),
                float(min(v[1] for v in primary.vertices)),
                float(min(v[2] for v in primary.vertices)),
            )
            model.bounding_max = (
                float(max(v[0] for v in primary.vertices)),
                float(max(v[1] for v in primary.vertices)),
                float(max(v[2] for v in primary.vertices)),
            )

    result_bytes = write_smdl(model, filepath)

    if opts.export_materials:
        mat_path = os.path.join(export_dir, "material.material")
        if not os.path.exists(mat_path):
            _write_companion_material(mat_path, "material")

    if opts.export_model_def:
        mdf_path = os.path.join(export_dir, f"{os.path.splitext(os.path.basename(filepath))[0]}.mdf")
        if not os.path.exists(mdf_path):
            _write_companion_mdf(mdf_path, filepath)

    if armature is not None:
        armature.data.pose_position = 'POSE'
        bpy.context.view_layer.update()

    logger.info("Exported scene to HoN model: %s (%d bytes)", filepath, len(result_bytes))
    return result_bytes


def _wrap_deg(deg: float) -> float:
    """Wrap angle in degrees to [-180.0, +180.0]."""
    return (deg + 180.0) % 360.0 - 180.0


def _decompose_hon_euler_track(
    matrices: List[Any],
    bone_name: str = "",
    is_cyclic: bool = False
) -> List[Tuple[float, float, float]]:
    """
    Decompose a sequence of bone relative transform matrices into HoN Euler angles (pitch, roll, yaw) in degrees.
    Rotation order in HoN: R = R_z(yaw) * R_x(pitch) * R_y(roll).
    
    Guarantees:
      1. Dual-branch continuous tracking via Viterbi dynamic programming.
      2. Knee hinge stabilization: removes mathematical gimbal lock roll artifacts on leg/calf bones.
      3. All angles strictly wrapped to [-180.0, +180.0] degrees (zero multi-thousand-degree windup).
      4. Seamless loop blend on cyclic clips (walk/run/idle/default).
    """
    N = len(matrices)
    if N == 0:
        return []

    is_knee = ("Leg" in bone_name or "Calf" in bone_name) and ("UpLeg" not in bone_name and "Thigh" not in bone_name)

    cands = []
    for m in matrices:
        e = m.to_euler('YXZ')
        p1 = _wrap_deg(math.degrees(e.x))
        r1 = _wrap_deg(math.degrees(e.y))
        y1 = _wrap_deg(math.degrees(e.z))

        p2 = _wrap_deg(180.0 - p1)
        r2 = _wrap_deg(r1 + 180.0)
        y2 = _wrap_deg(y1 + 180.0)

        cands.append(([p1, r1, y1], [p2, r2, y2]))

    # Viterbi global optimization over dual branches
    best_path = None
    min_total_cost = float('inf')

    for s0 in [0, 1]:
        dp = [{} for _ in range(N)]
        twist0 = abs(cands[0][s0][1]) + abs(cands[0][s0][2])
        dp[0][s0] = (twist0 * 0.5, [s0])

        for f in range(1, N):
            for s_curr in [0, 1]:
                cand_curr = cands[f][s_curr]
                twist_curr = abs(cand_curr[1]) + abs(cand_curr[2])
                best_prev_cost = float('inf')
                best_prev_path = None

                for s_prev in dp[f - 1]:
                    prev_cost, prev_path = dp[f - 1][s_prev]
                    cand_prev = cands[f - 1][s_prev]

                    dp_val = abs(_wrap_deg(cand_curr[0] - cand_prev[0]))
                    dr_val = abs(_wrap_deg(cand_curr[1] - cand_prev[1]))
                    dy_val = abs(_wrap_deg(cand_curr[2] - cand_prev[2]))
                    step_jump = max(dp_val, dr_val, dy_val)

                    step_cost = step_jump ** 2 + twist_curr * 0.5
                    total_step = prev_cost + step_cost
                    if total_step < best_prev_cost:
                        best_prev_cost = total_step
                        best_prev_path = prev_path + [s_curr]

                dp[f][s_curr] = (best_prev_cost, best_prev_path)

        if is_cyclic:
            for s_last in [0, 1]:
                if s_last in dp[N - 1]:
                    cost_last, path_last = dp[N - 1][s_last]
                    cand_last = cands[N - 1][s_last]
                    cand_first = cands[0][s0]

                    dp_seam = abs(_wrap_deg(cand_first[0] - cand_last[0]))
                    dr_seam = abs(_wrap_deg(cand_first[1] - cand_last[1]))
                    dy_seam = abs(_wrap_deg(cand_first[2] - cand_last[2]))
                    seam_jump = max(dp_seam, dr_seam, dy_seam)
                    total_cycle_cost = cost_last + 3.0 * (seam_jump ** 2)

                    if total_cycle_cost < min_total_cost:
                        min_total_cost = total_cycle_cost
                        best_path = path_last
        else:
            for s_last in [0, 1]:
                if s_last in dp[N - 1]:
                    cost_last, path_last = dp[N - 1][s_last]
                    if cost_last < min_total_cost:
                        min_total_cost = cost_last
                        best_path = path_last

    track = [list(cands[f][best_path[f]]) for f in range(N)]

    # Planar hinge constraint for knee bones near gimbal zone:
    # A physical humanoid knee cannot roll; isolate the pure hinge bend in pitch and twist in yaw.
    if is_knee:
        for f in range(N):
            p, r, y = track[f]
            # Planar hinge: a physical humanoid knee has 0 roll; transfer roll to yaw
            y = _wrap_deg(y + r)
            r = 0.0
            track[f] = [p, r, y]

    # Seamless loop blend on last 4 frames if cyclic
    if is_cyclic and N >= 8:
        blend_count = min(4, N // 3)
        f1 = track[0]
        for i in range(1, blend_count + 1):
            f_idx = N - i
            t = (blend_count + 1 - i) / float(blend_count + 1)
            for c in range(3):
                delta = _wrap_deg(f1[c] - track[f_idx][c])
                track[f_idx][c] = _wrap_deg(track[f_idx][c] + delta * (t * 0.85))

    return [(_wrap_deg(r[0]), _wrap_deg(r[1]), _wrap_deg(r[2])) for r in track]


def export_hon_clip(filepath: str, opts: Optional[ExportOptions] = None, armature_obj: Optional[Any] = None) -> bytes:
    """
    Export Blender Action animation into a HoN CLIP (.clip) binary file.
    """
    if not BPY_AVAILABLE:
        raise RuntimeError("Blender bpy environment is not available")

    if opts is None:
        opts = ExportOptions()

    if armature_obj is not None and getattr(armature_obj, "type", "") == "ARMATURE":
        arm_ob = armature_obj
    else:
        arm_objs = [o for o in bpy.context.selected_objects if o.type == "ARMATURE"]
        if not arm_objs:
            if bpy.context.view_layer.objects.active and bpy.context.view_layer.objects.active.type == "ARMATURE":
                arm_objs = [bpy.context.view_layer.objects.active]
            else:
                arm_objs = [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]

        if not arm_objs:
            raise ValueError("No armature object found to export animation clip")

        arm_ob = arm_objs[0]

    if hasattr(arm_ob.data, "pose_position") and arm_ob.data.pose_position != 'POSE':
        arm_ob.data.pose_position = 'POSE'
        bpy.context.view_layer.update()

    pose = arm_ob.pose
    scene = bpy.context.scene

    clip = HonClipData(
        name=os.path.splitext(os.path.basename(filepath))[0],
        num_frames=(opts.frame_end - opts.frame_start + 1)
    )

    motions_map: Dict[str, HonBoneMotion] = {}

    orig_modes = {pb.name: pb.rotation_mode for pb in pose.bones}
    
    # Inspect active action to preserve each bone's natural rotation mode (Euler vs Quaternion)
    act = arm_ob.animation_data.action if arm_ob.animation_data else None
    if act and hasattr(act, "slots") and len(act.slots) > 0 and hasattr(arm_ob.animation_data, "action_slot"):
        valid_slots = list(act.slots)
        if not arm_ob.animation_data.action_slot or arm_ob.animation_data.action_slot not in valid_slots:
            arm_ob.animation_data.action_slot = act.slots[0]
            bpy.context.view_layer.update()
    bone_fcurve_modes: Dict[str, str] = {}
    if act:
        fcurves_list = []
        if hasattr(act, "layers"):
            for layer in act.layers:
                for strip in layer.strips:
                    for cb in strip.channelbags:
                        fcurves_list.extend(cb.fcurves)
        elif hasattr(act, "fcurves"):
            fcurves_list = list(act.fcurves)

        for fc in fcurves_list:
            if "pose.bones[" in fc.data_path:
                try:
                    b_name = fc.data_path.split('"')[1]
                    if "rotation_euler" in fc.data_path:
                        bone_fcurve_modes[b_name] = 'XYZ'
                    elif "rotation_quaternion" in fc.data_path and b_name not in bone_fcurve_modes:
                        bone_fcurve_modes[b_name] = 'QUATERNION'
                except Exception:
                    pass

    for pb in pose.bones:
        if pb.name in bone_fcurve_modes:
            pb.rotation_mode = bone_fcurve_modes[pb.name]

    bone_matrices: Dict[str, List[Any]] = {pb.name: [] for pb in pose.bones}
    try:
        for f_idx, frame in enumerate(range(opts.frame_start, opts.frame_end + 1)):
            scene.frame_set(frame)
            for b_idx, pbone in enumerate(pose.bones):
                b_name = pbone.name
                if b_name not in motions_map:
                    motions_map[b_name] = HonBoneMotion(bone_index=b_idx, bone_name=b_name)

                motion = motions_map[b_name]
                if b_name == "Scene Root":
                    motion.translations.append((0.0, 0.0, 0.0))
                    motion.scales.append((1.0, 1.0, 1.0))
                    motion.visibilities.append(255.0)
                    bone_matrices[b_name].append(None)
                else:
                    matrix = pbone.matrix
                    if pbone.parent and pbone.parent.name != "Scene Root":
                        matrix = pbone.parent.matrix.inverted() @ matrix

                    pos = matrix.to_translation()
                    scale = matrix.to_scale()

                    motion.translations.append((pos.x, pos.y, pos.z))
                    motion.scales.append((scale.x, scale.y, scale.z))
                    motion.visibilities.append(255.0)
                    bone_matrices[b_name].append(matrix.copy())
    finally:
        for pb in pose.bones:
            if pb.name in orig_modes:
                pb.rotation_mode = orig_modes[pb.name]

    # Check if clip is a cyclic loop (walk, run, idle, default, etc.)
    base_name = os.path.splitext(os.path.basename(filepath))[0].lower()
    is_cyclic = any(kw in base_name for kw in ["walk", "run", "idle", "default", "bored", "portrait"])

    # Decompose rotation tracks with continuous Euler optimization
    for b_name, motion in motions_map.items():
        if b_name == "Scene Root":
            motion.euler_rotations = [(0.0, 0.0, 0.0)] * len(motion.translations)
        else:
            matrices = bone_matrices.get(b_name, [])
            motion.euler_rotations = _decompose_hon_euler_track(matrices, bone_name=b_name, is_cyclic=is_cyclic)

    clip.bone_motions = list(motions_map.values())
    clip.num_bones = len(clip.bone_motions)
    result_bytes = write_clip(clip, filepath)
    logger.info("Exported HoN clip: %s (%d frames, %d bytes)", filepath, clip.num_frames, len(result_bytes))
    return result_bytes

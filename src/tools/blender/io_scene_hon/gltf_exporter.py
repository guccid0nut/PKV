# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth to glTF / GLB 2.0 Exporter.
Exports HoN 3D models, skeletal hierarchies, skinned armatures (JOINTS_0, WEIGHTS_0, inverseBindMatrices),
PBR materials, and clip animations into standard glTF 2.0 (.glb binary and .gltf JSON + .bin).
"""

from __future__ import annotations

import io
import json
import logging
import math
import os
import struct
from typing import BinaryIO, Dict, List, Optional, Tuple, Union

from .smdl_parser import HonModelData, HonMesh, HonBone
from .clip_parser import HonClipData, HonBoneMotion, quat_normalize
from .material_parser import HonMaterialData

logger = logging.getLogger("io_scene_hon.gltf_exporter")


def _safe_float(val: float, fallback: float = 0.0) -> float:
    """Ensure float is finite and valid for JSON serialization."""
    if math.isnan(val) or math.isinf(val):
        return fallback
    return round(val, 6)


class GltfBufferBuilder:
    """Helper to accumulate binary buffers, bufferViews, and accessors."""

    def __init__(self) -> None:
        self.stream = io.BytesIO()
        self.buffer_views: List[Dict[str, any]] = []
        self.accessors: List[Dict[str, any]] = []

    def add_buffer_view(self, data: bytes, target: Optional[int] = None) -> int:
        offset = self.stream.tell()
        self.stream.write(data)

        # 4-byte alignment padding
        pad = (4 - (len(data) % 4)) % 4
        if pad > 0:
            self.stream.write(b"\x00" * pad)

        view_idx = len(self.buffer_views)
        view_entry: Dict[str, any] = {
            "buffer": 0,
            "byteOffset": offset,
            "byteLength": len(data)
        }
        if target is not None:
            view_entry["target"] = target
        self.buffer_views.append(view_entry)
        return view_idx

    def add_accessor(
        self,
        buffer_view_idx: int,
        component_type: int,
        count: int,
        accessor_type: str,
        min_vals: Optional[List[float]] = None,
        max_vals: Optional[List[float]] = None,
        byte_offset: int = 0
    ) -> int:
        acc_idx = len(self.accessors)
        acc_entry: Dict[str, any] = {
            "bufferView": buffer_view_idx,
            "byteOffset": byte_offset,
            "componentType": component_type,
            "count": count,
            "type": accessor_type
        }
        if min_vals is not None:
            acc_entry["min"] = [_safe_float(v) for v in min_vals]
        if max_vals is not None:
            acc_entry["max"] = [_safe_float(v) for v in max_vals]

        self.accessors.append(acc_entry)
        return acc_idx

    def get_binary_bytes(self) -> bytes:
        return self.stream.getvalue()


def _convert_matrix_hon_to_gltf(mat4x4: Tuple[float, ...]) -> Tuple[float, ...]:
    """
    Convert a 4x4 matrix from HoN (Z-Up right-handed) to glTF (Y-Up right-handed).
    Coordinate transformation: x' = x, y' = z, z' = -y.
    Transformation matrix C:
      [1,  0,  0, 0]
      [0,  0,  1, 0]
      [0, -1,  0, 0]
      [0,  0,  0, 1]
    M_gltf = C * M_hon * C^(-1)
    """
    m = mat4x4
    # Unpack HoN row-major matrix:
    # [m0,  m1,  m2,  m3]
    # [m4,  m5,  m6,  m7]
    # [m8,  m9,  m10, m11]
    # [m12, m13, m14, m15]
    # Resulting column-major glTF matrix (glTF matrices are column-major):
    # col0: [m0,  m8, -m4, 0]
    # col1: [m2,  m10, -m6, 0]
    # col2: [-m1, -m9, m5, 0]
    # col3: [m12, m14, -m13, 1]
    return (
        m[0], m[8], -m[4], 0.0,
        m[2], m[10], -m[6], 0.0,
        -m[1], -m[9], m[5], 0.0,
        m[12], m[14], -m[13], 1.0
    )


def _decompose_matrix(m: Tuple[float, ...]) -> Tuple[Tuple[float, float, float], Tuple[float, float, float, float], Tuple[float, float, float]]:
    """
    Decompose a 4x4 row-major matrix into (translation, rotation_quat, scale).
    """
    # Translation
    tx, ty, tz = m[12], m[13], m[14]

    # Basis vectors
    sx = math.sqrt(m[0]**2 + m[1]**2 + m[2]**2)
    sy = math.sqrt(m[4]**2 + m[5]**2 + m[6]**2)
    sz = math.sqrt(m[8]**2 + m[9]**2 + m[10]**2)

    sx = sx if sx > 1e-6 else 1.0
    sy = sy if sy > 1e-6 else 1.0
    sz = sz if sz > 1e-6 else 1.0

    # Normalized rotation matrix
    r00, r01, r02 = m[0] / sx, m[1] / sx, m[2] / sx
    r10, r11, r12 = m[4] / sy, m[5] / sy, m[6] / sy
    r20, r21, r22 = m[8] / sz, m[9] / sz, m[10] / sz

    # Convert rotation matrix to quaternion
    trace = r00 + r11 + r22
    if trace > 0.0:
        s = 0.5 / math.sqrt(trace + 1.0)
        qw = 0.25 / s
        qx = (r12 - r21) * s
        qy = (r20 - r02) * s
        qz = (r01 - r10) * s
    elif (r00 > r11) and (r00 > r22):
        s = 2.0 * math.sqrt(1.0 + r00 - r11 - r22)
        qw = (r12 - r21) / s
        qx = 0.25 * s
        qy = (r10 + r01) / s
        qz = (r20 + r02) / s
    elif r11 > r22:
        s = 2.0 * math.sqrt(1.0 + r11 - r00 - r22)
        qw = (r20 - r02) / s
        qx = (r10 + r01) / s
        qy = 0.25 * s
        qz = (r21 + r12) / s
    else:
        s = 2.0 * math.sqrt(1.0 + r22 - r00 - r11)
        qw = (r01 - r10) / s
        qx = (r20 + r02) / s
        qy = (r21 + r12) / s
        qz = 0.25 * s

    q = quat_normalize((qx, qy, qz, qw))
    return ((tx, ty, tz), q, (sx, sy, sz))


def export_hon_to_gltf(
    model: HonModelData,
    clips: Optional[Union[Dict[str, HonClipData], List[HonClipData]]] = None,
    output_path: str = "",
    materials: Optional[List[HonMaterialData]] = None,
    bake_animations: bool = True,
    fps: int = 30,
    export_textures: bool = False
) -> bytes:
    """
    Convert a HoN model and animations into standard glTF / GLB 2.0 bytes.
    
    Args:
        model: HonModelData containing skeleton and geometry meshes.
        clips: Optional dictionary or list of HonClipData animation tracks.
        output_path: Optional path to write .glb or .gltf file.
        materials: Optional list of HonMaterialData for meshes.
        bake_animations: Whether to bake skeletal animation tracks.
        fps: Target framerate for animation sampling (default 30).
        export_textures: Whether to save extracted textures alongside output file.
        
    Returns:
        GLB binary bytes (or writes .gltf + .bin if extension is .gltf).
    """
    builder = GltfBufferBuilder()

    nodes: List[Dict[str, any]] = []
    meshes: List[Dict[str, any]] = []
    materials_list: List[Dict[str, any]] = []
    textures_list: List[Dict[str, any]] = []
    images_list: List[Dict[str, any]] = []
    samplers_list: List[Dict[str, any]] = [{
        "magFilter": 9729, # LINEAR
        "minFilter": 9987, # LINEAR_MIPMAP_LINEAR
        "wrapS": 10497,    # REPEAT
        "wrapT": 10497     # REPEAT
    }]
    animations_list: List[Dict[str, any]] = []
    skins_list: List[Dict[str, any]] = []

    # Map image bytes to texture indices for deduplication
    image_to_tex_idx: Dict[bytes, int] = {}

    def get_or_add_texture(png_bytes: bytes) -> int:
        if png_bytes in image_to_tex_idx:
            return image_to_tex_idx[png_bytes]

        buf_view = builder.add_buffer_view(png_bytes)
        img_idx = len(images_list)
        images_list.append({
            "bufferView": buf_view,
            "mimeType": "image/png"
        })

        tex_idx = len(textures_list)
        textures_list.append({
            "source": img_idx,
            "sampler": 0
        })
        image_to_tex_idx[png_bytes] = tex_idx
        return tex_idx

    # 1. Process Bones & Armature Node Hierarchy
    has_skeleton = len(model.bones) > 0
    bone_node_indices: List[int] = []
    root_bone_node_indices: List[int] = []

    # Create root scene armature node
    armature_node_idx = len(nodes)
    nodes.append({
        "name": "Armature",
        "children": []
    })

    if has_skeleton:
        # Build node for each bone (offset by 1 because node 0 is Armature)
        for b_idx, bone in enumerate(model.bones):
            node_idx = len(nodes)
            bone_node_indices.append(node_idx)

            # Decompose local matrix in glTF space
            trans, rot, scale = _decompose_matrix(bone.loc_matrix)
            # Transform translation to glTF coordinate system: (x, z, -y)
            gl_trans = (trans[0], trans[2], -trans[1])
            # Transform rotation quaternion to glTF coordinate system: (qx, qz, -qy, qw)
            gl_rot = quat_normalize((rot[0], rot[2], -rot[1], rot[3]))
            # Scale
            gl_scale = (scale[0], scale[2], scale[1])

            node_entry: Dict[str, any] = {
                "name": bone.name if bone.name else f"Bone_{b_idx}",
                "translation": [_safe_float(v) for v in gl_trans],
                "rotation": [_safe_float(v) for v in gl_rot],
                "scale": [_safe_float(v, 1.0) for v in gl_scale],
                "children": []
            }
            nodes.append(node_entry)

        # Wire parent-child node relationships
        for b_idx, bone in enumerate(model.bones):
            curr_node_idx = bone_node_indices[b_idx]
            if 0 <= bone.parent_index < len(model.bones):
                parent_node_idx = bone_node_indices[bone.parent_index]
                nodes[parent_node_idx]["children"].append(curr_node_idx)
            else:
                root_bone_node_indices.append(curr_node_idx)
                nodes[armature_node_idx]["children"].append(curr_node_idx)

        # Build Inverse Bind Matrices Buffer
        ibm_stream = io.BytesIO()
        for bone in model.bones:
            gl_inv_mat = _convert_matrix_hon_to_gltf(bone.inv_matrix)
            ibm_stream.write(struct.pack("<16f", *gl_inv_mat))

        ibm_view = builder.add_buffer_view(ibm_stream.getvalue())
        ibm_accessor = builder.add_accessor(
            buffer_view_idx=ibm_view,
            component_type=5126, # FLOAT
            count=len(model.bones),
            accessor_type="MAT4"
        )

        skin_idx = len(skins_list)
        skins_list.append({
            "name": "HoNSkin",
            "inverseBindMatrices": ibm_accessor,
            "joints": bone_node_indices
        })

    # 2. Process Meshes & Primitives
    primitives: List[Dict[str, any]] = []

    for m_idx, mesh in enumerate(model.meshes):
        if mesh.num_vertices == 0 and not mesh.vertices:
            continue

        num_verts = len(mesh.vertices) if mesh.vertices else mesh.num_vertices
        if num_verts == 0:
            continue

        # Material resolution
        mat_entry: Dict[str, any] = {
            "name": mesh.default_material if mesh.default_material else f"Material_{m_idx}",
            "doubleSided": True,
            "pbrMetallicRoughness": {
                "baseColorFactor": [1.0, 1.0, 1.0, 1.0],
                "metallicFactor": 0.05,
                "roughnessFactor": 0.85
            }
        }

        # Check if material data is supplied
        mesh_mat = None
        if materials and m_idx < len(materials):
            mesh_mat = materials[m_idx]
        elif materials and len(materials) > 0:
            mesh_mat = materials[0]

        if mesh_mat:
            mat_entry["pbrMetallicRoughness"]["metallicFactor"] = mesh_mat.metallic
            mat_entry["pbrMetallicRoughness"]["roughnessFactor"] = mesh_mat.roughness

            if mesh_mat.diffuse_png_bytes:
                tex_idx = get_or_add_texture(mesh_mat.diffuse_png_bytes)
                mat_entry["pbrMetallicRoughness"]["baseColorTexture"] = {"index": tex_idx}

            if mesh_mat.normal_png_bytes:
                norm_tex_idx = get_or_add_texture(mesh_mat.normal_png_bytes)
                mat_entry["normalTexture"] = {"index": norm_tex_idx, "scale": 1.0}

            if mesh_mat.metallic_roughness_png_bytes:
                mr_tex_idx = get_or_add_texture(mesh_mat.metallic_roughness_png_bytes)
                mat_entry["pbrMetallicRoughness"]["metallicRoughnessTexture"] = {"index": mr_tex_idx}

            if mesh_mat.glow_png_bytes:
                glow_tex_idx = get_or_add_texture(mesh_mat.glow_png_bytes)
                mat_entry["emissiveTexture"] = {"index": glow_tex_idx}
                mat_entry["emissiveFactor"] = [1.0, 1.0, 1.0]

            if mesh_mat.is_additive:
                mat_entry["alphaMode"] = "BLEND"
                mat_entry["emissiveFactor"] = [1.0, 1.0, 1.0]
            elif mesh_mat.has_alpha:
                mat_entry["alphaMode"] = "MASK"
                mat_entry["alphaCutoff"] = 0.5
            else:
                mat_entry["alphaMode"] = "OPAQUE"

        mat_idx = len(materials_list)
        materials_list.append(mat_entry)

        primitive: Dict[str, any] = {
            "attributes": {},
            "material": mat_idx
        }

        # Positions (VEC3 FLOAT, coordinates transformed: x, z, -y)
        pos_stream = io.BytesIO()
        min_pos = [float("inf"), float("inf"), float("inf")]
        max_pos = [float("-inf"), float("-inf"), float("-inf")]

        for vx, vy, vz in mesh.vertices:
            gx, gy, gz = vx, vz, -vy
            pos_stream.write(struct.pack("<3f", gx, gy, gz))
            min_pos[0] = min(min_pos[0], gx)
            min_pos[1] = min(min_pos[1], gy)
            min_pos[2] = min(min_pos[2], gz)
            max_pos[0] = max(max_pos[0], gx)
            max_pos[1] = max(max_pos[1], gy)
            max_pos[2] = max(max_pos[2], gz)

        pos_view = builder.add_buffer_view(pos_stream.getvalue(), target=34962) # ARRAY_BUFFER
        pos_acc = builder.add_accessor(
            buffer_view_idx=pos_view,
            component_type=5126, # FLOAT
            count=num_verts,
            accessor_type="VEC3",
            min_vals=min_pos,
            max_vals=max_pos
        )
        primitive["attributes"]["POSITION"] = pos_acc

        # Normals (VEC3 FLOAT, transformed: nx, nz, -ny)
        if mesh.normals and len(mesh.normals) >= num_verts:
            norm_stream = io.BytesIO()
            for nx, ny, nz in mesh.normals:
                norm_stream.write(struct.pack("<3f", nx, nz, -ny))
            norm_view = builder.add_buffer_view(norm_stream.getvalue(), target=34962)
            norm_acc = builder.add_accessor(
                buffer_view_idx=norm_view,
                component_type=5126,
                count=num_verts,
                accessor_type="VEC3"
            )
            primitive["attributes"]["NORMAL"] = norm_acc

        # TexCoords (VEC2 FLOAT: u, v)
        if mesh.texcoords and len(mesh.texcoords) >= num_verts:
            uv_stream = io.BytesIO()
            for u, v in mesh.texcoords:
                uv_stream.write(struct.pack("<2f", u, v))
            uv_view = builder.add_buffer_view(uv_stream.getvalue(), target=34962)
            uv_acc = builder.add_accessor(
                buffer_view_idx=uv_view,
                component_type=5126,
                count=num_verts,
                accessor_type="VEC2"
            )
            primitive["attributes"]["TEXCOORD_0"] = uv_acc

        # Tangents (VEC4 FLOAT: tx, tz, -ty, handedness from sign chunk: 0 -> +1.0, 255 -> -1.0)
        if mesh.tangents and len(mesh.tangents) >= num_verts:
            tang_stream = io.BytesIO()
            has_signs = bool(getattr(mesh, "signs", None)) and len(mesh.signs) >= num_verts
            for i, (tx, ty, tz) in enumerate(mesh.tangents):
                w = (1.0 if mesh.signs[i] == 0 else -1.0) if has_signs else 1.0
                tang_stream.write(struct.pack("<4f", tx, tz, -ty, w))
            tang_view = builder.add_buffer_view(tang_stream.getvalue(), target=34962)
            tang_acc = builder.add_accessor(
                buffer_view_idx=tang_view,
                component_type=5126,
                count=num_verts,
                accessor_type="VEC4"
            )
            primitive["attributes"]["TANGENT"] = tang_acc

        # Vertex Colors (VEC4 FLOAT: r, g, b, a)
        if mesh.colors and len(mesh.colors) >= num_verts:
            col_stream = io.BytesIO()
            for r, g, b, a in mesh.colors:
                col_stream.write(struct.pack("<4f", r, g, b, a))
            col_view = builder.add_buffer_view(col_stream.getvalue(), target=34962)
            col_acc = builder.add_accessor(
                buffer_view_idx=col_view,
                component_type=5126,
                count=num_verts,
                accessor_type="VEC4"
            )
            primitive["attributes"]["COLOR_0"] = col_acc

        # Skinning: JOINTS_0 & WEIGHTS_0
        if has_skeleton and mesh.bone_links and len(mesh.bone_links) >= num_verts:
            joints_stream = io.BytesIO()
            weights_stream = io.BytesIO()

            for link in mesh.bone_links:
                # Normalize weights to sum to 1.0
                raw_weights = [max(0.0, link.weight0), max(0.0, link.weight1), max(0.0, link.weight2), max(0.0, link.weight3)]
                total_w = sum(raw_weights)
                if total_w > 1e-6:
                    norm_weights = [w / total_w for w in raw_weights]
                else:
                    norm_weights = [1.0, 0.0, 0.0, 0.0]

                raw_joints = [
                    max(0, link.bone_index0) if link.bone_index0 >= 0 else 0,
                    max(0, link.bone_index1) if link.bone_index1 >= 0 else 0,
                    max(0, link.bone_index2) if link.bone_index2 >= 0 else 0,
                    max(0, link.bone_index3) if link.bone_index3 >= 0 else 0
                ]

                joints_stream.write(struct.pack("<4H", *raw_joints))
                weights_stream.write(struct.pack("<4f", *norm_weights))

            joints_view = builder.add_buffer_view(joints_stream.getvalue(), target=34962)
            joints_acc = builder.add_accessor(
                buffer_view_idx=joints_view,
                component_type=5123, # UNSIGNED_SHORT
                count=num_verts,
                accessor_type="VEC4"
            )
            primitive["attributes"]["JOINTS_0"] = joints_acc

            weights_view = builder.add_buffer_view(weights_stream.getvalue(), target=34962)
            weights_acc = builder.add_accessor(
                buffer_view_idx=weights_view,
                component_type=5126, # FLOAT
                count=num_verts,
                accessor_type="VEC4"
            )
            primitive["attributes"]["WEIGHTS_0"] = weights_acc

        # Indices (Triangle faces, winding flipped: i0, i2, i1)
        if mesh.indices:
            idx_stream = io.BytesIO()
            is_short = num_verts <= 65535

            for i in range(0, len(mesh.indices), 3):
                i0 = mesh.indices[i]
                i1 = mesh.indices[i + 2] if i + 2 < len(mesh.indices) else 0
                i2 = mesh.indices[i + 1] if i + 1 < len(mesh.indices) else 0

                if is_short:
                    idx_stream.write(struct.pack("<3H", i0, i1, i2))
                else:
                    idx_stream.write(struct.pack("<3I", i0, i1, i2))

            idx_view = builder.add_buffer_view(idx_stream.getvalue(), target=34963) # ELEMENT_ARRAY_BUFFER
            idx_acc = builder.add_accessor(
                buffer_view_idx=idx_view,
                component_type=5123 if is_short else 5125, # UNSIGNED_SHORT or UNSIGNED_INT
                count=len(mesh.indices),
                accessor_type="SCALAR"
            )
            primitive["indices"] = idx_acc

        primitives.append(primitive)

    # Mesh Node
    mesh_idx = len(meshes)
    meshes.append({
        "name": "HoNModelMesh",
        "primitives": primitives
    })

    mesh_node_idx = len(nodes)
    mesh_node_entry: Dict[str, any] = {
        "name": "HoNModelMeshNode",
        "mesh": mesh_idx
    }
    if has_skeleton:
        mesh_node_entry["skin"] = 0
    nodes.append(mesh_node_entry)

    # 3. Process Skeletal Animations
    resolved_clips: List[HonClipData] = []
    if clips is not None:
        if isinstance(clips, dict):
            for cname, cdata in clips.items():
                if not cdata.name:
                    cdata.name = cname
                resolved_clips.append(cdata)
        elif isinstance(clips, list):
            resolved_clips.extend(clips)

    if bake_animations and has_skeleton and resolved_clips:
        # Pre-build bone name lookup
        bone_name_to_idx = {bone.name.lower(): b_idx for b_idx, bone in enumerate(model.bones)}

        for clip in resolved_clips:
            if clip.num_frames <= 0 or not clip.bone_motions:
                continue

            frame_duration = 1.0 / max(1, fps)
            num_frames = clip.num_frames

            # Time input accessor
            time_stream = io.BytesIO()
            for f in range(num_frames):
                time_stream.write(struct.pack("<f", f * frame_duration))
            time_view = builder.add_buffer_view(time_stream.getvalue())
            time_acc = builder.add_accessor(
                buffer_view_idx=time_view,
                component_type=5126,
                count=num_frames,
                accessor_type="SCALAR",
                min_vals=[0.0],
                max_vals=[(num_frames - 1) * frame_duration]
            )

            anim_channels: List[Dict[str, any]] = []
            anim_samplers: List[Dict[str, any]] = []

            for motion in clip.bone_motions:
                # Find matching skeleton bone
                target_b_idx = -1
                if motion.bone_name and motion.bone_name.lower() in bone_name_to_idx:
                    target_b_idx = bone_name_to_idx[motion.bone_name.lower()]
                elif 0 <= motion.bone_index < len(model.bones):
                    target_b_idx = motion.bone_index

                if target_b_idx < 0:
                    continue

                target_node_idx = bone_node_indices[target_b_idx]

                # 1. Translation Track
                if motion.translations:
                    trans_stream = io.BytesIO()
                    for f in range(num_frames):
                        tx, ty, tz = motion.translations[f] if f < len(motion.translations) else motion.translations[-1]
                        # Convert to glTF coords: (x, z, -y)
                        trans_stream.write(struct.pack("<3f", tx, tz, -ty))

                    trans_view = builder.add_buffer_view(trans_stream.getvalue())
                    trans_acc = builder.add_accessor(
                        buffer_view_idx=trans_view,
                        component_type=5126,
                        count=num_frames,
                        accessor_type="VEC3"
                    )

                    sampler_idx = len(anim_samplers)
                    anim_samplers.append({
                        "input": time_acc,
                        "output": trans_acc,
                        "interpolation": "LINEAR"
                    })
                    anim_channels.append({
                        "sampler": sampler_idx,
                        "target": {
                            "node": target_node_idx,
                            "path": "translation"
                        }
                    })

                # 2. Rotation Track (Quaternions: qx, qz, -qy, qw)
                if motion.rotations:
                    rot_stream = io.BytesIO()
                    for f in range(num_frames):
                        qx, qy, qz, qw = motion.rotations[f] if f < len(motion.rotations) else motion.rotations[-1]
                        gl_q = quat_normalize((qx, qz, -qy, qw))
                        rot_stream.write(struct.pack("<4f", *gl_q))

                    rot_view = builder.add_buffer_view(rot_stream.getvalue())
                    rot_acc = builder.add_accessor(
                        buffer_view_idx=rot_view,
                        component_type=5126,
                        count=num_frames,
                        accessor_type="VEC4"
                    )

                    sampler_idx = len(anim_samplers)
                    anim_samplers.append({
                        "input": time_acc,
                        "output": rot_acc,
                        "interpolation": "LINEAR"
                    })
                    anim_channels.append({
                        "sampler": sampler_idx,
                        "target": {
                            "node": target_node_idx,
                            "path": "rotation"
                        }
                    })

                # 3. Scale Track
                if motion.scales:
                    scale_stream = io.BytesIO()
                    for f in range(num_frames):
                        sx, sy, sz = motion.scales[f] if f < len(motion.scales) else motion.scales[-1]
                        scale_stream.write(struct.pack("<3f", sx, sz, sy))

                    scale_view = builder.add_buffer_view(scale_stream.getvalue())
                    scale_acc = builder.add_accessor(
                        buffer_view_idx=scale_view,
                        component_type=5126,
                        count=num_frames,
                        accessor_type="VEC3"
                    )

                    sampler_idx = len(anim_samplers)
                    anim_samplers.append({
                        "input": time_acc,
                        "output": scale_acc,
                        "interpolation": "LINEAR"
                    })
                    anim_channels.append({
                        "sampler": sampler_idx,
                        "target": {
                            "node": target_node_idx,
                            "path": "scale"
                        }
                    })

            if anim_channels:
                animations_list.append({
                    "name": clip.name if clip.name else "Animation",
                    "samplers": anim_samplers,
                    "channels": anim_channels
                })

    # Root Scene Nodes
    scene_nodes = [armature_node_idx, mesh_node_idx] if has_skeleton else [mesh_node_idx]

    bin_data = builder.get_binary_bytes()

    gltf_dict: Dict[str, any] = {
        "asset": {
            "version": "2.0",
            "generator": "ProjectKongor HoN 3D Pipeline Exporter"
        },
        "scene": 0,
        "scenes": [{"nodes": scene_nodes}],
        "nodes": nodes,
        "meshes": meshes,
        "materials": materials_list,
        "buffers": [{"byteLength": len(bin_data)}],
        "bufferViews": builder.buffer_views,
        "accessors": builder.accessors
    }

    if skins_list:
        gltf_dict["skins"] = skins_list
    if animations_list:
        gltf_dict["animations"] = animations_list
    if textures_list:
        gltf_dict["textures"] = textures_list
    if images_list:
        gltf_dict["images"] = images_list
    if samplers_list:
        gltf_dict["samplers"] = samplers_list

    json_str = json.dumps(gltf_dict, separators=(",", ":"))
    json_bytes = json_str.encode("utf-8")

    # If output path is explicitly .gltf, write separate .gltf and .bin
    if output_path and output_path.lower().endswith(".gltf"):
        bin_filename = os.path.splitext(os.path.basename(output_path))[0] + ".bin"
        gltf_dict["buffers"][0]["uri"] = bin_filename
        out_dir = os.path.dirname(os.path.abspath(output_path))
        os.makedirs(out_dir, exist_ok=True)

        with open(output_path, "w", encoding="utf-8") as f:
            json.dump(gltf_dict, f, indent=2)

        bin_path = os.path.join(out_dir, bin_filename)
        with open(bin_path, "wb") as f:
            f.write(bin_data)

        logger.info("Exported separate glTF: %s (JSON) and %s (BIN: %d bytes)", output_path, bin_path, len(bin_data))
        return json_bytes

    # Default: Pack into standard single binary GLB (.glb)
    json_pad = (4 - (len(json_bytes) % 4)) % 4
    json_chunk_len = len(json_bytes) + json_pad

    bin_pad = (4 - (len(bin_data) % 4)) % 4
    bin_chunk_len = len(bin_data) + bin_pad

    total_glb_len = 12 + 8 + json_chunk_len + 8 + bin_chunk_len

    glb_stream = io.BytesIO()
    # GLB Header
    glb_stream.write(struct.pack("<3I", 0x46546C67, 2, total_glb_len)) # Magic: "glTF", Version: 2

    # JSON Chunk Header & Payload
    glb_stream.write(struct.pack("<2I", json_chunk_len, 0x4E4F534A)) # Chunk Type: "JSON"
    glb_stream.write(json_bytes)
    glb_stream.write(b" " * json_pad)

    # BIN Chunk Header & Payload
    glb_stream.write(struct.pack("<2I", bin_chunk_len, 0x004E4942)) # Chunk Type: "BIN\0"
    glb_stream.write(bin_data)
    glb_stream.write(b"\x00" * bin_pad)

    glb_bytes = glb_stream.getvalue()
    logger.info("Exported GLB binary: %d bytes (%d nodes, %d meshes, %d skins, %d animations)",
                len(glb_bytes), len(nodes), len(meshes), len(skins_list), len(animations_list))

    if output_path:
        out_dir = os.path.dirname(os.path.abspath(output_path))
        if out_dir:
            os.makedirs(out_dir, exist_ok=True)
        with open(output_path, "wb") as f:
            f.write(glb_bytes)

    return glb_bytes

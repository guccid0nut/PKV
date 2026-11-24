# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth SMDL v3 Model Writer.
Chunk-by-chunk binary writer for HoN .model / .k2model files.
"""

from __future__ import annotations

import io
import logging
import struct
from typing import BinaryIO, Optional, Union

from .smdl_parser import HonModelData, HonMesh, HonBone, HonSurfaceData

logger = logging.getLogger("io_scene_hon.smdl_writer")


def _write_chunk(stream: BinaryIO, tag: str, data: bytes) -> None:
    """Write 4-byte ASCII tag, 4-byte int32 chunk size, followed by payload."""
    stream.write(tag.encode("ascii"))
    stream.write(struct.pack("<i", len(data)))
    stream.write(data)


def write_smdl(model: HonModelData, destination: Optional[Union[str, BinaryIO]] = None) -> bytes:
    """
    Serialize a HonModelData object to HoN SMDL v3 binary format.
    
    Args:
        model: The HonModelData instance to serialize.
        destination: Optional file path or stream. If None, returns bytes.
        
    Returns:
        The raw serialized bytes.
    """
    stream = io.BytesIO()

    # Magic header
    stream.write(b"SMDL")

    # 1. head Chunk
    num_meshes = len(model.meshes)
    num_bones = len(model.bones)
    num_surfs = len(model.surfaces) if model.surfaces else model.num_surfs
    min_x, min_y, min_z = model.bounding_min
    max_x, max_y, max_z = model.bounding_max

    if min_x == 0.0 and min_y == 0.0 and min_z == 0.0 and max_x == 0.0 and max_y == 0.0 and max_z == 0.0:
        all_verts = [v for m in model.meshes for v in m.vertices]
        if all_verts:
            min_x = min(v[0] for v in all_verts)
            min_y = min(v[1] for v in all_verts)
            min_z = min(v[2] for v in all_verts)
            max_x = max(v[0] for v in all_verts)
            max_y = max(v[1] for v in all_verts)
            max_z = max(v[2] for v in all_verts)

    head_data = struct.pack(
        "<5i6f",
        model.version,
        num_meshes,
        model.num_sprites,
        num_surfs,
        num_bones,
        min_x, min_y, min_z,
        max_x, max_y, max_z
    )
    _write_chunk(stream, "head", head_data)

    # 2. bone Chunk
    if num_bones > 0:
        bone_stream = io.BytesIO()
        for bone in model.bones:
            bone_stream.write(struct.pack("<i", bone.parent_index))

            # inv_matrix (12 floats)
            if len(bone.inv_matrix_raw) == 12:
                inv_floats = bone.inv_matrix_raw
            else:
                # Extract 12 floats from 4x4 matrix (rows 0, 1, 2, 3 without 4th column constants)
                m = bone.inv_matrix
                inv_floats = (
                    m[0], m[1], m[2],
                    m[4], m[5], m[6],
                    m[8], m[9], m[10],
                    m[12], m[13], m[14]
                )
            bone_stream.write(struct.pack("<12f", *inv_floats))

            # loc_matrix (12 floats)
            if len(bone.loc_matrix_raw) == 12:
                loc_floats = bone.loc_matrix_raw
            else:
                m = bone.loc_matrix
                loc_floats = (
                    m[0], m[1], m[2],
                    m[4], m[5], m[6],
                    m[8], m[9], m[10],
                    m[12], m[13], m[14]
                )
            bone_stream.write(struct.pack("<12f", *loc_floats))

            name_bytes = bone.name.encode("utf-8")
            bone_stream.write(struct.pack("<B", len(name_bytes)))
            bone_stream.write(name_bytes)
            bone_stream.write(b"\x00")

        _write_chunk(stream, "bone", bone_stream.getvalue())

    # 3. Mesh Chunks
    for mesh_idx, mesh in enumerate(model.meshes):
        mesh_stream = io.BytesIO()
        bmin_x, bmin_y, bmin_z = mesh.bounding_min
        bmax_x, bmax_y, bmax_z = mesh.bounding_max
        if bmin_x == 0.0 and bmin_y == 0.0 and bmin_z == 0.0 and bmax_x == 0.0 and bmax_y == 0.0 and bmax_z == 0.0 and mesh.vertices:
            bmin_x = min(v[0] for v in mesh.vertices)
            bmin_y = min(v[1] for v in mesh.vertices)
            bmin_z = min(v[2] for v in mesh.vertices)
            bmax_x = max(v[0] for v in mesh.vertices)
            bmax_y = max(v[1] for v in mesh.vertices)
            bmax_z = max(v[2] for v in mesh.vertices)
        num_verts = len(mesh.vertices) if mesh.vertices else mesh.num_vertices

        mesh_header = struct.pack(
            "<3i6fi",
            mesh.index if mesh.index >= 0 else mesh_idx,
            mesh.mode,
            num_verts,
            bmin_x, bmin_y, bmin_z,
            bmax_x, bmax_y, bmax_z,
            mesh.bone_index
        )
        mesh_stream.write(mesh_header)

        name_bytes = mesh.name.encode("utf-8")
        mat_bytes = mesh.default_material.encode("utf-8")
        mesh_stream.write(struct.pack("<B", len(name_bytes)))
        mesh_stream.write(struct.pack("<B", len(mat_bytes)))
        mesh_stream.write(name_bytes)
        mesh_stream.write(b"\x00")
        mesh_stream.write(mat_bytes)
        mesh_stream.write(b"\x00")

        _write_chunk(stream, "mesh", mesh_stream.getvalue())

        # vrts Chunk
        if mesh.vertices:
            vrts_stream = io.BytesIO()
            vrts_stream.write(struct.pack("<i", mesh.index if mesh.index >= 0 else mesh_idx))
            for vx, vy, vz in mesh.vertices:
                vrts_stream.write(struct.pack("<3f", vx, vy, vz))
            _write_chunk(stream, "vrts", vrts_stream.getvalue())

        # nrml Chunk
        if mesh.normals:
            nrml_stream = io.BytesIO()
            nrml_stream.write(struct.pack("<i", mesh.index if mesh.index >= 0 else mesh_idx))
            for nx, ny, nz in mesh.normals:
                nrml_stream.write(struct.pack("<3f", nx, ny, nz))
            _write_chunk(stream, "nrml", nrml_stream.getvalue())

        # texc Chunks (multi-channel with channel 0 fallback)
        uv_ch_list = mesh.uv_channels if any(mesh.uv_channels) else ([mesh.texcoords] if mesh.texcoords else [])
        for ch_idx, uvs in enumerate(uv_ch_list):
            if not uvs:
                continue
            texc_stream = io.BytesIO()
            texc_stream.write(struct.pack("<i", mesh.index if mesh.index >= 0 else mesh_idx))
            texc_stream.write(struct.pack("<i", ch_idx))
            for u, v in uvs:
                texc_stream.write(struct.pack("<2f", u, v))
            _write_chunk(stream, "texc", texc_stream.getvalue())

        # tang Chunks (multi-channel with channel 0 fallback)
        tang_ch_list = mesh.tangent_channels if any(mesh.tangent_channels) else ([mesh.tangents] if mesh.tangents else [])
        for ch_idx, tangents in enumerate(tang_ch_list):
            if not tangents:
                continue
            tang_stream = io.BytesIO()
            tang_stream.write(struct.pack("<i", mesh.index if mesh.index >= 0 else mesh_idx))
            tang_stream.write(struct.pack("<i", ch_idx))
            for tx, ty, tz in tangents:
                tang_stream.write(struct.pack("<3f", tx, ty, tz))
            _write_chunk(stream, "tang", tang_stream.getvalue())

        # sign Chunks (bitangent handedness bytes: 0 = +1, 255 = -1)
        sign_ch_list = mesh.sign_channels if any(mesh.sign_channels) else ([mesh.signs] if mesh.signs else [])
        for ch_idx, signs in enumerate(sign_ch_list):
            if not signs:
                continue
            sign_stream = io.BytesIO()
            sign_stream.write(struct.pack("<i", mesh.index if mesh.index >= 0 else mesh_idx))
            sign_stream.write(struct.pack("<i", ch_idx))
            sign_stream.write(bytes(min(255, max(0, int(s))) for s in signs))
            _write_chunk(stream, "sign", sign_stream.getvalue())

        # face Chunk
        if mesh.indices:
            face_stream = io.BytesIO()
            face_stream.write(struct.pack("<i", mesh.index if mesh.index >= 0 else mesh_idx))
            num_faces = len(mesh.indices) // 3
            face_stream.write(struct.pack("<i", num_faces))

            is_short = num_verts <= 65535
            face_type = 2 if is_short else 4
            if not is_short:
                logger.warning(
                    "[HF-019] Mesh '%s' has %d vertices (> 65,535). "
                    "Writing 32-bit index buffer (face_type=4). Note: K2 engine D3D9 renderer strictly expects "
                    "16-bit indices (face_type=2) and may explode geometry into spiderweb vertices pointing to root!",
                    mesh.name, num_verts
                )
            face_stream.write(struct.pack("<B", face_type))

            if is_short:
                for idx in mesh.indices:
                    face_stream.write(struct.pack("<H", idx))
            else:
                for idx in mesh.indices:
                    face_stream.write(struct.pack("<i", idx))

            _write_chunk(stream, "face", face_stream.getvalue())

        # link / lnk3 Chunk
        if mesh.bone_links:
            link_stream = io.BytesIO()
            link_stream.write(struct.pack("<i", mesh.index if mesh.index >= 0 else mesh_idx))
            link_stream.write(struct.pack("<i", len(mesh.bone_links)))

            for link in mesh.bone_links:
                active_weights = []
                active_indices = []
                for w, b in zip(link.weights, link.bone_indices):
                    if w > 0.0 and b >= 0:
                        active_weights.append(w)
                        active_indices.append(b)

                if not active_weights:
                    # Fallback single unweighted influence
                    active_weights = [1.0]
                    active_indices = [0]

                link_stream.write(struct.pack("<i", len(active_weights)))
                for w in active_weights:
                    link_stream.write(struct.pack("<f", w))
                for b in active_indices:
                    link_stream.write(struct.pack("<i", b))

            _write_chunk(stream, "lnk3", link_stream.getvalue())

        # colr Chunks (multi-channel with channel 0 fallback)
        colr_ch_list = mesh.color_channels if any(mesh.color_channels) else ([mesh.colors] if mesh.colors else [])
        for ch_idx, colors in enumerate(colr_ch_list):
            if not colors:
                continue
            colr_stream = io.BytesIO()
            colr_stream.write(struct.pack("<i", mesh.index if mesh.index >= 0 else mesh_idx))
            colr_stream.write(struct.pack("<i", ch_idx))
            for r, g, b, a in colors:
                rb = min(255, max(0, int(r * 255)))
                gb = min(255, max(0, int(g * 255)))
                bb = min(255, max(0, int(b * 255)))
                ab = min(255, max(0, int(a * 255)))
                colr_stream.write(struct.pack("<4B", rb, gb, bb, ab))
            _write_chunk(stream, "colr", colr_stream.getvalue())

    # 4. surf Chunks (Collision Hulls)
    if model.surfaces:
        for surf_idx, surf in enumerate(model.surfaces):
            surf_stream = io.BytesIO()
            bmin_x, bmin_y, bmin_z = surf.bounding_min
            bmax_x, bmax_y, bmax_z = surf.bounding_max

            surf_stream.write(struct.pack("<i", surf.index if surf.index >= 0 else surf_idx))
            surf_stream.write(struct.pack("<i", len(surf.planes)))
            surf_stream.write(struct.pack("<i", len(surf.points)))
            surf_stream.write(struct.pack("<i", len(surf.edges)))
            surf_stream.write(struct.pack("<i", len(surf.tris)))

            surf_stream.write(struct.pack("<3f", bmin_x, bmin_y, bmin_z))
            surf_stream.write(struct.pack("<3f", bmax_x, bmax_y, bmax_z))
            surf_stream.write(struct.pack("<i", surf.flags))

            for p in surf.planes:
                surf_stream.write(struct.pack("<4f", *p))
            for pt in surf.points:
                surf_stream.write(struct.pack("<3f", *pt))
            for e in surf.edges:
                surf_stream.write(struct.pack("<6f", *e))
            for t in surf.tris:
                surf_stream.write(struct.pack("<3I", *t))

            _write_chunk(stream, "surf", surf_stream.getvalue())

    result_bytes = stream.getvalue()
    logger.info("Serialized SMDL model: %d bytes", len(result_bytes))

    if destination is not None:
        if isinstance(destination, str):
            with open(destination, "wb") as f:
                f.write(result_bytes)
        elif hasattr(destination, "write"):
            destination.write(result_bytes)

    return result_bytes

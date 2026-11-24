# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth SMDL v3 Model Parser.
Chunk-by-chunk binary parser for HoN .model / .k2model files.

Acknowledgments:
    Strict stream chunk bounds validation and K2 format compatibility insights
    informed by KRAT0Sz / why2k (https://github.com/KRAT0Sz/KONGOR-TOOL) and RedBear.
"""

from __future__ import annotations

import io
import logging
import struct
from dataclasses import dataclass, field
from typing import BinaryIO, List, Optional, Tuple, Union

logger = logging.getLogger("io_scene_hon.smdl_parser")


@dataclass
class HonVertexBoneLink:
    """Represents skin weighting and bone influences for a single vertex."""
    weights: List[float] = field(default_factory=lambda: [0.0, 0.0, 0.0, 0.0])
    bone_indices: List[int] = field(default_factory=lambda: [-1, -1, -1, -1])

    @property
    def weight0(self) -> float:
        return self.weights[0] if len(self.weights) > 0 else 0.0

    @property
    def weight1(self) -> float:
        return self.weights[1] if len(self.weights) > 1 else 0.0

    @property
    def weight2(self) -> float:
        return self.weights[2] if len(self.weights) > 2 else 0.0

    @property
    def weight3(self) -> float:
        return self.weights[3] if len(self.weights) > 3 else 0.0

    @property
    def bone_index0(self) -> int:
        return self.bone_indices[0] if len(self.bone_indices) > 0 else -1

    @property
    def bone_index1(self) -> int:
        return self.bone_indices[1] if len(self.bone_indices) > 1 else -1

    @property
    def bone_index2(self) -> int:
        return self.bone_indices[2] if len(self.bone_indices) > 2 else -1

    @property
    def bone_index3(self) -> int:
        return self.bone_indices[3] if len(self.bone_indices) > 3 else -1


@dataclass
class HonBone:
    """Represents a skeletal joint node in HoN SMDL format."""
    index: int
    parent_index: int
    name: str
    inv_matrix: Tuple[float, ...] = (
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
        0.0, 0.0, 0.0, 1.0
    )
    loc_matrix: Tuple[float, ...] = (
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
        0.0, 0.0, 0.0, 1.0
    )
    inv_matrix_raw: Tuple[float, ...] = field(default_factory=tuple)
    loc_matrix_raw: Tuple[float, ...] = field(default_factory=tuple)


@dataclass
class HonMesh:
    """Represents a single geometric mesh / sub-mesh in HoN SMDL format."""
    index: int
    name: str
    default_material: str
    num_vertices: int = 0
    num_faces: int = 0
    mode: int = 0
    bone_index: int = -1
    bounding_min: Tuple[float, float, float] = (0.0, 0.0, 0.0)
    bounding_max: Tuple[float, float, float] = (0.0, 0.0, 0.0)
    vertices: List[Tuple[float, float, float]] = field(default_factory=list)
    normals: List[Tuple[float, float, float]] = field(default_factory=list)
    texcoords: List[Tuple[float, float]] = field(default_factory=list)
    tangents: List[Tuple[float, float, float]] = field(default_factory=list)
    signs: List[int] = field(default_factory=list)
    colors: List[Tuple[float, float, float, float]] = field(default_factory=list)
    uv_channels: List[List[Tuple[float, float]]] = field(default_factory=list)
    tangent_channels: List[List[Tuple[float, float, float]]] = field(default_factory=list)
    sign_channels: List[List[int]] = field(default_factory=list)
    color_channels: List[List[Tuple[float, float, float, float]]] = field(default_factory=list)
    indices: List[int] = field(default_factory=list)
    bone_links: List[HonVertexBoneLink] = field(default_factory=list)


@dataclass
class HonSurfaceData:
    """Represents collision surface hulls (surf chunk) with planes, points, edges, and tris."""
    index: int = 0
    planes: List[Tuple[float, float, float, float]] = field(default_factory=list)
    points: List[Tuple[float, float, float]] = field(default_factory=list)
    edges: List[Tuple[float, float, float, float, float, float]] = field(default_factory=list)
    tris: List[Tuple[int, int, int]] = field(default_factory=list)
    flags: int = 0
    bounding_min: Tuple[float, float, float] = (0.0, 0.0, 0.0)
    bounding_max: Tuple[float, float, float] = (0.0, 0.0, 0.0)


@dataclass
class HonModelData:
    """Root model container containing skeletal hierarchy, geometry meshes, and collision surfaces."""
    version: int = 3
    num_meshes: int = 0
    num_sprites: int = 0
    num_surfs: int = 0
    num_bones: int = 0
    bounding_min: Tuple[float, float, float] = (0.0, 0.0, 0.0)
    bounding_max: Tuple[float, float, float] = (0.0, 0.0, 0.0)
    bones: List[HonBone] = field(default_factory=list)
    meshes: List[HonMesh] = field(default_factory=list)
    surfaces: List[HonSurfaceData] = field(default_factory=list)


def _read_exact(stream: BinaryIO, size: int, context: str) -> bytes:
    """Read exactly `size` bytes from stream or raise an actionable ValueError."""
    if size < 0:
        raise ValueError(f"Invalid negative read size ({size}) while parsing SMDL {context}")
    data = stream.read(size)
    if len(data) < size:
        raise ValueError(
            f"Truncated SMDL stream while parsing {context}: expected {size} bytes, got {len(data)}"
        )
    return data


def _read_null_terminated_string(stream: BinaryIO, length: int) -> str:
    """Read length bytes string followed by null terminator."""
    data = _read_exact(stream, length, "string payload")
    # Read null terminator byte
    _ = _read_exact(stream, 1, "string null terminator")
    return data.decode("utf-8", errors="replace")


def read_smdl(source: Union[str, bytes, BinaryIO]) -> HonModelData:
    """
    Parse a HoN SMDL v3 binary model file.
    
    Args:
        source: File path (str), raw byte buffer (bytes), or binary stream (BinaryIO).
        
    Returns:
        HonModelData containing all parsed bones, meshes, materials, and vertex attributes.
    """
    if isinstance(source, str):
        with open(source, "rb") as f:
            return _parse_smdl_stream(f)
    elif isinstance(source, bytes):
        return _parse_smdl_stream(io.BytesIO(source))
    elif hasattr(source, "read"):
        return _parse_smdl_stream(source)
    else:
        raise TypeError(f"Unsupported source type for SMDL parser: {type(source)}")


def _parse_smdl_stream(stream: BinaryIO) -> HonModelData:
    magic = stream.read(4)
    if magic != b"SMDL":
        raise ValueError(f"Invalid SMDL header: expected b'SMDL', got {magic!r}")

    model = HonModelData()
    current_mesh: Optional[HonMesh] = None

    # Track stream total length
    current_pos = stream.tell()
    stream.seek(0, io.SEEK_END)
    total_length = stream.tell()
    stream.seek(current_pos, io.SEEK_SET)

    while stream.tell() < total_length:
        remaining = total_length - stream.tell()
        if remaining < 8:
            raise ValueError(
                f"Truncated SMDL chunk header at offset {stream.tell()}: "
                f"expected 8 bytes, only {remaining} byte(s) remaining"
            )
        tag_bytes = _read_exact(stream, 4, "chunk tag")
        tag = tag_bytes.decode("ascii", errors="replace")
        chunk_size_bytes = _read_exact(stream, 4, f"chunk '{tag}' length")
        chunk_size = struct.unpack("<i", chunk_size_bytes)[0]
        chunk_start = stream.tell()
        if chunk_size < 0:
            raise ValueError(
                f"Corrupted SMDL chunk '{tag}' at offset {chunk_start - 8}: negative length {chunk_size}"
            )
        chunk_end = chunk_start + chunk_size
        if chunk_end > total_length:
            raise ValueError(
                f"Truncated SMDL chunk '{tag}' at offset {chunk_start}: "
                f"declared length {chunk_size} exceeds stream size {total_length} "
                f"(available {total_length - chunk_start} bytes)"
            )

        logger.debug("Parsing chunk: tag=%s, size=%d, start=%d, end=%d", tag, chunk_size, chunk_start, chunk_end)

        if tag == "head":
            _read_head_chunk(stream, model)
        elif tag == "bone":
            _read_bone_chunk(stream, model)
        elif tag == "mesh":
            current_mesh = _read_mesh_chunk(stream, model.version)
            model.meshes.append(current_mesh)
        elif tag == "surf":
            surf_data = _read_surf_chunk(stream)
            model.surfaces.append(surf_data)
        elif tag == "vrts" and current_mesh is not None:
            _read_vrts_chunk(stream, current_mesh)
        elif tag == "nrml" and current_mesh is not None:
            _read_nrml_chunk(stream, current_mesh)
        elif tag == "texc" and current_mesh is not None:
            _read_texc_chunk(stream, current_mesh)
        elif tag == "tang" and current_mesh is not None:
            _read_tang_chunk(stream, current_mesh)
        elif tag == "sign" and current_mesh is not None:
            _read_sign_chunk(stream, current_mesh, chunk_size)
        elif tag == "face" and current_mesh is not None:
            _read_face_chunk(stream, current_mesh, model.version)
        elif tag in ("lnk1", "lnk3", "link") and current_mesh is not None:
            _read_link_chunk(stream, current_mesh)
        elif tag == "lnk2" and current_mesh is not None:
            _read_link_single_chunk(stream, current_mesh, chunk_size)
        elif tag == "colr" and current_mesh is not None:
            _read_colr_chunk(stream, current_mesh)
        else:
            logger.debug("Skipping or unhandled chunk tag: %s", tag)

        if stream.tell() < chunk_end:
            stream.seek(chunk_end, io.SEEK_SET)

    logger.info("Parsed SMDL model: %d meshes, %d surfs, %d bones, version=%d",
                len(model.meshes), len(model.surfaces), len(model.bones), model.version)
    return model


def _read_head_chunk(stream: BinaryIO, model: HonModelData) -> None:
    data = stream.read(44)
    (
        version,
        num_meshes,
        num_sprites,
        num_surfs,
        num_bones,
        min_x, min_y, min_z,
        max_x, max_y, max_z
    ) = struct.unpack("<5i6f", data)

    model.version = version
    model.num_meshes = num_meshes
    model.num_sprites = num_sprites
    model.num_surfs = num_surfs
    model.num_bones = num_bones
    model.bounding_min = (min_x, min_y, min_z)
    model.bounding_max = (max_x, max_y, max_z)


def _read_bone_chunk(stream: BinaryIO, model: HonModelData) -> None:
    for i in range(model.num_bones):
        parent_idx_bytes = stream.read(4)
        parent_index = struct.unpack("<i", parent_idx_bytes)[0]

        if model.version == 1:
            # SMDL v1: fixed-size name string then 4x4 matrices
            pos = stream.tell()
            b = stream.read(1)
            raw_name = bytearray()
            while b != b"\x00" and b != b"":
                raw_name.extend(b)
                b = stream.read(1)
            bone_name = raw_name.decode("utf-8", errors="replace")
            stream.seek(pos + 32, io.SEEK_SET) # 32-byte field
            inv_mat_16 = struct.unpack("<16f", stream.read(64))
            loc_mat_16 = struct.unpack("<16f", stream.read(64))
            inv_mat_floats = (
                inv_mat_16[0], inv_mat_16[1], inv_mat_16[2],
                inv_mat_16[4], inv_mat_16[5], inv_mat_16[6],
                inv_mat_16[8], inv_mat_16[9], inv_mat_16[10],
                inv_mat_16[12], inv_mat_16[13], inv_mat_16[14]
            )
            loc_mat_floats = (
                loc_mat_16[0], loc_mat_16[1], loc_mat_16[2],
                loc_mat_16[4], loc_mat_16[5], loc_mat_16[6],
                loc_mat_16[8], loc_mat_16[9], loc_mat_16[10],
                loc_mat_16[12], loc_mat_16[13], loc_mat_16[14]
            )
            inv_mat_4x4 = inv_mat_16
            loc_mat_4x4 = loc_mat_16
        else:
            inv_mat_floats = struct.unpack("<12f", stream.read(48))
            loc_mat_floats = struct.unpack("<12f", stream.read(48))

            name_len = struct.unpack("<B", stream.read(1))[0]
            bone_name = _read_null_terminated_string(stream, name_len)

            # Build full 4x4 matrix from 12 floats (3x4 row-major layout)
            inv_mat_4x4 = (
                inv_mat_floats[0], inv_mat_floats[1], inv_mat_floats[2], 0.0,
                inv_mat_floats[3], inv_mat_floats[4], inv_mat_floats[5], 0.0,
                inv_mat_floats[6], inv_mat_floats[7], inv_mat_floats[8], 0.0,
                inv_mat_floats[9], inv_mat_floats[10], inv_mat_floats[11], 1.0
            )
            loc_mat_4x4 = (
                loc_mat_floats[0], loc_mat_floats[1], loc_mat_floats[2], 0.0,
                loc_mat_floats[3], loc_mat_floats[4], loc_mat_floats[5], 0.0,
                loc_mat_floats[6], loc_mat_floats[7], loc_mat_floats[8], 0.0,
                loc_mat_floats[9], loc_mat_floats[10], loc_mat_floats[11], 1.0
            )

        bone = HonBone(
            index=i,
            parent_index=parent_index,
            name=bone_name,
            inv_matrix=inv_mat_4x4,
            loc_matrix=loc_mat_4x4,
            inv_matrix_raw=inv_mat_floats,
            loc_matrix_raw=loc_mat_floats
        )
        model.bones.append(bone)


def _read_mesh_chunk(stream: BinaryIO, version: int = 3) -> HonMesh:
    if version == 1:
        pos = stream.tell()
        b = stream.read(1)
        raw_name = bytearray()
        while b != b"\x00" and b != b"":
            raw_name.extend(b)
            b = stream.read(1)
        mesh_name = raw_name.decode("utf-8", errors="replace")
        stream.seek(pos + 32, io.SEEK_SET)

        b = stream.read(1)
        raw_mat = bytearray()
        while b != b"\x00" and b != b"":
            raw_mat.extend(b)
            b = stream.read(1)
        mat_name = raw_mat.decode("utf-8", errors="replace")

        return HonMesh(
            index=0,
            name=mesh_name,
            default_material=mat_name,
            num_vertices=0,
            mode=1,
            bone_index=-1
        )

    data = stream.read(40)
    (
        mesh_idx,
        mode,
        num_verts,
        min_x, min_y, min_z,
        max_x, max_y, max_z,
        bone_idx
    ) = struct.unpack("<3i6fi", data)

    name_len = struct.unpack("<B", stream.read(1))[0]
    mat_len = struct.unpack("<B", stream.read(1))[0]

    mesh_name = _read_null_terminated_string(stream, name_len)
    mat_name = _read_null_terminated_string(stream, mat_len)

    return HonMesh(
        index=mesh_idx,
        name=mesh_name,
        default_material=mat_name,
        num_vertices=num_verts,
        mode=mode,
        bone_index=bone_idx,
        bounding_min=(min_x, min_y, min_z),
        bounding_max=(max_x, max_y, max_z)
    )


def _read_vrts_chunk(stream: BinaryIO, mesh: HonMesh) -> None:
    _mesh_idx = struct.unpack("<i", stream.read(4))[0]
    raw_verts = struct.unpack(f"<{mesh.num_vertices * 3}f", stream.read(mesh.num_vertices * 12))
    mesh.vertices = [
        (raw_verts[i * 3], raw_verts[i * 3 + 1], raw_verts[i * 3 + 2])
        for i in range(mesh.num_vertices)
    ]


def _read_nrml_chunk(stream: BinaryIO, mesh: HonMesh) -> None:
    _mesh_idx = struct.unpack("<i", stream.read(4))[0]
    raw_normals = struct.unpack(f"<{mesh.num_vertices * 3}f", stream.read(mesh.num_vertices * 12))
    mesh.normals = [
        (raw_normals[i * 3], raw_normals[i * 3 + 1], raw_normals[i * 3 + 2])
        for i in range(mesh.num_vertices)
    ]


def _read_texc_chunk(stream: BinaryIO, mesh: HonMesh) -> None:
    _mesh_idx = struct.unpack("<i", _read_exact(stream, 4, "texc mesh_idx"))[0]
    channel_idx = struct.unpack("<i", _read_exact(stream, 4, "texc channel_idx"))[0]
    raw_texcoords = struct.unpack(
        f"<{mesh.num_vertices * 2}f",
        _read_exact(stream, mesh.num_vertices * 8, f"texc channel {channel_idx}")
    )
    uvs = [
        (raw_texcoords[i * 2], raw_texcoords[i * 2 + 1])
        for i in range(mesh.num_vertices)
    ]
    if channel_idx >= 0:
        while len(mesh.uv_channels) <= channel_idx:
            mesh.uv_channels.append([])
        mesh.uv_channels[channel_idx] = uvs
    if channel_idx == 0 or len(mesh.texcoords) == 0:
        mesh.texcoords = uvs


def _read_tang_chunk(stream: BinaryIO, mesh: HonMesh) -> None:
    _mesh_idx = struct.unpack("<i", _read_exact(stream, 4, "tang mesh_idx"))[0]
    channel_idx = struct.unpack("<i", _read_exact(stream, 4, "tang channel_idx"))[0]
    raw_tangents = struct.unpack(
        f"<{mesh.num_vertices * 3}f",
        _read_exact(stream, mesh.num_vertices * 12, f"tang channel {channel_idx}")
    )
    tangents = [
        (raw_tangents[i * 3], raw_tangents[i * 3 + 1], raw_tangents[i * 3 + 2])
        for i in range(mesh.num_vertices)
    ]
    if channel_idx >= 0:
        while len(mesh.tangent_channels) <= channel_idx:
            mesh.tangent_channels.append([])
        mesh.tangent_channels[channel_idx] = tangents
    if channel_idx == 0 or len(mesh.tangents) == 0:
        mesh.tangents = tangents


def _read_sign_chunk(stream: BinaryIO, mesh: HonMesh, chunk_size: int) -> None:
    """Read bitangent handedness sign bytes (0 = +1, 255 = -1) per vertex."""
    _mesh_idx = struct.unpack("<i", _read_exact(stream, 4, "sign mesh_idx"))[0]
    channel_idx = struct.unpack("<i", _read_exact(stream, 4, "sign channel_idx"))[0]
    payload_len = max(0, chunk_size - 8)
    raw_signs = _read_exact(stream, payload_len, f"sign channel {channel_idx}")
    signs = list(raw_signs)
    if channel_idx >= 0:
        while len(mesh.sign_channels) <= channel_idx:
            mesh.sign_channels.append([])
        mesh.sign_channels[channel_idx] = signs
    if channel_idx == 0 or len(mesh.signs) == 0:
        mesh.signs = signs
    logger.debug("Parsed sign chunk: mesh=%s channel=%d count=%d", mesh.name, channel_idx, len(signs))


def _read_face_chunk(stream: BinaryIO, mesh: HonMesh, version: int = 3) -> None:
    _mesh_idx = struct.unpack("<i", stream.read(4))[0]
    num_faces = struct.unpack("<i", stream.read(4))[0]
    mesh.num_faces = num_faces

    if version == 1:
        face_type = 4
    else:
        face_type = struct.unpack("<B", stream.read(1))[0]

    total_indices = num_faces * 3
    if face_type == 1:
        raw_indices = struct.unpack(f"<{total_indices}B", stream.read(total_indices))
    elif face_type == 2 or (face_type == 0 and mesh.num_vertices <= 65535):
        raw_indices = struct.unpack(f"<{total_indices}H", stream.read(total_indices * 2))
    else:
        raw_indices = struct.unpack(f"<{total_indices}i", stream.read(total_indices * 4))

    mesh.indices = list(raw_indices)


def _read_link_chunk(stream: BinaryIO, mesh: HonMesh) -> None:
    _mesh_idx = struct.unpack("<i", _read_exact(stream, 4, "link mesh_idx"))[0]
    num_verts = struct.unpack("<i", _read_exact(stream, 4, "link num_verts"))[0]
    bone_links: List[HonVertexBoneLink] = []
    max_influences = 0

    for v_idx in range(num_verts):
        num_weights = struct.unpack("<i", _read_exact(stream, 4, f"link vert {v_idx} num_weights"))[0]
        if num_weights < 0:
            raise ValueError(f"Negative weight count ({num_weights}) in mesh '{mesh.name}' vertex {v_idx}")
        if num_weights > max_influences:
            max_influences = num_weights
        weights = list(struct.unpack(f"<{num_weights}f", _read_exact(stream, num_weights * 4, f"link vert {v_idx} weights")))
        bone_indices = list(struct.unpack(f"<{num_weights}i", _read_exact(stream, num_weights * 4, f"link vert {v_idx} bone_indices")))

        # Preserve all (>4) influences while padding to at least 4 slots for legacy callers
        if num_weights < 4:
            weights.extend([0.0] * (4 - num_weights))
            bone_indices.extend([-1] * (4 - num_weights))

        bone_links.append(HonVertexBoneLink(weights=weights, bone_indices=bone_indices))

    if max_influences > 4:
        logger.debug(
            "Mesh '%s' contains vertices with up to %d bone influences (preserved in full IR)",
            mesh.name, max_influences
        )
    mesh.bone_links = bone_links


def _read_link_single_chunk(stream: BinaryIO, mesh: HonMesh, chunk_size: int = 0) -> None:
    """Read single-link (lnk2) where each vertex has exactly one bone index (weight=1.0)."""
    _mesh_idx = struct.unpack("<i", _read_exact(stream, 4, "lnk2 mesh_idx"))[0]
    num_verts = struct.unpack("<i", _read_exact(stream, 4, "lnk2 num_verts"))[0]
    payload_bytes = max(0, chunk_size - 8) if chunk_size >= 8 else num_verts * 4
    bone_links: List[HonVertexBoneLink] = []

    if num_verts > 0 and payload_bytes == num_verts * 4:
        raw_indices = struct.unpack(f"<{num_verts}I", _read_exact(stream, num_verts * 4, "lnk2 bone indices"))
        for bone_idx in raw_indices:
            bone_links.append(HonVertexBoneLink(
                weights=[1.0, 0.0, 0.0, 0.0],
                bone_indices=[int(bone_idx), -1, -1, -1]
            ))
    else:
        for v_idx in range(num_verts):
            _vert_bone = struct.unpack("<i", _read_exact(stream, 4, f"lnk2 vert {v_idx} prefix"))[0]
            bone_idx = struct.unpack("<i", _read_exact(stream, 4, f"lnk2 vert {v_idx} bone_idx"))[0]
            bone_links.append(HonVertexBoneLink(
                weights=[1.0, 0.0, 0.0, 0.0],
                bone_indices=[bone_idx, -1, -1, -1]
            ))

    mesh.bone_links = bone_links


def _read_surf_chunk(stream: BinaryIO) -> HonSurfaceData:
    """Read collision surface hull (surf) with planes, points, edges, and triangles."""
    surf_idx = struct.unpack("<i", stream.read(4))[0]
    num_planes = struct.unpack("<i", stream.read(4))[0]
    num_points = struct.unpack("<i", stream.read(4))[0]
    num_edges = struct.unpack("<i", stream.read(4))[0]
    num_tris = struct.unpack("<i", stream.read(4))[0]

    min_x, min_y, min_z = struct.unpack("<3f", stream.read(12))
    max_x, max_y, max_z = struct.unpack("<3f", stream.read(12))
    flags = struct.unpack("<i", stream.read(4))[0]

    planes = [
        struct.unpack("<4f", stream.read(16))
        for _ in range(num_planes)
    ]
    points = [
        struct.unpack("<3f", stream.read(12))
        for _ in range(num_points)
    ]
    edges = [
        struct.unpack("<6f", stream.read(24))
        for _ in range(num_edges)
    ]
    tris = [
        struct.unpack("<3I", stream.read(12))
        for _ in range(num_tris)
    ]

    return HonSurfaceData(
        index=surf_idx,
        planes=planes,
        points=points,
        edges=edges,
        tris=tris,
        flags=flags,
        bounding_min=(min_x, min_y, min_z),
        bounding_max=(max_x, max_y, max_z)
    )


def _read_colr_chunk(stream: BinaryIO, mesh: HonMesh) -> None:
    _mesh_idx = struct.unpack("<i", _read_exact(stream, 4, "colr mesh_idx"))[0]
    channel_idx = struct.unpack("<i", _read_exact(stream, 4, "colr channel_idx"))[0]
    raw_colrs = _read_exact(stream, mesh.num_vertices * 4, f"colr channel {channel_idx}")
    colors = [
        (raw_colrs[i * 4] / 255.0, raw_colrs[i * 4 + 1] / 255.0, raw_colrs[i * 4 + 2] / 255.0, raw_colrs[i * 4 + 3] / 255.0)
        for i in range(mesh.num_vertices)
    ]
    if channel_idx >= 0:
        while len(mesh.color_channels) <= channel_idx:
            mesh.color_channels.append([])
        mesh.color_channels[channel_idx] = colors
    if channel_idx == 0 or len(mesh.colors) == 0:
        mesh.colors = colors

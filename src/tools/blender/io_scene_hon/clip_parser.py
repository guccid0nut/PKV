# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth CLIP v2 Animation Parser.
Binary parser for HoN .clip animation keyframes supporting 10 motion channels per bone
and ZXY intrinsic Euler angle rotation order (R = R_z * R_x * R_y).
"""

from __future__ import annotations

import io
import logging
import math
import struct
from dataclasses import dataclass, field
from typing import BinaryIO, Dict, List, Optional, Tuple, Union

logger = logging.getLogger("io_scene_hon.clip_parser")


def quat_normalize(q: Tuple[float, float, float, float]) -> Tuple[float, float, float, float]:
    """Normalize a quaternion (x, y, z, w)."""
    x, y, z, w = q
    mag_sq = x * x + y * y + z * z + w * w
    if mag_sq <= 1e-12:
        return (0.0, 0.0, 0.0, 1.0)
    inv_mag = 1.0 / math.sqrt(mag_sq)
    return (x * inv_mag, y * inv_mag, z * inv_mag, w * inv_mag)


def quat_multiply(
    q1: Tuple[float, float, float, float],
    q2: Tuple[float, float, float, float]
) -> Tuple[float, float, float, float]:
    """Multiply two quaternions: q = q1 * q2."""
    x1, y1, z1, w1 = q1
    x2, y2, z2, w2 = q2
    return (
        w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2,
        w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2,
        w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2,
        w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2
    )


def euler_zxy_to_quaternion(
    pitch_deg: float,
    roll_deg: float,
    yaw_deg: float
) -> Tuple[float, float, float, float]:
    """
    Convert HoN intrinsic Euler rotation angles (in degrees) to normalized quaternion (x, y, z, w).
    Rotation order: R = R_z(yaw) * R_x(pitch) * R_y(roll).
    
    Channel mappings in HoN:
      - Channel 3: Pitch (around X axis)
      - Channel 4: Roll (around Y axis)
      - Channel 5: Yaw (around Z axis)
    """
    pitch_rad = pitch_deg * (math.pi / 180.0)
    roll_rad = roll_deg * (math.pi / 180.0)
    yaw_rad = yaw_deg * (math.pi / 180.0)

    # Half angles
    hx = pitch_rad * 0.5
    hy = roll_rad * 0.5
    hz = yaw_rad * 0.5

    sx, cx = math.sin(hx), math.cos(hx)
    sy, cy = math.sin(hy), math.cos(hy)
    sz, cz = math.sin(hz), math.cos(hz)

    # qZ = (0, 0, sz, cz)
    # qX = (sx, 0, 0, cx)
    # qY = (0, sy, 0, cy)
    # q = qZ * qX * qY
    qz = (0.0, 0.0, sz, cz)
    qx = (sx, 0.0, 0.0, cx)
    qy = (0.0, sy, 0.0, cy)

    q_zx = quat_multiply(qz, qx)
    q = quat_multiply(q_zx, qy)
    return quat_normalize(q)


def quat_slerp(
    q1: Tuple[float, float, float, float],
    q2: Tuple[float, float, float, float],
    t: float
) -> Tuple[float, float, float, float]:
    """
    Shortest-path Spherical Linear Interpolation (Slerp) between two quaternions.
    Falls back to normalized linear interpolation (NLerp) when angle is small.
    """
    x1, y1, z1, w1 = q1
    x2, y2, z2, w2 = q2

    dot = x1 * x2 + y1 * y2 + z1 * z2 + w1 * w2

    # Invert q2 if dot product is negative to take the shortest path on the 4D sphere
    if dot < 0.0:
        dot = -dot
        x2, y2, z2, w2 = -x2, -y2, -z2, -w2

    # Clamp dot product to valid acos range
    dot = min(1.0, max(-1.0, dot))

    # If quaternions are extremely close, use linear interpolation
    if dot > 0.9995:
        rx = x1 + t * (x2 - x1)
        ry = y1 + t * (y2 - y1)
        rz = z1 + t * (z2 - z1)
        rw = w1 + t * (w2 - w1)
        return quat_normalize((rx, ry, rz, rw))

    theta_0 = math.acos(dot)
    theta = theta_0 * t
    sin_theta = math.sin(theta)
    sin_theta_0 = math.sin(theta_0)

    s1 = math.cos(theta) - dot * sin_theta / sin_theta_0
    s2 = sin_theta / sin_theta_0

    rx = s1 * x1 + s2 * x2
    ry = s1 * y1 + s2 * y2
    rz = s1 * z1 + s2 * z2
    rw = s1 * w1 + s2 * w2
    return quat_normalize((rx, ry, rz, rw))


@dataclass
class HonBoneMotion:
    """Represents the complete animation motion tracks for a single bone."""
    bone_index: int
    bone_name: str
    anim_type: int = 0
    channels: Dict[int, List[float]] = field(default_factory=dict)
    translations: List[Tuple[float, float, float]] = field(default_factory=list)
    rotations: List[Tuple[float, float, float, float]] = field(default_factory=list)
    euler_rotations: List[Tuple[float, float, float]] = field(default_factory=list)
    scales: List[Tuple[float, float, float]] = field(default_factory=list)
    visibilities: List[float] = field(default_factory=list)


@dataclass
class HonClipData:
    """Represents a full HoN animation clip with multiple bone motions."""
    name: str = ""
    version: int = 2
    num_bones: int = 0
    num_frames: int = 0
    framerate: int = 30
    bone_motions: List[HonBoneMotion] = field(default_factory=list)


def _get_channel_value(channels: Dict[int, List[float]], channel_idx: int, frame: int, default: float) -> float:
    """Retrieve keyframe value for a channel, holding last value if frame > length."""
    if channel_idx in channels:
        keys = channels[channel_idx]
        if len(keys) == 0:
            return default
        if len(keys) == 1:
            return keys[0]
        if frame < len(keys):
            return keys[frame]
        return keys[-1]
    return default


def read_clip(source: Union[str, bytes, BinaryIO], clip_name: str = "") -> HonClipData:
    """
    Parse a HoN CLIP v2 binary animation file.
    
    Args:
        source: File path (str), raw byte buffer (bytes), or binary stream (BinaryIO).
        clip_name: Optional name for this animation clip.
        
    Returns:
        HonClipData containing decoded bone motion tracks, translation vectors,
        and ZXY rotation quaternions.
    """
    if isinstance(source, str):
        if not clip_name:
            import os
            clip_name = os.path.splitext(os.path.basename(source))[0]
        with open(source, "rb") as f:
            return _parse_clip_stream(f, clip_name)
    elif isinstance(source, bytes):
        return _parse_clip_stream(io.BytesIO(source), clip_name)
    elif hasattr(source, "read"):
        return _parse_clip_stream(source, clip_name)
    else:
        raise TypeError(f"Unsupported source type for CLIP parser: {type(source)}")


def _read_exact(stream: BinaryIO, size: int, context: str) -> bytes:
    """Read exactly `size` bytes from a CLIP stream or raise an actionable ValueError."""
    if size < 0:
        raise ValueError(f"Invalid negative read size ({size}) while parsing CLIP {context}")
    data = stream.read(size)
    if len(data) < size:
        raise ValueError(
            f"Truncated CLIP stream while parsing {context}: expected {size} bytes, got {len(data)}"
        )
    return data


def _parse_clip_stream(stream: BinaryIO, clip_name: str) -> HonClipData:
    magic = stream.read(4)
    if magic != b"CLIP":
        raise ValueError(f"Invalid CLIP header: expected b'CLIP', got {magic!r}")

    clip = HonClipData(name=clip_name)

    current_pos = stream.tell()
    stream.seek(0, io.SEEK_END)
    total_length = stream.tell()
    stream.seek(current_pos, io.SEEK_SET)

    bone_channel_map: Dict[int, Dict[str, any]] = {}

    while stream.tell() < total_length:
        remaining = total_length - stream.tell()
        if remaining < 8:
            raise ValueError(
                f"Truncated CLIP chunk header at offset {stream.tell()}: "
                f"expected 8 bytes, only {remaining} byte(s) remaining"
            )
        tag_bytes = _read_exact(stream, 4, "chunk tag")
        tag = tag_bytes.decode("ascii", errors="replace")
        chunk_size_bytes = _read_exact(stream, 4, f"chunk '{tag}' length")
        chunk_size = struct.unpack("<i", chunk_size_bytes)[0]
        chunk_start = stream.tell()
        if chunk_size < 0:
            raise ValueError(
                f"Corrupted CLIP chunk '{tag}' at offset {chunk_start - 8}: negative length {chunk_size}"
            )
        chunk_end = chunk_start + chunk_size
        if chunk_end > total_length:
            raise ValueError(
                f"Truncated CLIP chunk '{tag}' at offset {chunk_start}: "
                f"declared length {chunk_size} exceeds stream size {total_length} "
                f"(available {total_length - chunk_start} bytes)"
            )

        if tag == "head":
            version, num_bones, num_frames = struct.unpack("<3i", _read_exact(stream, 12, "head chunk"))
            clip.version = version
            clip.num_bones = num_bones
            clip.num_frames = num_frames
        elif tag == "bmtn":
            bone_idx, channel_type, key_count = struct.unpack("<3i", _read_exact(stream, 12, "bmtn header"))
            if key_count < 0:
                raise ValueError(f"Corrupted CLIP bmtn chunk: negative key_count {key_count}")
            name_len = struct.unpack("<B", _read_exact(stream, 1, "bmtn name_len"))[0]
            name_bytes = _read_exact(stream, name_len, "bmtn bone_name")
            _null = _read_exact(stream, 1, "bmtn bone_name null")
            bone_name = name_bytes.decode("utf-8", errors="replace")

            if channel_type == 6:
                # Visibility channel: bytes
                vis_bytes = _read_exact(stream, key_count, f"bmtn '{bone_name}' visibility keys")
                keys = [float(b) for b in vis_bytes]
            else:
                raw_floats = struct.unpack(
                    f"<{key_count}f",
                    _read_exact(stream, key_count * 4, f"bmtn '{bone_name}' channel {channel_type} keys")
                )
                keys = list(raw_floats)

            if bone_idx not in bone_channel_map:
                bone_channel_map[bone_idx] = {
                    "bone_name": bone_name,
                    "channels": {}
                }

            bone_channel_map[bone_idx]["channels"][channel_type] = keys

        if stream.tell() < chunk_end:
            stream.seek(chunk_end, io.SEEK_SET)

    # Reconstruct motion tracks for all frames
    for bone_idx in sorted(bone_channel_map.keys()):
        data = bone_channel_map[bone_idx]
        bname = data["bone_name"]
        channels = data["channels"]

        motion = HonBoneMotion(
            bone_index=bone_idx,
            bone_name=bname,
            channels=channels
        )

        num_frames = max(1, clip.num_frames)
        for f in range(num_frames):
            # Translations (Channels 0, 1, 2)
            pos_x = _get_channel_value(channels, 0, f, 0.0)
            pos_y = _get_channel_value(channels, 1, f, 0.0)
            pos_z = _get_channel_value(channels, 2, f, 0.0)
            motion.translations.append((pos_x, pos_y, pos_z))

            # Rotations (Channels 3=Pitch/X, 4=Roll/Y, 5=Yaw/Z)
            pitch = _get_channel_value(channels, 3, f, 0.0)
            roll = _get_channel_value(channels, 4, f, 0.0)
            yaw = _get_channel_value(channels, 5, f, 0.0)
            motion.euler_rotations.append((pitch, roll, yaw))

            q = euler_zxy_to_quaternion(pitch, roll, yaw)
            motion.rotations.append(q)

            # Visibilities (Channel 6)
            vis = _get_channel_value(channels, 6, f, 1.0)
            motion.visibilities.append(vis)

            # Scales (Channels 7, 8, 9)
            scale_x = _get_channel_value(channels, 7, f, 1.0)
            scale_y = _get_channel_value(channels, 8, f, 1.0)
            scale_z = _get_channel_value(channels, 9, f, 1.0)
            motion.scales.append((scale_x, scale_y, scale_z))

        clip.bone_motions.append(motion)

    logger.info("Parsed HoN CLIP '%s': %d frames, %d bone tracks", clip.name, clip.num_frames, len(clip.bone_motions))
    return clip

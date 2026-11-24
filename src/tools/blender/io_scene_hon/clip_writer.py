# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth CLIP v2 Animation Writer.
Chunk-by-chunk binary writer for HoN .clip animation keyframes.

Acknowledgments:
    Float epsilon constant-keyframe compression informed by KRAT0Sz / why2k
    (https://github.com/KRAT0Sz/KONGOR-TOOL) and K2 format insights from RedBear.
"""

from __future__ import annotations

import io
import logging
import struct
from typing import BinaryIO, Dict, List, Optional, Union

from .clip_parser import HonClipData, HonBoneMotion

logger = logging.getLogger("io_scene_hon.clip_writer")


def _write_chunk(stream: BinaryIO, tag: str, data: bytes) -> None:
    """Write 4-byte tag, 4-byte chunk length, followed by data."""
    stream.write(tag.encode("ascii"))
    stream.write(struct.pack("<i", len(data)))
    stream.write(data)


def write_clip(clip: HonClipData, destination: Optional[Union[str, BinaryIO]] = None) -> bytes:
    """
    Serialize a HonClipData object to HoN CLIP v2 binary format.
    
    Args:
        clip: The HonClipData instance to serialize.
        destination: Optional file path or stream. If None, returns bytes.
        
    Returns:
        The raw serialized bytes.
    """
    stream = io.BytesIO()

    # Magic header
    stream.write(b"CLIP")

    # 1. head Chunk
    num_bones = len(clip.bone_motions) if len(clip.bone_motions) > 0 else clip.num_bones
    num_frames = clip.num_frames
    if num_frames <= 0 and clip.bone_motions:
        for m in clip.bone_motions:
            if m.translations:
                num_frames = max(num_frames, len(m.translations))
            if m.rotations:
                num_frames = max(num_frames, len(m.rotations))
            for k in m.channels.values():
                num_frames = max(num_frames, len(k))

    head_data = struct.pack("<3i", clip.version, num_bones, num_frames)
    _write_chunk(stream, "head", head_data)

    # 2. bmtn Chunks (Bone Motion Channels)
    for motion in clip.bone_motions:
        bone_name_bytes = motion.bone_name.encode("utf-8")
        name_len = len(bone_name_bytes)

        # Reconstruct channels dictionary if empty but structured vectors exist,
        # or prioritize modified structured vectors (translations, euler_rotations) over stale raw channels.
        channels_to_write: Dict[int, List[float]] = {}
        if motion.channels:
            channels_to_write = dict(motion.channels)

            # Prioritize modified translations if they differ from or are missing in motion.channels
            if motion.translations:
                trans_x = [t[0] for t in motion.translations]
                trans_y = [t[1] for t in motion.translations]
                trans_z = [t[2] for t in motion.translations]

                def _ch_matches(ch_idx: int, target_vals: List[float]) -> bool:
                    if ch_idx not in motion.channels:
                        return False
                    raw = motion.channels[ch_idx]
                    if len(raw) == 1:
                        return all(abs(v - raw[0]) < 1e-6 for v in target_vals)
                    if len(raw) == len(target_vals):
                        return all(abs(v - k) < 1e-6 for v, k in zip(target_vals, raw))
                    return False

                if not (_ch_matches(0, trans_x) and _ch_matches(1, trans_y) and _ch_matches(2, trans_z)):
                    channels_to_write[0] = trans_x
                    channels_to_write[1] = trans_y
                    channels_to_write[2] = trans_z

            # Prioritize modified Euler rotations if they differ from or are missing in motion.channels
            if motion.euler_rotations:
                rot_x = [r[0] for r in motion.euler_rotations]
                rot_y = [r[1] for r in motion.euler_rotations]
                rot_z = [r[2] for r in motion.euler_rotations]

                def _ch_rot_matches(ch_idx: int, target_vals: List[float]) -> bool:
                    if ch_idx not in motion.channels:
                        return False
                    raw = motion.channels[ch_idx]
                    if len(raw) == 1:
                        return all(abs(v - raw[0]) < 1e-6 for v in target_vals)
                    if len(raw) == len(target_vals):
                        return all(abs(v - k) < 1e-6 for v, k in zip(target_vals, raw))
                    return False

                if not (_ch_rot_matches(3, rot_x) and _ch_rot_matches(4, rot_y) and _ch_rot_matches(5, rot_z)):
                    channels_to_write[3] = rot_x
                    channels_to_write[4] = rot_y
                    channels_to_write[5] = rot_z
        else:
            if motion.translations:
                channels_to_write[0] = [t[0] for t in motion.translations]
                channels_to_write[1] = [t[1] for t in motion.translations]
                channels_to_write[2] = [t[2] for t in motion.translations]

            if motion.euler_rotations:
                channels_to_write[3] = [r[0] for r in motion.euler_rotations]
                channels_to_write[4] = [r[1] for r in motion.euler_rotations]
                channels_to_write[5] = [r[2] for r in motion.euler_rotations]

            if motion.visibilities:
                channels_to_write[6] = [v for v in motion.visibilities]

            if motion.scales:
                channels_to_write[7] = [s[0] for s in motion.scales]
                channels_to_write[8] = [s[1] for s in motion.scales]
                channels_to_write[9] = [s[2] for s in motion.scales]

        # Write each channel as a separate bmtn chunk
        for channel_type in sorted(channels_to_write.keys()):
            keys = channels_to_write[channel_type]
            if len(keys) > 1:
                if channel_type == 6:
                    if all(int(k) == int(keys[0]) for k in keys):
                        keys = [keys[0]]
                else:
                    first_val = float(keys[0])
                    if all(abs(float(k) - first_val) < 1e-6 for k in keys):
                        keys = [keys[0]]
            key_count = len(keys)

            bmtn_stream = io.BytesIO()
            bmtn_stream.write(struct.pack("<3i", motion.bone_index, channel_type, key_count))
            bmtn_stream.write(struct.pack("<B", name_len))
            bmtn_stream.write(bone_name_bytes)
            bmtn_stream.write(b"\x00")

            if channel_type == 6:
                # Visibility channel written as raw bytes
                vis_bytes = bytes([min(255, max(0, int(k))) for k in keys])
                bmtn_stream.write(vis_bytes)
            else:
                for k in keys:
                    bmtn_stream.write(struct.pack("<f", float(k)))

            _write_chunk(stream, "bmtn", bmtn_stream.getvalue())

    result_bytes = stream.getvalue()
    logger.info("Serialized HoN CLIP '%s': %d bytes", clip.name, len(result_bytes))

    if destination is not None:
        if isinstance(destination, str):
            with open(destination, "wb") as f:
                f.write(result_bytes)
        elif hasattr(destination, "write"):
            destination.write(result_bytes)

    return result_bytes

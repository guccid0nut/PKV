#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth (K2 Engine) SMDL v3 Model and CLIP v2 Animation Decoder.
Decodes binary .model and .clip files into strongly-typed bone hierarchies
and frame-by-frame animation channels with sub-frame sampling and Slerp interpolation.
"""

import os
import sys
import struct
import logging
import argparse
from typing import Dict, List, Optional, Tuple, Any
import numpy as np

PROJECT_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
if PROJECT_ROOT not in sys.path:
    sys.path.insert(0, PROJECT_ROOT)

from tools.cinematic.quaternion_math import (
    euler_to_quat,
    normalize_quat,
    quat_slerp,
    lerp_vec,
)

logger = logging.getLogger("tools.cinematic.k2_clip_decoder")


class K2Bone:
    """Represents a single bone within a K2 SMDL skeletal hierarchy."""
    def __init__(self, bone_id: int, name: str, parent_id: int, inv_matrix: Tuple[float, ...], local_matrix: Tuple[float, ...]):
        self.bone_id = bone_id
        self.name = name
        self.parent_id = parent_id
        self.inv_matrix = inv_matrix
        self.local_matrix = local_matrix
        self.children: List['K2Bone'] = []

    def __repr__(self):
        return f"K2Bone(id={self.bone_id}, name='{self.name}', parent={self.parent_id})"


class K2Model:
    """Represents a decoded SMDL v3 model."""
    def __init__(self, filepath: str):
        self.filepath = os.path.normpath(filepath).replace("\\", "/")
        self.version = 0
        self.num_meshes = 0
        self.num_sprites = 0
        self.num_surfs = 0
        self.num_bones = 0
        self.bbox_min = (0.0, 0.0, 0.0)
        self.bbox_max = (0.0, 0.0, 0.0)
        self.bones: List[K2Bone] = []
        self.bone_by_name: Dict[str, K2Bone] = {}
        self.bone_by_id: Dict[int, K2Bone] = {}
        self.root_bones: List[K2Bone] = []
        self._parse()

    def _parse(self):
        if not os.path.exists(self.filepath):
            raise FileNotFoundError(f"SMDL model file not found: {self.filepath}")

        with open(self.filepath, 'rb') as f:
            magic = f.read(4)
            if magic != b'SMDL':
                raise ValueError(f"Invalid SMDL magic: {magic} (expected b'SMDL')")

            while True:
                chunk_header = f.read(8)
                if len(chunk_header) < 8:
                    break
                name = chunk_header[:4].decode('latin1', errors='ignore')
                size = struct.unpack('<I', chunk_header[4:])[0]
                data = f.read(size)

                if name == 'head':
                    v, nm, ns, nsurf, nb = struct.unpack('<IIIII', data[:20])
                    self.version = v
                    self.num_meshes = nm
                    self.num_sprites = ns
                    self.num_surfs = nsurf
                    self.num_bones = nb
                    if len(data) >= 44:
                        self.bbox_min = struct.unpack('<fff', data[20:32])
                        self.bbox_max = struct.unpack('<fff', data[32:44])
                elif name == 'bone':
                    offset = 0
                    for b_idx in range(self.num_bones):
                        parent_idx = struct.unpack('<i', data[offset:offset+4])[0]
                        offset += 4
                        inv_mat = struct.unpack('<12f', data[offset:offset+48])
                        offset += 48
                        local_mat = struct.unpack('<12f', data[offset:offset+48])
                        offset += 48
                        name_len = data[offset]
                        offset += 1
                        bone_name = data[offset:offset+name_len].decode('latin1', errors='ignore')
                        offset += name_len + 1  # null terminator

                        bone = K2Bone(b_idx, bone_name, parent_idx, inv_mat, local_mat)
                        self.bones.append(bone)
                        self.bone_by_name[bone_name] = bone
                        self.bone_by_id[b_idx] = bone

            # Build hierarchy
            for bone in self.bones:
                if bone.parent_id == -1 or bone.parent_id not in self.bone_by_id:
                    self.root_bones.append(bone)
                else:
                    parent = self.bone_by_id[bone.parent_id]
                    parent.children.append(bone)
        logger.debug(f"Parsed SMDL model '{self.filepath}' with {len(self.bones)} bones.")


class K2ClipChannel:
    """Represents motion keys for a single bone channel in a CLIP file."""
    def __init__(self, bone_idx: int, bone_name: str, m_type: int, num_keys: int, keys: List[Any]):
        self.bone_idx = bone_idx
        self.bone_name = bone_name
        self.m_type = m_type
        self.num_keys = num_keys
        self.keys = np.array(keys, dtype=np.float32) if m_type != 6 else np.array(keys, dtype=np.uint8)

    def sample(self, frame: float, loop: bool = True) -> float:
        """Sample the channel value at continuous frame index."""
        if self.num_keys == 0:
            return 0.0
        if self.num_keys == 1:
            return float(self.keys[0])

        n = self.num_keys
        if loop:
            f = frame % n
        else:
            f = max(0.0, min(float(n - 1), frame))

        f_idx = int(f)
        f_next = (f_idx + 1) % n if loop else min(n - 1, f_idx + 1)
        frac = f - f_idx

        v0 = float(self.keys[f_idx])
        v1 = float(self.keys[f_next])
        return (1.0 - frac) * v0 + frac * v1


class K2Clip:
    """Represents a decoded CLIP v2 animation clip."""
    def __init__(self, filepath: str):
        self.filepath = os.path.normpath(filepath).replace("\\", "/")
        self.clip_name = os.path.splitext(os.path.basename(filepath))[0]
        self.version = 0
        self.num_bones = 0
        self.num_frames = 0
        self.channels: Dict[str, Dict[int, K2ClipChannel]] = {}  # bone_name -> m_type -> K2ClipChannel
        self.bone_quats: Dict[str, np.ndarray] = {}  # bone_name -> array of [num_keys, 4] unit quaternions
        self._parse()
        self._precompute_quaternions()

    def _parse(self):
        if not os.path.exists(self.filepath):
            raise FileNotFoundError(f"CLIP file not found: {self.filepath}")

        with open(self.filepath, 'rb') as f:
            magic = f.read(4)
            if magic != b'CLIP':
                raise ValueError(f"Invalid CLIP magic: {magic} (expected b'CLIP')")

            while True:
                chunk_header = f.read(8)
                if len(chunk_header) < 8:
                    break
                name = chunk_header[:4].decode('latin1', errors='ignore')
                size = struct.unpack('<I', chunk_header[4:])[0]
                data = f.read(size)

                if name == 'head':
                    v, nb, nf = struct.unpack('<III', data[:12])
                    self.version = v
                    self.num_bones = nb
                    self.num_frames = nf
                elif name == 'bmtn':
                    bone_idx, m_type, num_keys = struct.unpack('<iii', data[:12])
                    name_len = data[12]
                    bone_name = data[13:13+name_len].decode('latin1', errors='ignore')
                    offset = 13 + name_len
                    key_type = data[offset]
                    keys_data = data[offset+1:]

                    if m_type == 6:
                        keys = list(struct.unpack(f'<{num_keys}B', keys_data[:num_keys]))
                    else:
                        if num_keys > 0:
                            keys = list(struct.unpack(f'<{num_keys}f', keys_data[:num_keys*4]))
                        else:
                            keys = []

                    ch = K2ClipChannel(bone_idx, bone_name, m_type, num_keys, keys)
                    if bone_name not in self.channels:
                        self.channels[bone_name] = {}
                    self.channels[bone_name][m_type] = ch
        logger.debug(f"Parsed CLIP '{self.filepath}' ({self.num_frames} frames, {self.num_bones} bones).")

    def _precompute_quaternions(self):
        """Precomputes and signs continuous unit quaternions for each bone across all keyframes."""
        for bname, b_chs in self.channels.items():
            if 3 in b_chs and 4 in b_chs and 5 in b_chs:
                num_k = max(b_chs[3].num_keys, b_chs[4].num_keys, b_chs[5].num_keys)
                if num_k == 0:
                    continue
                quats = np.zeros((num_k, 4), dtype=np.float64)
                for k in range(num_k):
                    rx = float(b_chs[3].keys[min(k, b_chs[3].num_keys - 1)]) if b_chs[3].num_keys > 0 else 0.0
                    ry = float(b_chs[4].keys[min(k, b_chs[4].num_keys - 1)]) if b_chs[4].num_keys > 0 else 0.0
                    rz = float(b_chs[5].keys[min(k, b_chs[5].num_keys - 1)]) if b_chs[5].num_keys > 0 else 0.0
                    q = euler_to_quat(rx, ry, rz, order='zxy')
                    if k > 0 and np.dot(quats[k - 1], q) < 0.0:
                        q = -q
                    quats[k] = q
                self.bone_quats[bname] = quats

    def sample_bone_raw(self, bone_name: str, frame: float, loop: bool = True) -> Tuple[np.ndarray, np.ndarray, np.ndarray]:
        """
        Samples raw location (trans X, Y, Z), rotation (pitch, roll, yaw in deg), and scale (X, Y, Z).
        """
        if bone_name not in self.channels:
            return np.zeros(3), np.zeros(3), np.ones(3)

        b_chs = self.channels[bone_name]

        # Translation (m_type 0, 1, 2)
        tx = b_chs[0].sample(frame, loop) if 0 in b_chs else 0.0
        ty = b_chs[1].sample(frame, loop) if 1 in b_chs else 0.0
        tz = b_chs[2].sample(frame, loop) if 2 in b_chs else 0.0
        loc = np.array([tx, ty, tz], dtype=np.float64)

        # Rotation (m_type 3, 4, 5) in degrees
        rx = b_chs[3].sample(frame, loop) if 3 in b_chs else 0.0
        ry = b_chs[4].sample(frame, loop) if 4 in b_chs else 0.0
        rz = b_chs[5].sample(frame, loop) if 5 in b_chs else 0.0
        rot_deg = np.array([rx, ry, rz], dtype=np.float64)

        # Scale (m_type 7, 8, 9)
        sx = b_chs[7].sample(frame, loop) if 7 in b_chs else 1.0
        sy = b_chs[8].sample(frame, loop) if 8 in b_chs else 1.0
        sz = b_chs[9].sample(frame, loop) if 9 in b_chs else 1.0
        scale = np.array([sx, sy, sz], dtype=np.float64)

        return loc, rot_deg, scale

    def sample_bone_quat(self, bone_name: str, frame: float, loop: bool = True) -> Tuple[np.ndarray, np.ndarray, np.ndarray]:
        """
        Samples location, quaternion rotation [w, x, y, z] via Slerp, and scale for a given bone.
        """
        loc, _, scale = self.sample_bone_raw(bone_name, frame, loop)

        if bone_name in self.bone_quats:
            quats = self.bone_quats[bone_name]
            num_k = len(quats)
            if num_k == 1:
                return loc, quats[0], scale

            is_action = self.clip_name in ['ability_1', 'ability_2', 'ability_3', 'ability_4', 'attack_1', 'attack_2', 'item_1', 'taunt_1']
            if loop and is_action and num_k > 3:
                start = 1
                end = num_k - 2
                span = end - start
                cycle = 2 * span
                tau = frame % cycle
                if tau > span:
                    f = float(end - (tau - span))
                else:
                    f = float(start + tau)
            else:
                f = (frame % num_k) if loop else max(0.0, min(float(num_k - 1), frame))

            f_idx = int(f)
            f_next = (f_idx + 1) % num_k if loop else min(num_k - 1, f_idx + 1)
            frac = f - f_idx

            q0 = quats[f_idx]
            q1 = quats[f_next]
            q_interp = quat_slerp(q0, q1, frac)
            return loc, q_interp, scale

        return loc, np.array([1.0, 0.0, 0.0, 0.0], dtype=np.float64), scale


class ClipLibrary:
    """Manages loaded K2 models and clips for Empath and Courier Parrot."""
    def __init__(self, project_root: str = PROJECT_ROOT):
        self.project_root = os.path.normpath(project_root).replace("\\", "/")
        self.empath_dir = os.path.join(self.project_root, "games", "shared", "heroes", "empath")
        self.empath_clips_dir = os.path.join(self.empath_dir, "clips")
        self.parrot_dir = os.path.join(self.project_root, "games", "shared", "heroes", "calamity", "alt8", "ability_02", "parrot")

        self.empath_model: Optional[K2Model] = None
        self.parrot_model: Optional[K2Model] = None
        self.empath_clips: Dict[str, K2Clip] = {}
        self.parrot_clips: Dict[str, K2Clip] = {}

        self._load_all()

    def _load_all(self):
        empath_model_path = os.path.join(self.empath_dir, "high.model")
        if os.path.exists(empath_model_path):
            self.empath_model = K2Model(empath_model_path)
            logger.info(f"Loaded Empath model ({len(self.empath_model.bones)} bones).")

        parrot_model_path = os.path.join(self.parrot_dir, "high.model")
        if os.path.exists(parrot_model_path):
            self.parrot_model = K2Model(parrot_model_path)
            logger.info(f"Loaded Parrot model ({len(self.parrot_model.bones)} bones).")

        if os.path.exists(self.empath_clips_dir):
            for fname in sorted(os.listdir(self.empath_clips_dir)):
                if fname.endswith(".clip"):
                    clip_name = os.path.splitext(fname)[0]
                    cpath = os.path.join(self.empath_clips_dir, fname)
                    self.empath_clips[clip_name] = K2Clip(cpath)
            logger.info(f"Loaded {len(self.empath_clips)} Empath animation clips.")

        parrot_clip_path = os.path.join(self.parrot_dir, "default_1.clip")
        if os.path.exists(parrot_clip_path):
            self.parrot_clips["default_1"] = K2Clip(parrot_clip_path)
            logger.info("Loaded Parrot default_1 animation clip.")

        # Align all Empath clip bone quaternions to default_1 reference hemisphere
        if "default_1" in self.empath_clips:
            ref_clip = self.empath_clips["default_1"]
            for cname, clip in self.empath_clips.items():
                if clip is ref_clip:
                    continue
                for bname, quats in clip.bone_quats.items():
                    if bname in ref_clip.bone_quats and len(quats) > 0 and len(ref_clip.bone_quats[bname]) > 0:
                        if np.dot(quats[0], ref_clip.bone_quats[bname][0]) < 0.0:
                            clip.bone_quats[bname] = -quats

    def get_empath_clip(self, clip_name: str) -> Optional[K2Clip]:
        """Retrieves an Empath clip by name, falling back to default_1 if not found."""
        if clip_name in self.empath_clips:
            return self.empath_clips[clip_name]
        return self.empath_clips.get("default_1")

    def get_parrot_clip(self, clip_name: str) -> Optional[K2Clip]:
        """Retrieves a Parrot clip by name, falling back to default_1."""
        if clip_name in self.parrot_clips:
            return self.parrot_clips[clip_name]
        return self.parrot_clips.get("default_1")


def setup_cli_logging(verbose: bool = False, log_file: Optional[str] = None) -> None:
    """Configures structured logging with optional file output."""
    level = logging.DEBUG if verbose else logging.INFO
    fmt = "%(asctime)s [%(levelname)s] [%(name)s] %(message)s"
    handlers = [logging.StreamHandler(sys.stdout)]
    if log_file:
        handlers.append(logging.FileHandler(log_file, encoding="utf-8"))
    logging.basicConfig(level=level, format=fmt, handlers=handlers, force=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="K2 SMDL Model and CLIP Animation Decoder")
    parser.add_argument("--model", type=str, default=None, help="Path to SMDL .model file to inspect")
    parser.add_argument("--clip", type=str, default=None, help="Path to CLIP .clip file to inspect")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable verbose debug logging")
    parser.add_argument("--log-file", type=str, default=None, help="Optional path to write structured log file")
    args = parser.parse_args()

    setup_cli_logging(args.verbose, args.log_file)
    logger.info("Initializing K2 Clip Decoder...")

    if args.model:
        model = K2Model(args.model)
        logger.info(f"Model: {model.filepath} | Bones: {len(model.bones)} | Meshes: {model.num_meshes}")
        for b in model.bones[:10]:
            logger.info(f"  {b}")

    if args.clip:
        clip = K2Clip(args.clip)
        logger.info(f"Clip: {clip.filepath} | Frames: {clip.num_frames} | Bones: {clip.num_bones}")
        for bname, chs in list(clip.channels.items())[:5]:
            logger.info(f"  Bone: {bname} -> Channel types: {list(chs.keys())}")

    if not args.model and not args.clip:
        logger.info("Loading default ClipLibrary...")
        lib = ClipLibrary(PROJECT_ROOT)
        logger.info(f"ClipLibrary initialized successfully: Empath clips = {len(lib.empath_clips)}, Parrot clips = {len(lib.parrot_clips)}")

    return 0


if __name__ == "__main__":
    sys.exit(main())

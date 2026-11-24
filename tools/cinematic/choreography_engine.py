#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Master Skeletal Animation & Room Movement Choreography Engine.
Decodes and retargets HoN skeletal clips, performs Quaternion Slerp multi-clip blending,
executes the 7-Act choreography across all 6,645 frames at 30 FPS, integrates
secondary cloth/hair dynamics (Box001-Box029), 52 ARKit facial blendshapes,
and Courier Parrot 3D Bézier flight with screaming shape keys.
"""

import os
import sys
import json
import math
import logging
import argparse
from typing import Dict, List, Tuple, Optional, Any
import numpy as np

PROJECT_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
if PROJECT_ROOT not in sys.path:
    sys.path.insert(0, PROJECT_ROOT)

from tools.cinematic.quaternion_math import (
    euler_to_quat,
    quat_to_euler,
    quat_slerp,
    quat_multiply,
    normalize_quat,
    cosine_smoothstep,
    hermite_smoothstep,
    lerp_vec,
)
from tools.cinematic.k2_clip_decoder import ClipLibrary, K2Clip, K2Model
from tools.cinematic.secondary_physics import SecondaryDynamicsEngine
from tools.cinematic.stage_trajectories import TrajectoryEngine

logger = logging.getLogger("tools.cinematic.choreography_engine")

# Canonical Bone Retargeting Mapping: Mixamo Pose Bone -> (HoN Bone Name, Default Offset Quat / Scale)
MIXAMO_TO_HON_BONE_MAP: Dict[str, str] = {
    "mixamorig:Hips": "Bip01 Pelvis",
    "mixamorig:Spine": "Bip01 Spine",
    "mixamorig:Spine1": "Bip01 Spine1",
    "mixamorig:Spine2": "Bip01 Spine2",
    "mixamorig:Neck": "Bip01 Neck",
    "mixamorig:Head": "Bip01 Head",
    "mixamorig:HeadTop_End": "Bip01 Head",

    # Left Arm Chain
    "mixamorig:LeftShoulder": "Bip01 L Clavicle",
    "mixamorig:LeftArm": "Bip01 L UpperArm",
    "mixamorig:LeftForeArm": "Bip01 L Forearm",
    "mixamorig:LeftHand": "Bip01 L Hand",
    "mixamorig:LeftHandThumb1": "Bip01 L Finger0",
    "mixamorig:LeftHandThumb2": "Bip01 L Finger0",
    "mixamorig:LeftHandThumb3": "Bip01 L Finger0",
    "mixamorig:LeftHandThumb4": "Bip01 L Finger0",
    "mixamorig:LeftHandIndex1": "Bip01 L Finger1",
    "mixamorig:LeftHandIndex2": "Bip01 L Finger1",
    "mixamorig:LeftHandIndex3": "Bip01 L Finger1",
    "mixamorig:LeftHandIndex4": "Bip01 L Finger1",
    "mixamorig:LeftHandMiddle1": "Bip01 L Finger1",
    "mixamorig:LeftHandMiddle2": "Bip01 L Finger1",
    "mixamorig:LeftHandMiddle3": "Bip01 L Finger1",
    "mixamorig:LeftHandMiddle4": "Bip01 L Finger1",
    "mixamorig:LeftHandRing1": "Bip01 L Finger1",
    "mixamorig:LeftHandRing2": "Bip01 L Finger1",
    "mixamorig:LeftHandRing3": "Bip01 L Finger1",
    "mixamorig:LeftHandRing4": "Bip01 L Finger1",
    "mixamorig:LeftHandPinky1": "Bip01 L Finger1",
    "mixamorig:LeftHandPinky2": "Bip01 L Finger1",
    "mixamorig:LeftHandPinky3": "Bip01 L Finger1",
    "mixamorig:LeftHandPinky4": "Bip01 L Finger1",

    # Right Arm Chain
    "mixamorig:RightShoulder": "Bip01 R Clavicle",
    "mixamorig:RightArm": "Bip01 R UpperArm",
    "mixamorig:RightForeArm": "Bip01 R Forearm",
    "mixamorig:RightHand": "Bip01 R Hand",
    "mixamorig:RightHandThumb1": "Bip01 R Finger0",
    "mixamorig:RightHandThumb2": "Bip01 R Finger0",
    "mixamorig:RightHandThumb3": "Bip01 R Finger0",
    "mixamorig:RightHandThumb4": "Bip01 R Finger0",
    "mixamorig:RightHandIndex1": "Bip01 R Finger1",
    "mixamorig:RightHandIndex2": "Bip01 R Finger1",
    "mixamorig:RightHandIndex3": "Bip01 R Finger1",
    "mixamorig:RightHandIndex4": "Bip01 R Finger1",
    "mixamorig:RightHandMiddle1": "Bip01 R Finger1",
    "mixamorig:RightHandMiddle2": "Bip01 R Finger1",
    "mixamorig:RightHandMiddle3": "Bip01 R Finger1",
    "mixamorig:RightHandMiddle4": "Bip01 R Finger1",
    "mixamorig:RightHandRing1": "Bip01 R Finger1",
    "mixamorig:RightHandRing2": "Bip01 R Finger1",
    "mixamorig:RightHandRing3": "Bip01 R Finger1",
    "mixamorig:RightHandRing4": "Bip01 R Finger1",
    "mixamorig:RightHandPinky1": "Bip01 R Finger1",
    "mixamorig:RightHandPinky2": "Bip01 R Finger1",
    "mixamorig:RightHandPinky3": "Bip01 R Finger1",
    "mixamorig:RightHandPinky4": "Bip01 R Finger1",

    # Left Leg Chain
    "mixamorig:LeftUpLeg": "Bip01 L Thigh",
    "mixamorig:LeftLeg": "Bip01 L Calf",
    "mixamorig:LeftFoot": "Bip01 L Foot",
    "mixamorig:LeftToeBase": "Bip01 L Toe0",
    "mixamorig:LeftToe_End": "Bip01 L Toe0",

    # Right Leg Chain
    "mixamorig:RightUpLeg": "Bip01 R Thigh",
    "mixamorig:RightLeg": "Bip01 R Calf",
    "mixamorig:RightFoot": "Bip01 R Foot",
    "mixamorig:RightToeBase": "Bip01 R Toe0",
    "mixamorig:RightToe_End": "Bip01 R Toe0",
}

# 12 Canonical Section Timeline Definitions
SECTION_TIMELINE: List[Dict[str, Any]] = [
    {"id": "S01", "name": "Atmospheric Intro", "start_frame": 0, "end_frame": 225, "clip_empath": "default_1", "clip_parrot": "bored_1"},
    {"id": "S02", "name": "Intro Vocal Chops", "start_frame": 225, "end_frame": 825, "clip_empath": "ability_1", "clip_parrot": "bored_1"},
    {"id": "S03", "name": "Drop 1 (Intro Drop)", "start_frame": 825, "end_frame": 1620, "clip_empath": "ability_4", "clip_parrot": "attack_1"},
    {"id": "S04", "name": "Verse 1", "start_frame": 1620, "end_frame": 2220, "clip_empath": "walk_1", "clip_parrot": "default_1"},
    {"id": "S05", "name": "Pre-Chorus 1", "start_frame": 2220, "end_frame": 2820, "clip_empath": "ability_2", "clip_parrot": "attack_2"},
    {"id": "S06", "name": "Chorus 1 (Drop 2)", "start_frame": 2820, "end_frame": 3645, "clip_empath": "ability_4", "clip_parrot": "attack_1"},
    {"id": "S07", "name": "Soft Interlude", "start_frame": 3645, "end_frame": 4380, "clip_empath": "default_1", "clip_parrot": "bored_1"},
    {"id": "S08", "name": "Verse 2 (Build-up)", "start_frame": 4380, "end_frame": 4950, "clip_empath": "walk_1", "clip_parrot": "attack_2"},
    {"id": "S09", "name": "Heavy Bridge Drop", "start_frame": 4950, "end_frame": 5505, "clip_empath": "attack_2", "clip_parrot": "attack_1"},
    {"id": "S10", "name": "Breath Pause", "start_frame": 5505, "end_frame": 5550, "clip_empath": "default_1", "clip_parrot": "bored_1"},
    {"id": "S11", "name": "Climax Final Chorus", "start_frame": 5550, "end_frame": 6510, "clip_empath": "ability_4", "clip_parrot": "attack_1"},
    {"id": "S12", "name": "Outro & Decay", "start_frame": 6510, "end_frame": 6645, "clip_empath": "default_1", "clip_parrot": "bored_1"}
]


class ChoreographyEngine:
    """
    Master engine coordinating skeletal decoding, Slerp crossfade multi-clip blending,
    stage traversal, secondary physics, and facial/shape key animation across 6,645 frames.
    """
    def __init__(
        self,
        project_root: str = PROJECT_ROOT,
        total_frames: int = 6645,
        fps: float = 30.0,
        vocal_timeline_path: Optional[str] = None,
        empath_lip_sync_path: Optional[str] = None,
        parrot_anim_path: Optional[str] = None,
    ):
        self.project_root = os.path.normpath(project_root).replace("\\", "/")
        self.total_frames = total_frames
        self.fps = fps
        self.dt = 1.0 / fps
        self.blend_window = 24  # frames of smooth crossfade transition between clips (0.80s)

        # Load sub-engines
        self.clip_library = ClipLibrary(self.project_root)
        self.trajectory_engine = TrajectoryEngine(total_frames=total_frames, fps=fps)
        self.secondary_physics = SecondaryDynamicsEngine(fps=fps)

        # Dataset paths
        self.vocal_timeline_path = vocal_timeline_path or self._find_asset_path("vocal_timeline.json")
        self.empath_lip_sync_path = empath_lip_sync_path or self._find_asset_path("empath_lip_sync.json")
        self.parrot_anim_path = parrot_anim_path or self._find_asset_path("parrot_animation.json")

        self.vocal_timeline: Dict[str, Any] = {}
        self.empath_lip_sync: Dict[str, Any] = {}
        self.parrot_anim: Dict[str, Any] = {}
        self._load_datasets()

    def _find_asset_path(self, filename: str) -> str:
        """Finds asset path checking assets/cinematic/ first, then fallback."""
        p1 = os.path.join(self.project_root, "assets", "cinematic", filename)
        if os.path.exists(p1):
            return p1
        p2 = os.path.join(self.project_root, "scratch", "music_video", filename)
        if os.path.exists(p2):
            return p2
        return p1

    def _load_datasets(self):
        if os.path.exists(self.vocal_timeline_path):
            with open(self.vocal_timeline_path, 'r', encoding='utf-8') as f:
                self.vocal_timeline = json.load(f)
            logger.info(f"Loaded vocal timeline from: {self.vocal_timeline_path}")

        if os.path.exists(self.empath_lip_sync_path):
            with open(self.empath_lip_sync_path, 'r', encoding='utf-8') as f:
                self.empath_lip_sync = json.load(f)
            logger.info(f"Loaded Empath lip sync from: {self.empath_lip_sync_path}")

        if os.path.exists(self.parrot_anim_path):
            with open(self.parrot_anim_path, 'r', encoding='utf-8') as f:
                self.parrot_anim = json.load(f)
            logger.info(f"Loaded Parrot animation from: {self.parrot_anim_path}")

    def _get_active_sections_and_weights(self, frame: int) -> List[Tuple[Dict[str, Any], float]]:
        """
        Finds active section(s) and blending weights for the given frame index.
        Applies smooth cosine Hermite transitions across section boundaries.
        """
        f = max(0, min(self.total_frames - 1, frame))
        half_w = self.blend_window // 2

        # Check each internal section boundary
        for k in range(1, len(SECTION_TIMELINE)):
            t_bound = SECTION_TIMELINE[k]["start_frame"]
            if (t_bound - half_w) <= f < (t_bound + half_w):
                prev_sec = SECTION_TIMELINE[k - 1]
                curr_sec = SECTION_TIMELINE[k]
                u = (f - (t_bound - half_w)) / float(self.blend_window)
                w_curr = cosine_smoothstep(u)
                w_prev = 1.0 - w_curr
                return [(prev_sec, w_prev), (curr_sec, w_curr)]

        # If not in any transition window, find the containing section
        for sec in SECTION_TIMELINE:
            if sec["start_frame"] <= f < sec["end_frame"]:
                return [(sec, 1.0)]

        return [(SECTION_TIMELINE[-1], 1.0)]

    def sample_empath_skeletal_frame(self, frame: int) -> Dict[str, Dict[str, np.ndarray]]:
        """
        Computes the complete 65 Mixamo pose bone transforms for Empath at specified frame.
        """
        f = max(0, min(self.total_frames - 1, frame))
        sec_weights = self._get_active_sections_and_weights(f)

        # Sample clips with Slerp multi-clip blending
        bone_transforms: Dict[str, Dict[str, np.ndarray]] = {}

        # Step secondary physics simulation
        pos_emp, vel_emp, acc_emp, yaw_emp = self.trajectory_engine.get_empath_state(f)
        audio_energy = 0.25
        if self.vocal_timeline and "sections" in self.vocal_timeline:
            for s in self.vocal_timeline["sections"]:
                if s.get("start_frame", 0) <= f < s.get("end_frame", 6645):
                    audio_energy = float(s.get("energy_level", 0.25))
                    break
        beat_phase = 2.0 * math.pi * (128.6 / 60.0) * (f / self.fps)
        self.secondary_physics.step_simulation(acc_emp, audio_energy, beat_phase)

        # Sample each mapped Mixamo bone
        for mix_bone, hon_bone in MIXAMO_TO_HON_BONE_MAP.items():
            blended_loc = np.zeros(3, dtype=np.float64)
            blended_quat = np.array([1.0, 0.0, 0.0, 0.0], dtype=np.float64)
            blended_scale = np.ones(3, dtype=np.float64)

            if len(sec_weights) == 1:
                sec, weight = sec_weights[0]
                clip = self.clip_library.get_empath_clip(sec["clip_empath"])
                local_f = float(f - sec["start_frame"])
                if clip:
                    loc, quat, scale = clip.sample_bone_quat(hon_bone, local_f, loop=True)
                    blended_loc = loc
                    blended_quat = quat
                    blended_scale = scale
            else:
                (sec1, w1), (sec2, w2) = sec_weights
                clip1 = self.clip_library.get_empath_clip(sec1["clip_empath"])
                clip2 = self.clip_library.get_empath_clip(sec2["clip_empath"])
                local_f1 = float(f - sec1["start_frame"])
                local_f2 = float(f - sec2["start_frame"])

                loc1, quat1, scale1 = clip1.sample_bone_quat(hon_bone, local_f1, loop=True) if clip1 else (np.zeros(3), np.array([1.,0.,0.,0.]), np.ones(3))
                loc2, quat2, scale2 = clip2.sample_bone_quat(hon_bone, local_f2, loop=True) if clip2 else (np.zeros(3), np.array([1.,0.,0.,0.]), np.ones(3))

                blended_loc = lerp_vec(loc1, loc2, w2)
                blended_quat = quat_slerp(quat1, quat2, w2)
                blended_scale = lerp_vec(scale1, scale2, w2)

            # Apply root motion translation and yaw to Hips
            if mix_bone == "mixamorig:Hips":
                blended_loc = pos_emp.copy()
                yaw_quat = euler_to_quat(0.0, 0.0, yaw_emp + 90.0, order='xyz')
                blended_quat = quat_multiply(yaw_quat, blended_quat)

            # Check if there are secondary physics angular deltas for this bone
            d_p, d_r, d_y = self.secondary_physics.get_bone_angular_delta(hon_bone)
            if abs(d_p) > 1e-4 or abs(d_r) > 1e-4 or abs(d_y) > 1e-4:
                sec_quat = euler_to_quat(d_p, d_r, d_y, order='xyz')
                blended_quat = quat_multiply(blended_quat, sec_quat)

            bone_transforms[mix_bone] = {
                "location": blended_loc,
                "rotation_quaternion": normalize_quat(blended_quat),
                "scale": blended_scale
            }

        return bone_transforms

    def sample_empath_secondary_bones(self, frame: int) -> Dict[str, Dict[str, np.ndarray]]:
        """
        Samples the 26 secondary cloth/hair/prop bones (Box001 - Box029) at specified frame.
        """
        f = max(0, min(self.total_frames - 1, frame))
        sec_weights = self._get_active_sections_and_weights(f)

        secondary_transforms: Dict[str, Dict[str, np.ndarray]] = {}
        box_bones = [f"Box{i:03d}" for i in range(1, 30)]

        for bname in box_bones:
            if len(sec_weights) == 1:
                sec, weight = sec_weights[0]
                clip = self.clip_library.get_empath_clip(sec["clip_empath"])
                local_f = float(f - sec["start_frame"])
                if clip:
                    loc, quat, scale = clip.sample_bone_quat(bname, local_f, loop=True)
                else:
                    loc, quat, scale = np.zeros(3), np.array([1.0, 0.0, 0.0, 0.0]), np.ones(3)
            else:
                (sec1, w1), (sec2, w2) = sec_weights
                clip1 = self.clip_library.get_empath_clip(sec1["clip_empath"])
                clip2 = self.clip_library.get_empath_clip(sec2["clip_empath"])
                local_f1 = float(f - sec1["start_frame"])
                local_f2 = float(f - sec2["start_frame"])

                loc1, quat1, scale1 = clip1.sample_bone_quat(bname, local_f1, loop=True) if clip1 else (np.zeros(3), np.array([1.,0.,0.,0.]), np.ones(3))
                loc2, quat2, scale2 = clip2.sample_bone_quat(bname, local_f2, loop=True) if clip2 else (np.zeros(3), np.array([1.,0.,0.,0.]), np.ones(3))

                loc = lerp_vec(loc1, loc2, w2)
                quat = quat_slerp(quat1, quat2, w2)
                scale = lerp_vec(scale1, scale2, w2)

            dp, dr, dy = self.secondary_physics.get_bone_angular_delta(bname)
            if abs(dp) > 1e-4 or abs(dr) > 1e-4 or abs(dy) > 1e-4:
                sec_q = euler_to_quat(dp, dr, dy, order='xyz')
                quat = quat_multiply(quat, sec_q)

            secondary_transforms[bname] = {
                "location": loc,
                "rotation_quaternion": normalize_quat(quat),
                "scale": scale
            }

        return secondary_transforms

    def sample_parrot_frame(self, frame: int) -> Tuple[np.ndarray, np.ndarray, Dict[str, float]]:
        """
        Samples Courier Parrot 3D position, Euler rotation (with banking), and shape keys.
        """
        f = max(0, min(self.total_frames - 1, frame))
        pos, vel, acc, rot_deg = self.trajectory_engine.get_parrot_state(f)

        shape_keys = {"Beak_Open": 0.0, "Wings_Spread": 0.05, "Crest_Fan": 0.0}
        if self.parrot_anim and "frames" in self.parrot_anim and f < len(self.parrot_anim["frames"]):
            p_frame = self.parrot_anim["frames"][f]
            shape_keys["Beak_Open"] = float(p_frame.get("beak_open", 0.0))
            shape_keys["Wings_Spread"] = float(p_frame.get("wing_spread", 0.05))
            shape_keys["Crest_Fan"] = float(p_frame.get("crest_fan", 0.0))

        return pos, rot_deg, shape_keys

    def sample_empath_blendshapes(self, frame: int) -> Dict[str, float]:
        """
        Samples the 52 ARKit facial blendshape weights for Empath at specified frame.
        """
        f = max(0, min(self.total_frames - 1, frame))
        if self.empath_lip_sync and "frames" in self.empath_lip_sync and f < len(self.empath_lip_sync["frames"]):
            return self.empath_lip_sync["frames"][f].get("blendshapes", {})
        return {}


def generate_choreography_dataset(
    project_root: str = PROJECT_ROOT,
    total_frames: int = 6645,
    fps: float = 30.0,
    output_path: Optional[str] = None,
    verbose: bool = False,
    log_file: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Generates a full pre-computed choreography dataset across all frames.
    """
    setup_cli_logging(verbose, log_file)
    logger.info(f"Generating choreography dataset for {total_frames} frames @ {fps} FPS...")

    engine = ChoreographyEngine(project_root=project_root, total_frames=total_frames, fps=fps)
    dataset = {
        "metadata": {
            "total_frames": total_frames,
            "fps": fps,
            "duration_seconds": total_frames / fps,
            "num_bones_empath": len(MIXAMO_TO_HON_BONE_MAP),
            "num_secondary_bones": 29
        },
        "frames": []
    }

    for f in range(total_frames):
        empath_bones = engine.sample_empath_skeletal_frame(f)
        parrot_pos, parrot_rot, parrot_shapes = engine.sample_parrot_frame(f)

        frame_entry = {
            "frame": f,
            "time": f / fps,
            "empath": {
                "root_pos": empath_bones["mixamorig:Hips"]["location"].tolist(),
                "root_quat": empath_bones["mixamorig:Hips"]["rotation_quaternion"].tolist(),
                "bones": {b: {k: v.tolist() for k, v in empath_bones[b].items()} for b in empath_bones}
            },
            "parrot": {
                "pos": parrot_pos.tolist(),
                "rot_deg": parrot_rot.tolist(),
                "shape_keys": parrot_shapes
            }
        }
        dataset["frames"].append(frame_entry)

    logger.info(f"Generated choreography dataset with {len(dataset['frames'])} frames.")

    if output_path:
        os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
        with open(output_path, "w", encoding="utf-8") as f:
            json.dump(dataset, f, indent=2)
        logger.info(f"Saved choreography dataset to {output_path}")

    return dataset


def setup_cli_logging(verbose: bool = False, log_file: Optional[str] = None) -> None:
    """Configures structured logging with optional file output."""
    level = logging.DEBUG if verbose else logging.INFO
    fmt = "%(asctime)s [%(levelname)s] [%(name)s] %(message)s"
    handlers = [logging.StreamHandler(sys.stdout)]
    if log_file:
        handlers.append(logging.FileHandler(log_file, encoding="utf-8"))
    logging.basicConfig(level=level, format=fmt, handlers=handlers, force=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Master Skeletal Choreography Engine")
    parser.add_argument("--frames", type=int, default=6645, help="Total frame count (default: 6645)")
    parser.add_argument("--fps", type=float, default=30.0, help="Framerate in FPS (default: 30.0)")
    parser.add_argument("--output", "-o", type=str, default=None, help="Optional output JSON path")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable verbose debug logging")
    parser.add_argument("--log-file", type=str, default=None, help="Optional path to write structured log file")
    args = parser.parse_args()

    setup_cli_logging(args.verbose, args.log_file)
    logger.info("Initializing ChoreographyEngine...")

    engine = ChoreographyEngine(total_frames=args.frames, fps=args.fps)
    f0_empath = engine.sample_empath_skeletal_frame(0)
    f0_parrot_pos, f0_parrot_rot, f0_shapes = engine.sample_parrot_frame(0)

    logger.info(f"Empath f0 Hips Pos: {f0_empath['mixamorig:Hips']['location']}")
    logger.info(f"Empath f0 Hips Quat: {f0_empath['mixamorig:Hips']['rotation_quaternion']}")
    logger.info(f"Empath f0 Spine2 Quat: {f0_empath['mixamorig:Spine2']['rotation_quaternion']}")
    logger.info(f"Parrot f0 Pos: {f0_parrot_pos}, Rot: {f0_parrot_rot}, Shapes: {f0_shapes}")

    if args.output:
        generate_choreography_dataset(
            project_root=PROJECT_ROOT,
            total_frames=args.frames,
            fps=args.fps,
            output_path=args.output,
            verbose=args.verbose,
            log_file=args.log_file,
        )

    logger.info("ChoreographyEngine evaluation completed successfully.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

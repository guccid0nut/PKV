#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Cinematography Director Engine for Dynamic 360° Orbital & Multi-Angle Cinematography.
Project: "Can You Feel My Heart" 3D Cinematic Duet Music Video (Empath & Courier Parrot).

Orchestrates the 7 canonical camera shots aligned with musical beats and lyric transitions across
all 6,645 frames (30 FPS, 221.5068s, 128.6 BPM):
  - Shot 1 (f1–f750): Intro Dolly-In (35mm, f/2.8)
  - Shot 2 (f751–f1400): Clean Vocals Medium Close-up (85mm, f/1.8, Empath focus)
  - Shot 3 (f1401–f2250): Pre-Chorus Wide Stage Traverse (24mm, f/4.0, dual framing)
  - Shot 4 (f2251–f3100): Chorus 1 Metal Scream Drop Low-Angle Close-up (50mm, f/2.0, low-angle parrot screeches)
  - Shot 5 (f3101–f4400): Chorus Climax Continuous 360° Orbital Camera Track (35mm, f/2.8, full 2pi sweep around Empath/stage)
  - Shot 6 (f4401–f5600): Bridge Duet Harmony Orbiting Two-Shot (50mm, f/2.2, midpoint tracking)
  - Shot 7 (f5601–f6645): Grand Outro High-Angle Pull-Out & Spiral (24mm, f/4.0, grand outro)
"""

import os
import sys
import math
import json
import logging
import argparse
import numpy as np
from typing import Dict, List, Tuple, Any, Optional

PROJECT_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
if PROJECT_ROOT not in sys.path:
    sys.path.insert(0, PROJECT_ROOT)

from tools.cinematic.stage_trajectories import TrajectoryEngine

logger = logging.getLogger("tools.cinematic.cinematography_director")

# Canonical Shot Definitions & Metadata
CANONICAL_SHOTS = [
    {
        "id": 1,
        "name": "Shot1_Intro_Dolly_In",
        "description": "Slow push from wide studio doorway to Empath at mic stand",
        "start_frame": 1,
        "end_frame": 750,
        "duration_frames": 750,
        "duration_sec": 25.00,
        "focal_length_mm": 35.0,
        "shot_type": "DOLLY_IN",
        "base_fstop": 2.8,
        "focus_subject": "Empath Golden Mic Stand"
    },
    {
        "id": 2,
        "name": "Shot2_Empath_CloseUp",
        "description": "Emotional portrait framing on Empath face during clean vocals",
        "start_frame": 751,
        "end_frame": 1400,
        "duration_frames": 650,
        "duration_sec": 21.67,
        "focal_length_mm": 85.0,
        "shot_type": "CLOSE_UP",
        "base_fstop": 1.8,
        "focus_subject": "Empath Face / Lip-Sync"
    },
    {
        "id": 3,
        "name": "Shot3_Studio_Wide_Traverse",
        "description": "Lateral track framing stage and studio environment during verse walk",
        "start_frame": 1401,
        "end_frame": 2250,
        "duration_frames": 850,
        "duration_sec": 28.33,
        "focal_length_mm": 24.0,
        "shot_type": "WIDE_TRAVERSE",
        "base_fstop": 4.0,
        "focus_subject": "Stage Traversal Centroid"
    },
    {
        "id": 4,
        "name": "Shot4_Low_Angle_Parrot_Scream",
        "description": "Heroic upward angle on perched and screaming Courier Parrot",
        "start_frame": 2251,
        "end_frame": 3100,
        "duration_frames": 850,
        "duration_sec": 28.33,
        "focal_length_mm": 50.0,
        "shot_type": "LOW_ANGLE",
        "base_fstop": 2.0,
        "focus_subject": "Courier Parrot Scream & Hover"
    },
    {
        "id": 5,
        "name": "Shot5_Chorus_360_Orbit",
        "description": "Sweeping continuous 360-degree circular camera arc around performers",
        "start_frame": 3101,
        "end_frame": 4400,
        "duration_frames": 1300,
        "duration_sec": 43.33,
        "focal_length_mm": 35.0,
        "shot_type": "ORBITAL_360",
        "base_fstop": 2.8,
        "focus_subject": "Duet Center Stage"
    },
    {
        "id": 6,
        "name": "Shot6_Dual_Two_Shot",
        "description": "Balanced composition framing both Empath and Parrot together",
        "start_frame": 4401,
        "end_frame": 5600,
        "duration_frames": 1200,
        "duration_sec": 40.00,
        "focal_length_mm": 50.0,
        "shot_type": "TWO_SHOT",
        "base_fstop": 2.2,
        "focus_subject": "Weighted Performer Midpoint"
    },
    {
        "id": 7,
        "name": "Shot7_Outro_Pull_Out",
        "description": "Continuous spiral arc and high crane pull-back into darkness",
        "start_frame": 5601,
        "end_frame": 6645,
        "duration_frames": 1045,
        "duration_sec": 34.83,
        "focal_length_mm": 24.0,
        "shot_type": "PULL_OUT",
        "base_fstop": 4.0,
        "focus_subject": "Diminishing Stage Center"
    }
]

# Canonical Cut Frames (1-based frame boundaries)
CANONICAL_CUT_FRAMES = [1, 751, 1401, 2251, 3101, 4401, 5601]
CANONICAL_CUT_PAIRS = [(750, 751), (1400, 1401), (2250, 2251), (3100, 3101), (4400, 4401), (5600, 5601)]

# Scream Drop Onsets (1-based frame indices)
SCREAM_DROP_ONSETS = [825, 2820, 5550]


def hermite_smoothstep(u: float) -> float:
    """Evaluates cubic Hermite smoothstep in [0, 1]."""
    u_c = max(0.0, min(1.0, float(u)))
    return u_c * u_c * (3.0 - 2.0 * u_c)


class CinematographyDirector:
    """
    Automated Cinematography Director system generating frame-by-frame camera positions,
    look-at targets, animated focal lengths, depth of field settings, and beat cuts across
    all 6,645 frames.
    """

    def __init__(
        self,
        project_root: Optional[str] = None,
        total_frames: int = 6645,
        fps: float = 30.0,
        bpm: float = 128.6
    ):
        self.project_root = project_root or PROJECT_ROOT
        self.total_frames = total_frames
        self.fps = fps
        self.dt = 1.0 / fps
        self.bpm = bpm
        self.beat_freq = bpm / 60.0
        self.beat_period_frames = (60.0 / bpm) * fps

        logger.info(f"CinematographyDirector: Initializing TrajectoryEngine for {total_frames} frames @ {fps} FPS...")
        self.traj_engine = TrajectoryEngine(total_frames=total_frames, fps=fps, bpm=bpm)

        # Full Frame 35mm Sensor Constants
        self.SENSOR_WIDTH_MM = 36.0
        self.SENSOR_HEIGHT_MM = 24.0
        self.ASPECT_RATIO = 16.0 / 9.0

        # Stage Landmarks
        self.MIC_STAND = np.array([0.05, 0.85, 0.00], dtype=np.float64)
        self.STAGE_CENTER_ELEVATED = np.array([0.05, 0.85, 1.20], dtype=np.float64)
        self.DOOR_PERCH = np.array([0.80, 1.55, 1.025], dtype=np.float64)

        # Precompute dense trajectory arrays across all frames
        self.cam_pos = np.zeros((total_frames, 3), dtype=np.float64)
        self.cam_target = np.zeros((total_frames, 3), dtype=np.float64)
        self.focal_length_mm = np.zeros(total_frames, dtype=np.float64)
        self.aperture_fstop = np.zeros(total_frames, dtype=np.float64)
        self.focus_distance = np.zeros(total_frames, dtype=np.float64)
        self.shot_indices = np.zeros(total_frames, dtype=np.int32)
        self.elevation_angles_deg = np.zeros(total_frames, dtype=np.float64)
        self.azimuth_angles_rad = np.zeros(total_frames, dtype=np.float64)

        self._build_cinematography_timeline()

    def _build_cinematography_timeline(self):
        """Precomputes deterministic camera kinematics across all frames."""
        logger.info("Computing 7-shot camera trajectories and dynamic targets...")

        for f_idx in range(self.total_frames):
            frame_1based = f_idx + 1
            shot = self._get_shot_definition(frame_1based)
            shot_id = shot["id"]
            self.shot_indices[f_idx] = shot_id

            f_len = shot["focal_length_mm"]
            f_stop = shot["base_fstop"]

            e_pos, _, _, _ = self.traj_engine.get_empath_state(f_idx)
            p_pos, _, _, _ = self.traj_engine.get_parrot_state(f_idx)

            # Shot 1: Intro Dolly-In (f1 - f750)
            if shot_id == 1:
                u = (frame_1based - 1) / max(1, 750 - 1)
                s = hermite_smoothstep(u)
                p_start = np.array([0.00, -3.20, 1.35], dtype=np.float64)
                p_end = np.array([0.05, -2.20, 1.28], dtype=np.float64)
                pos = (1.0 - s) * p_start + s * p_end
                target = self.STAGE_CENTER_ELEVATED.copy()

            # Shot 2: Clean Vocals Close-Up (f751 - f1400)
            elif shot_id == 2:
                u = (frame_1based - 751) / max(1, 1400 - 751)
                p_start = np.array([0.25, -0.30, 1.35], dtype=np.float64)
                p_end = np.array([-0.15, -0.55, 1.30], dtype=np.float64)
                sway_x = 0.015 * math.sin(2.0 * math.pi * (frame_1based - 751) / 180.0)
                sway_z = 0.010 * math.cos(2.0 * math.pi * (frame_1based - 751) / 240.0)
                pos = (1.0 - u) * p_start + u * p_end + np.array([sway_x, 0.0, sway_z])
                target = np.array([e_pos[0], e_pos[1], e_pos[2] + 1.45], dtype=np.float64)

            # Shot 3: Studio Wide Stage Traverse (f1401 - f2250)
            elif shot_id == 3:
                u = (frame_1based - 1401) / max(1, 2250 - 1401)
                p_start = np.array([-1.00, -3.80, 1.50], dtype=np.float64)
                p_end = np.array([0.90, -3.50, 1.40], dtype=np.float64)
                pos = (1.0 - u) * p_start + u * p_end
                target = np.array([0.85 * e_pos[0] + 0.15 * p_pos[0], 0.85 * e_pos[1] + 0.15 * p_pos[1], 1.10], dtype=np.float64)

            # Shot 4: Chorus 1 Scream Drop Low-Angle Close-Up (f2251 - f3100)
            elif shot_id == 4:
                u = (frame_1based - 2251) / max(1, 3100 - 2251)
                p_start = np.array([-0.95, 0.20, 0.75], dtype=np.float64)
                p_end = np.array([-0.55, 0.40, 0.80], dtype=np.float64)
                pos = (1.0 - u) * p_start + u * p_end
                target = np.array([p_pos[0], p_pos[1], p_pos[2] + 0.10], dtype=np.float64)

            # Shot 5: Chorus Climax Continuous 360° Orbital Camera Track (f3101 - f4400)
            elif shot_id == 5:
                u = (frame_1based - 3101) / max(1, 4400 - 3101)
                theta = -0.5 * math.pi + 2.0 * math.pi * u
                radius = 3.50 + 0.20 * math.sin(2.0 * math.pi * u)
                z_cam = 1.70 + 0.25 * math.sin(2.0 * theta)
                target = self.STAGE_CENTER_ELEVATED.copy()
                pos = np.array([
                    target[0] + radius * math.cos(theta),
                    target[1] + radius * math.sin(theta),
                    z_cam
                ], dtype=np.float64)

            # Shot 6: Bridge Duet Harmony Orbiting Two-Shot (f4401 - f5600)
            elif shot_id == 6:
                p_start = np.array([-1.60, -2.60, 0.95], dtype=np.float64)
                p_freeze = np.array([0.75, -2.35, 1.18], dtype=np.float64)
                if frame_1based < 5505:
                    u = (frame_1based - 4401) / max(1, 5505 - 4401)
                    pos = (1.0 - u) * p_start + u * p_freeze
                elif frame_1based <= 5550:
                    pos = p_freeze.copy()
                else:
                    u_post = (frame_1based - 5550) / max(1, 5600 - 5550)
                    pos = p_freeze + np.array([0.05 * u_post, -0.05 * u_post, 0.02 * u_post])
                target = np.array([
                    0.65 * e_pos[0] + 0.35 * p_pos[0],
                    0.65 * e_pos[1] + 0.35 * p_pos[1],
                    1.15
                ], dtype=np.float64)

            # Shot 7: Grand Outro High-Angle Pull-Out & Spiral (f5601 - f6645)
            else:
                u = (frame_1based - 5601) / max(1, 6645 - 5601)
                theta = -0.65 * math.pi + 2.0 * math.pi * (1.0 - (1.0 - u) ** 2)
                radius = 3.00 + 2.20 * (u ** 1.3)
                z_cam = 1.30 + 1.10 * (u ** 1.1)
                target = np.array([0.05, 0.85, 0.80], dtype=np.float64)
                pos = np.array([
                    target[0] + radius * math.cos(theta),
                    target[1] + radius * math.sin(theta),
                    z_cam
                ], dtype=np.float64)

            # Scream Shake Impulses
            shake_offset = self._compute_camera_shake(frame_1based)
            pos = pos + shake_offset

            self.cam_pos[f_idx] = pos
            self.cam_target[f_idx] = target
            self.focal_length_mm[f_idx] = f_len
            self.aperture_fstop[f_idx] = f_stop

            delta_target = target - pos
            dist = float(np.linalg.norm(delta_target))
            self.focus_distance[f_idx] = dist

            horiz_dist = float(math.hypot(delta_target[0], delta_target[1]))
            pitch_elev_rad = math.atan2(delta_target[2], max(1e-4, horiz_dist))
            self.elevation_angles_deg[f_idx] = math.degrees(pitch_elev_rad)

            rel_center = pos - self.STAGE_CENTER_ELEVATED
            self.azimuth_angles_rad[f_idx] = math.atan2(rel_center[1], rel_center[0])

        logger.info(f"CinematographyDirector: {self.total_frames}-frame timeline computation completed.")

    def _compute_camera_shake(self, frame_1based: int) -> np.ndarray:
        """Computes damped transient harmonic shake impulse for scream drops."""
        shake = np.zeros(3, dtype=np.float64)
        for drop_frame in SCREAM_DROP_ONSETS:
            if drop_frame <= frame_1based < drop_frame + 15:
                delta_f = frame_1based - drop_frame
                decay = math.exp(-delta_f / 4.0)
                freq = 0.35 * 2.0 * math.pi
                shake_amp = 0.035 * decay
                shake[0] += shake_amp * math.sin(freq * delta_f)
                shake[1] += shake_amp * math.cos(freq * delta_f) * 0.5
                shake[2] += shake_amp * math.sin(freq * delta_f + math.pi / 4.0)
        return shake

    def _get_shot_definition(self, frame_1based: int) -> Dict[str, Any]:
        """Returns the canonical shot definition for a given 1-based frame index."""
        f = max(1, min(self.total_frames, int(frame_1based)))
        for shot in CANONICAL_SHOTS:
            if shot["start_frame"] <= f <= shot["end_frame"]:
                return shot
        return CANONICAL_SHOTS[-1]

    def sample_frame(self, frame_1based: int) -> Dict[str, Any]:
        """
        Samples cinematography properties at the specified 1-based frame index (1..6645).
        """
        f = max(1, min(self.total_frames, int(frame_1based)))
        idx = f - 1

        shot = self._get_shot_definition(f)
        f_len = float(self.focal_length_mm[idx])
        fov_rad = 2.0 * math.atan(self.SENSOR_WIDTH_MM / (2.0 * f_len))
        fov_deg = math.degrees(fov_rad)

        return {
            "frame": f,
            "cam_location": self.cam_pos[idx].copy(),
            "target_location": self.cam_target[idx].copy(),
            "focal_length_mm": f_len,
            "aperture_fstop": float(self.aperture_fstop[idx]),
            "focus_distance": float(self.focus_distance[idx]),
            "fov_h_deg": fov_deg,
            "elevation_deg": float(self.elevation_angles_deg[idx]),
            "azimuth_rad": float(self.azimuth_angles_rad[idx]),
            "shot_id": int(shot["id"]),
            "shot_name": shot["name"],
            "shot_type": shot["shot_type"]
        }

    def get_cut_frames(self) -> List[int]:
        """Returns the list of 1-based cut keyframes."""
        return CANONICAL_CUT_FRAMES.copy()

    def get_all_frames_data(self) -> Dict[str, np.ndarray]:
        """Returns dictionary of full numpy arrays for fast batch processing and baking."""
        return {
            "cam_pos": self.cam_pos,
            "cam_target": self.cam_target,
            "focal_length_mm": self.focal_length_mm,
            "aperture_fstop": self.aperture_fstop,
            "focus_distance": self.focus_distance,
            "shot_indices": self.shot_indices,
            "elevation_angles_deg": self.elevation_angles_deg,
            "azimuth_angles_rad": self.azimuth_angles_rad
        }


def setup_cli_logging(verbose: bool = False, log_file: Optional[str] = None) -> None:
    """Configures structured logging with optional file output."""
    level = logging.DEBUG if verbose else logging.INFO
    fmt = "%(asctime)s [%(levelname)s] [%(name)s] %(message)s"
    handlers = [logging.StreamHandler(sys.stdout)]
    if log_file:
        handlers.append(logging.FileHandler(log_file, encoding="utf-8"))
    logging.basicConfig(level=level, format=fmt, handlers=handlers, force=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Cinematography Director & 360° Orbital Engine")
    parser.add_argument("--frames", type=int, default=6645, help="Total frame count (default: 6645)")
    parser.add_argument("--fps", type=float, default=30.0, help="Framerate in FPS (default: 30.0)")
    parser.add_argument("--bpm", type=float, default=128.6, help="Tempo in BPM (default: 128.6)")
    parser.add_argument("--output", "-o", type=str, default=None, help="Optional output JSON path")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable verbose debug logging")
    parser.add_argument("--log-file", type=str, default=None, help="Optional path to write structured log file")
    args = parser.parse_args()

    setup_cli_logging(args.verbose, args.log_file)
    logger.info("Initializing CinematographyDirector...")

    director = CinematographyDirector(total_frames=args.frames, fps=args.fps, bpm=args.bpm)
    logger.info(f"Canonical Shots: {len(CANONICAL_SHOTS)} | Cut Frames: {director.get_cut_frames()}")

    sample_test_frames = [1, 225, 750, 751, 825, 1400, 1401, 2250, 2251, 2820, 3100, 3101, 4400, 4401, 5550, 5600, 5601, args.frames]
    for sf in sample_test_frames:
        if sf <= args.frames:
            data = director.sample_frame(sf)
            logger.info(
                f"Frame {sf:4d} | Shot {data['shot_id']} ({data['shot_type']:12s}) | "
                f"Cam: [{data['cam_location'][0]:6.2f}, {data['cam_location'][1]:6.2f}, {data['cam_location'][2]:6.2f}] | "
                f"Lens: {data['focal_length_mm']:4.1f}mm | f/{data['aperture_fstop']:.1f} | "
                f"Dist: {data['focus_distance']:4.2f}m | FOV: {data['fov_h_deg']:4.1f}°"
            )

    if args.output:
        all_data = {
            "metadata": {"frames": args.frames, "fps": args.fps, "bpm": args.bpm},
            "cuts": director.get_cut_frames(),
            "frames": [director.sample_frame(f) for f in range(1, args.frames + 1)]
        }
        # Convert numpy arrays to list
        for f_entry in all_data["frames"]:
            f_entry["cam_location"] = f_entry["cam_location"].tolist()
            f_entry["target_location"] = f_entry["target_location"].tolist()
        os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
        with open(args.output, "w", encoding="utf-8") as f:
            json.dump(all_data, f, indent=2)
        logger.info(f"Saved cinematography timeline to {args.output}")

    logger.info("CinematographyDirector evaluation completed successfully.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

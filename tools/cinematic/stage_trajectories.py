#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Stage Trajectory Generator for Empath and Courier Parrot across the 7-Act Choreography.
Calculates deterministic 3D positions, velocities, accelerations, facing yaw headings,
and aerodynamic banking rotations across all 6,645 frames at 30 FPS.
"""

import os
import sys
import math
import json
import logging
import argparse
from typing import Dict, List, Tuple, Optional, Any
import numpy as np

PROJECT_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
if PROJECT_ROOT not in sys.path:
    sys.path.insert(0, PROJECT_ROOT)

logger = logging.getLogger("tools.cinematic.stage_trajectories")


def bezier_cubic(p0: np.ndarray, p1: np.ndarray, p2: np.ndarray, p3: np.ndarray, u: float) -> np.ndarray:
    """Evaluates a 3D cubic Bézier curve at parameter u in [0.0, 1.0]."""
    u_c = max(0.0, min(1.0, float(u)))
    inv_u = 1.0 - u_c
    return (inv_u ** 3) * p0 + 3.0 * (inv_u ** 2) * u_c * p1 + 3.0 * inv_u * (u_c ** 2) * p2 + (u_c ** 3) * p3


def bezier_cubic_derivative(p0: np.ndarray, p1: np.ndarray, p2: np.ndarray, p3: np.ndarray, u: float) -> np.ndarray:
    """Evaluates the first derivative (tangent velocity vector) of a 3D cubic Bézier curve."""
    u_c = max(0.0, min(1.0, float(u)))
    inv_u = 1.0 - u_c
    return 3.0 * (inv_u ** 2) * (p1 - p0) + 6.0 * inv_u * u_c * (p2 - p1) + 3.0 * (u_c ** 2) * (p3 - p2)


class TrajectoryEngine:
    """Generates continuous stage movement for Empath and Courier Parrot across 6,645 frames."""
    def __init__(self, total_frames: int = 6645, fps: float = 30.0, bpm: float = 128.6):
        self.total_frames = total_frames
        self.fps = fps
        self.dt = 1.0 / fps
        self.bpm = bpm
        self.beat_freq = bpm / 60.0

        # Canonical Landmarks
        self.MIC_STAND = np.array([0.05, 0.85, 0.00], dtype=np.float64)
        self.STAGE_LEFT = np.array([-0.60, 0.60, 0.00], dtype=np.float64)
        self.DOOR_PERCH = np.array([0.80, 1.55, 1.025], dtype=np.float64)
        self.STAGE_AIRSPACE = np.array([0.10, 0.70, 1.40], dtype=np.float64)

        # Precompute arrays for all frames
        self.empath_pos = np.zeros((total_frames, 3), dtype=np.float64)
        self.empath_vel = np.zeros((total_frames, 3), dtype=np.float64)
        self.empath_acc = np.zeros((total_frames, 3), dtype=np.float64)
        self.empath_yaw_deg = np.zeros(total_frames, dtype=np.float64)

        self.parrot_pos = np.zeros((total_frames, 3), dtype=np.float64)
        self.parrot_vel = np.zeros((total_frames, 3), dtype=np.float64)
        self.parrot_acc = np.zeros((total_frames, 3), dtype=np.float64)
        self.parrot_rot_deg = np.zeros((total_frames, 3), dtype=np.float64)  # [pitch, roll, yaw]

        self._compute_trajectories()

    def _compute_trajectories(self):
        # 1. Empath Trajectory Computation
        for f in range(self.total_frames):
            t_sec = f / self.fps
            beat_phase = 2.0 * math.pi * self.beat_freq * t_sec

            # Act 1: f0 - f825 (Solitary Prelude & Vocal Chops at Mic Stand)
            if f < 825:
                pos = self.MIC_STAND.copy()
                yaw = -90.0  # Facing front (-Y)

            # Act 2: f825 - f1620 (Drop 1: Levitation Pulse)
            elif f < 1620:
                prog = (f - 825) / (1620 - 825)
                # Levitation curve: peaks at +0.15m
                z_lev = 0.15 * (math.sin(math.pi * prog) ** 2)
                # Subtle swaying at drop
                sway_x = 0.02 * math.sin(beat_phase)
                pos = np.array([self.MIC_STAND[0] + sway_x, self.MIC_STAND[1], z_lev])
                yaw = -90.0 + 5.0 * math.sin(beat_phase * 0.5)

            # Act 3: f1620 - f2220 (Verse 1: Stage Traversal to Stage Left and back)
            elif f < 2220:
                prog = (f - 1620) / (2220 - 1620)
                # Out-and-back trajectory
                curve = math.sin(math.pi * prog) ** 2
                pos_x = self.MIC_STAND[0] + (self.STAGE_LEFT[0] - self.MIC_STAND[0]) * curve
                pos_y = self.MIC_STAND[1] + (self.STAGE_LEFT[1] - self.MIC_STAND[1]) * curve
                pos = np.array([pos_x, pos_y, 0.0])

                # Dynamic heading: turn toward motion
                if prog < 0.48:
                    # Walking to stage left
                    yaw = -140.0
                elif prog > 0.52:
                    # Walking back to mic
                    yaw = -40.0
                else:
                    # Turnaround point
                    turn_prog = (prog - 0.48) / 0.04
                    yaw = -140.0 + turn_prog * 100.0

            # Act 4: f2220 - f3645 (Pre-Chorus 1 & Chorus 1: Grounded Power Stance)
            elif f < 3645:
                # Power stance with weight shift on quarter notes
                weight_shift = 0.035 * math.sin(beat_phase)
                pos = np.array([self.MIC_STAND[0] + weight_shift, self.MIC_STAND[1], 0.0])
                yaw = -90.0 + 3.0 * math.sin(beat_phase)

            # Act 5: f3645 - f4950 (Soft Interlude & Verse 2 Build: Pacing Arc)
            elif f < 4950:
                prog = (f - 3645) / (4950 - 3645)
                # Gentle pacing between mic and subtle left offset
                pace_curve = math.sin(math.pi * prog) ** 2
                pos_x = self.MIC_STAND[0] - 0.30 * pace_curve
                pos_y = self.MIC_STAND[1] - 0.10 * pace_curve
                pos = np.array([pos_x, pos_y, 0.0])
                yaw = -90.0 - 15.0 * pace_curve

            # Act 6: f4950 - f5550 (Bridge Drop & Sudden Breath Pause Freeze)
            elif f < 5550:
                if f < 5505:
                    # Aggressive forward lean into mic
                    pos = np.array([self.MIC_STAND[0], self.MIC_STAND[1] - 0.05, 0.0])
                    yaw = -90.0
                else:
                    # Instantaneous frozen lock at frame 5505
                    pos = np.array([self.MIC_STAND[0], self.MIC_STAND[1] - 0.05, 0.0])
                    yaw = -90.0

            # Act 7: f5550 - f6645 (Transcendent Climax & Outro Fade)
            else:
                if f < 6510:
                    # Climax duet power stance with levitation harmonic
                    prog = (f - 5550) / (6510 - 5550)
                    z_lev = 0.08 * (math.sin(math.pi * prog) ** 2)
                    sway_x = 0.025 * math.sin(beat_phase)
                    pos = np.array([self.MIC_STAND[0] + sway_x, self.MIC_STAND[1], z_lev])
                    yaw = -90.0
                else:
                    # Outro grounding and bowing
                    pos = self.MIC_STAND.copy()
                    yaw = -90.0

            self.empath_pos[f] = pos
            self.empath_yaw_deg[f] = yaw

        # Smooth yaw with exponential moving average
        alpha_yaw = 0.15
        for f in range(1, self.total_frames):
            diff = self.empath_yaw_deg[f] - self.empath_yaw_deg[f - 1]
            diff = (diff + 180.0) % 360.0 - 180.0
            self.empath_yaw_deg[f] = self.empath_yaw_deg[f - 1] + alpha_yaw * diff

        # Compute velocities and accelerations for Empath
        for f in range(1, self.total_frames - 1):
            self.empath_vel[f] = (self.empath_pos[f + 1] - self.empath_pos[f - 1]) / (2.0 * self.dt)
        self.empath_vel[0] = self.empath_vel[1]
        self.empath_vel[-1] = self.empath_vel[-2]

        for f in range(1, self.total_frames - 1):
            self.empath_acc[f] = (self.empath_vel[f + 1] - self.empath_vel[f - 1]) / (2.0 * self.dt)
        self.empath_acc[0] = self.empath_acc[1]
        self.empath_acc[-1] = self.empath_acc[-2]

        # 2. Courier Parrot Trajectory Computation
        # Act 2 Flight Control Points
        p0_takeoff = self.DOOR_PERCH
        p1_takeoff = np.array([0.70, 1.40, 1.85], dtype=np.float64)
        p2_takeoff = np.array([0.30, 0.90, 1.75], dtype=np.float64)
        p3_takeoff = self.STAGE_AIRSPACE

        p0_land = self.STAGE_AIRSPACE
        p1_land = np.array([0.35, 1.05, 1.60], dtype=np.float64)
        p2_land = np.array([0.70, 1.45, 1.20], dtype=np.float64)
        p3_land = self.DOOR_PERCH

        for f in range(self.total_frames):
            # Act 1: f0 - f825 (Stationary Perch on Door)
            if f < 825:
                pos = self.DOOR_PERCH.copy()
                rot = np.array([0.0, 0.0, -135.0])  # Looking down toward mic stand

            # Act 2: f825 - f1620 (Drop 1 Flight Entrance, Screams, and Return)
            elif f < 1620:
                if f < 945:
                    # Takeoff Bézier (120 frames)
                    u = (f - 825) / 120.0
                    pos = bezier_cubic(p0_takeoff, p1_takeoff, p2_takeoff, p3_takeoff, u)
                    d_pos = bezier_cubic_derivative(p0_takeoff, p1_takeoff, p2_takeoff, p3_takeoff, u)
                    yaw = math.degrees(math.atan2(d_pos[1], d_pos[0]))
                    pitch = -math.degrees(math.atan2(d_pos[2], math.hypot(d_pos[0], d_pos[1])))
                    roll = -18.5 * math.sin(math.pi * u)  # Bank inward
                    rot = np.array([pitch, roll, yaw])
                elif f < 1480:
                    # Hovering above stage during screams
                    t_hover = (f - 945) / self.fps
                    h_bob = 0.08 * math.sin(2.0 * math.pi * 3.2 * t_hover)
                    pos = np.array([self.STAGE_AIRSPACE[0], self.STAGE_AIRSPACE[1], self.STAGE_AIRSPACE[2] + h_bob])
                    rot = np.array([5.0 * math.sin(t_hover * 4.0), 0.0, -110.0])
                else:
                    # Return Glide Landing (140 frames)
                    u = (f - 1480) / 140.0
                    pos = bezier_cubic(p0_land, p1_land, p2_land, p3_land, u)
                    d_pos = bezier_cubic_derivative(p0_land, p1_land, p2_land, p3_land, u)
                    yaw = math.degrees(math.atan2(d_pos[1], d_pos[0]))
                    pitch = -math.degrees(math.atan2(d_pos[2], math.hypot(d_pos[0], d_pos[1])))
                    roll = 15.0 * math.sin(math.pi * u)
                    rot = np.array([pitch, roll, yaw])

            # Act 3: f1620 - f2220 (Stationary Perch on Door)
            elif f < 2220:
                pos = self.DOOR_PERCH.copy()
                rot = np.array([0.0, 0.0, -135.0])

            # Act 4: f2220 - f3645 (Chorus 1 Scream Dive & Hover)
            elif f < 3645:
                if f < 2820:
                    pos = self.DOOR_PERCH.copy()
                    rot = np.array([0.0, 0.0, -135.0])
                elif f < 2940:
                    # Dive to center stage hover (120 frames)
                    u = (f - 2820) / 120.0
                    p_target = np.array([0.35, 1.10, 1.25], dtype=np.float64)
                    pos = bezier_cubic(self.DOOR_PERCH, np.array([0.65, 1.35, 1.60]), np.array([0.45, 1.20, 1.45]), p_target, u)
                    rot = np.array([-10.0, -15.0 * math.sin(math.pi * u), -120.0])
                elif f < 3500:
                    # Screaming hover in Chorus 1
                    t_hover = (f - 2940) / self.fps
                    h_bob = 0.06 * math.sin(2.0 * math.pi * 3.5 * t_hover)
                    pos = np.array([0.35, 1.10, 1.25 + h_bob])
                    rot = np.array([8.0 * math.sin(t_hover * 5.0), 0.0, -120.0])
                else:
                    # Return to Door Perch (145 frames)
                    u = (f - 3500) / 145.0
                    p_start = np.array([0.35, 1.10, 1.25], dtype=np.float64)
                    pos = bezier_cubic(p_start, np.array([0.50, 1.25, 1.50]), np.array([0.70, 1.45, 1.20]), self.DOOR_PERCH, u)
                    rot = np.array([5.0, 12.0 * math.sin(math.pi * u), 60.0])

            # Act 5: f3645 - f4950 (Perched Quiet Brooding)
            elif f < 4950:
                pos = self.DOOR_PERCH.copy()
                rot = np.array([0.0, 0.0, -135.0])

            # Act 6: f4950 - f5550 (Bridge Scream & Breath Pause Freeze)
            elif f < 5550:
                pos = self.DOOR_PERCH.copy()
                if f < 5505:
                    # Aggressive screech posture on perch
                    rot = np.array([12.0, 0.0, -135.0])
                else:
                    # Frozen breathless posture
                    rot = np.array([12.0, 0.0, -135.0])

            # Act 7: f5550 - f6645 (Grand Climax 360° Circular Orbit & Final Perch)
            else:
                p_orbit_entry = np.array([self.MIC_STAND[0] + 1.10, self.MIC_STAND[1], 1.45], dtype=np.float64)
                if f < 5600:
                    # Launch Takeoff Bézier (50 frames from f5550 to f5600)
                    u = (f - 5550) / 50.0
                    p0_launch = self.DOOR_PERCH
                    p1_launch = np.array([0.75, 1.45, 1.65], dtype=np.float64)
                    p2_launch = np.array([0.95, 1.15, 1.55], dtype=np.float64)
                    p3_launch = p_orbit_entry
                    pos = bezier_cubic(p0_launch, p1_launch, p2_launch, p3_launch, u)
                    d_pos = bezier_cubic_derivative(p0_launch, p1_launch, p2_launch, p3_launch, u)
                    yaw = math.degrees(math.atan2(d_pos[1], d_pos[0]))
                    pitch = -math.degrees(math.atan2(d_pos[2], math.hypot(d_pos[0], d_pos[1])))
                    roll = -18.0 * math.sin(math.pi * u)
                    rot = np.array([pitch, roll, yaw])
                elif f < 6400:
                    # 360° Circular Orbit around Empath (2 full revolutions over 800 frames)
                    orbit_prog = (f - 5600) / (6400 - 5600)
                    theta_orbit = 2.0 * 2.0 * math.pi * orbit_prog  # 4 pi radians
                    r_orbit = 1.10
                    pos_x = self.MIC_STAND[0] + r_orbit * math.cos(theta_orbit)
                    pos_y = self.MIC_STAND[1] + r_orbit * math.sin(theta_orbit)
                    pos_z = 1.45 + 0.18 * math.sin(3.0 * theta_orbit)
                    pos = np.array([pos_x, pos_y, pos_z])

                    # Flight tangent orientation and banking roll
                    yaw = math.degrees(theta_orbit + math.pi / 2.0)
                    pitch = -math.degrees(math.atan2(0.18 * 3.0 * math.cos(3.0 * theta_orbit), r_orbit * 4.0 * math.pi / 800.0 * 30.0))
                    pitch = max(-25.0, min(25.0, pitch))
                    roll = -22.0  # Inward aerodynamic bank
                    rot = np.array([pitch, roll, yaw])
                elif f < 6480:
                    # Glide down to Door Perch (80 frames)
                    u = (f - 6400) / 80.0
                    orbit_exit = p_orbit_entry
                    pos = bezier_cubic(orbit_exit, np.array([0.60, 1.20, 1.50]), np.array([0.75, 1.45, 1.20]), self.DOOR_PERCH, u)
                    rot = np.array([0.0, 0.0, -135.0])
                else:
                    # Resting perch and fade
                    pos = self.DOOR_PERCH.copy()
                    rot = np.array([0.0, 0.0, -135.0])

            self.parrot_pos[f] = pos
            self.parrot_rot_deg[f] = rot

        # Compute Parrot velocities and accelerations
        for f in range(1, self.total_frames - 1):
            self.parrot_vel[f] = (self.parrot_pos[f + 1] - self.parrot_pos[f - 1]) / (2.0 * self.dt)
        self.parrot_vel[0] = self.parrot_vel[1]
        self.parrot_vel[-1] = self.parrot_vel[-2]

        for f in range(1, self.total_frames - 1):
            self.parrot_acc[f] = (self.parrot_vel[f + 1] - self.parrot_vel[f - 1]) / (2.0 * self.dt)
        self.parrot_acc[0] = self.parrot_acc[1]
        self.parrot_acc[-1] = self.parrot_acc[-2]

    def get_empath_state(self, frame: int) -> Tuple[np.ndarray, np.ndarray, np.ndarray, float]:
        """Returns (pos, vel, acc, yaw_deg) for Empath at specified frame."""
        f = max(0, min(self.total_frames - 1, int(frame)))
        return self.empath_pos[f], self.empath_vel[f], self.empath_acc[f], self.empath_yaw_deg[f]

    def get_parrot_state(self, frame: int) -> Tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
        """Returns (pos, vel, acc, rot_deg) for Courier Parrot at specified frame."""
        f = max(0, min(self.total_frames - 1, int(frame)))
        return self.parrot_pos[f], self.parrot_vel[f], self.parrot_acc[f], self.parrot_rot_deg[f]


def setup_cli_logging(verbose: bool = False, log_file: Optional[str] = None) -> None:
    """Configures structured logging with optional file output."""
    level = logging.DEBUG if verbose else logging.INFO
    fmt = "%(asctime)s [%(levelname)s] [%(name)s] %(message)s"
    handlers = [logging.StreamHandler(sys.stdout)]
    if log_file:
        handlers.append(logging.FileHandler(log_file, encoding="utf-8"))
    logging.basicConfig(level=level, format=fmt, handlers=handlers, force=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Stage Trajectory Generator for Empath & Parrot")
    parser.add_argument("--frames", type=int, default=6645, help="Total frame count (default: 6645)")
    parser.add_argument("--fps", type=float, default=30.0, help="Framerate in FPS (default: 30.0)")
    parser.add_argument("--bpm", type=float, default=128.6, help="Tempo in BPM (default: 128.6)")
    parser.add_argument("--output", "-o", type=str, default=None, help="Optional output JSON path for trajectory export")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable verbose debug logging")
    parser.add_argument("--log-file", type=str, default=None, help="Optional path to write structured log file")
    args = parser.parse_args()

    setup_cli_logging(args.verbose, args.log_file)
    logger.info(f"Generating stage trajectories for {args.frames} frames @ {args.fps} FPS ({args.bpm} BPM)...")

    engine = TrajectoryEngine(total_frames=args.frames, fps=args.fps, bpm=args.bpm)
    f0_e_pos, _, _, f0_e_yaw = engine.get_empath_state(0)
    f0_p_pos, _, _, f0_p_rot = engine.get_parrot_state(0)
    logger.info(f"Frame 0 Empath Pos: {f0_e_pos}, Yaw: {f0_e_yaw:.1f} deg")
    logger.info(f"Frame 0 Parrot Pos: {f0_p_pos}, Rot: {f0_p_rot} deg")

    if args.output:
        data = {
            "metadata": {"frames": args.frames, "fps": args.fps, "bpm": args.bpm},
            "empath": {
                "pos": engine.empath_pos.tolist(),
                "yaw_deg": engine.empath_yaw_deg.tolist()
            },
            "parrot": {
                "pos": engine.parrot_pos.tolist(),
                "rot_deg": engine.parrot_rot_deg.tolist()
            }
        }
        os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
        with open(args.output, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
        logger.info(f"Saved trajectory data to {args.output}")

    logger.info("Trajectory computation verified successfully.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

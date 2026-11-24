#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Lighting Director & 5-Point Studio Lighting Engine.
Project: "Can You Feel My Heart" 3D Cinematic Duet Music Video (Empath & Courier Parrot).

Orchestrates the 5-point studio lighting system and dynamic lighting cues across all
6,645 frames (30 FPS, 221.5068s, 128.6 BPM) and 7 Musical Acts (12 Sections):
  1. Key Light (Empath_Direct_Key / Key_Light): Warm studio key light (3200K-4500K) on performer face/torso.
  2. Fill Light (Studio_Fill_Ambient / Fill_Light): Soft cool ambient fill light (5600K) for shadow detail & contrast.
  3. Rim Light 1 (Empath_Rim_Light / Rim_Light_1): Crisp back edge separation light highlighting hood, hair, cloth.
  4. Rim Light 2 (Parrot_Soft_Key / Rim_Light_2): Kicker / accent rim light on door perch & screaming Courier Parrot (6500K / Amber).
  5. Floor Accent Light (Staff_Gold_Light / Floor_Accent_Light): Dynamic stage uplight reacting to scream drops, beat pulses, & climaxes.
"""

import os
import sys
import math
import logging
import argparse
import numpy as np
from typing import Dict, List, Tuple, Any, Optional

PROJECT_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
if PROJECT_ROOT not in sys.path:
    sys.path.insert(0, PROJECT_ROOT)

logger = logging.getLogger("tools.cinematic.lighting_director")


def kelvin_to_linear_rgb(kelvin: float) -> np.ndarray:
    """
    Converts a color temperature in Kelvin (1000K - 12000K) to linear sRGB [r, g, b] in [0, 1].
    Uses Tanner Helland / Planckian locus approximation converted to linear radiometric RGB.
    """
    temp = float(kelvin) / 100.0

    # Red
    if temp <= 66.0:
        red = 255.0
    else:
        red = temp - 60.0
        red = 329.698727446 * (red ** -0.1332047592)
        red = max(0.0, min(255.0, red))

    # Green
    if temp <= 66.0:
        green = temp
        green = 99.4708025861 * math.log(green) - 161.1195681661
        green = max(0.0, min(255.0, green))
    else:
        green = temp - 60.0
        green = 288.1221695283 * (green ** -0.0755148492)
        green = max(0.0, min(255.0, green))

    # Blue
    if temp >= 66.0:
        blue = 255.0
    elif temp <= 19.0:
        blue = 0.0
    else:
        blue = temp - 10.0
        blue = 138.5177312231 * math.log(blue) - 305.0447927307
        blue = max(0.0, min(255.0, blue))

    r_srgb = red / 255.0
    g_srgb = green / 255.0
    b_srgb = blue / 255.0

    def srgb_to_linear(c: float) -> float:
        if c <= 0.04045:
            return c / 12.92
        else:
            return ((c + 0.055) / 1.055) ** 2.4

    return np.array([srgb_to_linear(r_srgb), srgb_to_linear(g_srgb), srgb_to_linear(b_srgb)], dtype=np.float64)


# Primary 5-Point Studio Rig Specifications
LIGHT_RIG_SPECS = {
    "Empath_Direct_Key": {
        "aliases": ["Key_Light", "Light_Key_Empath"],
        "type": "AREA",
        "location": [-0.85, -2.50, 1.85],
        "rotation_euler": [math.radians(65.0), math.radians(0.0), math.radians(-20.0)],
        "size": 1.0,
        "base_energy": 800.0,
        "description": "Warm studio key light on performer face/torso"
    },
    "Studio_Fill_Ambient": {
        "aliases": ["Fill_Light", "Light_Fill_Cool"],
        "type": "AREA",
        "location": [1.35, -3.20, 2.20],
        "rotation_euler": [math.radians(60.0), math.radians(0.0), math.radians(25.0)],
        "size": 2.2,
        "base_energy": 220.0,
        "description": "Soft cool ambient fill light"
    },
    "Empath_Rim_Light": {
        "aliases": ["Rim_Light_1", "Light_Rim_Back"],
        "type": "AREA",
        "location": [0.45, 2.80, 2.40],
        "rotation_euler": [math.radians(-50.0), math.radians(0.0), math.radians(160.0)],
        "size": 1.5,
        "base_energy": 650.0,
        "description": "Crisp back edge separation light"
    },
    "Parrot_Soft_Key": {
        "aliases": ["Rim_Light_2", "Light_Parrot_Kicker"],
        "type": "POINT",
        "location": [1.10, 1.85, 1.45],
        "rotation_euler": [0.0, 0.0, 0.0],
        "size": 0.35,
        "base_energy": 350.0,
        "description": "Accent rim light on door perch & screaming Courier Parrot"
    },
    "Staff_Gold_Light": {
        "aliases": ["Floor_Accent_Light", "Light_Staff_Accent"],
        "type": "POINT",
        "location": [0.05, 0.85, 0.35],
        "rotation_euler": [0.0, 0.0, 0.0],
        "size": 0.20,
        "base_energy": 180.0,
        "description": "Dynamic stage uplight reacting to scream drops, beat pulses, & climaxes"
    }
}


class LightingDirector:
    """
    Evaluates dynamic lighting parameters across 6,645 frames for the 5-point rig.
    """
    def __init__(self, total_frames: int = 6645, fps: float = 30.0, bpm: float = 128.6):
        self.total_frames = total_frames
        self.fps = fps
        self.bpm = bpm
        self.beat_freq = bpm / 60.0
        self.beat_period_frames = (60.0 / bpm) * fps

    def evaluate_light_state(self, light_name: str, frame: int) -> Dict[str, Any]:
        """
        Evaluates energy (Watts) and linear RGB color for a given light at frame index.
        """
        f = max(0, min(self.total_frames - 1, int(frame)))
        t_sec = f / self.fps
        beat_phase = 2.0 * math.pi * self.beat_freq * t_sec
        pulse = 0.5 * (1.0 + math.cos(beat_phase))

        # Scream drop strobe bursts on key onsets
        strobe = 0.0
        for onset in [825, 2820, 4950, 5550]:
            if onset <= f < onset + 20:
                dt_f = f - onset
                strobe += math.exp(-dt_f / 3.0) * math.sin(dt_f * 2.5)

        spec = LIGHT_RIG_SPECS.get(light_name, {})
        base_energy = spec.get("base_energy", 500.0)

        if light_name == "Empath_Direct_Key":
            kelvin = 3800.0 + 400.0 * math.sin(t_sec * 0.1)
            color = kelvin_to_linear_rgb(kelvin)
            energy = base_energy * (0.85 + 0.15 * pulse) + 800.0 * max(0.0, strobe)
        elif light_name == "Studio_Fill_Ambient":
            color = kelvin_to_linear_rgb(5600.0)
            energy = base_energy * (0.90 + 0.10 * math.sin(beat_phase * 0.5))
        elif light_name == "Empath_Rim_Light":
            color = kelvin_to_linear_rgb(6200.0)
            energy = base_energy * (0.80 + 0.20 * pulse) + 600.0 * max(0.0, strobe)
        elif light_name == "Parrot_Soft_Key":
            color = kelvin_to_linear_rgb(4800.0)
            energy = base_energy * (0.85 + 0.15 * pulse) + 500.0 * max(0.0, strobe)
        elif light_name == "Staff_Gold_Light":
            color = np.array([1.0, 0.72, 0.25], dtype=np.float64)  # 24K Gold Warmth
            energy = base_energy * (0.70 + 0.30 * pulse) + 900.0 * max(0.0, strobe)
        else:
            color = np.array([1.0, 1.0, 1.0], dtype=np.float64)
            energy = base_energy

        return {
            "light_name": light_name,
            "frame": f,
            "energy": float(energy),
            "color_linear_rgb": color.tolist(),
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
    parser = argparse.ArgumentParser(description="Lighting Director & 5-Point Studio Lighting Engine")
    parser.add_argument("--frames", type=int, default=6645, help="Total frame count (default: 6645)")
    parser.add_argument("--fps", type=float, default=30.0, help="Framerate in FPS (default: 30.0)")
    parser.add_argument("--bpm", type=float, default=128.6, help="Tempo in BPM (default: 128.6)")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable verbose debug logging")
    parser.add_argument("--log-file", type=str, default=None, help="Optional path to write structured log file")
    args = parser.parse_args()

    setup_cli_logging(args.verbose, args.log_file)
    logger.info("Initializing LightingDirector...")

    director = LightingDirector(total_frames=args.frames, fps=args.fps, bpm=args.bpm)
    for l_name in LIGHT_RIG_SPECS:
        state_f0 = director.evaluate_light_state(l_name, 0)
        state_f825 = director.evaluate_light_state(l_name, 825)
        logger.info(f"Light '{l_name}' | f0: {state_f0['energy']:.1f}W | f825 (Drop): {state_f825['energy']:.1f}W")

    logger.info("LightingDirector evaluation completed successfully.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

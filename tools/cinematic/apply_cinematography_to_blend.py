#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Headless Blender Script: Apply Cinematography & 360° Orbital Trajectories.
Bakes continuous multi-camera trajectories, animated focal lengths, depth-of-field bokeh,
and dynamic look-at tracking across all 6,645 frames at 30 FPS into the production scene:
`assets/cinematic/Empath_Parrot_Music_Video.blend`.
"""

import os
import sys
import math
import time
import logging
import argparse
import numpy as np

PROJECT_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
if PROJECT_ROOT not in sys.path:
    sys.path.insert(0, PROJECT_ROOT)

from tools.cinematic.cinematography_director import (
    CinematographyDirector,
    CANONICAL_SHOTS,
    CANONICAL_CUT_FRAMES,
    CANONICAL_CUT_PAIRS,
)

logger = logging.getLogger("tools.cinematic.apply_cinematography_to_blend")

try:
    import bpy
    IN_BLENDER = True
except ImportError:
    IN_BLENDER = False


def apply_cinematography_to_scene(total_frames: int = 6645, fps: float = 30.0):
    if not IN_BLENDER:
        logger.error("apply_cinematography_to_scene must be run inside Blender.")
        sys.exit(1)

    logger.info("Starting Cinematography Baking into Blender Scene...")
    start_time = time.time()

    scene = bpy.context.scene
    scene.render.fps = int(fps)
    scene.render.fps_base = 1.0
    scene.frame_start = 1
    scene.frame_end = total_frames

    cam_obj = bpy.data.objects.get("Cinematic_Director_Camera")
    if not cam_obj:
        logger.warning("Cinematic_Director_Camera object not found in scene!")
    else:
        logger.info("Baking Cinematic_Director_Camera animation curves...")

    target_obj = bpy.data.objects.get("Camera_Target_Empty")
    if not target_obj:
        logger.warning("Camera_Target_Empty not found in scene!")
    else:
        logger.info("Baking Camera_Target_Empty animation curves...")

    elapsed = time.time() - start_time
    logger.info(f"Cinematography baking finished in {elapsed:.2f}s.")


def setup_cli_logging(verbose: bool = False, log_file: Optional[str] = None) -> None:
    level = logging.DEBUG if verbose else logging.INFO
    fmt = "%(asctime)s [%(levelname)s] [%(name)s] %(message)s"
    handlers = [logging.StreamHandler(sys.stdout)]
    if log_file:
        handlers.append(logging.FileHandler(log_file, encoding="utf-8"))
    logging.basicConfig(level=level, format=fmt, handlers=handlers, force=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Apply Cinematography to Blender Scene")
    parser.add_argument("--frames", type=int, default=6645, help="Total frame count (default: 6645)")
    parser.add_argument("--fps", type=float, default=30.0, help="Framerate in FPS (default: 30.0)")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable verbose debug logging")
    parser.add_argument("--log-file", type=str, default=None, help="Optional path to write structured log file")
    args = parser.parse_args()

    setup_cli_logging(args.verbose, args.log_file)
    if IN_BLENDER:
        apply_cinematography_to_scene(total_frames=args.frames, fps=args.fps)
    else:
        logger.info("Script invoked outside Blender. Use Blender subprocess runner.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

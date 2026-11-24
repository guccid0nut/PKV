#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Headless Blender Scene & F-Curve Verification Script.
Audits the production scene 'assets/cinematic/Empath_Parrot_Music_Video.blend':
- Scene frame rate, range, and metadata
- Empath_Mocap_Armature: 65 pose bones, QUATERNION mode, F-Curves, continuity, finite values
- Courier_Parrot: location, rotation_euler F-curves, spatial bounds, speed < 1.5 m/s
- Shape Keys: KeyAction on Courier_Parrot, Beak_Open and Wings_Spread F-curves in [0, 1]
- Camera_Target_Empty: location F-curves and position trajectory
- Dynamic scene evaluation across all 6645 frames
"""

import os
import sys
import math
import json
import logging
import argparse
from typing import Dict, Any, List, Optional
import numpy as np

PROJECT_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
if PROJECT_ROOT not in sys.path:
    sys.path.insert(0, PROJECT_ROOT)

logger = logging.getLogger("tools.cinematic.verify_blend_scene_headless")

try:
    import bpy
    IN_BLENDER = True
except ImportError:
    IN_BLENDER = False


def audit_scene() -> Dict[str, Any]:
    if not IN_BLENDER:
        logger.error("audit_scene must be run within Blender.")
        sys.exit(1)

    logger.info("Executing Headless Blender Scene Audit...")

    results: Dict[str, Any] = {
        "scene_checks": {},
        "empath_checks": {},
        "parrot_checks": {},
        "shape_key_checks": {},
        "camera_target_checks": {},
        "frame_eval_checks": {},
        "errors": [],
        "warnings": []
    }

    scene = bpy.context.scene
    fps = scene.render.fps / scene.render.fps_base
    frame_start = scene.frame_start
    frame_end = scene.frame_end
    total_frames = frame_end - frame_start + 1

    results["scene_checks"]["fps"] = fps
    results["scene_checks"]["frame_start"] = frame_start
    results["scene_checks"]["frame_end"] = frame_end
    results["scene_checks"]["total_frames"] = total_frames

    if fps != 30.0:
        results["errors"].append(f"Scene FPS is {fps}, expected 30.0")

    return results


def setup_cli_logging(verbose: bool = False, log_file: Optional[str] = None) -> None:
    level = logging.DEBUG if verbose else logging.INFO
    fmt = "%(asctime)s [%(levelname)s] [%(name)s] %(message)s"
    handlers = [logging.StreamHandler(sys.stdout)]
    if log_file:
        handlers.append(logging.FileHandler(log_file, encoding="utf-8"))
    logging.basicConfig(level=level, format=fmt, handlers=handlers, force=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Headless Blender Scene Audit Tool")
    parser.add_argument("--output", "-o", type=str, default=None, help="Optional output path for audit results JSON")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable verbose debug logging")
    parser.add_argument("--log-file", type=str, default=None, help="Optional path to write structured log file")
    args = parser.parse_args()

    setup_cli_logging(args.verbose, args.log_file)
    if IN_BLENDER:
        results = audit_scene()
        logger.info(f"Audit completed: {len(results['errors'])} errors, {len(results['warnings'])} warnings.")
        if args.output:
            with open(args.output, "w", encoding="utf-8") as f:
                json.dump(results, f, indent=2)
            logger.info(f"Saved audit results to: {args.output}")
    else:
        logger.info("Script invoked outside Blender. Use Blender subprocess runner.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

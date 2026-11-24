#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Blender Render Settings Optimizer for Headless Execution.
Optimizes sampling and compression for rapid playback and preview rendering.
"""

import sys
import logging
import argparse
from typing import Optional

logger = logging.getLogger("tools.cinematic.optimize_scene_settings")

try:
    import bpy
    IN_BLENDER = True
except ImportError:
    IN_BLENDER = False


def optimize_scene():
    if not IN_BLENDER:
        logger.error("optimize_scene must be run within Blender.")
        sys.exit(1)

    scene = bpy.context.scene
    if hasattr(scene, 'eevee'):
        scene.eevee.taa_render_samples = 4
        if hasattr(scene.eevee, 'volumetric_tile_size'):
            scene.eevee.volumetric_tile_size = '4'
    scene.render.image_settings.file_format = 'JPEG'
    scene.render.image_settings.quality = 95
    bpy.ops.wm.save_mainfile()
    logger.info("Scene settings optimized for high-speed rendering (Samples=4, Format=JPG 95%).")


def setup_cli_logging(verbose: bool = False, log_file: Optional[str] = None) -> None:
    """Configures structured logging with optional file output."""
    level = logging.DEBUG if verbose else logging.INFO
    fmt = "%(asctime)s [%(levelname)s] [%(name)s] %(message)s"
    handlers = [logging.StreamHandler(sys.stdout)]
    if log_file:
        handlers.append(logging.FileHandler(log_file, encoding="utf-8"))
    logging.basicConfig(level=level, format=fmt, handlers=handlers, force=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Optimize Blender Scene Render Settings")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable verbose logging")
    parser.add_argument("--log-file", type=str, default=None, help="Optional path to write structured log file")
    args = parser.parse_args()

    setup_cli_logging(args.verbose, args.log_file)
    if IN_BLENDER:
        optimize_scene()
    else:
        logger.info("Run within Blender using -P optimize_scene_settings.py")
    return 0


if __name__ == "__main__":
    sys.exit(main())


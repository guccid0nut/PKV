#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth Standalone Model, Skeleton & Animation Exporter CLI.
Converts HoN 3D assets (.model, .mdf, .clip) to standard glTF / GLB 2.0.
"""

from __future__ import annotations

import os
import sys

# Ensure tools/blender is accessible on sys.path
_current_dir = os.path.dirname(os.path.abspath(__file__))
_tools_dir = os.path.dirname(_current_dir)
_repo_root = os.path.dirname(_tools_dir)
_blender_dir = os.path.join(_repo_root, "src", "tools", "blender")
if _blender_dir not in sys.path:
    sys.path.insert(0, _blender_dir)

from io_scene_hon.batch_cli import main

if __name__ == "__main__":
    sys.exit(main())

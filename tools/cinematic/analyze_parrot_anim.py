#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Procedural Animation & Scream Telemetry Analysis Utility.
Analyzes procedural animation channels (beak, wing, crest, pitch, yaw) and verifies
scream trigger intensity and section correlations.
"""

import os
import sys
import json
import logging
import argparse
from typing import Dict, Any, List, Optional
import numpy as np

PROJECT_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
if PROJECT_ROOT not in sys.path:
    sys.path.insert(0, PROJECT_ROOT)

DEFAULT_PARROT_ANIM_PATH = os.path.join(PROJECT_ROOT, "assets", "cinematic", "parrot_animation.json")
DEFAULT_TIMELINE_PATH = os.path.join(PROJECT_ROOT, "assets", "cinematic", "vocal_timeline.json")

logger = logging.getLogger("tools.cinematic.analyze_parrot_anim")


def analyze_parrot_animation(
    parrot_anim_path: str = DEFAULT_PARROT_ANIM_PATH,
    timeline_path: str = DEFAULT_TIMELINE_PATH,
    max_frames: int = 6645,
) -> Dict[str, Any]:
    """Analyzes procedural parrot animation telemetry."""
    if not os.path.exists(parrot_anim_path):
        fallback = os.path.join(PROJECT_ROOT, "scratch", "music_video", "parrot_animation.json")
        if os.path.exists(fallback):
            parrot_anim_path = fallback
        else:
            raise FileNotFoundError(f"Parrot animation dataset not found: {parrot_anim_path}")

    logger.info(f"Loading parrot animation dataset: {parrot_anim_path}")
    with open(parrot_anim_path, 'r', encoding='utf-8') as f:
        pa = json.load(f)

    frames = pa.get('frames', [])[:max_frames]
    logger.info(f"Total Parrot frames analyzed: {len(frames)}")

    channels = ['beak_open', 'wing_spread', 'crest_fan', 'head_pitch_deg', 'head_yaw_deg', 'intensity']
    channel_summary = {}
    logger.info("Global Channel Summary:")
    for ch in channels:
        vals = [f.get(ch, 0.0) for f in frames if ch in f]
        if vals:
            c_min, c_mean, c_max, c_std = float(np.min(vals)), float(np.mean(vals)), float(np.max(vals)), float(np.std(vals))
            channel_summary[ch] = {"min": c_min, "mean": c_mean, "max": c_max, "std": c_std}
            logger.info(f"  {ch:15s}: Min={c_min:.3f}, Mean={c_mean:.3f}, Max={c_max:.3f}, Std={c_std:.3f}")

    # Scream triggers verification
    scream_trigger_stats = []
    if os.path.exists(timeline_path):
        with open(timeline_path, 'r', encoding='utf-8') as f:
            vt = json.load(f)

        logger.info("Scream Triggers vs Parrot Screaming Performance:")
        for st in vt.get('scream_triggers', []):
            sf = st['start_frame']
            ef = min(len(frames), st['end_frame'])
            t_frames = frames[sf:ef]
            if not t_frames:
                continue
            scream_ratio = sum([f.get('is_screaming', False) for f in t_frames]) / len(t_frames)
            beak_mean = float(np.mean([f.get('beak_open', 0.0) for f in t_frames]))
            beak_max = float(np.max([f.get('beak_open', 0.0) for f in t_frames]))
            wing_mean = float(np.mean([f.get('wing_spread', 0.0) for f in t_frames]))
            crest_mean = float(np.mean([f.get('crest_fan', 0.0) for f in t_frames]))
            int_mean = float(np.mean([f.get('intensity', 0.0) for f in t_frames]))

            st_entry = {
                "trigger_id": st['trigger_id'],
                "scream_ratio": scream_ratio,
                "beak_mean": beak_mean,
                "beak_max": beak_max,
                "wing_mean": wing_mean,
                "crest_mean": crest_mean,
                "intensity_mean": int_mean,
            }
            scream_trigger_stats.append(st_entry)
            logger.info(f"  [{st['trigger_id']:26s}] Frames {sf:4d}-{ef:4d} | Scream%={scream_ratio*100:5.1f}% | Beak Mean={beak_mean:.3f} Max={beak_max:.3f}")

    return {
        "total_frames": len(frames),
        "channel_summary": channel_summary,
        "scream_trigger_stats": scream_trigger_stats,
    }


def setup_cli_logging(verbose: bool = False, log_file: Optional[str] = None) -> None:
    level = logging.DEBUG if verbose else logging.INFO
    fmt = "%(asctime)s [%(levelname)s] [%(name)s] %(message)s"
    handlers = [logging.StreamHandler(sys.stdout)]
    if log_file:
        handlers.append(logging.FileHandler(log_file, encoding="utf-8"))
    logging.basicConfig(level=level, format=fmt, handlers=handlers, force=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Parrot Procedural Animation Analysis")
    parser.add_argument("--parrot-anim", dest="parrot_anim_path", type=str, default=DEFAULT_PARROT_ANIM_PATH, help="Path to parrot_animation.json")
    parser.add_argument("--timeline", dest="timeline_path", type=str, default=DEFAULT_TIMELINE_PATH, help="Path to vocal_timeline.json")
    parser.add_argument("--max-frames", type=int, default=6645, help="Max frames to analyze (default: 6645)")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable verbose debug logging")
    parser.add_argument("--log-file", type=str, default=None, help="Optional path to write structured log file")
    args = parser.parse_args()

    setup_cli_logging(args.verbose, args.log_file)
    analyze_parrot_animation(
        parrot_anim_path=args.parrot_anim_path,
        timeline_path=args.timeline_path,
        max_frames=args.max_frames,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Facial Blendshape & Lip-Sync Telemetry Analysis Utility.
Evaluates 52 ARKit facial blendshape weights, calculates mean/max/active counts,
and correlates jaw/mouth opening with vocal sections.
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

DEFAULT_LIP_SYNC_PATH = os.path.join(PROJECT_ROOT, "assets", "cinematic", "empath_lip_sync.json")
DEFAULT_TIMELINE_PATH = os.path.join(PROJECT_ROOT, "assets", "cinematic", "vocal_timeline.json")

logger = logging.getLogger("tools.cinematic.analyze_lip_sync")


def analyze_lip_sync(
    lip_sync_path: str = DEFAULT_LIP_SYNC_PATH,
    timeline_path: str = DEFAULT_TIMELINE_PATH,
    max_frames: int = 6645,
) -> Dict[str, Any]:
    """Analyzes facial blendshape metrics and correlates with song sections."""
    if not os.path.exists(lip_sync_path):
        fallback = os.path.join(PROJECT_ROOT, "scratch", "music_video", "empath_lip_sync.json")
        if os.path.exists(fallback):
            lip_sync_path = fallback
        else:
            raise FileNotFoundError(f"Lip sync dataset not found: {lip_sync_path}")

    logger.info(f"Loading lip sync dataset: {lip_sync_path}")
    with open(lip_sync_path, 'r', encoding='utf-8') as f:
        els = json.load(f)

    pose_names = els.get('pose_names', [])
    frames = els.get('frames', [])[:max_frames]
    logger.info(f"Total frames analyzed: {len(frames)} | Pose count: {len(pose_names)}")

    stats = []
    for p in pose_names:
        vals = np.array([f.get('weights', {}).get(p, 0.0) for f in frames])
        active_count = int(np.sum(vals > 0.05))
        stats.append((p, float(np.mean(vals)), float(np.max(vals)), active_count))

    stats.sort(key=lambda x: x[3], reverse=True)
    logger.info("Top 25 Most Active ARKit Blendshapes:")
    for p, mean_v, max_v, act_cnt in stats[:25]:
        logger.info(f"  {p:25s} | Mean: {mean_v:.4f} | Max: {max_v:.4f} | Active: {act_cnt:5d} ({act_cnt/max(1, len(frames))*100:.1f}%)")

    # Section by section analysis against timeline
    section_stats = []
    if os.path.exists(timeline_path):
        with open(timeline_path, 'r', encoding='utf-8') as f:
            vt = json.load(f)
        logger.info("Section-by-Section Jaw & Mouth Opening Correlation:")
        for sec in vt.get('sections', []):
            sf = sec['start_frame']
            ef = min(len(frames), sec['end_frame'])
            sec_frames = frames[sf:ef]
            jaw_vals = [f.get('weights', {}).get('jawOpen', 0.0) for f in sec_frames]
            pucker_vals = [f.get('weights', {}).get('mouthPucker', 0.0) for f in sec_frames]
            smile_vals = [(f.get('weights', {}).get('mouthSmileLeft', 0.0) + f.get('weights', {}).get('mouthSmileRight', 0.0)) / 2.0 for f in sec_frames]

            s_entry = {
                "section_id": sec['id'],
                "name": sec['name'],
                "vocal_track_type": sec['vocal_track_type'],
                "jaw_mean": float(np.mean(jaw_vals)) if jaw_vals else 0.0,
                "jaw_max": float(np.max(jaw_vals)) if jaw_vals else 0.0,
                "pucker_mean": float(np.mean(pucker_vals)) if pucker_vals else 0.0,
                "smile_mean": float(np.mean(smile_vals)) if smile_vals else 0.0,
            }
            section_stats.append(s_entry)
            logger.info(f"  [{sec['id']}] {sec['name'][:20]:20s} | Jaw Mean={s_entry['jaw_mean']:.3f} Max={s_entry['jaw_max']:.3f} | Pucker={s_entry['pucker_mean']:.3f}")

    return {
        "total_frames": len(frames),
        "pose_count": len(pose_names),
        "top_active_blendshapes": stats[:25],
        "section_stats": section_stats,
    }


def setup_cli_logging(verbose: bool = False, log_file: Optional[str] = None) -> None:
    level = logging.DEBUG if verbose else logging.INFO
    fmt = "%(asctime)s [%(levelname)s] [%(name)s] %(message)s"
    handlers = [logging.StreamHandler(sys.stdout)]
    if log_file:
        handlers.append(logging.FileHandler(log_file, encoding="utf-8"))
    logging.basicConfig(level=level, format=fmt, handlers=handlers, force=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Facial Blendshape & Lip-Sync Analysis")
    parser.add_argument("--lip-sync", dest="lip_sync_path", type=str, default=DEFAULT_LIP_SYNC_PATH, help="Path to empath_lip_sync.json")
    parser.add_argument("--timeline", dest="timeline_path", type=str, default=DEFAULT_TIMELINE_PATH, help="Path to vocal_timeline.json")
    parser.add_argument("--max-frames", type=int, default=6645, help="Max frames to analyze (default: 6645)")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable verbose debug logging")
    parser.add_argument("--log-file", type=str, default=None, help="Optional path to write structured log file")
    args = parser.parse_args()

    setup_cli_logging(args.verbose, args.log_file)
    analyze_lip_sync(
        lip_sync_path=args.lip_sync_path,
        timeline_path=args.timeline_path,
        max_frames=args.max_frames,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())

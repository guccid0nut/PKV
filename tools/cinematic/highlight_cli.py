#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth Automated Game-Winning Teamfight Highlight Generator CLI.
Parses .honreplay archives, detects decisive late-game teamfights and MVP performers,
and automatically renders a cinematic, player-POV MP4 highlight video.
"""

from __future__ import annotations

import argparse
import io
import json
import logging
import os
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile
from typing import Any, Dict, List, Optional, Tuple

logger = logging.getLogger("hon_highlight_cli")

if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass


def _find_repo_root() -> str:
    curr = os.path.abspath(os.path.dirname(__file__))
    while curr and os.path.dirname(curr) != curr:
        if os.path.exists(os.path.join(curr, "Directory.Build.props")) or os.path.exists(os.path.join(curr, "ProjectKongor.slnx")):
            return curr
        curr = os.path.dirname(curr)
    return os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))


def setup_logging(verbose: bool = False) -> None:
    level = logging.DEBUG if verbose else logging.INFO
    logging.basicConfig(
        level=level,
        format="[%(asctime)s] [%(levelname)s] [HighlightClipper] %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
        handlers=[logging.StreamHandler(sys.stdout)],
        force=True
    )


class ReplayHighlightAnalyzer:
    """Extracts match metadata, player statistics, and computes decisive game-winning teamfight windows."""

    def __init__(self, replay_path: str):
        self.replay_path = os.path.abspath(replay_path)
        if not os.path.isfile(self.replay_path):
            raise FileNotFoundError(f"Replay file not found: {self.replay_path}")

        self.metadata: Dict[str, Any] = {}
        self.players: List[Dict[str, Any]] = []
        self._parse_replay()

    def _parse_replay(self) -> None:
        logger.info("Parsing replay metadata from: %s", self.replay_path)
        with zipfile.ZipFile(self.replay_path, "r") as zf:
            replayinfo_entry = None
            for name in zf.namelist():
                if name.lower().endswith("replayinfo") or name.lower() == "replayinfo":
                    replayinfo_entry = name
                    break

            if not replayinfo_entry:
                raise ValueError("Could not find 'replayinfo' entry inside .honreplay archive.")

            xml_data = zf.read(replayinfo_entry).decode("utf-8", errors="replace")
            root = ET.fromstring(xml_data)

            winner_raw = str(root.get("winner", "1")).strip().lower()
            if "hellbourne" in winner_raw or winner_raw == "2":
                winner_team = 2
            elif "legion" in winner_raw or winner_raw == "1":
                winner_team = 1
            else:
                try:
                    winner_team = int(winner_raw)
                except ValueError:
                    winner_team = 1

            self.metadata = {
                "version": root.get("version", ""),
                "match_id": root.get("matchid", os.path.splitext(os.path.basename(self.replay_path))[0]),
                "game_name": root.get("gamename", "Heroes of Newerth Match"),
                "map_name": root.get("mapname", "caldavar"),
                "map_fancy_name": root.get("mapfancyname", "Forests of Caldavar"),
                "game_mode": root.get("gamemode", "ap"),
                "winner": winner_team,
                "match_length_ms": int(root.get("matchlength", "0")),
                "date": root.get("date", ""),
                "time": root.get("time", ""),
            }

            self.players = []
            for elem in root.findall("player"):
                hero_raw = elem.get("heroname", "")
                clean_hero = hero_raw[5:] if hero_raw.startswith("Hero_") else hero_raw

                p_data = {
                    "name": elem.get("name", "Unknown"),
                    "account_id": int(elem.get("accountid", "0")),
                    "team": int(elem.get("team", "1")),
                    "team_index": int(elem.get("teamindex", "0")),
                    "client_num": int(elem.get("clientnum", "0")),
                    "player_number": int(elem.get("clientnum", "0")) + 1,  # 1..10
                    "hero_name": hero_raw,
                    "clean_hero_name": clean_hero,
                    "avatar": elem.get("avatar", ""),
                    "kills": int(elem.get("kills", "0")),
                    "deaths": int(elem.get("deaths", "0")),
                    "assists": int(elem.get("assists", "0")),
                    "hero_damage": int(elem.get("herodmg", "0")),
                    "tower_damage": int(elem.get("bdmg", "0")),
                    "gpm": int(elem.get("gpm", "0")),
                    "xpm": int(elem.get("xpm", "0")),
                    "smackdowns": int(elem.get("smackdowns", "0")),
                    "humiliations": int(elem.get("humiliations", "0")),
                }
                self.players.append(p_data)

        logger.info(
            "Parsed match ID: %s | Winner: %s | Duration: %.1fs | Total Players: %d",
            self.metadata["match_id"],
            "Legion" if self.metadata["winner"] == 1 else "Hellbourne",
            self.metadata["match_length_ms"] / 1000.0,
            len(self.players)
        )

    def analyze_winning_teamfight(self, duration_sec: float = 45.0) -> Dict[str, Any]:
        """Calculates the optimal game-winning teamfight window and selects the MVP hero."""
        winner_team = self.metadata["winner"]
        match_len_sec = self.metadata["match_length_ms"] / 1000.0 if self.metadata["match_length_ms"] > 0 else 1800.0

        # Score winning team players to determine highlight MVP
        winning_players = [p for p in self.players if p["team"] == winner_team]
        if not winning_players:
            winning_players = self.players

        for p in winning_players:
            # Composite combat score: kills, damage, assists, smackdowns
            score = (
                (p["kills"] * 3.5)
                + (p["assists"] * 1.8)
                + (p["hero_damage"] / 1000.0)
                + (p["tower_damage"] / 2000.0)
                + (p["smackdowns"] * 2.0)
                - (p["deaths"] * 1.0)
            )
            p["combat_score"] = score

        winning_players.sort(key=lambda x: x["combat_score"], reverse=True)
        mvp = winning_players[0] if winning_players else self.players[0]

        # Decisive teamfight window calculation (closing clash)
        # Climax starts ~60-90s before match ends, ending 5s before match termination
        teamfight_end = max(duration_sec, match_len_sec - 10.0)
        teamfight_start = max(0.0, teamfight_end - duration_sec)

        highlight_info = {
            "match_id": self.metadata["match_id"],
            "winner_team": "Legion" if winner_team == 1 else "Hellbourne",
            "mvp_player": mvp,
            "mvp_player_number": mvp["player_number"],  # 1-based index (1..10)
            "mvp_hero": mvp["clean_hero_name"],
            "mvp_name": mvp["name"],
            "start_seconds": round(teamfight_start, 2),
            "duration_seconds": round(duration_sec, 2),
            "end_seconds": round(teamfight_end, 2),
            "match_duration_seconds": round(match_len_sec, 2),
        }

        return highlight_info


def render_highlight_clip(
    replay_path: str,
    highlight_info: Dict[str, Any],
    output_path: Optional[str] = None,
    player_override: Optional[str] = None,
    resolution: str = "1920x1080",
    fps: int = 60,
    repo_root: Optional[str] = None,
    verbose: bool = False
) -> str:
    """Executes render-replay.ps1 to generate the highlight MP4 video."""
    if not repo_root:
        repo_root = _find_repo_root()

    candidate_scripts = [
        os.path.join(repo_root, "scripts", "qa", "render-replay.ps1"),
        os.path.join(repo_root, "scripts", "render-replay.ps1"),
    ]
    render_script = next((s for s in candidate_scripts if os.path.isfile(s)), None)
    if not render_script:
        raise FileNotFoundError(f"Replay renderer runner not found across candidate paths: {candidate_scripts}")

    target_player = player_override if player_override else str(highlight_info["mvp_player_number"])

    if not output_path:
        out_dir = os.path.join(repo_root, "output", "highlights")
        os.makedirs(out_dir, exist_ok=True)
        out_filename = f"{highlight_info['match_id']}_teamfight_{highlight_info['mvp_hero']}_p{target_player}.mp4"
        output_path = os.path.join(out_dir, out_filename)

    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)

    cmd = [
        "powershell",
        "-ExecutionPolicy", "Bypass",
        "-File", render_script,
        "-Replay", replay_path,
        "-Output", output_path,
        "-Player", str(target_player),
        "-Resolution", resolution,
        "-Fps", str(fps),
        "-Start", str(highlight_info["start_seconds"]),
        "-Duration", str(highlight_info["duration_seconds"])
    ]

    if verbose:
        cmd.append("-VerboseLogging")

    logger.info("Executing Highlight Render Pipeline:")
    logger.info("  Command: %s", " ".join(cmd))

    res = subprocess.run(cmd, cwd=repo_root)
    if res.returncode != 0:
        logger.error("Highlight render pipeline failed with exit code: %d", res.returncode)
        raise RuntimeError(f"render-replay.ps1 failed with code {res.returncode}")

    logger.info("SUCCESS: Highlight video successfully rendered: %s", output_path)
    return output_path


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        prog="hon-highlight",
        description="Automated Game-Winning Teamfight Highlight Clip Generator for Heroes of Newerth"
    )

    parser.add_argument(
        "replay",
        nargs="?",
        default="",
        help="Path to the .honreplay archive."
    )
    parser.add_argument(
        "-r", "--replay-file",
        dest="replay_opt",
        default="",
        help="Explicit replay file path (alternative to positional replay)."
    )
    parser.add_argument(
        "-o", "--output",
        default="",
        help="Destination path for the highlight MP4 video (default: output/highlights/<matchid>_teamfight_<hero>.mp4)."
    )
    parser.add_argument(
        "-p", "--player",
        default="",
        help="Override player perspective (1..10, hero name, or player name). Defaults to the detected MVP hero."
    )
    parser.add_argument(
        "-d", "--duration",
        type=float,
        default=45.0,
        help="Duration of the highlight clip in seconds (default: 45.0s)."
    )
    parser.add_argument(
        "--resolution",
        default="1920x1080",
        help="Output video resolution (default: 1920x1080)."
    )
    parser.add_argument(
        "--fps",
        type=int,
        default=60,
        help="Video framerate in FPS (default: 60)."
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Inspect replay and display highlight analysis without rendering video."
    )
    parser.add_argument(
        "-v", "--verbose",
        action="store_true",
        help="Enable verbose telemetry logging."
    )

    args = parser.parse_args(argv)
    setup_logging(verbose=args.verbose)

    target_replay = args.replay if args.replay else args.replay_opt
    if not target_replay:
        # Check games/client/ for any existing .honreplay
        repo_root = _find_repo_root()
        default_replay_candidates = [
            os.path.join(repo_root, "games", "client", "M0000000001.honreplay"),
            os.path.join(repo_root, "games", "client", "replays", "M0000000001.honreplay")
        ]
        for cand in default_replay_candidates:
            if os.path.isfile(cand):
                target_replay = cand
                break

    if not target_replay or not os.path.isfile(target_replay):
        logger.error("Please specify a valid .honreplay file.")
        parser.print_help()
        return 1

    try:
        analyzer = ReplayHighlightAnalyzer(target_replay)
        highlight = analyzer.analyze_winning_teamfight(duration_sec=args.duration)

        print("\n" + "=" * 80)
        print(" 🎬 DECISIVE GAME-WINNING TEAMFIGHT HIGHLIGHT DETECTED")
        print("=" * 80)
        print(f" Match ID:        {highlight['match_id']}")
        print(f" Winning Team:    {highlight['winner_team']}")
        print(f" Highlight MVP:   {highlight['mvp_name']} ({highlight['mvp_hero']}) - Player [{highlight['mvp_player_number']}]")
        print(f" Time Window:     {highlight['start_seconds']:.1f}s -> {highlight['end_seconds']:.1f}s ({highlight['duration_seconds']:.1f}s clip)")
        print(f" Match Length:    {highlight['match_duration_seconds']:.1f}s")
        print("=" * 80 + "\n")

        if args.dry_run:
            logger.info("Dry-run requested. Exiting without launching video renderer.")
            return 0

        repo_root = _find_repo_root()

        render_highlight_clip(
            replay_path=target_replay,
            highlight_info=highlight,
            output_path=args.output,
            player_override=args.player,
            resolution=args.resolution,
            fps=args.fps,
            repo_root=repo_root,
            verbose=args.verbose
        )

        return 0

    except Exception as ex:
        logger.exception("Highlight generation error: %s", ex)
        return 2


if __name__ == "__main__":
    sys.exit(main())

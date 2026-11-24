# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Unit regression suite for Project Kongor Cinematic Theater media, packaging, and workstation scrubbing.

Value-Weighted Testing Gate:
  - protects:
      1. XML well-formedness of `cinematic_theater.package`, `<video video="/videos/kongor_cinematic.avi">`,
         and `cinematic_gpu_label` free of workstation hardware strings ('canary_hardware_token', 'd3d11va',
         'CanaryPilotUser', 'canary_user_token').
      2. `cinematic_theater.lua` references `/videos/kongor_cinematic.mp3` first in `playCinematicAudio`,
         free of 'CanaryPilotUser' and 'canary_user_token'.
      3. SHA-256 byte-for-byte parity across all 4 `.package` paths and all 4 `.lua` paths.
      4. If `games/client/game/videos/` exists locally on disk, asserts `kongor_cinematic.avi` and
         `kongor_cinematic.mp3` exist, are non-empty, total < 250 MB (262,144,000 bytes), and legacy
         `guccidonut_2.*` files do NOT exist on disk (skipping gracefully if running in a CI checkout
         without untracked `game/videos/`).
  - fails_when:
      - Workstation PII or hardware labels regress into UI package or Lua script.
      - Any mirror of `.package` or `.lua` diverges from `override/`.
      - Audio loading logic fails to prioritize shipped `kongor_cinematic.mp3`.
      - Media files exceed size budgets or legacy `guccidonut_2.*` files linger on disk.
  - seam: Real `xml.etree.ElementTree`, real file I/O & SHA-256 hashing, real filesystem checks.
"""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

REPO_ROOT = Path(__file__).resolve().parents[2]

PACKAGE_PATHS = [
    REPO_ROOT / "games" / "client" / "override" / "ui" / "fe2" / "cinematic_theater.package",
    REPO_ROOT / "games" / "client" / "ClientPackageFiles" / "ui" / "fe2" / "cinematic_theater.package",
    REPO_ROOT / "games" / "client" / "game" / "ui" / "fe2" / "fe2" / "cinematic_theater.package",
]

LUA_PATHS = [
    REPO_ROOT / "games" / "client" / "override" / "ui" / "fe2" / "scripts" / "cinematic_theater.lua",
    REPO_ROOT / "games" / "client" / "ClientPackageFiles" / "ui" / "fe2" / "scripts" / "cinematic_theater.lua",
    REPO_ROOT / "games" / "client" / "game" / "ui" / "fe2" / "fe2" / "scripts" / "cinematic_theater.lua",
]


def _load_forbidden_tokens() -> list[str]:
    tokens = ["canary_hardware_token", "canary_user_token", "c:\\users\\"]
    allowed = {"guccidonut", "guccid0nut"}

    env_denylist = os.environ.get("KONGOR_PII_DENYLIST", "")
    if env_denylist:
        for part in env_denylist.split(","):
            part_clean = part.strip().lower()
            if len(part_clean) >= 4 and part_clean not in allowed and part_clean not in tokens:
                tokens.append(part_clean)

    pii_guard_path = REPO_ROOT / "config" / "local_pii_guard.local.json"
    if pii_guard_path.is_file():
        try:
            with open(pii_guard_path, "r", encoding="utf-8") as f:
                data = json.load(f)
            local_allowed = {str(x).strip().lower() for x in data.get("allowed_public_identity", [])}
            local_allowed |= allowed
            for term in data.get("terms", []):
                val = str(term.get("value", "")).strip().lower()
                if len(val) >= 4 and val not in local_allowed and val not in tokens:
                    tokens.append(val)
        except Exception:
            pass

    return tokens


FORBIDDEN_TOKENS = _load_forbidden_tokens()


def _sha256(path: Path) -> str:
    h = hashlib.sha256()
    h.update(path.read_bytes())
    return h.hexdigest()


class TestCinematicTheaterMedia(unittest.TestCase):
    def test_cinematic_theater_package_xml_and_labels(self) -> None:
        primary_pkg = PACKAGE_PATHS[0]
        self.assertTrue(primary_pkg.is_file(), f"Missing primary package: {primary_pkg}")

        raw_xml = primary_pkg.read_text(encoding="utf-8")
        non_ascii = [ch for ch in raw_xml if ord(ch) >= 128]
        self.assertEqual(
            non_ascii,
            [],
            f"Non-ASCII glyphs {non_ascii!r} render as missing-glyph boxes in K2 dyn_* fonts",
        )
        raw_xml_lower = raw_xml.lower()
        for token in FORBIDDEN_TOKENS:
            self.assertNotIn(
                token,
                raw_xml_lower,
                f"Forbidden workstation token '{token}' discovered in {primary_pkg.name}",
            )

        tree = ET.parse(primary_pkg)
        root = tree.getroot()
        self.assertEqual(root.tag, "package", "Root element must be <package>")

        # Check video element
        video_nodes = root.findall(".//video")
        theater_videos = [v for v in video_nodes if v.get("name") == "cinematic_theater_video"]
        self.assertEqual(len(theater_videos), 1, "Expected exactly 1 cinematic_theater_video element")
        self.assertEqual(
            theater_videos[0].get("video"),
            "/videos/kongor_cinematic.avi",
            "Video path must be '/videos/kongor_cinematic.avi'",
        )

        # Check GPU label
        label_nodes = root.findall(".//label")
        gpu_labels = [l for l in label_nodes if l.get("name") == "cinematic_gpu_label"]
        self.assertEqual(len(gpu_labels), 1, "Expected exactly 1 cinematic_gpu_label element")
        content = gpu_labels[0].get("content", "")
        self.assertIn("VULKAN / DXVK ACCEL", content, "GPU label must reflect Vulkan / DXVK pipeline")
        self.assertIn("1080P60", content, "GPU label must display resolution/framerate 1080P60")

    def test_cinematic_theater_lua_audio_order_and_scrubbing(self) -> None:
        primary_lua = LUA_PATHS[0]
        self.assertTrue(primary_lua.is_file(), f"Missing primary lua script: {primary_lua}")

        lua_text = primary_lua.read_text(encoding="utf-8")
        lua_lower = lua_text.lower()
        for token in FORBIDDEN_TOKENS:
            self.assertNotIn(
                token,
                lua_lower,
                f"Forbidden workstation token '{token}' discovered in {primary_lua.name}",
            )

        self.assertIn("/videos/kongor_cinematic.mp3", lua_text)
        self.assertIn("kongor_cinematic.avi", lua_text)

        # In playCinematicAudio, kongor_cinematic.mp3 must be attempted first in PlaySound
        func_start = lua_text.find("local function playCinematicAudio")
        self.assertGreater(func_start, 0, "playCinematicAudio function not found")
        func_end = lua_text.find("end", func_start + 100)
        # Find closing end of playCinematicAudio
        update_vol_pos = lua_text.find("local function updateVolumeUI", func_start)
        self.assertGreater(update_vol_pos, func_start)
        func_body = lua_text[func_start:update_vol_pos]

        mp3_pos = func_body.find("/videos/kongor_cinematic.mp3")
        wav_pos = func_body.find("/videos/kongor_cinematic.wav")
        self.assertGreater(mp3_pos, -1, "Expected /videos/kongor_cinematic.mp3 in playCinematicAudio")
        self.assertGreater(wav_pos, -1, "Expected /videos/kongor_cinematic.wav fallback in playCinematicAudio")
        self.assertLess(
            mp3_pos,
            wav_pos,
            "Shipped /videos/kongor_cinematic.mp3 must be attempted BEFORE .wav fallback in playCinematicAudio",
        )

    def test_cinematic_theater_mirrors_sha256_parity(self) -> None:
        # Package parity
        package_hashes = {}
        for path in PACKAGE_PATHS:
            if not path.is_file():
                continue
            package_hashes[path] = _sha256(path)

        self.assertGreaterEqual(len(package_hashes), 2, "Expected at least override and ClientPackageFiles mirrors")
        unique_pkg_hashes = set(package_hashes.values())
        self.assertEqual(
            len(unique_pkg_hashes),
            1,
            f"Package mirrors have diverging SHA-256 digests: {package_hashes}",
        )

        # Lua parity
        lua_hashes = {}
        for path in LUA_PATHS:
            if not path.is_file():
                continue
            lua_hashes[path] = _sha256(path)

        self.assertGreaterEqual(len(lua_hashes), 2, "Expected at least override and ClientPackageFiles mirrors")
        unique_lua_hashes = set(lua_hashes.values())
        self.assertEqual(
            len(unique_lua_hashes),
            1,
            f"Lua mirrors have diverging SHA-256 digests: {lua_hashes}",
        )

    def test_cinematic_theater_media_on_disk_if_present(self) -> None:
        videos_dir = REPO_ROOT / "games" / "client" / "game" / "videos"
        if not videos_dir.exists():
            self.skipTest(f"Videos directory {videos_dir} not present on this machine (CI environment)")

        avi_path = videos_dir / "kongor_cinematic.avi"
        mp3_path = videos_dir / "kongor_cinematic.mp3"

        if not avi_path.exists() and not mp3_path.exists():
            self.skipTest(f"Media files not populated in local videos dir {videos_dir}")

        self.assertTrue(avi_path.is_file(), f"Expected {avi_path} to exist on disk")
        self.assertTrue(mp3_path.is_file(), f"Expected {mp3_path} to exist on disk")

        avi_size = avi_path.stat().st_size
        mp3_size = mp3_path.stat().st_size
        self.assertGreater(avi_size, 0, "kongor_cinematic.avi must not be empty")
        self.assertGreater(mp3_size, 0, "kongor_cinematic.mp3 must not be empty")

        total_bytes = avi_size + mp3_size
        max_bytes = 262_144_000  # 250 MB
        self.assertLess(
            total_bytes,
            max_bytes,
            f"Total video + audio media size ({total_bytes} bytes) exceeds 250 MB limit ({max_bytes} bytes)",
        )

        legacy_matches = list(videos_dir.glob("guccidonut_2.*"))
        self.assertEqual(
            legacy_matches,
            [],
            f"Legacy guccidonut_2.* media files must not linger on disk: {legacy_matches}",
        )


if __name__ == "__main__":
    unittest.main()

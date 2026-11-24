# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""Unit tests for hero & alt-avatar SMDL rig vs. CLIP skeleton integrity in effective VFS.

protects: Hero_KingKlout.Alt4 (Earth Spirit 73-bone rig) walk/bored/death/default clips against
          7-bone stone1.model minion clip flattening/scale collapse, Alt4 morph.mdf stone1.model
          isolation (death_2.clip), and Hero_Prisoner.Alt8 / Hero_Shaman.Alt6 override MDF clip resolution.
fails_when: Any Hero_KingKlout.Alt4 core clip reverts to the 7-bone minion clip (m3bone_root scale 0.15),
            alt4/morph.mdf points death_1 back to the 73-bone hero death_1.clip, or Prisoner.Alt8 /
            Shaman.Alt6 override MDFs reference non-existent relative clip/model paths.
seam: Real binary SMDL (.model) & CLIP (.clip) headers/bone-motion chunks and XML .mdf files across
      games/client/override/ and games/client/game/resources*.s2z.
"""

from __future__ import annotations

import pathlib
import posixpath
import re
import struct
import unittest
import zipfile

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
OVERRIDE_ROOT = REPO_ROOT / "games/client/override"
GAME_ROOT = REPO_ROOT / "games/client/game"


def _parse_clip_summary(raw: bytes) -> tuple[int, int, dict[str, dict[int, list[float]]]]:
    if len(raw) < 24 or raw[:4] != b"CLIP":
        raise ValueError(f"Invalid CLIP header: {raw[:8]!r}")
    ver, num_bones, num_frames = struct.unpack_from("<3i", raw, 12)
    pos = 24
    bones: dict[str, dict[int, list[float]]] = {}
    while pos + 8 <= len(raw):
        tag = raw[pos : pos + 4]
        ln = struct.unpack_from("<i", raw, pos + 4)[0]
        chunk = raw[pos + 8 : pos + 8 + ln]
        if tag == b"bmtn" and ln >= 14:
            _, mtype, nkeys = struct.unpack_from("<3i", chunk, 0)
            if ver == 1:
                bname = chunk[12:44].split(b"\x00", 1)[0].decode("utf-8", errors="replace")
                koff = 44
            else:
                nlen = chunk[12]
                bname = chunk[13 : 13 + nlen].decode("utf-8", errors="replace")
                koff = 13 + nlen + 1
            vals = (
                list(struct.unpack_from(f"<{nkeys}f", chunk, koff))
                if mtype in (7, 8, 9) and koff + nkeys * 4 <= len(chunk)
                else []
            )
            bones.setdefault(bname, {})[mtype] = vals
        pos += 8 + ln
    return num_bones, num_frames, bones


class HeroAvatarRigClipIntegrityTests(unittest.TestCase):
    def test_king_klout_alt4_hero_clips_have_full_73_bone_rig_and_05_root_scale(self) -> None:
        clip_dir = OVERRIDE_ROOT / "heroes/king_klout/alt4/clips"
        for clip_name in ("walk_1.clip", "bored_1.clip", "death_1.clip", "default_1.clip"):
            clip_path = clip_dir / clip_name
            self.assertTrue(clip_path.is_file(), f"Missing override clip: {clip_path}")
            nb, nf, bones = _parse_clip_summary(clip_path.read_bytes())
            self.assertEqual(
                nb,
                73,
                f"{clip_name} must animate all 73 Earth Spirit hero bones (got {nb}; 7 means minion clip collision).",
            )
            self.assertGreaterEqual(nf, 20)
            self.assertIn("Root0_JNT", bones, f"{clip_name} missing Root0_JNT hero skeleton root.")
            m3_scales = bones.get("m3bone_root", {})
            for axis_mtype in (7, 8, 9):
                vals = m3_scales.get(axis_mtype, [])
                self.assertTrue(vals, f"{clip_name} missing scale track {axis_mtype} on m3bone_root")
                self.assertAlmostEqual(
                    min(vals),
                    0.5,
                    places=3,
                    msg=f"{clip_name} collapsed m3bone_root scale to {min(vals)} instead of 0.5.",
                )

    def test_king_klout_alt4_morph_mdf_uses_7_bone_stone_minion_death_clip(self) -> None:
        morph_mdf = OVERRIDE_ROOT / "heroes/king_klout/alt4/morph.mdf"
        self.assertTrue(morph_mdf.is_file(), f"Missing {morph_mdf}")
        txt = morph_mdf.read_text(encoding="utf-8")
        self.assertIn('file="stone1.model"', txt)
        self.assertNotIn('clip="clips/death_1.clip"', txt)
        self.assertIn('clip="clips/death_2.clip"', txt)

        res96 = GAME_ROOT / "resources96.s2z"
        if res96.is_file():
            with zipfile.ZipFile(res96) as zf:
                nb, _, _ = _parse_clip_summary(zf.read("heroes/king_klout/alt4/clips/death_2.clip"))
            self.assertEqual(nb, 7, "stone1.model morph death_2.clip must be the 7-bone stone remnant clip.")

    def test_prisoner_alt8_and_shaman_alt6_override_mdfs_resolve_all_clips_and_models(self) -> None:
        mdfs = (
            OVERRIDE_ROOT / "heroes/prisoner/alt8/chained/model.mdf",
            OVERRIDE_ROOT / "heroes/prisoner/alt8/chained/preview.mdf",
            OVERRIDE_ROOT / "heroes/shaman/alt6/model.mdf",
        )
        for mdf in mdfs:
            self.assertTrue(mdf.is_file(), f"Missing expected override MDF: {mdf}")

        if not (GAME_ROOT / "resources0.s2z").is_file():
            return

        # Build lightweight VFS set for prisoner & shaman trees
        vfs_paths: set[str] = set()
        for p in OVERRIDE_ROOT.rglob("*"):
            if p.is_file():
                vfs_paths.add(str(p.relative_to(OVERRIDE_ROOT)).replace("\\", "/").lower())
        for s2z_name in ("resources3.s2z", "resources4.s2z", "resources96.s2z", "resources97.s2z"):
            s2z = GAME_ROOT / s2z_name
            if s2z.is_file():
                with zipfile.ZipFile(s2z) as zf:
                    for name in zf.namelist():
                        vfs_paths.add(name.replace("\\", "/").lstrip("/").lower())

        for mdf in mdfs:
            base_vp = str(mdf.relative_to(OVERRIDE_ROOT)).replace("\\", "/")
            base_dir = posixpath.dirname(base_vp)
            txt = mdf.read_text(encoding="utf-8")
            for attr in ("file", "high"):
                m = re.search(rf'\b{attr}="([^"]+)"', txt)
                if m:
                    resolved = posixpath.normpath(posixpath.join(base_dir, m.group(1))).lower()
                    self.assertIn(resolved, vfs_paths, f"{base_vp} {attr}='{m.group(1)}' -> '{resolved}' missing in VFS")
            for clip_ref in re.findall(r'\bclip="([^"]+)"', txt):
                resolved = posixpath.normpath(posixpath.join(base_dir, clip_ref)).lower()
                self.assertIn(resolved, vfs_paths, f"{base_vp} clip='{clip_ref}' -> '{resolved}' missing in VFS")


if __name__ == "__main__":
    unittest.main()

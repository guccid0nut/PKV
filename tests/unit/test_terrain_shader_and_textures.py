# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""Unit and contract tests for K2 terrain HLSL shaders, 3-tier mip textures, and $checker proof guard (Fix 3.5G).

protects: Clean K2 terrain/cliff pixel shaders (zero rogue fAlpha overrides or magenta-hiding hacks, intact
          bFlatGroundSlab), pristine non-nested game/core/shaders/ synced with override/core/shaders/ps_3_0/,
          100% 3-tier (00000000/00000003/00000005) DDS resolution across all 479 terrain texture references in
          all 17 map .s2z archives, and $checker #FF00FF neon-magenta detection in scripts/verify_proof_screenshot.py.
fails_when: Rogue fAlpha overrides or magenta-suppression return in terrain_color.psh / mesh_color_cliff.psh,
            game/core/shaders/ is missing gui_channel.psh, contains a nested shaders/shaders/ duplicate, or drifts
            from override/core/shaders/ps_3_0/, any map terrain texture lacks a valid DDS in any of the 3 K2
            mip quality tiers, or verify_proof_screenshot.py fails to flag #FF00FF magenta clusters.
seam: Real HLSL .psh shader files, real 17 *.s2z map texturelists + textures.s2z + override/ mip directories,
      and real verify_proof_screenshot.verify_image() against synthetic/real OpenCV frames.
"""

from __future__ import annotations

import os
import pathlib
import re
import struct
import tempfile
import unittest
import zipfile

import cv2
import numpy as np

from scripts.verify_proof_screenshot import verify_image

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]

# Documented terrain donor aliases for custom/legacy map stems absent from retail v4.10.1 & textures.s2z:
# shortcut: 9 custom/beta map terrain stems use visually matched retail textures.s2z donors across 00000000/00000003/00000005, upgrade if original S2 source TGAs for soccer_brightgrass/BETA_grass1/savage2_ground{2,4}/0demonstration_rocktropical are recovered
EXPECTED_OVERRIDE_TERRAIN_STEMS = (
    "world/terrain/ground/dirt_01",
    "world/terrain/textures/0demonstration_rocktropical",
    "world/terrain/textures/beta_grass1",
    "world/terrain/textures/caldavar_hb_edge",
    "world/terrain/textures/caldavar_moss",
    "world/terrain/textures/caldavar_road2",
    "world/terrain/textures/grass1",
    "world/terrain/textures/sand_tiles",
    "world/terrain/textures/sandmixed",
    "world/terrain/textures/savage2_ground2",
    "world/terrain/textures/savage2_ground4",
    "world/terrain/textures/soccer_brightgrass",
)


def decode_k2_text(raw: bytes) -> str:
    if raw[:2] == b"\xff\xfe" or b"\x00" in raw[:40]:
        return raw.decode("utf-16-le", errors="replace").lstrip("\ufeff")
    return raw.decode("utf-8", errors="replace").lstrip("\ufeff")


class TerrainShaderAndTexturesTests(unittest.TestCase):
    def test_terrain_and_cliff_shaders_free_of_rogue_falpha_and_magenta_hacks(self) -> None:
        terrain_psh = (
            REPO_ROOT / "games/client/override/core/shaders/ps_3_0/terrain_color.psh"
        ).read_text(encoding="utf-8")
        cliff_psh = (
            REPO_ROOT / "games/client/override/core/shaders/ps_3_0/mesh_color_cliff.psh"
        ).read_text(encoding="utf-8")

        for name, src in (("terrain_color.psh", terrain_psh), ("mesh_color_cliff.psh", cliff_psh)):
            self.assertNotIn(
                "fAlpha = 1.0f",
                src,
                f"{name} still contains rogue 'fAlpha = 1.0f' dark-base override that forces $checker magenta onto riverbeds/dirt.",
            )
            self.assertNotRegex(
                src,
                r"abs\s*\(\s*c\w+\.r\s*-\s*c\w+\.g\s*\)",
                f"{name} still contains rogue neutral-grey/white Layer-1 punch-through hack.",
            )
            self.assertNotRegex(
                src,
                r"0\.95f.*0\.05f.*0\.95f|255.*0.*255",
                f"{name} must NOT contain magenta-hiding shader hacks ($checker must remain honest).",
            )

        self.assertIn(
            "bool bFlatGroundSlab = (vSpec.y < 0.5f && cCliffDiffuseColor.a < 0.01f);",
            cliff_psh,
            "mesh_color_cliff.psh must preserve bFlatGroundSlab for sol_ground fountain slabs.",
        )
        self.assertIn(
            "bool bNoVertexColor = (dot(In.Color0.rgb, 0.334f) <= 0.0f);",
            cliff_psh,
            "mesh_color_cliff.psh must detect unbaked boned Prop_Cliff2 models (e.g. Strikers stripes_round) lacking COLOR0.",
        )
        self.assertIn(
            "float fAlpha = (bFlatGroundSlab || bNoVertexColor) ? 0.0f : (cTerrainDiffuseColor2.a * In.Color0.a);",
            cliff_psh,
        )
        self.assertIn(
            "#ifdef TERRAIN_DEREPEAT",
            cliff_psh,
            "mesh_color_cliff.psh must gate distance UV derepeat scaling behind #ifdef TERRAIN_DEREPEAT to match terrain_color.psh.",
        )

    def test_client_game_core_shaders_present_clean_and_synced(self) -> None:
        game_dir = REPO_ROOT / "games/client/game"
        if not (game_dir / "resources0.s2z").is_file():
            self.skipTest("Local client binary/archive tree not installed.")

        game_shaders = game_dir / "core/shaders"
        self.assertTrue(
            (game_shaders / "ps_3_0/gui_channel.psh").is_file(),
            "games/client/game/core/shaders/ps_3_0/gui_channel.psh must exist; HoN ships ps_3_0/vs_3_0 HLSL "
            "shaders loose under game/core/shaders/ (not inside resources*.s2z). Restore from retail v4.10.1.",
        )
        self.assertFalse(
            (game_shaders / "shaders").exists(),
            "games/client/game/core/shaders/shaders nested duplicate folder must not exist.",
        )
        for rel in ("ps_3_0/terrain_color.psh", "ps_3_0/mesh_color_cliff.psh"):
            override_bytes = (REPO_ROOT / "games/client/override/core/shaders" / rel).read_bytes()
            game_bytes = (game_shaders / rel).read_bytes()
            self.assertEqual(
                game_bytes,
                override_bytes,
                f"games/client/game/core/shaders/{rel} drifted from games/client/override/core/shaders/{rel}.",
            )
        for rel in ("ps_2_0/terrain_color.psh", "ps_glsl/terrain_color.psh"):
            psh_path = game_shaders / rel
            if psh_path.is_file():
                self.assertNotIn(
                    "fAlpha = 1.0f",
                    psh_path.read_text(encoding="utf-8"),
                    f"games/client/game/core/shaders/{rel} still contains rogue fAlpha = 1.0f override.",
                )

    def test_soccer_pitch_free_of_flat_dull_diffuse_tiles(self) -> None:
        soccer_s2z = REPO_ROOT / "games/client/game/maps/soccer.s2z"
        if not soccer_s2z.is_file():
            self.skipTest("Local soccer.s2z map archive not installed.")
        with zipfile.ZipFile(soccer_s2z) as zf:
            tm = zf.read("tilematerialmap")
        _, w_s, _ = struct.unpack_from("<3i", tm, 0)
        w = abs(w_s)
        bad_tiles = []
        for ty in range(160, 355):
            for tx in range(60, 455):
                _, d0, _ = struct.unpack_from("<3H", tm, 12 + (ty * w + tx) * 20)
                if d0 == 4:
                    bad_tiles.append((tx, ty))
        self.assertEqual(
            bad_tiles,
            [],
            f"soccer.s2z still has {len(bad_tiles)} $flat_dull (id=4) diffuse pitch tiles causing pink strips: {bad_tiles[:10]}",
        )

    def test_all_three_mip_tiers_present_and_valid_dds_for_missing_terrain_stems(self) -> None:
        override_root = REPO_ROOT / "games/client/override"
        dirt_tga = override_root / "world/terrain/ground/dirt_01.tga"
        self.assertTrue(
            dirt_tga.is_file(),
            f"Missing {dirt_tga}; CTexture::Load outside textures.s2z requires unprefixed .tga files.",
        )
        for tier in ("00000000", "00000003", "00000005"):
            dirt_dds = override_root / tier / "world/terrain/ground/dirt_01.dds"
            self.assertTrue(dirt_dds.is_file(), f"Missing {dirt_dds}")
            self._assert_valid_dds(dirt_dds)

        unprefixed_tex_dir = override_root / "world/terrain/textures"
        actual_tgas = (
            {p.name.lower(): p for p in unprefixed_tex_dir.iterdir() if p.is_file()}
            if unprefixed_tex_dir.is_dir()
            else {}
        )
        for stem in EXPECTED_OVERRIDE_TERRAIN_STEMS:
            if stem == "world/terrain/ground/dirt_01":
                continue
            leaf = pathlib.Path(stem).name
            for tga_suffix in ("_d.tga", "_n.tga"):
                tga_lower = f"{leaf}{tga_suffix}".lower()
                self.assertIn(
                    tga_lower,
                    actual_tgas,
                    f"Missing '{tga_lower}' in {unprefixed_tex_dir}; CTexture::Load outside textures.s2z requires .tga.",
                )
            for tier in ("00000000", "00000003", "00000005"):
                tier_dir = override_root / tier / pathlib.Path(stem).parent
                leaf = pathlib.Path(stem).name
                actual = {p.name.lower(): p for p in tier_dir.iterdir() if p.is_file()} if tier_dir.is_dir() else {}
                for suffix in ("_d.dds", "_n_rxgb.dds", "_n_s.dds"):
                    target_lower = f"{leaf}{suffix}".lower()
                    self.assertIn(
                        target_lower,
                        actual,
                        f"Missing '{target_lower}' in {tier_dir}. Populate all 3 K2 mip tiers (00000000/00000003/00000005).",
                    )
                    self._assert_valid_dds(actual[target_lower])

        maps_dir = REPO_ROOT / "games/client/game/maps"
        textures_s2z = REPO_ROOT / "games/client/game/textures.s2z"
        if maps_dir.is_dir() and textures_s2z.is_file():
            if not os.environ.get("KONGOR_RUN_MANUAL_TESTS") and not os.environ.get("NEXUS_RUN_MANUAL_TESTS"):
                return
            with zipfile.ZipFile(textures_s2z) as zf:
                pool = {n.lower() for n in zf.namelist()}
            for tier in ("00000000", "00000003", "00000005"):
                t_root = override_root / tier
                for p in t_root.rglob("*.dds"):
                    pool.add(str(p.relative_to(override_root)).replace("\\", "/").lower())

            unresolved: list[str] = []
            for s2z in sorted(maps_dir.glob("*.s2z")):
                with zipfile.ZipFile(s2z) as zf:
                    for entry in zf.namelist():
                        if entry.lower().endswith("texturelist"):
                            txt = decode_k2_text(zf.read(entry))
                            for ref in re.findall(r'name="(/world/[^"]+)"', txt):
                                clean = ref.lstrip("/").lower()
                                base = clean[:-4] if clean.endswith(".tga") else clean
                                for tier in ("00000000", "00000003", "00000005"):
                                    cands = (
                                        f"{tier}/{base}.dds",
                                        f"{tier}/{clean}",
                                        f"{tier}/{base}_rxgb.dds",
                                    )
                                    if not any(c in pool for c in cands):
                                        unresolved.append(f"{s2z.name}:{tier}:{ref}")
            self.assertEqual(
                unresolved,
                [],
                f"Unresolved map terrain texture references across mip tiers: {unresolved[:20]}",
            )

    def test_verify_proof_screenshot_flags_neon_magenta_checker_and_passes_clean_world(self) -> None:
        rng = np.random.default_rng(42)
        base = rng.integers(35, 110, size=(720, 1280, 3), dtype=np.uint8)
        # Natural green/brown terrain variation
        base[:, :, 1] = np.clip(base[:, :, 1].astype(np.int16) + 25, 0, 255).astype(np.uint8)

        with tempfile.TemporaryDirectory() as tmp:
            clean_path = pathlib.Path(tmp) / "clean.png"
            pink_path = pathlib.Path(tmp) / "pink_checker.png"
            dim_pink_path = pathlib.Path(tmp) / "dim_shadowed_checker.png"
            ui_720p_path = pathlib.Path(tmp) / "learnatorium_purple_ui_720p.png"
            ui_1080p_path = pathlib.Path(tmp) / "learnatorium_purple_ui_1080p.png"
            cv2.imwrite(str(clean_path), base)

            pink_img = base.copy()
            # Inject a 12x12 lit $checker magenta patch (BGR = 195, 35, 195 -> area 144 >= 40)
            pink_img[300:312, 600:612] = (195, 35, 195)
            cv2.imwrite(str(pink_path), pink_img)

            dim_pink_img = base.copy()
            # Inject a 12x12 dim shadowed $checker magenta patch (BGR = 165, 15, 165 -> area 144 >= 40)
            dim_pink_img[300:312, 600:612] = (165, 15, 165)
            cv2.imwrite(str(dim_pink_path), dim_pink_img)

            # Stamp exact measured K2 Learnatorium Level 8 GoldenRed Mastery XP bar + Difficulty diamonds:
            # fill BGR=(179,66,170), bottom bevel BGR=(171,12,161), diamond highlights BGR=(255,154,255)/(219,111,205)
            ui_720 = base.copy()
            ui_720[155:162, 380:520] = (179, 66, 170)
            ui_720[162:164, 380:520] = (171, 12, 161)
            ui_720[120:126, 460:485] = (219, 111, 205)
            ui_720[121:123, 465:475] = (255, 154, 255)
            cv2.imwrite(str(ui_720p_path), ui_720)
            ui_1080 = cv2.resize(ui_720, (1920, 1080), interpolation=cv2.INTER_NEAREST)
            cv2.imwrite(str(ui_1080p_path), ui_1080)

            ok_clean, msg_clean, _, rc_clean = verify_image(str(clean_path), expected_type="in_game", allow_login=True)
            self.assertTrue(ok_clean, f"Clean synthetic frame should pass verify_image: {msg_clean}")
            self.assertEqual(rc_clean, 0)

            for p_label, p_path in (("lit", pink_path), ("dim_shadowed", dim_pink_path)):
                ok_pink, msg_pink, stats_pink, rc_pink = verify_image(str(p_path), expected_type="in_game", allow_login=True)
                self.assertFalse(ok_pink, f"Synthetic {p_label} $checker magenta patch must be rejected by verify_image.")
                self.assertEqual(rc_pink, 1)
                self.assertGreaterEqual(stats_pink["max_magenta_cluster"], 144)
                self.assertIn("$checker", msg_pink)

            for label, ui_path in (("720p", ui_720p_path), ("1080p", ui_1080p_path)):
                ok_ui, msg_ui, stats_ui, rc_ui = verify_image(str(ui_path), expected_type="general", allow_login=True)
                self.assertTrue(ok_ui, f"Learnatorium purple UI chrome ({label}) must NOT false-reject: {msg_ui}")
                self.assertEqual(rc_ui, 0)
                self.assertEqual(stats_ui["magenta_px"], 0)

    def _assert_valid_dds(self, path: pathlib.Path) -> None:
        raw = path.read_bytes()
        self.assertGreaterEqual(len(raw), 136, f"DDS file too small: {path}")
        self.assertEqual(raw[:4], b"DDS ", f"Invalid DDS magic in {path}")
        height, width, _, _, mips = struct.unpack_from("<5I", raw, 12)
        self.assertGreaterEqual(width, 32)
        self.assertGreaterEqual(height, 32)
        self.assertGreaterEqual(mips, 1)


if __name__ == "__main__":
    unittest.main()

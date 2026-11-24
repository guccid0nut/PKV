# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Comprehensive Automated Verification Suite for:
1. DEF-VULKAN-01: Vulkan Particle Billboard Opaque Square Artifact Fix
2. DEF-HUD-01: Trapped In-Match HUD Textures
3. DEF-HUD-02: Missing Legacy UI Frame & Element Textures in game_apex.interface
"""

import os
import glob
import unittest
import xml.etree.ElementTree as ET
from PIL import Image

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
OVERRIDE_ROOT = os.path.join(REPO_ROOT, "games", "client", "override")


class TestVulkanParticleMaterials(unittest.TestCase):
    """Verifies that particle materials in override have depthwrite='false' and valid blendmodes."""

    def setUp(self):
        self.materials_dir = os.path.join(OVERRIDE_ROOT, "shared", "effects", "materials")
        self.assertTrue(os.path.isdir(self.materials_dir), f"Directory not found: {self.materials_dir}")
        self.materials = glob.glob(os.path.join(self.materials_dir, "*.material"))
        self.assertGreater(len(self.materials), 2000, "Expected at least 2000 materials in override")

    def test_all_materials_are_valid_xml(self):
        """Every material file must parse cleanly as XML without syntax errors."""
        failed = []
        for m in self.materials:
            try:
                ET.parse(m)
            except Exception as e:
                failed.append((os.path.basename(m), str(e)))
        self.assertEqual(len(failed), 0, f"Materials with XML parse errors: {failed[:10]}")

    def test_translucent_and_effect_phases_have_depthwrite_false(self):
        """Every translucent, blended, or effect phase must explicitly specify depthwrite='false'."""
        violations = []
        for m in self.materials:
            try:
                tree = ET.parse(m)
                for phase in tree.getroot().findall("phase"):
                    is_trans = phase.attrib.get("translucent", "false").lower() in ("true", "1")
                    is_blend = "srcblend" in phase.attrib or "dstblend" in phase.attrib
                    vs = phase.attrib.get("vs", "")
                    ps = phase.attrib.get("ps", "")
                    is_effect = "effect" in vs or "effect" in ps
                    
                    if is_trans or is_blend or is_effect:
                        dw = phase.attrib.get("depthwrite")
                        if dw != "false":
                            violations.append((os.path.basename(m), phase.attrib.get("name"), dw))
            except Exception:
                pass
        self.assertEqual(len(violations), 0, f"Phases without depthwrite='false': {violations[:10]}")

    def test_effect_phases_have_cull_none(self):
        """Effect billboard phases must specify cull='CULL_NONE' so quads render from any angle."""
        bad_cull = []
        for m in self.materials:
            try:
                tree = ET.parse(m)
                for phase in tree.getroot().findall("phase"):
                    vs = phase.attrib.get("vs", "")
                    ps = phase.attrib.get("ps", "")
                    if "effect" in vs or "effect" in ps:
                        cull = phase.attrib.get("cull")
                        if cull != "CULL_NONE":
                            bad_cull.append((os.path.basename(m), cull))
            except Exception:
                pass
        self.assertEqual(len(bad_cull), 0, f"Effect phases with non-CULL_NONE cull: {bad_cull[:10]}")

    def test_core_particle_materials_exist(self):
        """Core particle materials explicitly cited in CLIENT_DEFECT_MANIFEST must exist."""
        required = [
            "fire_add.material",
            "bad_healing_smoke_12.material",
            "smoke_12.material",
            "2dsmoke01.material",
            "dust.material",
            "dust_cloud.material",
            "whitedot.material",
            "logoball.material",
        ]
        for name in required:
            path = os.path.join(self.materials_dir, name)
            self.assertTrue(os.path.isfile(path), f"Missing core particle material: {name}")


class TestVulkanParticleTGAHeaders(unittest.TestCase):
    """Verifies that 32-bit particle TGAs in override have byte 17 set to 0x08 (8 attribute bits)."""

    def test_particle_tgas_have_8_attribute_bits(self):
        textures_dir = os.path.join(OVERRIDE_ROOT, "shared", "effects", "textures")
        tga_files = glob.glob(os.path.join(textures_dir, "**", "*.tga"), recursive=True)
        self.assertGreater(len(tga_files), 1000, "Expected at least 1000 TGA textures in override")
        
        bad_headers = []
        for path in tga_files:
            try:
                with open(path, "rb") as f:
                    header = f.read(18)
                    if len(header) < 18:
                        continue
                    bpp = header[16]
                    desc = header[17]
                    if bpp == 32:
                        alpha_bits = desc & 0x0F
                        if alpha_bits != 8:
                            bad_headers.append((os.path.basename(path), bpp, hex(desc)))
            except Exception as e:
                bad_headers.append((os.path.basename(path), str(e)))
        
        self.assertEqual(len(bad_headers), 0, f"TGAs with invalid alpha bits: {bad_headers[:10]}")


class TestInMatchHUDTextures(unittest.TestCase):
    """Verifies DEF-HUD-01 trapped in-match HUD textures."""

    def test_trapped_icons_extracted_and_valid(self):
        assets = [
            ("items/icons/muted.dds", 64, 64),
            ("items/icons/muted.tga", 64, 64),
            ("items/icons/shared.dds", 64, 64),
            ("items/icons/shared.tga", 64, 64),
            ("triggers/custom_maps/soccer/ball/mapicon.dds", 128, 128),
            ("triggers/custom_maps/soccer/ball/mapicon.tga", 128, 128),
        ]
        for rel_path, expected_w, expected_h in assets:
            full_path = os.path.join(OVERRIDE_ROOT, rel_path)
            self.assertTrue(os.path.isfile(full_path), f"Missing HUD asset: {rel_path}")
            self.assertGreater(os.path.getsize(full_path), 0, f"Empty HUD asset: {rel_path}")
            
            # Verify image can be decoded and has expected dimensions
            with Image.open(full_path) as im:
                self.assertEqual(im.size, (expected_w, expected_h), f"Dimension mismatch for {rel_path}")

    def test_ui_icons_shared_and_muted_exist(self):
        for name in ["shared.dds", "shared.tga", "muted.dds", "muted.tga"]:
            p = os.path.join(OVERRIDE_ROOT, "ui", "icons", name)
            self.assertTrue(os.path.isfile(p), f"Missing ui/icons asset: {name}")


class TestLegacyUIFrameFallbacks(unittest.TestCase):
    """Verifies DEF-HUD-02 clean 1x1 transparent fallbacks for legacy UI assets in game_apex.interface."""

    def test_all_20_legacy_ui_textures_exist_as_1x1_transparent(self):
        targets = [
            "ui/common/alt_courier",
            "ui/common/alt_courier/AltCourier",
            "ui/common/atribute_overlay_additive",
            "ui/common/button_gloss",
            "ui/common/gold/gold",
            "ui/common/portrait_gloss",
            "ui/common/sign/sign",
            "ui/elements/button_bevel",
            "ui/elements/button_roundhl",
            "ui/elements/stat_icons/total_kills",
            "ui/frames/glass_header_big",
            "ui/frames/key",
            "ui/frames/rounded_bg_blue",
            "ui/frames/rounded_bg_white",
            "ui/frames/roundframe",
            "ui/frames/shadow",
            "ui/frames/togglebox",
            "ui/frames/tooltip_bg",
            "ui/info/combobox_button_up",
            "ui/info/combobox_button_over",
            "ui/info/combobox_dropdown",
        ]

        for rel in targets:
            for ext in [".tga", ".dds"]:
                full_path = os.path.join(OVERRIDE_ROOT, rel + ext)
                self.assertTrue(os.path.isfile(full_path), f"Missing fallback: {rel + ext}")
                self.assertGreater(os.path.getsize(full_path), 0, f"Empty fallback: {rel + ext}")
                
                with Image.open(full_path) as im:
                    self.assertEqual(im.size, (1, 1), f"Expected 1x1 size for {rel + ext}, got {im.size}")
                    rgba = im.convert("RGBA").getpixel((0, 0))
                    self.assertEqual(rgba[3], 0, f"Expected transparent alpha (0) for {rel + ext}, got {rgba}")


class TestGameApexInterfaceBindings(unittest.TestCase):
    """Verifies game_apex.interface XML syntax and bindings."""

    def test_game_apex_interface_valid_xml(self):
        path = os.path.join(OVERRIDE_ROOT, "ui", "game_apex.interface")
        self.assertTrue(os.path.isfile(path), f"Missing game_apex.interface at {path}")
        try:
            tree = ET.parse(path)
            self.assertIsNotNone(tree.getroot())
        except Exception as e:
            self.fail(f"XML parse error in game_apex.interface: {e}")

    def test_game_apex_interface_bindings_present(self):
        path = os.path.join(OVERRIDE_ROOT, "ui", "game_apex.interface")
        with open(path, "r", encoding="utf-8", errors="ignore") as f:
            content = f.read()
        self.assertIn("/triggers/custom_maps/soccer/ball/mapicon.tga", content)
        self.assertIn("/items/icons/shared.tga", content)
        self.assertIn("/items/icons/muted.tga", content)


if __name__ == "__main__":
    unittest.main(verbosity=2)

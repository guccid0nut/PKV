# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
test_native_rmlui_motd.py
Unit test suite for the RmlUi In-Game Store & Web News Panels (MOTD).
Verifies:
1. Template Synchronization: motd.rml and motd.rcss match across root and client asset paths.
2. Structure & 3-Column Layout: Hero Spotlight, Carousel Card, and Merrick's Vault.
3. Carousel Navigation & Deep-Linking Targets: arrow buttons, 4 dots, slide text IDs, and vault deal elements.
4. Vulkan Layer Binary & C Exports: VkLayer_hon_replay_capture.dll exports Kongor_Overlay_ShowMotd, SetVisible, and Toggle.
5. Master Server Feed Contract: NewsController.cs MotdFeedResponseDto aligns with RmlUi DOM hydrator data models.
"""

import os
import unittest
import xml.etree.ElementTree as ET
import ctypes

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

MOTD_RML_PATHS = [
    os.path.join(REPO_ROOT, "assets", "hud", "motd.rml"),
    os.path.join(REPO_ROOT, "games", "client", "Assets", "hud", "motd.rml"),
    os.path.join(REPO_ROOT, "games", "client", "windows-x64", "assets", "hud", "motd.rml"),
]

MOTD_RCSS_PATHS = [
    os.path.join(REPO_ROOT, "assets", "hud", "motd.rcss"),
    os.path.join(REPO_ROOT, "games", "client", "Assets", "hud", "motd.rcss"),
    os.path.join(REPO_ROOT, "games", "client", "windows-x64", "assets", "hud", "motd.rcss"),
]

VK_LAYER_PATHS = [
    os.path.join(REPO_ROOT, "games", "client", "VkLayer_hon_replay_capture.dll"),
    os.path.join(REPO_ROOT, "games", "client", "windows-x64", "VkLayer_hon_replay_capture.dll"),
    os.path.join(REPO_ROOT, "native", "build", "vulkan_layer_build", "Release", "VkLayer_hon_replay_capture.dll"),
]


class TestRmlUiMotd(unittest.TestCase):
    def test_01_template_files_exist_and_synchronized(self):
        """Verify that motd.rml and motd.rcss exist and are bit-for-bit synchronized across paths."""
        for path in MOTD_RML_PATHS:
            self.assertTrue(os.path.exists(path), f"Missing RML file: {path}")

        with open(MOTD_RML_PATHS[0], "rb") as f:
            primary_rml = f.read()
        for path in MOTD_RML_PATHS[1:]:
            with open(path, "rb") as f:
                self.assertEqual(primary_rml, f.read(), f"Desynchronized RML file: {path}")

        for path in MOTD_RCSS_PATHS:
            self.assertTrue(os.path.exists(path), f"Missing RCSS file: {path}")

        with open(MOTD_RCSS_PATHS[0], "rb") as f:
            primary_rcss = f.read()
        for path in MOTD_RCSS_PATHS[1:]:
            with open(path, "rb") as f:
                self.assertEqual(primary_rcss, f.read(), f"Desynchronized RCSS file: {path}")

    def test_02_rml_structure_and_panoramic_columns(self):
        """Verify the 3-column panoramic layout (Hero Spotlight, Carousel, Merrick's Vault)."""
        tree = ET.parse(MOTD_RML_PATHS[0])
        root = tree.getroot()
        self.assertEqual(root.tag, "rml")

        element_ids = {elem.attrib["id"] for elem in root.iter() if "id" in elem.attrib}

        required_layout_ids = [
            "motd-window",
            "hero-name",
            "hero-subtitle",
            "hero-tags",
            "hero-lore",
            "slide-badge",
            "slide-title",
            "slide-subtitle",
            "bullet-1",
            "bullet-2",
            "bullet-3",
            "deal-1",
            "deal-2",
            "deal-1-title",
            "deal-1-price",
            "deal-2-title",
            "deal-2-price",
            "btn-visit-store",
        ]

        for req_id in required_layout_ids:
            self.assertIn(req_id, element_ids, f"Missing required MOTD element ID: {req_id}")

    def test_03_interactive_carousel_controls(self):
        """Verify navigation arrows, 4 active dot indicators, and slide action button exist."""
        tree = ET.parse(MOTD_RML_PATHS[0])
        root = tree.getroot()
        element_ids = {elem.attrib["id"] for elem in root.iter() if "id" in elem.attrib}

        # Carousel navigation arrows
        self.assertIn("btn-prev-slide", element_ids, "Missing previous slide navigation button")
        self.assertIn("btn-next-slide", element_ids, "Missing next slide navigation button")

        # 4 Interactive dot indicators
        for i in range(4):
            dot_id = f"dot-{i}"
            self.assertIn(dot_id, element_ids, f"Missing carousel dot indicator: {dot_id}")

        # Slide action button
        self.assertIn("slide-action-btn", element_ids, "Missing slide action button")

    def test_04_vulkan_layer_binary_and_exports(self):
        """Verify VkLayer_hon_replay_capture.dll exists and exports MOTD and visibility APIs."""
        deployed_paths = [p for p in VK_LAYER_PATHS if "build" not in p]
        for dll_path in deployed_paths:
            self.assertTrue(os.path.exists(dll_path), f"Missing layer DLL: {dll_path}")

        # Load DLL and test C exported symbols
        primary_dll = ctypes.CDLL(VK_LAYER_PATHS[0])

        show_motd = getattr(primary_dll, "Kongor_Overlay_ShowMotd", None)
        self.assertIsNotNone(show_motd, "Missing exported symbol: Kongor_Overlay_ShowMotd")

        set_visible = getattr(primary_dll, "Kongor_Overlay_SetVisible", None)
        self.assertIsNotNone(set_visible, "Missing exported symbol: Kongor_Overlay_SetVisible")

        toggle = getattr(primary_dll, "Kongor_Overlay_Toggle", None)
        self.assertIsNotNone(toggle, "Missing exported symbol: Kongor_Overlay_Toggle")

    def test_05_master_server_feed_alignment(self):
        """Verify Master Server NewsController.cs defines the expected feed endpoints and slide IDs."""
        news_controller_path = os.path.join(
            REPO_ROOT, "src", "services", "master-server", "Controllers", "News", "NewsController.cs"
        )
        self.assertTrue(os.path.exists(news_controller_path), f"Missing NewsController: {news_controller_path}")

        with open(news_controller_path, "r", encoding="utf-8") as f:
            content = f.read()

        # Check endpoints
        self.assertIn('api/motd', content)
        self.assertIn('events/common/client/motd/feed.json', content)

        # Check Hero Spotlight
        self.assertIn('Hero_Gwynplaine', content)
        self.assertIn('The Man Who Laughs', content)

        # Check Carousel Slide IDs
        self.assertIn('"bubbles"', content)
        self.assertIn('"fps"', content)
        self.assertIn('"audio"', content)
        self.assertIn('"twitch"', content)

        # Check Merrick's Vault Deals
        self.assertIn('Join the Discord', content)
        self.assertIn('Looking for Volunteers', content)


if __name__ == "__main__":
    unittest.main()

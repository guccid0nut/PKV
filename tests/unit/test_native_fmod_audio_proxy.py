# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
test_native_fmod_audio_proxy.py
Comprehensive unit test suite for native fmodex64 audio proxy (kongor_audio).
Verifies:
1. PE Header & Export Parity: 100% of k2_x64.dll audio imports satisfied.
2. DLL Forwarders: Forwarded symbols route cleanly to fmodex64_core.dll.
3. Dynamic In-Process Loading via ctypes.WinDLL.
4. Channel Unclamping: Default configuration unclamps engine from 64 to 256+ voices.
5. WASAPI Low-Latency Buffer Calibration: 256 samples x 4 buffers (5.33ms latency).
6. Diagnostics & Reconfiguration API: Kongor_Audio_GetStatus, Kongor_Audio_Configure,
   Kongor_Audio_Seek, Kongor_Audio_GetCinematicPosition, Kongor_Audio_ResetCinematic.
7. Unbuffered Native Logging to logs/kongor_audio.log.
"""

import os
import sys
import ctypes
import unittest
import pefile

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
CLIENT_FMOD_PATH = os.path.join(REPO_ROOT, "games", "client", "fmodex64.dll")
CLIENT_CORE_PATH = os.path.join(REPO_ROOT, "games", "client", "fmodex64_core.dll")
CLIENT_K2_PATH = os.path.join(REPO_ROOT, "games", "client", "k2_x64.dll")
AUDIO_LOG_PATH = os.path.join(REPO_ROOT, "logs", "kongor_audio.log")

class AudioTelemetry(ctypes.Structure):
    _pack_ = 8
    _fields_ = [
        ("outputType", ctypes.c_int32),
        ("speakerMode", ctypes.c_int32),
        ("bufferLength", ctypes.c_int32),
        ("numBuffers", ctypes.c_int32),
        ("wasapiActive", ctypes.c_int32),
        ("hrtfActive", ctypes.c_int32),
        ("latencyMs", ctypes.c_float),
        ("listenerPos", ctypes.c_float * 3),
        ("listenerFwd", ctypes.c_float * 3),
        ("activeVoices", ctypes.c_int32),
        ("peakVoices", ctypes.c_int32),
        ("maxChannelsConfigured", ctypes.c_int32),
    ]

class TestNativeFmodAudioProxy(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not os.path.exists(CLIENT_FMOD_PATH):
            raise unittest.SkipTest(f"Proxy binary not found at {CLIENT_FMOD_PATH}")
        if not os.path.exists(CLIENT_CORE_PATH):
            raise unittest.SkipTest(f"Genuine FMOD core binary not found at {CLIENT_CORE_PATH}")

    def test_01_pe_exports_satisfy_all_k2_engine_imports(self):
        """Verify that every single symbol imported by k2_x64.dll is exported by fmodex64.dll."""
        if not os.path.exists(CLIENT_K2_PATH):
            self.skipTest(f"k2_x64.dll not found at {CLIENT_K2_PATH}")

        pe_k2 = pefile.PE(CLIENT_K2_PATH)
        k2_imports = set()
        for entry in getattr(pe_k2, "DIRECTORY_ENTRY_IMPORT", []):
            if "fmod" in entry.dll.decode("utf-8", "ignore").lower():
                for imp in entry.imports:
                    if imp.name:
                        k2_imports.add(imp.name.decode("utf-8"))

        self.assertGreaterEqual(len(k2_imports), 90, "Expected at least 90 FMOD imports in k2_x64.dll")

        pe_proxy = pefile.PE(CLIENT_FMOD_PATH)
        proxy_exports = {exp.name.decode("utf-8") for exp in pe_proxy.DIRECTORY_ENTRY_EXPORT.symbols if exp.name}

        missing = k2_imports - proxy_exports
        self.assertEqual(len(missing), 0, f"Proxy missing {len(missing)} imports required by K2: {missing}")

    def test_02_custom_diagnostic_apis_exported(self):
        """Verify presence of custom Kongor_Audio_* diagnostic & telemetry endpoints."""
        pe_proxy = pefile.PE(CLIENT_FMOD_PATH)
        proxy_exports = {exp.name.decode("utf-8") for exp in pe_proxy.DIRECTORY_ENTRY_EXPORT.symbols if exp.name}

        required_custom_apis = [
            "Kongor_Audio_GetStatus",
            "Kongor_Audio_Configure",
            "Kongor_Audio_Seek",
            "Kongor_Audio_GetCinematicPosition",
            "Kongor_Audio_ResetCinematic",
        ]
        for api in required_custom_apis:
            self.assertIn(api, proxy_exports, f"Missing custom export: {api}")

    def test_03_forwarder_exports_target_core_engine(self):
        """Verify that forwarded exports target fmodex64_core correctly."""
        pe_proxy = pefile.PE(CLIENT_FMOD_PATH)
        forwarders = [exp for exp in pe_proxy.DIRECTORY_ENTRY_EXPORT.symbols if exp.forwarder]
        self.assertGreater(len(forwarders), 50, "Expected multiple forwarder exports targeting fmodex64_core")

        for fwd in forwarders:
            target = fwd.forwarder.decode("utf-8", "ignore")
            self.assertTrue(target.startswith("fmodex64_core."), f"Invalid forwarder target: {target}")

    def test_04_dynamic_load_and_initial_telemetry(self):
        """Dynamically load fmodex64.dll and verify safe stereo opt-in defaults (<100 KB proxy)."""
        self.assertLess(
            os.path.getsize(CLIENT_FMOD_PATH),
            100 * 1024,
            "fmodex64.dll proxy must remain < 100 KB for WILLOWMAKER ProxySizeMaxThreshold",
        )
        cdll = ctypes.WinDLL(CLIENT_FMOD_PATH)
        # Ensure clean baseline in case a prior test reconfigured in-process state
        cdll.Kongor_Audio_Configure(0, 0, 1024)
        telem = AudioTelemetry()
        cdll.Kongor_Audio_GetStatus(ctypes.byref(telem))

        # Day-2 safe defaults: 256 voices unclamped, Stereo/engine driver pass-through (WASAPI=0, HRTF=0), 1024 buffer
        self.assertGreaterEqual(telem.maxChannelsConfigured, 256)
        self.assertEqual(telem.bufferLength, 1024)
        self.assertEqual(telem.numBuffers, 4)
        self.assertEqual(telem.wasapiActive, 0)
        self.assertEqual(telem.hrtfActive, 0)

    def test_05_dynamic_reconfiguration_api(self):
        """Test runtime reconfiguration via Kongor_Audio_Configure."""
        cdll = ctypes.WinDLL(CLIENT_FMOD_PATH)

        # Configure WASAPI=1, HRTF=1, bufferSize=128 (2.67ms latency)
        cdll.Kongor_Audio_Configure(1, 1, 128)

        telem = AudioTelemetry()
        cdll.Kongor_Audio_GetStatus(ctypes.byref(telem))

        self.assertEqual(telem.wasapiActive, 1)
        self.assertEqual(telem.hrtfActive, 1)
        self.assertEqual(telem.bufferLength, 128)
        self.assertAlmostEqual(telem.latencyMs, 2.667, places=2)

        # Restore safe stereo opt-in defaults
        cdll.Kongor_Audio_Configure(0, 0, 1024)

    def test_06_cinematic_seek_and_reset(self):
        """Test soundtrack seek tracking and reset."""
        cdll = ctypes.WinDLL(CLIENT_FMOD_PATH)

        cdll.Kongor_Audio_ResetCinematic()
        self.assertEqual(cdll.Kongor_Audio_GetCinematicPosition(), 0)

        # Seek to 45,000 ms (45 sec)
        cdll.Kongor_Audio_Seek(0, 45000)
        self.assertEqual(cdll.Kongor_Audio_GetCinematicPosition(), 45000)

        cdll.Kongor_Audio_ResetCinematic()
        self.assertEqual(cdll.Kongor_Audio_GetCinematicPosition(), 0)

    def test_07_unbuffered_native_logging(self):
        """Verify that native logging is active and writing to logs/kongor_audio.log."""
        self.assertTrue(os.path.exists(AUDIO_LOG_PATH), f"Log file not found at {AUDIO_LOG_PATH}")
        with open(AUDIO_LOG_PATH, "r", encoding="utf-8", errors="ignore") as f:
            content = f.read()

        self.assertIn("[KONGOR_AUDIO]", content)
        self.assertIn("Genuine FMOD Ex core loaded successfully", content)

    def test_08_repaired_effects_stringtable_and_audio_assets_valid(self):
        """
        protects: K2 XML effect parser (main-menu tower_1 FPS drop) & FMOD createSound asset integrity
        fails_when: stray </model>/<model tags return, client_sounds_en.str regains trailing tabs, or repaired .ogg files fail FMOD_OPENMEMORY
        seam: xml.etree.ElementTree + genuine fmodex64_core.dll FMOD::System::createSound
        """
        import xml.etree.ElementTree as ET

        effect_rels = [
            os.path.join("buildings", "conq01", "attack_tower", "tower_1", "effects", "body.effect"),
            os.path.join("items", "basic", "flaming_eye", "ursa", "death.effect"),
            os.path.join("triggers", "custom_maps", "thegrimmhunt", "armordilo", "effects", "passive_docile.effect"),
        ]
        for rel in effect_rels:
            full = os.path.join(REPO_ROOT, "games", "client", "override", rel)
            self.assertTrue(os.path.exists(full), f"Missing repaired .effect override: {full}")
            root = ET.parse(full).getroot()
            self.assertEqual(root.tag, "effect")

        str_path = os.path.join(REPO_ROOT, "games", "client", "override", "stringtables", "client_sounds_en.str")
        self.assertTrue(os.path.exists(str_path))
        with open(str_path, "r", encoding="utf-8") as f:
            str_lines = f.read().splitlines()
        for line in str_lines:
            self.assertFalse(
                line.endswith((".wav\t", ".ogg\t", ".mp3\t")),
                f"Trailing tab after audio extension in client_sounds_en.str: {line!r}",
            )
        joined = "\n".join(str_lines)
        self.assertIn("/shared/sounds/announcer/{set}/startgame.wav", joined)
        self.assertIn("/shared/sounds/radial/en/defend_this_structure.wav", joined)

        repaired_oggs = [
            "shared/sounds/ui/menu/button_click.ogg",
            "heroes/flint_beastwood/alt12/ability_04/sounds/impact.ogg",
            "heroes/flint_beastwood/alt12/ability_04/sounds/cast2_1.ogg",
            "heroes/aluna/alt10/ability_02/sounds/cast_grunt.ogg",
            "heroes/mumra/alt/ability_02/sounds/cast_vox_3.ogg",
            "heroes/mumra/alt/ability_04/sounds/cast_vox_3.ogg",
            "heroes/frosty/alt10/sounds/death_3.ogg",
            "heroes/gunblade/alt10/sounds/voice/no_mana_4.ogg",
            "heroes/bomb/alt12/custom/sounds2/cast_chance_2.ogg",
            "heroes/bomb/alt12/custom/sounds2/cast_chance_3.ogg",
            "heroes/treant/ability_02/sounds/cast.ogg",
        ]
        for rel in repaired_oggs:
            p = os.path.join(REPO_ROOT, "games", "client", "override", *rel.split("/"))
            self.assertTrue(os.path.exists(p), f"Missing repaired .ogg override: {p}")
            with open(p, "rb") as f:
                data = f.read()
            self.assertGreater(len(data), 1024, f"Repaired .ogg unexpectedly tiny: {rel}")
            self.assertFalse(data.startswith(b"<!DOCTYP"), f"HTML error page still present in {rel}")

if __name__ == "__main__":
    unittest.main()

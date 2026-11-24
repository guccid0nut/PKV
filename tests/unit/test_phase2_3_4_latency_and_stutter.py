# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Automated unit verification for Phase 2, 3, and 4:
- Phase 2: DXVK Low-Latency Esports Mode (games/client/dxvk.conf, games/client/windows-x64/dxvk.conf)
- Phase 3: Startup Config 1000 FPS & VSync/PresentInterval decoupling
- Phase 4: Communicator Lua SetVisible boolean coercion & Lua 5.1 bytecode compilation
"""

import os
import subprocess
import unittest

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

class Phase234VerificationTests(unittest.TestCase):
    def test_phase2_dxvk_configs(self):
        if not os.environ.get("KONGOR_RUN_MANUAL_TESTS") and not os.environ.get("NEXUS_RUN_MANUAL_TESTS"):
            self.skipTest("Live client DXVK config benchmark test; run with KONGOR_RUN_MANUAL_TESTS=1")
        conf_paths = [
            os.path.join(REPO_ROOT, "games", "client", "dxvk.conf"),
            os.path.join(REPO_ROOT, "games", "client", "windows-x64", "dxvk.conf")
        ]
        
        required_settings = {
            "d3d9.presentInterval": "0",
            "dxgi.syncInterval": "0",
            "d3d9.tearFree": "false",
            "dxgi.tearFree": "false",
            "d3d9.maxFrameLatency": "1",
            "dxgi.maxFrameLatency": "1",
            "d3d9.maxFrameRate": "1000",
            "dxvk.nvLowLatency": "true",
            "d3d9.deferSurfaceCreation": "false",
            "dxvk.numCompilerThreads": "8",
            "dxvk.enableGraphicsPipelineLibrary": "true",
            "dxvk.gplFastLink": "true",
            "dxvk.enableAsync": "true"
        }
        
        for path in conf_paths:
            self.assertTrue(os.path.exists(path), f"File missing: {path}")
            with open(path, "r", encoding="utf-8") as f:
                content = f.read()
            
            for key, expected_val in required_settings.items():
                expected_line = f"{key} = {expected_val}"
                self.assertIn(expected_line, content, f"Missing or incorrect '{expected_line}' in {path}")
            self.assertTrue(content.endswith("\n"), f"File missing trailing newline: {path}")

    def test_phase3_startup_configs(self):
        if not os.environ.get("KONGOR_RUN_MANUAL_TESTS") and not os.environ.get("NEXUS_RUN_MANUAL_TESTS"):
            self.skipTest("Live client startup.cfg benchmark test; run with KONGOR_RUN_MANUAL_TESTS=1")
        # 1. override startup.cfg (UTF-16LE)
        override_cfg = os.path.join(REPO_ROOT, "games", "client", "userdata", "Heroes of Newerth x64", "override", "startup.cfg")
        self.assertTrue(os.path.exists(override_cfg), f"Missing {override_cfg}")
        with open(override_cfg, "r", encoding="utf-16le") as f:
            override_content = f.read()
        
        self.assertIn('SetSave "cl_maxFPS" "1000" "0"', override_content)
        self.assertIn('SetSave "host_maxFPS" "1000" "0"', override_content)
        self.assertIn('SetSave "host_maximumFPS" "1000" "0"', override_content)
        self.assertIn('SetSave "vid_maxFPS" "1000" "0"', override_content)
        self.assertIn('SetSave "d3d_presentInterval" "0" "0"', override_content)
        self.assertIn('SetSave "vid_vsync" "false" "0"', override_content)
        self.assertIn('SetSave "gl_swapInterval" "0" "0"', override_content)
        self.assertNotIn('SetSave "cl_maxFPS" "144"', override_content)
        self.assertNotIn('SetSave "vid_maxFPS" "144"', override_content)

        # 2. game startup.cfg (UTF-8)
        game_cfg = os.path.join(REPO_ROOT, "games", "client", "userdata", "Heroes of Newerth x64", "game", "startup.cfg")
        self.assertTrue(os.path.exists(game_cfg), f"Missing {game_cfg}")
        with open(game_cfg, "r", encoding="utf-8") as f:
            game_content = f.read()
            
        self.assertIn('SetSave "cl_maxFPS" "1000" "0"', game_content)
        self.assertIn('SetSave "host_maxFPS" "1000" "0"', game_content)
        self.assertIn('SetSave "host_maximumFPS" "1000" "0"', game_content)
        self.assertIn('SetSave "vid_maxFPS" "1000" "0"', game_content)
        self.assertIn('SetSave "d3d_presentInterval" "0" "0"', game_content)
        self.assertIn('SetSave "vid_vsync" "false" "0"', game_content)

        # 3. base startup.cfg (UTF-8)
        base_cfg = os.path.join(REPO_ROOT, "games", "client", "userdata", "Heroes of Newerth x64", "base", "startup.cfg")
        self.assertTrue(os.path.exists(base_cfg), f"Missing {base_cfg}")
        with open(base_cfg, "r", encoding="utf-8") as f:
            base_content = f.read()
            
        self.assertIn('SetSave "cl_maxFPS" "1000" "0"', base_content)
        self.assertIn('SetSave "host_maxFPS" "1000" "0"', base_content)
        self.assertIn('SetSave "host_maximumFPS" "1000" "0"', base_content)
        self.assertIn('SetSave "vid_maxFPS" "1000" "0"', base_content)
        self.assertIn('SetSave "d3d_presentInterval" "0" "0"', base_content)
        self.assertIn('SetSave "vid_vsync" "false" "0"', base_content)

        # 4. client startup.cfg (UTF-8)
        client_cfg = os.path.join(REPO_ROOT, "games", "client", "startup.cfg")
        self.assertTrue(os.path.exists(client_cfg), f"Missing {client_cfg}")
        with open(client_cfg, "r", encoding="utf-8") as f:
            client_content = f.read()
            
        self.assertIn('SetSave "cl_maxFPS" "1000" "0"', client_content)
        self.assertIn('SetSave "host_maxFPS" "1000" "0"', client_content)
        self.assertIn('SetSave "host_maximumFPS" "1000" "0"', client_content)
        self.assertIn('SetSave "vid_maxFPS" "1000" "0"', client_content)
        self.assertIn('SetSave "d3d_presentInterval" "0" "0"', client_content)
        self.assertIn('SetSave "vid_vsync" "false" "0"', client_content)

        # 5. verify launch_screen_practice.ps1 does not override presentInterval to 1
        practice_script = os.path.join(REPO_ROOT, "scripts", "launch_screen_practice.ps1")
        if os.path.exists(practice_script):
            with open(practice_script, "r", encoding="utf-8") as f:
                practice_content = f.read()
            self.assertNotIn('d3d_presentInterval 1', practice_content, "launch_screen_practice.ps1 must not force d3d_presentInterval 1")
            self.assertIn('d3d_presentInterval 0', practice_content, "launch_screen_practice.ps1 must decouple d3d_presentInterval to 0")

    def test_phase4_communicator_lua_boolean_safety(self):
        override_lua = os.path.join(REPO_ROOT, "games", "client", "override", "ui", "scripts", "newui", "communicator_v2.lua")
        self.assertTrue(os.path.exists(override_lua), f"Missing {override_lua}")
        with open(override_lua, "r", encoding="utf-8") as f:
            content = f.read()
            
        expected_call = "kickWidget:SetVisible((isGameChannel and user.showKick) and true or false)"
        self.assertIn(expected_call, content, "communicator_v2.lua does not contain boolean-coerced SetVisible call")
        self.assertNotIn("kickWidget:SetVisible(isGameChannel and user.showKick)", content, "communicator_v2.lua still contains unsafe SetVisible call")

        # Bytecode compilation verification with luac51
        luac = next(
            (
                p for p in (
                    os.path.join(REPO_ROOT, "native", "build", "luac51_build", "Release", "luac51.exe"),
                    os.path.join(REPO_ROOT, "native", "tools", "luac51", "build", "Release", "luac51.exe"),
                    os.path.join(REPO_ROOT, "artifacts", "native-binaries", "windows-x64", "luac51.exe"),
                    os.path.join(REPO_ROOT, ".tools", "bin", "lua", "luac51.exe"),
                )
                if os.path.exists(p)
            ),
            "",
        )
        if os.path.exists(luac):
            res = subprocess.run([luac, "-p", override_lua], capture_output=True, text=True)
            self.assertEqual(res.returncode, 0, f"luac51 compilation failed on {override_lua}: {res.stderr}")

if __name__ == "__main__":
    unittest.main()

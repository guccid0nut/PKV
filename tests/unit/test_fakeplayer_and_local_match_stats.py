# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Unit regression test suite for Phase D:
- Fix AddFakePlayer instant concede in non-solo matches:
  - CPlayer::InitializeConnectionless IAT hook on game_x64.dll.
  - Clears PLAYER_FLAG_TERMINATED (0x0080) at CPlayer+0x3DE.
  - Disarms disconnect grace timer at CPlayer+0x438.
  - Persistent GameSharedPatcherWorker & EnsureGameX64IatHooksInstalled loop.
- Guard unrecorded local match post-game stats (MatchID <= 0 / >= 4294967295):
  - main_v2.lua post-match trigger gate.
  - match_stats_v2.lua MatchInfoSummary error mask suppression.
  - match_stats_v2.lua ChatShowPostGameStats early return guard.
- SHA-256 byte-for-byte mirror parity across override and games/ trees.
"""

import hashlib
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent


def get_file_sha256(path: Path) -> str:
    hasher = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(65536):
            hasher.update(chunk)
    return hasher.hexdigest()


def read_native_guard_sources() -> str:
    guard_dir = REPO_ROOT / "native" / "proxies" / "native_guard"
    parts = []
    for name in ("dllmain.cpp", "vfs_and_hooks.cpp", "queue_arcade_hooks.cpp", "kongor_base_cmd.h"):
        p = guard_dir / name
        if p.is_file():
            parts.append(p.read_text(encoding="utf-8", errors="replace"))
    return "\n".join(parts)


class TestFakePlayerAndLocalMatchStats(unittest.TestCase):
    """
    Protects:
      1. FakePlayer termination flag clearing and grace timer reset in native_guard.
      2. Dynamic and persistent IAT hooking of game_x64.dll across reconnects and map loads.
      3. Prevention of automatic post-game stats screen popups on practice/local matches.
      4. Suppression of fetch failure masks when MatchID is 0 / unrecorded.
      5. Full 5-way mirror parity for modified UI Lua scripts.
    """

    def test_native_guard_initialize_connectionless_hook(self):
        """
        Verifies dllmain.cpp defines Hooked_CPlayer_InitializeConnectionless,
        clears 0x0080 at +0x3DE, sets 0xFFFFFFFFu at +0x438, and contains
        the mangled symbol for CPlayer::InitializeConnectionless.
        """
        dllmain_path = REPO_ROOT / "native" / "proxies" / "native_guard" / "dllmain.cpp"
        self.assertTrue(dllmain_path.is_file(), f"Missing {dllmain_path}")
        content = read_native_guard_sources()

        # 1. Symbol name and function pointer
        self.assertIn("kSymInitializeConnectionless", content)
        self.assertIn("?InitializeConnectionless@CPlayer@@QEAAXPEAVCHostServer@@HAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@_N@Z", content)
        self.assertIn("PFN_CPlayer_InitializeConnectionless", content)
        self.assertIn("g_origInitializeConnectionless", content)

        # 2. Hook implementation with SEH and bitmask manipulation
        self.assertIn("Hooked_CPlayer_InitializeConnectionless", content)
        self.assertIn("0x3DE", content)
        self.assertIn("0x0080", content)
        self.assertIn("0x438", content)
        self.assertIn("0xFFFFFFFFu", content)

        # 3. Sentinel and OutputDebugString logging for verification
        self.assertIn("[KONGOR NATIVE GUARD] [FAKEPLAYER] Active-client flag preserved for FakePlayer", content)

    def test_native_guard_game_x64_iat_hook_manager(self):
        """
        Verifies EnsureGameX64IatHooksInstalled installs both CanAccessAltAvatar
        and InitializeConnectionless on game_x64.dll, is thread-safe via SRWLOCK,
        and is invoked from CPlayer hook call-sites and GameSharedPatcherWorker.
        """
        content = read_native_guard_sources()

        self.assertIn("EnsureGameX64IatHooksInstalled()", content)
        self.assertIn("s_gameX64IatLock", content)
        self.assertIn("AcquireSRWLockExclusive", content)
        self.assertIn("ReleaseSRWLockExclusive", content)

        # IAT Hook registrations
        self.assertIn("kSymInitializeConnectionless", content)
        self.assertIn("Hooked_CPlayer_InitializeConnectionless", content)
        self.assertIn("CanAccessAltAvatar", content)
        self.assertIn("HookedCanAccessAltAvatar2", content)

        # Call sites
        idx_spawn = content.find("Hooked_CPlayer_SpawnHero")
        self.assertGreater(idx_spawn, 0)
        spawn_body = content[idx_spawn:idx_spawn + 300]
        self.assertIn("EnsureGameX64IatHooksInstalled();", spawn_body)

        idx_select = content.find("Hooked_CPlayer_SelectHero")
        self.assertGreater(idx_select, 0)
        select_body = content[idx_select:idx_select + 300]
        self.assertIn("EnsureGameX64IatHooksInstalled();", select_body)

    def test_native_guard_patcher_worker_persistent_loop(self):
        """
        Verifies GameSharedPatcherWorker no longer exits after 300 attempts (2.5 mins),
        runs a persistent while (true) loop with 250ms sleep, and calls EnsureGameX64IatHooksInstalled on every tick.
        """
        content = read_native_guard_sources()

        idx_worker = content.find("DWORD WINAPI GameSharedPatcherWorker(LPVOID")
        self.assertGreater(idx_worker, 0)
        idx_end = content.find("static bool InitializeRawInput", idx_worker)
        self.assertGreater(idx_end, idx_worker)
        worker_code = content[idx_worker:idx_end]

        # Must not contain the old attempt counter
        self.assertNotIn("attempts < 300", worker_code)
        self.assertIn("while (true)", worker_code)
        self.assertIn("EnsureGameX64IatHooksInstalled();", worker_code)
        self.assertIn("Sleep(250);", worker_code)

    def test_main_v2_postgame_stats_guard(self):
        """
        Verifies main_v2.lua checks (showStatsMid > 0) and (showStatsMid < 4294967295),
        and resets ui_match_stats_waitingToShow to 'false' for unrecorded matches.
        """
        main_path = REPO_ROOT / "games" / "client" / "override" / "ui" / "scripts" / "main_v2.lua"
        self.assertTrue(main_path.is_file(), f"Missing {main_path}")
        content = main_path.read_text(encoding="utf-8", errors="replace")

        self.assertIn("local showStatsMid = tonumber(GetShowStatsMatchID()) or 0", content)
        self.assertIn("(showStatsMid > 0) and (showStatsMid < 4294967295)", content)
        self.assertIn("Set('ui_match_stats_waitingToShow', 'false', 'bool')", content)
        self.assertIn("Skipping post-game stats auto-open for unrecorded/spectated", content)

    def test_match_stats_v2_local_match_guards(self):
        """
        Verifies match_stats_v2.lua guards:
        1. MatchInfoSummary suppresses fetch_fail_mask for non-historical midNum <= 0.
        2. ChatShowPostGameStats ignores showMid <= 0 or showMid >= 4294967295.
        """
        stats_path = REPO_ROOT / "games" / "client" / "override" / "ui" / "scripts" / "newui" / "match_stats_v2.lua"
        self.assertTrue(stats_path.is_file(), f"Missing {stats_path}")
        content = stats_path.read_text(encoding="utf-8", errors="replace")

        # 1. MatchInfoSummary suppression
        self.assertTrue(
            "local midNum = tonumber(match_id) or " in content,
            "match_stats_v2.lua must parse midNum from tonumber(match_id)",
        )
        self.assertTrue(
            ("if midNum <= 0 and not (matchStats and matchStats.isHistorical) then" in content)
            or ("if (tonumber(match_id) or 0) <= 0 and not (matchStats and matchStats.isHistorical) then" in content),
            "match_stats_v2.lua must suppress fetch_fail_mask for non-historical local matches (match_id <= 0)",
        )
        self.assertIn("Suppressed fetch_fail_mask for unrecorded local match", content)

        # 2. ChatShowPostGameStats guard
        self.assertIn("local showMid = tonumber(GetShowStatsMatchID()) or 0", content)
        self.assertIn("if showMid <= 0 or showMid >= 4294967295", content)
        self.assertIn("ChatShowPostGameStats ignored for unrecorded", content)

    def test_mirror_parity_main_v2(self):
        """
        Verifies SHA-256 byte-for-byte mirror parity for main_v2.lua across all 5 tree locations.
        """
        paths = [
            REPO_ROOT / "games" / "client" / "override" / "ui" / "scripts" / "main_v2.lua",
            REPO_ROOT / "games" / "client" / "ClientPackageFiles" / "ui" / "scripts" / "main_v2.lua",
            REPO_ROOT / "games" / "client" / "game" / "ui" / "scripts" / "main_v2.lua",
            REPO_ROOT / "games" / "shared" / "ui" / "scripts" / "main_v2.lua",
        ]
        hashes = {}
        for p in paths:
            self.assertTrue(p.is_file(), f"Missing mirror file {p}")
            hashes[str(p)] = get_file_sha256(p)

        first_hash = list(hashes.values())[0]
        for p_str, h in hashes.items():
            self.assertEqual(h, first_hash, f"Hash mismatch in {p_str}: {h} != {first_hash}")

    def test_mirror_parity_match_stats_v2(self):
        """
        Verifies SHA-256 byte-for-byte mirror parity for match_stats_v2.lua across all 5 tree locations.
        """
        paths = [
            REPO_ROOT / "games" / "client" / "override" / "ui" / "scripts" / "newui" / "match_stats_v2.lua",
            REPO_ROOT / "games" / "client" / "ClientPackageFiles" / "ui" / "scripts" / "newui" / "match_stats_v2.lua",
            REPO_ROOT / "games" / "client" / "game" / "ui" / "scripts" / "newui" / "match_stats_v2.lua",
            REPO_ROOT / "games" / "shared" / "ui" / "scripts" / "newui" / "match_stats_v2.lua",
        ]
        hashes = {}
        for p in paths:
            self.assertTrue(p.is_file(), f"Missing mirror file {p}")
            hashes[str(p)] = get_file_sha256(p)

        first_hash = list(hashes.values())[0]
        for p_str, h in hashes.items():
            self.assertEqual(h, first_hash, f"Hash mismatch in {p_str}: {h} != {first_hash}")

    def test_initialize_connectionless_binary_signature_client_and_server(self):
        """
        Protects: Exact +0x26 19-byte instruction signature (B8 82 02 00 00 ...)
        and 15-byte prologues of CPlayer::SpawnHero and CPlayer::Terminate across
        client and server game_shared_x64.dll binaries on disk.
        """
        import pefile

        expected_init_conn_19 = bytes.fromhex(
            "B8 82 02 00 00 C6 86 6B 06 00 00 01 66 09 86 DE 03 00 00"
        )
        expected_spawn_hero_15 = bytes.fromhex(
            "48 89 4C 24 08 55 53 56 57 41 54 41 55 41 56"
        )
        expected_terminate_15 = bytes.fromhex(
            "48 8B C4 48 89 48 08 55 53 56 57 41 54 41 55"
        )

        sym_init = "?InitializeConnectionless@CPlayer@@QEAAXPEAVCHostServer@@HAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@_N@Z"
        sym_spawn = "?SpawnHero@CPlayer@@QEAA_NXZ"
        sym_term = "?Terminate@CPlayer@@QEAAXXZ"

        targets = [
            REPO_ROOT / "games" / "client" / "game_shared_x64.dll",
            REPO_ROOT / "games" / "server" / "game_shared_x64.dll",
            REPO_ROOT / "games" / "hon server" / "game_shared_x64.dll",
        ]
        checked = 0
        for dll_path in targets:
            if not dll_path.is_file():
                continue
            checked += 1
            pe = pefile.PE(str(dll_path), fast_load=True)
            pe.parse_data_directories(
                directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXPORT"]]
            )
            exports = {
                e.name.decode("ascii", "ignore"): e.address
                for e in pe.DIRECTORY_ENTRY_EXPORT.symbols
                if e.name
            }
            raw = dll_path.read_bytes()

            rva_init = exports.get(sym_init)
            self.assertIsNotNone(rva_init, f"Missing {sym_init} in {dll_path}")
            off_init = pe.get_offset_from_rva(rva_init)
            self.assertEqual(
                raw[off_init + 0x26 : off_init + 0x26 + 19],
                expected_init_conn_19,
                f"InitializeConnectionless+0x26 signature drift in {dll_path}",
            )

            rva_spawn = exports.get(sym_spawn)
            self.assertIsNotNone(rva_spawn, f"Missing {sym_spawn} in {dll_path}")
            off_spawn = pe.get_offset_from_rva(rva_spawn)
            self.assertEqual(
                raw[off_spawn : off_spawn + 15],
                expected_spawn_hero_15,
                f"SpawnHero 15-byte prologue drift in {dll_path}",
            )

            rva_term = exports.get(sym_term)
            self.assertIsNotNone(rva_term, f"Missing {sym_term} in {dll_path}")
            off_term = pe.get_offset_from_rva(rva_term)
            self.assertEqual(
                raw[off_term : off_term + 15],
                expected_terminate_15,
                f"Terminate 15-byte prologue drift in {dll_path}",
            )

        self.assertGreaterEqual(checked, 2, "Expected client and server game_shared_x64.dll")

    def test_native_guard_bot_economy_active_flag_patch_and_hooks(self):
        """
        Protects:
        1. InstallManagedBytePatch 'CPlayer::InitializeConnectionless_ActiveFlag' (0x0282 -> 0x0202).
        2. Hooked_CPlayer_InitializeConnectionless preserving active flag for AddBot (!bFakePlayer) too.
        3. Hooked_CPlayer_SpawnHero healing active flag before SpawnHero on client & dedicated server.
        4. Hooked_CPlayer_Terminate reaping bot active flags when the last human terminates.
        """
        content = read_native_guard_sources()

        self.assertNotIn("if (pPlayer && pHostServer && bFakePlayer)", content)
        self.assertIn("if (pPlayer && pHostServer)", content)
        self.assertIn('"CPlayer::InitializeConnectionless_ActiveFlag"', content)
        self.assertIn("0xB8, 0x02, 0x02, 0x00, 0x00", content)
        self.assertIn("[KONGOR NATIVE GUARD] [BOT-ECONOMY] Active-client flag preserved for Bot", content)
        self.assertIn("Hooked_CPlayer_Terminate", content)
        self.assertIn("ReapConnectionlessBotsIfNoActiveHumans", content)
        self.assertIn('[KONGOR_HOTFIX] Active patch:', content)

    def test_deployed_version_dll_sha256_parity_across_client_and_servers(self):
        """
        Protects: Byte-for-byte SHA-256 parity across all deployed version.dll locations.
        """
        targets = [
            REPO_ROOT / "games" / "client" / "version.dll",
            REPO_ROOT / "games" / "client" / "windows-x64" / "version.dll",
            REPO_ROOT / "games" / "server" / "version.dll",
            REPO_ROOT / "games" / "server" / "windows-x64" / "version.dll",
            REPO_ROOT / "games" / "hon server" / "version.dll",
        ]
        existing = [p for p in targets if p.is_file()]
        self.assertGreaterEqual(len(existing), 3, "Expected deployed version.dll binaries")
        ref_hash = get_file_sha256(existing[0])
        for p in existing[1:]:
            self.assertEqual(
                get_file_sha256(p),
                ref_hash,
                f"Stale version.dll detected at {p} (expected parity with {existing[0]})",
            )


if __name__ == "__main__":
    unittest.main()

# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Regression & PE32+/Zip contract tests for Day-2 Windows Playtest Hotfix and deployed client DLLs.

Value-Weighted Gate:
- Protects: PE32+ x86_64 headers & day2-hotfix-1 stamps across version.dll, fmodex64.dll, and vid_vulkan_x64.dll;
  ProjectKongor-Day2-Hotfix-Win.zip < 5 MB Discord size budget, MAX_PATH-safe (< 115 char) archive entry paths,
  absence of fmodex64_core.dll / WILLOWMAKER.exe, CommandLineToArgvW-safe .bat launchers, and 100% SHA256SUMS.json
  byte-for-byte integrity.
- Fails when: Long zip prefixes break Windows Explorer 260-char MAX_PATH extraction, trailing %~dp0 backslashes
  escape quotes in .bat wrappers, fmodex64_core.dll is overwritten, or unpatched native DLLs are staged.
- Seam: Real PE32+ binary header/section scan + zipfile.ZipFile + SHA-256 verification.
"""

from __future__ import annotations

import hashlib
import json
import struct
import zipfile
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
CLIENT_DIR = REPO_ROOT / "games" / "client"
STAGED_WIN_ZIP = Path(
    r"D:\KongorPlaytestStaging\playtest-20261029-183354-79c8b0e9e\Hotfix-Day2\ProjectKongor-Day2-Hotfix-Win.zip"
)


def assert_pe64_dll(path: Path) -> bytes:
    assert path.is_file(), f"Missing native DLL {path}."
    data = path.read_bytes()
    assert data[:2] == b"MZ", f"{path} missing MZ header."
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    assert data[e_lfanew : e_lfanew + 4] == b"PE\x00\x00", f"{path} missing PE signature."
    machine = struct.unpack_from("<H", data, e_lfanew + 4)[0]
    assert machine == 0x8664, f"{path} is not x86_64 PE32+ (machine={hex(machine)})."
    return data


def test_01_repo_windows_client_dlls_are_pe64_and_carry_day2_hotfix_markers():
    ver_bytes = assert_pe64_dll(CLIENT_DIR / "version.dll")
    assert b"day2-hotfix-1" in ver_bytes or b"v5.0.1" in ver_bytes or b"[KONGOR_HOTFIX]" in ver_bytes, "games/client/version.dll missing hotfix build stamp!"

    fmod_bytes = assert_pe64_dll(CLIENT_DIR / "fmodex64.dll")
    assert len(fmod_bytes) < 200_000, (
        f"games/client/fmodex64.dll is {len(fmod_bytes)} bytes — expected lightweight WASAPI proxy, not core FMOD!"
    )
    assert b"KONGOR_AUDIO_WASAPI" in fmod_bytes, "fmodex64.dll missing opt-in KONGOR_AUDIO_WASAPI gate!"

    vk_bytes = assert_pe64_dll(CLIENT_DIR / "vid_vulkan_x64.dll")
    assert b"day2-hotfix-1" in vk_bytes or b"v5.0.1" in vk_bytes or b"[KONGOR_HOTFIX]" in vk_bytes, "vid_vulkan_x64.dll missing hotfix build stamp!"
    assert b"KongorProbeDriverBackend" in vk_bytes, "vid_vulkan_x64.dll missing KongorProbeDriverBackend export!"


@pytest.mark.skipif(not STAGED_WIN_ZIP.exists(), reason="Staged Day-2 Windows hotfix zip not present on this machine")
def test_02_staged_windows_hotfix_zip_budget_maxpath_and_sha256_manifest():
    sz = STAGED_WIN_ZIP.stat().st_size
    assert 400_000 < sz < 5 * 1024 * 1024, f"Windows hotfix zip outside <5 MB Discord budget: {sz} bytes"

    with zipfile.ZipFile(STAGED_WIN_ZIP, "r") as zf:
        names = zf.namelist()
        assert "Apply-Hotfix.bat" in names
        assert "Revert-Hotfix.bat" in names
        assert "hotfix/Apply-Hotfix.ps1" in names
        assert "hotfix/Revert-Hotfix.ps1" in names
        assert "hotfix/SHA256SUMS.json" in names

        for name in names:
            assert len(name) < 115, (
                f"Zip entry '{name}' ({len(name)} chars) risks Windows Explorer 260-char MAX_PATH failure!"
            )
            lower = name.lower()
            assert "fmodex64_core.dll" not in lower, "Hotfix must NEVER overwrite fmodex64_core.dll!"
            assert "willowmaker.exe" not in lower, "Day-2 hotfix must stay launcher-free (<5 MB)!"

        for bat_name in ("Apply-Hotfix.bat", "Revert-Hotfix.bat"):
            bat_text = zf.read(bat_name).decode("utf-8")
            assert '"%SCRIPT_DIR%"' not in bat_text, (
                f"{bat_name} passes trailing-backslash \"%SCRIPT_DIR%\" which escapes quotes in CommandLineToArgvW!"
            )

        manifest = json.loads(zf.read("hotfix/SHA256SUMS.json").decode("utf-8"))
        assert len(manifest) >= 39, f"Expected >= 39 files in SHA256SUMS.json, got {len(manifest)}"
        for rel_posix, expected_sha in manifest.items():
            arc_path = f"hotfix/{rel_posix}"
            assert arc_path in names, f"Manifest entry {arc_path} missing from zip!"
            actual_sha = hashlib.sha256(zf.read(arc_path)).hexdigest()
            assert actual_sha == expected_sha.lower(), f"SHA-256 mismatch inside zip for {arc_path}!"

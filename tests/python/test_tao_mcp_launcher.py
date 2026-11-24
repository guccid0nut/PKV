#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)
"""
Unit and end-to-end tests for TAO MCP cross-OS .NET 11.0 runtime auto-discovery,
build-fingerprinted shadow caching, dead-PID shadow pruning, and sub-second CLI execution.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
TOOLS_DIR = REPO_ROOT / "src" / "tools"
KONGOR_DIR = TOOLS_DIR / "kongor"

if str(KONGOR_DIR) not in sys.path:
    sys.path.insert(0, str(KONGOR_DIR))
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))

import mcp_server  # noqa: E402


def _make_fake_dotnet_root(
    root: Path,
    *,
    is_windows: bool,
    netcore_versions: list[str],
    sdk_versions: list[str] | None = None,
    include_exe: bool = True,
) -> Path:
    root.mkdir(parents=True, exist_ok=True)
    if include_exe:
        bin_name = "dotnet.exe" if is_windows else "dotnet"
        bin_file = root / bin_name
        bin_file.write_bytes(b"fake-dotnet-host")
    (root / "host" / "fxr" / "11.0.0").mkdir(parents=True, exist_ok=True)
    for ver in netcore_versions:
        (root / "shared" / "Microsoft.NETCore.App" / ver).mkdir(parents=True, exist_ok=True)
    for sdk_ver in sdk_versions or []:
        (root / "sdk" / sdk_ver).mkdir(parents=True, exist_ok=True)
    return root


def test_discover_dotnet_root_windows_prefers_ga_11_over_preview_11(tmp_path: Path) -> None:
    """
    protects: Windows .NET 11.0 hostfxr resolution when C:\\Program Files\\dotnet only has preview.7 while %LOCALAPPDATA%\\Microsoft\\dotnet has GA 11.0.0.
    fails_when: discover_dotnet_root returns preview-only ProgramFiles or trusts a preview DOTNET_ROOT over GA 11.0.0.
    seam: mcp_server.discover_dotnet_root + mcp_server.configure_dotnet_environment over real filesystem trees.
    """
    prog_files = tmp_path / "ProgramFiles"
    local_app = tmp_path / "LocalAppData"
    home_dir = tmp_path / "UserHome"

    preview_root = _make_fake_dotnet_root(
        prog_files / "dotnet",
        is_windows=True,
        netcore_versions=["11.0.0-preview.7.26381.103"],
        sdk_versions=["11.0.100-preview.7"],
    )
    ga_root = _make_fake_dotnet_root(
        local_app / "Microsoft" / "dotnet",
        is_windows=True,
        netcore_versions=["11.0.0", "11.0.1-preview.1"],
        sdk_versions=["11.0.100"],
    )

    env = {
        "DOTNET_ROOT": str(preview_root),
        "DOTNET_ROOT_X64": str(preview_root),
        "LOCALAPPDATA": str(local_app),
        "ProgramFiles": str(prog_files),
        "PATH": rf"{preview_root};C:\Windows\System32",
    }

    resolved_root, version_str, source = mcp_server.configure_dotnet_environment(
        env,
        platform_name="win32",
        home_dir=home_dir,
        which_dotnet="",
        update_process_env=False,
        emit_telemetry=False,
    )

    assert resolved_root == ga_root
    assert version_str == "11.0.0"
    assert source == "LOCALAPPDATA"
    assert env["DOTNET_ROOT"] == str(ga_root)
    assert env["DOTNET_ROOT_X64"] == str(ga_root)
    assert env["PATH"].split(";")[0] == str(ga_root)


def test_discover_dotnet_root_linux_and_macos_layouts(tmp_path: Path) -> None:
    """
    protects: Cross-OS .NET 11.0 auto-discovery and colon-delimited PATH prepending on Linux and macOS.
    fails_when: POSIX ~/.dotnet or symlinked dotnet installations are missed or Windows ';' separator corrupts POSIX PATH.
    seam: mcp_server.configure_dotnet_environment across simulated 'linux' and 'darwin' layouts.
    """
    # 1. Linux ~/.dotnet GA 11.0.2 discovery
    linux_home = tmp_path / "linux_home"
    linux_dotnet = _make_fake_dotnet_root(
        linux_home / ".dotnet",
        is_windows=False,
        netcore_versions=["10.0.5", "11.0.2"],
        sdk_versions=["11.0.102"],
    )
    linux_env = {"HOME": str(linux_home), "PATH": "/usr/local/bin:/usr/bin"}
    root_linux, ver_linux, src_linux = mcp_server.configure_dotnet_environment(
        linux_env,
        platform_name="linux",
        home_dir=linux_home,
        which_dotnet="",
        update_process_env=False,
        emit_telemetry=False,
    )
    assert root_linux == linux_dotnet
    assert ver_linux == "11.0.2"
    assert src_linux == "HOME_DOTNET"
    assert linux_env["DOTNET_ROOT"] == str(linux_dotnet)
    assert linux_env["PATH"] == f"{linux_dotnet}:/usr/local/bin:/usr/bin"

    # 2. macOS Homebrew libexec layout resolved via PATH wrapper parent/libexec
    mac_home = tmp_path / "mac_home"
    mac_home.mkdir(parents=True, exist_ok=True)
    brew_cellar = tmp_path / "opt" / "homebrew" / "Cellar" / "dotnet" / "11.0.0"
    brew_bin = brew_cellar / "bin"
    brew_bin.mkdir(parents=True, exist_ok=True)
    wrapper_dotnet = brew_bin / "dotnet"
    wrapper_dotnet.write_text("#!/bin/sh\n", encoding="utf-8")
    brew_libexec = _make_fake_dotnet_root(
        brew_cellar / "libexec",
        is_windows=False,
        netcore_versions=["11.0.0"],
        include_exe=True,
    )

    mac_env = {"HOME": str(mac_home), "PATH": f"{brew_bin}:/usr/bin"}
    root_mac, ver_mac, _ = mcp_server.configure_dotnet_environment(
        mac_env,
        platform_name="darwin",
        home_dir=mac_home,
        which_dotnet=str(wrapper_dotnet),
        update_process_env=False,
        emit_telemetry=False,
    )
    assert root_mac == brew_libexec
    assert ver_mac == "11.0.0"
    assert mac_env["DOTNET_ROOT"] == str(brew_libexec)
    assert mac_env["PATH"].startswith(f"{brew_libexec}:")


def test_get_newest_build_candidate_supports_windows_and_posix_apphost(tmp_path: Path) -> None:
    """
    protects: Selection of newest Release/Debug build candidate across Windows ('kongor.exe') and Linux/macOS ('kongor').
    fails_when: POSIX apphost binary without '.exe' extension is ignored or older build configuration wins.
    seam: mcp_server.get_newest_build_candidate against synthetic Kongor.Cli bin directories.
    """
    debug_dir = tmp_path / "src" / "tools" / "kongor-cli" / "bin" / "Debug" / "net11.0"
    release_dir = tmp_path / "src" / "tools" / "kongor-cli" / "bin" / "Release" / "net11.0"
    debug_dir.mkdir(parents=True)
    release_dir.mkdir(parents=True)

    (debug_dir / "kongor").write_bytes(b"elf-debug")
    (debug_dir / "kongor.dll").write_bytes(b"dll-debug")
    os.utime(debug_dir / "kongor", (1000, 1000))
    os.utime(debug_dir / "kongor.dll", (1000, 1000))

    (release_dir / "kongor").write_bytes(b"elf-release")
    (release_dir / "kongor.dll").write_bytes(b"dll-release")
    os.utime(release_dir / "kongor", (2000, 2000))
    os.utime(release_dir / "kongor.dll", (2000, 2000))

    mtime, chosen_dir, chosen_target = mcp_server.get_newest_build_candidate(tmp_path, platform_name="linux")
    assert chosen_dir == release_dir
    assert chosen_target == "kongor"
    assert mtime >= 2000


def test_shadow_runtime_warm_cache_and_dead_pid_pruning(tmp_path: Path) -> None:
    """
    protects: Zero-lock MSBuild byte-copy isolation, warm cache_<hash> reuse, and dead-PID shadow directory pruning.
    fails_when: Top-level kongor.exe shares hardlink inode with bin_dir, warm cache_dir is deleted by cleanup_shadow_runtime, or dead pid_* dirs leak.
    seam: mcp_server.prepare_shadow_runtime + cleanup_shadow_runtime + prune_stale_shadows.
    """
    bin_dir = tmp_path / "bin" / "Release" / "net11.0"
    nested_dir = bin_dir / "runtimes" / "win-x64" / "native"
    nested_dir.mkdir(parents=True)
    (bin_dir / "kongor.exe").write_bytes(b"MZ-apphost")
    (bin_dir / "kongor.dll").write_bytes(b"IL-payload")
    (nested_dir / "dep.dll").write_bytes(b"native-dep")

    temp_root = tmp_path / "temp_shadow"
    runtime_base = temp_root / "kongor_mcp_runtime"
    dead_pid_dir = runtime_base / "pid_99999991"
    live_pid_dir = runtime_base / f"pid_{os.getpid()}"
    dead_pid_dir.mkdir(parents=True)
    live_pid_dir.mkdir(parents=True)
    (dead_pid_dir / "stale.txt").write_text("stale", encoding="utf-8")
    (live_pid_dir / "alive.txt").write_text("alive", encoding="utf-8")

    shadow_1 = mcp_server.prepare_shadow_runtime(bin_dir, generation=0, temp_root=temp_root)
    assert shadow_1.name.startswith("cache_")
    assert (shadow_1 / "kongor.exe").is_file()
    assert (shadow_1 / ".ready").is_file()
    assert not dead_pid_dir.exists(), "Dead PID shadow directory must be pruned automatically"
    assert live_pid_dir.exists(), "Current live PID shadow directory must never be pruned"

    # Top-level kongor.exe must be an independent byte-copy (different inode) so MSBuild never hits MSB3021
    assert (shadow_1 / "kongor.exe").stat().st_ino != (bin_dir / "kongor.exe").stat().st_ino

    # cleanup_shadow_runtime must keep shared cache_* directories intact while deleting ephemeral pid_* dirs
    mcp_server.cleanup_shadow_runtime(shadow_1)
    assert shadow_1.is_dir()

    t0 = time.perf_counter()
    shadow_2 = mcp_server.prepare_shadow_runtime(bin_dir, generation=1, temp_root=temp_root)
    warm_ms = (time.perf_counter() - t0) * 1000.0
    assert shadow_2 == shadow_1
    assert warm_ms < 50.0


def _build_stripped_dotnet_env() -> dict[str, str]:
    stripped_env = {
        k: v
        for k, v in os.environ.items()
        if k.upper() not in ("DOTNET_ROOT", "DOTNET_ROOT_X64", "KONGOR_DOTNET_ROOT")
    }
    path_sep = ";" if sys.platform == "win32" else ":"
    stripped_env["PATH"] = path_sep.join(
        p for p in stripped_env.get("PATH", "").split(path_sep) if "dotnet" not in p.lower()
    )
    return stripped_env


@pytest.fixture(scope="module")
def warmed_shadow_runtime() -> Path:
    """One-time module fixture populating the shadow cache and warming OS PE/JIT caches before timing assertions."""
    _, chosen_bin_dir, _ = mcp_server.get_newest_build_candidate(REPO_ROOT)
    assert chosen_bin_dir is not None, (
        "Kongor.Cli build output not found; build with 'dotnet build src/tools/kongor-cli/Kongor.Cli.csproj -c Release'"
    )
    shadow_dir = mcp_server.prepare_shadow_runtime(chosen_bin_dir, generation=0)
    tao_script = REPO_ROOT / "src" / "tools" / "tao_mcp.py"
    subprocess.run(
        [sys.executable, "-u", str(tao_script), "--tool", "kongor_health_check"],
        cwd=str(REPO_ROOT),
        env=_build_stripped_dotnet_env(),
        capture_output=True,
        text=True,
        timeout=15,
    )
    return shadow_dir


def test_live_tao_mcp_health_check_self_heals_and_executes_fast(warmed_shadow_runtime: Path) -> None:
    """
    protects: End-to-end tao_mcp.py .NET 11.0 self-healing when DOTNET_ROOT is unset and < 500ms server-side kongor_health_check execution.
    fails_when: kongor.exe fails hostfxr 11.0 resolution without parent DOTNET_ROOT, or kongor_health_check exceeds 500ms server-side.
    seam: Real subprocess invocation of python -u src/tools/tao_mcp.py --tool kongor_health_check.
    """
    assert warmed_shadow_runtime.is_dir()
    tao_script = REPO_ROOT / "src" / "tools" / "tao_mcp.py"
    stripped_env = _build_stripped_dotnet_env()

    t0 = time.perf_counter()
    hc_proc = subprocess.run(
        [sys.executable, "-u", str(tao_script), "--tool", "kongor_health_check"],
        cwd=str(REPO_ROOT),
        env=stripped_env,
        capture_output=True,
        text=True,
        timeout=15,
    )
    hc_wall_ms = (time.perf_counter() - t0) * 1000.0

    assert hc_proc.returncode == 0, f"kongor_health_check failed: stderr={hc_proc.stderr}"
    assert "DOTNET_ROOT=" in hc_proc.stderr
    hc_json = json.loads(hc_proc.stdout)
    assert hc_json["elapsedMs"] < 500.0, f"Expected health_check elapsedMs < 500ms, got {hc_json['elapsedMs']}ms"
    assert hc_wall_ms < 2000.0, f"Expected warm tao_mcp.py health_check wall-clock < 2000ms, got {hc_wall_ms:.1f}ms"


def test_live_tao_mcp_diagnostics_executes_under_500ms_server_side(warmed_shadow_runtime: Path) -> None:
    """
    protects: Sub-500ms server-side kongor_diagnostics fast-path execution without unbound loopback TCP retry stalls.
    fails_when: ClusterDiagnosticsService stalls on unbound loopback ports or Garnet RESP2 INFO framing times out.
    seam: Real subprocess invocation of python -u src/tools/tao_mcp.py --tool kongor_diagnostics.
    """
    assert warmed_shadow_runtime.is_dir()
    tao_script = REPO_ROOT / "src" / "tools" / "tao_mcp.py"
    stripped_env = _build_stripped_dotnet_env()

    t0 = time.perf_counter()
    diag_proc = subprocess.run(
        [sys.executable, "-u", str(tao_script), "--tool", "kongor_diagnostics"],
        cwd=str(REPO_ROOT),
        env=stripped_env,
        capture_output=True,
        text=True,
        timeout=15,
    )
    diag_wall_ms = (time.perf_counter() - t0) * 1000.0

    assert diag_proc.returncode == 0, f"kongor_diagnostics failed: stderr={diag_proc.stderr}"
    diag_json = json.loads(diag_proc.stdout)
    assert diag_json["executionTimeMs"] < 500.0, (
        f"Expected kongor_diagnostics executionTimeMs < 500ms, got {diag_json['executionTimeMs']}ms"
    )
    assert diag_wall_ms < 2000.0, f"Expected warm tao_mcp.py diagnostics wall-clock < 2000ms, got {diag_wall_ms:.1f}ms"

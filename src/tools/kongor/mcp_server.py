#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)
"""
TAO (Together as One) - KONGOR MCP Server Launcher Shim.
Named after Empath's signature ultimate ability 'Together as One' (TAO),
uniting the game client, dedicated servers, Aspire cluster, and developer tooling into a cohesive whole.

Directs all MCP JSON-RPC 2.0 communication directly to the single canonical
.NET 11 implementation in Kongor.Cli (src/tools/kongor-cli), eliminating
dual-stack drift while preserving full backwards compatibility.
"""

from __future__ import annotations

import hashlib
import os
import re
import shutil
import signal
import subprocess
import sys
import time
from collections.abc import Mapping, MutableMapping
from pathlib import Path


def find_repo_root() -> Path:
    current = Path(__file__).resolve().parent
    for parent in [current] + list(current.parents):
        if (parent / "Directory.Build.props").is_file() or (parent / "ProjectKongor.slnx").is_file():
            return parent
    return Path(__file__).resolve().parents[2]


_SEMVER_RE = re.compile(r"^(\d+)\.(\d+)\.(\d+)(?:-(.+))?$")


def _parse_netcore_11_version(version_name: str) -> tuple[int, int, int, int, str] | None:
    """
    Parse a Microsoft.NETCore.App directory name (e.g. '11.0.0' or '11.0.0-preview.7.26381.103').
    Returns a sortable tuple where GA stable releases (is_stable=1) strictly outrank prereleases (is_stable=0).
    """
    match = _SEMVER_RE.match(version_name.strip())
    if not match:
        return None
    major, minor, patch, prerelease = (
        int(match.group(1)),
        int(match.group(2)),
        int(match.group(3)),
        match.group(4) or "",
    )
    if major != 11:
        return None
    is_stable = 1 if not prerelease else 0
    return (is_stable, major, minor, patch, prerelease)


def _score_dotnet_candidate(
    candidate: Path,
    source: str,
    priority_rank: int,
    platform_name: str,
) -> tuple[tuple[int, int, int, int, int, int, str, int], str | None] | None:
    """
    Evaluate whether `candidate` is a usable .NET host root and score it.
    Score order (descending):
      1. Runtime tier: 2 = .NET 11 GA, 1 = .NET 11 preview/rc, 0 = non-11 dotnet fallback
      2. Patch/minor version of Microsoft.NETCore.App 11.x
      3. Presence of matching 11.x SDK directory
      4. Candidate source priority (explicit env > user-local > system > PATH)
    """
    try:
        if not candidate.is_dir():
            return None
    except OSError:
        return None

    dotnet_bin_name = "dotnet.exe" if platform_name == "win32" else "dotnet"
    has_dotnet_exe = (candidate / dotnet_bin_name).is_file()
    has_hostfxr = (candidate / "host" / "fxr").is_dir()
    shared_app_dir = candidate / "shared" / "Microsoft.NETCore.App"
    has_shared_app = shared_app_dir.is_dir()

    if not has_dotnet_exe and not (has_hostfxr and has_shared_app):
        return None

    best_v11: tuple[int, int, int, int, str] | None = None
    best_v11_str: str | None = None
    if has_shared_app:
        try:
            for entry in shared_app_dir.iterdir():
                if not entry.is_dir():
                    continue
                parsed = _parse_netcore_11_version(entry.name)
                if parsed is not None and (best_v11 is None or parsed > best_v11):
                    best_v11 = parsed
                    best_v11_str = entry.name
        except OSError:
            pass

    has_sdk_11 = 0
    sdk_dir = candidate / "sdk"
    if sdk_dir.is_dir():
        try:
            for entry in sdk_dir.iterdir():
                if entry.is_dir() and entry.name.startswith("11."):
                    has_sdk_11 = 1
                    break
        except OSError:
            pass

    if best_v11 is not None:
        is_stable, major, minor, patch, prerelease = best_v11
        tier = 2 if is_stable else 1
        score = (tier, major, minor, patch, int(has_dotnet_exe), has_sdk_11, prerelease, -priority_rank)
        return score, best_v11_str

    if has_dotnet_exe:
        score = (0, 0, 0, 0, 1, has_sdk_11, "", -priority_rank)
        return score, None

    return None


def discover_dotnet_root(
    *,
    platform_name: str | None = None,
    env: Mapping[str, str] | None = None,
    home_dir: Path | None = None,
    which_dotnet: str | None = None,
) -> tuple[Path | None, str | None, str]:
    """
    Auto-discover the best .NET 11.0 runtime/SDK root across Windows, Linux, and macOS.
    Returns (dotnet_root_path, netcore_11_version_or_none, source_label).
    """
    plat = platform_name or sys.platform
    env_map = env if env is not None else os.environ
    resolved_home = home_dir
    if resolved_home is None:
        user_profile = env_map.get("USERPROFILE") or env_map.get("HOME")
        resolved_home = Path(user_profile) if user_profile else Path.home()

    raw_candidates: list[tuple[Path, str]] = []

    kongor_override = env_map.get("KONGOR_DOTNET_ROOT", "").strip()
    if kongor_override:
        raw_candidates.append((Path(kongor_override), "KONGOR_DOTNET_ROOT"))

    existing_dotnet_root = env_map.get("DOTNET_ROOT", "").strip()
    if existing_dotnet_root:
        raw_candidates.append((Path(existing_dotnet_root), "DOTNET_ROOT"))

    if plat == "win32":
        local_app_data = env_map.get("LOCALAPPDATA", "").strip()
        if local_app_data:
            raw_candidates.append((Path(local_app_data) / "Microsoft" / "dotnet", "LOCALAPPDATA"))
        else:
            raw_candidates.append((resolved_home / "AppData" / "Local" / "Microsoft" / "dotnet", "HOME_LOCALAPPDATA"))

        raw_candidates.append((resolved_home / ".dotnet", "USERPROFILE_DOTNET"))

        prog_files = env_map.get("ProgramFiles", r"C:\Program Files").strip()
        if prog_files:
            raw_candidates.append((Path(prog_files) / "dotnet", "ProgramFiles"))
        raw_candidates.append((Path(r"C:\Program Files\dotnet"), "WinSystemProgramFiles"))
    elif plat == "darwin":
        raw_candidates.append((resolved_home / ".dotnet", "HOME_DOTNET"))
        raw_candidates.append((Path("/opt/homebrew/opt/dotnet/libexec"), "HomebrewArm64"))
        raw_candidates.append((Path("/usr/local/share/dotnet"), "MacSystemShare"))
        raw_candidates.append((Path("/usr/local/opt/dotnet/libexec"), "HomebrewX64"))
    else:
        raw_candidates.append((resolved_home / ".dotnet", "HOME_DOTNET"))
        raw_candidates.append((Path("/usr/share/dotnet"), "LinuxUsrShare"))
        raw_candidates.append((Path("/usr/lib/dotnet"), "LinuxUsrLib"))
        raw_candidates.append((Path("/usr/lib64/dotnet"), "LinuxUsrLib64"))
        raw_candidates.append((Path("/usr/local/share/dotnet"), "LinuxUsrLocalShare"))
        raw_candidates.append((Path("/snap/dotnet-sdk/current"), "LinuxSnap"))

    resolved_which = which_dotnet if which_dotnet is not None else shutil.which("dotnet", path=env_map.get("PATH"))
    if resolved_which:
        which_path = Path(resolved_which)
        raw_candidates.append((which_path.parent, "PATH_WHICH"))
        try:
            real_parent = which_path.resolve().parent
            if real_parent != which_path.parent:
                raw_candidates.append((real_parent, "PATH_SYMLINK_TARGET"))
            # Homebrew wrapper scripts in /opt/homebrew/bin/dotnet keep host/fxr in ../opt/dotnet/libexec
            libexec_sibling = real_parent.parent / "libexec"
            if libexec_sibling.is_dir():
                raw_candidates.append((libexec_sibling, "PATH_LIBEXEC"))
        except OSError:
            pass

    seen: set[str] = set()
    scored: list[tuple[tuple[int, int, int, int, int, int, str, int], Path, str | None, str]] = []

    for idx, (cand_path, source_label) in enumerate(raw_candidates):
        norm_key = str(cand_path).rstrip("/\\")
        if plat == "win32":
            norm_key = norm_key.lower()
        if not norm_key or norm_key in seen:
            continue
        seen.add(norm_key)

        # If KONGOR_DOTNET_ROOT is explicitly set and valid, honor it directly
        evaluated = _score_dotnet_candidate(cand_path, source_label, idx, plat)
        if evaluated is None:
            continue
        score, version_str = evaluated
        if source_label == "KONGOR_DOTNET_ROOT":
            return cand_path, version_str, source_label
        scored.append((score, cand_path, version_str, source_label))

    if not scored:
        return None, None, "not_found"

    scored.sort(key=lambda item: item[0], reverse=True)
    _, best_root, best_ver, best_source = scored[0]
    return best_root, best_ver, best_source


_last_logged_dotnet_key: str | None = None


def configure_dotnet_environment(
    target_env: MutableMapping[str, str] | None = None,
    *,
    platform_name: str | None = None,
    home_dir: Path | None = None,
    which_dotnet: str | None = None,
    update_process_env: bool = True,
    emit_telemetry: bool = True,
) -> tuple[Path | None, str | None, str]:
    """
    Ensure DOTNET_ROOT and PATH point to a valid .NET 11.0 runtime before spawning kongor / dotnet.
    Self-heals missing or preview-only system DOTNET_ROOT across Windows, Linux, and macOS.
    """
    global _last_logged_dotnet_key
    plat = platform_name or sys.platform
    path_sep = ";" if plat == "win32" else ":"
    env_ref: MutableMapping[str, str] = target_env if target_env is not None else os.environ

    dotnet_root, version_str, source = discover_dotnet_root(
        platform_name=plat,
        env=env_ref,
        home_dir=home_dir,
        which_dotnet=which_dotnet,
    )

    if dotnet_root is None:
        if emit_telemetry and _last_logged_dotnet_key != "missing":
            _last_logged_dotnet_key = "missing"
            sys.stderr.write(
                "[Kongor-MCP Launcher] Warning: Could not auto-discover a .NET 11.0 installation; "
                "relying on ambient host environment. Install .NET 11 SDK or set DOTNET_ROOT.\n"
            )
            sys.stderr.flush()
        return None, None, source

    root_str = str(dotnet_root)
    previous_root = env_ref.get("DOTNET_ROOT", "")
    env_ref["DOTNET_ROOT"] = root_str
    if "DOTNET_ROOT_X64" in env_ref:
        env_ref["DOTNET_ROOT_X64"] = root_str
    env_ref.setdefault("DOTNET_ROLL_FORWARD", "LatestMinor")
    env_ref.setdefault("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
    env_ref.setdefault("DOTNET_NOLOGO", "1")

    current_path = env_ref.get("PATH", "")
    raw_entries = [p for p in current_path.split(path_sep) if p]
    norm_root = root_str.rstrip("/\\").lower() if plat == "win32" else root_str.rstrip("/")
    filtered_entries = [
        entry
        for entry in raw_entries
        if (entry.rstrip("/\\").lower() if plat == "win32" else entry.rstrip("/")) != norm_root
    ]
    new_path = path_sep.join([root_str] + filtered_entries)
    env_ref["PATH"] = new_path

    if update_process_env and env_ref is not os.environ:
        os.environ["DOTNET_ROOT"] = root_str
        if "DOTNET_ROOT_X64" in os.environ:
            os.environ["DOTNET_ROOT_X64"] = root_str
        os.environ["PATH"] = new_path

    log_key = f"{root_str}|{version_str}|{source}"
    if emit_telemetry and _last_logged_dotnet_key != log_key:
        _last_logged_dotnet_key = log_key
        healed_note = (
            f", healed_from={previous_root!r}"
            if previous_root and previous_root.rstrip("/\\").lower() != root_str.rstrip("/\\").lower()
            else ""
        )
        sys.stderr.write(
            f"[Kongor-MCP Launcher] .NET runtime configured: DOTNET_ROOT={root_str} "
            f"(netcore={version_str or 'fallback'}, source={source}{healed_note})\n"
        )
        sys.stderr.flush()

    return dotnet_root, version_str, source


_global_job_handle = None

def setup_windows_job_object(proc: subprocess.Popen) -> None:
    """
    On Windows, assign child process to a Job Object configured with
    JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE so that terminating the parent
    (e.g., closing IDE, killing Python host) immediately terminates the child.
    """
    global _global_job_handle
    if sys.platform != "win32":
        return
    try:
        import ctypes
        from ctypes import wintypes

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        job_object_limit_kill_on_job_close = 0x2000
        job_object_limit_breakaway_ok = 0x0800
        job_object_silent_breakaway_ok = 0x1000
        job_object_extended_limit_information = 9

        class JobObjectBasicLimitInformation(ctypes.Structure):
            _fields_ = [
                ("PerProcessUserTimeLimit", wintypes.LARGE_INTEGER),
                ("PerJobUserTimeLimit", wintypes.LARGE_INTEGER),
                ("LimitFlags", wintypes.DWORD),
                ("MinimumWorkingSetSize", ctypes.c_size_t),
                ("MaximumWorkingSetSize", ctypes.c_size_t),
                ("ActiveProcessLimit", wintypes.DWORD),
                ("Affinity", ctypes.c_size_t),
                ("PriorityClass", wintypes.DWORD),
                ("SchedulingClass", wintypes.DWORD),
            ]

        class IoCounters(ctypes.Structure):
            _fields_ = [
                ("ReadOperationCount", ctypes.c_uint64),
                ("WriteOperationCount", ctypes.c_uint64),
                ("OtherOperationCount", ctypes.c_uint64),
                ("ReadTransferCount", ctypes.c_uint64),
                ("WriteTransferCount", ctypes.c_uint64),
                ("OtherTransferCount", ctypes.c_uint64),
            ]

        class JobObjectExtendedLimitInformation(ctypes.Structure):
            _fields_ = [
                ("BasicLimitInformation", JobObjectBasicLimitInformation),
                ("IoInfo", IoCounters),
                ("ProcessMemoryLimit", ctypes.c_size_t),
                ("JobMemoryLimit", ctypes.c_size_t),
                ("PeakProcessMemoryLimit", ctypes.c_size_t),
                ("PeakJobMemoryLimit", ctypes.c_size_t),
            ]

        _global_job_handle = kernel32.CreateJobObjectW(None, None)
        if not _global_job_handle:
            return
        info = JobObjectExtendedLimitInformation()
        info.BasicLimitInformation.LimitFlags = (
            job_object_limit_kill_on_job_close | job_object_limit_breakaway_ok | job_object_silent_breakaway_ok
        )
        if kernel32.SetInformationJobObject(
            _global_job_handle,
            job_object_extended_limit_information,
            ctypes.byref(info),
            ctypes.sizeof(info),
        ):
            kernel32.AssignProcessToJobObject(_global_job_handle, int(proc._handle))
    except Exception as ex:
        sys.stderr.write(f"[Kongor-MCP Launcher] Warning: setup_windows_job_object failed: {ex}\n")


def is_process_alive(pid: int) -> bool:
    """Accurately check whether `pid` refers to a currently running OS process."""
    if pid <= 0:
        return False
    if pid == os.getpid():
        return True

    if sys.platform == "win32":
        try:
            import ctypes
            from ctypes import wintypes

            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
            kernel32.OpenProcess.restype = wintypes.HANDLE
            kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
            kernel32.WaitForSingleObject.restype = wintypes.DWORD
            kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
            kernel32.CloseHandle.restype = wintypes.BOOL

            synchronize = 0x00100000
            wait_timeout = 0x00000102
            h_proc = kernel32.OpenProcess(synchronize, False, pid)
            if not h_proc:
                # ERROR_ACCESS_DENIED (5) indicates the PID exists at higher elevation
                return ctypes.get_last_error() == 5
            try:
                return kernel32.WaitForSingleObject(h_proc, 0) == wait_timeout
            finally:
                kernel32.CloseHandle(h_proc)
        except Exception as ex:
            sys.stderr.write(f"[Kongor-MCP Launcher] Debug: Win32 liveness check failed for pid={pid}: {ex}\n")
            return False

    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except Exception:
        return False


def cleanup_shadow_runtime(shadow_dir: Path | None) -> None:
    """
    Clean up ephemeral per-PID shadow directories on process exit while preserving
    immutable build-fingerprinted `cache_*` shadow directories for sub-millisecond reuse.
    """
    if shadow_dir is None or not shadow_dir.is_dir():
        return
    if shadow_dir.name.startswith("cache_"):
        return
    try:
        shutil.rmtree(shadow_dir, ignore_errors=True)
    except Exception as ex:
        sys.stderr.write(f"[Kongor-MCP Launcher] Debug: cleanup_shadow_runtime notice for {shadow_dir}: {ex}\n")


def prune_stale_shadows(base_dir: Path, max_cached_builds: int = 3) -> None:
    """Removes leftover per-PID shadow directories from dead processes and trims old build caches."""
    if not base_dir.is_dir():
        return

    cache_dirs: list[tuple[float, Path]] = []

    for item in base_dir.iterdir():
        if not item.is_dir():
            continue
        name = item.name
        if name.startswith("pid_") or name.startswith("staging_pid_"):
            parts = name.split("_")
            pid_idx = 2 if name.startswith("staging_pid_") else 1
            try:
                pid = int(parts[pid_idx])
                if is_process_alive(pid):
                    continue
            except (IndexError, ValueError) as ex:
                sys.stderr.write(f"[Kongor-MCP Launcher] Debug: Malformed shadow dir {name}: {ex}\n")
            try:
                shutil.rmtree(item, ignore_errors=True)
            except Exception as ex:
                sys.stderr.write(f"[Kongor-MCP Launcher] Warning: Failed to remove stale shadow {item}: {ex}\n")
        elif name.startswith("cache_"):
            try:
                ready_marker = item / ".ready"
                mtime = ready_marker.stat().st_mtime if ready_marker.is_file() else item.stat().st_mtime
                cache_dirs.append((mtime, item))
            except OSError:
                pass

    if len(cache_dirs) > max_cached_builds:
        cache_dirs.sort(key=lambda pair: pair[0], reverse=True)
        for _, stale_cache in cache_dirs[max_cached_builds:]:
            try:
                shutil.rmtree(stale_cache, ignore_errors=True)
            except Exception as ex:
                sys.stderr.write(f"[Kongor-MCP Launcher] Debug: Deferred trimming active cache {stale_cache}: {ex}\n")


def compute_build_fingerprint(bin_dir: Path) -> str:
    """Compute a fast deterministic fingerprint over top-level build artifacts in `bin_dir`."""
    hasher = hashlib.sha256()
    hasher.update(str(bin_dir.resolve()).encode("utf-8", errors="replace"))
    entries: list[tuple[str, int, int]] = []
    try:
        with os.scandir(bin_dir) as it:
            for entry in it:
                if entry.is_file():
                    st = entry.stat()
                    entries.append((entry.name, st.st_mtime_ns, st.st_size))
    except OSError:
        pass
    entries.sort(key=lambda item: item[0])
    for name, mtime_ns, size in entries:
        hasher.update(f"{name}:{mtime_ns}:{size};".encode("utf-8"))
    return hasher.hexdigest()[:16]


def prepare_shadow_runtime(bin_dir: Path, generation: int = 0, temp_root: Path | None = None) -> Path:
    """
    Prepares bin_dir into an isolated shadow directory in TEMP.
    Uses a build-fingerprinted cache (`cache_<sha256[:16]>`) populated via atomic per-PID staging
    so warm starts complete in < 2ms with zero file copies, while byte-copying top-level and
    first-party outputs (shutil.copy2) so Windows ImageSectionObject locks never block 'dotnet build'.
    """
    t0 = time.perf_counter()
    resolved_temp = temp_root if temp_root is not None else Path(os.environ.get("TEMP", os.environ.get("TMP", "/tmp")))
    temp_base = resolved_temp / "kongor_mcp_runtime"
    temp_base.mkdir(parents=True, exist_ok=True)
    prune_stale_shadows(temp_base)

    fingerprint = compute_build_fingerprint(bin_dir)
    cache_dir = temp_base / f"cache_{fingerprint}"
    ready_marker = cache_dir / ".ready"
    has_payload = any(
        (cache_dir / candidate_name).is_file()
        for candidate_name in ("kongor.exe", "kongor", "kongor.dll", "Kongor.Cli.dll")
    )

    if ready_marker.is_file() and has_payload:
        elapsed_ms = (time.perf_counter() - t0) * 1000.0
        sys.stderr.write(
            f"[Kongor-MCP Launcher] Shadow runtime cache hit in {elapsed_ms:.1f}ms (dir={cache_dir})\n"
        )
        sys.stderr.flush()
        return cache_dir

    staging_dir = temp_base / f"staging_pid_{os.getpid()}_gen_{generation}_{time.time_ns()}"
    staging_dir.mkdir(parents=True, exist_ok=True)

    copied_count = 0
    linked_count = 0

    for item in bin_dir.rglob("*"):
        if not item.is_file():
            continue
        rel = item.relative_to(bin_dir)
        # Skip nested publish/ or artifacts/ output trees inside bin_dir
        if rel.parts and rel.parts[0].lower() in ("publish", "artifacts"):
            continue

        dest = staging_dir / rel
        dest.parent.mkdir(parents=True, exist_ok=True)

        # Always byte-copy top-level assemblies and first-party Kongor files so NTFS
        # hardlinks do not share kernel SECTION_OBJECT_POINTERS with MSBuild outputs.
        is_top_level = len(rel.parts) == 1
        is_first_party = (
            item.name.lower().startswith("kongor")
            or item.name == "kongor"
            or item.suffix.lower() in (".exe", ".pdb", ".json", ".config")
        )

        if is_top_level or is_first_party:
            shutil.copy2(item, dest)
            copied_count += 1
        else:
            try:
                os.link(item, dest)
                linked_count += 1
            except Exception:
                shutil.copy2(item, dest)
                copied_count += 1

    # Ensure POSIX apphost binary retains executable permission bit
    posix_apphost = staging_dir / "kongor"
    if sys.platform != "win32" and posix_apphost.is_file():
        try:
            posix_apphost.chmod(posix_apphost.stat().st_mode | 0o111)
        except OSError as ex:
            sys.stderr.write(f"[Kongor-MCP Launcher] Warning: chmod +x failed on {posix_apphost}: {ex}\n")

    (staging_dir / ".ready").write_text(f"{fingerprint}\n", encoding="utf-8")

    target_dir = cache_dir
    try:
        os.replace(staging_dir, cache_dir)
    except OSError:
        if (cache_dir / ".ready").is_file():
            shutil.rmtree(staging_dir, ignore_errors=True)
            target_dir = cache_dir
        else:
            fallback_suffix = f"pid_{os.getpid()}" if generation == 0 else f"pid_{os.getpid()}_gen_{generation}"
            fallback_dir = temp_base / fallback_suffix
            if fallback_dir.exists():
                shutil.rmtree(fallback_dir, ignore_errors=True)
            os.replace(staging_dir, fallback_dir)
            target_dir = fallback_dir

    elapsed_ms = (time.perf_counter() - t0) * 1000.0
    sys.stderr.write(
        f"[Kongor-MCP Launcher] Shadow runtime ready in {elapsed_ms:.1f}ms "
        f"(copied={copied_count}, hardlinked={linked_count}, dir={target_dir})\n"
    )
    sys.stderr.flush()
    return target_dir


def _resolve_dotnet_executable(env: Mapping[str, str] | None = None) -> str:
    env_map = env if env is not None else os.environ
    dotnet_root = env_map.get("DOTNET_ROOT", "").strip()
    dotnet_bin_name = "dotnet.exe" if sys.platform == "win32" else "dotnet"
    if dotnet_root:
        candidate = Path(dotnet_root) / dotnet_bin_name
        if candidate.is_file():
            return str(candidate)
    resolved = shutil.which("dotnet", path=env_map.get("PATH"))
    return resolved if resolved else "dotnet"


def get_newest_build_candidate(repo_root: Path, platform_name: str | None = None) -> tuple[float, Path | None, str | None]:
    plat = platform_name or sys.platform
    apphost_name = "kongor.exe" if plat == "win32" else "kongor"
    cli_dir = repo_root / "src" / "tools" / "kongor-cli"
    candidates: list[tuple[float, Path, str]] = []
    for conf in ["Release", "Debug"]:
        bin_dir = cli_dir / "bin" / conf / "net11.0"
        dll_mtime = 0.0
        for dll_name in ["kongor.dll", "Kongor.Cli.dll"]:
            dll_file = bin_dir / dll_name
            if dll_file.is_file():
                dll_mtime = max(dll_mtime, dll_file.stat().st_mtime)
        apphost_file = bin_dir / apphost_name
        if apphost_file.is_file():
            mtime = max(apphost_file.stat().st_mtime, dll_mtime)
            candidates.append((mtime, bin_dir, apphost_name))
        elif dll_mtime > 0.0:
            target_dll = "kongor.dll" if (bin_dir / "kongor.dll").is_file() else "Kongor.Cli.dll"
            candidates.append((dll_mtime, bin_dir, target_dll))

    if candidates:
        candidates.sort(key=lambda item: item[0], reverse=True)
        return candidates[0]
    return 0.0, None, None


def find_mcp_command(
    repo_root: Path,
    generation: int = 0,
    env: MutableMapping[str, str] | None = None,
) -> tuple[list[str], Path | None]:
    configure_dotnet_environment(env, update_process_env=(env is None or env is os.environ))
    dotnet_cmd = _resolve_dotnet_executable(env)
    apphost_name = "kongor.exe" if sys.platform == "win32" else "kongor"

    cli_dir = repo_root / "src" / "tools" / "kongor-cli"
    proj_path = cli_dir / "Kongor.Cli.csproj"

    _, chosen_bin_dir, chosen_target = get_newest_build_candidate(repo_root)
    if chosen_bin_dir is not None and chosen_target is not None:
        try:
            shadow_dir = prepare_shadow_runtime(chosen_bin_dir, generation=generation)
            if chosen_target == apphost_name:
                return [str(shadow_dir / chosen_target), "mcp"], shadow_dir
            return [dotnet_cmd, "exec", str(shadow_dir / chosen_target), "mcp"], shadow_dir
        except Exception as ex:
            sys.stderr.write(f"[Kongor-MCP Launcher] Warning: prepare_shadow_runtime failed, falling back to direct execution: {ex}\n")
            if chosen_target == apphost_name:
                return [str(chosen_bin_dir / chosen_target), "mcp"], None
            return [dotnet_cmd, "exec", str(chosen_bin_dir / chosen_target), "mcp"], None

    # Fallback to dotnet run
    return [dotnet_cmd, "run", "--project", str(proj_path), "--", "mcp"], None


def main() -> int:
    import threading

    repo_root = find_repo_root()
    extra_args = sys.argv[1:]
    env = os.environ.copy()
    env["PYTHONUNBUFFERED"] = "1"
    configure_dotnet_environment(env, update_process_env=True)

    # Non-stdio or one-shot flags use direct pass-through
    is_direct_mode = (
        env.get("KONGOR_MCP_DIRECT_STDIO") == "1"
        or any(arg in ("--register-only", "--help", "-h", "--version", "http", "sse", "streamable-http") for arg in extra_args)
    )

    if is_direct_mode:
        base_cmd, shadow_runtime_dir = find_mcp_command(repo_root, generation=0, env=env)
        full_cmd = base_cmd + extra_args
        try:
            proc = subprocess.Popen(
                full_cmd,
                stdin=sys.stdin,
                stdout=sys.stdout,
                stderr=sys.stderr,
                cwd=str(repo_root),
                env=env,
            )
            setup_windows_job_object(proc)
            ret = proc.wait()
            return 0 if ret == 0 else (ret if ret > 0 else 1)
        except Exception as ex:
            sys.stderr.write(f"[Kongor-MCP Launcher] Failed to launch canonical .NET MCP server: {ex}\n")
            return 1
        finally:
            cleanup_shadow_runtime(shadow_runtime_dir)

    # Hot-reloading stdio supervisor mode: keeps IDE sys.stdin/sys.stdout open permanently
    # and transparently swaps shadow kongor.exe whenever a newer build is detected on disk.
    generation = 0
    active_mtime, _, _ = get_newest_build_candidate(repo_root)
    base_cmd, shadow_runtime_dir = find_mcp_command(repo_root, generation=generation, env=env)
    full_cmd = base_cmd + extra_args

    proc: subprocess.Popen | None = None
    stdout_thread: threading.Thread | None = None
    stop_stdout_event = threading.Event()
    init_request_line: bytes | None = None
    initialized_notif_line: bytes | None = None
    signal_terminated = False

    def start_stdout_forwarder(child_proc: subprocess.Popen, stop_evt: threading.Event) -> threading.Thread:
        def _pump() -> None:
            try:
                assert child_proc.stdout is not None
                while not stop_evt.is_set():
                    line = child_proc.stdout.readline()
                    if not line:
                        break
                    sys.stdout.buffer.write(line)
                    sys.stdout.buffer.flush()
            except Exception as ex:
                if not stop_evt.is_set():
                    sys.stderr.write(f"[Kongor-MCP Launcher] Debug: stdout pump ended: {ex}\n")

        t = threading.Thread(target=_pump, name=f"kongor-mcp-stdout-gen-{generation}", daemon=True)
        t.start()
        return t

    def stop_child(child_proc: subprocess.Popen | None, old_shadow: Path | None, stop_evt: threading.Event) -> None:
        stop_evt.set()
        if child_proc is not None:
            try:
                if child_proc.stdin:
                    child_proc.stdin.close()
            except Exception as ex:
                sys.stderr.write(f"[Kongor-MCP Launcher] Debug: stdin close notice: {ex}\n")
            try:
                if child_proc.poll() is None:
                    child_proc.terminate()
                    child_proc.wait(timeout=2.0)
            except Exception as ex:
                sys.stderr.write(f"[Kongor-MCP Launcher] Debug: child terminate notice: {ex}\n")
                try:
                    child_proc.kill()
                except Exception as kex:
                    sys.stderr.write(f"[Kongor-MCP Launcher] Debug: child kill notice: {kex}\n")
        cleanup_shadow_runtime(old_shadow)

    try:
        proc = subprocess.Popen(
            full_cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=sys.stderr,
            cwd=str(repo_root),
            env=env,
        )
        setup_windows_job_object(proc)
        stdout_thread = start_stdout_forwarder(proc, stop_stdout_event)

        def handle_signal(signum, frame):
            nonlocal signal_terminated
            signal_terminated = True
            stop_child(proc, shadow_runtime_dir, stop_stdout_event)

        try:
            signal.signal(signal.SIGINT, handle_signal)
            signal.signal(signal.SIGTERM, handle_signal)
        except (ValueError, AttributeError) as ex:
            sys.stderr.write(f"[Kongor-MCP Launcher] Debug: signal registration skipped: {ex}\n")

        for raw_line in sys.stdin.buffer:
            if signal_terminated:
                break

            stripped = raw_line.strip()
            if stripped:
                if b'"initialize"' in stripped and b'"method"' in stripped and init_request_line is None:
                    init_request_line = raw_line
                elif b'"notifications/initialized"' in stripped and initialized_notif_line is None:
                    initialized_notif_line = raw_line

            # Check if a newer build exists on disk or if child exited unexpectedly
            newest_mtime, _, _ = get_newest_build_candidate(repo_root)
            child_dead = proc is None or proc.poll() is not None
            build_updated = newest_mtime > (active_mtime + 0.5) and init_request_line is not None

            if child_dead or build_updated:
                reason = "newer build detected on disk" if build_updated else "child process exited"
                generation += 1
                sys.stderr.write(
                    f"[Kongor-MCP Launcher] Hot-reloading shadow MCP child (gen={generation}, reason={reason})...\n"
                )
                sys.stderr.flush()

                stop_child(proc, shadow_runtime_dir, stop_stdout_event)
                active_mtime = newest_mtime
                base_cmd, shadow_runtime_dir = find_mcp_command(repo_root, generation=generation, env=env)
                full_cmd = base_cmd + extra_args
                stop_stdout_event = threading.Event()

                proc = subprocess.Popen(
                    full_cmd,
                    stdin=subprocess.PIPE,
                    stdout=subprocess.PIPE,
                    stderr=sys.stderr,
                    cwd=str(repo_root),
                    env=env,
                )
                setup_windows_job_object(proc)

                # Replay cached MCP initialization handshake transparently if this isn't an initialize call itself
                if init_request_line is not None and raw_line != init_request_line:
                    try:
                        assert proc.stdin is not None and proc.stdout is not None
                        proc.stdin.write(init_request_line)
                        proc.stdin.flush()
                        _ = proc.stdout.readline()  # Consume replayed initialize response
                        if initialized_notif_line is not None and raw_line != initialized_notif_line:
                            proc.stdin.write(initialized_notif_line)
                            proc.stdin.flush()
                    except Exception as ex:
                        sys.stderr.write(f"[Kongor-MCP Launcher] Warning: Handshake replay failed on gen {generation}: {ex}\n")

                stdout_thread = start_stdout_forwarder(proc, stop_stdout_event)

            try:
                assert proc is not None and proc.stdin is not None
                proc.stdin.write(raw_line)
                proc.stdin.flush()
            except Exception as ex:
                sys.stderr.write(f"[Kongor-MCP Launcher] Warning: Failed writing request to child stdin: {ex}\n")

        # EOF on sys.stdin: close child stdin and allow in-flight response to flush
        if proc is not None and proc.stdin is not None:
            try:
                proc.stdin.close()
            except Exception as ex:
                sys.stderr.write(f"[Kongor-MCP Launcher] Debug: final stdin close notice: {ex}\n")

        if stdout_thread is not None:
            stdout_thread.join(timeout=5.0)

        if proc is not None:
            try:
                ret = proc.wait(timeout=5.0)
            except Exception:
                proc.terminate()
                ret = 0
            if ret == 0 or signal_terminated:
                return 0
            sys.stderr.write(f"[Kongor-MCP Launcher] Child MCP server exited with non-zero code {ret}\n")
            sys.stderr.flush()
            return ret if ret > 0 else 1
        return 0
    except Exception as ex:
        sys.stderr.write(f"[Kongor-MCP Launcher] Failed to launch canonical .NET MCP server: {ex}\n")
        sys.stderr.write(f"[Kongor-MCP Launcher] Command was: {full_cmd!r} (cwd={repo_root})\n")
        sys.stderr.flush()
        return 1
    finally:
        stop_child(proc, shadow_runtime_dir, stop_stdout_event)


if __name__ == "__main__":
    sys.exit(main())


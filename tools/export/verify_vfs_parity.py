#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)
"""
Project Kongor Standalone Playtest Export Tooling:
  1. sanitize-staged : Scrubs developer auto-login hashes & local OS username checks
                       in staged copies ONLY (source workspace stays 100% untouched).
  2. pack-tier       : Packs eligible loose files in-tier into strict 32-bit K2 .s2z
                       archives (< 1.45 GB, < 60,000 entries, allowZip64=False, 2-char
                       suffixes '00'..'99' or 'a0'..'z9' matching CFileManager::RegisterArchives
                       k2_x64.dll + 0x891100), preserving game/textures.s2z FN-003 loose overrides.
  3. write-manifests : Generates playtest_policy.json and build_manifest.json (camelCase,
                       matching WillowmakerJsonSerializerContext) with .s2z SHA-256 hashes
                       and winning gameplay entity CRC32.
  4. verify-parity   : Models the exact K2 VFS mount chain and asserts 100.00% CRC32 and
                       winning-tier parity between source games/client + games/shared and
                       the staged export bundle.
  5. scan-secrets    : Deep-scans all loose files AND streams every entry inside every .s2z
                       archive for forbidden source/symbol/crash-uploader artifacts, hardcoded
                       credentials, MD5 login hashes, API tokens, and local user paths.
"""

from __future__ import annotations

import argparse
import binascii
import datetime as dt
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import time
from typing import Any, Sequence
import warnings
import xml.etree.ElementTree as ET
import zipfile

MAX_K2_ARCHIVE_HARD_LIMIT = 1_500_000_000
DEFAULT_PACK_TARGET_LIMIT = 1_450_000_000
MAX_NON_ZIP64_ENTRIES = 60_000
SINGLE_FILE_PACK_CAP = 1_400_000_000
ZIP64_EOCD_SIG = b"PK\x06\x06"
ZIP64_EOCD_LOCATOR_SIG = b"PK\x06\x07"
EMPTY_STRING_MD5 = "d41d8cd98f00b204e9800998ecf8427e"

# Pre-compressed media stored with ZIP_STORED for fast zero-inflation K2 reads;
# XML/Lua/entity/model/tga/wav assets deflated with ZIP_DEFLATED.
STORED_MEDIA_EXTENSIONS = frozenset({
    ".dds",
    ".png",
    ".jpg",
    ".jpeg",
    ".ogg",
    ".mp3",
    ".zip",
    ".s2z",
    ".bin",
    ".lut",
    ".ttf",
    ".otf",
})

STREAMING_VIDEO_EXTENSIONS = frozenset({
    ".mp4",
    ".avi",
    ".webm",
    ".bik",
    ".ogv",
})

NATIVE_BINARY_EXTENSIONS = frozenset({
    ".dll",
    ".exe",
    ".so",
    ".dylib",
    ".local",
})

EARLY_BOOT_ROOT_FILES = frozenset({
    "8bit",
    "non8bit",
    "descriptor",
    "checksums",
    # [L2-P0-07] Quarantined retail S2 manifest.xml / *.manifest.xml.zip(.disabled) intentionally omitted
    "compat_resource_ignore.txt",
    "graphicsdriver.info",
    "core.resources",
    "game.resources",
    "init.cfg",
    "autoexec.cfg",
    "login.cfg",
    "startup.cfg",
    "default_binds.cfg",
    "customization.cfg",
    "game_settings.cfg",
    "map_settings.cfg",
    "playtest_policy.json",
    "build_manifest.json",
})

GAMEPLAY_ENTITY_EXTENSIONS = frozenset({
    ".entity",
    ".gamemechanics",
    ".upgrades",
})

TEXT_SCAN_EXTENSIONS = frozenset({
    ".cfg",
    ".conf",
    ".ini",
    ".json",
    ".lua",
    ".xml",
    ".interface",
    ".package",
    ".entity",
    ".mdf",
    ".material",
    ".effect",
    ".gamemechanics",
    ".upgrades",
    ".str",
    ".txt",
    ".md",
    ".shader",
    ".psh",
    ".vsh",
    ".h",
    ".sh",
    ".command",
    ".yml",
    ".yaml",
})

FORBIDDEN_BUNDLE_EXTENSIONS = frozenset({
    ".cs",
    ".csproj",
    ".sln",
    ".slnx",
    ".cpp",
    ".cc",
    ".cxx",
    ".c",
    ".hpp",
    ".pdb",
    ".dmp",
    ".mdmp",
    ".ilk",
    ".exp",
    ".lib",
    ".ps1",
    ".py",
    ".bak",
    ".honreplay",
})

LEGACY_VANILLA_BAK_EXEMPT_ARCHIVES = frozenset({
    "resources0.s2z",
    "resources2.s2z",
    "resources94.s2z",
    "resources98.s2z",
})

def _load_runtime_pii() -> tuple[list[str], list[str], list[str], str]:
    dev_users = ["canary_dev_user", "testdev"]
    extra_terms: list[str] = []
    login_scrub_users = ["canary_dev_user", "testdev"]
    host_user_prefix = (
        os.environ.get("KONGOR_HOST_USER_PREFIX", "kongor_host").strip() or "kongor_host"
    )

    env_user = os.environ.get("KONGOR_DEV_USERNAME", "").strip()
    if env_user:
        if env_user.lower() not in [u.lower() for u in dev_users]:
            dev_users.append(env_user)
        if env_user.lower() not in [u.lower() for u in login_scrub_users]:
            login_scrub_users.append(env_user)

    env_denylist = os.environ.get("KONGOR_PII_DENYLIST", "")
    if env_denylist:
        for part in env_denylist.split(","):
            p = part.strip()
            if len(p) >= 3 and p.lower() not in [u.lower() for u in dev_users] and p.lower() not in [t.lower() for t in extra_terms]:
                extra_terms.append(p)

    repo_root = Path(__file__).resolve().parents[2]
    guard_path = repo_root / "config" / "local_pii_guard.local.json"
    if guard_path.is_file():
        try:
            with open(guard_path, "r", encoding="utf-8") as f:
                data = json.load(f)
            allowed = {str(x).strip().lower() for x in data.get("allowed_public_identity", [])}
            for pub_id in allowed:
                if (
                    len(pub_id) >= 3
                    and "@" not in pub_id
                    and pub_id not in [u.lower() for u in login_scrub_users]
                ):
                    login_scrub_users.append(pub_id)
            for term in data.get("terms", []):
                val = str(term if isinstance(term, str) else term.get("value", "")).strip()
                cat = str("" if isinstance(term, str) else term.get("category", "")).strip()
                if not val or len(val) < 3:
                    continue
                if val.lower() in allowed:
                    continue
                if cat == "own_identifier" and "@" not in val and "-" not in val:
                    if val.lower() not in [u.lower() for u in dev_users]:
                        dev_users.append(val)
                    if val.lower() not in [u.lower() for u in login_scrub_users]:
                        login_scrub_users.append(val)
                else:
                    if val.lower() not in [t.lower() for t in extra_terms] and val.lower() not in [u.lower() for u in dev_users]:
                        extra_terms.append(val)
        except Exception as exc:
            print(f"[verify_vfs_parity] Warning: failed to load {guard_path}: {exc}", file=sys.stderr)

    return dev_users, extra_terms, login_scrub_users, host_user_prefix


RUNTIME_DEV_USERNAMES, RUNTIME_EXTRA_PII_TERMS, RUNTIME_LOGIN_SCRUB_USERNAMES, RUNTIME_HOST_USER_PREFIX = _load_runtime_pii()
DEV_USER_PATTERN = "|".join(re.escape(n) for n in RUNTIME_DEV_USERNAMES)
LOGIN_SCRUB_PATTERN = "|".join(re.escape(n) for n in RUNTIME_LOGIN_SCRUB_USERNAMES)


SECRET_SCAN_RULES: list[tuple[str, str, re.Pattern[str]]] = [
    (
        "k2_cfg_login_password",
        "CRED",
        re.compile(
            r"""(?i)\b(?:setsave|set)\s+["']?login_password["']?\s+["']([^"'\r\n]{1,})["']"""
        ),
    ),
    (
        "k2_cfg_login_name",
        "CRED",
        re.compile(
            r"""(?i)\b(?:setsave|set)\s+["']?login_name["']?\s+["']([^"'\r\n]{1,})["']"""
        ),
    ),
    (
        "lua_hardcoded_md5_password",
        "CRED",
        re.compile(
            r"""(?i)\b(?:savedPass|login_password|passHash)\s*=\s*["']([0-9a-f]{32})["']"""
        ),
    ),
    (
        "local_user_path_leak",
        "PATH",
        re.compile(
            r"""(?i)(?:[c-z]:\\users\\[a-z0-9_.-]+|[c-z]:/users/[a-z0-9_.-]+|/users/[a-z0-9_.-]+/pycharmprojects)"""  # kongor-security-ignore
        ),
    ),
    (
        "dev_os_username_check",
        "PII",
        re.compile(rf"""(?i)['"](?:{DEV_USER_PATTERN})[a-zA-Z0-9_]*['"]"""),
    ),
    (
        "github_pat",
        "TOKEN",
        re.compile(r"""\bgithub_pat_[A-Za-z0-9_]{22,}\b"""),
    ),
    (
        "github_classic_token",
        "TOKEN",
        re.compile(r"""\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}\b"""),
    ),
    (
        "aws_access_key",
        "CRED",
        re.compile(r"""\b(?:AKIA|ASIA)[0-9A-Z]{16}\b"""),
    ),
    (
        "jwt_token",
        "JWT",
        re.compile(
            r"""\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b"""
        ),
    ),
    (
        "private_key_pem",
        "KEY",
        re.compile(
            r"""-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----"""
        ),
    ),
    (
        "slack_token",
        "TOKEN",
        re.compile(r"""\bxox[baprs]-[0-9A-Za-z-]{10,}\b"""),
    ),
    (
        "discord_webhook",
        "URL",
        re.compile(
            r"""https://(?:ptb\.|canary\.)?discord(?:app)?\.com/api/webhooks/[0-9]{15,}/[A-Za-z0-9_-]{20,}"""
        ),
    ),
    (
        "openai_api_key",
        "TOKEN",
        re.compile(r"""\bsk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{20,}\b"""),
    ),
    (
        "stripe_api_key",
        "TOKEN",
        re.compile(r"""\b(?:sk|rk)_(?:live|test)_[0-9a-zA-Z]{24,}\b"""),
    ),
    (
        "google_api_key",
        "TOKEN",
        re.compile(r"""\bAIza[0-9A-Za-z_-]{35}\b"""),
    ),
    (
        "kongor_session_cookie",
        "CRED",
        re.compile(
            r"""(?i)\b(?:kongor_session|hon_session|auth_cookie)\s*=\s*["']?[A-Za-z0-9_\-.]{16,}["']?"""
        ),
    ),
    (
        "bearer_token",
        "TOKEN",
        re.compile(r"""(?i)\bbearer\s+[A-Za-z0-9_\-.]{24,}\b"""),
    ),
    (
        "dev_seed_identity",
        "PII",
        re.compile(
            rf"""(?i)[a-z0-9_.+-]+@proton\.me|guest@project\.kongor|host_{re.escape(RUNTIME_HOST_USER_PREFIX)}|@nexus\.local|@kongor\.local"""
        ),
    ),
    (
        "dev_seed_hash",
        "CRED",
        re.compile(
            r"""9fb9cf192701aba7aaa38e6f15457082|AQAAAAIAAYagAAAAEMUkpLAr01Nj"""
        ),
    ),
]

for _term in RUNTIME_EXTRA_PII_TERMS:
    SECRET_SCAN_RULES.append((
        f"runtime_pii_{_term[:12].lower()}",
        "PII",
        re.compile(re.escape(_term), re.IGNORECASE),
    ))

BINARY_SCAN_EXTENSIONS = frozenset({
    ".dll",
    ".exe",
    ".so",
    ".dylib",
})

SKIP_BINARY_MAGIC_EXTENSIONS = frozenset({
    ".s2z",
    ".zip",
    ".dds",
    ".png",
    ".jpg",
    ".jpeg",
    ".tga",
    ".avi",
    ".mp3",
    ".ogg",
    ".wav",
    ".ttf",
    ".otf",
    ".icns",
    ".str",
    ".lua",
    ".xml",
    ".json",
    ".cfg",
    ".txt",
    ".md",
    ".interface",
    ".package",
    ".entity",
    ".material",
    ".mdf",
    ".effect",
    ".shader",
    ".psh",
    ".vsh",
})

MACHO_MAGICS = frozenset({
    b"\xfe\xed\xfa\xcf",
    b"\xcf\xfa\xed\xfe",
    b"\xfe\xed\xfa\xce",
    b"\xce\xfa\xed\xfe",
    b"\xca\xfe\xba\xbe",
    b"\xbe\xba\xfe\xca",
})


def is_binary_candidate(fpath: Path) -> bool:
    name_lower = fpath.name.lower()
    suffix_lower = fpath.suffix.lower()
    if suffix_lower in BINARY_SCAN_EXTENSIONS or ".so." in name_lower:
        return True
    if suffix_lower in SKIP_BINARY_MAGIC_EXTENSIONS:
        return False
    try:
        with fpath.open("rb") as fh:
            header4 = fh.read(4)
        if (
            header4.startswith(b"MZ")
            or header4.startswith(b"\x7fELF")
            or header4 in MACHO_MAGICS
        ):
            return True
    except OSError:
        pass
    return False


def is_posix_executable(fpath: Path, header4: bytes) -> bool:
    name_lower = fpath.name.lower()
    suffix_lower = fpath.suffix.lower()
    if suffix_lower in {".sh", ".command", ".dylib", ".so"} or ".so." in name_lower:
        return True
    if (
        header4.startswith(b"#!")
        or header4.startswith(b"\x7fELF")
        or header4 in MACHO_MAGICS
    ):
        return True
    return False


BINARY_ASCII_RE = re.compile(rb"[\x20-\x7e]{6,}")
BINARY_UTF16LE_RE = re.compile(rb"(?:[\x20-\x7e]\x00){6,}")

BINARY_PII_RULES: list[tuple[str, str, re.Pattern[str]]] = [
    ("binary_users_canary_dev_user", "PII", re.compile(rf"""(?i)users[\\/](?:{DEV_USER_PATTERN})""")),
    ("binary_pycharmprojects", "PII", re.compile(r"""(?i)pycharmprojects""")),
    ("binary_nexus_private", "PII", re.compile(r"""(?i)nexus-private""")),
    (
        "binary_boundary_canary_dev_user",
        "PII",
        re.compile(rf"""(?i)(?:^|[\\/"'\s=:\x00])(?:{DEV_USER_PATTERN})(?:$|[\\/"'\s=:\x00])"""),
    ),
    (
        "dev_seed_identity",
        "PII",
        re.compile(
            rf"""(?i)[a-z0-9_.+-]+@proton\.me|guest@project\.kongor|host_{re.escape(RUNTIME_HOST_USER_PREFIX)}|@nexus\.local|@kongor\.local"""
        ),
    ),
    (
        "dev_seed_hash",
        "CRED",
        re.compile(
            r"""9fb9cf192701aba7aaa38e6f15457082|AQAAAAIAAYagAAAAEMUkpLAr01Nj"""
        ),
    ),
]

for _term in RUNTIME_EXTRA_PII_TERMS:
    BINARY_PII_RULES.append((
        f"runtime_pii_{_term[:12].lower()}",
        "PII",
        re.compile(re.escape(_term), re.IGNORECASE),
    ))

BINARY_USER_PATH_RE = re.compile(
    r"""(?:(?i:[c-z]:[\\/]users[\\/]([a-z0-9_.-]+)[\\/])|/(?:Users|home)/([A-Za-z0-9_.-]+)/)"""
)

VENDOR_BUILD_PREFIX_ALLOWLIST: list[re.Pattern[str]] = [
    re.compile(
        r"""(?i)[c-z]:[\\/]users[\\/]administrator[\\/]desktop[\\/]agent[\\/]_work"""
    ),
    re.compile(
        r"""(?i)(?:[c-z]:[\\/]users[\\/]hyperxewl[\\/]desktop[\\/]cef_binary_|/Users/hyperxewl/(?:Desktop/(?:cef_binary_|curl/|libxml2/)|Downloads/(?:speex-1\.2\.0/|speexdsp-1\.2rc3/)))"""  # kongor-security-ignore
    ),
    re.compile(r"""(?i)/home/(?:dev|developer|michael|runner)/"""),  # kongor-security-ignore
    re.compile(r"""(?i)/home/kekekeks/Projects/MicroCom/"""),  # kongor-security-ignore
    re.compile(r"""(?i)/Users/(?:adam|admin|administrator|caiodottori|nico|raymond)/"""),  # kongor-security-ignore
    re.compile(r"""(?i)/Users/runner/work/"""),  # kongor-security-ignore
]

JSON_SECRET_KEY_RE = re.compile(
    r"""(?i)(?:password|passwd|secret|auth[_-]?token|access[_-]?token|refresh[_-]?token|bearer[_-]?token|session[_-]?token|api[_-]?key|private[_-]?key|connection[_-]?string|basic[_-]?auth|(?:^|[_-])token$)"""
)


def scan_binary_payload(
    origin_label: str,
    raw_bytes: bytes,
    findings: list[dict[str, Any]],
) -> None:
    ascii_strings = [m.decode("latin-1") for m in BINARY_ASCII_RE.findall(raw_bytes)]
    u16_strings = [
        m.decode("utf-16le", errors="ignore")
        for m in BINARY_UTF16LE_RE.findall(raw_bytes)
    ]
    all_strings = ascii_strings + u16_strings

    for s in all_strings:
        s_lower = s.lower()
        if (
            not any(u.lower() in s_lower for u in RUNTIME_DEV_USERNAMES)
            and not any(t.lower() in s_lower for t in RUNTIME_EXTRA_PII_TERMS)
            and "pycharm" not in s_lower
            and "nexus-private" not in s_lower
            and "users" not in s_lower
            and "home/" not in s_lower
            and "proton.me" not in s_lower
            and "kongor.local" not in s_lower
            and "nexus.local" not in s_lower
            and f"host_{RUNTIME_HOST_USER_PREFIX.lower()}" not in s_lower
            and "project.kongor" not in s_lower
            and "9fb9cf19" not in s_lower
            and "aqaaaai" not in s_lower
        ):
            continue

        for rule_info in BINARY_PII_RULES:
            if len(rule_info) == 3:
                rule_name, category, rx = rule_info
            else:
                rule_name, rx = rule_info
                category = "PII"
            if rule_name == "dev_seed_identity":
                if "MERRICK.DatabaseContext" in origin_label:
                    continue
                if any(
                    asm in origin_label
                    for asm in (
                        "NEXUS.ControlPanel.GUI",
                        "KONGOR.MasterServer",
                        "ZORGATH.WebPortal.API",
                    )
                ):
                    match = rx.search(s)
                    if match and match.group(0).lower() == "@kongor.local" and (
                        "admin@kongor.local" in s_lower or "preview@kongor.local" in s_lower
                    ):
                        continue
                    if match:
                        findings.append({
                            "location": origin_label,
                            "rule": rule_name,
                            "category": category,
                            "maskedSnippet": mask_snippet(match.group(0)),
                        })
                    continue
            match = rx.search(s)
            if match:
                findings.append({
                    "location": origin_label,
                    "rule": rule_name,
                    "category": category,
                    "maskedSnippet": mask_snippet(match.group(0)),
                })

        user_match = BINARY_USER_PATH_RE.search(s)
        if user_match:
            is_exempt = any(vrx.search(s) for vrx in VENDOR_BUILD_PREFIX_ALLOWLIST)
            if not is_exempt:
                findings.append({
                    "location": origin_label,
                    "rule": "binary_local_user_path_leak",
                    "category": "PATH",
                    "maskedSnippet": mask_snippet(user_match.group(0)),
                })


def scan_json_payload(
    origin_label: str,
    data: Any,
    findings: list[dict[str, Any]],
    key_path: str = "",
) -> None:
    if isinstance(data, dict):
        for k, v in data.items():
            k_str = str(k)
            curr_path = f"{key_path}.{k_str}" if key_path else k_str
            if JSON_SECRET_KEY_RE.search(k_str):
                if isinstance(v, str):
                    trimmed = v.strip()
                    if trimmed and trimmed not in {"...", "***"}:
                        findings.append({
                            "location": f"{origin_label}::{curr_path}",
                            "rule": "json_secret_value_detected",
                            "category": "CRED",
                            "maskedSnippet": mask_snippet(trimmed),
                        })
                elif isinstance(v, dict):
                    if "Value" in v and isinstance(v["Value"], str):
                        trimmed_val = v["Value"].strip()
                        if trimmed_val and trimmed_val not in {"...", "***"}:
                            findings.append({
                                "location": f"{origin_label}::{curr_path}.Value",
                                "rule": "json_secret_value_detected",
                                "category": "CRED",
                                "maskedSnippet": mask_snippet(trimmed_val),
                            })
            scan_json_payload(origin_label, v, findings, curr_path)
    elif isinstance(data, list):
        for idx, item in enumerate(data):
            scan_json_payload(origin_label, item, findings, f"{key_path}[{idx}]")


def log(msg: str) -> None:
    stamp = dt.datetime.now(dt.timezone.utc).strftime("%H:%M:%S.%f")[:-3]
    print(f"[VFS-EXPORT {stamp}Z] {msg}", flush=True)


def mask_snippet(raw: str) -> str:
    trimmed = raw.strip()
    if not trimmed:
        return "****"
    if len(trimmed) >= 12:
        return f"{trimmed[:4]}****{trimmed[-4:]}"
    if len(trimmed) >= 5:
        return f"{trimmed[:2]}****{trimmed[-1:]}"
    return "****"


def normalize_vpath(raw_path: str) -> str:
    cleaned = raw_path.replace("\\", "/").lstrip("/")
    while "//" in cleaned:
        cleaned = cleaned.replace("//", "/")
    return cleaned.lower()


def to_extended_path(path: Path) -> Path:
    """Adds Windows \\\\?\\ prefix when path length approaches MAX_PATH (260) to avoid WinError 3."""
    if os.name != "nt":
        return path
    s = str(path if path.is_absolute() else path.resolve())
    if s.startswith("\\\\?\\") or len(s) < 240:
        return path
    if s.startswith("\\\\"):
        return Path("\\\\?\\UNC\\" + s[2:])
    return Path("\\\\?\\" + s)


def compute_file_crc32(path: Path) -> tuple[int, int]:
    crc = 0
    total = 0
    with open(to_extended_path(path), "rb") as fh:
        while True:
            chunk = fh.read(1024 * 1024)
            if not chunk:
                break
            crc = binascii.crc32(chunk, crc)
            total += len(chunk)
    return (crc & 0xFFFFFFFF), total


def compute_file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with open(to_extended_path(path), "rb") as fh:
        while True:
            chunk = fh.read(1024 * 1024)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def is_dev_excluded_source_file(
    rel_posix: str,
    skip_cinematic_videos: bool = False,
    include_test_videos: bool = False,
) -> bool:
    rel_lower = normalize_vpath(rel_posix)
    parts = PurePosixPath(rel_lower).parts
    leaf = PurePosixPath(rel_lower).name
    ext = PurePosixPath(rel_lower).suffix.lower()

    if not leaf:
        return True
    if parts and parts[0] in {"shadercache", "texturecache", "replays", "logs"}:
        return True
    if ext == ".s2z" and leaf.startswith(("shadercache", "texturecache")):
        return True
    if ".bak" in leaf or ".backup" in leaf or leaf.endswith((".old", ".active_prev", ".disabled")):
        return True
    if ".old_" in leaf or ".disabled_" in leaf or leaf.startswith((".disabled_", ".hotreload")):
        return True
    if ".manifest.xml.zip" in leaf or leaf in {"manifest.xml", "manifest.xml.disabled"}:
        return True
    if ext in {".pdb", ".dmp", ".mdmp", ".log", ".honreplay", ".ps1", ".py", ".bat", ".sh"}:
        return True
    if leaf in {
        "kongor_vfs_probe.cfg",
        "moved_to_vault_j.txt",
        "autoexec.cfg",
        "login.cfg",
        "startup.cfg",
        "init.cfg",
        "rollback_journal.json",
        "playtest_policy.json",
        "build_manifest.json",
        "mods_state.json",
        ".ds_store",
    }:
        return True
    if rel_lower == "mods/mods_state.json":
        return True
    # Always exclude legacy dev video (<dev_user>_2.*)
    if any(rel_lower.startswith(f"videos/{dev_u.lower()}_2.") or leaf.startswith(f"{dev_u.lower()}_2.") for dev_u in RUNTIME_LOGIN_SCRUB_USERNAMES):
        return True
    # Include kongor_cinematic.* by default unless skip_cinematic_videos is True
    if skip_cinematic_videos and (
        rel_lower.startswith("videos/kongor_cinematic.")
        or leaf.startswith("kongor_cinematic.")
    ):
        return True
    return False


def should_keep_loose_in_tier(
    tier_name: str,
    rel_posix: str,
    file_size: int,
    textures_s2z_entries: set[str],
) -> tuple[bool, str]:
    rel_lower = normalize_vpath(rel_posix)
    parts = PurePosixPath(rel_lower).parts
    leaf = PurePosixPath(rel_lower).name
    ext = PurePosixPath(rel_lower).suffix.lower()

    if ext == ".s2z":
        return True, "existing_s2z_archive"
    if ext in NATIVE_BINARY_EXTENSIONS:
        return True, "native_binary"
    if ext in STREAMING_VIDEO_EXTENSIONS:
        return True, "streaming_video"
    # K2's D3D9 shader compiler (vid_d3d9_x64.dll D3D_LoadVertexShader @ +0x7c1a0 /
    # D3D_LoadPixelShader @ +0x7dcf0) probes shaders via CFileManager::Exists(..., 0x100000)
    # (k2_x64.dll @ +0x8967c0, flag 0x100000 = FILE_NOARCHIVES) and compiles HLSL sources &
    # #include headers via GetSystemPath + D3DXCompileShaderFromFileW on cold empty userdata/.
    # Every tier's core/shaders/** tree (.vsh, .psh, .shader, .h) MUST remain loose on disk so
    # cold first-boot compilation succeeds and higher-tier shader overrides win over game/.
    if (
        rel_lower.startswith("core/shaders/")
        or "/core/shaders/" in f"/{rel_lower}"
        or ext in {".vsh", ".psh", ".shader"}
    ):
        return True, "k2_file_noarchives_shader"
    if file_size >= SINGLE_FILE_PACK_CAP:
        return True, "exceeds_single_file_32bit_zip_cap"

    if len(parts) == 1 and tier_name.lower() in {"game", "base", "configuration"}:
        if leaf in EARLY_BOOT_ROOT_FILES:
            return True, "early_boot_root_metadata"
        if leaf.startswith("core_") and leaf.endswith(".resources"):
            return True, "early_boot_locale_resources"
        if leaf.startswith("curse_words_") and leaf.endswith(".txt"):
            return True, "early_boot_filter_table"

    if tier_name.lower() == "override" and textures_s2z_entries:
        if rel_lower in textures_s2z_entries or f"00000000/{rel_lower}" in textures_s2z_entries:
            return True, "fn003_textures_s2z_loose_override"

    return False, "pack_eligible"


def load_textures_s2z_entries(
    textures_s2z: Path | Sequence[Path] | None,
) -> set[str]:
    entries: set[str] = set()
    if textures_s2z is None:
        return entries

    paths: list[Path]
    if isinstance(textures_s2z, Path):
        paths = [textures_s2z]
    else:
        paths = [Path(p) for p in textures_s2z if p is not None]

    seen_paths: set[Path] = set()
    for tex_path in paths:
        resolved = tex_path.resolve()
        if resolved in seen_paths:
            continue
        seen_paths.add(resolved)
        if not tex_path.is_file():
            continue
        file_entries = 0
        with zipfile.ZipFile(tex_path, "r") as zf:
            for name in zf.namelist():
                if not name.endswith("/"):
                    norm = normalize_vpath(name)
                    entries.add(norm)
                    file_entries += 1
                    slash_idx = norm.find("/")
                    if slash_idx == 8 and norm[:8].isdigit():
                        entries.add(norm[9:])
        log(f"Loaded {file_entries:,} texture index entries from '{tex_path}'.")
    if len(seen_paths) > 1 and entries:
        log(f"Combined {len(entries):,} unique texture index keys across textures.s2z archive(s).")
    return entries


def generate_two_char_suffixes(style: str) -> list[str]:
    """
    CFileManager::RegisterArchives (k2_x64.dll + 0x891100) checks that the suffix
    between 'resources' and '.s2z' has length 1 or 2 (bVar4 = len in {1, 2}) and
    sorts archive names lexicographically ascending before popping from the back.
      - 'digits': '00'..'99' (for tiers with no pre-existing resources*.s2z)
      - 'alpha' : 'a0'..'z9' (sorts strictly above 'resources99.s2z' in game/ while keeping len == 2!)
    """
    if style == "digits":
        return [f"{i:02d}" for i in range(100)]
    if style == "alpha":
        out: list[str] = []
        for ch_ord in range(ord("a"), ord("z") + 1):
            ch = chr(ch_ord)
            for digit in range(10):
                out.append(f"{ch}{digit}")
        return out
    raise ValueError(f"Unsupported suffix style '{style}'. Expected 'digits' or 'alpha'.")


def assert_strict_32bit_s2z(archive_path: Path) -> dict[str, Any]:
    size_bytes = archive_path.stat().st_size
    if size_bytes >= MAX_K2_ARCHIVE_HARD_LIMIT:
        raise RuntimeError(
            f"Archive '{archive_path}' is {size_bytes:,} bytes, exceeding K2 1.5 GB signed 32-bit ceiling ({MAX_K2_ARCHIVE_HARD_LIMIT:,})."
        )

    with open(archive_path, "rb") as fh:
        tail_seek = max(0, size_bytes - 131072)
        fh.seek(tail_seek)
        tail = fh.read()
        if ZIP64_EOCD_SIG in tail or ZIP64_EOCD_LOCATOR_SIG in tail:
            raise RuntimeError(
                f"Archive '{archive_path}' contains Zip64 EOCD signature; K2 requires strict 32-bit Zip headers (allowZip64=False)."
            )

    entry_crcs: dict[str, tuple[int, int]] = {}
    with zipfile.ZipFile(archive_path, "r") as zf:
        infos = zf.infolist()
        if len(infos) > 65534:
            raise RuntimeError(
                f"Archive '{archive_path}' contains {len(infos):,} entries (> 65,534 16-bit non-Zip64 limit)."
            )
        for info in infos:
            extra = info.extra or b""
            idx = 0
            while idx + 4 <= len(extra):
                header_id = int.from_bytes(extra[idx : idx + 2], "little")
                block_len = int.from_bytes(extra[idx + 2 : idx + 4], "little")
                if header_id == 0x0001:
                    raise RuntimeError(
                        f"Archive '{archive_path}' entry '{info.filename}' contains Zip64 extra field (0x0001)."
                    )
                idx += 4 + block_len
            if not info.is_dir():
                entry_crcs[normalize_vpath(info.filename)] = (
                    info.CRC & 0xFFFFFFFF,
                    info.file_size,
                )

        bad_entry = zf.testzip()
        if bad_entry is not None:
            raise RuntimeError(
                f"Archive '{archive_path}' failed CRC/decompression test at entry '{bad_entry}'."
            )

    return {
        "archive": archive_path.name,
        "sizeBytes": size_bytes,
        "entryCount": len(entry_crcs),
        "entryMap": entry_crcs,
    }


# ---------------------------------------------------------------------------
# Subcommand 1: sanitize-staged
# ---------------------------------------------------------------------------
LUA_DEV_AUTOLOGIN_PASS_RE = re.compile(
    rf"""(?ms)^[ \t]*if\s+\(Empty\(savedPass\)\s+or\s+savedPass\s*==\s*['"]d41d8cd98f00b204e9800998ecf8427e['"]\)\s+and\s+user\s*==\s*['"](?:{LOGIN_SCRUB_PATTERN})['"]\s+then.*?^[ \t]*end"""
)

LUA_DEV_AUTOLOGIN_USER_RE = re.compile(
    rf"""(?ms)^([ \t]*)if\s+Empty\(user\)\s+or\s+user\s*==\s*['"]UnnamedNewbie['"].*?['"](?:{LOGIN_SCRUB_PATTERN})[a-zA-Z0-9_]*['"]\s+then.*?^[ \t]*end"""
)

LOCAL_USER_PATH_SCRUB_RE = re.compile(
    r"""(?i)(?:[c-z]:(?:\\+|/)users(?:\\+|/)[a-z0-9_.-]+|/users/[a-z0-9_.-]+/pycharmprojects)(?:(?:\\+|/)pycharmprojects)?(?:(?:\\+|/)projectkongor)?(?:\\+|/)?"""  # kongor-security-ignore
)

TARGET_SANITIZE_LEAFS = frozenset({
    "main_v2.lua",
    "main.interface",
    "main_v2.interface",
    "main_account.lua",
    "player_hosted_v2.lua",
    "main_matches.lua",
    "store.lua",
    "client_messages.str",
})


def resolve_luac51_binary() -> Path:
    env_override = os.environ.get("KONGOR_LUAC51_PATH", "").strip()
    if env_override:
        env_path = Path(env_override).resolve()
        if env_path.is_file():
            return env_path
        raise RuntimeError(
            f"KONGOR_LUAC51_PATH='{env_override}' does not exist. Unset KONGOR_LUAC51_PATH or point it to luac51.exe."
        )

    repo_root = Path(__file__).resolve().parents[2]
    candidates = (
        repo_root / "native" / "tools" / "luac51" / "build" / "Release" / "luac51.exe",
        repo_root / "native" / "build" / "luac51_build" / "Release" / "luac51.exe",
        repo_root / "artifacts" / "native-binaries" / "windows-x64" / "luac51.exe",
        repo_root / ".tools" / "bin" / "lua" / "luac51.exe",
        *( [Path(p)] if (p := shutil.which("luac51")) else [] ),
    )
    for cand in candidates:
        if cand.is_file():
            return cand
    raise RuntimeError(
        "luac51.exe compiler not found for mandatory staged Lua bytecode verification. "
        "Build it with CMake ('cmake --build native/build --config Release --target luac51') or stage it under 'artifacts/native-binaries/windows-x64/luac51.exe' or '.tools/bin/lua/luac51.exe'."
    )


def _run_luac51_syntax_check(luac51_bin: Path, payload: bytes) -> tuple[int, str]:
    proc = subprocess.run(
        [str(luac51_bin), "-p", "-"],
        input=payload,
        capture_output=True,
        check=False,
    )
    msg = (
        proc.stderr.decode("utf-8", errors="replace")
        or proc.stdout.decode("utf-8", errors="replace")
        or ""
    ).strip()
    return proc.returncode, msg


def _extract_luac_error_reason(luac_msg: str) -> str:
    # luac51 formats errors as "<drive_path>: stdin:<line>: <reason>"
    return re.sub(r"^.*?stdin:\d+:\s*", "", luac_msg).strip()


def validate_sanitized_payload(
    raw_bytes: bytes,
    leaf_lower: str,
    origin_label: str,
    luac51_bin: Path | None,
    orig_archive_bytes: bytes | None = None,
) -> Path | None:
    if leaf_lower.endswith(".lua"):
        if luac51_bin is None:
            luac51_bin = resolve_luac51_binary()
        rc_new, msg_new = _run_luac51_syntax_check(luac51_bin, raw_bytes)
        if rc_new != 0:
            if orig_archive_bytes is not None:
                rc_orig, msg_orig = _run_luac51_syntax_check(
                    luac51_bin, orig_archive_bytes
                )
                reason_orig = _extract_luac_error_reason(msg_orig)
                reason_new = _extract_luac_error_reason(msg_new)
                if rc_orig != 0 and reason_orig and reason_orig == reason_new:
                    log(
                        f"  [WARN] Legacy archive entry '{origin_label}' had pre-existing Lua syntax error "
                        f"prior to sanitization ({reason_orig}); verified sanitization introduced 0 new syntax errors."
                    )
                else:
                    raise RuntimeError(
                        f"Sanitized Lua payload '{origin_label}' failed Lua 5.1 bytecode compilation: "
                        f"{msg_new or 'unknown luac51 error'} (orig_rc={rc_orig}). "
                        f"Inspect tools/export/verify_vfs_parity.py sanitize_staged_text rules."
                    )
            else:
                raise RuntimeError(
                    f"Sanitized Lua payload '{origin_label}' failed Lua 5.1 bytecode compilation: "
                    f"{msg_new or 'unknown luac51 error'}. "
                    f"Inspect tools/export/verify_vfs_parity.py sanitize_staged_text rules."
                )
    elif leaf_lower.endswith((".interface", ".xml", ".package")):
        try:
            ET.fromstring(raw_bytes)
        except ET.ParseError as exc:
            raise RuntimeError(
                f"Sanitized XML payload '{origin_label}' failed XML well-formedness parse: {exc}. "
                f"Inspect tools/export/verify_vfs_parity.py sanitize_staged_text rules."
            ) from exc

    residual_findings: list[dict[str, Any]] = []
    scan_text_payload(
        origin_label,
        raw_bytes.decode("utf-8", errors="ignore"),
        residual_findings,
    )
    if residual_findings:
        first_hit = residual_findings[0]
        raise RuntimeError(
            f"Sanitized payload '{origin_label}' still triggers secret/leak rule '{first_hit['rule']}' "
            f"(match={first_hit['maskedSnippet']}). Expand sanitize_staged_text before exporting."
        )
    return luac51_bin


def sanitize_staged_text(raw: str, leaf_lower: str) -> tuple[str, int]:
    updated = raw
    edits = 0

    if leaf_lower in {"main_v2.lua", "main_account.lua"}:
        updated_next, n1 = LUA_DEV_AUTOLOGIN_PASS_RE.subn(
            "\t-- [PLAYTEST EXPORT] Scrubbed developer auto-login password hash",
            updated,
        )
        updated = updated_next
        edits += n1

        updated_next, n2 = LUA_DEV_AUTOLOGIN_USER_RE.subn(
            r"\1-- [PLAYTEST EXPORT] Preserved clean user login field without developer default override",
            updated,
        )
        updated = updated_next
        edits += n2

    for dev_u in RUNTIME_LOGIN_SCRUB_USERNAMES:
        if dev_u.lower() in updated.lower():
            updated_next, n3 = re.subn(
                rf"""(['"]){re.escape(dev_u)}[a-zA-Z0-9_]*\1""",
                r"\1__default_os_user__\1",
                updated,
                flags=re.IGNORECASE,
            )
            updated = updated_next
            edits += n3
    if False:
        _unused = re.subn(
            r"""(['"])__canary_unused__\1""",
            r"\1__default_os_user__\1",
            updated,
            flags=re.IGNORECASE,
        )
        updated = updated_next
        edits += n3

    updated_next, n_path = LOCAL_USER_PATH_SCRUB_RE.subn("", updated)
    updated = updated_next
    edits += n_path

    if leaf_lower in {"main.interface", "main_v2.interface"}:
        updated_next, n4 = re.subn(
            rf"""(?i)CreateString\(\s*'p2p_nickname'\s*,\s*'(?:{LOGIN_SCRUB_PATTERN})'\s*\)""",
            "CreateString('p2p_nickname', 'Player')",
            updated,
        )
        updated = updated_next
        edits += n4

        updated_next, n5 = re.subn(
            rf"""(?i)(\bnick\s*=\s*)'(?:{LOGIN_SCRUB_PATTERN})'""",
            r"\1'Player'",
            updated,
        )
        updated = updated_next
        edits += n5

        updated_next, n6 = re.subn(
            rf"""(?i)Set\(\s*'login_name'\s*,\s*'(?:{LOGIN_SCRUB_PATTERN})'\s*\)""",
            "Set('login_name', 'Player')",
            updated,
        )
        updated = updated_next
        edits += n6

    if leaf_lower == "client_messages.str":
        updated_next, n7 = re.subn(
            rf"""(?mi)^(client_default_name[ \t]+)(?:{LOGIN_SCRUB_PATTERN})\b""",
            r"\1Player",
            updated,
        )
        updated = updated_next
        edits += n7

    if edits == 0 or updated == raw:
        return raw, 0
    return updated, edits


def sanitize_staged_file(file_path: Path) -> int:
    raw = file_path.read_text(encoding="utf-8", errors="surrogateescape")
    updated, edits = sanitize_staged_text(raw, file_path.name.lower())
    if edits > 0 and updated != raw:
        file_path.write_text(updated, encoding="utf-8", errors="surrogateescape")
    return edits


def sanitize_staged_s2z_archive(
    arch_path: Path,
    tier_name: str,
    luac51_bin: Path | None,
) -> tuple[list[dict[str, Any]], Path | None]:
    replacements_by_idx: dict[int, tuple[int, bytes, str]] = {}
    dropped_by_idx: dict[int, str] = {}
    arch_leaf_lower = arch_path.name.lower()
    scrub_dev_entries = arch_leaf_lower not in LEGACY_VANILLA_BAK_EXEMPT_ARCHIVES

    with zipfile.ZipFile(arch_path, "r") as zf_probe:
        probe_infos = zf_probe.infolist()
        for idx, info in enumerate(probe_infos):
            if info.is_dir():
                continue
            vpath = normalize_vpath(info.filename)
            if scrub_dev_entries and is_dev_excluded_source_file(vpath):
                dropped_by_idx[idx] = vpath
                continue
            if info.file_size > 16 * 1024 * 1024:
                continue
            leaf_lower = PurePosixPath(vpath).name
            if leaf_lower not in TARGET_SANITIZE_LEAFS:
                continue
            orig_bytes = zf_probe.read(info)
            orig_text = orig_bytes.decode("utf-8", errors="surrogateescape")
            updated_text, edits = sanitize_staged_text(orig_text, leaf_lower)
            if edits > 0 and updated_text != orig_text:
                new_bytes = updated_text.encode("utf-8", errors="surrogateescape")
                origin_label = f"{tier_name}/{arch_path.name}::{info.filename}"
                luac51_bin = validate_sanitized_payload(
                    raw_bytes=new_bytes,
                    leaf_lower=leaf_lower,
                    origin_label=origin_label,
                    luac51_bin=luac51_bin,
                    orig_archive_bytes=orig_bytes,
                )
                replacements_by_idx[idx] = (edits, new_bytes, vpath)

    if not replacements_by_idx and not dropped_by_idx:
        return [], luac51_bin

    nlink = arch_path.stat().st_nlink
    if nlink > 1:
        raise RuntimeError(
            f"Refusing to rewrite hard-linked archive '{arch_path}' (st_nlink={nlink}). "
            f"Run sanitize-staged ONLY on an isolated staged bundle or scratch copy, never on source games/client."
        )

    t_arch = time.perf_counter()
    log(
        f"Sanitizing {len(replacements_by_idx)} entry(ies) and scrubbing {len(dropped_by_idx)} dev-excluded entry(ies) "
        f"inside staged archive [{tier_name}] '{arch_path.name}' ({arch_path.stat().st_size / (1024 * 1024):.2f} MB)..."
    )

    tmp_path = arch_path.with_name(f"{arch_path.name}.san_tmp")
    winning_vpath_state: dict[str, dict[str, Any] | None] = {}

    try:
        with warnings.catch_warnings(), zipfile.ZipFile(
            arch_path, "r"
        ) as zf_in, zipfile.ZipFile(
            tmp_path,
            mode="w",
            compression=zipfile.ZIP_DEFLATED,
            allowZip64=False,
            compresslevel=6,
        ) as zf_out:
            warnings.filterwarnings(
                "ignore", message="^Duplicate name:", category=UserWarning
            )
            infos = zf_in.infolist()
            total_infos = len(infos)
            for idx, info in enumerate(infos):
                if idx > 0 and idx % 5000 == 0:
                    log(
                        f"  [{arch_path.name}] rewriting entry {idx:,}/{total_infos:,}..."
                    )
                if idx in dropped_by_idx:
                    scrubbed_vpath = dropped_by_idx[idx]
                    winning_vpath_state.pop(scrubbed_vpath, None)
                    log(
                        f"  Scrubbed dev-excluded archive entry [{tier_name}/{arch_path.name}] '{scrubbed_vpath}'."
                    )
                    continue

                safe_dt = (
                    info.date_time
                    if info.date_time and info.date_time[0] >= 1980
                    else (1980, 1, 1, 0, 0, 0)
                )
                out_info = zipfile.ZipInfo(filename=info.filename, date_time=safe_dt)
                out_info.compress_type = (
                    info.compress_type
                    if info.compress_type in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED)
                    else zipfile.ZIP_DEFLATED
                )
                out_info.external_attr = info.external_attr
                out_info.create_system = info.create_system
                out_info.comment = info.comment
                out_info.extra = b""

                if info.is_dir():
                    zf_out.writestr(out_info, b"", compress_type=zipfile.ZIP_STORED)
                    continue

                vpath = normalize_vpath(info.filename)
                if idx in replacements_by_idx:
                    edits, new_bytes, _ = replacements_by_idx[idx]
                    zf_out.writestr(
                        out_info, new_bytes, compress_type=out_info.compress_type
                    )
                    new_crc = binascii.crc32(new_bytes) & 0xFFFFFFFF
                    rec = {
                        "tier": tier_name,
                        "archive": arch_path.name,
                        "vpath": vpath,
                        "edits": edits,
                        "crc32": f"{new_crc:08x}",
                        "sizeBytes": len(new_bytes),
                    }
                    winning_vpath_state[vpath] = rec
                    log(
                        f"  Sanitized archive entry [{tier_name}/{arch_path.name}] '{vpath}' "
                        f"({edits} replacement(s), crc32={new_crc:08x}, size={len(new_bytes):,})."
                    )
                else:
                    raw_payload = zf_in.read(info)
                    zf_out.writestr(
                        out_info, raw_payload, compress_type=out_info.compress_type
                    )
                    if vpath in winning_vpath_state:
                        # A later untouched duplicate entry in the same archive overrides earlier duplicate in K2
                        winning_vpath_state[vpath] = None

        assert_strict_32bit_s2z(tmp_path)
        os.replace(tmp_path, arch_path)
    except Exception:
        if tmp_path.exists():
            tmp_path.unlink()
        raise

    final_records = [
        rec for rec in winning_vpath_state.values() if rec is not None
    ]
    elapsed_arch = round(time.perf_counter() - t_arch, 2)
    log(
        f"Completed strict 32-bit rewrite of [{tier_name}] '{arch_path.name}' "
        f"({len(final_records)} sanitized winning entry(ies), {len(dropped_by_idx)} scrubbed dev entry(ies), "
        f"{arch_path.stat().st_size / (1024 * 1024):.2f} MB) in {elapsed_arch:.2f}s."
    )
    return final_records, luac51_bin


def cmd_sanitize_staged(args: argparse.Namespace) -> int:
    staged_client = Path(args.staged_client).resolve()
    staged_shared = Path(args.staged_shared).resolve() if args.staged_shared else None

    tier_roots: list[tuple[str, Path]] = []
    if staged_shared and staged_shared.is_dir():
        tier_roots.append(("shared", staged_shared))
    for sub in ("override", "ClientPackageFiles", "game", "base"):
        candidate = staged_client / sub
        if candidate.is_dir():
            tier_roots.append((sub, candidate))

    sanitized_records: list[dict[str, Any]] = []
    archive_sanitized_records: list[dict[str, Any]] = []
    luac51_bin: Path | None = None

    for tier_name, root_dir in tier_roots:
        s2z_candidates: list[Path] = []
        for dirpath, _, filenames in os.walk(root_dir):
            for fname in sorted(filenames):
                fpath = Path(dirpath) / fname
                lower_name = fname.lower()
                if lower_name.endswith(".s2z"):
                    s2z_candidates.append(fpath)
                    continue
                if lower_name not in TARGET_SANITIZE_LEAFS:
                    continue
                rel = normalize_vpath(str(fpath.relative_to(root_dir)))
                edits = sanitize_staged_file(fpath)
                if edits > 0:
                    luac51_bin = validate_sanitized_payload(
                        raw_bytes=fpath.read_bytes(),
                        leaf_lower=lower_name,
                        origin_label=f"{tier_name}/{rel}",
                        luac51_bin=luac51_bin,
                    )
                    crc, size = compute_file_crc32(fpath)
                    log(
                        f"Sanitized staged UI file [{tier_name}] '{rel}' ({edits} replacement(s), crc32={crc:08x})."
                    )
                    sanitized_records.append({
                        "tier": tier_name,
                        "vpath": rel,
                        "edits": edits,
                        "crc32": f"{crc:08x}",
                        "sizeBytes": size,
                    })

        for arch_path in sorted(s2z_candidates, key=lambda p: p.as_posix().lower()):
            arch_recs, luac51_bin = sanitize_staged_s2z_archive(
                arch_path=arch_path,
                tier_name=tier_name,
                luac51_bin=luac51_bin,
            )
            archive_sanitized_records.extend(arch_recs)

    report = {
        "sanitizedCount": len(sanitized_records),
        "records": sanitized_records,
        "archiveSanitizedCount": len(archive_sanitized_records),
        "archiveRecords": archive_sanitized_records,
    }
    if args.json_out:
        out_path = Path(args.json_out).resolve()
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
        log(f"Wrote sanitization manifest to '{out_path}'.")
    return 0


# ---------------------------------------------------------------------------
# Subcommand 2: pack-tier
# ---------------------------------------------------------------------------
def cmd_pack_tier(args: argparse.Namespace) -> int:
    t0 = time.perf_counter()
    tier_dir = Path(args.tier_dir).resolve()
    tier_name = args.tier_name
    max_bytes = int(args.max_archive_bytes)
    if max_bytes <= 0 or max_bytes >= MAX_K2_ARCHIVE_HARD_LIMIT:
        raise ValueError(
            f"--max-archive-bytes must be between 1 and {MAX_K2_ARCHIVE_HARD_LIMIT - 1:,} (got {max_bytes:,})."
        )

    if not tier_dir.is_dir():
        log(f"Tier directory '{tier_dir}' does not exist; nothing to pack.")
        return 0

    raw_textures_arg = args.textures_s2z
    if isinstance(raw_textures_arg, (str, Path)):
        textures_paths = (
            [Path(raw_textures_arg).resolve()] if str(raw_textures_arg).strip() else []
        )
    else:
        textures_paths = [
            Path(p).resolve() for p in (raw_textures_arg or []) if str(p).strip()
        ]
    textures_entries = load_textures_s2z_entries(textures_paths)
    suffixes = generate_two_char_suffixes(args.suffix_style)

    eligible_files: list[tuple[str, Path, int]] = []
    kept_loose: list[dict[str, Any]] = []

    for dirpath, _, filenames in os.walk(tier_dir):
        for fname in filenames:
            fpath = Path(dirpath) / fname
            rel_posix = fpath.relative_to(tier_dir).as_posix()
            fsize = to_extended_path(fpath).stat().st_size
            keep, reason = should_keep_loose_in_tier(
                tier_name=tier_name,
                rel_posix=rel_posix,
                file_size=fsize,
                textures_s2z_entries=textures_entries,
            )
            if keep:
                kept_loose.append({
                    "vpath": normalize_vpath(rel_posix),
                    "reason": reason,
                    "sizeBytes": fsize,
                })
            else:
                eligible_files.append((rel_posix, fpath, fsize))

    eligible_files.sort(key=lambda item: normalize_vpath(item[0]))
    log(
        f"Tier '{tier_name}' scan: {len(eligible_files):,} pack-eligible loose file(s), "
        f"{len(kept_loose):,} file(s) intentionally kept loose."
    )

    if not eligible_files:
        summary = {
            "tierName": tier_name,
            "tierDir": str(tier_dir),
            "packedFilesCount": 0,
            "keptLooseCount": len(kept_loose),
            "createdArchives": [],
            "elapsedSeconds": round(time.perf_counter() - t0, 3),
        }
        if args.json_out:
            out_p = Path(args.json_out).resolve()
            out_p.parent.mkdir(parents=True, exist_ok=True)
            out_p.write_text(json.dumps(summary, indent=2), encoding="utf-8")
        return 0

    # Reserve suffixes that do not collide with any existing file in tier_dir
    available_suffixes = [
        sx for sx in suffixes if not (tier_dir / f"resources{sx}.s2z").exists()
    ]
    if not available_suffixes:
        raise RuntimeError(
            f"No available 2-character archive suffixes remaining in '{tier_dir}'."
        )

    created_archives: list[Path] = []
    source_expected_crc: dict[str, tuple[int, int, Path]] = {}

    shard_idx = 0
    current_zf: zipfile.ZipFile | None = None
    current_raw_fh: Any = None
    current_archive_path: Path | None = None
    current_entries = 0

    def open_next_shard() -> None:
        nonlocal shard_idx, current_zf, current_raw_fh, current_archive_path, current_entries
        if shard_idx >= len(available_suffixes):
            raise RuntimeError(
                f"Exhausted all {len(available_suffixes)} 2-char archive shards in tier '{tier_name}'."
            )
        sx = available_suffixes[shard_idx]
        shard_idx += 1
        current_archive_path = tier_dir / f"resources{sx}.s2z"
        current_raw_fh = open(current_archive_path, "wb")
        current_zf = zipfile.ZipFile(
            current_raw_fh,
            mode="w",
            compression=zipfile.ZIP_DEFLATED,
            allowZip64=False,
            compresslevel=6,
        )
        current_entries = 0
        created_archives.append(current_archive_path)
        log(f"Opened new 32-bit K2 shard '{current_archive_path.name}' in tier '{tier_name}'.")

    def close_current_shard() -> None:
        nonlocal current_zf, current_raw_fh, current_archive_path, current_entries
        if current_zf is not None:
            current_zf.close()
            current_zf = None
        if current_raw_fh is not None:
            current_raw_fh.close()
            current_raw_fh = None
        if current_archive_path is not None:
            final_sz = current_archive_path.stat().st_size
            log(
                f"Closed shard '{current_archive_path.name}' ({current_entries:,} entries, {final_sz / (1024 * 1024):.2f} MB)."
            )
        current_archive_path = None
        current_entries = 0

    try:
        open_next_shard()
        for rel_posix, src_path, raw_size in eligible_files:
            assert current_zf is not None and current_raw_fh is not None
            written_so_far = current_raw_fh.tell()
            estimated_cd_bytes = (current_entries + 1) * 112 + 65536
            if current_entries > 0 and (
                written_so_far + raw_size + estimated_cd_bytes > max_bytes
                or current_entries >= MAX_NON_ZIP64_ENTRIES
            ):
                close_current_shard()
                open_next_shard()
                assert current_zf is not None

            ext = PurePosixPath(rel_posix).suffix.lower()
            compress_type = (
                zipfile.ZIP_STORED
                if ext in STORED_MEDIA_EXTENSIONS
                else zipfile.ZIP_DEFLATED
            )

            crc_val, exact_size = compute_file_crc32(src_path)
            arcname = rel_posix.replace("\\", "/").lstrip("/")
            current_zf.write(
                to_extended_path(src_path),
                arcname=arcname,
                compress_type=compress_type,
            )
            source_expected_crc[normalize_vpath(arcname)] = (crc_val, exact_size, src_path)
            current_entries += 1
    finally:
        close_current_shard()

    # Verify every created .s2z BEFORE deleting a single packed loose file!
    verified_archive_summaries: list[dict[str, Any]] = []
    packed_entry_lookup: dict[str, tuple[int, int]] = {}
    for arch in created_archives:
        info_summary = assert_strict_32bit_s2z(arch)
        packed_entry_lookup.update(info_summary["entryMap"])
        verified_archive_summaries.append({
            "archive": info_summary["archive"],
            "sizeBytes": info_summary["sizeBytes"],
            "entryCount": info_summary["entryCount"],
        })

    for vpath, (expected_crc, expected_sz, _) in source_expected_crc.items():
        got = packed_entry_lookup.get(vpath)
        if got is None:
            raise RuntimeError(
                f"Post-pack verification failed in tier '{tier_name}': missing packed entry '{vpath}'."
            )
        got_crc, got_sz = got
        if got_crc != expected_crc or got_sz != expected_sz:
            raise RuntimeError(
                f"Post-pack CRC mismatch in tier '{tier_name}' for '{vpath}': "
                f"expected crc={expected_crc:08x}/size={expected_sz}, got crc={got_crc:08x}/size={got_sz}."
            )

    # Safe to unlink packed loose files inside staged tier_dir and prune empty subdirectories
    for _, (_, _, staged_loose_file) in source_expected_crc.items():
        to_extended_path(staged_loose_file).unlink()

    for dirpath, _dirnames, _filenames in os.walk(tier_dir, topdown=False):
        dp = Path(dirpath)
        if dp == tier_dir:
            continue
        ext_dp = to_extended_path(dp)
        try:
            if not any(ext_dp.iterdir()):
                ext_dp.rmdir()
        except OSError as exc:
            log(f"Kept non-empty directory '{dp}': {exc}")

    elapsed = round(time.perf_counter() - t0, 3)
    log(
        f"Tier '{tier_name}' packing complete in {elapsed:.2f}s: "
        f"{len(source_expected_crc):,} file(s) -> {len(created_archives)} archive(s), "
        f"{len(kept_loose):,} file(s) kept loose."
    )

    summary = {
        "tierName": tier_name,
        "tierDir": str(tier_dir),
        "packedFilesCount": len(source_expected_crc),
        "keptLooseCount": len(kept_loose),
        "keptLooseSamples": kept_loose[:50],
        "createdArchives": verified_archive_summaries,
        "elapsedSeconds": elapsed,
    }
    if args.json_out:
        out_p = Path(args.json_out).resolve()
        out_p.parent.mkdir(parents=True, exist_ok=True)
        out_p.write_text(json.dumps(summary, indent=2), encoding="utf-8")
    return 0


# ---------------------------------------------------------------------------
# VFS Resolution Model (Shared by write-manifests and verify-parity)
# ---------------------------------------------------------------------------
def list_k2_resources_archives_high_to_low(
    tier_dir: Path,
    strict_two_char_check: bool,
) -> tuple[list[Path], list[str]]:
    """
    Mirrors CFileManager::RegisterArchives (k2_x64.dll + 0x891100):
    1. Globs resources*.s2z (case-insensitive)
    2. Checks suffix length between 'resources' and '.s2z' is 1 or 2 chars
    3. Sorts lowercase filenames lexicographically ascending, then pops from BACK
       (highest lexicographical name wins!).
    """
    violations: list[str] = []
    candidates: list[tuple[str, Path]] = []
    if not tier_dir.is_dir():
        return [], violations

    for child in tier_dir.iterdir():
        if not child.is_file():
            continue
        lower_name = child.name.lower()
        if not (lower_name.startswith("resources") and lower_name.endswith(".s2z")):
            continue
        suffix = lower_name[len("resources") : -len(".s2z")]
        if len(suffix) not in (1, 2):
            msg = (
                f"Invalid K2 archive name '{child}' (suffix '{suffix}' has length {len(suffix)}; "
                f"CFileManager::RegisterArchives k2_x64.dll+0x891100 requires 1 or 2 chars)."
            )
            if strict_two_char_check:
                violations.append(msg)
            continue
        candidates.append((lower_name, child))

    candidates.sort(key=lambda pair: pair[0], reverse=True)
    return [pair[1] for pair in candidates], violations


def build_vfs_winner_map(
    client_root: Path,
    shared_root: Path | None,
    is_source_workspace: bool,
    skip_cinematic_videos: bool = False,
    include_test_videos: bool = False,
) -> tuple[dict[str, tuple[str, int, int, str]], list[str]]:
    """
    Resolves virtual_path -> (tier_name, crc32_int, size_bytes, origin_kind)
    across K2's active mount order (highest to lowest priority):
      override -> KONGOR/extensions -> KONGOR/updates -> KONGOR/configuration
      -> ../shared -> ClientPackageFiles -> game -> base
    """
    tiers_high_to_low: list[tuple[str, Path]] = [
        ("override", client_root / "override"),
        ("KONGOR/extensions", client_root / "KONGOR" / "extensions"),
        ("KONGOR/updates", client_root / "KONGOR" / "updates"),
        ("KONGOR/configuration", client_root / "KONGOR" / "configuration"),
    ]
    if shared_root is not None:
        tiers_high_to_low.append(("shared", shared_root))
    tiers_high_to_low.extend([
        ("ClientPackageFiles", client_root / "ClientPackageFiles"),
        ("game", client_root / "game"),
        ("base", client_root / "base"),
    ])

    winners: dict[str, tuple[str, int, int, str]] = {}
    violations: list[str] = []

    for tier_name, tier_dir in tiers_high_to_low:
        if not tier_dir.is_dir():
            continue

        # 1. Loose files in tier_dir take precedence over archives in the same tier
        loose_files: list[tuple[str, Path]] = []
        for dirpath, _, filenames in os.walk(tier_dir):
            for fname in filenames:
                fpath = Path(dirpath) / fname
                rel_posix = fpath.relative_to(tier_dir).as_posix()
                rel_lower = normalize_vpath(rel_posix)
                if rel_lower.endswith(".s2z"):
                    continue
                if is_source_workspace and is_dev_excluded_source_file(
                    rel_posix,
                    skip_cinematic_videos=skip_cinematic_videos,
                    include_test_videos=include_test_videos,
                ):
                    continue
                if (not is_source_workspace) and PurePosixPath(rel_lower).name in {
                    "init.cfg",
                    "autoexec.cfg",
                    "playtest_policy.json",
                    "build_manifest.json",
                    "mods_state.json",
                }:
                    continue
                loose_files.append((rel_lower, fpath))

        loose_files.sort(key=lambda item: item[0])
        for vpath, fpath in loose_files:
            if vpath not in winners:
                crc, sz = compute_file_crc32(fpath)
                winners[vpath] = (tier_name, crc, sz, "loose")

        # 2. resources*.s2z in reverse lexicographical order
        archives, arch_violations = list_k2_resources_archives_high_to_low(
            tier_dir=tier_dir,
            strict_two_char_check=(not is_source_workspace),
        )
        violations.extend(arch_violations)

        for arch_path in archives:
            if not is_source_workspace:
                sz_arch = arch_path.stat().st_size
                if sz_arch >= MAX_K2_ARCHIVE_HARD_LIMIT and arch_path.name.lower() != "resources94.s2z":
                    # Note: pre-existing vanilla resources94.s2z is 1,546,291,511 bytes (~1.44 GiB < 2^31-1)
                    if sz_arch >= 2_147_483_647:
                        violations.append(
                            f"Archive '{arch_path}' exceeds signed 32-bit int32 max ({sz_arch:,} bytes)."
                        )
            with zipfile.ZipFile(arch_path, "r") as zf:
                # Within a single .s2z, later duplicate entries override earlier ones in K2's zip map
                infos = zf.infolist()
                for info in reversed(infos):
                    if info.is_dir():
                        continue
                    vpath = normalize_vpath(info.filename)
                    if is_source_workspace and is_dev_excluded_source_file(
                        vpath,
                        skip_cinematic_videos=skip_cinematic_videos,
                        include_test_videos=include_test_videos,
                    ):
                        continue
                    if (not is_source_workspace) and is_dev_excluded_source_file(
                        vpath,
                        skip_cinematic_videos=skip_cinematic_videos,
                        include_test_videos=include_test_videos,
                    ):
                        # Ignore legacy .bak/.backup entries inside untouched base/game S2 archives
                        if arch_path.name.lower() in LEGACY_VANILLA_BAK_EXEMPT_ARCHIVES:
                            continue
                    if vpath not in winners:
                        winners[vpath] = (
                            tier_name,
                            info.CRC & 0xFFFFFFFF,
                            info.file_size,
                            f"s2z:{arch_path.name}",
                        )

        # 3. <tier>/textures.s2z fallback within each tier (KONGOR/updates/textures.s2z Shard 1 & game/textures.s2z Shard 2)
        tex_arch = tier_dir / "textures.s2z"
        if tex_arch.is_file():
            with zipfile.ZipFile(tex_arch, "r") as zf:
                for info in reversed(zf.infolist()):
                    if info.is_dir():
                        continue
                    vpath = normalize_vpath(info.filename)
                    if vpath not in winners:
                        winners[vpath] = (
                            tier_name,
                            info.CRC & 0xFFFFFFFF,
                            info.file_size,
                            "s2z:textures.s2z",
                        )

    return winners, violations


# ---------------------------------------------------------------------------
# Subcommand 3: verify-parity
# ---------------------------------------------------------------------------
def cmd_verify_parity(args: argparse.Namespace) -> int:
    t0 = time.perf_counter()
    source_client = Path(args.source_client).resolve()
    source_shared = Path(args.source_shared).resolve() if args.source_shared else None
    staged_client = Path(args.staged_client).resolve()
    staged_shared = Path(args.staged_shared).resolve() if args.staged_shared else None

    loose_sanitized_expected: dict[tuple[str, str], tuple[str, int | None]] = {}
    archive_sanitized_expected: dict[tuple[str, str, str], tuple[str, int | None]] = {}
    if args.sanitized_manifest and Path(args.sanitized_manifest).is_file():
        san_data = json.loads(
            Path(args.sanitized_manifest).read_text(encoding="utf-8")
        )
        for rec in san_data.get("records", []):
            vp = normalize_vpath(rec["vpath"])
            sz_opt = int(rec["sizeBytes"]) if "sizeBytes" in rec else None
            loose_sanitized_expected[(rec["tier"], vp)] = (
                rec["crc32"].lower(),
                sz_opt,
            )
        for rec in san_data.get("archiveRecords", []):
            vp = normalize_vpath(rec["vpath"])
            arch_origin = f"s2z:{str(rec['archive']).lower()}"
            sz_opt = int(rec["sizeBytes"]) if "sizeBytes" in rec else None
            archive_sanitized_expected[(rec["tier"], arch_origin, vp)] = (
                rec["crc32"].lower(),
                sz_opt,
            )

    skip_cin = bool(getattr(args, "skip_cinematic_videos", False))
    inc_vid = bool(getattr(args, "include_test_videos", False))

    log("Building VFS winning-entry map for SOURCE workspace...")
    src_winners, src_violations = build_vfs_winner_map(
        client_root=source_client,
        shared_root=source_shared,
        is_source_workspace=True,
        skip_cinematic_videos=skip_cin,
        include_test_videos=inc_vid,
    )

    log("Building VFS winning-entry map for STAGED export bundle...")
    stg_winners, stg_violations = build_vfs_winner_map(
        client_root=staged_client,
        shared_root=staged_shared,
        is_source_workspace=False,
        skip_cinematic_videos=skip_cin,
        include_test_videos=inc_vid,
    )

    missing_in_staged: list[str] = []
    extra_in_staged: list[str] = []
    crc_mismatches: list[dict[str, Any]] = []
    tier_mismatches: list[dict[str, Any]] = []
    fn003_violations: list[str] = []
    loose_shader_violations: list[str] = []

    all_vpaths = sorted(set(src_winners.keys()) | set(stg_winners.keys()))
    for vp in all_vpaths:
        s_entry = src_winners.get(vp)
        d_entry = stg_winners.get(vp)
        if s_entry is None:
            extra_in_staged.append(vp)
            continue
        if d_entry is None:
            missing_in_staged.append(vp)
            continue

        s_tier, s_crc, s_sz, s_origin = s_entry
        d_tier, d_crc, d_sz, d_origin = d_entry

        if s_tier != d_tier:
            tier_mismatches.append({
                "vpath": vp,
                "sourceTier": s_tier,
                "stagedTier": d_tier,
                "sourceOrigin": s_origin,
                "stagedOrigin": d_origin,
            })

        vp_ext = PurePosixPath(vp).suffix.lower()
        if s_origin == "loose" and (
            vp.startswith("core/shaders/")
            or "/core/shaders/" in f"/{vp}"
            or vp_ext in {".vsh", ".psh", ".shader"}
        ):
            if d_origin != "loose":
                loose_shader_violations.append(
                    f"FILE_NOARCHIVES shader violation: '{vp}' (tier '{s_tier}') must remain loose on disk for cold-boot D3D9 shader compilation, got '{d_origin}'."
                )

        loose_key = (d_tier, vp)
        arch_key = (d_tier, d_origin.lower(), vp)
        if s_origin == "loose" and loose_key in loose_sanitized_expected:
            expected_san_crc, expected_san_sz = loose_sanitized_expected[loose_key]
            if f"{d_crc:08x}" != expected_san_crc or (
                expected_san_sz is not None and d_sz != expected_san_sz
            ):
                crc_mismatches.append({
                    "vpath": vp,
                    "reason": "sanitized_crc_mismatch",
                    "expectedSanitizedCrc32": expected_san_crc,
                    "stagedCrc32": f"{d_crc:08x}",
                    "expectedSanitizedSize": expected_san_sz,
                    "stagedSize": d_sz,
                })
        elif s_origin != "loose" and arch_key in archive_sanitized_expected:
            expected_san_crc, expected_san_sz = archive_sanitized_expected[arch_key]
            if f"{d_crc:08x}" != expected_san_crc or (
                expected_san_sz is not None and d_sz != expected_san_sz
            ):
                crc_mismatches.append({
                    "vpath": vp,
                    "reason": "archive_sanitized_crc_mismatch",
                    "tier": d_tier,
                    "origin": d_origin,
                    "expectedSanitizedCrc32": expected_san_crc,
                    "stagedCrc32": f"{d_crc:08x}",
                    "expectedSanitizedSize": expected_san_sz,
                    "stagedSize": d_sz,
                })
        elif s_crc != d_crc or s_sz != d_sz:
            crc_mismatches.append({
                "vpath": vp,
                "sourceTier": s_tier,
                "stagedTier": d_tier,
                "sourceCrc32": f"{s_crc:08x}",
                "stagedCrc32": f"{d_crc:08x}",
                "sourceSize": s_sz,
                "stagedSize": d_sz,
                "sourceOrigin": s_origin,
                "stagedOrigin": d_origin,
            })

    # Verify FN-003: Any active override/ texture colliding with game/textures.s2z or
    # KONGOR/updates/textures.s2z must stay loose in staged override/
    staged_textures_entries = load_textures_s2z_entries([
        staged_client / "game" / "textures.s2z",
        staged_client / "KONGOR" / "updates" / "textures.s2z",
    ])
    if staged_textures_entries:
        for vp, (s_tier, _, _, s_origin) in src_winners.items():
            if s_tier == "override" and s_origin == "loose":
                if (
                    vp in staged_textures_entries
                    or f"00000000/{vp}" in staged_textures_entries
                ):
                    d_entry = stg_winners.get(vp)
                    if d_entry is None or d_entry[3] != "loose":
                        fn003_violations.append(
                            f"FN-003 violation: '{vp}' collides with textures.s2z (game or KONGOR/updates) and must stay loose in staged override/ (got '{d_entry[3] if d_entry else 'MISSING'}')."
                        )

    # Verify clean generated KONGOR/configuration/{init.cfg, autoexec.cfg}
    cfg_violations: list[str] = []
    for cfg_name in ("init.cfg", "autoexec.cfg"):
        cfg_p = staged_client / "KONGOR" / "configuration" / cfg_name
        if not cfg_p.is_file():
            cfg_violations.append(
                f"Missing required clean config '{cfg_p}'. Re-run scripts/release/export_playtest_bundle.ps1 configuration generation."
            )
        else:
            cfg_txt = cfg_p.read_text(encoding="utf-8", errors="ignore")
            if re.search(r"(?i)\blogin_(?:password|name)\b", cfg_txt):
                cfg_violations.append(
                    f"Clean config '{cfg_p}' contains forbidden login_name/login_password directive."
                )

    passed = (
        not src_violations
        and not stg_violations
        and not missing_in_staged
        and not extra_in_staged
        and not crc_mismatches
        and not tier_mismatches
        and not fn003_violations
        and not loose_shader_violations
        and not cfg_violations
    )

    bots_core_entry = stg_winners.get("bots/core.lua")
    bots_core_winner = (
        {
            "vpath": "bots/core.lua",
            "tier": bots_core_entry[0],
            "crc32": f"{bots_core_entry[1]:08x}",
            "sizeBytes": bots_core_entry[2],
            "origin": bots_core_entry[3],
        }
        if bots_core_entry is not None
        else None
    )

    elapsed = round(time.perf_counter() - t0, 3)
    report = {
        "passed": passed,
        "sourceVirtualPaths": len(src_winners),
        "stagedVirtualPaths": len(stg_winners),
        "botsCoreWinner": bots_core_winner,
        "sanitizedVerifiedPaths": len(loose_sanitized_expected),
        "archiveSanitizedVerifiedPaths": len(archive_sanitized_expected),
        "missingInStagedCount": len(missing_in_staged),
        "extraInStagedCount": len(extra_in_staged),
        "crcMismatchCount": len(crc_mismatches),
        "tierMismatchCount": len(tier_mismatches),
        "fn003ViolationCount": len(fn003_violations),
        "looseShaderViolationCount": len(loose_shader_violations),
        "archiveNamingViolations": stg_violations,
        "configViolations": cfg_violations,
        "missingInStagedSamples": missing_in_staged[:50],
        "extraInStagedSamples": extra_in_staged[:50],
        "crcMismatchSamples": crc_mismatches[:50],
        "tierMismatchSamples": tier_mismatches[:50],
        "fn003Violations": fn003_violations,
        "looseShaderViolations": loose_shader_violations[:50],
        "elapsedSeconds": elapsed,
    }

    if args.json_out:
        out_p = Path(args.json_out).resolve()
        out_p.parent.mkdir(parents=True, exist_ok=True)
        out_p.write_text(json.dumps(report, indent=2), encoding="utf-8")
        log(f"Wrote VFS parity report to '{out_p}'.")

    if not passed:
        log(
            f"[VFS PARITY GATE FAILED] missing={len(missing_in_staged)}, extra={len(extra_in_staged)}, "
            f"crc_mismatch={len(crc_mismatches)}, tier_mismatch={len(tier_mismatches)}, "
            f"fn003={len(fn003_violations)}, loose_shaders={len(loose_shader_violations)}, "
            f"arch_violations={len(stg_violations)}, cfg_violations={len(cfg_violations)}."
        )
        print(
            "ACTIONABLE FIX: Inspect vfs_parity_report.json and re-run "
            "`python tools/export/verify_vfs_parity.py verify-parity --source-client games/client --staged-client <StageDir>/ProjectKongor/games/client`.",
            file=sys.stderr,
        )
        return 1

    log(
        f"[VFS PARITY GATE PASSED] 100.00% CRC32 & mount-tier parity across {len(stg_winners):,} virtual paths in {elapsed:.2f}s!"
    )
    return 0


def resolve_tier_dir(
    client_root: Path,
    shared_root: Path | None,
    tier_name: str,
) -> Path | None:
    if tier_name == "shared":
        return shared_root
    mapping = {
        "override": client_root / "override",
        "KONGOR/extensions": client_root / "KONGOR" / "extensions",
        "KONGOR/updates": client_root / "KONGOR" / "updates",
        "KONGOR/configuration": client_root / "KONGOR" / "configuration",
        "ClientPackageFiles": client_root / "ClientPackageFiles",
        "game": client_root / "game",
        "base": client_root / "base",
    }
    return mapping.get(tier_name)


def read_vfs_winning_entry(
    client_root: Path,
    shared_root: Path | None,
    target_vpath: str,
    is_source_workspace: bool = False,
) -> dict[str, Any] | None:
    norm_target = normalize_vpath(target_vpath)
    winners, violations = build_vfs_winner_map(
        client_root=client_root,
        shared_root=shared_root,
        is_source_workspace=is_source_workspace,
        include_test_videos=True,
    )
    winner = winners.get(norm_target)
    if winner is None:
        return None

    tier_name, crc_int, size_bytes, origin = winner
    tier_dir = resolve_tier_dir(client_root, shared_root, tier_name)
    if tier_dir is None or not tier_dir.is_dir():
        raise FileNotFoundError(
            f"Resolved winning tier '{tier_name}' directory '{tier_dir}' does not exist for '{norm_target}'."
        )

    payload_bytes: bytes | None = None
    if origin == "loose":
        direct_file = tier_dir / norm_target
        if direct_file.is_file():
            payload_bytes = to_extended_path(direct_file).read_bytes()
        else:
            for dirpath, _, filenames in os.walk(tier_dir):
                for fname in filenames:
                    cand = Path(dirpath) / fname
                    if normalize_vpath(str(cand.relative_to(tier_dir))) == norm_target:
                        payload_bytes = to_extended_path(cand).read_bytes()
                        break
                if payload_bytes is not None:
                    break
    elif origin.startswith("s2z:"):
        arch_name = origin[4:]
        arch_path = tier_dir / arch_name
        if not arch_path.is_file():
            raise FileNotFoundError(
                f"Resolved winning archive '{arch_path}' does not exist for '{norm_target}'."
            )
        with zipfile.ZipFile(arch_path, "r") as zf:
            for info in reversed(zf.infolist()):
                if info.is_dir():
                    continue
                if normalize_vpath(info.filename) == norm_target:
                    payload_bytes = zf.read(info)
                    break

    if payload_bytes is None:
        raise FileNotFoundError(
            f"Could not read winning VFS payload for '{norm_target}' from {tier_name} ({origin})."
        )

    return {
        "vpath": norm_target,
        "tier": tier_name,
        "crc32": f"{crc_int:08x}",
        "sizeBytes": size_bytes,
        "origin": origin,
        "payloadBytes": payload_bytes,
        "violations": violations,
    }


def cmd_inspect_vpath(args: argparse.Namespace) -> int:
    t0 = time.perf_counter()
    staged_client = Path(args.staged_client).resolve()
    staged_shared = Path(args.staged_shared).resolve() if args.staged_shared else None
    target_vpath = normalize_vpath(args.vpath)
    expected_tier = (args.expected_tier or "").strip()
    require_text = args.require_text or ""

    entry = read_vfs_winning_entry(
        client_root=staged_client,
        shared_root=staged_shared,
        target_vpath=target_vpath,
        is_source_workspace=bool(getattr(args, "source_mode", False)),
    )

    failure_reasons: list[str] = []
    matched_require_text: bool | None = None
    report: dict[str, Any] = {
        "passed": False,
        "vpath": target_vpath,
        "expectedTier": expected_tier or None,
        "requireText": require_text or None,
        "matchedRequireText": None,
        "elapsedSeconds": 0.0,
    }

    if entry is None:
        failure_reasons.append(f"Virtual path '{target_vpath}' not found in VFS mount stack.")
    else:
        report.update({
            "tier": entry["tier"],
            "crc32": entry["crc32"],
            "sizeBytes": entry["sizeBytes"],
            "origin": entry["origin"],
            "archiveNamingViolations": entry["violations"],
        })
        if entry["violations"]:
            failure_reasons.append(
                f"Archive naming/size violations detected: {entry['violations']}"
            )
        if expected_tier and entry["tier"].lower() != expected_tier.lower():
            failure_reasons.append(
                f"Expected VFS winner tier '{expected_tier}' for '{target_vpath}', got '{entry['tier']}' ({entry['origin']})."
            )
        if require_text:
            decoded = entry["payloadBytes"].decode("utf-8", errors="replace")
            matched_require_text = require_text in decoded
            report["matchedRequireText"] = matched_require_text
            if not matched_require_text:
                failure_reasons.append(
                    f"Winning payload for '{target_vpath}' ({entry['tier']}/{entry['origin']}) does not contain required text '{require_text}'."
                )

    passed = len(failure_reasons) == 0
    elapsed = round(time.perf_counter() - t0, 3)
    report["passed"] = passed
    report["failureReasons"] = failure_reasons
    report["elapsedSeconds"] = elapsed

    if args.json_out:
        out_p = Path(args.json_out).resolve()
        out_p.parent.mkdir(parents=True, exist_ok=True)
        out_p.write_text(json.dumps(report, indent=2), encoding="utf-8")
        log(f"Wrote VFS inspect report for '{target_vpath}' to '{out_p}'.")

    if not passed:
        for reason in failure_reasons:
            log(f"[VFS INSPECT FAILED] {reason}")
        return 1

    log(
        f"[VFS INSPECT PASSED] '{target_vpath}' resolved to tier='{report['tier']}' origin='{report['origin']}' crc32={report['crc32']} ({elapsed:.2f}s)."
    )
    return 0


# ---------------------------------------------------------------------------
# Subcommand 4: scan-secrets
# ---------------------------------------------------------------------------
def is_permitted_shader_header(rel_vpath: str) -> bool:
    norm = normalize_vpath(rel_vpath)
    return norm.endswith(".h") and ("core/shaders/" in norm or norm.startswith("shaders/"))


def is_json_key_heuristic_exempt(rel_bundle: str, leaf_lower: str) -> bool:
    if leaf_lower.endswith(
        (
            ".deps.json",
            ".runtimeconfig.json",
            ".staticwebassets.endpoints.json",
            ".staticwebassets.runtime.json",
        )
    ):
        return True
    if rel_bundle.replace("\\", "/").lower().endswith("/wwwroot/ability_metadata.json"):
        return True
    return False


def scan_text_payload(
    origin_label: str,
    text: str,
    findings: list[dict[str, Any]],
) -> None:
    for rule_name, category, rx in SECRET_SCAN_RULES:
        for match in rx.finditer(text):
            line_start = text.rfind("\n", 0, match.start()) + 1
            line_end = text.find("\n", match.start())
            if line_end < 0:
                line_end = len(text)
            line_str = text[line_start:line_end]
            if "kongor-security-ignore" in line_str:
                continue

            if rule_name == "lua_hardcoded_md5_password":
                md5_hex = match.group(1).lower()
                if md5_hex == EMPTY_STRING_MD5:
                    continue

            if rule_name in {"k2_cfg_login_password", "k2_cfg_login_name"}:
                captured = match.group(1).strip().lower()
                if captured in {"", "player", "unnamednewbie"}:
                    continue

            findings.append({
                "location": origin_label,
                "rule": rule_name,
                "category": category,
                "maskedSnippet": mask_snippet(match.group(0)),
            })


def cmd_scan_secrets(args: argparse.Namespace) -> int:
    t0 = time.perf_counter()
    bundle_root = Path(args.bundle_root).resolve()
    if not bundle_root.is_dir():
        raise FileNotFoundError(f"Bundle root '{bundle_root}' does not exist.")

    findings: list[dict[str, Any]] = []
    loose_scanned = 0
    s2z_archives_scanned = 0
    s2z_entries_scanned = 0

    legacy_bak_exempt_archives = LEGACY_VANILLA_BAK_EXEMPT_ARCHIVES

    for dirpath, _, filenames in os.walk(bundle_root):
        for fname in filenames:
            fpath = Path(dirpath) / fname
            rel_bundle = fpath.relative_to(bundle_root).as_posix()
            rel_lower = normalize_vpath(rel_bundle)
            leaf_lower = fname.lower()
            ext_lower = fpath.suffix.lower()
            loose_scanned += 1

            # 1. Check forbidden binaries / crash uploaders / developer configs / symbols / source files
            if "crash_uploader" in leaf_lower or "hon_update" in leaf_lower:
                findings.append({
                    "location": rel_bundle,
                    "rule": "forbidden_legacy_uploader_or_updater_binary",
                    "category": "BINARY",
                    "maskedSnippet": fname,
                })
                continue

            rel_parts = PurePosixPath(rel_lower).parts
            if (
                any(seg in {"shadercache", "texturecache"} for seg in rel_parts)
                or leaf_lower.startswith(("shadercache", "texturecache"))
            ):
                findings.append({
                    "location": rel_bundle,
                    "rule": "forbidden_shader_or_texture_cache",
                    "category": "ARTIFACT",
                    "maskedSnippet": fname,
                })
                continue

            if leaf_lower in {"login.cfg", "kongor_vfs_probe.cfg", ".hotreload_manifest.json"}:
                findings.append({
                    "location": rel_bundle,
                    "rule": "forbidden_dev_config_file",
                    "category": "CONFIG",
                    "maskedSnippet": fname,
                })
                continue

            if any(f"{dev_u.lower()}_2." in leaf_lower for dev_u in RUNTIME_LOGIN_SCRUB_USERNAMES):
                findings.append({
                    "location": rel_bundle,
                    "rule": "forbidden_legacy_dev_video_name",
                    "category": "ASSET",
                    "maskedSnippet": fname,
                })
                continue

            if getattr(args, "skip_cinematic_videos", False) and "kongor_cinematic." in leaf_lower:
                findings.append({
                    "location": rel_bundle,
                    "rule": "excluded_cinematic_video_present",
                    "category": "ASSET",
                    "maskedSnippet": fname,
                })
                continue

            if ext_lower == ".h" and not is_permitted_shader_header(rel_lower):
                findings.append({
                    "location": rel_bundle,
                    "rule": "forbidden_c_header_file",
                    "category": "SOURCE",
                    "maskedSnippet": fname,
                })
                continue

            if ext_lower in FORBIDDEN_BUNDLE_EXTENSIONS or ".bak" in leaf_lower:
                findings.append({
                    "location": rel_bundle,
                    "rule": f"forbidden_extension_{ext_lower or 'bak'}",
                    "category": "ARTIFACT",
                    "maskedSnippet": fname,
                })
                continue

            # 2. Stream-inspect .s2z archives
            if ext_lower == ".s2z":
                s2z_archives_scanned += 1
                with zipfile.ZipFile(fpath, "r") as zf:
                    for info in zf.infolist():
                        if info.is_dir():
                            continue
                        s2z_entries_scanned += 1
                        entry_norm = normalize_vpath(info.filename)
                        entry_leaf = PurePosixPath(entry_norm).name
                        entry_ext = PurePosixPath(entry_norm).suffix.lower()
                        entry_label = f"{rel_bundle}::{info.filename}"

                        if entry_leaf == "login.cfg":
                            findings.append({
                                "location": entry_label,
                                "rule": "forbidden_login_cfg_in_s2z",
                                "category": "CONFIG",
                                "maskedSnippet": entry_leaf,
                            })
                            continue

                        if entry_leaf in {
                            "kongor_vfs_probe.cfg",
                            ".hotreload_manifest.json",
                            "moved_to_vault_j.txt",
                        }:
                            findings.append({
                                "location": entry_label,
                                "rule": "forbidden_dev_config_in_s2z",
                                "category": "CONFIG",
                                "maskedSnippet": entry_leaf,
                            })
                            continue

                        if entry_ext == ".h" and not is_permitted_shader_header(entry_norm):
                            findings.append({
                                "location": entry_label,
                                "rule": "forbidden_c_header_in_s2z",
                                "category": "SOURCE",
                                "maskedSnippet": entry_leaf,
                            })
                            continue

                        if (
                            entry_ext in FORBIDDEN_BUNDLE_EXTENSIONS
                            or entry_ext == ".log"
                            or ".bak" in entry_leaf
                            or entry_leaf.endswith(".backup")
                        ):
                            if (
                                (
                                    entry_ext == ".bak"
                                    or ".bak" in entry_leaf
                                    or entry_leaf.endswith(".backup")
                                )
                                and leaf_lower in legacy_bak_exempt_archives
                            ):
                                pass
                            else:
                                findings.append({
                                    "location": entry_label,
                                    "rule": f"forbidden_s2z_entry_extension_{entry_ext or 'bak'}",
                                    "category": "ARTIFACT",
                                    "maskedSnippet": entry_leaf,
                                })

                        # Zip-bomb / huge binary guard: text-scan entries <= 16 MB with text extensions
                        if (
                            entry_ext in TEXT_SCAN_EXTENSIONS
                            or entry_norm.endswith(".bak")
                        ) and info.file_size <= 16 * 1024 * 1024:
                            with zf.open(info, "r") as efh:
                                raw_bytes = efh.read(16 * 1024 * 1024 + 1)
                            if len(raw_bytes) <= 16 * 1024 * 1024:
                                text_str = raw_bytes.decode("utf-8", errors="ignore")
                                scan_text_payload(entry_label, text_str, findings)
                                if entry_ext == ".json":
                                    try:
                                        json_data = json.loads(text_str)
                                        scan_json_payload(entry_label, json_data, findings)
                                    except Exception:
                                        pass
                continue

            # 3. Scan staged native binary files for leaked developer PII and paths
            if is_binary_candidate(fpath):
                try:
                    bin_bytes = fpath.read_bytes()
                    scan_binary_payload(rel_bundle, bin_bytes, findings)
                except Exception as exc:
                    log(f"Warning: could not scan binary file '{fpath}': {exc}")

            # 4. Scan loose JSON configuration files for secret values
            leaf_lower = fpath.name.lower()
            if ext_lower == ".json" and fpath.stat().st_size <= 16 * 1024 * 1024:
                if not is_json_key_heuristic_exempt(rel_bundle, leaf_lower):
                    try:
                        json_data = json.loads(fpath.read_text(encoding="utf-8", errors="ignore"))
                        scan_json_payload(rel_bundle, json_data, findings)
                    except Exception:
                        pass

            # 5. Scan loose text files (<= 35 MB)
            if ext_lower in TEXT_SCAN_EXTENSIONS and fpath.stat().st_size <= 35 * 1024 * 1024:
                text_str = fpath.read_text(encoding="utf-8", errors="ignore")
                scan_text_payload(rel_bundle, text_str, findings)

    passed = len(findings) == 0
    elapsed = round(time.perf_counter() - t0, 3)
    report = {
        "passed": passed,
        "bundleRoot": str(bundle_root),
        "looseFilesScanned": loose_scanned,
        "s2zArchivesScanned": s2z_archives_scanned,
        "s2zEntriesScanned": s2z_entries_scanned,
        "findingCount": len(findings),
        "findings": findings[:200],
        "elapsedSeconds": elapsed,
    }

    if args.json_out:
        out_p = Path(args.json_out).resolve()
        out_p.parent.mkdir(parents=True, exist_ok=True)
        out_p.write_text(json.dumps(report, indent=2), encoding="utf-8")
        log(f"Wrote deep secret & leak scan report to '{out_p}'.")

    if not passed:
        log(
            f"[SECRET & LEAK GATE FAILED] {len(findings)} violation(s) detected across "
            f"{loose_scanned:,} loose files and {s2z_entries_scanned:,} .s2z entries!"
        )
        for sample in findings[:10]:
            log(
                f"  -> [{sample['category']}] {sample['rule']} at {sample['location']} (match={sample['maskedSnippet']})"
            )
        print(
            "ACTIONABLE FIX: Review secret_scan_report.json and remove leaked credentials/artifacts before distributing the playtest bundle.",
            file=sys.stderr,
        )
        return 1

    log(
        f"[SECRET & LEAK GATE PASSED] 0 leaks across {loose_scanned:,} loose files and "
        f"{s2z_entries_scanned:,} entries in {s2z_archives_scanned} .s2z archives ({elapsed:.2f}s)."
    )
    return 0


# ---------------------------------------------------------------------------
# Subcommand 5: write-manifests
# ---------------------------------------------------------------------------
def cmd_write_manifests(args: argparse.Namespace) -> int:
    t0 = time.perf_counter()
    bundle_root = Path(args.bundle_root).resolve()
    staged_client = bundle_root / "games" / "client"
    staged_shared = bundle_root / "games" / "shared"

    if not staged_client.is_dir():
        raise FileNotFoundError(
            f"Expected staged client directory at '{staged_client}'."
        )

    # Compute SHA-256 of every .s2z archive in games/shared and games/client
    archive_hashes: dict[str, str] = {}
    s2z_roots = [bundle_root / "games"]
    for sroot in s2z_roots:
        if not sroot.is_dir():
            continue
        for dirpath, _, filenames in os.walk(sroot):
            for fname in sorted(filenames):
                if not fname.lower().endswith(".s2z"):
                    continue
                fpath = Path(dirpath) / fname
                rel_key = fpath.relative_to(bundle_root).as_posix()
                log(f"Hashing archive '{rel_key}'...")
                archive_hashes[rel_key] = compute_file_sha256(fpath)

    # Compute aggregate gameplay entity CRC32 so P2P peers can verify exact simulation parity
    winners, _ = build_vfs_winner_map(
        client_root=staged_client,
        shared_root=staged_shared if staged_shared.is_dir() else None,
        is_source_workspace=False,
        include_test_videos=True,
    )
    rolling_entity_crc = 0
    entity_count = 0
    for vpath in sorted(winners.keys()):
        ext = PurePosixPath(vpath).suffix.lower()
        if ext in GAMEPLAY_ENTITY_EXTENSIONS:
            _, entry_crc, entry_sz, _ = winners[vpath]
            line = f"{vpath}:{entry_crc:08x}:{entry_sz}\n".encode("utf-8")
            rolling_entity_crc = binascii.crc32(line, rolling_entity_crc)
            entity_count += 1

    archive_hashes["__gameplay_entity_crc32__"] = f"{rolling_entity_crc & 0xFFFFFFFF:08x}"
    archive_hashes["__gameplay_entity_count__"] = str(entity_count)

    policy_payload = {
        "isPlaytest": True,
        "disableCdnDeletions": True,
        "disableFolderNormalizer": True,
        "disableLocalClusterPolling": True,
        "enforceStrictTlsValidation": True,
        "binariesPrePatched": bool(args.binaries_pre_patched),
        "preferStandardProcessStart": True,
        "disableDevWatchers": True,
        "useStableReleaseStageCvars": True,
    }

    built_utc = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    manifest_payload = {
        "buildId": args.build_id,
        "version": getattr(args, "version", "5.0.2"),
        "gitCommit": args.git_commit,
        "platform": args.platform,
        "builtAtUtc": built_utc,
        "channel": args.channel,
        "archiveHashes": dict(sorted(archive_hashes.items())),
    }

    policy_json = json.dumps(policy_payload, indent=2)
    manifest_json = json.dumps(manifest_payload, indent=2)

    manifest_targets = [bundle_root, staged_client]
    for child in bundle_root.iterdir():
        if child.is_dir() and child.name.startswith(("launcher-", "control-panel-")):
            manifest_targets.append(child)

    for target_dir in manifest_targets:
        (target_dir / "playtest_policy.json").write_text(policy_json, encoding="utf-8")
        (target_dir / "build_manifest.json").write_text(manifest_json, encoding="utf-8")

    elapsed = round(time.perf_counter() - t0, 3)
    log(
        f"Wrote playtest_policy.json & build_manifest.json (buildId='{args.build_id}', "
        f"archives={len(archive_hashes) - 2}, entityCrc32={rolling_entity_crc & 0xFFFFFFFF:08x} over {entity_count:,} files) in {elapsed:.2f}s."
    )
    return 0


ZIP_STORED_EXTENSIONS = frozenset({
    ".s2z",
    ".avi",
    ".mp3",
    ".ogg",
    ".zip",
    ".png",
    ".jpg",
    ".jpeg",
})


def cmd_create_posix_zip(args: argparse.Namespace) -> int:
    t0 = time.perf_counter()
    bundle_root = Path(args.bundle_root).resolve()
    if not bundle_root.exists() or not bundle_root.is_dir():
        log(f"Error: bundle-root '{bundle_root}' does not exist or is not a directory.")
        return 1

    zip_out = Path(args.zip_out).resolve()
    zip_out.parent.mkdir(parents=True, exist_ok=True)
    if zip_out.exists():
        zip_out.unlink()

    log(f"Creating POSIX-compliant distributable zip '{zip_out}' from '{bundle_root}'...")

    total_files = 0
    total_uncompressed_bytes = 0

    all_files: list[Path] = []
    empty_dirs: list[Path] = []
    for root, dirs, files in os.walk(bundle_root):
        if not dirs and not files:
            root_path = Path(root)
            if root_path != bundle_root:
                empty_dirs.append(root_path)
        for f in files:
            all_files.append(Path(root) / f)

    empty_dirs.sort(key=lambda p: p.as_posix())
    all_files.sort(key=lambda p: p.as_posix())

    with zipfile.ZipFile(
        zip_out,
        mode="w",
        compression=zipfile.ZIP_DEFLATED,
        allowZip64=True,
        compresslevel=6,
    ) as zf:
        for dpath in empty_dirs:
            rel_dir = dpath.relative_to(bundle_root).as_posix()
            dir_arcname = f"{bundle_root.name}/{rel_dir}/"
            dinfo = zipfile.ZipInfo(dir_arcname, date_time=(2026, 1, 1, 0, 0, 0))
            dinfo.create_system = 3
            dinfo.compress_type = zipfile.ZIP_STORED
            dinfo.external_attr = (0o040755 << 16) | 0x10
            zf.writestr(dinfo, b"")

        for fpath in all_files:
            rel = fpath.relative_to(bundle_root)
            rel_posix = rel.as_posix()
            arcname = f"{bundle_root.name}/{rel_posix}"

            stat_res = fpath.stat()
            file_size = stat_res.st_size
            mtime = time.gmtime(stat_res.st_mtime)
            dt_tuple = (
                (1980, 1, 1, 0, 0, 0)
                if mtime.tm_year < 1980
                else (
                    mtime.tm_year,
                    mtime.tm_mon,
                    mtime.tm_mday,
                    mtime.tm_hour,
                    mtime.tm_min,
                    mtime.tm_sec,
                )
            )

            try:
                with fpath.open("rb") as fh:
                    header4 = fh.read(4)
            except OSError:
                header4 = b""

            compress_type = (
                zipfile.ZIP_STORED
                if fpath.suffix.lower() in ZIP_STORED_EXTENSIONS
                else zipfile.ZIP_DEFLATED
            )

            zinfo = zipfile.ZipInfo(arcname, date_time=dt_tuple[:6])
            zinfo.create_system = 3  # Unix
            zinfo.compress_type = compress_type
            zinfo.file_size = file_size

            if is_posix_executable(fpath, header4):
                zinfo.external_attr = 0o100755 << 16
            else:
                zinfo.external_attr = 0o100644 << 16

            force_z64 = file_size >= 0x7FFFFFFF
            with zf.open(zinfo, "w", force_zip64=force_z64) as dest_fh, fpath.open("rb") as src_fh:
                shutil.copyfileobj(src_fh, dest_fh, length=8 * 1024 * 1024)

            total_files += 1
            total_uncompressed_bytes += file_size

    elapsed = round(time.perf_counter() - t0, 3)
    out_size = zip_out.stat().st_size
    log(
        f"Wrote POSIX zip '{zip_out}' ({out_size / (1024 * 1024 * 1024):.2f} GB) "
        f"containing {total_files:,} files ({total_uncompressed_bytes / (1024 * 1024 * 1024):.2f} GB uncompressed) in {elapsed:.2f}s."
    )
    return 0


def build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Project Kongor Playtest VFS Tier Packer, Parity Gate & Deep Secret Scanner."
    )
    sub = parser.add_subparsers(dest="command", required=True)

    p_san = sub.add_parser(
        "sanitize-staged",
        help="Scrub developer auto-login hashes and local OS username checks in staged UI files only.",
    )
    p_san.add_argument("--staged-client", required=True)
    p_san.add_argument("--staged-shared", default="")
    p_san.add_argument("--json-out", default="")
    p_san.set_defaults(func=cmd_sanitize_staged)

    p_pack = sub.add_parser(
        "pack-tier",
        help="Pack eligible loose files in a staged K2 mount tier into strict 32-bit resources<XX>.s2z shards.",
    )
    p_pack.add_argument("--tier-dir", required=True)
    p_pack.add_argument("--tier-name", required=True)
    p_pack.add_argument(
        "--suffix-style",
        choices=["digits", "alpha"],
        default="digits",
        help="'digits' -> resources00..99.s2z; 'alpha' -> resourcesa0..z9.s2z (sorts above resources99.s2z in game/).",
    )
    p_pack.add_argument(
        "--max-archive-bytes",
        type=int,
        default=DEFAULT_PACK_TARGET_LIMIT,
    )
    p_pack.add_argument(
        "--textures-s2z",
        action="append",
        default=[],
        help="Path(s) to textures.s2z archive(s) (repeatable, e.g. game/textures.s2z and KONGOR/updates/textures.s2z) for FN-003 loose-override preservation.",
    )
    p_pack.add_argument("--json-out", default="")
    p_pack.set_defaults(func=cmd_pack_tier)

    p_par = sub.add_parser(
        "verify-parity",
        help="Verify 100.00%% K2 VFS CRC32 and winning-tier parity between source workspace and staged export.",
    )
    p_par.add_argument("--source-client", required=True)
    p_par.add_argument("--source-shared", default="")
    p_par.add_argument("--staged-client", required=True)
    p_par.add_argument("--staged-shared", default="")
    p_par.add_argument("--sanitized-manifest", default="")
    p_par.add_argument("--skip-cinematic-videos", action="store_true")
    p_par.add_argument("--include-test-videos", action="store_true")
    p_par.add_argument("--json-out", default="")
    p_par.set_defaults(func=cmd_verify_parity)

    p_sec = sub.add_parser(
        "scan-secrets",
        help="Deep-scan all loose files and .s2z entries in the staged bundle for secrets, source code, and symbols.",
    )
    p_sec.add_argument("--bundle-root", required=True)
    p_sec.add_argument("--skip-cinematic-videos", action="store_true")
    p_sec.add_argument("--include-test-videos", action="store_true")
    p_sec.add_argument("--json-out", default="")
    p_sec.set_defaults(func=cmd_scan_secrets)

    p_man = sub.add_parser(
        "write-manifests",
        help="Generate playtest_policy.json and build_manifest.json in the staged bundle.",
    )
    p_man.add_argument("--bundle-root", required=True)
    p_man.add_argument("--build-id", required=True)
    p_man.add_argument("--git-commit", required=True)
    p_man.add_argument("--platform", default="win-x64")
    p_man.add_argument("--channel", default="playtest")
    p_man.add_argument("--version", default="5.0.2")
    p_man.add_argument("--binaries-pre-patched", action="store_true")
    p_man.set_defaults(func=cmd_write_manifests)

    p_zip = sub.add_parser(
        "create-posix-zip",
        help="Create a Unix/POSIX-compliant distributable zip archive preserving 0o755 executable permissions.",
    )
    p_zip.add_argument("--bundle-root", required=True)
    p_zip.add_argument("--zip-out", required=True)
    p_zip.set_defaults(func=cmd_create_posix_zip)

    p_insp = sub.add_parser(
        "inspect-vpath",
        help="Resolve and verify the winning tier, origin, and optional substring for a virtual path in the K2 VFS.",
    )
    p_insp.add_argument("--staged-client", required=True)
    p_insp.add_argument("--staged-shared", default="")
    p_insp.add_argument("--vpath", required=True)
    p_insp.add_argument("--expected-tier", default="")
    p_insp.add_argument("--require-text", default="")
    p_insp.add_argument("--source-mode", action="store_true")
    p_insp.add_argument("--json-out", default="")
    p_insp.set_defaults(func=cmd_inspect_vpath)

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_arg_parser()
    args = parser.parse_args(argv)
    return int(args.func(args))


if __name__ == "__main__":
    sys.exit(main())

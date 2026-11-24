#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
"""
Cleanroom, deterministic Multi-OS pkv-v5 CDN Manifest & CAS Generator for Project Kongor.

Produces PKV Guard-compliant manifests for Windows (`wac`), Linux (`lac`), and macOS (`mac`)
matching `NEXUS.Contracts.Cdn.Manifest`, `PkvManifestValidator`, `ManifestSignatureVerifier`,
and `WILLOWMAKER.Core.Services.ContentBroker`:
  - protocol: "pkv-v5"
  - version: >= 5.0.0 (sourced from Directory.Build.props <VersionPrefix>)
  - mode: "overlay"
  - objectLayout: "sha256" (objects/<ab>/<sha256>)
  - hashAlgorithm: "SHA-256"
  - sequence: monotonic >= 1
  - signature: 64-byte IEEE P1363 (r || s) ECDSA P-256 SHA-256 over PKV-CDN-MANIFEST-V5 transcript
"""

from __future__ import annotations

import argparse
import base64
import fnmatch
import hashlib
import json
import os
import re
import shutil
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import (
    decode_dss_signature,
    encode_dss_signature,
)

from tools.export.binary_linkage_audit import parse_macho

CANONICAL_HEADER = "PKV-CDN-MANIFEST-V5"
DEFAULT_PUBLIC_KEY_X_HEX = "05b1399c6fbd80f20c579b81489f6617d42e4d6ad41285e1c90dc4c1f2563536"
DEFAULT_PUBLIC_KEY_Y_HEX = "1d946704f9f2a943b2cb3772c0d044346a70872a2dada0cc31d56d2a69d76de5"
RECOVERY_PUBLIC_KEY_X_HEX = "4a13a468e8a367c5e9137e978556ab774f600164e29e2dab93ac0b14eee5503a"
RECOVERY_PUBLIC_KEY_Y_HEX = "d19ca58a1b0cd7bb6df49a8fc25aef53fa8f52d1f6d0fe3bec8e22b40a314bf8"

SUPPORTED_VARIANTS = ("wac", "lac", "mac", "las", "was")
DEFAULT_CLIENT_VARIANTS = ("wac", "lac", "mac")

FORBIDDEN_OVERLAY_GLOBS: tuple[str, ...] = (
    "*.s2z",
    "*.zip",
    "*.pdb",
    "*.dmp",
    "*.mdmp",
    "*.log",
    "*.bak",
    "*.bak*",
    "*.backup*",
    "*.old",
    "*.old_*",
    "*.active_prev",
    "*.orig",
    "*.disabled",
    "*.disabled_*",
    ".disabled_*",
    ".hotreload*",
    "*.partial",
    ".DS_Store",
    "manifest.xml",
    "*.manifest.xml.zip",
    "*.honreplay",
    "*.ps1",
    "*.py",
    "*.bat",
    "kongor_vfs_probe.cfg",
    "MOVED_TO_VAULT_J.txt",
    "rollback_journal.json",
    "mods_state.json",
    "startup.cfg",
    "login.cfg",
    "autoexec.cfg",
    "*flashplayer*",
    "*ffmpegsumo*",
    "libplugin_carbon_helper.dylib",
    "awesomium_process",
    "Awesomium",
)


def log(msg: str, level: str = "INFO") -> None:
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
    print(f"[PKV-MANIFEST {ts}][{level}] {msg}", flush=True)


def read_platform_version(repo_root: Path) -> str:
    props_path = repo_root / "Directory.Build.props"
    if props_path.is_file():
        raw = props_path.read_text(encoding="utf-8")
        m = re.search(r"<VersionPrefix>([^<]+)</VersionPrefix>", raw)
        if m:
            return m.group(1).strip()
    return "5.0.2"


def validate_pkv_v5_version(version: str) -> None:
    if not version or not version.strip():
        raise ValueError(
            "Manifest version cannot be empty. Specify --version >= 5.0.0 (e.g. '5.0.2')."
        )
    v = version.strip()
    if v[:1] in ("v", "V"):
        v = v[1:]
    head = re.split(r"[-+]", v, maxsplit=1)[0]
    parts = head.split(".")
    try:
        major = int(parts[0])
    except ValueError as exc:
        raise ValueError(
            f"Invalid manifest version '{version}': major segment is not an integer."
        ) from exc
    if major < 5:
        raise ValueError(
            f"Legacy pre-v5 version '{version}' rejected by PKV Guard (major={major} < 5). "
            f"Use version >= 5.0.0 (see Directory.Build.props)."
        )


def normalize_rel_path(path_str: str) -> str:
    norm = path_str.replace("\\", "/").strip()
    while "//" in norm:
        norm = norm.replace("//", "/")
    return norm


def is_posix_executable_relpath(norm_path: str) -> bool:
    parts = norm_path.split("/")
    fname = parts[-1]
    lower_name = fname.lower()
    _, ext = os.path.splitext(lower_name)
    if ext in (".sh", ".command", ".so", ".dylib", ".bin"):
        return True
    if ".so." in lower_name:
        return True
    if lower_name in ("hon-x86_64", "hon-x86", "hon64", "hon-x86_64-server", "willowmaker", "kongor"):
        return True
    return False


def validate_overlay_relpath(norm_path: str, file_size: int | None = None) -> None:
    if not norm_path:
        raise ValueError("Overlay relative path cannot be empty.")
    if any(ch in norm_path for ch in ("\t", "\r", "\n", "\x00", "|", ":")):
        raise ValueError(f"Forbidden control/delimiter character in manifest path '{norm_path}'.")
    if norm_path.startswith("/"):
        raise ValueError(f"Leading slash is forbidden in manifest path '{norm_path}'.")
    segments = [s for s in norm_path.split("/") if s]
    if ".." in segments:
        raise ValueError(f"Directory traversal '..' is forbidden in manifest path '{norm_path}'.")

    # Enforce strict lowercase 'games/client/override/' for Linux case-sensitive ext4/xfs
    if norm_path.lower().startswith("games/client/override/") and not norm_path.startswith(
        "games/client/override/"
    ):
        raise ValueError(
            f"Case-sensitivity violation in '{norm_path}': must use exact lowercase 'games/client/override/'."
        )

    lower_norm = norm_path.lower()
    forbidden_prefixes = (
        "games/client/game/",
        "games/client/base/",
        "games/shared/",
        "games/server/game/",
        "games/server_p2p/game/",
        "games/client/windows-x64/game/",
        "games/client/windows-x64/base/",
        "games/client/linux-x64/game/",
        "games/client/linux-x64/base/",
        "games/client/macos-x64/game/",
        "games/client/macos-x64/base/",
        "userdata/",
    )
    if lower_norm.startswith(forbidden_prefixes) or "/userdata/" in lower_norm or lower_norm == "userdata":
        raise ValueError(
            f"Forbidden base/archive/userdata path '{norm_path}' rejected by PKV emergency overlay policy."
        )

    forbidden_dir_segments = {"replays", "shadercache", "texturecache", "logs", ".git"}
    for seg in segments[:-1]:
        seg_l = seg.lower()
        if seg_l in forbidden_dir_segments or seg_l.startswith(".disabled_"):
            raise ValueError(
                f"Forbidden transient directory segment '{seg}' in manifest path '{norm_path}'."
            )

    if "awesomium.framework/" in lower_norm or lower_norm.endswith("awesomium.framework"):
        raise ValueError(
            f"Legacy 32-bit 'Awesomium.framework' path '{norm_path}' is unreferenced bloat and forbidden in pkv-v5."
        )

    fname = segments[-1]
    for pat in FORBIDDEN_OVERLAY_GLOBS:
        if fnmatch.fnmatchcase(fname.lower(), pat.lower()):
            raise ValueError(
                f"File '{norm_path}' matches forbidden overlay pattern '{pat}' "
                f"(.s2z/.zip archives and legacy Flash/FFmpeg/temp artifacts are blocked)."
            )

    if "awesomium" in fname.lower():
        is_allowed_stub = (
            lower_norm.endswith("libs-stub/libawesomium-1-7.so.5")
            or lower_norm.endswith("libs-runtime/libawesomium-1-7.so.5")
        ) and (file_size is None or file_size <= 65536)
        if not is_allowed_stub:
            raise ValueError(
                f"Legacy Chromium/Awesomium binary '{norm_path}' is forbidden in pkv-v5 manifests "
                f"(only <= 64 KB 0-dep libs-stub/libawesomium-1-7.so.5 is permitted)."
            )

    if not is_allowed_emergency_bundle_path(norm_path):
        raise ValueError(
            f"Path '{norm_path}' is outside the emergency bundle allowlist "
            f"(ContentBroker.IsAllowedEmergencyBundlePath)."
        )


def is_allowed_emergency_bundle_path(norm_path: str) -> bool:
    """
    Exact Python parity for `WILLOWMAKER.Core.Services.ContentBroker.IsAllowedEmergencyBundlePath`
    (`src/extensions/cdn-broker/Services/ContentBroker/ContentBroker.cs:795-899`).
    """
    lower = norm_path.lower()

    # 1. games/client/override/**
    if lower.startswith("games/client/override/"):
        return len(norm_path) > len("games/client/override/")

    # 2. games/client/<leaf> where extension is .dll, .exe, .so (including versioned .so.N), .dylib
    if lower.startswith("games/client/"):
        remainder = norm_path[len("games/client/") :]
        if remainder and "/" not in remainder:
            _, ext = os.path.splitext(remainder.lower())
            if ext in (".dll", ".exe", ".so", ".dylib") or ".so." in remainder.lower():
                return True

    # 3. games/client/{windows-x64,linux-x64,macos-x64}/** (excluding .../game/ and .../base/)
    for os_prefix in (
        "games/client/windows-x64/",
        "games/client/linux-x64/",
        "games/client/macos-x64/",
    ):
        if lower.startswith(os_prefix):
            if lower.startswith((f"{os_prefix}game/", f"{os_prefix}base/")):
                return False
            return len(norm_path) > len(os_prefix)

    # 4 & 5. games/{server,server_p2p}/<leaf> and games/{server,server_p2p}/libs-x86_64/**
    for srv_prefix in ("games/server/", "games/server_p2p/"):
        if lower.startswith(srv_prefix):
            remainder = norm_path[len(srv_prefix) :]
            if remainder and "/" not in remainder:
                rem_lower = remainder.lower()
                _, ext = os.path.splitext(rem_lower)
                if (
                    rem_lower == "hon-x86_64-server"
                    or ext in (".dll", ".exe", ".so", ".sh", ".bin")
                    or ".so." in rem_lower
                ):
                    return True
            if lower.startswith(f"{srv_prefix}libs-x86_64/"):
                return len(norm_path) > len(f"{srv_prefix}libs-x86_64/")

    return False


def classify_path_variants(norm_path: str) -> set[str]:
    """
    Map a relative bundle path to the target CDN OS variants ('wac', 'lac', 'mac', 'las', 'was').
    Cross-platform loose assets (e.g. games/client/override/**) map to all 3 client variants.
    """
    lower = norm_path.lower()
    fname = norm_path.split("/")[-1].lower()
    _, ext = os.path.splitext(fname)

    # Cross-platform client VFS loose tiers
    if lower.startswith("games/client/override/"):
        return {"wac", "lac", "mac"}

    # Explicit OS client overlays
    if lower.startswith("games/client/linux-x64/"):
        return {"lac"}
    if lower.startswith("games/client/macos-x64/"):
        return {"mac"}
    if lower.startswith("games/client/windows-x64/"):
        return {"wac"}

    # Root games/client/<leaf>
    if lower.startswith("games/client/") and "/" not in lower[len("games/client/") :]:
        if ext in (".exe", ".dll"):
            return {"wac"}
        if ext == ".dylib":
            return {"mac"}
        if ext == ".so" or ".so." in fname:
            return {"lac"}
        return {"wac", "lac", "mac"}

    # Dedicated / P2P server binaries
    if lower.startswith(("games/server/", "games/server_p2p/")):
        is_p2p = lower.startswith("games/server_p2p/")
        if ext in (".exe", ".dll"):
            return {"wac"} if is_p2p else {"wac", "was"}
        if (
            ext in (".so", ".sh", ".bin")
            or ".so." in fname
            or fname == "hon-x86_64-server"
            or "/libs-x86_64/" in lower
        ):
            return {"lac"} if is_p2p else {"lac", "las"}
        return {"wac", "lac", "las", "was"}

    return {"wac", "lac", "mac"}


def _validate_transcript_safe_scalar(value: str | None, field_name: str) -> None:
    if value and any(ch in value for ch in ("\t", "\r", "\n", "\x00")):
        raise ValueError(f"Control character forbidden in manifest {field_name}.")


def _validate_transcript_safe_path(path_val: str, collection_name: str) -> None:
    if any(ch in path_val for ch in ("\t", "\r", "\n", "\x00", "|")):
        raise ValueError(
            f"Control character or transcript delimiter forbidden in {collection_name} path: '{path_val}'"
        )


def build_canonical_transcript(manifest_dict: dict[str, Any]) -> bytes:
    """
    Construct the exact UTF-8 (no BOM, LF) canonical signing transcript matching
    `NEXUS.Contracts.Cdn.ManifestSignatureVerifier.BuildCanonicalTranscript`.
    """
    raw_protocol = manifest_dict.get("protocol")
    raw_version = manifest_dict.get("version")
    raw_mode = manifest_dict.get("mode")
    raw_hash_alg = manifest_dict.get("hashAlgorithm")
    raw_obj_layout = manifest_dict.get("objectLayout")

    protocol = "" if raw_protocol is None else str(raw_protocol)
    version = "" if raw_version is None else str(raw_version)
    sequence = int(manifest_dict.get("sequence", 0))
    mode_str = "" if raw_mode is None else str(raw_mode)
    hash_alg_str = "" if raw_hash_alg is None else str(raw_hash_alg)
    obj_layout_str = "" if raw_obj_layout is None else str(raw_obj_layout)

    _validate_transcript_safe_scalar(protocol, "Protocol")
    _validate_transcript_safe_scalar(version, "Version")
    _validate_transcript_safe_scalar(mode_str, "Mode")
    _validate_transcript_safe_scalar(hash_alg_str, "HashAlgorithm")
    _validate_transcript_safe_scalar(obj_layout_str, "ObjectLayout")

    mode = "mirror" if mode_str.strip() == "" else mode_str.strip().lower()
    hash_alg = hash_alg_str.strip().upper()
    obj_layout = obj_layout_str.strip().lower()

    def join_sorted(items: list[str] | None, col_name: str) -> str:
        if not items:
            return ""
        for p in items:
            _validate_transcript_safe_path(p, col_name)
        normed = sorted(p.replace("\\", "/") for p in items)
        return "|".join(normed)

    lines = [
        CANONICAL_HEADER,
        f"protocol={protocol}",
        f"version={version}",
        f"sequence={sequence}",
        f"mode={mode}",
        f"hashAlgorithm={hash_alg}",
        f"objectLayout={obj_layout}",
        f"excludeFromSource={join_sorted(manifest_dict.get('excludeFromSource'), 'ExcludeFromSource')}",
        f"excludeFromTarget={join_sorted(manifest_dict.get('excludeFromTarget'), 'ExcludeFromTarget')}",
        f"executablePaths={join_sorted(manifest_dict.get('executablePaths'), 'ExecutablePaths')}",
        f"remove={join_sorted(manifest_dict.get('remove'), 'Remove')}",
        "files=",
    ]

    files_map: dict[str, dict[str, Any]] = manifest_dict.get("files") or {}
    ordered_pairs = sorted(
        ((k.replace("\\", "/"), v) for k, v in files_map.items()),
        key=lambda item: item[0],
    )
    for raw_k in files_map.keys():
        _validate_transcript_safe_path(raw_k, "Files")
    for norm_key, entry in ordered_pairs:
        raw_h = "" if entry.get("hash") is None else str(entry.get("hash"))
        _validate_transcript_safe_scalar(raw_h, "Files[].Hash")
        size_val = int(entry.get("size", 0) or 0)
        hash_val = raw_h.strip().lower()
        lines.append(f"{norm_key}\t{size_val}\t{hash_val}")

    return ("\n".join(lines) + "\n").encode("utf-8")


def sign_manifest_ieee_p1363(manifest_dict: dict[str, Any], private_key: ec.EllipticCurvePrivateKey) -> str:
    """Sign canonical transcript using ECDSA P-256 SHA-256 and encode as 64-byte IEEE P1363 (r || s) base64."""
    transcript = build_canonical_transcript(manifest_dict)
    der_sig = private_key.sign(transcript, ec.ECDSA(hashes.SHA256()))
    r, s = decode_dss_signature(der_sig)
    p1363 = r.to_bytes(32, "big") + s.to_bytes(32, "big")
    return base64.b64encode(p1363).decode("ascii")


def verify_manifest_ieee_p1363(
    manifest_dict: dict[str, Any],
    public_key_x_hex: str = DEFAULT_PUBLIC_KEY_X_HEX,
    public_key_y_hex: str = DEFAULT_PUBLIC_KEY_Y_HEX,
) -> bool:
    """Verify a 64-byte IEEE P1363 (r || s) ECDSA P-256 SHA-256 signature against (X, Y) coordinates."""
    sig_b64 = manifest_dict.get("signature")
    if not sig_b64 or not isinstance(sig_b64, str):
        return False
    try:
        raw = base64.b64decode(sig_b64, validate=True)
    except Exception:
        return False
    if len(raw) != 64:
        return False

    r = int.from_bytes(raw[:32], "big")
    s = int.from_bytes(raw[32:], "big")
    der_sig = encode_dss_signature(r, s)

    x_int = int.from_bytes(bytes.fromhex(public_key_x_hex), "big")
    y_int = int.from_bytes(bytes.fromhex(public_key_y_hex), "big")
    pub_nums = ec.EllipticCurvePublicNumbers(x_int, y_int, ec.SECP256R1())
    pub_key = pub_nums.public_key()

    transcript = build_canonical_transcript(manifest_dict)
    try:
        pub_key.verify(der_sig, transcript, ec.ECDSA(hashes.SHA256()))
        return True
    except Exception:
        return False


def validate_pkv_v5_manifest_dict(
    manifest_dict: dict[str, Any],
    require_signature: bool = True,
    public_key_x_hex: str | None = None,
    public_key_y_hex: str | None = None,
) -> tuple[bool, str | None]:
    """Python mirror of `NEXUS.Contracts.Cdn.PkvManifestValidator.Verify`."""
    if str(manifest_dict.get("protocol", "")).lower() != "pkv-v5":
        return False, f"Protocol must be 'pkv-v5', got '{manifest_dict.get('protocol')}'."
    try:
        validate_pkv_v5_version(str(manifest_dict.get("version", "")))
    except ValueError as exc:
        return False, str(exc)
    if str(manifest_dict.get("mode", "")).lower() != "overlay":
        return False, f"Mode must be 'overlay', got '{manifest_dict.get('mode')}'."
    if str(manifest_dict.get("objectLayout", "")).lower() != "sha256":
        return False, f"ObjectLayout must be 'sha256', got '{manifest_dict.get('objectLayout')}'."
    if str(manifest_dict.get("hashAlgorithm", "")).upper() not in ("SHA-256", "SHA256"):
        return False, f"HashAlgorithm must be 'SHA-256', got '{manifest_dict.get('hashAlgorithm')}'."
    seq = manifest_dict.get("sequence", 0)
    if not isinstance(seq, int) or seq < 1:
        return False, f"Sequence must be an integer >= 1, got '{seq}'."

    files_map = manifest_dict.get("files")
    if not isinstance(files_map, dict):
        return False, "'files' must be a JSON object."
    for rel_path, entry in files_map.items():
        validate_overlay_relpath(rel_path, int(entry["size"]))
        h = str(entry.get("hash", ""))
        if not re.fullmatch(r"[0-9a-f]{64}", h):
            return False, f"Entry '{rel_path}' has invalid lowercase SHA-256 hash '{h}'."

    if require_signature:
        if public_key_x_hex and public_key_y_hex:
            if not verify_manifest_ieee_p1363(manifest_dict, public_key_x_hex, public_key_y_hex):
                return False, "ECDSA P-256 IEEE P1363 signature verification failed against override public key."
        else:
            ok_default = verify_manifest_ieee_p1363(
                manifest_dict, DEFAULT_PUBLIC_KEY_X_HEX, DEFAULT_PUBLIC_KEY_Y_HEX
            )
            ok_recovery = verify_manifest_ieee_p1363(
                manifest_dict, RECOVERY_PUBLIC_KEY_X_HEX, RECOVERY_PUBLIC_KEY_Y_HEX
            )
            if not (ok_default or ok_recovery):
                return False, "ECDSA P-256 IEEE P1363 signature verification failed against PKV public keys."

    return True, None


def compute_sha256_and_size(path: Path) -> tuple[str, int]:
    h = hashlib.sha256()
    total = 0
    with path.open("rb") as fh:
        while True:
            chunk = fh.read(131072)
            if not chunk:
                break
            h.update(chunk)
            total += len(chunk)
    return h.hexdigest().lower(), total


def generate_pkv_cdn_tree(
    output_dir: Path,
    version: str,
    variants: list[str] | tuple[str, ...] = DEFAULT_CLIENT_VARIANTS,
    sequence: int | None = None,
    bundle_root: Path | None = None,
    override_dir: Path | None = None,
    explicit_files: list[str] | None = None,
    remove_paths: list[str] | None = None,
    baseline_empty: bool = False,
    signing_key_pem_path: Path | None = None,
    generate_ephemeral_key: bool = False,
    allow_unsigned_loopback: bool = False,
) -> dict[str, Any]:
    """
    Generate deterministic pkv-v5 manifests (`wac`, `lac`, `mac`, etc.), CAS objects (`objects/ab/<sha256>`),
    release snapshots (`releases/<version>/<variant>/manifest.json`), and `pkv-info.json`.
    """
    validate_pkv_v5_version(version)
    for v in variants:
        if v not in SUPPORTED_VARIANTS:
            raise ValueError(f"Unsupported CDN variant '{v}'. Supported: {SUPPORTED_VARIANTS}")

    if not signing_key_pem_path and not generate_ephemeral_key and not allow_unsigned_loopback:
        raise ValueError(
            "Signing configuration required: pass --signing-key-path <pem>, --generate-ephemeral-key "
            "(cleanroom test/CI verification), or --allow-unsigned-loopback (local 127.0.0.1 /cdn dev mode)."
        )

    private_key: ec.EllipticCurvePrivateKey | None = None
    pub_x_hex: str | None = None
    pub_y_hex: str | None = None

    if signing_key_pem_path is not None:
        pem_bytes = signing_key_pem_path.read_bytes()
        loaded = serialization.load_pem_private_key(pem_bytes, password=None)
        if not isinstance(loaded, ec.EllipticCurvePrivateKey) or not isinstance(
            loaded.curve, ec.SECP256R1
        ):
            raise ValueError(
                f"Signing key '{signing_key_pem_path}' must be an ECDSA NIST P-256 (secp256r1) PEM private key."
            )
        private_key = loaded
        nums = private_key.public_key().public_numbers()
        pub_x_hex = nums.x.to_bytes(32, "big").hex()
        pub_y_hex = nums.y.to_bytes(32, "big").hex()
    elif generate_ephemeral_key:
        private_key = ec.generate_private_key(ec.SECP256R1())
        nums = private_key.public_key().public_numbers()
        pub_x_hex = nums.x.to_bytes(32, "big").hex()
        pub_y_hex = nums.y.to_bytes(32, "big").hex()

    output_dir.mkdir(parents=True, exist_ok=True)
    seq_state_path = output_dir / "sequence_state.json"
    prev_seq = 0
    if seq_state_path.is_file():
        try:
            prev_seq = int(json.loads(seq_state_path.read_text(encoding="utf-8")).get("lastSequence", 0))
        except Exception:
            prev_seq = 0

    resolved_sequence = sequence if sequence is not None else (prev_seq + 1)
    if resolved_sequence < 1:
        raise ValueError(f"Sequence must be >= 1, got {resolved_sequence}.")
    if prev_seq > 0 and resolved_sequence < prev_seq:
        raise ValueError(
            f"Monotonic sequence violation: requested sequence {resolved_sequence} < previous {prev_seq}."
        )

    # Collect candidate (norm_rel_path -> source_file_path)
    collected: dict[str, Path] = {}
    if not baseline_empty:
        if override_dir is not None:
            if not override_dir.is_dir():
                raise FileNotFoundError(f"Override source directory '{override_dir}' does not exist.")
            for dp, _dns, fns in os.walk(override_dir):
                rel_d = os.path.relpath(dp, override_dir).replace("\\", "/")
                for fn in sorted(fns):
                    fp = Path(dp) / fn
                    sub_rel = fn if rel_d == "." else f"{rel_d}/{fn}"
                    norm_rel = normalize_rel_path(f"games/client/override/{sub_rel}")
                    # Skip local dev artifacts (.bak/.log/.s2z) quietly when scanning override directory
                    try:
                        validate_overlay_relpath(norm_rel, fp.stat().st_size)
                    except ValueError:
                        continue
                    collected[norm_rel] = fp

        if explicit_files:
            if bundle_root is None:
                raise ValueError("--bundle-root is required when --files are specified.")
            for raw_rel in explicit_files:
                norm_rel = normalize_rel_path(raw_rel)
                fp = bundle_root / Path(norm_rel)
                if not fp.is_file():
                    raise FileNotFoundError(
                        f"Explicit manifest file '{norm_rel}' not found under '{bundle_root}'."
                    )
                validate_overlay_relpath(norm_rel, fp.stat().st_size)
                collected[norm_rel] = fp

    normalized_removes: list[str] = []
    if remove_paths:
        for rp in sorted({normalize_rel_path(x) for x in remove_paths if x.strip()}):
            validate_overlay_relpath(rp, None)
            if not rp.startswith("games/client/override/") or rp.startswith(
                "games/client/override/core/shaders/"
            ):
                raise ValueError(
                    f"Destructive Remove is only permitted under 'games/client/override/' "
                    f"(excluding core/shaders/), got '{rp}'."
                )
            normalized_removes.append(rp)

    # Hash & stage deduplicated CAS objects under objects/<ab>/<sha256>
    objects_root = output_dir / "objects"
    cas_written = 0
    hashed_entries: dict[str, dict[str, Any]] = {}

    for norm_rel in sorted(collected.keys()):
        src_fp = collected[norm_rel]
        # Verify macOS Mach-O code signature presence before publishing to `mac`
        target_vars = classify_path_variants(norm_rel)
        if "mac" in target_vars and (
            norm_rel.lower().endswith(".dylib") or norm_rel.endswith("/HoN64")
        ):
            minfo = parse_macho(src_fp)
            if not all(s["has_code_signature"] for s in minfo["slices"]):
                raise ValueError(
                    f"macOS Mach-O binary '{norm_rel}' is missing an embedded LC_CODE_SIGNATURE! "
                    f"Ad-hoc sign via 'codesign --force --sign -' before generating CDN manifests."
                )

        sha256_hex, size_bytes = compute_sha256_and_size(src_fp)
        prefix2 = sha256_hex[:2]
        cas_dest = objects_root / prefix2 / sha256_hex
        if not cas_dest.is_file():
            cas_dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src_fp, cas_dest)
            cas_written += 1

        hashed_entries[norm_rel] = {
            "size": size_bytes,
            "hash": sha256_hex,
            "variants": target_vars,
            "executable": is_posix_executable_relpath(norm_rel),
        }

    generated_manifests: dict[str, str] = {}
    r2_upload_entries: list[dict[str, str]] = []

    for variant in variants:
        variant_files: dict[str, dict[str, Any]] = {}
        exec_paths: list[str] = []

        for norm_rel in sorted(hashed_entries.keys()):
            meta = hashed_entries[norm_rel]
            if variant in meta["variants"]:
                variant_files[norm_rel] = {
                    "size": meta["size"],
                    "hash": meta["hash"],
                }
                if meta["executable"] and variant in ("lac", "mac", "las"):
                    exec_paths.append(norm_rel)

        variant_removes = (
            list(normalized_removes) if variant in ("wac", "lac", "mac") else []
        )

        manifest_obj: dict[str, Any] = {
            "protocol": "pkv-v5",
            "version": version,
            "sequence": resolved_sequence,
            "hashAlgorithm": "SHA-256",
            "excludeFromSource": [],
            "excludeFromTarget": [],
            "files": variant_files,
            "mode": "overlay",
            "objectLayout": "sha256",
            "remove": variant_removes,
            "executablePaths": sorted(exec_paths),
            "signature": None,
        }

        if private_key is not None:
            manifest_obj["signature"] = sign_manifest_ieee_p1363(manifest_obj, private_key)

        is_valid, err_reason = validate_pkv_v5_manifest_dict(
            manifest_obj,
            require_signature=(private_key is not None),
            public_key_x_hex=pub_x_hex,
            public_key_y_hex=pub_y_hex,
        )
        if not is_valid:
            raise RuntimeError(
                f"Self-verification failed for '{variant}/manifest.json': {err_reason}"
            )

        manifest_json = json.dumps(manifest_obj, indent=2) + "\n"

        live_path = output_dir / variant / "manifest.json"
        live_path.parent.mkdir(parents=True, exist_ok=True)
        live_path.write_text(manifest_json, encoding="utf-8", newline="\n")

        rel_snap_path = output_dir / "releases" / version / variant / "manifest.json"
        rel_snap_path.parent.mkdir(parents=True, exist_ok=True)
        rel_snap_path.write_text(manifest_json, encoding="utf-8", newline="\n")

        generated_manifests[variant] = str(live_path)
        r2_upload_entries.append(
            {
                "localPath": f"{variant}/manifest.json",
                "r2Key": f"{variant}/manifest.json",
                "contentType": "application/json",
                "cacheControl": "public, max-age=30, must-revalidate",
            }
        )
        r2_upload_entries.append(
            {
                "localPath": f"releases/{version}/{variant}/manifest.json",
                "r2Key": f"releases/{version}/{variant}/manifest.json",
                "contentType": "application/json",
                "cacheControl": "public, max-age=31536000, immutable",
            }
        )
        log(
            f"Generated '{variant}/manifest.json' (version={version}, seq={resolved_sequence}, "
            f"files={len(variant_files)}, executables={len(exec_paths)}, signed={private_key is not None})."
        )

    # Record CAS object upload entries
    seen_hashes: set[str] = set()
    for meta in hashed_entries.values():
        h = meta["hash"]
        if h in seen_hashes:
            continue
        seen_hashes.add(h)
        obj_rel = f"objects/{h[:2]}/{h}"
        r2_upload_entries.append(
            {
                "localPath": obj_rel,
                "r2Key": obj_rel,
                "contentType": "application/octet-stream",
                "cacheControl": "public, max-age=31536000, immutable",
            }
        )

    now_iso = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    pkv_info = {
        "protocol": "pkv-v5",
        "version": version,
        "sequence": resolved_sequence,
        "mode": "overlay",
        "objectLayout": "sha256",
        "hashAlgorithm": "SHA-256",
        "variants": list(variants),
        "signed": private_key is not None,
        "generatedAtUtc": now_iso,
    }
    pkv_info_path = output_dir / "pkv-info.json"
    pkv_info_path.write_text(json.dumps(pkv_info, indent=2) + "\n", encoding="utf-8", newline="\n")
    r2_upload_entries.append(
        {
            "localPath": "pkv-info.json",
            "r2Key": "pkv-info.json",
            "contentType": "application/json",
            "cacheControl": "public, max-age=60, must-revalidate",
        }
    )

    seq_state_path.write_text(
        json.dumps(
            {
                "lastSequence": resolved_sequence,
                "version": version,
                "updatedAtUtc": now_iso,
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
        newline="\n",
    )

    if generate_ephemeral_key and pub_x_hex and pub_y_hex:
        eph_pub_path = output_dir / "ephemeral_signing_public_key.json"
        eph_pub_path.write_text(
            json.dumps(
                {
                    "curve": "NIST P-256 (secp256r1)",
                    "signatureEncoding": "IEEE P1363 (r || s, 64 bytes)",
                    "publicKeyXHex": pub_x_hex,
                    "publicKeyYHex": pub_y_hex,
                },
                indent=2,
            )
            + "\n",
            encoding="utf-8",
            newline="\n",
        )

    upload_plan = {
        "protocol": "pkv-v5",
        "bucket": os.environ.get("KONGOR_CDN_BUCKET", "kongor-patches"),
        "cdnDomain": os.environ.get("KONGOR_CDN_DOMAIN", "").strip() or "https://cdn.example.com/",
        "version": version,
        "sequence": resolved_sequence,
        "objects": r2_upload_entries,
    }
    (output_dir / "r2_upload_plan.json").write_text(
        json.dumps(upload_plan, indent=2) + "\n", encoding="utf-8", newline="\n"
    )

    return {
        "outputDir": str(output_dir),
        "version": version,
        "sequence": resolved_sequence,
        "variants": list(variants),
        "manifests": generated_manifests,
        "uniqueCasObjects": len(seen_hashes),
        "newCasObjectsWritten": cas_written,
        "signed": private_key is not None,
        "publicKeyXHex": pub_x_hex,
        "publicKeyYHex": pub_y_hex,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Generate deterministic Multi-OS pkv-v5 CDN manifests (wac, lac, mac) and SHA-256 CAS objects."
    )
    parser.add_argument("--repo-root", default=".", help="Path to ProjectKongor repository root.")
    parser.add_argument(
        "--output-dir",
        default="artifacts/cdn-pkv-v5",
        help="Target CDN staging root for {wac,lac,mac}/manifest.json, pkv-info.json, and objects/ab/<sha256>.",
    )
    parser.add_argument("--version", default="", help="Platform SemVer >= 5.0.0 (defaults to Directory.Build.props).")
    parser.add_argument("--sequence", type=int, default=0, help="Explicit monotonic sequence >= 1 (0 = auto-increment).")
    parser.add_argument(
        "--variants",
        default="wac,lac,mac",
        help="Comma-separated CDN variants to emit (default: wac,lac,mac).",
    )
    parser.add_argument("--bundle-root", default="", help="Root directory for resolving --files relative paths.")
    parser.add_argument("--override-from", default="", help="Optional games/client/override directory to scan.")
    parser.add_argument("--files", nargs="*", default=[], help="Explicit bundle-relative file paths to include.")
    parser.add_argument("--remove", nargs="*", default=[], help="Explicit override relative paths to remove.")
    parser.add_argument("--baseline-empty", action="store_true", help="Emit clean 0-file baseline overlay manifests.")
    parser.add_argument("--signing-key-path", default="", help="Path to ECDSA P-256 PEM private signing key.")
    parser.add_argument(
        "--generate-ephemeral-key",
        action="store_true",
        help="Sign manifests with an in-memory ephemeral ECDSA P-256 keypair (writes ephemeral_signing_public_key.json).",
    )
    parser.add_argument(
        "--allow-unsigned-loopback",
        action="store_true",
        help="Emit unsigned manifests (signature: null) for local 127.0.0.1 /cdn development only.",
    )
    args = parser.parse_args(argv)

    repo_root = Path(args.repo_root).resolve()
    version = args.version.strip() if args.version.strip() else read_platform_version(repo_root)
    variants = [v.strip() for v in args.variants.split(",") if v.strip()]
    seq = args.sequence if args.sequence > 0 else None

    key_str = args.signing_key_path.strip() or os.environ.get("KONGOR_CDN_SIGNING_KEY_PATH", "").strip()
    key_path = Path(key_str).resolve() if key_str else None

    summary = generate_pkv_cdn_tree(
        output_dir=Path(args.output_dir).resolve(),
        version=version,
        variants=variants,
        sequence=seq,
        bundle_root=Path(args.bundle_root).resolve() if args.bundle_root.strip() else None,
        override_dir=Path(args.override_from).resolve() if args.override_from.strip() else None,
        explicit_files=list(args.files) if args.files else None,
        remove_paths=list(args.remove) if args.remove else None,
        baseline_empty=args.baseline_empty,
        signing_key_pem_path=key_path,
        generate_ephemeral_key=args.generate_ephemeral_key,
        allow_unsigned_loopback=args.allow_unsigned_loopback,
    )
    log(
        f"Completed pkv-v5 CDN generation in '{summary['outputDir']}' "
        f"(variants={summary['variants']}, uniqueCasObjects={summary['uniqueCasObjects']}, signed={summary['signed']})."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())

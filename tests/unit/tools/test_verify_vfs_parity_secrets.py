# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Value-Weighted Testing Gate:
  - protects: Gate #2 (`scan-secrets`) security boundary in tools/export/verify_vfs_parity.py,
    preventing leaked developer seed credentials, identity strings, hash seeds, and wrapped COMPEL
    configuration values from escaping into distributed playtest bundles.
  - fails_when:
      (1) Wrapped JSON secret value objects like {"Password": {"Value": "hunter2"}} bypass scan_json_payload;
      (2) Placeholder values ("...", "***") are falsely flagged as secret credentials;
      (3) Dev seed identities (dev_seed_canary@proton.me, host_kongor_host, @nexus.local) or seed hashes
          (9fb9cf192701aba7aaa38e6f15457082, AQAAAAIAAYagAAAAEMUkpLAr01Nj) in text/YAML files are skipped;
      (4) Dev seed identity in UTF-16LE / ASCII native binaries (.dll / .exe) is skipped by the cheap pre-filter; or
      (5) A clean bundle directory is falsely rejected.
  - seam: Real `vvp.cmd_scan_secrets` execution against real filesystem trees in `tmp_path` bundle roots
    with full report JSON serialization.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

REPO_ROOT = Path(__file__).resolve().parents[3]
EXPORT_TOOLS_DIR = REPO_ROOT / "tools" / "export"
if str(EXPORT_TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(EXPORT_TOOLS_DIR))

import verify_vfs_parity as vvp


def _run_scan(bundle_root: Path, json_out: Path | None = None) -> tuple[int, dict]:
    report_file = json_out or (bundle_root / "_report.json")
    args = argparse.Namespace(
        bundle_root=str(bundle_root),
        json_out=str(report_file),
        skip_cinematic_videos=False,
        include_test_videos=False,
    )
    rc = vvp.cmd_scan_secrets(args)
    report_data = {}
    if report_file.is_file():
        report_data = json.loads(report_file.read_text(encoding="utf-8"))
    return rc, report_data


def test_compel_json_with_secret_value_detected(tmp_path: Path) -> None:
    """Test 1: COMPEL.json with {"Password": {"Value": "hunter2"}} -> returns 1 and report has finding on .Value."""
    bundle_dir = tmp_path / "bundle1"
    bundle_dir.mkdir(parents=True)
    compel_file = bundle_dir / "COMPEL.json"
    compel_file.write_text(
        json.dumps({"Password": {"Value": "hunter2"}}, indent=2),
        encoding="utf-8",
    )

    rc, report = _run_scan(bundle_dir)

    assert rc == 1
    assert report.get("passed") is False
    assert report.get("findingCount", 0) >= 1

    value_findings = [
        f for f in report.get("findings", [])
        if f.get("location", "").endswith(".Value")
    ]
    assert len(value_findings) >= 1
    finding = value_findings[0]
    assert finding["rule"] == "json_secret_value_detected"
    assert finding["category"] == "CRED"


def test_compel_json_with_placeholders_allowed(tmp_path: Path) -> None:
    """Test 2: COMPEL.json with {"Password": {"Value": "..."}, "AuthenticationToken": {"Value": "..."}} -> 0 findings (returns 0)."""
    bundle_dir = tmp_path / "bundle2"
    bundle_dir.mkdir(parents=True)
    compel_file = bundle_dir / "COMPEL.json"
    compel_file.write_text(
        json.dumps(
            {
                "Password": {"Value": "..."},
                "AuthenticationToken": {"Value": "..."},
            },
            indent=2,
        ),
        encoding="utf-8",
    )

    rc, report = _run_scan(bundle_dir)

    assert rc == 0
    assert report.get("passed") is True
    assert report.get("findingCount", 0) == 0
    assert len(report.get("findings", [])) == 0


def test_seed_identity_and_hash_in_text_and_yaml(tmp_path: Path) -> None:
    """Test 3: .txt (and .yml) containing dev_seed_canary@proton.me or 9fb9cf192701aba7aaa38e6f15457082 -> returns 1."""
    # Subtest 3a: .txt with dev seed identity
    bundle_txt = tmp_path / "bundle_txt"
    bundle_txt.mkdir(parents=True)
    (bundle_txt / "seeds.txt").write_text(
        "admin_contact: dev_seed_canary@proton.me\n", encoding="utf-8"
    )
    rc_txt, report_txt = _run_scan(bundle_txt)
    assert rc_txt == 1
    assert report_txt.get("passed") is False
    rules_txt = [f["rule"] for f in report_txt.get("findings", [])]
    assert "dev_seed_identity" in rules_txt

    # Subtest 3b: .yml with dev seed hash
    bundle_yml = tmp_path / "bundle_yml"
    bundle_yml.mkdir(parents=True)
    (bundle_yml / "docker-compose.yml").write_text(
        "services:\n  db:\n    environment:\n      SEED_HASH: 9fb9cf192701aba7aaa38e6f15457082\n",
        encoding="utf-8",
    )
    rc_yml, report_yml = _run_scan(bundle_yml)
    assert rc_yml == 1
    assert report_yml.get("passed") is False
    rules_yml = [f["rule"] for f in report_yml.get("findings", [])]
    assert "dev_seed_hash" in rules_yml


def test_dev_seed_identity_detected_in_utf16le_binary(tmp_path: Path) -> None:
    """Test 4: Fake PE .dll (b'MZ' + b'\\x00'*64 + 'host_kongor_host@nexus.local'.encode('utf-16le') + b'\\x00'*64) -> returns 1."""
    bundle_dir = tmp_path / "bundle_bin"
    bundle_dir.mkdir(parents=True)
    dll_path = bundle_dir / "cgame_x64.dll"
    payload = (
        b"MZ"
        + b"\x00" * 64
        + "host_kongor_host@nexus.local".encode("utf-16le")
        + b"\x00" * 64
    )
    dll_path.write_bytes(payload)

    rc, report = _run_scan(bundle_dir)

    assert rc == 1
    assert report.get("passed") is False
    rules = [f["rule"] for f in report.get("findings", [])]
    assert "dev_seed_identity" in rules
    categories = [f["category"] for f in report.get("findings", [])]
    assert "PII" in categories


def test_clean_bundle_passes(tmp_path: Path) -> None:
    """Test 5: Clean bundle directory -> returns 0 (passed: True)."""
    bundle_dir = tmp_path / "bundle_clean"
    bundle_dir.mkdir(parents=True)
    (bundle_dir / "init.cfg").write_text("setsave vid_maxFPS 144\n", encoding="utf-8")
    (bundle_dir / "config.json").write_text(
        json.dumps({"serverName": "Project Kongor Community Server", "maxPlayers": 10}),
        encoding="utf-8",
    )
    (bundle_dir / "service.yml").write_text(
        "version: '3.8'\nservices:\n  web:\n    image: nginx:alpine\n",
        encoding="utf-8",
    )

    rc, report = _run_scan(bundle_dir)

    assert rc == 0
    assert report.get("passed") is True
    assert report.get("findingCount", 0) == 0
    assert len(report.get("findings", [])) == 0


def test_msbuild_deps_and_hon_ability_metadata_json_key_false_positives_exempted(tmp_path: Path) -> None:
    """Test 6: MSBuild .deps.json with UserSecrets and ability_metadata.json with Shop_Secret_name -> 0 findings (returns 0)."""
    bundle_dir = tmp_path / "bundle_exempt"
    deps_file = bundle_dir / "services-win-x64" / "master-server" / "KONGOR.MasterServer.deps.json"
    deps_file.parent.mkdir(parents=True, exist_ok=True)
    deps_file.write_text(
        json.dumps({"libraries": {"Microsoft.Extensions.Configuration.UserSecrets/11.0.0": {"type": "package"}}}, indent=2),
        encoding="utf-8",
    )

    metadata_file = bundle_dir / "services-win-x64" / "web-portal-ui" / "wwwroot" / "ability_metadata.json"
    metadata_file.parent.mkdir(parents=True, exist_ok=True)
    metadata_file.write_text(
        json.dumps({"Shop_Secret_name": "Secret", "State_ShopAccess_Secret_description": "Observatory / Secret shop"}, indent=2),
        encoding="utf-8",
    )

    rc, report = _run_scan(bundle_dir)

    assert rc == 0
    assert report.get("passed") is True
    assert report.get("findingCount", 0) == 0
    assert len(report.get("findings", [])) == 0


def test_exempted_json_files_still_scanned_for_text_secrets(tmp_path: Path) -> None:
    """Test 7: Exempted .deps.json with leaked secret text/seed -> returns 1 (caught by scan_text_payload)."""
    bundle_dir = tmp_path / "bundle_leaked_deps"
    deps_file = bundle_dir / "KONGOR.MasterServer.deps.json"
    deps_file.parent.mkdir(parents=True, exist_ok=True)
    deps_file.write_text(
        json.dumps({"contact": "dev_seed_canary@proton.me"}, indent=2),
        encoding="utf-8",
    )

    rc, report = _run_scan(bundle_dir)

    assert rc == 1
    assert report.get("passed") is False
    assert report.get("findingCount", 0) >= 1
    rules = [f["rule"] for f in report.get("findings", [])]
    assert "dev_seed_identity" in rules


def test_lowercase_http_users_routes_and_starkbank_vendor_exempted_while_real_os_paths_caught(tmp_path: Path) -> None:
    """Test 8: Lowercase REST routes (/users/accounts/, /users/tokens/) & StarkbankEcdsa vendor prefix pass, while real macOS /Users/alice/ & Windows C:\\Users\\alice\\ fail."""
    bundle_clean = tmp_path / "bundle_routes_clean"
    bundle_clean.mkdir(parents=True)
    ui_dll = bundle_clean / "DAWNBRINGER.WebPortal.UI.dll"
    ui_dll.write_bytes(
        b"MZ"
        + b"\x00" * 32
        + "/users/accounts/123".encode("utf-16le")
        + b"\x00" * 16
        + "/users/tokens/".encode("utf-16le")
        + b"\x00" * 16
        + b"/Users/caiodottori/projects/ecdsa-dotnet/obj/Release/StarkbankEcdsa.pdb"  # kongor-security-ignore
        + b"\x00" * 32
    )
    rc_clean, report_clean = _run_scan(bundle_clean)
    assert rc_clean == 0
    assert report_clean.get("passed") is True
    assert report_clean.get("findingCount", 0) == 0

    bundle_leak = tmp_path / "bundle_real_macos_leak"
    bundle_leak.mkdir(parents=True)
    leaked_dll = bundle_leak / "CustomTool.dll"
    leaked_dll.write_bytes(
        b"MZ"
        + b"\x00" * 32
        + b"/Users/alice/PrivateRepo/CustomTool.cs"
        + b"\x00" * 32
    )
    rc_leak, report_leak = _run_scan(bundle_leak)
    assert rc_leak == 1
    assert report_leak.get("passed") is False
    rules_leak = [f["rule"] for f in report_leak.get("findings", [])]
    assert "binary_local_user_path_leak" in rules_leak


def test_dev_excluded_source_file_matches_export_allowlist() -> None:
    """Test 9: .disabled, .disabled_<ts>, and .manifest.xml.zip are excluded, while _disabled.dds UI textures stay included."""
    assert vvp.is_dev_excluded_source_file("ui/fe2/newui/communicator_v2.package.disabled_20261011_160042") is True
    assert vvp.is_dev_excluded_source_file("ui/fe2/newui/homepage_v2.package.disabled_20261011_173625") is True
    assert vvp.is_dev_excluded_source_file("ui/scripts/newui/definitions/heroes/hero_pyromancertutorial.lua.disabled") is True
    assert vvp.is_dev_excluded_source_file("4.10.1.0.manifest.xml.zip") is True
    assert vvp.is_dev_excluded_source_file("4.10.1.0.manifest.xml.zip.disabled") is True
    assert vvp.is_dev_excluded_source_file("rollback_journal.json") is True

    assert vvp.is_dev_excluded_source_file("ui/elements/button_bevel_disabled.dds") is False
    assert vvp.is_dev_excluded_source_file("reborn_assets/reborn_heroes/rampage/ability_01/state_disabled.entity") is False


def test_main_interface_included_in_sanitize_staged_targets() -> None:
    """Test 10: ui/fe2/main.interface is sanitized identically to main_v2.interface with 0 residual findings."""
    assert "main.interface" in vvp.TARGET_SANITIZE_LEAFS
    src_xml = (REPO_ROOT / "games" / "client" / "ClientPackageFiles" / "ui" / "fe2" / "main.interface")
    if not src_xml.is_file(): src_xml = (REPO_ROOT / "games" / "client" / "override" / "ui" / "fe2" / "main.interface")
    raw_xml = src_xml.read_text(encoding="utf-8")
    sanitized_xml, edits = vvp.sanitize_staged_text(raw_xml, "main.interface")
    assert edits >= 0
    findings: list[dict] = []
    vvp.scan_text_payload("override/ui/fe2/main.interface", sanitized_xml, findings)
    assert findings == []

    # Synthetic dirty main.interface payload must be scrubbed cleanly
    dirty_xml = (
        '<interface name="main">\n'
        "  <panel onload=\"CreateString('p2p_nickname', 'canary_dev_user'); Set('login_name', 'canary_dev_user');\" nick='canary_dev_user'/>\n"
        "</interface>\n"
    )
    scrubbed_dirty, dirty_edits = vvp.sanitize_staged_text(dirty_xml, "main.interface")
    assert dirty_edits >= 3
    assert "canary_dev_user" not in scrubbed_dirty
    dirty_findings: list[dict] = []
    vvp.scan_text_payload("override/ui/fe2/main.interface", scrubbed_dirty, dirty_findings)
    assert dirty_findings == []

# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Value-Weighted Canary Tests for Cleanroom Secret, Gitleaks, and Local PII Guards.

- protects: Embedded connection-URI passwords, COMPEL remote tokens, Supabase PATs,
  hardcoded Windows user paths/URIs, and local PII denylist enforcement in
  scripts/lib/Test-KongorSecretScrub.ps1 + .gitleaks.toml.
- fails_when: A real database password in a postgresql:// URI, a compel_* token,
  a hardcoded file:///C:/Users/<handle>/ link, or a custom local PII denylist term
  slips past Test-KongorSecretScrub / Test-KongorLocalPiiGuard / gitleaks, OR when
  legitimate synthetic test placeholders trigger false positives.
- seam: Real PowerShell execution of scripts/lib/Test-KongorSecretScrub.ps1 and
  real gitleaks CLI execution against .gitleaks.toml in an isolated temp directory.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRUB_LIB = REPO_ROOT / "scripts" / "lib" / "Test-KongorSecretScrub.ps1"
GITLEAKS_CONFIG = REPO_ROOT / ".gitleaks.toml"


def _find_gitleaks() -> str | None:
    found = shutil.which("gitleaks")
    if found:
        return found
    local_app_data = os.environ.get("LOCALAPPDATA", "")
    if local_app_data:
        pkg_root = Path(local_app_data) / "Microsoft" / "WinGet" / "Packages"
        if pkg_root.exists():
            fast_matches = list(pkg_root.glob("*[Gg]itleaks*/gitleaks.exe")) + list(pkg_root.glob("*[Gg]itleaks*/*/gitleaks.exe"))
            if fast_matches:
                return str(fast_matches[0])
            matches = list(pkg_root.rglob("gitleaks.exe"))
            if matches:
                return str(matches[0])
    return None


def _run_ps_json(ps_body: str) -> dict:
    script = f"""
$ErrorActionPreference = 'Stop'
. '{SCRUB_LIB}'
{ps_body}
"""
    proc = subprocess.run(
        ["powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
        capture_output=True,
        text=True,
        timeout=45,
        check=True,
    )
    return json.loads(proc.stdout.strip())


def test_secret_scrub_blocks_planted_uri_passwords_and_compel_tokens() -> None:
    planted_uri = "postgresql://" + "postgres.realprojref99:Sup3rS3cr3tDbP4ss!@" + "aws-0-us-east-1.pooler.supabase.com:6543/postgres"
    planted_compel = "X-Compel-Token: compel_" + "9f8e7d6c5b4a3f2e1d0c9b8a7f6e5d4c"
    planted_sbp = "sbp_" + ("a1b2c3d4" * 5)

    payload = f"{planted_uri}\n{planted_compel}\n{planted_sbp}\n"
    res = _run_ps_json(
        f"""
$r = Test-KongorSecretScrub -Content @'
{payload}
'@
[PSCustomObject]@{{
    Passed = $r.Passed
    Rules  = @($r.Findings | ForEach-Object {{ $_.RuleName }})
    Raw    = @($r.Findings | ForEach-Object {{ $_.MaskedSnippet }})
}} | ConvertTo-Json -Compress
"""
    )
    assert res["Passed"] is False
    rules = set(res["Rules"])
    assert "uri_connection_password" in rules
    assert "compel_control_token" in rules
    assert "supabase_access_token" in rules
    for masked in res["Raw"]:
        assert "Sup3rS3cr3tDbP4ss!" not in masked


def test_secret_scrub_allows_documented_synthetic_placeholders() -> None:
    safe_payload = "\n".join(
        [
            "postgresql://postgres:[YOUR-PASSWORD]@db.xyzcompany.supabase.co:5432/postgres",
            "postgresql://postgres.xyzref:MyRawSecret!23@aws-0-us-east-1.pooler.supabase.com:6543/postgres",
            "postgresql://postgres:postgres@localhost:5432/merrick",
            "compel_test_token_0123456789abcdef0123456789",
        ]
    )
    res = _run_ps_json(
        f"""
$r = Test-KongorSecretScrub -Content @'
{safe_payload}
'@
[PSCustomObject]@{{
    Passed = $r.Passed
    Rules  = @($r.Findings | ForEach-Object {{ $_.RuleName }})
}} | ConvertTo-Json -Compress
"""
    )
    assert res["Passed"] is True, f"Unexpected findings on synthetic placeholders: {res['Rules']}"


def test_local_pii_guard_blocks_denylist_terms_and_windows_user_links(tmp_path: Path) -> None:
    cfg = tmp_path / "local_pii_guard.local.json"
    cfg.write_text(
        json.dumps(
            {
                "schema_version": 1,
                "terms": [
                    {"value": "SyntheticOwnerHandle99", "category": "own_identifier", "severity": "high"},
                    {"value": "ROG-CANARY-HOST-2026", "category": "own_identifier", "severity": "high"},
                ],
                "allowed_public_identity": ["guccidonut"],
            }
        ),
        encoding="utf-8",
    )

    dirty_doc = tmp_path / "dirty_doc.md"
    dirty_doc.write_text(
        "See [foo](file:///C:/" + "Users/SomePrivateUser/PycharmProjects/ProjectKongor/README.md)\n"
        "Host: ROG-CANARY-HOST-2026\n"
        "Account: SyntheticOwnerHandle99\n",
        encoding="utf-8",
    )

    clean_doc = tmp_path / "clean_doc.md"
    clean_doc.write_text(
        "See [README](README.md)\n"
        "Allowed public handle: guccidonut\n"
        "Synthetic path: C:\\Users\\developer\\Desktop\\hon_dump.dmp\n",
        encoding="utf-8",
    )

    res_dirty = _run_ps_json(
        f"""
$r = Test-KongorLocalPiiGuard -ConfigPath '{cfg}' -CandidateFiles @('{dirty_doc}') -RequireDenylist
[PSCustomObject]@{{
    Passed   = $r.Passed
    Rules    = @($r.Findings | ForEach-Object {{ $_.rule_name }})
    Snippets = @($r.Findings | ForEach-Object {{ $_.masked_snippet }})
}} | ConvertTo-Json -Compress
"""
    )
    assert res_dirty["Passed"] is False
    assert "local_pii_own_identifier" in res_dirty["Rules"]
    assert "hardcoded_windows_user_path" in res_dirty["Rules"]
    for s in res_dirty["Snippets"]:
        assert "SyntheticOwnerHandle99" not in s
        assert "SomePrivateUser" not in s

    res_clean = _run_ps_json(
        f"""
$r = Test-KongorLocalPiiGuard -ConfigPath '{cfg}' -CandidateFiles @('{clean_doc}') -RequireDenylist
[PSCustomObject]@{{ Passed = $r.Passed; Count = $r.Findings.Count }} | ConvertTo-Json -Compress
"""
    )
    assert res_clean["Passed"] is True
    assert res_clean["Count"] == 0


def test_gitleaks_config_catches_planted_secrets_and_allows_placeholders(tmp_path: Path) -> None:
    gitleaks = _find_gitleaks()
    assert gitleaks is not None, "gitleaks binary must be installed to certify .gitleaks.toml"

    probe_file = tmp_path / "probe.txt"
    probe_file.write_text(
        "postgresql://" + "postgres.realproj999:V3ryR3alDbS3cr3t99!@" + "aws-0-us-east-1.pooler.supabase.com:6543/postgres\n"
        "compel_" + "9f8e7d6c5b4a3f2e1d0c9b8a7f6e5d4c\n",
        encoding="utf-8",
    )
    bad = subprocess.run(
        [gitleaks, "dir", str(tmp_path), "--config", str(GITLEAKS_CONFIG), "--redact"],
        capture_output=True,
        text=True,
        timeout=15,
    )
    assert bad.returncode != 0, "gitleaks must fail on planted URI password and compel_ token"

    probe_file.write_text(
        "postgresql://postgres:[YOUR-PASSWORD]@db.xyzcompany.supabase.co:5432/postgres\n"
        "postgresql://postgres.xyzref:UnitTestSample!23@aws-0-us-east-1.pooler.supabase.com:6543/postgres\n"
        "compel_test_placeholder_0123456789abcdef\n",
        encoding="utf-8",
    )
    good = subprocess.run(
        [gitleaks, "dir", str(tmp_path), "--config", str(GITLEAKS_CONFIG), "--redact"],
        capture_output=True,
        text=True,
        timeout=15,
    )
    assert good.returncode == 0, f"gitleaks unexpectedly flagged safe placeholders:\n{good.stdout}\n{good.stderr}"


def test_secret_scrub_and_pii_guard_hardened_patterns(tmp_path: Path) -> None:
    planted_cfat = "cfat_" + "AbCdEfGhIjKlMnOpQrStUvWxYz012345"
    planted_sb_secret = "sb_secret_" + "1a2b3c4d5e6f7g8h9i0j"
    planted_ado = "https://dev.azure." + "com/privateorg/PKV/_git/PKV"
    planted_assign = (
        'JWT_SECRET || "real-fallback-' + 'secret-998877"\n'
        'WEBHOOK_URL: "https://hooks.slack.com/' + 'services/T11/B22/XYZ"\n'
    )

    scrub_bad = _run_ps_json(
        f"""
$r = Test-KongorSecretScrub -Content @'
{planted_cfat}
{planted_sb_secret}
{planted_ado}
{planted_assign}
'@
[PSCustomObject]@{{
    Passed = $r.Passed
    Rules  = @($r.Findings | ForEach-Object {{ $_.RuleName }})
}} | ConvertTo-Json -Compress
"""
    )
    assert scrub_bad["Passed"] is False
    bad_rules = set(scrub_bad["Rules"])
    assert "cloudflare_api_token" in bad_rules
    assert "supabase_secret_key" in bad_rules
    assert "azure_devops_url" in bad_rules
    assert "generic_secret_assignment" in bad_rules

    scrub_ok = _run_ps_json(
        """
$r = Test-KongorSecretScrub -Content @'
https://dev.azure.com/exampleorg/PKV/_git/PKV
const secret = process.env.JWT_SECRET || "${JWT_SECRET}"
'@
[PSCustomObject]@{ Passed = $r.Passed; Rules = @($r.Findings | ForEach-Object { $_.RuleName }) } | ConvertTo-Json -Compress
"""
    )
    assert scrub_ok["Passed"] is True, f"Unexpected findings: {scrub_ok['Rules']}"

    pii_file = tmp_path / "posix_pii.md"
    pii_file.write_text(
        "Linux: /home/" + "real_human_user/src/app.py\n"
        "macOS: /Users/" + "real_human_user/Documents/notes.txt\n"
        "IDE: ~/Pycharm" + "Projects/ProjectKongor/README.md\n",
        encoding="utf-8",
    )
    pii_bad = _run_ps_json(
        f"""
$r = Test-KongorLocalPiiGuard -CandidateFiles @('{pii_file}')
[PSCustomObject]@{{
    Passed = $r.Passed
    Rules  = @($r.Findings | ForEach-Object {{ $_.rule_name }})
}} | ConvertTo-Json -Compress
"""
    )
    assert pii_bad["Passed"] is False
    assert "hardcoded_posix_user_path" in pii_bad["Rules"]
    assert "hardcoded_pycharm_path" in pii_bad["Rules"]

    safe_file = tmp_path / "posix_safe.md"
    safe_file.write_text(
        "Container: /home/voicebox/.cache/huggingface\n"
        "Route: /Home/Stats/123 and /users/accounts/login\n",
        encoding="utf-8",
    )
    pii_ok = _run_ps_json(
        f"""
$r = Test-KongorLocalPiiGuard -CandidateFiles @('{safe_file}')
[PSCustomObject]@{{ Passed = $r.Passed; Count = $r.Findings.Count }} | ConvertTo-Json -Compress
"""
    )
    assert pii_ok["Passed"] is True
    assert pii_ok["Count"] == 0

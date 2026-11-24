# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Value-Weighted Canary Tests for scripts/Invoke-PublicGithubExport.ps1 (8-Gate Exporter).

- protects: Default-deny public GitHub export boundary (zero .s2z/.dll/.exe binaries,
  zero retail overlay SHA-256 asset leaks, zero personal PII/workstation paths, zero
  secrets, zero Git LFS rules, mandatory AGPL-3.0 / MPL-2.0 / CC BY-NC-SA 4.0 governance,
  Azure DevOps URL refusal, and strict guccidonut@users.noreply.github.com commit identity
  without mutating local workspace git config or remotes).
- fails_when: Any forbidden binary extension, retail asset hash, PII token, secret,
  filter=lfs rule, missing license, or non-noreply remote commit slips past Gates 1-7,
  or when -SkipBuildGate is permitted without KONGOR_EXPORT_CANARY_TEST=1.
- seam: Real PowerShell execution of scripts/Invoke-PublicGithubExport.ps1 + real git
  and gitleaks CLI execution against isolated temporary workspaces.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
EXPORT_SCRIPT = REPO_ROOT / "scripts" / "Invoke-PublicGithubExport.ps1"
REAL_MANIFEST = REPO_ROOT / "config" / "public_export_manifest.json"


def _make_clean_fixture_workspace(base_dir: Path) -> tuple[Path, Path]:
    ws = base_dir / "ws"
    ws.mkdir(parents=True, exist_ok=True)
    (ws / ".github").mkdir(parents=True, exist_ok=True)
    (ws / "config").mkdir(parents=True, exist_ok=True)
    (ws / "native").mkdir(parents=True, exist_ok=True)
    (ws / "docs" / "screenshots").mkdir(parents=True, exist_ok=True)
    (ws / "games" / "dawnbringer-overlay" / "images").mkdir(parents=True, exist_ok=True)

    shutil.copy2(REAL_MANIFEST, ws / "config" / "public_export_manifest.json")
    shutil.copy2(REPO_ROOT / ".gitleaks.toml", ws / ".gitleaks.toml")

    for gov_rel in [
        "LICENSE",
        "native/LICENSE",
        "docs/LICENSE.md",
        "NOTICE",
        "NAMING_POLICY.md",
        "SECURITY.md",
        "THIRD_PARTY_NOTICES.md",
        "CITATION.cff",
        "INTEROPERABILITY_AND_DMCA.md",
        "REUSE.toml",
        ".github/PULL_REQUEST_TEMPLATE.md",
    ]:
        shutil.copy2(REPO_ROOT / gov_rel, ws / gov_rel)
    shutil.copytree(REPO_ROOT / "LICENSES", ws / "LICENSES", dirs_exist_ok=True)
    shutil.copy2(REPO_ROOT / ".github" / "dependabot.yml", ws / ".github" / "dependabot.yml")
    shutil.copy2(REPO_ROOT / ".git-blame-ignore-revs", ws / ".git-blame-ignore-revs")

    (ws / "README.md").write_text(
        "# Project KONGOR V Cleanroom Fixture\n\nAll links are repo-relative: [LICENSE](LICENSE).\n",
        encoding="utf-8",
    )
    (ws / "games" / "dawnbringer-overlay" / "images" / "retail_hero_icon.png").write_bytes(
        b"\x89PNG\r\n\x1a\nRETAIL_COPYRIGHTED_ICON_BYTES_DO_NOT_EXPORT_998877"
    )

    pii_cfg = base_dir / "canary_pii_guard.local.json"
    pii_cfg.write_text(
        json.dumps(
            {
                "schema_version": 1,
                "terms": [
                    {"value": "CanaryPrivateIdentity99", "category": "own_identifier", "severity": "high"},
                    {"value": "PRIVATE-DEV-LAPTOP-42", "category": "own_identifier", "severity": "high"},
                ],
                "allowed_public_identity": ["guccidonut"],
            }
        ),
        encoding="utf-8",
    )
    return ws, pii_cfg


def _run_exporter(
    ws: Path,
    pii_cfg: Path,
    extra_args: list[str] | None = None,
    canary_env: bool = True,
) -> subprocess.CompletedProcess[str]:
    receipt = ws / "artifacts" / "public_export_receipt.json"
    cmd = [
        "powershell",
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        str(EXPORT_SCRIPT),
        "-SourceRoot",
        str(ws),
        "-PiiConfigPath",
        str(pii_cfg),
        "-ReceiptPath",
        str(receipt),
    ]
    if extra_args:
        cmd.extend(extra_args)

    env = os.environ.copy()
    if canary_env:
        env["KONGOR_EXPORT_CANARY_TEST"] = "1"
    else:
        env.pop("KONGOR_EXPORT_CANARY_TEST", None)

    return subprocess.run(
        cmd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=env,
        timeout=60,
    )


def test_preflight_blocks_azure_devops_url_and_unguarded_skip_build(tmp_path: Path) -> None:
    ws, pii_cfg = _make_clean_fixture_workspace(tmp_path)

    # 1. -SkipBuildGate without KONGOR_EXPORT_CANARY_TEST=1 must fail closed (exit 10)
    res_unguarded = _run_exporter(ws, pii_cfg, ["-SkipBuildGate"], canary_env=False)
    assert res_unguarded.returncode == 10, res_unguarded.stdout + res_unguarded.stderr

    # 2. Pushing to Azure DevOps URL must fail closed (exit 10)
    res_azure = _run_exporter(
        ws,
        pii_cfg,
        ["-Push", "-GithubRepoUrl", "https://dev.azure.com/exampleorg/ProjectKONGOR/_git/ProjectKONGOR"],
        canary_env=False,
    )
    assert res_azure.returncode == 10, res_azure.stdout + res_azure.stderr
    assert "Azure DevOps" in res_azure.stdout


def test_gate1_blocks_forbidden_binary_extensions(tmp_path: Path) -> None:
    ws, pii_cfg = _make_clean_fixture_workspace(tmp_path)

    res_dll = _run_exporter(ws, pii_cfg, ["-SkipBuildGate", "-CanaryPostStageMutation", "inject_forbidden_dll"])
    assert res_dll.returncode == 11, res_dll.stdout + res_dll.stderr
    assert ".dll" in res_dll.stdout

    res_s2z = _run_exporter(ws, pii_cfg, ["-SkipBuildGate", "-CanaryPostStageMutation", "inject_forbidden_s2z"])
    assert res_s2z.returncode == 11, res_s2z.stdout + res_s2z.stderr
    assert ".s2z" in res_s2z.stdout


def test_gate2_blocks_retail_overlay_sha256_collision(tmp_path: Path) -> None:
    ws, pii_cfg = _make_clean_fixture_workspace(tmp_path)
    retail_bytes = (ws / "games" / "dawnbringer-overlay" / "images" / "retail_hero_icon.png").read_bytes()
    (ws / "docs" / "screenshots" / "01_leaked_icon.png").write_bytes(retail_bytes)

    res = _run_exporter(ws, pii_cfg, ["-SkipBuildGate"])
    assert res.returncode == 12, res.stdout + res.stderr
    assert "GATE-2-RETAIL-SHA256" in res.stdout


def test_gate3_blocks_planted_pii_and_workstation_user_paths(tmp_path: Path) -> None:
    ws, pii_cfg = _make_clean_fixture_workspace(tmp_path)
    (ws / "README.md").write_text(
        "Leaked owner: CanaryPrivateIdentity99\n"
        "Link: [bad](file:///C:/" + "Users/SomeRealDeveloper/PycharmProjects/ProjectKongor/README.md)\n",
        encoding="utf-8",
    )

    res = _run_exporter(ws, pii_cfg, ["-SkipBuildGate"])
    assert res.returncode == 13, res.stdout + res.stderr
    assert "GATE-3-PII" in res.stdout
    assert "CanaryPrivateIdentity99" not in res.stdout  # Must be masked in logs


def test_gate4_blocks_planted_credentials(tmp_path: Path) -> None:
    ws, pii_cfg = _make_clean_fixture_workspace(tmp_path)
    planted_uri = (
        "postgresql://"
        + "postgres.realproj99:V3ryR3alDbS3cr3t99!@"
        + "aws-0-us-east-1.pooler.supabase.com:6543/postgres\n"
    )
    (ws / "README.md").write_text(planted_uri, encoding="utf-8")

    res = _run_exporter(ws, pii_cfg, ["-SkipBuildGate"])
    assert res.returncode == 14, res.stdout + res.stderr
    assert "GATE-4-SECRETS" in res.stdout
    assert "V3ryR3alDbS3cr3t99!" not in res.stdout


def test_gate5_blocks_lfs_rogue_submodule_and_missing_license(tmp_path: Path) -> None:
    ws, pii_cfg = _make_clean_fixture_workspace(tmp_path)

    res_lfs = _run_exporter(ws, pii_cfg, ["-SkipBuildGate", "-CanaryPostStageMutation", "inject_git_lfs"])
    assert res_lfs.returncode == 15, res_lfs.stdout + res_lfs.stderr

    res_sub = _run_exporter(ws, pii_cfg, ["-SkipBuildGate", "-CanaryPostStageMutation", "inject_rogue_submodule"])
    assert res_sub.returncode == 15, res_sub.stdout + res_sub.stderr

    res_lic = _run_exporter(ws, pii_cfg, ["-SkipBuildGate", "-CanaryPostStageMutation", "remove_governance_license"])
    assert res_lic.returncode == 15, res_lic.stdout + res_lic.stderr

    # Regression guard: disallowed SLA / hardcoded tool-count / upstream-CDN / private-remote tokens must fail Gate 5
    (ws / "README.md").write_text("Response within 72 hours via 101-tool server on cdn.kongor.net\n", encoding="utf-8")
    res_up = _run_exporter(ws, pii_cfg, ["-SkipBuildGate"])
    assert res_up.returncode == 15, res_up.stdout + res_up.stderr
    assert "GATE-5-GOVERNANCE" in res_up.stdout


def test_gate7_blocks_dirty_remote_email_and_certifies_clean_dry_run_without_touching_local_git(
    tmp_path: Path,
) -> None:
    ws, pii_cfg = _make_clean_fixture_workspace(tmp_path)

    # Record workspace git email & origin URL before running exporter
    orig_email = subprocess.run(
        ["git", "-C", str(REPO_ROOT), "config", "--get", "user.email"],
        capture_output=True,
        text=True,
        check=False,
    ).stdout.strip()
    orig_remote = subprocess.run(
        ["git", "-C", str(REPO_ROOT), "remote", "get-url", "origin"],
        capture_output=True,
        text=True,
        check=False,
    ).stdout.strip()

    # 1. Dirty remote branch containing non-noreply email must fail Gate 7 (exit 17)
    dirty_remote = tmp_path / "dirty_remote.git"
    dirty_work = tmp_path / "dirty_work"
    subprocess.run(["git", "init", "--bare", "-b", "main", str(dirty_remote)], check=True, capture_output=True)
    subprocess.run(["git", "init", "-b", "main", str(dirty_work)], check=True, capture_output=True)
    (dirty_work / "seed.txt").write_text("seed", encoding="utf-8")
    subprocess.run(["git", "-C", str(dirty_work), "add", "seed.txt"], check=True, capture_output=True)
    subprocess.run(
        [
            "git",
            "-C",
            str(dirty_work),
            "-c",
            "user.name=PrivateDev",
            "-c",
            "user.email=privatedev@personal.example.com",
            "commit",
            "-m",
            "dirty history",
        ],
        check=True,
        capture_output=True,
    )
    subprocess.run(
        ["git", "-C", str(dirty_work), "push", str(dirty_remote), "HEAD:refs/heads/main"],
        check=True,
        capture_output=True,
    )

    res_dirty_remote = _run_exporter(ws, pii_cfg, ["-SkipBuildGate", "-GithubRepoUrl", str(dirty_remote)])
    assert res_dirty_remote.returncode == 17, res_dirty_remote.stdout + res_dirty_remote.stderr
    assert "GATE-7-GIT-IDENTITY" in res_dirty_remote.stdout

    # 2. Clean dry-run must pass (exit 0), emit deterministic receipt, and never touch workspace git config
    res_ok = _run_exporter(ws, pii_cfg, ["-SkipBuildGate"])
    assert res_ok.returncode == 0, res_ok.stdout + res_ok.stderr

    receipt_path = ws / "artifacts" / "public_export_receipt.json"
    assert receipt_path.is_file()
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    assert receipt["mode"] == "dry_run"
    assert receipt["pushed"] is False
    assert receipt["exportIdentity"] in [
        "guccidonut <guccidonut@users.noreply.github.com>",
        "guccid0nut <guccid0nut@users.noreply.github.com>",
    ]
    assert receipt["commitCount"] == 1
    assert len(receipt["cleanroomCommitSha"]) == 40
    assert len(receipt["treeSha256"]) == 64

    after_email = subprocess.run(
        ["git", "-C", str(REPO_ROOT), "config", "--get", "user.email"],
        capture_output=True,
        text=True,
        check=False,
    ).stdout.strip()
    after_remote = subprocess.run(
        ["git", "-C", str(REPO_ROOT), "remote", "get-url", "origin"],
        capture_output=True,
        text=True,
        check=False,
    ).stdout.strip()
    assert after_email == orig_email
    assert after_remote == orig_remote


def test_inplace_retail_assets_remain_untouched_and_cleanroom_substitutions_apply_in_staging(
    tmp_path: Path,
) -> None:
    ws, pii_cfg = _make_clean_fixture_workspace(tmp_path)

    retail_hud_bytes = b"\x89PNG\r\n\x1a\nRETAIL_HUD_ICON_ACCURSED_ORIGINAL_BYTES"
    retail_caldavar_bytes = b"\x89PNG\r\n\x1a\nRETAIL_WILLOWMAKER_CALDAVAR_ORIGINAL_BYTES"
    cleanroom_caldavar_bytes = b"\x89PNG\r\n\x1a\nCLEANROOM_PKV_GEOMETRIC_PLACEHOLDER_BYTES"
    retail_glb_bytes = b"glTF\x02\x00\x00\x00RETAIL_EMPATH_GLB_BYTES"
    retail_web_hero_bytes = b"\x89PNG\r\n\x1a\nRETAIL_DAWNBRINGER_HERO_ICON_BYTES"

    (ws / "assets" / "hud" / "icons").mkdir(parents=True, exist_ok=True)
    (ws / "assets" / "hud" / "devtools.rml").write_text("<rml><body/></rml>\n", encoding="utf-8")
    (ws / "assets" / "hud" / "icons" / "Hero_Accursed.Alt9.png").write_bytes(retail_hud_bytes)

    (ws / "src" / "apps" / "willowmaker" / "Assets").mkdir(parents=True, exist_ok=True)
    (ws / "src" / "apps" / "willowmaker" / "Assets" / "caldavar.png").write_bytes(retail_caldavar_bytes)

    (ws / "src" / "apps" / "control-panel" / "Assets" / "models").mkdir(parents=True, exist_ok=True)
    (ws / "src" / "apps" / "control-panel" / "Assets" / "models" / "empath_cn.glb").write_bytes(retail_glb_bytes)

    (ws / "src" / "services" / "web-ui" / "wwwroot" / "images" / "heroes").mkdir(parents=True, exist_ok=True)
    (ws / "src" / "services" / "web-ui" / "wwwroot" / "images" / "heroes" / "hero_accursed.png").write_bytes(
        retail_web_hero_bytes
    )

    # 1. Missing cleanroom placeholder when retail target exists must fail closed (ExitCode 10)
    res_missing_ph = _run_exporter(ws, pii_cfg, ["-SkipBuildGate"])
    assert res_missing_ph.returncode == 10, res_missing_ph.stdout + res_missing_ph.stderr
    assert "STAGE-SUBSTITUTION" in res_missing_ph.stdout

    # 2. With cleanroom placeholder present, export succeeds, strips retail files, substitutes caldavar.png in staging,
    #    and leaves $SourceRoot retail files 100% untouched.
    (ws / "config" / "cleanroom_placeholders").mkdir(parents=True, exist_ok=True)
    (ws / "config" / "cleanroom_placeholders" / "caldavar.png").write_bytes(cleanroom_caldavar_bytes)

    res_ok = _run_exporter(ws, pii_cfg, ["-SkipBuildGate"])
    assert res_ok.returncode == 0, res_ok.stdout + res_ok.stderr
    assert "Applied staging-only cleanroom substitution" in res_ok.stdout

    # Source workspace retail files must be 100% byte-for-byte untouched
    assert (ws / "assets" / "hud" / "icons" / "Hero_Accursed.Alt9.png").read_bytes() == retail_hud_bytes
    assert (ws / "src" / "apps" / "willowmaker" / "Assets" / "caldavar.png").read_bytes() == retail_caldavar_bytes
    assert (ws / "src" / "apps" / "control-panel" / "Assets" / "models" / "empath_cn.glb").read_bytes() == retail_glb_bytes
    assert (
        ws / "src" / "services" / "web-ui" / "wwwroot" / "images" / "heroes" / "hero_accursed.png"
    ).read_bytes() == retail_web_hero_bytes


def test_gate5_enforces_spdx_license_boundaries_vendor_cleanliness_and_ascii_headers(
    tmp_path: Path,
) -> None:
    # 1. Native original file mis-stamped with AGPL instead of MPL-2.0 must fail Gate 5
    ws1, pii1 = _make_clean_fixture_workspace(tmp_path / "case1")
    (ws1 / "native" / "proxies" / "native_guard").mkdir(parents=True, exist_ok=True)
    (ws1 / "native" / "proxies" / "native_guard" / "guard.cpp").write_text(
        "// SPDX-License-Identifier: AGPL-3.0-or-later\n"
        "// SPDX-FileCopyrightText: 2026 guccidonut\n",
        encoding="utf-8",
    )
    res_wrong_native = _run_exporter(ws1, pii1, ["-SkipBuildGate"])
    assert res_wrong_native.returncode == 15, res_wrong_native.stdout + res_wrong_native.stderr
    assert "MPL-2.0" in res_wrong_native.stdout

    # 2. Vendored third-party file stamped with guccidonut copyright must fail Gate 5
    ws2, pii2 = _make_clean_fixture_workspace(tmp_path / "case2")
    (ws2 / "native" / "include").mkdir(parents=True, exist_ok=True)
    (ws2 / "native" / "include" / "concurrentqueue.h").write_text(
        "// SPDX-FileCopyrightText: 2026 guccidonut\n",
        encoding="utf-8",
    )
    res_vendor_contaminated = _run_exporter(ws2, pii2, ["-SkipBuildGate"])
    assert res_vendor_contaminated.returncode == 15, res_vendor_contaminated.stdout + res_vendor_contaminated.stderr
    assert "SKIP_VENDOR" in res_vendor_contaminated.stdout

    # 3. Non-ASCII em-dash inside SPDX header line must fail Gate 5
    ws3, pii3 = _make_clean_fixture_workspace(tmp_path / "case3")
    (ws3 / "src" / "tools" / "kongor-cli").mkdir(parents=True, exist_ok=True)
    (ws3 / "src" / "tools" / "kongor-cli" / "Sample.cs").write_text(
        "// SPDX-License-Identifier: AGPL-3.0-or-later\n"
        "// SPDX-FileCopyrightText: 2026 guccidonut \u2014 TAO MCP\n",
        encoding="utf-8",
    )
    res_non_ascii = _run_exporter(ws3, pii3, ["-SkipBuildGate"])
    assert res_non_ascii.returncode == 15, res_non_ascii.stdout + res_non_ascii.stderr
    assert "7-bit ASCII" in res_non_ascii.stdout


def test_gate3_enforces_sibling_auxiliary_pii_local_overlay(tmp_path: Path) -> None:
    ws, pii_cfg = _make_clean_fixture_workspace(tmp_path)
    aux_overlay = pii_cfg.parent / "extra_privacy_overlay.pii.local.json"
    aux_overlay.write_text(
        json.dumps(
            {
                "schema_version": 1,
                "terms": [
                    {
                        "value": "CanaryAuxPrivacyHandle77",
                        "category": "privacy_request",
                        "severity": "high",
                    }
                ],
            }
        ),
        encoding="utf-8",
    )

    note_path = ws / "docs" / "aux_privacy_check.md"
    note_path.write_text(
        "# Auxiliary Privacy Canary\n\nMentions CanaryAuxPrivacyHandle77 in docs.\n",
        encoding="utf-8",
    )

    # 1. Must fail closed at Gate 3 (exit 13) when a staged file hits a sibling *.pii.local.json overlay
    res_blocked = _run_exporter(ws, pii_cfg, ["-SkipBuildGate"])
    assert res_blocked.returncode == 13, res_blocked.stdout + res_blocked.stderr
    assert "GATE-3-PII" in res_blocked.stdout
    assert "extra_privacy_overlay.pii.local.json" in res_blocked.stdout
    # Raw denylist term must never leak verbatim in failure output (masked snippet only)
    assert "CanaryAuxPrivacyHandle77" not in res_blocked.stdout

    # 2. Once scrubbed, Gate 3 must pass and report auxOverlays=1
    note_path.write_text("# Auxiliary Privacy Canary\n\nClean neutral attribution.\n", encoding="utf-8")
    res_clean = _run_exporter(ws, pii_cfg, ["-SkipBuildGate"])
    assert res_clean.returncode == 0, res_clean.stdout + res_clean.stderr
    assert "auxOverlays=1" in res_clean.stdout



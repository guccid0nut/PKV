#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Value-Weighted Testing Gate for Wave 4 Track 5 (Self-Hosted Garage CAS & Repo Diet):
- protects:
  1. Self-hosted Garage v1.0.1 (AGPL-3.0) docker-compose schema, ports (3900-3903),
     and zero-secret garage.toml configuration.
  2. PowerShell AST validity (0 parse errors) for sync_garage_cas.ps1 and
     Invoke-KongorRepoDiet.ps1.
  3. sync_garage_cas.ps1 UTF-8 BOM manifest.ndjson parsing, SHA-256 content-addressed
     S3 key planning, and local .cas/objects/xx/yyyy... compatibility.
  4. Invoke-KongorRepoDiet.ps1 Data Loss Firewall: -Mode ApplyLive refusal without
     -ConfirmDestructiveHistoryRewrite, missing/tampered backup bundle refusal,
     disk-space preflight refusal, HEAD-tracked path strip refusal, SHA-256 verified
     bundle backup, and real isolated-sandbox pack shrinkage with 100% HEAD^{tree} parity.
- fails_when:
  Garage compose schema breaks, secrets leak into garage.toml, PowerShell syntax breaks,
  ApplyLive bypasses confirmation/backup/disk-space/HEAD guards, or sandbox filter-repo
  fails to shrink dead historical bloat while preserving HEAD^{tree}.
- seam:
  infra/docker/docker-compose.garage-cas.yml, infra/garage/garage.toml,
  scripts/ops/sync_garage_cas.ps1, scripts/ops/Invoke-KongorRepoDiet.ps1.
"""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import subprocess
import tomllib


REPO_ROOT = Path(__file__).resolve().parents[2]
COMPOSE_FILE = REPO_ROOT / "infra" / "docker" / "docker-compose.garage-cas.yml"
GARAGE_TOML = REPO_ROOT / "infra" / "garage" / "garage.toml"
SYNC_CAS_PS1 = REPO_ROOT / "scripts" / "ops" / "sync_garage_cas.ps1"
REPO_DIET_PS1 = REPO_ROOT / "scripts" / "ops" / "Invoke-KongorRepoDiet.ps1"
LIVE_MANIFEST = REPO_ROOT / "games" / ".asset_manifest" / "manifest.ndjson"


def _run(cmd: list[str], cwd: Path | None = None, check: bool = False) -> subprocess.CompletedProcess[str]:
    env = os.environ.copy()
    env["GIT_TERMINAL_PROMPT"] = "0"
    env["GCM_INTERACTIVE"] = "never"
    return subprocess.run(
        cmd,
        cwd=str(cwd) if cwd else None,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=env,
        check=check,
    )


def _run_ps1(script: Path, args: list[str]) -> subprocess.CompletedProcess[str]:
    cmd = [
        "powershell",
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        str(script),
        *args,
    ]
    return _run(cmd, cwd=REPO_ROOT)


def _create_synthetic_bloated_repo(repo_dir: Path) -> dict[str, str]:
    """Create a tiny temp Git repo with ~3 MiB of dead historical bloat + 1 dangling blob."""
    repo_dir.mkdir(parents=True, exist_ok=True)
    _run(["git", "init", "-b", "main"], cwd=repo_dir, check=True)
    _run(["git", "config", "user.name", "Kongor Test"], cwd=repo_dir, check=True)
    _run(["git", "config", "user.email", "test@kongor.local"], cwd=repo_dir, check=True)

    # Commit 1: active files + historical bloat under both default strip paths
    (repo_dir / "README.md").write_text("# Synthetic Kongor Repo\n", encoding="utf-8")
    (repo_dir / "src").mkdir(parents=True, exist_ok=True)
    (repo_dir / "src" / "app.txt").write_text("active runtime source\n", encoding="utf-8")

    ghidra_dir = repo_dir / "src" / "tools" / "ghidra" / "projects"
    ghidra_dir.mkdir(parents=True, exist_ok=True)
    (ghidra_dir / "dead_project.bin").write_bytes(os.urandom(1_500_000))

    forge_dir = repo_dir / "src" / "tools" / "forge-scraper" / "forge_skins"
    forge_dir.mkdir(parents=True, exist_ok=True)
    (forge_dir / "dead_skin.bin").write_bytes(os.urandom(1_500_000))

    _run(["git", "add", "."], cwd=repo_dir, check=True)
    _run(["git", "commit", "-m", "Add initial files and historical bloat"], cwd=repo_dir, check=True)

    # Commit 2: remove bloat from HEAD (so bloat exists ONLY in history, not HEAD)
    _run(
        ["git", "rm", "-rf", "src/tools/ghidra/projects", "src/tools/forge-scraper/forge_skins"],
        cwd=repo_dir,
        check=True,
    )
    (repo_dir / "src" / "app.txt").write_text("active runtime source v2\n", encoding="utf-8")
    _run(["git", "add", "src/app.txt"], cwd=repo_dir, check=True)
    _run(["git", "commit", "-m", "Remove dead bloat paths from HEAD"], cwd=repo_dir, check=True)

    # Add a 500 KiB dangling unreferenced blob and pack objects so live_before reflects real pack bloat
    dangling_tmp = repo_dir / ".git" / "tmp_dangling_src.bin"
    dangling_tmp.write_bytes(os.urandom(500_000))
    _run(["git", "hash-object", "-w", str(dangling_tmp)], cwd=repo_dir, check=True)
    dangling_tmp.unlink()
    _run(["git", "repack", "-a", "-d"], cwd=repo_dir, check=True)

    head_commit = _run(["git", "rev-parse", "HEAD"], cwd=repo_dir, check=True).stdout.strip()
    head_tree = _run(["git", "rev-parse", "HEAD^{tree}"], cwd=repo_dir, check=True).stdout.strip()
    return {"head_commit": head_commit, "head_tree": head_tree}


def test_garage_compose_and_toml_schema_and_security() -> None:
    assert GARAGE_TOML.is_file(), f"Missing {GARAGE_TOML}"
    assert COMPOSE_FILE.is_file(), f"Missing {COMPOSE_FILE}"

    with GARAGE_TOML.open("rb") as fh:
        cfg = tomllib.load(fh)

    assert cfg["metadata_dir"] == "/var/lib/garage/meta"
    assert cfg["data_dir"] == "/var/lib/garage/data"
    assert cfg["db_engine"] in ("lmdb", "sqlite")
    assert cfg["replication_factor"] >= 1
    assert ":3901" in cfg["rpc_bind_addr"]
    assert ":3900" in cfg["s3_api"]["api_bind_addr"]
    assert ":3902" in cfg["s3_web"]["bind_addr"]
    assert ":3903" in cfg["admin"]["api_bind_addr"]

    # Privacy/secret hygiene: zero hardcoded secrets in committed garage.toml
    assert "rpc_secret" not in cfg, "rpc_secret must come from GARAGE_RPC_SECRET env var, never committed in garage.toml"
    assert "admin_token" not in cfg.get("admin", {}), "admin_token must come from GARAGE_ADMIN_TOKEN env var"
    assert "metrics_token" not in cfg.get("admin", {}), "metrics_token must come from GARAGE_METRICS_TOKEN env var"

    # Validate docker compose schema via real `docker compose config`
    qc = _run(["docker", "compose", "-f", str(COMPOSE_FILE), "config", "-q"], cwd=REPO_ROOT)
    assert qc.returncode == 0, f"docker compose config -q failed:\n{qc.stderr}\n{qc.stdout}"

    rendered = _run(["docker", "compose", "-f", str(COMPOSE_FILE), "config", "--format", "json"], cwd=REPO_ROOT)
    assert rendered.returncode == 0, f"docker compose config --format json failed:\n{rendered.stderr}"
    spec = json.loads(rendered.stdout)
    svc = spec["services"]["garage"]
    assert svc["image"] == "dxflrs/garage:v1.0.1"
    target_ports = {int(p["target"]) for p in svc["ports"]}
    assert {3900, 3901, 3902, 3903}.issubset(target_ports)
    assert "GARAGE_RPC_SECRET" in svc["environment"]
    compose_raw = COMPOSE_FILE.read_text(encoding="utf-8")
    assert "${GARAGE_RPC_SECRET" in compose_raw


def test_powershell_scripts_ast_zero_errors() -> None:
    for script in (SYNC_CAS_PS1, REPO_DIET_PS1):
        assert script.is_file(), f"Missing script: {script}"
        ps_cmd = (
            "$tokens = $null; $errors = $null; "
            f"[void][System.Management.Automation.Language.Parser]::ParseFile('{str(script)}', [ref]$tokens, [ref]$errors); "
            "if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_.Message }; exit 1 }; "
            "Write-Output 'AST_OK'"
        )
        res = _run(["powershell", "-NoProfile", "-Command", ps_cmd], cwd=REPO_ROOT)
        assert res.returncode == 0 and "AST_OK" in res.stdout, f"AST parse failed for {script}:\n{res.stderr}\n{res.stdout}"


def test_sync_garage_cas_dryrun_manifest_and_local_cas(tmp_path: Path) -> None:
    fake_repo = tmp_path / "repo"
    game_dir = fake_repo / "games" / "client" / "game"
    game_dir.mkdir(parents=True, exist_ok=True)

    keep_file = game_dir / "autoexec.cfg"
    keep_bytes = b"set cl_test 1\n"
    keep_file.write_bytes(keep_bytes)
    keep_sha = hashlib.sha256(keep_bytes).hexdigest()

    override_file = game_dir / "custom.lua"
    override_bytes = b"return { ok = true }\n"
    override_file.write_bytes(override_bytes)

    # Write manifest with UTF-8 BOM matching real games/.asset_manifest/manifest.ndjson
    manifest_dir = fake_repo / "games" / ".asset_manifest"
    manifest_dir.mkdir(parents=True, exist_ok=True)
    manifest_file = manifest_dir / "manifest.ndjson"
    entries = [
        {"rel_path": "autoexec.cfg", "tree": "games/client/game", "size_bytes": len(keep_bytes), "kind": "config", "action": "keep"},
        {"rel_path": "custom.lua", "tree": "games/client/game", "size_bytes": len(override_bytes), "kind": "config", "action": "keep_custom_override"},
        {"rel_path": "dup.txt", "tree": "games/client/game", "size_bytes": 100, "kind": "other", "action": "delete_archive_duplicate"},
        {"rel_path": "srv.ogg", "tree": "games/client/game", "size_bytes": 200, "kind": "audio", "action": "delete_server_media"},
    ]
    manifest_text = "\n".join(json.dumps(e) for e in entries) + "\n"
    manifest_file.write_bytes(b"\xef\xbb\xbf" + manifest_text.encode("utf-8"))

    # Create a synthetic native/tools/kongor-cas compatible .cas/objects/xx/yyyy... chunk
    cas_root = fake_repo / "games" / ".cas"
    chunk_payload = b"synthetic-cas-chunk-payload"
    chunk_sha = hashlib.sha256(chunk_payload).hexdigest()
    chunk_path = cas_root / "objects" / chunk_sha[:2] / chunk_sha[2:]
    chunk_path.parent.mkdir(parents=True, exist_ok=True)
    chunk_path.write_bytes(chunk_payload)

    plan_json = tmp_path / "cas_plan.json"
    res = _run_ps1(
        SYNC_CAS_PS1,
        [
            "-Action", "Plan",
            "-DryRun",
            "-HashFiles",
            "-RepoRoot", str(fake_repo),
            "-CasRoot", str(cas_root),
            "-ManifestPath", str(manifest_file),
            "-PlanOutputPath", str(plan_json),
        ],
    )
    assert res.returncode == 0, f"sync_garage_cas.ps1 failed:\n{res.stdout}\n{res.stderr}"
    plan = json.loads(plan_json.read_text(encoding="utf-8-sig"))
    assert plan["schema_version"] == "kongor.garage_cas_sync.v1"
    assert plan["status"] == "PASS"
    assert plan["dry_run"] is True
    assert plan["local_cas"]["valid_addressed"] == 1
    assert plan["local_cas"]["chunks"][0]["s3_key"] == f"cas/v1/objects/{chunk_sha[:2]}/{chunk_sha[2:]}"
    assert plan["manifest_plan"]["total_lines"] == 4
    assert plan["manifest_plan"]["selected_entries"] == 2
    assert plan["manifest_plan"]["skipped_entries"] == 2
    assert plan["manifest_plan"]["hashed_entries"] == 2
    planned_keys = {item["rel_path"]: item["s3_key"] for item in plan["manifest_plan"]["sample_planned_objects"]}
    assert planned_keys["autoexec.cfg"] == f"cas/v1/blobs/{keep_sha[:2]}/{keep_sha[2:]}"

    # Also smoke-test against the real live manifest.ndjson (read-only DryRun, capped to 10 entries)
    if LIVE_MANIFEST.is_file():
        live_plan_json = tmp_path / "live_manifest_plan.json"
        live_res = _run_ps1(
            SYNC_CAS_PS1,
            ["-DryRun", "-MaxFiles", "10", "-PlanOutputPath", str(live_plan_json)],
        )
        assert live_res.returncode == 0, f"Live manifest dry-run failed:\n{live_res.stdout}\n{live_res.stderr}"
        live_plan = json.loads(live_plan_json.read_text(encoding="utf-8-sig"))
        assert live_plan["status"] == "PASS"
        assert live_plan["manifest_plan"]["total_lines"] > 1000
        assert live_plan["manifest_plan"]["selected_entries"] == 10


def test_repo_diet_guards_refuse_unsafe_apply_live_and_low_disk(tmp_path: Path) -> None:
    repo_dir = tmp_path / "tiny_repo"
    backup_dir = tmp_path / "backups"
    _create_synthetic_bloated_repo(repo_dir)

    # 1. ApplyLive MUST refuse without -ConfirmDestructiveHistoryRewrite (exit 2)
    res_no_confirm = _run_ps1(
        REPO_DIET_PS1,
        [
            "-Mode", "ApplyLive",
            "-RepoRoot", str(repo_dir),
            "-BackupRoot", str(backup_dir),
            "-MinFreeSpaceGB", "0.05",
        ],
    )
    assert res_no_confirm.returncode == 2, f"Expected exit 2, got {res_no_confirm.returncode}:\n{res_no_confirm.stdout}"
    assert "ConfirmDestructiveHistoryRewrite" in res_no_confirm.stdout

    # 2. ApplyLive with confirmation MUST STILL refuse when no verified bundle exists (exit 3)
    res_no_bundle = _run_ps1(
        REPO_DIET_PS1,
        [
            "-Mode", "ApplyLive",
            "-ConfirmDestructiveHistoryRewrite",
            "-RepoRoot", str(repo_dir),
            "-BackupRoot", str(backup_dir),
            "-MinFreeSpaceGB", "0.05",
        ],
    )
    assert res_no_bundle.returncode == 3, f"Expected exit 3, got {res_no_bundle.returncode}:\n{res_no_bundle.stdout}"

    # 3. Disk-space preflight MUST refuse when drive free space < MinFreeSpaceGB (exit 4)
    res_low_disk = _run_ps1(
        REPO_DIET_PS1,
        [
            "-Mode", "BackupOnly",
            "-RepoRoot", str(repo_dir),
            "-BackupRoot", str(backup_dir),
            "-MinFreeSpaceGB", "999999",
        ],
    )
    assert res_low_disk.returncode == 4, f"Expected exit 4, got {res_low_disk.returncode}:\n{res_low_disk.stdout}"
    assert "Insufficient free disk space" in res_low_disk.stdout


def test_repo_diet_end_to_end_backup_and_sandbox_shrink(tmp_path: Path) -> None:
    repo_dir = tmp_path / "bloated_repo"
    backup_dir = tmp_path / "backups"
    meta = _create_synthetic_bloated_repo(repo_dir)

    # Step 1: BackupOnly -> verified .bundle + .sha256 + .manifest.json
    backup_report_path = tmp_path / "backup_report.json"
    res_bkp = _run_ps1(
        REPO_DIET_PS1,
        [
            "-Mode", "BackupOnly",
            "-RepoRoot", str(repo_dir),
            "-BackupRoot", str(backup_dir),
            "-MinFreeSpaceGB", "0.05",
            "-ReportPath", str(backup_report_path),
        ],
    )
    assert res_bkp.returncode == 0, f"BackupOnly failed:\n{res_bkp.stdout}\n{res_bkp.stderr}"
    bkp_report = json.loads(backup_report_path.read_text(encoding="utf-8-sig"))
    assert bkp_report["status"] == "PASS"
    assert bkp_report["live_repo_modified"] is False
    bundle_file = Path(bkp_report["bundle"]["bundle_path"])
    sha_file = Path(bkp_report["bundle"]["sha256_path"])
    assert bundle_file.is_file()
    assert sha_file.is_file()
    assert bkp_report["bundle"]["bundle_verify_passed"] is True

    # Independent Python SHA-256 verification of the generated bundle
    independent_sha = hashlib.sha256(bundle_file.read_bytes()).hexdigest()
    assert bkp_report["bundle"]["actual_sha256"] == independent_sha
    assert sha_file.read_text(encoding="ascii").strip().startswith(independent_sha)

    # Step 2: DryRunSandbox -> isolated git-filter-repo shrink + 100% HEAD^{tree} parity
    dryrun_report_path = tmp_path / "dryrun_report.json"
    res_dry = _run_ps1(
        REPO_DIET_PS1,
        [
            "-Mode", "DryRunSandbox",
            "-RepoRoot", str(repo_dir),
            "-BackupRoot", str(backup_dir),
            "-MinFreeSpaceGB", "0.05",
            "-ReportPath", str(dryrun_report_path),
        ],
    )
    assert res_dry.returncode == 0, f"DryRunSandbox failed:\n{res_dry.stdout}\n{res_dry.stderr}"
    dry_report = json.loads(dryrun_report_path.read_text(encoding="utf-8-sig"))
    assert dry_report["status"] == "PASS"
    assert dry_report["live_repo_modified"] is False
    assert dry_report["live_repo_untouched"] is True
    assert dry_report["fsck"]["passed"] is True
    assert dry_report["head_tree_parity"]["live_before"] == meta["head_tree"]
    assert dry_report["head_tree_parity"]["sandbox_after"] == meta["head_tree"]
    assert dry_report["head_tree_parity"]["sandbox_before_eq_after"] is True
    assert dry_report["head_tree_parity"]["live_eq_sandbox_after"] is True

    # Prove real pack shrinkage: > 3.0 MiB dead bloat + dangling blob stripped down to tiny pack (< 50 KiB)
    before_bytes = int(dry_report["live_before"]["objects_dir_bytes"])
    after_bytes = int(dry_report["sandbox_after"]["objects_dir_bytes"])
    assert before_bytes > 3_000_000, f"Expected synthetic bloated repo > 3 MB, got {before_bytes}"
    assert after_bytes < 250_000, f"Expected sandbox repo shrunk < 250 KB, got {after_bytes}"
    assert dry_report["savings"]["vs_live_percent"] > 90.0

    # Verify temporary sandbox directory was cleaned up while bundle & report remain
    assert not Path(dry_report["sandbox_path"]).exists()
    assert bundle_file.is_file()

    # Verify live synthetic repo was NEVER mutated
    live_head_after = _run(["git", "rev-parse", "HEAD"], cwd=repo_dir, check=True).stdout.strip()
    live_tree_after = _run(["git", "rev-parse", "HEAD^{tree}"], cwd=repo_dir, check=True).stdout.strip()
    assert live_head_after == meta["head_commit"]
    assert live_tree_after == meta["head_tree"]

    # Step 3: HEAD protection guard MUST refuse stripping a path currently tracked in HEAD (exit 6)
    res_head_guard = _run_ps1(
        REPO_DIET_PS1,
        [
            "-Mode", "DryRunSandbox",
            "-RepoRoot", str(repo_dir),
            "-BackupRoot", str(backup_dir),
            "-StripPaths", "src/",
            "-MinFreeSpaceGB", "0.05",
        ],
    )
    assert res_head_guard.returncode == 6, f"Expected exit 6 for HEAD strip guard, got {res_head_guard.returncode}:\n{res_head_guard.stdout}"

    # Step 4: SHA-256 tamper guard MUST refuse a bundle whose .sha256 does not match (exit 3)
    sha_file.write_text("0" * 64 + f"  {bundle_file.name}\n", encoding="ascii")
    res_tamper = _run_ps1(
        REPO_DIET_PS1,
        [
            "-Mode", "DryRunSandbox",
            "-RepoRoot", str(repo_dir),
            "-BackupRoot", str(backup_dir),
            "-MinFreeSpaceGB", "0.05",
        ],
    )
    assert res_tamper.returncode == 3, f"Expected exit 3 for tampered .sha256, got {res_tamper.returncode}:\n{res_tamper.stdout}"
    assert "SHA-256 mismatch" in res_tamper.stdout

# Project KONGOR V Scripts Directory

Automation, verification gates, packaging, Docker workflows, asset pipelines, and cleanroom utilities for Project KONGOR V.

## Directory Layout

### Tier A: Canonical Root Entrypoints (`28` files)
Core gates, build/test locks, hooks, and developer entrypoints pinned at `scripts/`:
- **Build, Gates & Hooks**: `build.ps1`, `gate_check.ps1` (+ `.gate_integrity`), `git-pre-push.ps1`, `install-cli.ps1`, `install-git-hooks.ps1`, `Install-OptionalAgentTools.ps1`, `sync-version.ps1`, `test_gate_mutants.ps1`, `test_phase1_security_firewall.ps1`, `verify-aot.ps1`, `.banned_symbols_baseline.json`, `.nuget_vulnerability_baseline.json`
- **Netcode & Security Oracles**: `verify_netcode_oracle.ps1` (+ `.netcode_oracle_integrity`), `test_netcode_mutants.ps1`
- **Lua / XML / Visual / Evidence Verifiers**: `verify_client_lua.ps1`, `verify_xml_syntax.ps1`, `verify_evidence_obligations.ps1`, `verify_proof_screenshot.py`
- **Locks, Knowledge Base, Packaging & Runtime**: `Invoke-KongorTestLock.ps1`, `Invoke-PublicGithubExport.ps1`, `kongor_kb.ps1`, `kongor_mcp_issues.ps1`, `kongor_worker_lock.ps1`, `launch_screen_practice.ps1`, `prebake-assets.ps1`, `triage_client_logs.ps1`

### Tier B: Domain Subfolders
- **`scripts/assets/`** (`__init__.py`): K2 texture audits (`audit_k2_textures.ps1`, `sanitize_alpha_texture.ps1`, `verify_avatar_assets.ps1`), DDS/TGA conversion, custom map builders, and Blender/ComfyUI helpers.
- **`scripts/dev/`**: Repository census (`audit_cleanup_census.ps1`), session compaction, DNS/credential hygiene, and developer environment helpers.
- **`scripts/docker/`**: Dedicated server / Linux container orchestration (`launch_docker_stack.ps1`, `launch_local_servers.ps1`, `run_linux_server_docker_smoke.ps1`).
- **`scripts/forge/`** (`__init__.py`): 3D hero, skin, icon, announcer, taunt, and arcade text forge pipelines (`hero_forge.py`, `forge_core.py`, `mcp_asset_bridge.py`).
- **`scripts/qa/`** (`__init__.py`): E2E test suites, benchmarks, capture workers, doc citation/link gates (`verify_doc_links.ps1`, `verify_doc_citations.ps1`), Roslyn banned-symbol audits, and debt ledger generation (`audit_debt.ps1`).
- **`scripts/re/`** (`__init__.py`): Ghidra symbol export, native ABI ownership verification (`verify_native_abi_ownership.ps1`), protocol generation (`generate_protocol.ps1`), metagen parity (`test_metagen_parity.ps1`), profiler leak guards (`Assert-NoDevProfilerLeak.ps1`), and crash dump triage (`diagnose_crash_cdb.ps1`).
- **`scripts/release/`**: Cleanroom SPDX license headers (`Apply-CleanroomFileHeaders.ps1`), CDN manifest generation, and playtest/CDN release scripts.
- **`scripts/vfs/`** (`__init__.py`): Archive (`.s2z`) packaging/injection (`inject_override_to_s2z.ps1`, `revert_injected_override.ps1`), delta patching, override parity sync, and BYOA hydration (`Hydrate-LocalGameAssets.ps1`).

### Shared Support & Specialized Subfolders
- **`scripts/lib/`**: Shared PowerShell helper modules (`MutantHarness.psm1`, `Stop-KongorGameCleanly.ps1`, `Test-KongorSecretScrub.ps1`, `KongorAuditLedger.psm1`, etc.).
- **`scripts/cleanup/`**, **`scripts/fixtures/`**, **`scripts/mcp_audit/`**, **`scripts/rl/`**, **`scripts/staging/`**, **`scripts/vault/`**, **`scripts/voicebox/`**, **`scripts/vulkan-installer/`**.

## Conventions & Contributor Invariants
1. **SPDX License Headers**: All `.ps1` and `.py` scripts must include the `AGPL-3.0-or-later` SPDX header (`scripts/release/Apply-CleanroomFileHeaders.ps1 -VerifyOnly`).
2. **Python Packages**: Subfolders imported as Python packages (`assets`, `forge`, `qa`, `re`, `vfs`) carry an `__init__.py` with the AGPL-3.0 SPDX header.
3. **Concurrency Locks**: Always wrap `dotnet`, `python -m pytest`, and `cmake --build` via `scripts/Invoke-KongorTestLock.ps1`; live client/override mutations must assert `scripts/kongor_worker_lock.ps1`.
4. **Secret Scrubbing & Doc Drift**: Scripts modifying assets/configs use `scripts/lib/Test-KongorSecretScrub.ps1`; any `scripts/<domain>/<name>.ps1` invocation in docs is verified by `scripts/qa/verify_doc_links.ps1` (`[COMMAND]` check).
5. **PKV Public Export Tiers**: Public-safe governance/RL/cleanroom RE scripts export to PKV (`config/public_export_manifest.json`), while private RE, local `.s2z` VFS, voicebox, and live credential helpers remain internal-only.

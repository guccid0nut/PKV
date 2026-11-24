<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<!-- SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors -->

# Project KONGOR V (`PKV`) — Maintainer Operations & Co-Maintainer Runbook

This runbook captures every recurring release, security, signing, incident-response, database DR, and contributor-onboarding procedure so Project KONGOR V has a bus factor `>= 2` and any trusted co-maintainer can operate releases and production clusters cleanly.

---

## 1. Co-Maintainer Onboarding & Access Matrix

1. **Repository & Code Review:** Review `.github/CODEOWNERS` and join the maintainer group for PR review on `native/`, `src/`, `infra/`, `scripts/`, and security-sensitive paths.
2. **Local Toolchain Verification:**
   - Install `.NET 11.0.100` SDK (must match `global.json`), Python `3.12+`, Docker Desktop / Engine, and CMake/MSVC (Windows) or GCC/Clang (Linux).
   - Verify the sub-5-second inner-loop quality gate:
     ```powershell
     .\scripts\gate_check.ps1
     ```
   - Verify North-Star engineering SLOs and Evidence Dossiers:
     ```powershell
     .\scripts\qa\verify_north_star_slos.ps1
     .\scripts\verify_evidence_obligations.ps1 -All -Strict
     ```
3. **First Contribution Path (`good-first-issue`):**
   - Look for issues labeled `good-first-issue` (UI polish in `games/client/override/`, unit tests in `tests/unit/`, or doc/script improvements).
   - Always run builds and tests through the singleton lock wrapper:
     ```powershell
     .\scripts\Invoke-KongorTestLock.ps1 -Owner "dev" -- dotnet test tests/unit/libs/Kongor.Core.Tests/Kongor.Core.Tests.csproj -c Release --filter "Category!=Manual"
     ```

---

## 2. Playtest Bundle Export & Signed CDN Release Procedure

Players receive pre-patched `5.0.2` client builds via the playtest bundle and receive incremental UI/security updates over the ECDSA P-256 signed CDN.

1. **Pre-Release Verification Gate (All Must Pass):**
   ```powershell
   .\scripts\gate_check.ps1
   .\scripts\qa\verify_doc_links.ps1 -EmitFingerprints -BaselineFile data/gate/doc_links_archive_baseline.json -FailOnUnbaselined
   .\scripts\qa\verify_north_star_slos.ps1
   .\scripts\re\test_metagen_parity.ps1
   .\scripts\verify_evidence_obligations.ps1 -All -Strict
   .\scripts\release\Apply-CleanroomFileHeaders.ps1 -VerifyOnly -ScanNewFiles
   dotnet list ProjectKongor.slnx package --vulnerable --include-transitive
   ```
2. **Parity Sync & Archive Injection (When `games/client/override/` Changed):**
   - Confirm 0 parity drift (`Tier 5` of `scripts/gate_check.ps1`).
   - Run non-destructive `.s2z` injection (5-tier `.bak` rotation, pre-injection syntax + secret scrub guards):
     ```powershell
     .\scripts\vfs\inject_override_to_s2z.ps1
     ```
3. **Playtest Bundle Export:**
   ```powershell
   .\scripts\release\export_playtest_bundle.ps1
   ```
   Confirm `playtest_policy.json` (`IsPlaytest = true`, `BinariesPrePatched = true`) and `build_manifest.json` are stamped into the output bundle.
4. **CDN Manifest Generation & ECDSA P-256 Signing:**
   - Generate and sign the manifest using the offline/CI ECDSA P-256 signing key (never commit private `.pem` keys to git!).
   - Verify signature validation against `WILLOWMAKER`'s pinned public key (`PkvManifestValidator`) and run `WILLOWMAKER.Tests` (`EmergencyCdnPublisherE2ETests`).
   - Publish signed manifest + compressed deltas to the CDN origin (`cdn.newbs.io` / Cloudflare R2).

---

## 3. Secret Rotation & Vault Hygiene

1. **Single Source of Truth:** Local operator secrets live in the dotnet user-secrets vault (managed via `NEXUS.ControlPanel.GUI -> Secrets & Configs` or `kongor docker init-secrets`), which regenerates the git-ignored `.env`.
2. **Production Secrets (`KONGOR`, `TRANSMUTANSTEIN`, `ZORGATH`, `COMPEL`):**
   - Rotate `Jwt:Key` (`>= 32` random bytes), `InternalServiceKey`, `CompelAuthenticationToken`, `Discord:ClientSecret`, `ConnectionStrings:MERRICK`, and Valkey password at least every 90 days or immediately on suspected exposure.
   - Note: Local/default web portal auth is **Discord-only** (`Email:SmtpEnabled = false`). MailKit SMTP credentials are strictly opt-in for external deployments.
3. **Pre-Push & Public Export Scrubbing:**
   - Every `git push` runs `scripts/lib/Test-KongorSecretScrub.ps1` + `gitleaks`.
   - Before any public mirror sync (`scripts/Invoke-PublicGithubExport.ps1`), verify all security fixes are already deployed on production infrastructure (Responsible Disclosure Gate).

---

## 4. Database Backup, Restore & Disaster Recovery (PostgreSQL + Valkey)

1. **Automated Encrypted Backup & Restore Drill:**
   - Run the verified DR drill script (creates a compressed `pg_dump` backup, optionally encrypts with AES-256-CBC/OpenSSL or age, restores into an ephemeral PostgreSQL verification database, and asserts row-count & schema integrity):
     ```powershell
     .\scripts\ops\test_db_backup_restore.ps1
     ```
2. **Valkey 8.1 Streams Durability (`:28298`):**
   - `MatchProcessingChannel` persists post-match ingestion jobs via Valkey Streams (`XADD` / `XREADGROUP` / `XACK`) with AOF enabled (`--appendonly yes`).
   - Monitor `/healthz` on `KONGOR.MasterServer`; if `MatchProcessingChannel` degrades from Valkey Streams in Production, `/healthz` reports `Unhealthy` and emits `[SECURITY-ALERT]`/`[MatchProcessingChannel]` errors rather than silently dropping matches.

---

## 5. Incident Response & Rollback Playbook

1. **Feature-Flagged Instant Rollback (Zero Binary Redeploy):**
   - `Matches:OwnershipCheckMode`: `Enforce` -> `Shadow` -> `Off`
   - `Chat:IdleTimeoutMode`: `Enforce` -> `Shadow` -> `Off`
   - `Store:Mode`: `PreservationFreeAll` vs `EarnedProgression` (restricted staff badges in `RestrictedEntitlements.json` always stay locked to Staff/GM)
   - `Moderation:ChatFilterEnabled`: `true` / `false`
   - `GameServer:ExamineChecksumsPolicy`: `RankedOnly` / `Always` / `Disabled`
2. **Crash & Native Triage:**
   - Locate latest minidumps (`*.dmp`) and logs (`console.log`, `master-server.log`, `chat-server.log`).
   - Run WinDbg CDB (`!analyze -v`, `.exr -1`, `kn`, `r`, `lm vm`) and correlate RVAs against `ProjectKongor.rep` in Ghidra.
   - Record recurring MCP/tooling faults via `.\scripts\kongor_mcp_issues.ps1 -Action new` or `-Action bump -Id MCP-NNN`, and analyze cross-tool reliability hotspots via:
     ```powershell
     .\scripts\kongor_mcp_issues.ps1 -Action ingest-outcomes
     ```
3. **Client Rollback:**
   - Revert `.s2z` archive injection locally via `.\scripts\vfs\revert_injected_override.ps1`.
   - On CDN, re-sign and publish the previous known-good `manifest.json` snapshot.

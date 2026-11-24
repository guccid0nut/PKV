# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Programmatic census & zero-caller verifier for Project KONGOR V cleanup waves.

.DESCRIPTION
    Dynamically inventories tracked and untracked-visible files under scripts/, tools/,
    src/tools/, and repository root (plus ignored files under scripts/). Classifies every
    file under scripts/ into TierA (28 frozen root entrypoints), ExistingSubfolders,
    TierB/<domain>, TierC (dead one-offs + ignored .pyc/.bak_*), or UNCLASSIFIED.
    Fails closed (non-zero exit) if any tracked/untracked-visible script is UNCLASSIFIED
    unless -AllowUnclassified is specified.

    Scans active repository text files (excluding artifacts/telemetry/**, docs/archive/**,
    docs/reference/self-healing-journeys/**, and audit_cleanup_census.ps1 itself) and emits:
      - artifacts/telemetry/cleanup-census.json
      - artifacts/telemetry/cleanup-dead-scripts.ndjson

.PARAMETER RepoRoot
    Optional repository root path. Defaults to two levels above $PSScriptRoot.

.PARAMETER OutDir
    Optional output directory for telemetry files. Defaults to <RepoRoot>/artifacts/telemetry.

.PARAMETER AllowUnclassified
    Allows exit 0 even when UNCLASSIFIED scripts exist (still logs and reports them).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$RepoRoot = "",

    [Parameter(Mandatory = $false)]
    [string]$OutDir = "",

    [Parameter(Mandatory = $false)]
    [switch]$AllowUnclassified = $false
)

$ErrorActionPreference = "Stop"
$swTotal = [System.Diagnostics.Stopwatch]::StartNew()

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
} else {
    $RepoRoot = (Resolve-Path $RepoRoot).Path
}

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $RepoRoot "artifacts\telemetry"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [CLEANUP CENSUS] Project KONGOR V Programmatic Census & Dead-Script Verifier" -ForegroundColor Cyan
Write-Host " RepoRoot : $RepoRoot" -ForegroundColor Gray
Write-Host " OutDir   : $OutDir" -ForegroundColor Gray
Write-Host "================================================================================" -ForegroundColor Cyan

function Invoke-GitList {
    param(
        [string[]]$GitArgs
    )
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $raw = & git -C $RepoRoot --no-optional-locks @GitArgs 2>&1
        $ec = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prevEap
    }
    if ($ec -ne 0) {
        $msg = ($raw | ForEach-Object { [string]$_ }) -join "`n"
        throw "git $($GitArgs -join ' ') failed (exit=$ec): $msg"
    }
    $list = New-Object System.Collections.Generic.List[string]
    foreach ($line in $raw) {
        if ($line -is [System.Management.Automation.ErrorRecord]) { continue }
        $s = ([string]$line).Trim()
        if (-not [string]::IsNullOrWhiteSpace($s)) {
            $list.Add(($s -replace '\\', '/'))
        }
    }
    return ,$list.ToArray()
}

function Get-FileSha256Safe {
    param([string]$FullPath)
    if (-not [System.IO.File]::Exists($FullPath)) { return "" }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fs = $null
    try {
        $fs = [System.IO.File]::OpenRead($FullPath)
        $bytes = $sha.ComputeHash($fs)
        return ([System.BitConverter]::ToString($bytes)).Replace("-", "").ToLowerInvariant()
    } finally {
        if ($null -ne $fs) { $fs.Dispose() }
        $sha.Dispose()
    }
}

# 1. Classification Tables (Plan v3 + Verified seq_28/seq_29 + Horizon 2.5)
$tierASet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in @(
    ".banned_symbols_baseline.json",
    ".gate_integrity",
    ".netcode_oracle_integrity",
    ".nuget_vulnerability_baseline.json",
    "build.ps1",
    "gate_check.ps1",
    "git-pre-push.ps1",
    "install-cli.ps1",
    "install-git-hooks.ps1",
    "Install-OptionalAgentTools.ps1",
    "Invoke-KongorTestLock.ps1",
    "Invoke-PublicGithubExport.ps1",
    "kongor_kb.ps1",
    "kongor_mcp_issues.ps1",
    "kongor_worker_lock.ps1",
    "launch_screen_practice.ps1",
    "prebake-assets.ps1",
    "sync-version.ps1",
    "test_gate_mutants.ps1",
    "test_netcode_mutants.ps1",
    "test_phase1_security_firewall.ps1",
    "triage_client_logs.ps1",
    "verify-aot.ps1",
    "verify_client_lua.ps1",
    "verify_evidence_obligations.ps1",
    "verify_netcode_oracle.ps1",
    "verify_proof_screenshot.py",
    "verify_xml_syntax.ps1"
)) { [void]$tierASet.Add($name) }

$existingSubfolders = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($sub in @(
    "lib", "cleanup", "fixtures", "mcp_audit", "rl", "staging", "vault", "voicebox", "vulkan-installer"
)) { [void]$existingSubfolders.Add($sub) }

$tierBByDomain = @{
    "forge" = @(
        "announcer_forge.py",
        "arcade_text_forge.py",
        "blender_arcade_text_builder.py",
        "dsp_audio_fallback.py",
        "forge_core.py",
        "hero_forge.py",
        "icon_forge.py",
        "mcp_asset_bridge.py",
        "skin_forge.py",
        "taunt_forge.py"
    )
    "docker" = @(
        "invoke_linux_client_journey_container.ps1",
        "launch_docker_stack.ps1",
        "launch_local_servers.ps1",
        "launch_queue_minigame_server.ps1",
        "provision_windows_account.ps1",
        "queue_linux_ranked.ps1",
        "run_linux_bot_soak.ps1",
        "run_linux_server_docker_smoke.ps1",
        "send_linux_ready.ps1",
        "set_matchmaking_journey_mode.ps1",
        "sync_linux_dedicated_bots.ps1"
    )
    "release" = @(
        "Apply-CleanroomFileHeaders.ps1",
        "export_playtest_bundle.ps1",
        "generate_pkv_cdn_manifests.ps1",
        "publish_cdn_update.ps1"
    )
    "dev" = @(
        "audit_cleanup_census.ps1",
        "compact_session_state.ps1",
        "fix_dns_cloudflare.ps1",
        "git-mirror-local-backup.ps1",
        "install_tao_mcp.ps1",
        "optimize_dev_ram.ps1",
        "query_modern_web_guidance.ps1",
        "restore_quarantine.ps1",
        "rotate_live_credentials.ps1",
        "sanitize_workspace.ps1",
        "scan-dependencies.ps1",
        "start_cluster_and_client_mcp.ps1"
    )
    "re" = @(
        "Assert-NoDevProfilerLeak.ps1",
        "bootstrap_decompiler.ps1",
        "debug_hon_cdb.ps1",
        "decompile_bytecode.ps1",
        "deploy_native_guard.ps1",
        "diagnose_crash_cdb.ps1",
        "diff_dossiers.ps1",
        "export_ghidra_symbols.ps1",
        "frida_diagnose_lag.js",
        "generate_protocol.ps1",
        "generate_synthetic_pdbs.ps1",
        "revert_native_guard.ps1",
        "scaffold_evidence_dossier.py",
        "test_fma_lockstep.ps1",
        "test_hook_toggle.ps1",
        "test_metagen_parity.ps1",
        "test_synthetic_pdbs.ps1",
        "transfer_cross_binary_symbols.ps1",
        "triangulate_rva.js",
        "validate_native_hooks.py",
        "verify_dossier_corpus.ps1",
        "verify_native_abi_ownership.ps1",
        "verify_native_guard_surface.ps1",
        "verify_vfs_resolution.js"
    )
    "vfs" = @(
        "Hydrate-LocalGameAssets.ps1",
        "apply_delta_patch.ps1",
        "asset_registry.py",
        "audit_missing_textures.py",
        "build_client_content.py",
        "check_injection_status.ps1",
        "create_delta_patch.ps1",
        "evict_stale_textures.py",
        "evict_verified_override.ps1",
        "export_cvar_catalog.ps1",
        "inject_override_to_s2z.ps1",
        "pack_canonical_s2z.ps1",
        "quarantine_loose_game_assets.ps1",
        "read_archive_entry.ps1",
        "remediate_phase2_assets.ps1",
        "repack_clean_s2z.py",
        "resolve_missing_asset.ps1",
        "restore_custom_hero_textures.py",
        "revert_injected_override.ps1",
        "seed_dxvk_cache.ps1",
        "split_oversized_archives.ps1",
        "sync_client_files.ps1",
        "sync_engine_configs.py",
        "sync_k2_engine_catalogs.ps1",
        "sync_override_parity.ps1",
        "sync_override_to_shared.ps1",
        "update_tao_build.ps1",
        "verify_archives.ps1",
        "verify_custom_maps_s2z.ps1",
        "verify_map_asset_refs.ps1",
        "verify_vfs_completeness.ps1"
    )
    "assets" = @(
        "audit_full_avatar_roster.py",
        "audit_k2_textures.ps1",
        "batch_convert_tga_to_dds.ps1",
        "build_10v10_chaos_maps.py",
        "build_custom_maps_catalog.py",
        "build_k2_entity_schema.py",
        "build_seasonal_halloween_maps.py",
        "export_discord_drp_assets.py",
        "fix_mixamo_glb.py",
        "generate_botmetadata.py",
        "launch_comic_studio.ps1",
        "launch_model_viewer.ps1",
        "launch_physics_sandbox.ps1",
        "rebake_all_normal_maps_rxgb.ps1",
        "remaster_texture_pipeline.py",
        "run_comfyui.bat",
        "run_comfyui.ps1",
        "sanitize_alpha_texture.ps1",
        "update_devo_map_settings.ps1",
        "update_pebbles_duel_map.ps1",
        "verify_avatar_assets.ps1"
    )
    "qa" = @(
        ".benchmark_baseline.json",
        ".benchmark_map_load_baseline.json",
        "Invoke-KongorAbBenchmark.ps1",
        "Invoke-KongorAutoRatchet.ps1",
        "Invoke-KongorBlastRadius.ps1",
        "Invoke-KongorDeterminismProbe.ps1",
        "Invoke-KongorLiveCanary.ps1",
        "Send-GameServerCommand.ps1",
        "Send-KongorWorkerSteer.ps1",
        "analyze_video_pacing.py",
        "audit-banned-symbols.ps1",
        "audit_debt.ps1",
        "audit_git_lfs_debt.ps1",
        "benchmark_baseline.ps1",
        "benchmark_goose_ab.ps1",
        "benchmark_map_load.ps1",
        "capture_contact_sheet.ps1",
        "capture_proc_audio.ps1",
        "capture_verified_window.ps1",
        "capture_worker.ps1",
        "clean-workspace.ps1",
        "coordinator_capture.ps1",
        "export_audit_log.ps1",
        "invoke_k2_lua.ps1",
        "invoke_tao_mcp.py",
        "kongor_shj.ps1",
        "launch_onscreen_client.ps1",
        "launch_spectator_stream.ps1",
        "live_monitor_capture.ps1",
        "mac_remote_test.ps1",
        "record_verified_clip.ps1",
        "reload_ui.ps1",
        "render-replay.ps1",
        "run-all-cli-audits.ps1",
        "run-soak-test.ps1",
        "run_wave5_live_e2e.ps1",
        "send_cmd.ps1",
        "simulate_queue_minigame_e2e.ps1",
        "snap_desktop.ps1",
        "soak_mailbox_idle_10m.ps1",
        "test_cas_integrity.ps1",
        "test_emergency_cdn_e2e.ps1",
        "test_friendships_e2e.ps1",
        "test_ipc.ps1",
        "test_map_smoke.ps1",
        "test_phase2_epistemic_dossiers.ps1",
        "test_phase2_midwars.ps1",
        "test_phase4_integrity.ps1",
        "test_phase4_worker_and_crash_gates.ps1",
        "test_phase5_integrity.ps1",
        "test_verify_avatar_assets.ps1",
        "test_visual_audio_oracles.ps1",
        "test_zombie_guard.ps1",
        "verify_doc_citations.ps1",
        "verify_doc_links.ps1",
        "verify_logout_cycle.ps1",
        "verify_north_star_slos.ps1",
        "verify_phase5_live.py",
        "verify_store_tabs.ps1",
        "verify_type_and_drag.py"
    )
}

$tierBLookup = @{}
foreach ($dom in $tierBByDomain.Keys) {
    foreach ($fn in $tierBByDomain[$dom]) {
        $tierBLookup[$fn] = $dom
    }
}

$tierCSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

# Historical Phase 2a removed Tier C scripts (deleted in Wave 4 Commit 4A)
$removedInPhase2aSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($deadName in @(
    # 3 log/deprecated/duplicate
    "audit_worker8_out.txt",
    "flip_pedestal_textures.py",
    "install-vulkan.sh",
    # 10 ad-hoc login lua probes
    "dump_loginbox.lua",
    "find_black_box.lua",
    "hide_widgets.lua",
    "inspect_black_boxes.lua",
    "inspect_login_widgets.lua",
    "inspect_texture.lua",
    "inspect_widgets.lua",
    "test_cmd.lua",
    "test_logo_s2.lua",
    "test_png_textures.lua",
    # 42 worker/shard batch scripts
    "apply_worker14_effects_fix.py",
    "audit_all_attachments_worker10.ps1",
    "audit_all_avatars_worker7.py",
    "audit_effects_w8.py",
    "audit_worker13.py",
    "audit_worker14_deep.py",
    "audit_worker14_weapon_effects.py",
    "audit_worker2.py",
    "audit_worker2_deep.ps1",
    "audit_worker3.ps1",
    "audit_worker3_skeptical.py",
    "audit_worker4_deep.py",
    "audit_worker7_deep.py",
    "audit_worker7_effects.ps1",
    "audit_worker7_effects.py",
    "audit_worker_10_deep.ps1",
    "enum_windows_worker.ps1",
    "extract_trapped_assets_worker_a.ps1",
    "find_all_worker14_attachments.py",
    "inspect_all_attachments_worker7.py",
    "inspect_worker10_details.ps1",
    "worker10_apply_fixes.ps1",
    "worker11_upgrade_heroes.ps1",
    "worker12_upgrade_heroes.ps1",
    "worker14_upgrade_heroes.ps1",
    "worker14_upgrade_heroes_remedy.ps1",
    "worker14_verify_all.ps1",
    "worker1_high_quality_sync.ps1",
    "worker2_modernize_and_fix.ps1",
    "worker2_verify_all.ps1",
    "worker3_fix_meshes.ps1",
    "worker3_remedy_subvariants.ps1",
    "worker3_upgrade_and_fix.ps1",
    "worker3_verify_all.ps1",
    "worker4_fix_and_upgrade.ps1",
    "worker5_process_heroes.ps1",
    "worker6_process_heroes.ps1",
    "worker7_upgrade_effects.ps1",
    "worker8_fix_all.ps1",
    "worker8_process_heroes.ps1",
    "worker8_verify_all.ps1",
    "worker9_fix_all.ps1",
    # 37 hero/clip/BOM one-shots
    "audit_all_model_dirs.ps1",
    "audit_attachments.py",
    "audit_expanded_weapon_effects.py",
    "audit_hero_avatars_deep.ps1",
    "audit_heroes_1_to_6.ps1",
    "audit_referenced_effects.py",
    "audit_target_effects.py",
    "blender_ops.py",
    "check_all_heroes_weapons.ps1",
    "check_avatar_mdfs.ps1",
    "check_boost.py",
    "check_pearl_plant_pest.ps1",
    "compare_avatar_coverage.ps1",
    "final_clip_fixes.ps1",
    "find_all_attachments_comprehensive.py",
    "find_all_model_effects.py",
    "find_all_weapon_dirs.ps1",
    "find_missing_clips.ps1",
    "find_skeleton_attachments.py",
    "find_sotm_boost.ps1",
    "find_yoshi.ps1",
    "fix_empath_clips.ps1",
    "fix_prop_clips.ps1",
    "fix_remaining_gadgets.ps1",
    "inspect_all_1937_effects.py",
    "inspect_all_boost_and_weapon_effects.py",
    "inspect_heroes_structure.py",
    "inspect_other_heroes_effects.ps1",
    "inspect_store_body_effects.py",
    "list_archive_boms.py",
    "list_candidate_effects.py",
    "repair_store_l.py",
    "scan_weapon_effects.py",
    "strip_boms_from_archives.py",
    "sync_all_empath_ghostly_clips.ps1",
    "sync_base_models.ps1",
    "verify_effects.py",
    # 60 ad-hoc login/pedestal/store/win32/boot one-shots
    "align_medallion.py",
    "audit_avatar_skeletons_and_paths.py",
    "audit_material_texture_refs.py",
    "bake_abyssal_ivory_staff.py",
    "bake_heartbeat_3d_sphere.py",
    "batch_inject_login_to_s2z.ps1",
    "check_archive_contents.ps1",
    "check_crash.ps1",
    "check_dds_header.ps1",
    "check_mats.py",
    "check_tex_exists.py",
    "check_tga_stats.ps1",
    "convert_all_effect_textures_to_tga.py",
    "decompose_store.py",
    "diagnose_client_boot.ps1",
    "eval_lua.ps1",
    "evict_loose_corrupt_textures.py",
    "find_line_531.ps1",
    "find_missing_preview_tags.ps1",
    "find_real_dual_mesh.py",
    "find_tags.py",
    "fix_skeleton_mismatches.py",
    "full_onscreen_capture.py",
    "generate_missing_system_button_textures.py",
    "get_main_thread.ps1",
    "hydrate_untrapped_textures.ps1",
    "inspect_alt_platform_archives.py",
    "inspect_asset_db.py",
    "inspect_clip.py",
    "inspect_desktop_windows.ps1",
    "inspect_glb.py",
    "inspect_glow_tga.py",
    "inspect_mismatches.py",
    "inspect_model.py",
    "inspect_pedestal_tgas.py",
    "inspect_spotlight_mats.py",
    "inspect_trophy_skins.py",
    "list_pipes.ps1",
    "measure_disc_center.py",
    "pack_verified_fixes.ps1",
    "patch_heroes_preview_xml.ps1",
    "patch_startup_cfg.py",
    "query_assets.py",
    "query_fonts.py",
    "read_legion_light.py",
    "read_platform_res0.py",
    "read_sample_effects.py",
    "read_underside_effect.py",
    "run_capture.ps1",
    "run_enum.ps1",
    "sample_pool_rgb.py",
    "search_font_defs.py",
    "search_light_effects.py",
    "search_ui_effects.py",
    "strip_all_boms.py",
    "sync_store_preview_stage.ps1",
    "test_find_window.py",
    "test_rect.ps1",
    "test_win.ps1",
    "track_liveness.ps1"
)) { [void]$removedInPhase2aSet.Add($deadName) }

# 2. Inventory scripts/, tools/, src/tools/, and repo root dynamically
Write-Host "[1/4] Collecting dynamic git and filesystem inventories..." -ForegroundColor Yellow

$scriptsTracked = Invoke-GitList @("ls-files", "--", "scripts")
$scriptsUntracked = Invoke-GitList @("ls-files", "--others", "--exclude-standard", "--", "scripts")
$scriptsIgnored = Invoke-GitList @("ls-files", "--others", "--ignored", "--exclude-standard", "--", "scripts")

$toolsTracked = Invoke-GitList @("ls-files", "--", "tools")
$toolsUntracked = Invoke-GitList @("ls-files", "--others", "--exclude-standard", "--", "tools")

$srcToolsTracked = Invoke-GitList @("ls-files", "--", "src/tools")
$srcToolsUntracked = Invoke-GitList @("ls-files", "--others", "--exclude-standard", "--", "src/tools")

$allTrackedSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($tf in (Invoke-GitList @("ls-files"))) { [void]$allTrackedSet.Add($tf) }

$allIgnoredTop = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$topEntries = @(Get-ChildItem -LiteralPath $RepoRoot -Force | Where-Object { $_.Name -ne ".git" } | Sort-Object Name)
$topNamesForCheck = @($topEntries | ForEach-Object { $_.Name })
if ($topNamesForCheck.Count -gt 0) {
    $ignoredMatched = $topNamesForCheck | & git -C $RepoRoot --no-optional-locks check-ignore --stdin 2>$null
    foreach ($ig in $ignoredMatched) {
        $igClean = ([string]$ig).Trim().Trim('"').TrimEnd('/')
        if (-not [string]::IsNullOrWhiteSpace($igClean)) {
            [void]$allIgnoredTop.Add($igClean)
        }
    }
}

$rootInventory = New-Object System.Collections.Generic.List[object]
foreach ($entry in $topEntries) {
    $kind = if ($entry.PSIsContainer) { "dir" } else { "file" }
    $isTracked = $false
    if ($kind -eq "file") {
        $isTracked = $allTrackedSet.Contains($entry.Name)
    } else {
        $prefix = $entry.Name + "/"
        foreach ($tf in $allTrackedSet) {
            if ($tf.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $isTracked = $true
                break
            }
        }
    }
    $isIgnored = $allIgnoredTop.Contains($entry.Name)
    $gitStatusTag = if ($isTracked) { "tracked" } elseif ($isIgnored) { "ignored" } else { "untracked" }
    $rootInventory.Add([PSCustomObject]@{
        name = $entry.Name
        kind = $kind
        gitState = $gitStatusTag
        sizeBytes = if ($kind -eq "file") { $entry.Length } else { $null }
    })
}

# 3. Classify every file under scripts/
Write-Host "[2/4] Classifying all files under scripts/..." -ForegroundColor Yellow

$scriptRecords = New-Object System.Collections.Generic.List[object]
$unclassifiedList = New-Object System.Collections.Generic.List[object]
$tierCCandidates = New-Object System.Collections.Generic.List[object]

function Add-ClassifiedScript {
    param(
        [string]$RelPath,
        [string]$GitState
    )
    $sub = $RelPath.Substring("scripts/".Length)
    $segments = $sub -split '/'
    $leaf = $segments[-1]
    $tier = "UNCLASSIFIED"
    $domain = ""
    $targetPath = $RelPath

    # Ignored bytecode & timestamped backups are always TierC
    if ($sub -match '(^|/)__pycache__/.*\.pyc$' -or $leaf -match '\.bak_\d+') {
        $tier = "TierC"
        $domain = "ignored_artifact"
        $targetPath = $null
    } elseif ($segments.Count -eq 1) {
        if ($tierASet.Contains($leaf)) {
            $tier = "TierA"
            $domain = "root_entrypoint"
            $targetPath = "scripts/$leaf"
        } elseif ($leaf -eq "README.md") {
            $tier = "Docs"
            $domain = "root_docs"
            $targetPath = "scripts/README.md"
        } elseif ($tierBLookup.ContainsKey($leaf)) {
            $dom = $tierBLookup[$leaf]
            $tier = "TierB"
            $domain = $dom
            $targetPath = "scripts/$dom/$leaf"
        } elseif ($tierCSet.Contains($leaf)) {
            $tier = "TierC"
            $domain = "dead_oneoff"
            $targetPath = $null
        }
    } elseif ($segments.Count -ge 2) {
        $topDir = $segments[0]
        if ($existingSubfolders.Contains($topDir)) {
            $tier = "ExistingSubfolders"
            $domain = $topDir
            $targetPath = $RelPath
        } elseif ($tierBByDomain.ContainsKey($topDir) -and $segments.Count -eq 2 -and ($leaf -eq "__init__.py" -or ($tierBLookup.ContainsKey($leaf) -and $tierBLookup[$leaf] -eq $topDir))) {
            $tier = "TierB"
            $domain = $topDir
            $targetPath = "scripts/$topDir/$leaf"
        }
    }

    $rec = [PSCustomObject]@{
        path = $RelPath
        leaf = $leaf
        stem = [System.IO.Path]::GetFileNameWithoutExtension($leaf)
        gitState = $GitState
        tier = $tier
        domain = $domain
        targetPath = $targetPath
    }
    $scriptRecords.Add($rec)

    if ($tier -eq "UNCLASSIFIED") {
        $unclassifiedList.Add($rec)
    } elseif ($tier -eq "TierC") {
        $tierCCandidates.Add($rec)
    }
}

$seenScriptPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($p in $scriptsTracked) {
    if ($seenScriptPaths.Add($p)) { Add-ClassifiedScript -RelPath $p -GitState "tracked" }
}
foreach ($p in $scriptsUntracked) {
    if ($seenScriptPaths.Add($p)) { Add-ClassifiedScript -RelPath $p -GitState "untracked" }
}
foreach ($p in $scriptsIgnored) {
    if ($seenScriptPaths.Add($p)) { Add-ClassifiedScript -RelPath $p -GitState "ignored" }
}

$tierCounts = [ordered]@{
    TierA = @($scriptRecords | Where-Object { $_.tier -eq "TierA" }).Count
    ExistingSubfolders = @($scriptRecords | Where-Object { $_.tier -eq "ExistingSubfolders" }).Count
    TierB = @($scriptRecords | Where-Object { $_.tier -eq "TierB" }).Count
    TierC = @($scriptRecords | Where-Object { $_.tier -eq "TierC" }).Count
    UNCLASSIFIED = $unclassifiedList.Count
}

$tierBDomainCounts = [ordered]@{}
foreach ($dom in ($tierBByDomain.Keys | Sort-Object)) {
    $tierBDomainCounts[$dom] = @($scriptRecords | Where-Object { $_.tier -eq "TierB" -and $_.domain -eq $dom }).Count
}
$existingSubfolderCounts = [ordered]@{}
foreach ($sub in ($existingSubfolders | Sort-Object)) {
    $existingSubfolderCounts[$sub] = @($scriptRecords | Where-Object { $_.tier -eq "ExistingSubfolders" -and $_.domain -eq $sub }).Count
}

Write-Host ("  scripts/ totals: total=$($scriptRecords.Count) TierA=$($tierCounts.TierA) ExistingSubfolders=$($tierCounts.ExistingSubfolders) TierB=$($tierCounts.TierB) TierC=$($tierCounts.TierC) UNCLASSIFIED=$($tierCounts.UNCLASSIFIED)") -ForegroundColor Gray
if ($unclassifiedList.Count -gt 0) {
    Write-Host "  [WARN] UNCLASSIFIED files found under scripts/:" -ForegroundColor Red
    foreach ($u in $unclassifiedList) {
        Write-Host "    - $($u.path) [$($u.gitState)]" -ForegroundColor Red
    }
}

# 4. Scan active repository text files for Tier C caller references & dynamic interpolation
Write-Host "[3/4] Scanning active repository text files for TierC callers and dynamic dispatch..." -ForegroundColor Yellow

$scanRoots = @("src", "native", "tools", "scripts", "tests", "docs", "infra", "config", ".github", ".githooks", ".agents")
$candidateScanFiles = New-Object System.Collections.Generic.List[string]
$seenScanSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

foreach ($tf in $allTrackedSet) {
    $isRootFile = ($tf -notmatch '/')
    $inScope = $isRootFile
    if (-not $inScope) {
        foreach ($sr in $scanRoots) {
            if ($tf.StartsWith($sr + "/", [System.StringComparison]::OrdinalIgnoreCase)) {
                $inScope = $true
                break
            }
        }
    }
    if (-not $inScope) { continue }
    if ($seenScanSet.Add($tf)) { $candidateScanFiles.Add($tf) }
}

foreach ($ut in (Invoke-GitList @("ls-files", "--others", "--exclude-standard", "--", $scanRoots))) {
    if ($seenScanSet.Add($ut)) { $candidateScanFiles.Add($ut) }
}

$binaryExtRegex = '\.(?:png|jpg|jpeg|dds|tga|bmp|ico|gif|webp|wav|ogg|mp3|mp4|mkv|webm|s2z|kpatch|dll|exe|lib|pdb|obj|exp|nupkg|jar|pyc|zip|7z|gz|parquet|bin|glb|fbx|db|sqlite3|pcapng|dmp)$'
$excludePathRegex = '^(?:artifacts/telemetry/|config/header_provenance_manifest\.json$|docs/archive/|docs/reference/self-healing-journeys/|scripts/dev/audit_cleanup_census\.ps1$|src/tools/clangd/clangd_23\.1\.0/|src/tools/ghidra/projects/|src/tools/comfyui/|src/tools/voicebox/voicebox-repo/|src/tools/proximo/|src/tools/mixamo-mcp/browser_profile/)'

$searchFiles = New-Object System.Collections.Generic.List[string]
foreach ($rel in $candidateScanFiles) {
    if ($rel -match $excludePathRegex) { continue }
    if ($rel -match $binaryExtRegex) { continue }
    $full = Join-Path $RepoRoot ($rel -replace '/', '\')
    if (-not [System.IO.File]::Exists($full)) { continue }
    $fi = [System.IO.FileInfo]::new($full)
    if ($fi.Length -gt 2097152) { continue }
    $searchFiles.Add($rel)
}

Write-Host "  Text files queued for caller scan: $($searchFiles.Count)" -ForegroundColor Gray

# Build fast single-pass regexes for tracked TierC leaf names and python module imports
$trackedDeadRecs = @($tierCCandidates | Where-Object { $_.domain -ne "ignored_artifact" })
$leafToPaths = @{}
$pyStemToPaths = @{}
foreach ($dr in $trackedDeadRecs) {
    if (-not $leafToPaths.ContainsKey($dr.leaf)) {
        $leafToPaths[$dr.leaf] = New-Object System.Collections.Generic.List[string]
    }
    $leafToPaths[$dr.leaf].Add($dr.path)
    if ($dr.leaf -match '\.py$') {
        if (-not $pyStemToPaths.ContainsKey($dr.stem)) {
            $pyStemToPaths[$dr.stem] = New-Object System.Collections.Generic.List[string]
        }
        $pyStemToPaths[$dr.stem].Add($dr.path)
    }
}

$targetLeaves = [string[]]@($leafToPaths.Keys)
$targetPyStems = [string[]]@($pyStemToPaths.Keys)

$dynamicPatterns = @(
    @{ id = "PS_PSSCRIPTROOT_INTERP"; regex = '\$PSScriptRoot[\\/][A-Za-z0-9_.-]{0,60}\$\{?[A-Za-z_][A-Za-z0-9_]*\}?[A-Za-z0-9_.*-]{0,60}\.(?:ps1|py|js|lua)' },
    @{ id = "SCRIPTS_VAR_INTERP"; regex = 'scripts[\\/][A-Za-z0-9_.-]{0,60}\$\{?[A-Za-z_][A-Za-z0-9_]*\}?[A-Za-z0-9_.*-]{0,60}\.(?:ps1|py|js|lua)' },
    @{ id = "PY_FSTRING_SCRIPTS"; regex = 'f["''][^"''\r\n]{0,60}scripts/\{[^"''\r\n]{1,60}\}[^"''\r\n]{0,60}["'']' },
    @{ id = "JOIN_PATH_SCRIPTS_VAR"; regex = 'Join-Path[^;\r\n]{0,100}["'']scripts["''][^;\r\n]{0,100}\$[A-Za-z_][A-Za-z0-9_]*' }
)

if (-not ("KongorCleanupCensusScannerV2" -as [type])) {
    $scannerCs = @"
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

public static class KongorCleanupCensusScannerV2 {
    public sealed class ScanHit {
        public string RelFile;
        public string Kind;
        public string Key;
        public string PatternId;
        public string Prefix;
        public string Snippet;
    }

    private static bool IsTokenChar(char c) {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-';
    }

    public static List<ScanHit> ScanAll(
        string repoRoot,
        string[] relFiles,
        string[] targetLeaves,
        string[] targetPyStems,
        string[] dynIds,
        string[] dynRegexes)
    {
        var leafSet = new HashSet<string>(targetLeaves, StringComparer.OrdinalIgnoreCase);
        var pyStemSet = new HashSet<string>(targetPyStems, StringComparer.Ordinal);
        var extRx = new Regex(@"[A-Za-z0-9_.-]+\.(?:ps1|py|lua|txt|sh)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(5));
        var pyRx = new Regex(@"(?:\bfrom\s+(?:scripts\.)?|\bimport\s+(?:scripts\.)?)([A-Za-z0-9_]+)\b", RegexOptions.Compiled, TimeSpan.FromSeconds(5));
        var dynRxs = new Regex[dynRegexes.Length];
        for (int i = 0; i < dynRegexes.Length; i++) {
            dynRxs[i] = new Regex(dynRegexes[i], RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(5));
        }
        var prefixRx = new Regex(@"(?:scripts[\\/]|PSScriptRoot[\\/])([A-Za-z0-9_-]{3,})[\$\{]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var bag = new ConcurrentBag<ScanHit>();
        Parallel.ForEach(relFiles, rel => {
            string full = Path.Combine(repoRoot, rel.Replace('/', '\\'));
            string content;
            try {
                content = File.ReadAllText(full);
            } catch (Exception ex) {
                bag.Add(new ScanHit { RelFile = rel, Kind = "error", PatternId = "File.ReadAllText", Snippet = ex.GetType().Name + ": " + ex.Message });
                return;
            }
            if (string.IsNullOrEmpty(content)) return;

            bool hasExt = content.IndexOf(".ps1", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf(".py", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf(".lua", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf(".txt", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf(".sh", StringComparison.OrdinalIgnoreCase) >= 0;

            if (hasExt) {
                try {
                    foreach (Match m in extRx.Matches(content)) {
                        int endIdx = m.Index + m.Length;
                        if (endIdx < content.Length && IsTokenChar(content[endIdx])) continue;
                        string tok = m.Value;
                        if (leafSet.Contains(tok)) {
                            bag.Add(new ScanHit { RelFile = rel, Kind = "leaf", Key = tok });
                        }
                    }
                } catch (Exception ex) {
                    bag.Add(new ScanHit { RelFile = rel, Kind = "error", PatternId = "extRx.Matches", Snippet = ex.GetType().Name + ": " + ex.Message });
                }
            }

            if (pyStemSet.Count > 0 && content.IndexOf("import", StringComparison.Ordinal) >= 0) {
                try {
                    foreach (Match pm in pyRx.Matches(content)) {
                        string stem = pm.Groups[1].Value;
                        if (pyStemSet.Contains(stem)) {
                            bag.Add(new ScanHit { RelFile = rel, Kind = "pyimport", Key = stem });
                        }
                    }
                } catch (Exception ex) {
                    bag.Add(new ScanHit { RelFile = rel, Kind = "error", PatternId = "pyRx.Matches", Snippet = ex.GetType().Name + ": " + ex.Message });
                }
            }

            if (!rel.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) && !rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) {
                if (content.IndexOf("scripts", StringComparison.OrdinalIgnoreCase) >= 0
                    || content.IndexOf("PSScriptRoot", StringComparison.OrdinalIgnoreCase) >= 0) {
                    for (int i = 0; i < dynRxs.Length; i++) {
                        try {
                            foreach (Match dm in dynRxs[i].Matches(content)) {
                                string snip = dm.Value;
                                var pfx = prefixRx.Match(snip);
                                if (pfx.Success) {
                                    bag.Add(new ScanHit {
                                        RelFile = rel,
                                        Kind = "dyn",
                                        PatternId = dynIds[i],
                                        Prefix = pfx.Groups[1].Value,
                                        Snippet = snip
                                    });
                                }
                            }
                        } catch (Exception ex) {
                            bag.Add(new ScanHit { RelFile = rel, Kind = "error", PatternId = "dynRxs[" + dynIds[i] + "].Matches", Snippet = ex.GetType().Name + ": " + ex.Message });
                        }
                    }
                }
            }
        });

        return new List<ScanHit>(bag);
    }
}
"@
    Add-Type -TypeDefinition $scannerCs -Language CSharp
}

$staticHitsMap = @{}
foreach ($dr in $tierCCandidates) {
    $staticHitsMap[$dr.path] = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
}

$dynamicLiteralPrefixes = New-Object System.Collections.Generic.List[object]
$scanErrors = New-Object System.Collections.Generic.List[object]
$dynIds = [string[]]@($dynamicPatterns | ForEach-Object { $_.id })
$dynRegexStrings = [string[]]@($dynamicPatterns | ForEach-Object { $_.regex })
$scanHits = [KongorCleanupCensusScannerV2]::ScanAll(
    $RepoRoot,
    [string[]]$searchFiles.ToArray(),
    $targetLeaves,
    $targetPyStems,
    $dynIds,
    $dynRegexStrings
)

foreach ($sh in $scanHits) {
    if ($sh.Kind -eq "leaf") {
        if ($leafToPaths.ContainsKey($sh.Key)) {
            foreach ($candPath in $leafToPaths[$sh.Key]) {
                if ($candPath -ne $sh.RelFile) {
                    [void]$staticHitsMap[$candPath].Add($sh.RelFile)
                }
            }
        }
    } elseif ($sh.Kind -eq "pyimport") {
        if ($pyStemToPaths.ContainsKey($sh.Key)) {
            foreach ($candPath in $pyStemToPaths[$sh.Key]) {
                if ($candPath -ne $sh.RelFile) {
                    [void]$staticHitsMap[$candPath].Add($sh.RelFile)
                }
            }
        }
    } elseif ($sh.Kind -eq "dyn") {
        $dynamicLiteralPrefixes.Add([PSCustomObject]@{
            file = $sh.RelFile
            patternId = $sh.PatternId
            prefix = $sh.Prefix
            snippet = $sh.Snippet
        })
    } elseif ($sh.Kind -eq "error") {
        Write-Warning "Caller scan error in '$($sh.RelFile)' [$($sh.PatternId)]: $($sh.Snippet)"
        $scanErrors.Add([PSCustomObject]@{
            file = $sh.RelFile
            stage = $sh.PatternId
            error = $sh.Snippet
        })
    }
}

Write-Host "  Dynamic interpolation prefix matches in active code: $($dynamicLiteralPrefixes.Count) | Scan errors: $($scanErrors.Count)" -ForegroundColor Gray

# 5. Build and emit NDJSON + Census JSON
Write-Host "[4/4] Writing cleanup-census.json and cleanup-dead-scripts.ndjson..." -ForegroundColor Yellow

$ndjsonLines = New-Object System.Collections.Generic.List[string]
$deadSummary = [ordered]@{
    totalTierC = $tierCCandidates.Count
    cleanDelete = 0
    deleteLocalIgnored = 0
    keepOrShim = 0
    reviewDocRef = 0
}
$checkedPatternIds = @($dynamicPatterns | ForEach-Object { $_.id })

foreach ($cand in ($tierCCandidates | Sort-Object path)) {
    $full = Join-Path $RepoRoot ($cand.path -replace '/', '\')
    $sha = Get-FileSha256Safe -FullPath $full
    $hitFiles = @($staticHitsMap[$cand.path] | Sort-Object)
    $activeHitFiles = @($hitFiles | Where-Object {
        -not $_.StartsWith("docs/", [System.StringComparison]::OrdinalIgnoreCase)
    })

    $dynHits = @($dynamicLiteralPrefixes | Where-Object {
        $cand.leaf.StartsWith($_.prefix, [System.StringComparison]::OrdinalIgnoreCase)
    })
    $dynRisk = ($dynHits.Count -gt 0)

    $action = "clean_delete"
    if ($cand.domain -eq "ignored_artifact") {
        $action = "delete_local_ignored"
        $deadSummary.deleteLocalIgnored++
    } elseif ($activeHitFiles.Count -gt 0 -or $dynRisk) {
        $action = "keep_or_shim"
        $deadSummary.keepOrShim++
    } elseif ($hitFiles.Count -gt 0) {
        $action = "review_doc_ref"
        $deadSummary.reviewDocRef++
    } else {
        $deadSummary.cleanDelete++
    }

    $row = [PSCustomObject]@{
        path = $cand.path
        stem = $cand.stem
        gitState = $cand.gitState
        sha256 = $sha
        staticHits = $hitFiles.Count
        staticHitFiles = $hitFiles
        activeCodeHits = $activeHitFiles.Count
        activeHitFiles = $activeHitFiles
        dynamicInterpolationChecked = $checkedPatternIds
        dynamicRisk = $dynRisk
        dynamicRiskDetails = @($dynHits | ForEach-Object { "$($_.file):$($_.patternId):$($_.prefix)" })
        recommendedAction = $action
        searchedFileCount = $searchFiles.Count
    }
    $ndjsonLines.Add(($row | ConvertTo-Json -Compress -Depth 4))
}

$ndjsonPath = Join-Path $OutDir "cleanup-dead-scripts.ndjson"
$ndjsonBody = if ($ndjsonLines.Count -gt 0) { ($ndjsonLines -join "`n") + "`n" } else { "" }
[System.IO.File]::WriteAllText($ndjsonPath, $ndjsonBody, $utf8NoBom)

$trackedCount = 0
$untrackedCount = 0
$ignoredCount = 0
foreach ($sr in $scriptRecords) {
    if ($sr.gitState -eq "tracked") { $trackedCount++ }
    elseif ($sr.gitState -eq "untracked") { $untrackedCount++ }
    elseif ($sr.gitState -eq "ignored") { $ignoredCount++ }
}
$scriptsByGitState = [ordered]@{
    tracked = $trackedCount
    untracked = $untrackedCount
    ignored = $ignoredCount
}

$summaryObj = [PSCustomObject]@{
    scriptsTotal = $scriptRecords.Count
    scriptsByGitState = $scriptsByGitState
    scriptsByTier = $tierCounts
    tierBByDomain = $tierBDomainCounts
    existingSubfolders = $existingSubfolderCounts
    deadScriptsActionSummary = $deadSummary
    toolsFileCount = ($toolsTracked.Length + $toolsUntracked.Length)
    srcToolsFileCount = ($srcToolsTracked.Length + $srcToolsUntracked.Length)
    rootEntryCount = $rootInventory.Count
    scanErrorCount = $scanErrors.Count
}

$sortedScripts = [object[]]($scriptRecords.ToArray() | Sort-Object path)
$toolsInv = [PSCustomObject]@{
    tracked = $toolsTracked
    untracked = $toolsUntracked
}
$srcToolsInv = [PSCustomObject]@{
    tracked = $srcToolsTracked
    untracked = $srcToolsUntracked
}
$inventoriesObj = [PSCustomObject]@{
    scripts = $sortedScripts
    tools = $toolsInv
    srcTools = $srcToolsInv
    root = $rootInventory.ToArray()
}

$censusObj = [PSCustomObject]@{
    schemaVersion = "1.0.0"
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    repoRoot = $RepoRoot
    searchedFileCount = $searchFiles.Count
    summary = $summaryObj
    unclassifiedScripts = $unclassifiedList.ToArray()
    dynamicInterpolationHits = $dynamicLiteralPrefixes.ToArray()
    scanErrors = $scanErrors.ToArray()
    inventories = $inventoriesObj
}

$censusPath = Join-Path $OutDir "cleanup-census.json"
[System.IO.File]::WriteAllText($censusPath, ($censusObj | ConvertTo-Json -Depth 6), $utf8NoBom)

$swTotal.Stop()
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " Census JSON  : $censusPath" -ForegroundColor Green
Write-Host " Dead NDJSON  : $ndjsonPath" -ForegroundColor Green
Write-Host " Dead Summary : clean_delete=$($deadSummary.cleanDelete), delete_local_ignored=$($deadSummary.deleteLocalIgnored), keep_or_shim=$($deadSummary.keepOrShim), review_doc_ref=$($deadSummary.reviewDocRef)" -ForegroundColor Gray
Write-Host " Scan Errors  : $($scanErrors.Count)" -ForegroundColor Gray
Write-Host " Elapsed      : $([math]::Round($swTotal.Elapsed.TotalSeconds, 2))s" -ForegroundColor Gray
Write-Host "================================================================================" -ForegroundColor Cyan

if ($scanErrors.Count -gt 0) {
    Write-Error "FAIL-CLOSED: $($scanErrors.Count) caller-scan error(s) occurred during census. Inspect scanErrors in '$censusPath' so no live script is misclassified."
    exit 3
}

if ($unclassifiedList.Count -gt 0 -and -not $AllowUnclassified) {
    Write-Error "FAIL-CLOSED: $($unclassifiedList.Count) UNCLASSIFIED file(s) detected under scripts/. Classify them in scripts/dev/audit_cleanup_census.ps1 or re-run with -AllowUnclassified."
    exit 2
}

exit 0

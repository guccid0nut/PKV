// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Services.Native;
using NEXUS.Contracts.Ipc;
using WILLOWMAKER.Core.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
///     MCP Tool Handler for 'kongor_native_manage' (alias 'kongor_native').
///     Provides programmatic governance, export validation, upgrade, and two-tier rollback for native game engine DLLs.
/// </summary>
public sealed partial class NativeManageHandler
{
    private static readonly string[] NativeGuardHookedSubsystems =
    [
        "HeapAlloc / HeapFree bounds check (real-time leak mitigation)",
        "WSASend / WSARecv packet frame bounds validation",
        "Vectored Exception Handler (STATUS_ACCESS_VIOLATION 0xC0000005 guard)",
        "Stack Overflow Handler (STATUS_STACK_OVERFLOW 0xC00000FD guard)"
    ];

    private readonly IGameIpcBridge _gameIpcBridge;
    private readonly INativeHookService _nativeHookService;
    private readonly Action<string> _logger;

    public NativeManageHandler(
        IGameIpcBridge? gameIpcBridge = null,
        Action<string>? logger = null,
        INativeHookService? nativeHookService = null)
    {
        _gameIpcBridge = gameIpcBridge ?? new GameIpcBridge();
        _logger = logger ?? (msg => Console.Error.WriteLine($"[NativeManageHandler] {msg}"));
        _nativeHookService = nativeHookService ?? new NativeHookService(_logger);
    }

    public NativeManageHandler(Action<string>? logger)
        : this(null, logger, null)
    {
    }

    public INativeHookService NativeHookService => _nativeHookService;

    public Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string action = "status";
        if (arguments.TryGetProperty("action", out JsonElement actElem) && !string.IsNullOrWhiteSpace(actElem.GetString()))
        {
            action = actElem.GetString()!.Trim().ToLowerInvariant();
        }

        string repoRoot = ResolveRepoRoot(arguments);
        _logger($"[HandleAsync] Action '{action}', RepoRoot: '{repoRoot}'");

        switch (action)
        {
            case "status":
                return Task.FromResult(HandleStatus(repoRoot));

            case "cef_status":
            case "cef-status":
            case "cef":
                return Task.FromResult(HandleCefStatus(repoRoot, arguments));

            case "cef_deactivate":
            case "cef-deactivate":
            case "deactivate_cef":
            case "deactivate-cef":
                return Task.FromResult(HandleCefDeactivate(repoRoot, arguments));

            case "cef_rollback":
            case "cef-rollback":
            case "rollback_cef":
            case "rollback-cef":
                return Task.FromResult(HandleCefRollback(repoRoot, arguments));

            case "verify":
                return Task.FromResult(HandleVerify(repoRoot, arguments));

            case "upgrade":
                return Task.FromResult(HandleUpgrade(repoRoot, arguments));

            case "rollback":
                return Task.FromResult(HandleRollback(repoRoot, arguments));

            case "patch_status":
            case "patch-status":
            case "patches":
                return Task.FromResult(HandlePatchStatus(repoRoot, arguments));

            case "patch_apply":
            case "patch-apply":
                return Task.FromResult(HandlePatchApply(repoRoot, arguments));

            case "patch_rollback":
            case "patch-rollback":
                return Task.FromResult(HandlePatchRollback(repoRoot, arguments));

            case "redirect_iat":
            case "redirect-iat":
            case "iat":
                return Task.FromResult(HandleRedirectIat(repoRoot, arguments));

            case "security_status":
            case "security-status":
            case "tls_status":
            case "tls-status":
                return Task.FromResult(HandleSecurityStatus(repoRoot, arguments));

            case "security_upgrade":
            case "security-upgrade":
            case "tls_upgrade":
            case "tls-upgrade":
                return Task.FromResult(HandleSecurityUpgrade(repoRoot, arguments));

            case "security_rollback":
            case "security-rollback":
            case "tls_rollback":
            case "tls-rollback":
                return Task.FromResult(HandleSecurityRollback(repoRoot, arguments));

            case "tls_verify":
            case "tls-verify":
            case "verify_handshake":
            case "verify-handshake":
                return HandleTlsVerifyAsync(arguments);

            case "audio_stub_status":
            case "audio-stub-status":
            case "audio_status":
                return Task.FromResult(HandleAudioStubStatus(repoRoot, arguments));

            case "audio_stub_deploy":
            case "audio-stub-deploy":
            case "deploy_audio_stub":
                return Task.FromResult(HandleAudioStubDeploy(repoRoot, arguments));

            case "audio_stub_rollback":
            case "audio-stub-rollback":
            case "rollback_audio_stub":
                return Task.FromResult(HandleAudioStubRollback(repoRoot, arguments));

            case "client_audio_status":
            case "client-audio-status":
            case "wasapi_status":
                return Task.FromResult(HandleClientAudioStatus(repoRoot, arguments));

            case "client_audio_deploy":
            case "client-audio-deploy":
            case "wasapi_deploy":
            case "wasapi_upgrade":
                return Task.FromResult(HandleClientAudioDeploy(repoRoot, arguments));

            case "client_audio_rollback":
            case "client-audio-rollback":
            case "wasapi_rollback":
                return Task.FromResult(HandleClientAudioRollback(repoRoot, arguments));

            case "client_audio_benchmark":
            case "client-audio-benchmark":
            case "wasapi_benchmark":
                return Task.FromResult(HandleClientAudioBenchmark(repoRoot, arguments));

            case "memory_status":
            case "memory-status":
            case "mimalloc_status":
                return Task.FromResult(HandleMemoryStatus(repoRoot, arguments));

            case "memory_deploy":
            case "memory-deploy":
            case "mimalloc_deploy":
            case "mimalloc_upgrade":
                return Task.FromResult(HandleMemoryDeploy(repoRoot, arguments));

            case "memory_rollback":
            case "memory-rollback":
            case "mimalloc_rollback":
                return Task.FromResult(HandleMemoryRollback(repoRoot, arguments));

            case "video_status":
            case "video-status":
                return Task.FromResult(HandleVideoStatus(repoRoot, arguments));

            case "video_clean_server":
            case "video-clean-server":
            case "video_clean":
            case "video-clean":
            case "clean_server_video":
                return Task.FromResult(HandleVideoCleanServer(repoRoot, arguments));

            case "video_restore_server":
            case "video-restore-server":
            case "restore_server_video":
                return Task.FromResult(HandleVideoRestoreServer(repoRoot, arguments));

            case "video_deploy":
            case "video-deploy":
            case "video_upgrade":
            case "video-upgrade":
                return Task.FromResult(HandleVideoDeploy(repoRoot, arguments));

            case "video_rollback":
            case "video-rollback":
                return Task.FromResult(HandleVideoRollback(repoRoot, arguments));

            case "vid_status":
            case "vid-status":
            case "headless_vid_status":
            case "headless-vid-status":
                return Task.FromResult(HandleVidStatus(repoRoot, arguments));

            case "vid_purge":
            case "vid-purge":
            case "headless_vid_purge":
            case "headless-vid-purge":
            case "vid_clean":
            case "vid-clean":
                return Task.FromResult(HandleVidPurge(repoRoot, arguments));

            case "vid_rollback":
            case "vid-rollback":
            case "headless_vid_rollback":
            case "headless-vid-rollback":
                return Task.FromResult(HandleVidRollback(repoRoot, arguments));

            case "guard_status":
            case "guard-status":
            case "guard":
                return Task.FromResult(HandleGuardStatus(repoRoot, arguments));

            case "guard_inject":
            case "guard-inject":
            case "inject":
                return Task.FromResult(HandleGuardInject(repoRoot, arguments));

            case "guard_telemetry":
            case "guard-telemetry":
                return Task.FromResult(HandleGuardTelemetry(repoRoot, arguments));

            case "guard_loading":
            case "guard-loading":
            case "loading_metrics":
            case "vfs_metrics":
            case "loading":
                return Task.FromResult(HandleGuardLoadingMetrics(repoRoot, arguments));

            case "test_trampoline":
            case "test-trampoline":
            case "trampoline":
                return Task.FromResult(HandleTestTrampoline());

            case "hot_patch":
            case "hot-patch":
            case "live_heal":
                return Task.FromResult(HandleHotPatch(repoRoot, arguments));

            case "vfs_status":
            case "vfs-status":
            case "archive_status":
                return Task.FromResult(HandleVfsStatus(repoRoot, arguments));

            case "vfs_benchmark":
            case "vfs-benchmark":
            case "archive_benchmark":
                return Task.FromResult(HandleVfsBenchmark(repoRoot, arguments));

            case "vfs_inspect":
            case "vfs-inspect":
            case "archive_inspect":
                return Task.FromResult(HandleVfsInspect(repoRoot, arguments));

            case "vfs_upgrade":
            case "vfs-upgrade":
            case "archive_upgrade":
                return Task.FromResult(HandleVfsUpgrade(repoRoot, arguments));

            case "vfs_rollback":
            case "vfs-rollback":
            case "archive_rollback":
                return Task.FromResult(HandleVfsRollback(repoRoot, arguments));

            case "subtick_input":
            case "subtick-input":
            case "subtick":
                return HandleSubTickInputAsync(arguments, cancellationToken);

            case "telemetry_feed":
            case "telemetry-feed":
            case "telemetry_update":
            case "telemetry-update":
                return HandleTelemetryFeedAsync(arguments, cancellationToken);

            case "ips_patch":
            case "ips32_patch":
            case "ips_encode":
            case "ips_decode":
                return Task.FromResult(HandleIpsPatch(arguments));

            case "decode_fastdiv":
            case "synthesize_fastdiv":
            case "fastdiv":
                return Task.FromResult(HandleDecodeFastDiv(arguments));

            case "audit_detour_cfg":
            case "disasm_cfg_lanes":
                return Task.FromResult(HandleAuditDetourCfg(arguments));

            case "transform_dag":
            case "binary_dag":
                return Task.FromResult(HandleTransformDag(arguments));

            case "verify_closure":
            case "verify-closure":
            case "closure_digest":
                return Task.FromResult(HandleVerifyClosure(arguments));

            case "correlate_static_runtime":
            case "correlate-static-runtime":
            case "correlation_matrix":
                return Task.FromResult(HandleCorrelateStaticRuntime(arguments));

            case "high_pcode_graph":
            case "high-pcode-graph":
            case "pcode_def_use":
                return Task.FromResult(HandleHighPCodeGraph(arguments));

            case "investigation_dag":
            case "investigation-dag":
            case "epistemic_dag":
                return Task.FromResult(HandleInvestigationDag(arguments));

            case "compare_equivalence":
            case "compare-equivalence":
            case "cleanroom_equivalence":
                return Task.FromResult(HandleCompareEquivalence8Facet(arguments));

            case "inspect_pe_resources":
            case "inspect-pe-resources":
            case "pe_resources":
                return Task.FromResult(HandleInspectPeResources(arguments));

            case "decode_coff_r2r":
            case "decode-coff-r2r":
            case "coff_r2r":
                return Task.FromResult(HandleDecodeCoffR2R(arguments));

            case "coordinate_convert":
            case "coordinate-convert":
            case "address_coordinate":
                return Task.FromResult(HandleCoordinateConvert(arguments));

            case "audit_smc_writes":
            case "audit-smc-writes":
            case "smc_audit":
                return Task.FromResult(HandleAuditSmcWrites(arguments));

            case "detect_recursion_cycles":
            case "detect-recursion-cycles":
            case "tarjan_scc":
                return Task.FromResult(HandleDetectRecursionCycles(arguments));

            case "ida_manage":
            case "ida-manage":
            case "ida_bridge":
            {
                IdaManageHandler idaHandler = new IdaManageHandler(logger: _logger);
                return idaHandler.HandleAsync(arguments, cancellationToken);
            }

            default:
                return Task.FromResult(ToolCallResult.Error($"Unknown action '{action}'. Valid actions: status, verify, upgrade, rollback, patch_status, patch_apply, patch_rollback, redirect_iat, security_status, security_upgrade, security_rollback, tls_verify, audio_stub_status, audio_stub_deploy, audio_stub_rollback, vfs_status, vfs_benchmark, vfs_inspect, vfs_upgrade, vfs_rollback, guard_status, guard_inject, guard_telemetry, subtick_input, telemetry_feed, test_trampoline, hot_patch, ips_patch, decode_fastdiv, audit_detour_cfg, transform_dag, verify_closure, correlate_static_runtime, high_pcode_graph, investigation_dag, compare_equivalence, inspect_pe_resources, decode_coff_r2r, coordinate_convert, audit_smc_writes, detect_recursion_cycles, ida_manage."));
        }
    }

    private async Task<ToolCallResult> HandleSubTickInputAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string actionCmd = "Order move 0 0";
        if (arguments.TryGetProperty("command", out JsonElement cmdEl) && !string.IsNullOrWhiteSpace(cmdEl.GetString()))
        {
            actionCmd = cmdEl.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("actionCommand", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            actionCmd = actEl.GetString()!.Trim();
        }

        uint sequence = 0;
        if (arguments.TryGetProperty("sequence", out JsonElement seqEl) && seqEl.TryGetUInt32(out uint sVal))
        {
            sequence = sVal;
        }

        long timestampQpc = 0;
        if (arguments.TryGetProperty("timestampQpc", out JsonElement qpcEl) && qpcEl.TryGetInt64(out long qVal))
        {
            timestampQpc = qVal;
        }

        SubTickInputResult result = await _gameIpcBridge.InjectSubTickInputAsync(actionCmd, timestampQpc, sequence, 3000, cancellationToken).ConfigureAwait(false);

        return ToolCallResult.Success(new
        {
            success = result.Success,
            action = "subtick_input",
            inputSequence = result.InputSequence,
            measuredLatencyMs = result.MeasuredLatencyMs,
            timestampQpc = result.TimestampQpc,
            message = result.Message ?? "Sub-tick input dispatched."
        });
    }

    private async Task<ToolCallResult> HandleTelemetryFeedAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        uint drawCalls = 180;
        if (arguments.TryGetProperty("drawCalls", out JsonElement dcEl) && dcEl.TryGetUInt32(out uint dcVal))
            drawCalls = dcVal;

        uint activeEntities = 110;
        if (arguments.TryGetProperty("activeEntities", out JsonElement aeEl) && aeEl.TryGetUInt32(out uint aeVal))
            activeEntities = aeVal;

        uint particlesAlive = 45;
        if (arguments.TryGetProperty("particlesAlive", out JsonElement paEl) && paEl.TryGetUInt32(out uint paVal))
            particlesAlive = paVal;

        uint pingMicroseconds = 12000;
        if (arguments.TryGetProperty("pingMicroseconds", out JsonElement pingEl) && pingEl.TryGetUInt32(out uint pingVal))
            pingMicroseconds = pingVal;

        uint serverTick = 1000;
        if (arguments.TryGetProperty("serverTick", out JsonElement tickEl) && tickEl.TryGetUInt32(out uint tickVal))
            serverTick = tickVal;

        float inputLatencyMs = 0f;
        if (arguments.TryGetProperty("inputLatencyMs", out JsonElement latEl) && latEl.TryGetSingle(out float latVal))
            inputLatencyMs = latVal;

        bool updated = await _gameIpcBridge.PushTelemetryUpdateAsync(drawCalls, activeEntities, particlesAlive, pingMicroseconds, serverTick, inputLatencyMs, 2000, cancellationToken).ConfigureAwait(false);

        return ToolCallResult.Success(new
        {
            success = updated,
            action = "telemetry_feed",
            drawCalls,
            activeEntities,
            particlesAlive,
            pingMicroseconds,
            serverTick,
            inputLatencyMs,
            telemetryUpdated = updated,
            message = updated ? "Dynamic telemetry updated successfully." : "Failed to update dynamic telemetry (no connected native client)."
        });
    }

}

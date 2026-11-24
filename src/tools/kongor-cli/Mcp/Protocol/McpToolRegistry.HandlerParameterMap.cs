// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

namespace Kongor.Cli.Mcp.Protocol;

public sealed partial class McpToolRegistry
{
    /// <summary>
    /// Per-tool allowlist of handler-accepted sub-action / convenience argument names verified by
    /// HandlerSchemaPropertyRatchetTests (MCP-293). Keeps tools/list schemas terse (< 8 KB budget)
    /// while ensuring strict unknown_argument rejection per tool with zero false rejections.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AcceptedHandlerKeysByTool =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["kongor_app_eval"] = Set("code", "expression"),
            ["kongor_assert_ui"] = Set("contact_sheet_path", "contactSheetPath", "target", "value", "viewportHeight", "viewportWidth", "mode"),
            ["kongor_assert_visual"] = Set("candidate", "candidate_fixture", "candidate_path", "enforce_thresholds", "enforceThresholds", "golden", "ignore_viewport", "mask", "max_diff_percentage", "maxDiffPercentage", "min_ssim", "mode", "strict", "tolerance", "validate_threshold", "validateThreshold"),
            ["kongor_asset_bake_pipeline"] = Set("border_tolerance", "borderTolerance", "cutout_solid_border", "cutoutSolidBorder", "destination", "file", "height", "input_file", "input_path", "inputPath", "invert_y", "name", "output_path", "outputPath", "samplers", "shader", "synthetic_test_icon", "syntheticTestIcon", "target", "target_height", "target_width", "targetHeight", "targetWidth", "vfs_path", "vfsPath", "width", "mode"),
            ["kongor_audio_preview"] = Set("duration", "duration_seconds", "event", "event_name", "eventName", "path", "playback", "serverPort", "sound_dir", "mode"),
            ["kongor_audio_studio"] = Set("falloff", "file", "include_tree", "includeTargetProcessTree", "integrated_loudness", "name", "output_directory", "output_file", "outputFile", "pid", "sample_file", "sound_path", "target_pid", "targetProcessId", "text", "type", "vfs_path", "vfsPath", "volume", "x", "y", "z", "mode"),
            ["kongor_audit_stats"] = Set("player", "recordBacklog"),
            ["kongor_bot_choreography"] = Set("map", "mode"),
            ["kongor_cache_coherence_verify"] = Set("field", "id", "key", "name", "pattern", "query", "target", "mode"),
            ["kongor_call_tool"] = Set("args", "args_json", "arguments", "category", "domain", "filter", "name", "search", "tool", "mode"),
            ["kongor_camera_control"] = Set("autoLaunch", "forceOfflineCheck", "slot", "target", "mode"),
            ["kongor_capture_window"] = Set("allow_fallback", "allowFallback", "dwell_ms", "dwellMs", "height", "offscreen_x", "offscreen_y", "offscreenX", "offscreenY", "overlay_label", "overlay_widget", "overlayLabel", "overlayWidget", "pid", "process_id", "processId", "strict_method", "strictMethod", "transient", "width"),
            ["kongor_ci_trigger"] = Set("tail_lines", "tailLines"),
            ["kongor_client_capture_preview"] = Set("avatar", "hero_key", "mode"),
            ["kongor_client_eval_lua"] = Set("channel", "heroName", "lua", "name", "script", "skip_syntax_check", "skipSyntaxCheck", "wait_for_engine", "waitForEngine", "wait_for_ipc_ms", "waitForIpcMs", "mode"),
            ["kongor_client_navigate"] = Set("accountName", "mode", "pass", "player", "route", "screen", "slot", "target", "username"),
            ["kongor_client_practice"] = Set("botCount", "expectedPhase", "fakePlayers", "fps", "heroId", "heroLevel", "heroName", "mapName", "nickname", "pass", "pid", "process_id", "processId", "slot", "slotIndex", "username", "warmReuse", "mode"),
            ["kongor_compel_manage"] = Set("mode"),
            ["kongor_crash_patch"] = Set("address", "module", "patched_bytes_hex", "patchedBytesHex", "path", "rva", "mode"),
            ["kongor_crash_triage"] = Set("offset", "target_module", "targetModule", "mode"),
            ["kongor_db_fixture"] = Set("connection_string", "connectionString", "mode"),
            ["kongor_db_migrate"] = Set("connectionString", "migration", "migration_name", "target_migration", "mode"),
            ["kongor_db_migration_test"] = Set("connection_string", "connectionString", "mode"),
            ["kongor_db_pool_diagnostics"] = Set("probe_remote_db", "probeRemoteDb", "mode"),
            ["kongor_deploy_canary"] = Set("docker_image", "image_tag", "mode"),
            ["kongor_diagnostics"] = Set("ai_diagnose", "enable_sampling", "mode"),
            ["kongor_enable_toolset"] = Set("domains", "toolset"),
            ["kongor_engine_cache_purge"] = Set("mode"),
            ["kongor_engine_inspect"] = Set("binary", "dll", "file", "filter", "limit", "name", "symbol", "mode"),
            ["kongor_errors_since"] = Set("allowDiagnosticWarnings", "includeBaselinedNoise", "lookbackSeconds", "reason", "mode"),
            ["kongor_fixture_inject"] = Set("coords", "hp", "mana", "scenario", "mode"),
            ["kongor_game_action"] = Set("account", "map", "matchId", "sequence", "target_entity", "target_name", "targetName", "timestampQpc", "username", "mode"),
            ["kongor_game_client_manage"] = Set("account", "accountName", "all", "allow_keystroke_fallback", "allowKeystrokeFallback", "auto_login", "autoLogin", "code", "context", "customNickname", "enabled", "enable_replay_capture", "enableReplayCapture", "enforce_frame_pacing", "enforceFramePacing", "exitCode", "headless", "launch_profile", "launchProfile", "limit", "maxLines", "nickname", "pass", "password", "pid", "playerNickname", "process_id", "processId", "slot", "slot_index", "slotIndex", "timeoutMs", "trigger", "use_launcher_settings", "useLauncherSettings", "username", "workingDirectory", "mode"),
            ["kongor_game_command"] = Set("allow_keystroke_fallback", "allowKeystrokeFallback", "autoLaunch", "count", "factor", "limit", "map", "matchId", "pid", "process_id", "processId", "step", "ticks", "timescale", "value", "mode"),
            ["kongor_game_lua"] = Set("account", "allow_keystroke_fallback", "allowKeystrokeFallback", "assert_state", "assert_widget", "assertState", "assertWidget", "count", "data", "factor", "fixture", "frames", "gold", "include_hidden", "includeHidden", "inventory", "items", "level", "lua", "matchId", "max_depth", "params", "payload", "pid", "player", "pos", "process_id", "processId", "productId", "scale", "script", "step", "ticks", "timescale", "trigger_name", "value", "visible_only", "visibleOnly", "visual_proof", "visualProof", "widget", "widget_event", "widgetEvent", "mode"),
            ["kongor_game_manage"] = Set("headless", "window_mode", "windowMode", "mode"),
            ["kongor_game_server_command"] = Set("autoLaunch", "channel", "map", "matchId", "target", "mode"),
            ["kongor_game_server_manage"] = Set("channel", "maxLines", "workingDirectory", "mode"),
            ["kongor_game_snapshot"] = Set("cooldowns", "currentPhase", "destination", "formatted", "frame", "gold", "hero", "inventory", "items", "level", "milliseconds", "ms", "phase", "player", "pos", "seconds", "x", "y", "z", "mode"),
            ["kongor_game_step"] = Set("step_count", "stepCount", "time_scale", "timescale", "mode"),
            ["kongor_game_telemetry"] = Set("activeEntities", "count", "drawCalls", "inputLatencyMs", "map", "particlesAlive", "pingMicroseconds", "serverTick", "mode"),
            ["kongor_get_client_logs"] = Set("channel_filter", "channelFilter", "count", "filter", "level", "lines", "max_lines", "maxLines", "min_severity", "minSeverity", "query", "search", "severity", "tail_lines", "tailLines", "text_filter", "textFilter", "mode"),
            ["kongor_health_check"] = Set("mode"),
            ["kongor_hot_reload"] = Set("debounce", "path", "watched_paths", "watchedPaths", "mode"),
            ["kongor_inspect_hero_pool"] = Set("mode"),
            ["kongor_inspect_model_mdf"] = Set("alt_name", "archive_sample_path", "hero_name", "max_models", "mdf_path", "mdfPath", "scan_archive_models", "mode"),
            ["kongor_k2_lint"] = Set("file", "severity", "mode"),
            ["kongor_k2_ui_inspect"] = Set("boxes", "element", "interface", "interface_name", "interfaceName", "layout_boxes", "layoutBoxes", "package", "target", "viewportHeight", "viewportWidth", "mode"),
            ["kongor_log_correlate"] = Set("window_minutes", "mode"),
            ["kongor_match_analytics"] = Set("count", "directory", "file_path", "max_rows", "path", "player_index", "playerIndex", "query", "slot", "mode"),
            ["kongor_match_launch"] = Set("use_launcher_settings", "useLauncherSettings", "mode"),
            ["kongor_match_state"] = Set("account", "allowLocalFallback", "fallbackToClient", "timeoutMs", "username", "mode"),
            ["kongor_material_inspect"] = Set("material_path", "materialPath", "mode"),
            ["kongor_memory_leak_detect"] = Set("pid", "process_id", "processId", "mode"),
            ["kongor_mod_build"] = Set("action", "mod_path", "output_path", "outputPath", "reload", "source_dir", "sourceDir", "mode"),
            ["kongor_native_manage"] = Set("actionCommand", "activeEntities", "binary_path", "bit_width", "bytes_hex", "call_graph", "cleanroom_pseudocode", "coordinate", "declared_total_bytes", "detour_length", "detour_start", "divisor", "dossier", "drawCalls", "edges", "function_address", "imageBase", "input_hex", "inputLatencyMs", "instructions", "ips32", "max_depth_clamp", "max_operations", "multiplier", "nodes", "offset", "operations", "original_pseudocode", "particlesAlive", "patch_hex", "path", "pingMicroseconds", "post_shift", "pre_shift", "raw_json", "raw_machine", "repo_root", "repoRoot", "rsrc_hex", "runtime_after", "runtime_before", "runtime_truncated", "section_end", "section_start", "sections", "sequence", "serverTick", "static_after", "static_before", "static_truncated", "subject_id", "targetSpace", "timestampQpc", "url", "uses_overflow_add", "xor_key_hex", "mode"),
            ["kongor_net_packet_fuzz"] = Set("hex", "strategy", "mode"),
            ["kongor_network_chaos"] = Set("asymmetric", "nodeCount", "targetType", "mode"),
            ["kongor_network_impair_simulate"] = Set("mode"),
            ["kongor_perf_window"] = Set("expectedVkLayerLoaded", "expected_vk_layer_loaded", "includeModuleAudit", "include_module_audit", "panel", "processId", "process_id", "targetAction", "mode"),
            ["kongor_realtime_stream"] = Set("maxEvents", "streamType", "mode"),
            ["kongor_reload_and_verify"] = Set("allowDiagnosticWarnings", "baselineState", "canary_settle_ms", "canarySettleMs", "candidateState", "capture_screenshot", "captureScreenshot", "evidenceId", "files", "path", "reason", "screenshotPath", "mode"),
            ["kongor_replay_diff"] = Set("client_hex", "client_path", "client_tick", "clientTick", "desync", "desync_tick", "desyncTick", "file_a", "file_b", "frame", "hex", "hex_a", "hex_b", "include_unchanged", "injected_desync_tick", "packet_hex", "path_a", "path_b", "replay_a", "replay_b", "server_hex", "server_path", "server_tick", "serverPort", "serverTick", "window_size", "mode"),
            ["kongor_replay_inspect"] = Set("directory", "name", "searchPath", "serverPort", "targetHero", "type", "mode"),
            ["kongor_replay_playback"] = Set("delta", "delta_ticks", "deltaTicks", "file", "fog_of_war", "fogOfWar", "host", "name", "path", "playback_speed", "playbackSpeed", "replay_path", "replayPath", "serverPort", "speedStep", "targetHero", "targetType", "tick", "timeMs", "mode"),
            ["kongor_scrimmage_launch"] = Set("limit", "simulateServerFailure", "mode"),
            ["kongor_self_heal"] = Set("description", "recipe", "severity", "source", "tab", "mode"),
            ["kongor_server_manage"] = Set("lobbyId", "maxLines", "port", "mode"),
            ["kongor_server_perf_stream"] = Set("target_port", "mode"),
            ["kongor_state_snapshot"] = Set("mode"),
            ["kongor_sync_mirrors"] = Set("mode"),
            ["kongor_tao_capabilities"] = Set("timeout", "mode"),
            ["kongor_telemetry_stream"] = Set("mode"),
            ["kongor_texture_diff"] = Set("baseline_path", "baselinePath", "candidate_path", "candidatePath", "threshold", "mode"),
            ["kongor_texture_in_game_inspect"] = Set("preset", "mode"),
            ["kongor_texture_preview"] = Set("reload", "mode"),
            ["kongor_transient_capture"] = Set("mode", "method", "pid", "process_id", "processId", "path", "savePath", "outputPath", "customOutputPath"),
            ["kongor_ui_control"] = Set("event_index", "event_type", "eventIndex", "eventType", "index", "key_name", "keyName", "mode"),
            ["kongor_ui_flow"] = Set("account", "allow_keystroke_fallback", "allowKeystrokeFallback", "assert_widget", "assertWidget", "bundlePath", "category", "checkpoint", "clickFidelity", "click_fidelity", "fidelity", "gameName", "hero", "interface", "limit", "map", "matrix_path", "matrixPath", "name", "overlay", "overlay_widget", "overlayWidget", "panels", "player", "productId", "replay_bundle_path", "replayBundlePath", "repro_output_path", "reproOutputPath", "seed", "show_overlay", "showOverlay", "steps", "type", "visual_proof", "visualProof", "watchTab", "widget", "mode"),
            ["kongor_ui_observer"] = Set("target", "timeout", "mode"),
            ["kongor_ui_visual_diff"] = Set("mask_clock_fps", "mask_dynamic", "mask_regions", "maskClockFps", "maskDynamic", "maskRegions", "masks", "t", "timestamp", "ToleranceThreshold", "mode"),
            ["kongor_validate_previews"] = Set("hero_filter", "heroFilter", "max_issues", "mode"),
            ["kongor_vfs_completeness_audit"] = Set("domain", "max_issues", "path", "mode"),
            ["kongor_vfs_locate"] = Set("filename", "query", "mode"),
            ["kongor_vfs_mount_graph"] = Set("live_scan", "liveScan", "mode"),
            ["kongor_vfs_resolve"] = Set("filter", "query", "mode"),
            ["kongor_vulkan"] = Set("captureAction", "faultAddresses", "frameNumber", "outputPath", "registerBuffer", "repoRoot", "slotIndex", "mode"),
            ["kongor_wait_for"] = Set("expectedText", "expression", "poll_interval_ms", "pollIntervalMs", "strict_timeout", "strictTimeout", "timeout", "type", "widget", "mode"),
            ["kongor_willowmaker_manage"] = Set("mode")
        };

    private static HashSet<string> Set(params string[] items) =>
        new(items, StringComparer.OrdinalIgnoreCase);

    internal static bool IsAcceptedByHandlerParameterMap(string toolName, string key)
    {
        if (!AcceptedHandlerKeysByTool.TryGetValue(toolName, out IReadOnlySet<string>? keys))
        {
            return false;
        }

        if (keys.Contains(key)) return true;
        if (keys.Contains(ToCamelCaseKey(key))) return true;
        if (keys.Contains(ToSnakeCaseKey(key))) return true;
        return false;
    }
}

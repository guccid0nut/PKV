// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PerfWindowHandler.cs
// System: Kongor MCP Server - Frame-Accurate Performance Window & Lua Attribution
// Purpose: Implements 'kongor_perf_window' to measure a specific interaction window
//          via vid_vulkan's per-rendered-frame FastBus (Local\KongorTelemetryBuffer)
//          with 100ms lossless draining, true QPC frame-delta percentiles, High-Hz
//          hitch buckets, Lua UI transition correlation (<= 1.0ms budget), and
//          native loaded-module runtime drift auditing.
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Telemetry;
using NEXUS.Contracts.Telemetry;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// State tracking record for an active performance measurement window.
/// </summary>
public sealed class PerfWindowState
{
    public required string WindowId { get; init; }
    public required int ProcessId { get; init; }
    public required ulong StartFrameIndex { get; init; }
    public required long StartTimestampQpc { get; init; }
    public required DateTimeOffset StartTimeUtc { get; init; }
    public required ulong StartLuaFrame { get; init; }
    public required double StartLuaTimestamp { get; init; }
    public string TelemetrySource { get; init; } = "unknown";
    public bool IsRenderFrameAccurate { get; init; }
    public string TelemetryMapName { get; init; } = string.Empty;
    public double HitchThresholdMs { get; init; } = 30.0;
    public double MajorHitchThresholdMs { get; init; } = 50.0;
    public double BudgetThresholdMs { get; init; } = 1.0;
}

/// <summary>
/// Model representing a recorded UI transition from TAO in Lua.
/// </summary>
public sealed class LuaTransitionRecord
{
    public ulong Frame { get; set; }
    public double Timestamp { get; set; }
    public double HostTime { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Action { get; set; } = "";
    public double DurationMs { get; set; }
    public bool BudgetExceeded { get; set; }
    public JsonElement? Extra { get; set; }
}

/// <summary>
/// Model representing a detected frame hitch correlated with a UI transition.
/// </summary>
public sealed class CorrelatedHitchRecord
{
    public ulong FrameIndex { get; set; }
    public double TotalFrameTimeMs { get; set; }
    public float CpuFrameTimeMs { get; set; }
    public float GpuFrameTimeMs { get; set; }
    public uint ReservedFlags { get; set; }
    public bool IsMajorHitch { get; set; }
    public LuaTransitionRecord? AttributedTransition { get; set; }
}

/// <summary>
/// Key loaded module entry with on-disk SHA-256 digest for runtime drift verification.
/// </summary>
public sealed class RuntimeModuleDigest
{
    public required string ModuleName { get; init; }
    public required string FilePath { get; init; }
    public required string Sha256 { get; init; }
}

/// <summary>
/// Result of a live loaded-module audit against hon_x64.exe.
/// </summary>
public sealed class RuntimeModuleAuditResult
{
    public required int ProcessId { get; init; }
    public required bool ProcessAlive { get; init; }
    public required int TotalLoadedModules { get; init; }
    public required bool VidVulkanLoaded { get; init; }
    public required bool D3d9Loaded { get; init; }
    public required bool VersionDllLoaded { get; init; }
    public required bool VkLayerReplayCaptureLoaded { get; init; }
    public bool? ExpectedVkLayerReplayCaptureLoaded { get; init; }
    public required List<string> DetectedDeniedHooks { get; init; }
    public required List<RuntimeModuleDigest> KeyModules { get; init; }
    public required bool Passed { get; init; }
    public required List<string> Findings { get; init; }
    public required string AuditedAtUtc { get; init; }
}

/// <summary>
/// MCP tool handler for 'kongor_perf_window' (aliases: 'perf_window', 'kongor_perf').
/// Dual-plane measurement:
/// 1. Fast-Bus Native Data Plane: Prefers vid_vulkan_x64.dll's per-rendered-frame
///    Local\KongorTelemetryBuffer with 100ms lossless draining and true QPC deltas;
///    only falls back to version.dll's 20Hz guard ring when vid_vulkan is absent.
/// 2. Control Plane: Queries event-driven Lua UI transition ring buffer in tao.lua.
/// Also provides live loaded-module auditing ('audit_runtime' / 'includeModuleAudit').
/// </summary>
public sealed class PerfWindowHandler : IDisposable
{
    private const int MaxCollectedRecords = 65536;
    private const int MaxDetailedHitchesReturned = 200;
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaxAutoDrainDuration = TimeSpan.FromMinutes(10);

    private static readonly string[] DeniedOverlayHookModules =
    [
        "RTSSHooks64.dll",
        "DiscordHook64.dll",
        "graphics-hook64.dll",
        "obs-vulkan64.dll",
        "SteamOverlayVulkanLayer64.dll",
        "SteamFossilizeVulkanLayer64.dll",
        "renderdoc.dll",
        "MediaSDKVulkanLayer64.dll",
        "MedalHook64.dll"
    ];

    private static readonly HashSet<string> KeyAuditModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "vid_vulkan_x64.dll",
        "d3d9.dll",
        "version.dll",
        "VkLayer_hon_replay_capture.dll"
    };

    private sealed class ActiveDrainSession : IDisposable
    {
        private readonly MemoryMappedTelemetryBuffer _buffer;
        private readonly FrameTelemetryRecord[] _scratch;
        private readonly object _recordsLock = new();
        private readonly List<FrameTelemetryRecord> _records = new(4096);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _drainTask;
        private readonly Action<string>? _logger;
        private ulong _lastSeenFrameIndex;
        private ulong _lastSeenHead;
        private ulong _droppedRingFrames;
        private bool _disposed;

        public string TelemetrySource { get; }
        public bool IsRenderFrameAccurate { get; }
        public string MapName { get; }

        public ActiveDrainSession(
            MemoryMappedTelemetryBuffer buffer,
            ulong startFrameIndex,
            string telemetrySource,
            bool isRenderFrameAccurate,
            string mapName,
            Action<string>? logger)
        {
            _buffer = buffer;
            int cap = (int)Math.Clamp(buffer.Capacity, 256U, 8192U);
            _scratch = new FrameTelemetryRecord[cap];
            _lastSeenFrameIndex = startFrameIndex;
            _lastSeenHead = buffer.Head;
            TelemetrySource = telemetrySource;
            IsRenderFrameAccurate = isRenderFrameAccurate;
            MapName = mapName;
            _logger = logger;
            _cts.CancelAfter(MaxAutoDrainDuration);
            CancellationToken loopToken = _cts.Token;
            _drainTask = Task.Run(() => DrainLoopAsync(loopToken));
        }

        private async Task DrainLoopAsync(CancellationToken ct)
        {
            using PeriodicTimer timer = new(DrainPollInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    DrainOnce();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on window stop or safety timeout.
            }
            catch (ObjectDisposedException)
            {
                // Expected if session was disposed concurrently.
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[PerfWindowHandler] Background FastBus drain loop ended with error: {ex.Message}");
            }
            finally
            {
                _cts.Dispose();
            }
        }

        public void DrainOnce()
        {
            lock (_recordsLock)
            {
                if (_disposed)
                {
                    return;
                }

                // Read only the newly produced delta since _lastSeenHead, staying at least 64 slots
                // clear of the producer's live write slot (head & mask) to avoid lock-free overwrite races.
                int capacity = (int)Math.Clamp(_buffer.Capacity, 256U, 8192U);
                int maxSafeSlice = Math.Max(1, Math.Min(_scratch.Length, capacity - 64));

                int readCount = 0;
                ulong capturedHead = 0;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    ulong headBefore = _buffer.Head;
                    if (headBefore <= _lastSeenHead)
                    {
                        return;
                    }

                    ulong delta = headBefore - _lastSeenHead;
                    int toRead = (int)Math.Min(delta + 64UL, (ulong)maxSafeSlice);
                    readCount = _buffer.ReadRecent(_scratch.AsSpan(0, toRead));
                    ulong headAfter = _buffer.Head;
                    // Never advance _lastSeenHead past headBefore: frames written between ReadRecent() and
                    // reading headAfter might not be inside _scratch yet and must be picked up on the next drain.
                    capturedHead = headBefore;

                    // If producer burst-advanced past our slice window between headBefore and ReadRecent, retry with the larger delta
                    if (headAfter - _lastSeenHead > (ulong)toRead && toRead < maxSafeSlice)
                    {
                        continue;
                    }

                    // Verify producer did not wrap over the copied slice during ReadRecent
                    ulong oldestReadHead = headBefore > (ulong)readCount ? (headBefore - (ulong)readCount) : 0UL;
                    if (headAfter - oldestReadHead < (ulong)capacity)
                    {
                        break;
                    }
                }

                _lastSeenHead = capturedHead;

                for (int i = 0; i < readCount; i++)
                {
                    FrameTelemetryRecord rec = _scratch[i];
                    if (rec.FrameIndex == 0 || rec.FrameIndex <= _lastSeenFrameIndex)
                    {
                        continue;
                    }

                    if (_lastSeenFrameIndex > 0 && rec.FrameIndex > _lastSeenFrameIndex + 1)
                    {
                        _droppedRingFrames += rec.FrameIndex - _lastSeenFrameIndex - 1;
                    }

                    if (_records.Count < MaxCollectedRecords)
                    {
                        _records.Add(rec);
                    }

                    _lastSeenFrameIndex = rec.FrameIndex;
                }
            }
        }

        public async Task<(List<FrameTelemetryRecord> Records, ulong DroppedRingFrames)> StopAndCollectAsync()
        {
            try
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }

            try
            {
                await _drainTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected cancellation.
            }

            DrainOnce();

            lock (_recordsLock)
            {
                return (new List<FrameTelemetryRecord>(_records), _droppedRingFrames);
            }
        }

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by DrainLoopAsync finally block.
            }

            lock (_recordsLock)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _buffer.Dispose();
            }
        }
    }

    private readonly ITaoClient? _taoClient;
    private readonly GameClientManageHandler? _clientManageHandler;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly Func<int?, MemoryMappedTelemetryBuffer?>? _customBufferFactory;
    private readonly Func<ulong, List<FrameTelemetryRecord>>? _testRecordsProvider;
    private readonly Func<string, MemoryMappedTelemetryBuffer?>? _mapOpenerOverride;
    private readonly Func<int, IReadOnlyList<(string ModuleName, string FilePath)>?>? _moduleEnumeratorOverride;

    private static readonly string StateFilePath = Path.Combine(Path.GetTempPath(), "kongor_perf_window_active.json");

    private readonly object _syncLock = new();
    private PerfWindowState? _activeWindow;
    private ActiveDrainSession? _activeDrainSession;

    public PerfWindowState? ActiveWindow
    {
        get
        {
            lock (_syncLock)
            {
                if (_activeWindow != null) return _activeWindow;
                if (File.Exists(StateFilePath))
                {
                    try
                    {
                        string json = File.ReadAllText(StateFilePath);
                        return JsonSerializer.Deserialize<PerfWindowState>(json);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Invoke($"[PerfWindowHandler] Failed reading persisted state file '{StateFilePath}': {ex.Message}");
                    }
                }
                return null;
            }
        }
    }

    public PerfWindowHandler(
        ITaoClient? taoClient = null,
        GameClientManageHandler? clientManageHandler = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        Func<int?, MemoryMappedTelemetryBuffer?>? customBufferFactory = null,
        Func<ulong, List<FrameTelemetryRecord>>? testRecordsProvider = null,
        Func<string, MemoryMappedTelemetryBuffer?>? mapOpenerOverride = null,
        Func<int, IReadOnlyList<(string ModuleName, string FilePath)>?>? moduleEnumeratorOverride = null)
    {
        _taoClient = taoClient;
        _clientManageHandler = clientManageHandler;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _customBufferFactory = customBufferFactory;
        _testRecordsProvider = testRecordsProvider;
        _mapOpenerOverride = mapOpenerOverride;
        _moduleEnumeratorOverride = moduleEnumeratorOverride;
    }

    public void Dispose()
    {
        CleanupAbortedWindow(deleteStateFile: false);
    }

    private void CleanupAbortedWindow(bool deleteStateFile = true)
    {
        ActiveDrainSession? sessionToDispose;
        lock (_syncLock)
        {
            sessionToDispose = _activeDrainSession;
            _activeDrainSession = null;
            _activeWindow = null;
            if (deleteStateFile && File.Exists(StateFilePath))
            {
                try
                {
                    File.Delete(StateFilePath);
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[PerfWindowHandler] Failed deleting aborted state file '{StateFilePath}': {ex.Message}");
                }
            }
        }
        sessionToDispose?.Dispose();
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string action = "status";
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("action", out JsonElement aEl) && !string.IsNullOrWhiteSpace(aEl.GetString()))
            {
                action = aEl.GetString()!.Trim().ToLowerInvariant();
            }
        }

        double hitchThreshold = 30.0;
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("hitchThresholdMs", out JsonElement htEl) && htEl.TryGetDouble(out double htVal) && htVal > 0)
        {
            hitchThreshold = htVal;
        }

        double majorHitchThreshold = 50.0;
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("majorHitchThresholdMs", out JsonElement mhtEl) && mhtEl.TryGetDouble(out double mhtVal) && mhtVal > 0)
        {
            majorHitchThreshold = mhtVal;
        }

        double budgetThreshold = 1.0;
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("budgetThresholdMs", out JsonElement btEl) && btEl.TryGetDouble(out double btVal) && btVal > 0)
        {
            budgetThreshold = btVal;
        }

        bool includeModuleAudit = TryGetBoolArgument(arguments, "includeModuleAudit", "include_module_audit") ?? false;
        bool? expectedVkLayerLoaded = TryGetBoolArgument(arguments, "expectedVkLayerLoaded", "expected_vk_layer_loaded");

        switch (action)
        {
            case "start":
                return await HandleStartAsync(arguments, hitchThreshold, majorHitchThreshold, budgetThreshold, cancellationToken).ConfigureAwait(false);

            case "stop":
                return await HandleStopAsync(includeModuleAudit, expectedVkLayerLoaded, cancellationToken).ConfigureAwait(false);

            case "measure":
                int durationMs = 1000;
                if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("durationMs", out JsonElement durEl) && durEl.TryGetInt32(out int dVal) && dVal > 0)
                {
                    durationMs = Math.Clamp(dVal, 50, 30000);
                }
                ToolCallResult startRes = await HandleStartAsync(arguments, hitchThreshold, majorHitchThreshold, budgetThreshold, cancellationToken).ConfigureAwait(false);
                if (startRes.IsError) return startRes;

                try
                {
                    await Task.Delay(durationMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    CleanupAbortedWindow();
                    throw;
                }
                return await HandleStopAsync(includeModuleAudit, expectedVkLayerLoaded, cancellationToken).ConfigureAwait(false);

            case "audit_runtime":
                return HandleAuditRuntime(arguments, expectedVkLayerLoaded);

            case "simulate_hitch":
                return await HandleSimulateHitchAsync(arguments, cancellationToken).ConfigureAwait(false);

            case "status":
            default:
                return HandleStatus();
        }
    }

    private async Task<ToolCallResult> HandleStartAsync(
        JsonElement arguments,
        double hitchThreshold,
        double majorHitchThreshold,
        double budgetThreshold,
        CancellationToken cancellationToken)
    {
        int pid = ResolveTargetProcessId(arguments);

        ulong startFrameIndex = 0;
        long startTimestampQpc = 0;
        string telemetrySource = "unavailable";
        bool isRenderFrameAccurate = false;
        string mapName = string.Empty;
        ActiveDrainSession? newDrainSession = null;

        // Query native telemetry buffer baseline & start 100ms lossless background drainer
        if (_testRecordsProvider != null)
        {
            startFrameIndex = 1000;
            startTimestampQpc = 1000000;
            telemetrySource = "synthetic_test_provider";
            isRenderFrameAccurate = true;
            mapName = "synthetic";
        }
        else
        {
            (MemoryMappedTelemetryBuffer? buffer, string source, bool renderAccurate, string openedMap) = OpenTelemetryBufferWithMetadata(pid);
            telemetrySource = source;
            isRenderFrameAccurate = renderAccurate;
            mapName = openedMap;

            if (buffer != null)
            {
                startFrameIndex = buffer.Head;
                if (buffer.ReadLatest(out FrameTelemetryRecord latestRec) && latestRec.FrameIndex > 0)
                {
                    startFrameIndex = latestRec.FrameIndex;
                    startTimestampQpc = latestRec.TimestampQpc;
                }

                // Transfer ownership of the opened buffer to the 50ms lossless drainer.
                newDrainSession = new ActiveDrainSession(
                    buffer,
                    startFrameIndex,
                    telemetrySource,
                    isRenderFrameAccurate,
                    mapName,
                    _logger);
            }
        }

        // Query Lua TAO transition log baseline & synchronize frame index
        ulong startLuaFrame = startFrameIndex;
        double startLuaTimestamp = 0;

        if (_taoClient != null && _taoClient.IsConnected)
        {
            try
            {
                TaoResponse<JsonElement> syncResp = await _taoClient.DispatchAsync("sync_frame", new { frame = startFrameIndex }, 1500, cancellationToken).ConfigureAwait(false);
                if (syncResp.Success && syncResp.Data.ValueKind == JsonValueKind.Object)
                {
                    if (syncResp.Data.TryGetProperty("currentFrame", out JsonElement fEl) && fEl.TryGetUInt64(out ulong fVal))
                    {
                        startLuaFrame = fVal;
                    }
                    if (syncResp.Data.TryGetProperty("currentTimestamp", out JsonElement tEl) && tEl.TryGetDouble(out double tVal))
                    {
                        startLuaTimestamp = tVal;
                    }
                }
                else
                {
                    TaoResponse<JsonElement> logResp = await _taoClient.DispatchAsync("get_transition_log", new { limit = 1 }, 1500, cancellationToken).ConfigureAwait(false);
                    if (logResp.Success && logResp.Data.ValueKind == JsonValueKind.Object)
                    {
                        if (logResp.Data.TryGetProperty("currentFrame", out JsonElement fEl) && fEl.TryGetUInt64(out ulong fVal))
                        {
                            startLuaFrame = fVal;
                        }
                        if (logResp.Data.TryGetProperty("currentTimestamp", out JsonElement tEl) && tEl.TryGetDouble(out double tVal))
                        {
                            startLuaTimestamp = tVal;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                newDrainSession?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[PerfWindowHandler] Probing initial Lua transition mark failed: {ex.Message}");
            }
        }

        string windowId = Guid.NewGuid().ToString("N")[..8];
        ActiveDrainSession? previousSession;
        lock (_syncLock)
        {
            previousSession = _activeDrainSession;
            _activeDrainSession = newDrainSession;
            _activeWindow = new PerfWindowState
            {
                WindowId = windowId,
                ProcessId = pid,
                StartFrameIndex = startFrameIndex,
                StartTimestampQpc = startTimestampQpc,
                StartTimeUtc = _timeProvider.GetUtcNow(),
                StartLuaFrame = startLuaFrame,
                StartLuaTimestamp = startLuaTimestamp,
                TelemetrySource = telemetrySource,
                IsRenderFrameAccurate = isRenderFrameAccurate,
                TelemetryMapName = mapName,
                HitchThresholdMs = hitchThreshold,
                MajorHitchThresholdMs = majorHitchThreshold,
                BudgetThresholdMs = budgetThreshold
            };
            try
            {
                string json = JsonSerializer.Serialize(_activeWindow);
                File.WriteAllText(StateFilePath, json);
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[PerfWindowHandler] Failed persisting active window state to '{StateFilePath}': {ex.Message}");
            }
        }
        previousSession?.Dispose();

        _logger?.Invoke(
            $"[PerfWindowHandler] Started performance window '{windowId}' on PID {pid} (Source: {telemetrySource}, RenderAccurate: {isRenderFrameAccurate}, Map: {mapName}, StartFrame: {startFrameIndex}, StartLuaFrame: {startLuaFrame}).");

        return ToolCallResult.Success(new
        {
            status = "recording",
            active = true,
            action = "start",
            windowId,
            processId = pid,
            telemetrySource,
            isRenderFrameAccurate,
            telemetryMapName = mapName,
            startFrameIndex,
            startTimestampQpc,
            startLuaFrame,
            startLuaTimestamp,
            hitchThresholdMs = hitchThreshold,
            majorHitchThresholdMs = majorHitchThreshold,
            budgetThresholdMs = budgetThreshold,
            message = "Performance measurement window started. Capture active."
        });
    }

    private async Task<ToolCallResult> HandleStopAsync(
        bool includeModuleAudit,
        bool? expectedVkLayerLoaded,
        CancellationToken cancellationToken)
    {
        PerfWindowState? state;
        ActiveDrainSession? drainSession;
        lock (_syncLock)
        {
            state = _activeWindow;
            drainSession = _activeDrainSession;
            _activeWindow = null;
            _activeDrainSession = null;

            if (state == null && File.Exists(StateFilePath))
            {
                try
                {
                    string json = File.ReadAllText(StateFilePath);
                    state = JsonSerializer.Deserialize<PerfWindowState>(json);
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[PerfWindowHandler] Failed reading persisted state file '{StateFilePath}': {ex.Message}");
                }
            }
            try
            {
                if (File.Exists(StateFilePath))
                {
                    File.Delete(StateFilePath);
                }
            }
            catch (Exception delEx)
            {
                Console.Error.WriteLine($"[PerfWindowHandler] Failed deleting state file '{StateFilePath}': {delEx.Message}");
            }
        }

        if (state == null)
        {
            drainSession?.Dispose();
            return ToolCallResult.Json(new
            {
                status = "failed",
                error = "No active performance measurement window found. Call 'kongor_perf_window' with action 'start' first.",
                actionableRemediation = "Call 'kongor_perf_window' with action 'start' before invoking 'stop'."
            }, isError: true);
        }

        DateTimeOffset stopTimeUtc = _timeProvider.GetUtcNow();
        double elapsedMs = (stopTimeUtc - state.StartTimeUtc).TotalMilliseconds;

        // 1. Gather Frame Telemetry Records (lossless drain preferred; snapshot fallback if cross-process stop)
        List<FrameTelemetryRecord> records = [];
        ulong droppedRingFrames = 0;
        string telemetrySource = state.TelemetrySource;
        bool isRenderFrameAccurate = state.IsRenderFrameAccurate;
        string telemetryMapName = state.TelemetryMapName;

        if (_testRecordsProvider != null)
        {
            drainSession?.Dispose();
            records = _testRecordsProvider(state.StartFrameIndex);
            telemetrySource = "synthetic_test_provider";
            isRenderFrameAccurate = true;
            telemetryMapName = "synthetic";
        }
        else if (drainSession != null)
        {
            try
            {
                (List<FrameTelemetryRecord> drained, ulong dropped) = await drainSession.StopAndCollectAsync().ConfigureAwait(false);
                records = drained;
                droppedRingFrames = dropped;
                telemetrySource = drainSession.TelemetrySource;
                isRenderFrameAccurate = drainSession.IsRenderFrameAccurate;
                telemetryMapName = drainSession.MapName;
            }
            finally
            {
                drainSession.Dispose();
            }
        }
        else
        {
            (MemoryMappedTelemetryBuffer? buffer, string source, bool renderAccurate, string openedMap) = OpenTelemetryBufferWithMetadata(state.ProcessId);
            telemetrySource = source;
            isRenderFrameAccurate = renderAccurate;
            telemetryMapName = openedMap;
            using (buffer)
            {
                if (buffer != null)
                {
                    int capacity = (int)Math.Clamp(buffer.Capacity, 256U, 8192U);
                    FrameTelemetryRecord[] recordArray = new FrameTelemetryRecord[capacity];
                    int readCount = buffer.ReadRecent(recordArray.AsSpan());

                    ulong lastSeen = state.StartFrameIndex;
                    for (int i = 0; i < readCount; i++)
                    {
                        FrameTelemetryRecord rec = recordArray[i];
                        if (rec.FrameIndex > state.StartFrameIndex)
                        {
                            if (lastSeen > 0 && rec.FrameIndex > lastSeen + 1)
                            {
                                droppedRingFrames += rec.FrameIndex - lastSeen - 1;
                            }
                            records.Add(rec);
                            lastSeen = rec.FrameIndex;
                        }
                    }
                }
            }
        }

        // 2. Query Lua UI Transition Log since window start
        List<LuaTransitionRecord> transitions = [];
        if (_taoClient != null && _taoClient.IsConnected)
        {
            try
            {
                TaoResponse<JsonElement> logResp = await _taoClient.DispatchAsync("get_transition_log", new
                {
                    sinceFrame = state.StartLuaFrame,
                    sinceTimestamp = state.StartLuaTimestamp,
                    limit = 256
                }, 2000, cancellationToken).ConfigureAwait(false);

                if (logResp.Success && logResp.Data.ValueKind == JsonValueKind.Object && logResp.Data.TryGetProperty("transitions", out JsonElement trArray) && trArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in trArray.EnumerateArray())
                    {
                        ulong frame = item.TryGetProperty("frame", out JsonElement fEl) && fEl.TryGetUInt64(out ulong fVal) ? fVal : 0;
                        double ts = item.TryGetProperty("timestamp", out JsonElement tEl) && tEl.TryGetDouble(out double tVal) ? tVal : 0.0;
                        double ht = item.TryGetProperty("hostTime", out JsonElement hEl) && hEl.TryGetDouble(out double hVal) ? hVal : 0.0;
                        string from = item.TryGetProperty("from", out JsonElement frEl) ? frEl.GetString() ?? "" : "";
                        string to = item.TryGetProperty("to", out JsonElement toEl) ? toEl.GetString() ?? "" : "";
                        string act = item.TryGetProperty("action", out JsonElement aEl) ? aEl.GetString() ?? "" : "";
                        double dur = item.TryGetProperty("durationMs", out JsonElement dEl) && dEl.TryGetDouble(out double dVal) ? dVal : 0.0;
                        bool budgetExceeded = item.TryGetProperty("budgetExceeded", out JsonElement bEl) && bEl.GetBoolean();

                        JsonElement? extra = item.TryGetProperty("extra", out JsonElement exEl) ? exEl : null;

                        transitions.Add(new LuaTransitionRecord
                        {
                            Frame = frame,
                            Timestamp = ts,
                            HostTime = ht,
                            From = from,
                            To = to,
                            Action = act,
                            DurationMs = dur,
                            BudgetExceeded = budgetExceeded || (dur > state.BudgetThresholdMs),
                            Extra = extra
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[PerfWindowHandler] Retrieving Lua transition log failed: {ex.Message}");
            }
        }

        // 3. Compute True QPC Frame-Delta & High-Hz Metrics
        int totalFrames = records.Count;
        ulong engineFramesElapsed = (totalFrames > 0 && state.StartFrameIndex > 0 && records[^1].FrameIndex > state.StartFrameIndex)
            ? (records[^1].FrameIndex - state.StartFrameIndex)
            : (ulong)totalFrames;

        List<double> effectiveFrameTimesMs = new(totalFrames);
        List<CorrelatedHitchRecord> hitches = [];
        int hitchCount30ms = 0;
        int hitchCount50ms = 0;
        int gt8ms = 0;
        int gt10ms = 0;
        int gt16_67ms = 0;
        int gt33_33ms = 0;
        int gt50ms = 0;

        int sentinelHitchCount = 0;
        int sentinelCpuStallCount = 0;
        int sentinelGpuStallCount = 0;
        int sentinelDwmWaitCount = 0;
        int sentinelPreemptionCount = 0;

        double sumCpuEngineMs = 0.0;
        double sumGpuRenderMs = 0.0;
        double sumPresentWaitMs = 0.0;
        double sumDrawCalls = 0.0;

        double qpcDeltaSumMs = 0.0;
        int qpcDeltaCount = 0;
        double qpcFrequency = Stopwatch.Frequency > 0 ? Stopwatch.Frequency : 10_000_000.0;

        ulong prevFrameIndex = (_testRecordsProvider == null && state.StartTimestampQpc > 0) ? state.StartFrameIndex : 0UL;
        long prevTimestampQpc = (_testRecordsProvider == null && state.StartTimestampQpc > 0) ? state.StartTimestampQpc : 0L;

        foreach (FrameTelemetryRecord rec in records)
        {
            sumCpuEngineMs += rec.CpuFrameTimeMs;
            sumGpuRenderMs += rec.GpuFrameTimeMs;
            sumPresentWaitMs += rec.InputLatencyMs;
            sumDrawCalls += rec.DrawCallCount;

            double effectiveFrameMs;
            bool usedQpcDelta = false;
            // Only derive end-to-end QPC frame deltas on render-frame-accurate buffers.
            // version.dll's 20Hz guard ring ticks every ~50ms via Sleep(50) and would falsely score as 100% 50ms hitches.
            if (isRenderFrameAccurate &&
                prevTimestampQpc > 0 &&
                rec.TimestampQpc > prevTimestampQpc &&
                (prevFrameIndex == 0 || rec.FrameIndex == prevFrameIndex + 1))
            {
                double qpcMs = (rec.TimestampQpc - prevTimestampQpc) * 1000.0 / qpcFrequency;
                if (qpcMs > 0.05 && qpcMs < 5000.0)
                {
                    effectiveFrameMs = qpcMs;
                    qpcDeltaSumMs += qpcMs;
                    qpcDeltaCount++;
                    usedQpcDelta = true;
                }
                else
                {
                    effectiveFrameMs = ComputeFallbackFrameMs(rec);
                }
            }
            else
            {
                effectiveFrameMs = ComputeFallbackFrameMs(rec);
            }

            if (rec.TimestampQpc > 0)
            {
                prevFrameIndex = rec.FrameIndex;
                prevTimestampQpc = rec.TimestampQpc;
            }

            effectiveFrameTimesMs.Add(effectiveFrameMs);

            if (effectiveFrameMs > 8.0) gt8ms++;
            if (effectiveFrameMs > 10.0) gt10ms++;
            if (effectiveFrameMs > 16.67) gt16_67ms++;
            if (effectiveFrameMs > 33.33) gt33_33ms++;
            if (effectiveFrameMs > 50.0) gt50ms++;

            // Sentinel flags are ONLY valid on render-frame-accurate buffers (never on version.dll's 20Hz PTT ring).
            if (isRenderFrameAccurate)
            {
                if ((rec.ReservedFlags & FrameTelemetryRecord.FlagHitch) != 0) sentinelHitchCount++;
                if ((rec.ReservedFlags & FrameTelemetryRecord.FlagCpuStall) != 0) sentinelCpuStallCount++;
                if ((rec.ReservedFlags & FrameTelemetryRecord.FlagGpuStall) != 0) sentinelGpuStallCount++;
                if ((rec.ReservedFlags & FrameTelemetryRecord.FlagDwmWait) != 0) sentinelDwmWaitCount++;
                if ((rec.ReservedFlags & FrameTelemetryRecord.FlagPreemption) != 0) sentinelPreemptionCount++;
            }

            bool isHitch30 = effectiveFrameMs >= state.HitchThresholdMs ||
                             (!usedQpcDelta && (rec.CpuFrameTimeMs >= state.HitchThresholdMs || rec.GpuFrameTimeMs >= state.HitchThresholdMs));
            bool isHitch50 = effectiveFrameMs >= state.MajorHitchThresholdMs ||
                             (!usedQpcDelta && (rec.CpuFrameTimeMs >= state.MajorHitchThresholdMs || rec.GpuFrameTimeMs >= state.MajorHitchThresholdMs));

            if (isHitch30)
            {
                hitchCount30ms++;
                if (isHitch50) hitchCount50ms++;

                if (hitches.Count < MaxDetailedHitchesReturned)
                {
                    // 1. Primary: Correlate with UI transition matching frame number (exact or within 1 frame)
                    LuaTransitionRecord? attributed = transitions.FirstOrDefault(t =>
                        (t.Frame > 0 && rec.FrameIndex > 0 && Math.Abs((long)t.Frame - (long)rec.FrameIndex) <= 1) ||
                        (t.Frame == rec.FrameIndex && t.Frame > 0));

                    // 2. Secondary Fallback: Clock-sync record (QPC elapsed ms <-> Lua timestamp elapsed ms) ONLY if Lua frame was unavailable
                    if (attributed == null && rec.TimestampQpc > 0 && state.StartTimestampQpc > 0 && state.StartLuaTimestamp > 0)
                    {
                        double nativeElapsedMs = (rec.TimestampQpc - state.StartTimestampQpc) * 1000.0 / qpcFrequency;
                        attributed = transitions
                            .Where(t => t.Timestamp > 0)
                            .OrderBy(t => Math.Abs((t.Timestamp - state.StartLuaTimestamp) - nativeElapsedMs))
                            .FirstOrDefault(t => Math.Abs((t.Timestamp - state.StartLuaTimestamp) - nativeElapsedMs) <= 50.0);
                    }

                    hitches.Add(new CorrelatedHitchRecord
                    {
                        FrameIndex = rec.FrameIndex,
                        TotalFrameTimeMs = Math.Round(effectiveFrameMs, 3),
                        CpuFrameTimeMs = rec.CpuFrameTimeMs,
                        GpuFrameTimeMs = rec.GpuFrameTimeMs,
                        ReservedFlags = isRenderFrameAccurate ? rec.ReservedFlags : 0U,
                        IsMajorHitch = isHitch50,
                        AttributedTransition = attributed
                    });
                }
            }
        }

        double minFrameTimeMs = 0.0;
        double maxFrameTimeMs = 0.0;
        double avgFrameTimeMs = 0.0;
        double p50FrameTimeMs = 0.0;
        double p95FrameTimeMs = 0.0;
        double p99FrameTimeMs = 0.0;
        double p999FrameTimeMs = 0.0;
        double medianFps = 0.0;
        double p1LowFps = 0.0;
        double p01LowFps = 0.0;
        double averageFps = 0.0;

        if (effectiveFrameTimesMs.Count > 0)
        {
            double[] sorted = effectiveFrameTimesMs.ToArray();
            Array.Sort(sorted);

            minFrameTimeMs = Math.Round(sorted[0], 3);
            maxFrameTimeMs = Math.Round(sorted[^1], 3);
            avgFrameTimeMs = Math.Round(effectiveFrameTimesMs.Average(), 3);
            p50FrameTimeMs = Math.Round(GetPercentile(sorted, 0.50), 3);
            p95FrameTimeMs = Math.Round(GetPercentile(sorted, 0.95), 3);
            p99FrameTimeMs = Math.Round(GetPercentile(sorted, 0.99), 3);
            p999FrameTimeMs = Math.Round(GetPercentile(sorted, 0.999), 3);

            medianFps = p50FrameTimeMs > 0.001 ? Math.Round(1000.0 / p50FrameTimeMs, 2) : 0.0;
            p1LowFps = ComputeLowTailFps(sorted, 0.01);
            p01LowFps = ComputeLowTailFps(sorted, 0.001);

            if (qpcDeltaCount >= 2 && qpcDeltaSumMs > 0.0)
            {
                averageFps = Math.Round(qpcDeltaCount / (qpcDeltaSumMs / 1000.0), 2);
            }
            else if (elapsedMs > 0.0)
            {
                averageFps = Math.Round(totalFrames / (elapsedMs / 1000.0), 2);
            }
            else if (avgFrameTimeMs > 0.001)
            {
                averageFps = Math.Round(1000.0 / avgFrameTimeMs, 2);
            }
        }

        double rateDurationSec = qpcDeltaSumMs > 0.0
            ? (qpcDeltaSumMs / 1000.0)
            : Math.Max(0.001, elapsedMs / 1000.0);

        double avgCpuEngineMs = totalFrames > 0 ? Math.Round(sumCpuEngineMs / totalFrames, 3) : 0.0;
        double avgGpuRenderMs = totalFrames > 0 ? Math.Round(sumGpuRenderMs / totalFrames, 3) : 0.0;
        double avgPresentWaitMs = totalFrames > 0 ? Math.Round(sumPresentWaitMs / totalFrames, 3) : 0.0;
        double avgDrawCalls = totalFrames > 0 ? Math.Round(sumDrawCalls / totalFrames, 1) : 0.0;

        // 5. Frame Budget Compliance Verification (<= 1.0 ms for UI ticks)
        List<LuaTransitionRecord> budgetViolations = transitions
            .Where(t => t.DurationMs > state.BudgetThresholdMs)
            .ToList();

        bool frameBudgetPassed = budgetViolations.Count == 0;

        RuntimeModuleAuditResult? moduleAudit = includeModuleAudit
            ? ExecuteModuleAudit(state.ProcessId, expectedVkLayerLoaded)
            : null;

        _logger?.Invoke(
            $"[PerfWindowHandler] Stopped window '{state.WindowId}' (Source={telemetrySource}, RenderAccurate={isRenderFrameAccurate}, Sampled={totalFrames}, Dropped={droppedRingFrames}, AvgFPS={averageFps}, MedFPS={medianFps}, 1%Low={p1LowFps}, P99={p99FrameTimeMs}ms, Max={maxFrameTimeMs}ms, Hitches>10ms={gt10ms}, >30ms={hitchCount30ms}, >50ms={hitchCount50ms}, BudgetPassed={frameBudgetPassed}).");

        return ToolCallResult.Success(new
        {
            status = "completed",
            action = "stop",
            windowId = state.WindowId,
            processId = state.ProcessId,
            summary = new
            {
                telemetrySource,
                isRenderFrameAccurate,
                telemetryMapName,
                totalFrames,
                engineFramesElapsed,
                sampledFrames = totalFrames,
                droppedRingFrames,
                qpcDeltaFrames = qpcDeltaCount,
                elapsedMs = Math.Round(elapsedMs, 2),
                averageFps,
                medianFps,
                p1LowFps,
                p01LowFps,
                avgFrameTimeMs,
                minFrameTimeMs,
                maxFrameTimeMs,
                p50FrameTimeMs,
                p95FrameTimeMs,
                p99FrameTimeMs,
                p999FrameTimeMs,
                avgCpuEngineMs,
                avgGpuRenderMs,
                avgPresentWaitMs,
                avgDrawCalls,
                hitchCount30ms,
                hitchCount50ms,
                hitches = new
                {
                    gt8ms,
                    gt10ms,
                    gt16_67ms,
                    gt33_33ms,
                    gt50ms,
                    rateGt8msPerSec = Math.Round(gt8ms / rateDurationSec, 2),
                    rateGt10msPerSec = Math.Round(gt10ms / rateDurationSec, 2)
                },
                sentinelHitchFlags = new
                {
                    hitch = sentinelHitchCount,
                    cpuStall = sentinelCpuStallCount,
                    gpuStall = sentinelGpuStallCount,
                    dwmWait = sentinelDwmWaitCount,
                    preemption = sentinelPreemptionCount
                },
                transitionsCount = transitions.Count,
                budgetViolationsCount = budgetViolations.Count,
                frameBudgetPassed
            },
            budgetThresholdMs = state.BudgetThresholdMs,
            hitches,
            transitions,
            budgetViolations,
            moduleAudit,
            timestampUtc = stopTimeUtc.ToString("o")
        });
    }

    private ToolCallResult HandleAuditRuntime(JsonElement arguments, bool? expectedVkLayerLoaded)
    {
        int pid = ResolveTargetProcessId(arguments);
        if (pid <= 0)
        {
            return ToolCallResult.Json(new
            {
                status = "inconclusive",
                action = "audit_runtime",
                processAlive = false,
                error = "No running hon_x64.exe process detected (and no explicit 'pid' provided).",
                actionableRemediation = "Launch hon_x64.exe or supply an explicit 'pid' argument to audit loaded modules."
            }, isError: false);
        }

        RuntimeModuleAuditResult audit = ExecuteModuleAudit(pid, expectedVkLayerLoaded);
        return ToolCallResult.Success(new
        {
            status = audit.ProcessAlive ? (audit.Passed ? "passed" : "drift_detected") : "inconclusive",
            action = "audit_runtime",
            audit
        });
    }

    private RuntimeModuleAuditResult ExecuteModuleAudit(int pid, bool? expectedVkLayerLoaded)
    {
        string auditedAtUtc = _timeProvider.GetUtcNow().ToString("o");
        IReadOnlyList<(string ModuleName, string FilePath)>? loadedModules = null;

        if (_moduleEnumeratorOverride != null)
        {
            loadedModules = _moduleEnumeratorOverride(pid);
        }
        else if (OperatingSystem.IsWindows() && pid > 0)
        {
            try
            {
                using Process proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    List<(string ModuleName, string FilePath)> list = [];
                    foreach (ProcessModule mod in proc.Modules)
                    {
                        list.Add((mod.ModuleName ?? string.Empty, mod.FileName ?? string.Empty));
                    }
                    loadedModules = list;
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[PerfWindowHandler] Module audit failed for PID {pid}: {ex.Message}");
            }
        }

        if (loadedModules == null)
        {
            return new RuntimeModuleAuditResult
            {
                ProcessId = pid,
                ProcessAlive = false,
                TotalLoadedModules = 0,
                VidVulkanLoaded = false,
                D3d9Loaded = false,
                VersionDllLoaded = false,
                VkLayerReplayCaptureLoaded = false,
                ExpectedVkLayerReplayCaptureLoaded = expectedVkLayerLoaded,
                DetectedDeniedHooks = [],
                KeyModules = [],
                Passed = false,
                Findings = [$"Unable to enumerate loaded modules for PID {pid} (process exited or access denied)."],
                AuditedAtUtc = auditedAtUtc
            };
        }

        HashSet<string> moduleSet = new(loadedModules.Select(m => m.ModuleName), StringComparer.OrdinalIgnoreCase);
        bool vkLayerLoaded = moduleSet.Contains("VkLayer_hon_replay_capture.dll");
        bool vidVulkanLoaded = moduleSet.Contains("vid_vulkan_x64.dll");
        bool d3d9Loaded = moduleSet.Contains("d3d9.dll");
        bool versionLoaded = moduleSet.Contains("version.dll");

        List<string> deniedHits = [];
        foreach (string denied in DeniedOverlayHookModules)
        {
            if (moduleSet.Contains(denied))
            {
                deniedHits.Add(denied);
            }
        }

        List<RuntimeModuleDigest> keyModules = [];
        foreach ((string modName, string filePath) in loadedModules)
        {
            if (KeyAuditModules.Contains(modName))
            {
                keyModules.Add(new RuntimeModuleDigest
                {
                    ModuleName = modName,
                    FilePath = filePath,
                    Sha256 = ComputeFileSha256(filePath)
                });
            }
        }

        List<string> findings = [];
        if (expectedVkLayerLoaded.HasValue && vkLayerLoaded != expectedVkLayerLoaded.Value)
        {
            findings.Add($"VkLayer_hon_replay_capture.dll drift: loaded={vkLayerLoaded}, expected={expectedVkLayerLoaded.Value}.");
        }
        if (deniedHits.Count > 0)
        {
            findings.Add($"Denied third-party overlay/Vulkan hook(s) loaded: {string.Join(", ", deniedHits)}.");
        }

        return new RuntimeModuleAuditResult
        {
            ProcessId = pid,
            ProcessAlive = true,
            TotalLoadedModules = loadedModules.Count,
            VidVulkanLoaded = vidVulkanLoaded,
            D3d9Loaded = d3d9Loaded,
            VersionDllLoaded = versionLoaded,
            VkLayerReplayCaptureLoaded = vkLayerLoaded,
            ExpectedVkLayerReplayCaptureLoaded = expectedVkLayerLoaded,
            DetectedDeniedHooks = deniedHits,
            KeyModules = keyModules,
            Passed = findings.Count == 0,
            Findings = findings,
            AuditedAtUtc = auditedAtUtc
        };
    }

    private ToolCallResult HandleStatus()
    {
        PerfWindowState? state;
        lock (_syncLock)
        {
            state = _activeWindow;
            if (state == null && File.Exists(StateFilePath))
            {
                try
                {
                    string json = File.ReadAllText(StateFilePath);
                    state = JsonSerializer.Deserialize<PerfWindowState>(json);
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[PerfWindowHandler] Failed reading state file '{StateFilePath}': {ex.Message}");
                }
            }
        }

        if (state == null)
        {
            return ToolCallResult.Success(new
            {
                status = "idle",
                active = false,
                message = "No performance measurement window is currently active."
            });
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        double elapsedMs = (now - state.StartTimeUtc).TotalMilliseconds;

        return ToolCallResult.Success(new
        {
            status = "recording",
            active = true,
            windowId = state.WindowId,
            processId = state.ProcessId,
            telemetrySource = state.TelemetrySource,
            isRenderFrameAccurate = state.IsRenderFrameAccurate,
            telemetryMapName = state.TelemetryMapName,
            startFrameIndex = state.StartFrameIndex,
            startLuaFrame = state.StartLuaFrame,
            elapsedMs = Math.Round(elapsedMs, 2),
            hitchThresholdMs = state.HitchThresholdMs,
            majorHitchThresholdMs = state.MajorHitchThresholdMs,
            budgetThresholdMs = state.BudgetThresholdMs
        });
    }

    private async Task<ToolCallResult> HandleSimulateHitchAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        double durationMs = 35.0;
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("durationMs", out JsonElement durEl) && durEl.TryGetDouble(out double dVal) && dVal > 0)
        {
            durationMs = dVal;
        }

        string panel = "store";
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("panel", out JsonElement pEl) && !string.IsNullOrWhiteSpace(pEl.GetString()))
        {
            panel = pEl.GetString()!;
        }

        string action = "forced_busy_loop";
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("targetAction", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!;
        }

        if (_taoClient != null && _taoClient.IsConnected)
        {
            TaoResponse<JsonElement> resp = await _taoClient.DispatchAsync("simulate_hitch", new
            {
                durationMs,
                panel,
                action
            }, 5000, cancellationToken).ConfigureAwait(false);

            if (resp.Success)
            {
                return ToolCallResult.Success(resp.Data);
            }

            return ToolCallResult.Json(new
            {
                status = "failed",
                error = resp.Error ?? "Failed to dispatch simulate_hitch to Lua TAO."
            }, isError: true);
        }

        return ToolCallResult.Success(new
        {
            status = "simulated",
            durationMs,
            panel,
            action,
            message = "Simulated hitch parameters recorded (client disconnected)."
        });
    }

    private int ResolveTargetProcessId(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if ((arguments.TryGetProperty("pid", out JsonElement pEl) ||
                 arguments.TryGetProperty("processId", out pEl) ||
                 arguments.TryGetProperty("process_id", out pEl)) &&
                pEl.TryGetInt32(out int pVal) && pVal > 0)
            {
                return pVal;
            }
        }

        Process? proc = _clientManageHandler?.GetRunningClientProcess() ?? new ClientProcessSupervisor().FindRunningClientProcess();
        return proc?.Id ?? 0;
    }

    private (MemoryMappedTelemetryBuffer? Buffer, string TelemetrySource, bool IsRenderFrameAccurate, string MapName) OpenTelemetryBufferWithMetadata(int processId)
    {
        if (_customBufferFactory != null)
        {
            MemoryMappedTelemetryBuffer? custom = _customBufferFactory(processId);
            return custom != null
                ? (custom, "custom_test_buffer", true, "custom")
                : (null, "unavailable", false, string.Empty);
        }

        // Priority 1: vid_vulkan_x64.dll per-rendered-frame FastBus (200+ Hz, QPC-timestamped, sentinel-flagged).
        string[] fastBusCandidates = ["Local\\KongorTelemetryBuffer", "KongorTelemetryBuffer"];
        foreach (string candidate in fastBusCandidates)
        {
            MemoryMappedTelemetryBuffer? buffer = TryOpenMap(candidate);
            if (buffer == null)
            {
                continue;
            }

            if (processId > 0 &&
                buffer.TryReadHeaderV2(out TelemetryRingHeaderV2 h2) &&
                h2.ProducerProcessId != 0 &&
                h2.ProducerProcessId != (ulong)processId)
            {
                _logger?.Invoke(
                    $"[PerfWindowHandler] Skipping '{candidate}' because ProducerProcessId={h2.ProducerProcessId} does not match target PID={processId}.");
                buffer.Dispose();
                continue;
            }

            return (buffer, "vid_vulkan_render_fastbus", true, candidate);
        }

        // Priority 2 (Fallback only): version.dll's ~20 Hz state/PTT guard ring. Explicitly marked NOT render-frame-accurate.
        if (processId > 0)
        {
            string[] guardCandidates = [$"Local\\KongorClientTelemetry_{processId}", $"KongorClientTelemetry_{processId}"];
            foreach (string candidate in guardCandidates)
            {
                MemoryMappedTelemetryBuffer? buffer = TryOpenMap(candidate);
                if (buffer != null)
                {
                    _logger?.Invoke(
                        $"[PerfWindowHandler] WARNING: Falling back to 20Hz version.dll guard ring '{candidate}' (Local\\KongorTelemetryBuffer unavailable). Frame metrics are NOT render-frame-accurate.");
                    return (buffer, "native_guard_20hz_fallback", false, candidate);
                }
            }
        }

        return (null, "unavailable", false, string.Empty);
    }

    private MemoryMappedTelemetryBuffer? TryOpenMap(string mapName)
    {
        if (_mapOpenerOverride != null)
        {
            return _mapOpenerOverride(mapName);
        }

        return MemoryMappedTelemetryBuffer.TryOpenConsumer(mapName, out MemoryMappedTelemetryBuffer? buffer, out _)
            ? buffer
            : null;
    }

    private static double ComputeFallbackFrameMs(in FrameTelemetryRecord rec)
    {
        if (rec.TimestampQpc > 0)
        {
            double componentSum = rec.CpuFrameTimeMs + rec.GpuFrameTimeMs + rec.InputLatencyMs;
            if (componentSum > 0.05)
            {
                return componentSum;
            }
        }
        return Math.Max(rec.CpuFrameTimeMs, rec.GpuFrameTimeMs);
    }

    private static double GetPercentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0.0;
        int idx = Math.Clamp((int)Math.Floor(p * sorted.Length), 0, sorted.Length - 1);
        return sorted[idx];
    }

    private static double ComputeLowTailFps(double[] sorted, double tailFraction)
    {
        if (sorted.Length == 0) return 0.0;
        int worstCount = Math.Max(1, (int)Math.Ceiling(sorted.Length * tailFraction));
        double sumWorstMs = 0.0;
        for (int i = sorted.Length - worstCount; i < sorted.Length; i++)
        {
            sumWorstMs += sorted[i];
        }
        double avgWorstMs = sumWorstMs / worstCount;
        return avgWorstMs > 0.001 ? Math.Round(1000.0 / avgWorstMs, 2) : 0.0;
    }

    private static string ComputeFileSha256(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] hash = SHA256.HashData(fs);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static bool? TryGetBoolArgument(JsonElement arguments, string camelKey, string snakeKey)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (arguments.TryGetProperty(camelKey, out JsonElement el) || arguments.TryGetProperty(snakeKey, out el))
        {
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
        }

        return null;
    }
}

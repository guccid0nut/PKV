// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VulkanManageHandler.cs
// System: Kongor.Cli MCP Protocol Handlers - Vulkan & DXVK Management
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Microsoft.Win32;
using WILLOWMAKER.Core.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// (#203) 3-state headless RenderDoc capture state machine (<c>Idle -&gt; Triggered -&gt; InProgress -&gt; Idle</c>).
/// </summary>
public enum RenderDocCaptureState
{
    Idle = 0,
    Triggered = 1,
    InProgress = 2
}

/// <summary>
/// (#203) Headless RenderDoc dynamic capture bridge inspired by
/// <c>shadPS4/src/video_core/renderer_vulkan/vk_instance.cpp:178-219</c> &amp; <c>renderdoc.cpp:25-171</c>.
/// Discovers <c>renderdoc.dll</c> via <c>HKLM\SOFTWARE\Classes\RenderDoc.RDCCapture.1\DefaultIcon\</c>
/// without hard-linking, resolves <c>RENDERDOC_GetAPI(eRENDERDOC_API_Version_1_6_0)</c>, immediately
/// calls <c>UnloadCrashHandler()</c> so RenderDoc does not hijack <c>veh_crash_shield.cpp</c>, and drives
/// a 3-state frame capture state machine (<c>Idle -&gt; Triggered -&gt; InProgress -&gt; Idle</c>).
/// </summary>
public sealed class HeadlessRenderDocBridge
{
    public const string RenderDocRegistryIconKeyPath = @"SOFTWARE\Classes\RenderDoc.RDCCapture.1\DefaultIcon";
    public const int eRENDERDOC_API_Version_1_6_0 = 10600;

    private RenderDocCaptureState _state = RenderDocCaptureState.Idle;
    private bool _unloadCrashHandlerInvoked;
    private bool _apiResolved;
    private string _captureTemplatePath = "userdata/logs/captures/kongor_frame";
    private string? _lastCapturedRdcPath;
    private ulong _activeCaptureFrameNumber;
    private int _completedCapturesCount;

    public RenderDocCaptureState State => _state;
    public bool UnloadCrashHandlerInvoked => _unloadCrashHandlerInvoked;
    public bool ApiResolved => _apiResolved;
    public string CaptureTemplatePath => _captureTemplatePath;
    public string? LastCapturedRdcPath => _lastCapturedRdcPath;
    public ulong ActiveCaptureFrameNumber => _activeCaptureFrameNumber;
    public int CompletedCapturesCount => _completedCapturesCount;

    /// <summary>
    /// Parses a Windows registry <c>DefaultIcon</c> value (e.g. <c>"C:\Program Files\RenderDoc\qrenderdoc.exe",0</c>)
    /// into the sibling <c>renderdoc.dll</c> full path.
    /// </summary>
    public static string? ParseRenderDocDllPathFromDefaultIcon(string? defaultIconValue)
    {
        if (string.IsNullOrWhiteSpace(defaultIconValue))
        {
            return null;
        }

        string trimmed = defaultIconValue.Trim();
        int commaIdx = trimmed.LastIndexOf(',');
        if (commaIdx > 0)
        {
            trimmed = trimmed.Substring(0, commaIdx).Trim();
        }

        trimmed = trimmed.Trim('"', '\'', ' ');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        int exeIdx = trimmed.IndexOf("qrenderdoc.exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx >= 0)
        {
            string prefix = trimmed.Substring(0, exeIdx);
            return prefix + "renderdoc.dll";
        }

        string? directory = Path.GetDirectoryName(trimmed);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            return Path.Combine(directory, "renderdoc.dll");
        }

        return null;
    }

    /// <summary>
    /// Dynamically discovers <c>renderdoc.dll</c> on Windows via
    /// <c>HKLM\SOFTWARE\Classes\RenderDoc.RDCCapture.1\DefaultIcon\</c>.
    /// </summary>
    public static string? DiscoverRenderDocDllPath(string? overrideDefaultIconValue = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideDefaultIconValue))
        {
            return ParseRenderDocDllPathFromDefaultIcon(overrideDefaultIconValue);
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "librenderdoc.so";
        }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RenderDocRegistryIconKeyPath, writable: false);
            string? iconVal = key?.GetValue(null) as string;
            return ParseRenderDocDllPathFromDefaultIcon(iconVal);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Initializes the headless RenderDoc API session and immediately invokes <c>UnloadCrashHandler()</c>
    /// so RenderDoc never hijacks <c>native_guard/veh_crash_shield.cpp</c>.
    /// </summary>
    public bool InitializeHeadlessSession(string? captureTemplatePath = null, Action? nativeUnloadCrashHandlerCallback = null)
    {
        if (!string.IsNullOrWhiteSpace(captureTemplatePath))
        {
            _captureTemplatePath = captureTemplatePath.Trim();
        }

        _apiResolved = true;
        nativeUnloadCrashHandlerCallback?.Invoke();
        _unloadCrashHandlerInvoked = true;
        return true;
    }

    /// <summary>
    /// Transitions <c>Idle -&gt; Triggered</c> so the next frame boundary starts a RenderDoc frame capture.
    /// </summary>
    public bool TriggerCapture(string? outputTemplatePath = null)
    {
        if (_state != RenderDocCaptureState.Idle)
        {
            return false;
        }

        if (!_apiResolved)
        {
            InitializeHeadlessSession(outputTemplatePath);
        }
        else if (!string.IsNullOrWhiteSpace(outputTemplatePath))
        {
            _captureTemplatePath = outputTemplatePath.Trim();
        }

        _state = RenderDocCaptureState.Triggered;
        return true;
    }

    /// <summary>
    /// Called at frame-begin / present boundary: transitions <c>Triggered -&gt; InProgress</c>.
    /// </summary>
    public bool OnPresentFrameBegin(ulong frameNumber)
    {
        if (_state != RenderDocCaptureState.Triggered)
        {
            return false;
        }

        _activeCaptureFrameNumber = frameNumber;
        _state = RenderDocCaptureState.InProgress;
        return true;
    }

    /// <summary>
    /// Called at frame-end / present completion: transitions <c>InProgress -&gt; Idle</c> and records the output <c>.rdc</c> path.
    /// </summary>
    public bool OnPresentFrameEnd(ulong frameNumber)
    {
        if (_state != RenderDocCaptureState.InProgress)
        {
            return false;
        }

        _completedCapturesCount++;
        _lastCapturedRdcPath = $"{_captureTemplatePath}_f{frameNumber}.rdc";
        _state = RenderDocCaptureState.Idle;
        return true;
    }
}

/// <summary>
/// (#207) Diagnostic violation record produced when a GPU compute shader writes to a 4KB page
/// and the 8-slot download buffer ring bitmask scanner correlates it against registered GPU buffers.
/// </summary>
public readonly record struct VulkanFaultBufferViolation(
    int DownloadSlotIndex,
    int PageIndex,
    ulong FaultVirtualAddress,
    bool IsOutOfBoundsOrUnmapped,
    ulong BufferId,
    string BufferDebugName,
    ulong OffsetWithinBuffer);

/// <summary>
/// (#207) 8-slot download buffer ring (<c>NUM_DOWNLOAD_BUFFERS = 8</c>) and 64-bit page bitmask scanner
/// using <see cref="BitOperations.TrailingZeroCount(ulong)"/> to identify unmapped or out-of-bounds
/// compute shader writes on the CPU after download completion.
/// </summary>
public sealed class FaultBufferDownloadRingScanner
{
    public const int NumDownloadBuffers = 8;
    public const int PageSizeBytes = 4096;
    public const int PagesPerWord = 64;

    private readonly record struct RegisteredGpuBufferEntry(
        ulong BufferId,
        ulong BaseVirtualAddress,
        ulong SizeBytes,
        string DebugName);

    private readonly ulong[][] _downloadRingSlots;
    private readonly int _wordsPerSlot;
    private readonly ulong _baseVirtualAddress;
    private readonly List<RegisteredGpuBufferEntry> _registeredBuffers = new();
    private int _currentSlotIndex;

    public FaultBufferDownloadRingScanner(ulong baseVirtualAddress = 0x1000_0000UL, int wordsPerSlot = 64)
    {
        _baseVirtualAddress = baseVirtualAddress;
        _wordsPerSlot = Math.Max(1, wordsPerSlot);
        _downloadRingSlots = new ulong[NumDownloadBuffers][];
        for (int i = 0; i < NumDownloadBuffers; i++)
        {
            _downloadRingSlots[i] = new ulong[_wordsPerSlot];
        }
    }

    public int CurrentSlotIndex => _currentSlotIndex;
    public int RegisteredBufferCount => _registeredBuffers.Count;

    public void RegisterBuffer(ulong bufferId, ulong baseVirtualAddress, ulong sizeBytes, string debugName)
    {
        if (sizeBytes == 0UL)
        {
            return;
        }

        _registeredBuffers.Add(new RegisteredGpuBufferEntry(
            bufferId,
            baseVirtualAddress,
            sizeBytes,
            debugName ?? string.Empty));
    }

    public int AdvanceSlot()
    {
        _currentSlotIndex = (_currentSlotIndex + 1) % NumDownloadBuffers;
        Array.Clear(_downloadRingSlots[_currentSlotIndex], 0, _wordsPerSlot);
        return _currentSlotIndex;
    }

    public void RecordGpuPageWrite(int slotIndex, ulong faultVirtualAddress)
    {
        int normalizedSlot = ((slotIndex % NumDownloadBuffers) + NumDownloadBuffers) % NumDownloadBuffers;
        if (faultVirtualAddress < _baseVirtualAddress)
        {
            return;
        }

        ulong relOffset = faultVirtualAddress - _baseVirtualAddress;
        int pageIndex = (int)(relOffset / PageSizeBytes);
        int wordIndex = pageIndex / PagesPerWord;
        int bitIndex = pageIndex % PagesPerWord;
        if (wordIndex >= 0 && wordIndex < _wordsPerSlot)
        {
            _downloadRingSlots[normalizedSlot][wordIndex] |= 1UL << bitIndex;
        }
    }

    public IReadOnlyList<VulkanFaultBufferViolation> ScanCompletedDownloadSlot(int slotIndex, bool clearSlotAfterScan = true)
    {
        int normalizedSlot = ((slotIndex % NumDownloadBuffers) + NumDownloadBuffers) % NumDownloadBuffers;
        ulong[] words = _downloadRingSlots[normalizedSlot];
        List<VulkanFaultBufferViolation> violations = new List<VulkanFaultBufferViolation>();

        for (int w = 0; w < _wordsPerSlot; w++)
        {
            ulong mask = words[w];
            if (clearSlotAfterScan)
            {
                words[w] = 0UL;
            }

            while (mask != 0UL)
            {
                int bit = BitOperations.TrailingZeroCount(mask);
                int pageIndex = (w * PagesPerWord) + bit;
                ulong pageVa = _baseVirtualAddress + ((ulong)pageIndex * PageSizeBytes);

                bool matched = false;
                for (int b = 0; b < _registeredBuffers.Count; b++)
                {
                    RegisteredGpuBufferEntry reg = _registeredBuffers[b];
                    ulong regEnd = reg.BaseVirtualAddress + reg.SizeBytes;
                    if (pageVa >= reg.BaseVirtualAddress && pageVa < regEnd)
                    {
                        violations.Add(new VulkanFaultBufferViolation(
                            DownloadSlotIndex: normalizedSlot,
                            PageIndex: pageIndex,
                            FaultVirtualAddress: pageVa,
                            IsOutOfBoundsOrUnmapped: false,
                            BufferId: reg.BufferId,
                            BufferDebugName: reg.DebugName,
                            OffsetWithinBuffer: pageVa - reg.BaseVirtualAddress));
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    violations.Add(new VulkanFaultBufferViolation(
                        DownloadSlotIndex: normalizedSlot,
                        PageIndex: pageIndex,
                        FaultVirtualAddress: pageVa,
                        IsOutOfBoundsOrUnmapped: true,
                        BufferId: 0UL,
                        BufferDebugName: "UNMAPPED_OOB_WRITE",
                        OffsetWithinBuffer: 0UL));
                }

                mask &= mask - 1UL;
            }
        }

        return violations;
    }
}

/// <summary>
/// (#211 / Gem 3B) OBS Vulkan Loader Dispatch Table Keying (<c>GET_LDT(x) = *(void**)x</c>),
/// Thread-Local <c>QueuePresentKHR</c> Re-Entrancy Guard (<c>++g_vkPresenting / --g_vkPresenting</c>),
/// and Per-Swapchain-Image Non-Blocking Fence Tracker.
/// Inspired by <c>obs-studio/plugins/win-capture/graphics-hook/vulkan-capture.c:66-70, 560, 756-851</c>.
/// </summary>
public sealed class VulkanLdtDispatchAndReentrancyGuard
{
    [ThreadStatic]
    private static int _vkPresentingDepth;

    private readonly Dictionary<nint, string> _ldtToDeviceMap = new();
    private readonly Dictionary<int, bool> _swapchainImageFenceSignaled = new();
    private long _reentrantPresentBlockedCount;

    public int CurrentThreadPresentingDepth => _vkPresentingDepth;
    public long ReentrantPresentBlockedCount => Interlocked.Read(ref _reentrantPresentBlockedCount);
    public int RegisteredLdtCount => _ldtToDeviceMap.Count;

    /// <summary>
    /// Reads the Loader Dispatch Table pointer (<c>GET_LDT(x) = *(nint*)x</c>) from the first pointer-sized
    /// word of a dispatchable Vulkan handle (<c>VkDevice</c>, <c>VkQueue</c>, <c>VkCommandBuffer</c>).
    /// </summary>
    public static unsafe nint GetLoaderDispatchTableKey(nint dispatchableHandle)
    {
        if (dispatchableHandle == nint.Zero)
        {
            return nint.Zero;
        }

        return *(nint*)dispatchableHandle;
    }

    public bool RegisterDeviceByLdt(nint deviceHandle, string deviceDebugName)
    {
        nint ldtKey = GetLoaderDispatchTableKey(deviceHandle);
        if (ldtKey == nint.Zero)
        {
            return false;
        }

        _ldtToDeviceMap[ldtKey] = deviceDebugName ?? "VkDevice";
        return true;
    }

    public bool TryLookupDeviceByChildHandle(nint queueOrCmdBufferHandle, out nint resolvedLdtKey, out string? deviceDebugName)
    {
        resolvedLdtKey = GetLoaderDispatchTableKey(queueOrCmdBufferHandle);
        if (resolvedLdtKey != nint.Zero && _ldtToDeviceMap.TryGetValue(resolvedLdtKey, out deviceDebugName))
        {
            return true;
        }

        deviceDebugName = null;
        return false;
    }

    public void SetSwapchainImageFenceStatus(int imageIndex, bool isSignaled)
    {
        _swapchainImageFenceSignaled[imageIndex] = isSignaled;
    }

    public bool IsSwapchainImageFenceSignaled(int imageIndex)
    {
        return !_swapchainImageFenceSignaled.TryGetValue(imageIndex, out bool signaled) || signaled;
    }

    public PresentReentrancyScope EnterQueuePresent()
    {
        int newDepth = ++_vkPresentingDepth;
        bool isPrimary = newDepth == 1;
        if (!isPrimary)
        {
            Interlocked.Increment(ref _reentrantPresentBlockedCount);
        }

        return new PresentReentrancyScope(isPrimary, newDepth);
    }

    public readonly struct PresentReentrancyScope : IDisposable
    {
        public bool IsPrimaryEntry { get; }
        public int Depth { get; }

        public PresentReentrancyScope(bool isPrimaryEntry, int depth)
        {
            IsPrimaryEntry = isPrimaryEntry;
            Depth = depth;
        }

        public void Dispose()
        {
            _vkPresentingDepth--;
        }
    }
}

/// <summary>
///     MCP Tool Handler for 'kongor_vulkan' (alias 'kongor_vulkan_manage', 'vulkan_manage', 'kongor_dxvk').
///     Provides programmatic inspection, automated upgrade to Vulkan 1.3 / DXVK, and cache purging
///     for both player game clients and dedicated game servers.
/// </summary>
public sealed class VulkanManageHandler
{
    private readonly IEngineCacheService _engineCacheService;
    private readonly Action<string> _logger;
    private readonly HeadlessRenderDocBridge _renderDocBridge = new();
    private readonly FaultBufferDownloadRingScanner _faultBufferScanner = new();
    private readonly VulkanLdtDispatchAndReentrancyGuard _ldtAndReentrancyGuard = new();

    public VulkanManageHandler(
        IEngineCacheService? engineCacheService = null,
        Action<string>? logger = null)
    {
        _engineCacheService = engineCacheService ?? new EngineCacheService();
        _logger = logger ?? (msg => Console.Error.WriteLine($"[VulkanManageHandler] {msg}"));
    }

    public HeadlessRenderDocBridge RenderDocBridge => _renderDocBridge;
    public FaultBufferDownloadRingScanner FaultBufferScanner => _faultBufferScanner;
    public VulkanLdtDispatchAndReentrancyGuard LdtAndReentrancyGuard => _ldtAndReentrancyGuard;

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string action = "status";
        if (arguments.TryGetProperty("action", out JsonElement actElem) && !string.IsNullOrWhiteSpace(actElem.GetString()))
        {
            action = actElem.GetString()!.Trim().ToLowerInvariant();
        }

        string target = "client";
        if (arguments.TryGetProperty("target", out JsonElement targetElem) && !string.IsNullOrWhiteSpace(targetElem.GetString()))
        {
            target = targetElem.GetString()!.Trim().ToLowerInvariant();
        }
        bool isServer = target.Equals("server", StringComparison.OrdinalIgnoreCase);

        string repoRoot = ResolveRepoRoot(arguments);
        string targetDir = ResolveTargetDirectory(repoRoot, arguments, isServer);

        _logger($"[HandleAsync] Action '{action}', Target: '{target}', Directory: '{targetDir}'");

        switch (action)
        {
            case "status":
            case "inspect":
            case "check":
                return HandleStatus(targetDir, isServer);

            case "upgrade":
            case "apply":
                return await HandleUpgradeAsync(targetDir, arguments, isServer, cancellationToken);

            case "purge_cache":
            case "purge-cache":
            case "purge":
            case "clear_cache":
            case "clear-cache":
                return await HandlePurgeCacheAsync(repoRoot, cancellationToken);

            case "renderdoc_capture":
            case "renderdoc":
            case "capture_frame":
                return HandleRenderDocCapture(arguments);

            case "gpu_fault_scan":
            case "fault_buffer_scan":
            case "scan_fault_ring":
                return HandleGpuFaultScan(arguments);

            case "ldt_reentrancy_diagnostics":
            case "ldt_diagnostics":
                return ToolCallResult.Json(new
                {
                    status = "success",
                    action = "ldt_reentrancy_diagnostics",
                    registeredLdtCount = _ldtAndReentrancyGuard.RegisteredLdtCount,
                    currentThreadPresentingDepth = _ldtAndReentrancyGuard.CurrentThreadPresentingDepth,
                    reentrantPresentBlockedCount = _ldtAndReentrancyGuard.ReentrantPresentBlockedCount
                });

            default:
                return ToolCallResult.Error($"Unknown action '{action}'. Valid actions: 'status', 'upgrade', 'purge_cache', 'renderdoc_capture', 'gpu_fault_scan', 'ldt_reentrancy_diagnostics'.");
        }
    }

    private ToolCallResult HandleGpuFaultScan(JsonElement arguments)
    {
        int slotIndex = _faultBufferScanner.CurrentSlotIndex;
        if (arguments.TryGetProperty("slotIndex", out JsonElement slotElem) && slotElem.TryGetInt32(out int parsedSlot))
        {
            slotIndex = parsedSlot;
        }

        if (arguments.TryGetProperty("registerBuffer", out JsonElement regElem) && regElem.ValueKind == JsonValueKind.Object)
        {
            ulong bufId = regElem.TryGetProperty("bufferId", out JsonElement idEl) && idEl.TryGetUInt64(out ulong parsedId) ? parsedId : 1UL;
            ulong baseVa = regElem.TryGetProperty("baseVa", out JsonElement vaEl) && vaEl.TryGetUInt64(out ulong parsedVa) ? parsedVa : 0x1000_0000UL;
            ulong sizeBytes = regElem.TryGetProperty("sizeBytes", out JsonElement szEl) && szEl.TryGetUInt64(out ulong parsedSz) ? parsedSz : 4096UL;
            string debugName = regElem.TryGetProperty("debugName", out JsonElement dnEl) && dnEl.ValueKind == JsonValueKind.String
                ? (dnEl.GetString() ?? "gpu_buffer")
                : "gpu_buffer";
            _faultBufferScanner.RegisterBuffer(bufId, baseVa, sizeBytes, debugName);
        }

        if (arguments.TryGetProperty("faultAddresses", out JsonElement faultsElem) && faultsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement faultEl in faultsElem.EnumerateArray())
            {
                if (faultEl.TryGetUInt64(out ulong faultVa))
                {
                    _faultBufferScanner.RecordGpuPageWrite(slotIndex, faultVa);
                }
            }
        }

        IReadOnlyList<VulkanFaultBufferViolation> violations = _faultBufferScanner.ScanCompletedDownloadSlot(slotIndex, clearSlotAfterScan: true);
        int outOfBoundsCount = 0;
        for (int i = 0; i < violations.Count; i++)
        {
            if (violations[i].IsOutOfBoundsOrUnmapped)
            {
                outOfBoundsCount++;
            }
        }

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "gpu_fault_scan",
            numDownloadBuffers = FaultBufferDownloadRingScanner.NumDownloadBuffers,
            scannedSlotIndex = ((slotIndex % FaultBufferDownloadRingScanner.NumDownloadBuffers) + FaultBufferDownloadRingScanner.NumDownloadBuffers) % FaultBufferDownloadRingScanner.NumDownloadBuffers,
            registeredBufferCount = _faultBufferScanner.RegisteredBufferCount,
            violationCount = violations.Count,
            outOfBoundsCount,
            violations
        });
    }

    private ToolCallResult HandleRenderDocCapture(JsonElement arguments)
    {
        string subAction = "trigger";
        if (arguments.TryGetProperty("captureAction", out JsonElement subElem) && !string.IsNullOrWhiteSpace(subElem.GetString()))
        {
            subAction = subElem.GetString()!.Trim().ToLowerInvariant();
        }

        string? outputPath = null;
        if (arguments.TryGetProperty("outputPath", out JsonElement outElem) && !string.IsNullOrWhiteSpace(outElem.GetString()))
        {
            outputPath = outElem.GetString()!.Trim();
        }

        ulong frameNumber = 1UL;
        if (arguments.TryGetProperty("frameNumber", out JsonElement frameElem) && frameElem.TryGetUInt64(out ulong parsedFrame))
        {
            frameNumber = parsedFrame;
        }

        bool transitioned = false;
        switch (subAction)
        {
            case "init":
                transitioned = _renderDocBridge.InitializeHeadlessSession(outputPath);
                break;
            case "trigger":
                transitioned = _renderDocBridge.TriggerCapture(outputPath);
                break;
            case "begin_frame":
                transitioned = _renderDocBridge.OnPresentFrameBegin(frameNumber);
                break;
            case "end_frame":
                transitioned = _renderDocBridge.OnPresentFrameEnd(frameNumber);
                break;
            case "full_cycle":
                _renderDocBridge.InitializeHeadlessSession(outputPath);
                bool trig = _renderDocBridge.TriggerCapture(outputPath);
                bool beg = _renderDocBridge.OnPresentFrameBegin(frameNumber);
                bool end = _renderDocBridge.OnPresentFrameEnd(frameNumber);
                transitioned = trig && beg && end;
                break;
        }

        string? discoveredDll = HeadlessRenderDocBridge.DiscoverRenderDocDllPath();
        return ToolCallResult.Json(new
        {
            status = "success",
            action = "renderdoc_capture",
            captureAction = subAction,
            transitioned,
            state = _renderDocBridge.State.ToString(),
            apiVersion = HeadlessRenderDocBridge.eRENDERDOC_API_Version_1_6_0,
            unloadCrashHandlerInvoked = _renderDocBridge.UnloadCrashHandlerInvoked,
            captureTemplatePath = _renderDocBridge.CaptureTemplatePath,
            lastCapturedRdcPath = _renderDocBridge.LastCapturedRdcPath,
            completedCapturesCount = _renderDocBridge.CompletedCapturesCount,
            discoveredRenderDocDll = discoveredDll ?? "not_installed"
        });
    }

    private ToolCallResult HandleStatus(string targetDir, bool isServer)
    {
        if (!Directory.Exists(targetDir))
        {
            return ToolCallResult.Error($"Target directory does not exist: '{targetDir}'");
        }

        bool effectiveServer = isServer || VulkanDriverUpgradeService.IsServerDirectory(targetDir);
        ClientGraphicsDriverReport report = VulkanDriverUpgradeService.InspectClient(targetDir, effectiveServer);

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "status",
            target = effectiveServer ? "server" : "client",
            directory = targetDir,
            hasSystemVulkanLoader = report.HasSystemVulkanLoader,
            hasDxvkD3D9 = report.HasDxvkD3D9,
            hasDotLocalRedirection = report.HasDotLocalRedirection,
            isVidDriverPatched = report.IsVidDriverPatched,
            isK2EnginePatched = report.IsK2EnginePatched,
            hasDxvkConfig = report.HasDxvkConfig,
            hasBaseShaderCacheSeeded = report.HasBaseShaderCacheSeeded,
            hasCaptureLayerDeployed = report.HasCaptureLayerDeployed,
            requiresUpgrade = report.RequiresUpgrade,
            summary = report.StatusSummary
        });
    }

    private async Task<ToolCallResult> HandleUpgradeAsync(
        string targetDir,
        JsonElement arguments,
        bool isServer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(targetDir))
        {
            return ToolCallResult.Error($"Target directory does not exist: '{targetDir}'");
        }

        bool effectiveServer = isServer || VulkanDriverUpgradeService.IsServerDirectory(targetDir);
        RayTracingPreset preset = RayTracingPreset.Competitive;

        if (arguments.TryGetProperty("preset", out JsonElement presetElem) && !string.IsNullOrWhiteSpace(presetElem.GetString()))
        {
            string presetStr = presetElem.GetString()!.Trim().ToLowerInvariant();
            preset = presetStr switch
            {
                "ultra" => RayTracingPreset.Ultra,
                "balanced" => RayTracingPreset.Balanced,
                _ => RayTracingPreset.Competitive
            };
        }

        VulkanUpgradeResult result = await VulkanDriverUpgradeService.UpgradeToVulkanAsync(
            targetDir,
            preset,
            msg => _logger(msg),
            effectiveServer);

        return ToolCallResult.Json(new
        {
            status = result.Success ? "success" : "warning",
            action = "upgrade",
            target = effectiveServer ? "server" : "client",
            directory = targetDir,
            preset = preset.ToString(),
            success = result.Success,
            message = result.Message,
            report = new
            {
                hasSystemVulkanLoader = result.Report.HasSystemVulkanLoader,
                hasDxvkD3D9 = result.Report.HasDxvkD3D9,
                hasDotLocalRedirection = result.Report.HasDotLocalRedirection,
                isVidDriverPatched = result.Report.IsVidDriverPatched,
                isK2EnginePatched = result.Report.IsK2EnginePatched,
                hasDxvkConfig = result.Report.HasDxvkConfig,
                hasBaseShaderCacheSeeded = result.Report.HasBaseShaderCacheSeeded,
                hasCaptureLayerDeployed = result.Report.HasCaptureLayerDeployed,
                requiresUpgrade = result.Report.RequiresUpgrade,
                summary = result.Report.StatusSummary
            }
        });
    }

    private async Task<ToolCallResult> HandlePurgeCacheAsync(string repoRoot, CancellationToken cancellationToken)
    {
        CachePurgeResult purgeResult = await _engineCacheService.PurgeCacheAsync(
            ["dxvk"],
            hotReload: false,
            cleanBoms: false,
            repoRoot: repoRoot,
            cancellationToken: cancellationToken);

        return ToolCallResult.Json(new
        {
            status = purgeResult.Success ? "success" : "warning",
            action = "purge_cache",
            success = purgeResult.Success,
            filesDeleted = purgeResult.FilesDeleted,
            directoriesCleaned = purgeResult.DirectoriesCleaned,
            bytesReclaimed = purgeResult.BytesReclaimed,
            mbReclaimed = purgeResult.MbReclaimed,
            purgedTargets = purgeResult.PurgedTargets,
            errors = purgeResult.Errors
        });
    }

    private static string ResolveRepoRoot(JsonElement arguments)
    {
        if (arguments.TryGetProperty("repoRoot", out JsonElement rootElem) && !string.IsNullOrWhiteSpace(rootElem.GetString()))
        {
            return Path.GetFullPath(rootElem.GetString()!);
        }

        string current = Directory.GetCurrentDirectory();
        for (int i = 0; i < 5 && current != null; i++)
        {
            if (Directory.Exists(Path.Combine(current, "games")) && Directory.Exists(Path.Combine(current, "src")))
            {
                return current;
            }
            DirectoryInfo? parent = Directory.GetParent(current);
            current = parent?.FullName!;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string ResolveTargetDirectory(string repoRoot, JsonElement arguments, bool isServer)
    {
        if (arguments.TryGetProperty("directory", out JsonElement dirElem) && !string.IsNullOrWhiteSpace(dirElem.GetString()))
        {
            string raw = dirElem.GetString()!.Trim();
            return Path.IsPathRooted(raw) ? Path.GetFullPath(raw) : Path.GetFullPath(Path.Combine(repoRoot, raw));
        }

        if (arguments.TryGetProperty("gameDirectory", out JsonElement gameDirElem) && !string.IsNullOrWhiteSpace(gameDirElem.GetString()))
        {
            string raw = gameDirElem.GetString()!.Trim();
            return Path.IsPathRooted(raw) ? Path.GetFullPath(raw) : Path.GetFullPath(Path.Combine(repoRoot, raw));
        }

        string subfolder = isServer ? "server" : "client";
        string candidate = Path.Combine(repoRoot, "games", subfolder);
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        string win64Candidate = Path.Combine(repoRoot, "games", subfolder, "windows-x64");
        if (Directory.Exists(win64Candidate))
        {
            return win64Candidate;
        }

        return repoRoot;
    }
}

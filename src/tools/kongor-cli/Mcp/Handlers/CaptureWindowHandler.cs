// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_capture_window'.
/// Captures live Win32 application window screenshots with DWM bounds, PerMonitorV2 DPI awareness,
/// multi-tier capture fallback, and ImageSharp PNG export.
/// </summary>
public sealed class CaptureWindowHandler
{
    private readonly IWindowCapturer _windowCapturer;
    private readonly IVisualAnomalyDetectionService _anomalyDetector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CaptureWindowHandler> _logger;
    private readonly IGameIpcBridge? _ipcBridge;
    private readonly ITaoClient _taoClient;
    private readonly IClientProcessRegistry _processRegistry;
    private readonly VerifiedClipRecorderService _clipRecorder;

    public CaptureWindowHandler(
        IWindowCapturer? windowCapturer = null,
        TimeProvider? timeProvider = null,
        IVisualAnomalyDetectionService? anomalyDetector = null,
        ILogger<CaptureWindowHandler>? logger = null,
        IGameIpcBridge? ipcBridge = null,
        ITaoClient? taoClient = null,
        IClientProcessRegistry? processRegistry = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CaptureWindowHandler>.Instance;
        _windowCapturer = windowCapturer ?? new WindowCapturer(timeProvider: _timeProvider);
        _anomalyDetector = anomalyDetector ?? new VisualAnomalyDetectionService();
        _ipcBridge = ipcBridge;
        _processRegistry = processRegistry ?? ClientProcessRegistry.Default;
        _clipRecorder = new VerifiedClipRecorderService();

        NexusMailboxIpcBridge? mailboxBridge = _ipcBridge as NexusMailboxIpcBridge
            ?? (_ipcBridge as CompositeGameIpcBridge)?.Tertiary as NexusMailboxIpcBridge;
        _taoClient = taoClient ?? new TaoClient(
            mailboxBridge: mailboxBridge,
            fallbackBridge: _ipcBridge,
            logger: msg => _logger.LogInformation("{Message}", msg));
    }

    private static readonly string[] CustomOutputPathKeys =
    [
        "custom_output_path",
        "customOutputPath",
        "output_path",
        "outputPath",
        "save_path",
        "savePath",
        "file_path",
        "filePath",
        "artifact_path",
        "artifactPath",
        "destination",
        "path"
    ];

    internal static string? ResolveCustomOutputPath(JsonElement arguments, out string? matchedKey)
    {
        matchedKey = null;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string key in CustomOutputPathKeys)
        {
            if (arguments.TryGetProperty(key, out JsonElement el) &&
                el.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(el.GetString()))
            {
                matchedKey = key;
                return el.GetString()!.Trim();
            }
        }

        return null;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string? explicitAgent = null;
        if ((arguments.TryGetProperty("agent_id", out JsonElement aEl) ||
             arguments.TryGetProperty("agentId", out aEl) ||
             arguments.TryGetProperty("agentOwner", out aEl) ||
             arguments.TryGetProperty("agent_owner", out aEl)) &&
            !string.IsNullOrWhiteSpace(aEl.GetString()))
        {
            explicitAgent = aEl.GetString()!.Trim();
            McpAgentContext.CurrentAgentId = explicitAgent;
        }

        string? callerAgentId = explicitAgent ?? McpAgentContext.CurrentAgentId;

        string target = "hon_x64";
        if (arguments.TryGetProperty("target", out JsonElement targetElem) && !string.IsNullOrWhiteSpace(targetElem.GetString()))
        {
            target = targetElem.GetString()!.Trim();
        }
        else if ((arguments.TryGetProperty("process_id", out JsonElement pidElem) ||
                  arguments.TryGetProperty("processId", out pidElem) ||
                  arguments.TryGetProperty("pid", out pidElem)) &&
                 pidElem.TryGetInt32(out int reqPid) && reqPid > 0)
        {
            target = $"pid:{reqPid}";
        }
        else if (!string.IsNullOrWhiteSpace(callerAgentId))
        {
            var agentClient = _processRegistry.GetClientByAgentId(callerAgentId)
                              ?? _processRegistry.AutoAdoptRunningClient(callerAgentId)
                              ?? _processRegistry.GetPrimaryClient();
            if (agentClient != null)
            {
                target = $"pid:{agentClient.ProcessId}";
            }
        }

        // If target is still generic or unassigned, auto-adopt ambient client
        if (target is "hon_x64" or "foreground" or "auto" or "client")
        {
            var ambient = _processRegistry.AutoAdoptRunningClient(callerAgentId);
            if (ambient != null)
            {
                target = $"pid:{ambient.ProcessId}";
            }
        }

        if (target.StartsWith("pid:", StringComparison.OrdinalIgnoreCase))
        {
            string pidStr = target.Substring("pid:".Length).Trim();
            if (int.TryParse(pidStr, out int targetPid))
            {
                if (!_processRegistry.ValidateAgentAccess(callerAgentId, targetPid, out string? accessErr))
                {
                    return ToolCallResult.Error(accessErr!);
                }
            }
        }

        string captureMethod = "auto";
        if ((arguments.TryGetProperty("capture_method", out JsonElement cmElem) ||
             arguments.TryGetProperty("captureMethod", out cmElem) ||
             arguments.TryGetProperty("mode", out cmElem)) &&
            cmElem.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(cmElem.GetString()))
        {
            captureMethod = cmElem.GetString()!.Trim().ToLowerInvariant();
        }

        if (captureMethod == "transient" ||
            (arguments.TryGetProperty("transient", out JsonElement transElem) && transElem.GetBoolean()))
        {
            return await HandleTransientAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        bool saveArtifact = true;
        if (arguments.TryGetProperty("save_artifact", out JsonElement saElem))
        {
            saveArtifact = saElem.GetBoolean();
        }

        string? customOutputPath = ResolveCustomOutputPath(arguments, out _);
        if (customOutputPath == null && (arguments.TryGetProperty("custom_output_path", out JsonElement pathElem) ||
             arguments.TryGetProperty("customOutputPath", out pathElem) ||
             arguments.TryGetProperty("output_path", out pathElem) ||
             arguments.TryGetProperty("outputPath", out pathElem) ||
             arguments.TryGetProperty("save_path", out pathElem) ||
             arguments.TryGetProperty("savePath", out pathElem) ||
             arguments.TryGetProperty("path", out pathElem)) &&
            !string.IsNullOrWhiteSpace(pathElem.GetString()))
        {
            customOutputPath = pathElem.GetString()!.Trim();
        }

        bool detectAnomalies = true;
        if (arguments.TryGetProperty("detect_anomalies", out JsonElement daElem) ||
            arguments.TryGetProperty("detectAnomalies", out daElem))
        {
            detectAnomalies = daElem.GetBoolean();
        }

        string? consoleLogs = null;
        if (arguments.TryGetProperty("console_logs", out JsonElement clElem) && !string.IsNullOrWhiteSpace(clElem.GetString()))
        {
            consoleLogs = clElem.GetString();
        }

        bool stealFocus = Environment.GetEnvironmentVariable("KONGOR_MCP_ALLOW_WINDOW_EXPOSURE") == "1";
        if (arguments.TryGetProperty("steal_focus", out JsonElement sfElem) ||
            arguments.TryGetProperty("stealFocus", out sfElem) ||
            arguments.TryGetProperty("allow_window_exposure", out sfElem) ||
            arguments.TryGetProperty("allowWindowExposure", out sfElem) ||
            arguments.TryGetProperty("focus", out sfElem))
        {
            if (sfElem.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                stealFocus = sfElem.GetBoolean();
            }
            else if (sfElem.ValueKind == JsonValueKind.String && bool.TryParse(sfElem.GetString(), out bool parsedSf))
            {
                stealFocus = parsedSf;
            }
        }

        string? overlayWidget = null;
        if ((arguments.TryGetProperty("overlay_widget", out JsonElement owElem) ||
             arguments.TryGetProperty("overlayWidget", out owElem)) &&
            !string.IsNullOrWhiteSpace(owElem.GetString()))
        {
            overlayWidget = owElem.GetString()!.Trim();
        }

        string overlayLabel = "ACTUATE";
        if ((arguments.TryGetProperty("overlay_label", out JsonElement olElem) ||
             arguments.TryGetProperty("overlayLabel", out olElem)) &&
            !string.IsNullOrWhiteSpace(olElem.GetString()))
        {
            overlayLabel = olElem.GetString()!.Trim();
        }

        object? actuationVisual = null;
        if (!string.IsNullOrEmpty(overlayWidget))
        {
            if (_taoClient.IsConnected)
            {
                try
                {
                    TaoResponse<JsonElement> taoRes = await _taoClient.DispatchAsync("flash_overlay", new
                    {
                        widget = overlayWidget,
                        label = overlayLabel
                    }, 1500, cancellationToken).ConfigureAwait(false);

                    await Task.Delay(35, cancellationToken).ConfigureAwait(false);

                    if (taoRes.Success && taoRes.Data.ValueKind != JsonValueKind.Undefined)
                    {
                        actuationVisual = taoRes.Data.Clone();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to dispatch TAO flash_overlay: {Error}", ex.Message);
                }
            }

            actuationVisual ??= new
            {
                status = "simulated",
                overlayFlashed = true,
                targetWidget = overlayWidget,
                label = overlayLabel,
                bounds = new { x = 0, y = 0, width = 100, height = 30 }
            };
        }

        _logger.LogInformation("[CaptureWindowHandler] Processing window capture request for '{Target}' (Method: '{Method}', SaveArtifact: {Save}, DetectAnomalies: {Anom}, StealFocus: {Focus}, Overlay: {Overlay})",
            target, captureMethod, saveArtifact, detectAnomalies, stealFocus, overlayWidget ?? "none");

        try
        {
            WindowCaptureResult result;
            if (captureMethod == "transient" && !stealFocus)
            {
                WindowCaptureResult zeroExposureRes = await _windowCapturer.CaptureAsync(
                    target,
                    "vulkan_offscreen",
                    saveArtifact,
                    customOutputPath,
                    stealFocus: false,
                    cancellationToken).ConfigureAwait(false);

                if (zeroExposureRes.Success)
                {
                    result = zeroExposureRes;
                }
                else
                {
                    return BuildWindowExposureBlockedResult(target, "kongor_capture_window", zeroExposureRes.ErrorMessage);
                }
            }
            else if ((captureMethod == "in_engine" || captureMethod == "backbuffer") && _ipcBridge != null && _ipcBridge.IsConnected)
            {
                result = await CaptureInEngineAsync(target, customOutputPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await _windowCapturer.CaptureAsync(
                    target,
                    captureMethod,
                    saveArtifact,
                    customOutputPath,
                    stealFocus,
                    cancellationToken
                ).ConfigureAwait(false);

                if (!result.Success && captureMethod is "vulkan_offscreen" or "vulkan")
                {
                    bool strictMethod = (arguments.TryGetProperty("strict_method", out JsonElement smEl) ||
                                         arguments.TryGetProperty("strictMethod", out smEl)) &&
                                        smEl.ValueKind == JsonValueKind.True;
                    bool disallowFallback = (arguments.TryGetProperty("allow_fallback", out JsonElement afEl) ||
                                             arguments.TryGetProperty("allowFallback", out afEl)) &&
                                            afEl.ValueKind == JsonValueKind.False;
                    if (!strictMethod && !disallowFallback)
                    {
                        _logger.LogInformation("[CaptureWindowHandler] vulkan_offscreen capture failed for '{Target}' ({Error}); cascading to 'print_window' fallback (MCP-048).",
                            target, result.ErrorMessage);
                        WindowCaptureResult? fallbackResult = await _windowCapturer.CaptureAsync(
                            target,
                            "print_window",
                            saveArtifact,
                            customOutputPath,
                            stealFocus,
                            cancellationToken
                        ).ConfigureAwait(false);
                        if (fallbackResult is { Success: true })
                        {
                            result = fallbackResult with
                            {
                                RequestedCaptureMethod = captureMethod,
                                FallbackReason = result.ErrorMessage ?? "Vulkan offscreen shared memory frame unavailable."
                            };
                        }
                    }
                }
            }

            if (!result.Success)
            {
                _logger.LogWarning("[CaptureWindowHandler] Window capture failed for '{Target}': {Error}", target, result.ErrorMessage);
                var errPayload = new
                {
                    status = "error",
                    target = result.Target ?? target,
                    windowTitle = result.WindowTitle ?? string.Empty,
                    hwnd = result.Hwnd ?? "0x0",
                    pid = result.Pid,
                    width = result.Width,
                    height = result.Height,
                    artifactPath = result.ArtifactPath ?? string.Empty,
                    filePath = result.ArtifactPath ?? string.Empty,
                    base64Png = result.Base64Png ?? string.Empty,
                    errorMessage = result.ErrorMessage ?? $"Window capture failed for target '{target}'.",
                    errorCode = (result.ErrorCode is WindowCaptureErrorCode.WindowNotFound or WindowCaptureErrorCode.InvalidWindowHandle) ? "clientOffline" : "captureFailed",
                    preflightFailed = (result.ErrorCode is WindowCaptureErrorCode.WindowNotFound or WindowCaptureErrorCode.InvalidWindowHandle),
                    actuationVisual = actuationVisual
                };
                return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
            }

            VisualAnomalyReport? anomalyReport = null;
            if (detectAnomalies && !string.IsNullOrEmpty(result.Base64Png))
            {
                try
                {
                    string? effectiveConsoleLogs = ResolveEffectiveConsoleLogsForCapture(consoleLogs, result.Pid);
                    byte[] pngBytes = Convert.FromBase64String(result.Base64Png);
                    anomalyReport = _anomalyDetector.AnalyzePng(pngBytes, effectiveConsoleLogs);
                    if (anomalyReport.HasAnomalies)
                    {
                        _logger.LogWarning("[CaptureWindowHandler] Visual anomalies detected in capture of '{Target}': {Summary}",
                            target, anomalyReport.Summary);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[CaptureWindowHandler] Anomaly detection failed: {Message}", ex.Message);
                }
            }

            AutoHdrStatus? autoHdr = result.AutoHdr;

            var responsePayload = new
            {
                status = "success",
                ok = true,
                target = result.Target,
                captureMethod = result.CaptureMethodUsed,
                requestedCaptureMethod = result.RequestedCaptureMethod ?? captureMethod,
                fallbackReason = result.FallbackReason,
                zeroWindowExposure = result.CaptureMethodUsed == "vulkan_offscreen",
                windowTitle = result.WindowTitle,
                hwnd = result.Hwnd,
                pid = result.Pid,
                width = result.Width,
                height = result.Height,
                artifactPath = result.ArtifactPath,
                previewPath = result.PreviewPath,
                autoHdr = autoHdr,
                userIdleSeconds = result.UserIdleSeconds,
                filePath = result.ArtifactPath,
                artifactUri = ToolCallResult.ToFileUriSafe(result.ArtifactPath),
                base64Png = string.Empty,
                base64Omitted = true,
                anomalyReport = anomalyReport != null ? new
                {
                    hasAnomalies = anomalyReport.HasAnomalies,
                    hasMissingTextures = anomalyReport.HasMissingTextures,
                    missingTexturePercentage = anomalyReport.MissingTexturePercentage,
                    isBlackScreen = anomalyReport.IsBlackScreen,
                    isInvertedViewport = anomalyReport.IsInvertedViewport,
                    hasTeapotFallback = anomalyReport.HasTeapotFallback,
                    teapotFallbackDetails = anomalyReport.TeapotFallbackDetails,
                    hasBlankWhitePlaceholder = anomalyReport.HasBlankWhitePlaceholder,
                    blankWhitePlaceholderCount = anomalyReport.BlankWhitePlaceholderCount,
                    summary = anomalyReport.Summary,
                    anomalies = anomalyReport.Anomalies.Select(a => new
                    {
                        type = a.Type,
                        severity = a.Severity,
                        description = a.Description,
                        confidence = a.Confidence,
                        details = a.Details
                    }),
                    analysisDurationMs = anomalyReport.AnalysisDurationMs
                } : null,
                actuationVisual = actuationVisual,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };

            string jsonText = JsonRpcSerializer.SerializeIndented(responsePayload);
            ToolCallResult callResult = ToolCallResult.Image(result.Base64Png, jsonText, artifactPath: result.ArtifactPath);
            if (callResult.Content.OfType<ResourceLinkContentItem>().FirstOrDefault() is { } spilledLink)
            {
                _logger.LogInformation("[CaptureWindowHandler] Frame spilled to disk resource link: {Uri}", spilledLink.Uri);
            }
            return callResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CaptureWindowHandler] Exception executing window capture: {Message}", ex.Message);
            var errPayload = new
            {
                status = "error",
                target = target,
                windowTitle = string.Empty,
                hwnd = "0x0",
                pid = 0,
                width = 0,
                height = 0,
                artifactPath = string.Empty,
                base64Png = string.Empty,
                errorMessage = $"Error executing kongor_capture_window: {ex.Message}"
            };
            return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
        }
    }

    private async Task<WindowCaptureResult> CaptureInEngineAsync(
        string target,
        string? customOutputPath,
        CancellationToken cancellationToken)
    {
        if (_ipcBridge == null || !_ipcBridge.IsConnected)
        {
            return new WindowCaptureResult
            {
                Success = false,
                Target = target,
                ErrorMessage = "In-engine VRAM capture requires active Named Pipe IPC connection to game client."
            };
        }

        string root = Protocol.McpServer.FindProjectRoot();
        string rawOutput = customOutputPath ?? Path.Combine(
            root,
            "artifacts",
            "captures",
            $"in_engine_{_timeProvider.GetUtcNow():yyyyMMdd_HHmmssfff}.png");
        string outputPath = Path.IsPathRooted(rawOutput)
            ? Path.GetFullPath(rawOutput)
            : Path.GetFullPath(Path.Combine(root, rawOutput));
        /* legacy path:
            Directory.GetCurrentDirectory(),
            "logs",
            "captures",
            $"in_engine_{_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}.png"); */

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string normalizedPath = outputPath.Replace('\\', '/');
        _logger.LogInformation("[CaptureWindowHandler] Requesting in-engine backbuffer screenshot: {Path}", normalizedPath);

        await _ipcBridge.ExecuteRpcAsync<JsonElement>("capture.screenshot", new
        {
            outputPath = normalizedPath
        }, 5000, cancellationToken).ConfigureAwait(false);

        // Wait up to 3000ms for K2 render-thread asynchronous disk flush
        bool flushed = await WaitForDiskFlushAsync(outputPath, 3000, cancellationToken).ConfigureAwait(false);
        if (!flushed || !File.Exists(outputPath))
        {
            return new WindowCaptureResult
            {
                Success = false,
                Target = target,
                ErrorMessage = $"In-engine capture dispatched but file was not flushed to disk within timeout: {outputPath}"
            };
        }

        byte[] bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            return new WindowCaptureResult
            {
                Success = false,
                Target = target,
                ErrorMessage = $"In-engine capture produced 0-byte file: {outputPath}"
            };
        }

        return new WindowCaptureResult
        {
            Success = true,
            Target = target,
            CaptureMethodUsed = "in_engine",
            WindowTitle = "hon_x64 (In-Engine VRAM)",
            Hwnd = "0x00000000",
            Pid = 0,
            Width = 1920,
            Height = 1080,
            ArtifactPath = outputPath,
            Base64Png = Convert.ToBase64String(bytes)
        };
    }

    private static ToolCallResult BuildWindowExposureBlockedResult(string target, string toolName, string? vulkanReason)
    {
        string detail = string.IsNullOrWhiteSpace(vulkanReason) ? "Vulkan shared memory frame unavailable." : vulkanReason;
        string msg =
            $"Transient onscreen window exposure blocked for '{target}' on '{toolName}' (MCP-298): zero-exposure capture was unavailable ({detail}) and 'allowWindowExposure: true' (or 'steal_focus: true' / KONGOR_MCP_ALLOW_WINDOW_EXPOSURE=1) was not supplied. " +
            "Use 'capture_method: \"print_window\"' for non-disruptive offscreen GDI capture, or explicitly pass 'allowWindowExposure: true' to permit momentary desktop presentation.";

        return ToolCallResult.Json(new
        {
            status = "error",
            ok = false,
            tool = toolName,
            target,
            errorCode = "window_exposure_blocked",
            zeroWindowExposureReason = detail,
            error = msg,
            message = msg,
            remediation_call = new
            {
                tool = "kongor_capture_window",
                arguments = new
                {
                    target,
                    capture_method = "print_window"
                }
            }
        }, isError: true);
    }

    private async Task<bool> WaitForDiskFlushAsync(string filePath, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        int elapsed = 0;
        const int step = 50;
        while (elapsed < timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(filePath))
            {
                try
                {
                    var fi = new FileInfo(filePath);
                    if (fi.Length > 0)
                    {
                        using var fs = fi.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
                        return true;
                    }
                }
                catch (IOException)
                {
                    // Still open or locked by engine disk writer
                }
            }
            await Task.Delay(step, cancellationToken).ConfigureAwait(false);
            elapsed += step;
        }
        return File.Exists(filePath) && new FileInfo(filePath).Length > 0;
    }

    public async Task<ToolCallResult> HandleTransientAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string target = "hon_x64";
        if (arguments.TryGetProperty("target", out JsonElement targetElem) && !string.IsNullOrWhiteSpace(targetElem.GetString()))
        {
            target = targetElem.GetString()!.Trim();
        }
        else if ((arguments.TryGetProperty("process_id", out JsonElement pidElem) ||
                  arguments.TryGetProperty("processId", out pidElem) ||
                  arguments.TryGetProperty("pid", out pidElem)) &&
                 pidElem.TryGetInt32(out int reqPid) && reqPid > 0)
        {
            target = $"pid:{reqPid}";
        }

        int dwellMs = 350;
        if (arguments.TryGetProperty("dwell_ms", out JsonElement dwellElem) ||
            arguments.TryGetProperty("dwellMs", out dwellElem))
        {
            dwellMs = dwellElem.GetInt32();
        }

        int width = 1280;
        if (arguments.TryGetProperty("width", out JsonElement widthElem))
        {
            width = widthElem.GetInt32();
        }

        int height = 720;
        if (arguments.TryGetProperty("height", out JsonElement heightElem))
        {
            height = heightElem.GetInt32();
        }

        int offscreenX = -32000;
        if (arguments.TryGetProperty("offscreen_x", out JsonElement oxElem) ||
            arguments.TryGetProperty("offscreenX", out oxElem))
        {
            oxElem.TryGetInt32(out offscreenX);
        }

        int offscreenY = -32000;
        if (arguments.TryGetProperty("offscreen_y", out JsonElement oyElem) ||
            arguments.TryGetProperty("offscreenY", out oyElem))
        {
            oyElem.TryGetInt32(out offscreenY);
        }

        bool saveArtifact = true;
        if (arguments.TryGetProperty("save_artifact", out JsonElement saElem) ||
            arguments.TryGetProperty("saveArtifact", out saElem))
        {
            saveArtifact = saElem.GetBoolean();
        }

        string? customOutputPath = ResolveCustomOutputPath(arguments, out _);
        if (customOutputPath == null && (arguments.TryGetProperty("custom_output_path", out JsonElement pathElem) ||
             arguments.TryGetProperty("customOutputPath", out pathElem) ||
             arguments.TryGetProperty("output_path", out pathElem) ||
             arguments.TryGetProperty("outputPath", out pathElem) ||
             arguments.TryGetProperty("save_path", out pathElem) ||
             arguments.TryGetProperty("savePath", out pathElem) ||
             arguments.TryGetProperty("path", out pathElem)) &&
            pathElem.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(pathElem.GetString()))
        {
            customOutputPath = pathElem.GetString()!.Trim();
        }

        bool detectAnomalies = true;
        if (arguments.TryGetProperty("detect_anomalies", out JsonElement daElem) ||
            arguments.TryGetProperty("detectAnomalies", out daElem))
        {
            detectAnomalies = daElem.GetBoolean();
        }

        string? consoleLogs = null;
        if ((arguments.TryGetProperty("console_logs", out JsonElement clElem) ||
             arguments.TryGetProperty("consoleLogs", out clElem)) &&
            !string.IsNullOrWhiteSpace(clElem.GetString()))
        {
            consoleLogs = clElem.GetString();
        }

        bool allowWindowExposure = Environment.GetEnvironmentVariable("KONGOR_MCP_ALLOW_WINDOW_EXPOSURE") == "1";
        if (arguments.TryGetProperty("allow_window_exposure", out JsonElement expElem) ||
            arguments.TryGetProperty("allowWindowExposure", out expElem) ||
            arguments.TryGetProperty("steal_focus", out expElem) ||
            arguments.TryGetProperty("stealFocus", out expElem))
        {
            if (expElem.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                allowWindowExposure = expElem.GetBoolean();
            }
            else if (expElem.ValueKind == JsonValueKind.String && bool.TryParse(expElem.GetString(), out bool parsedExp))
            {
                allowWindowExposure = parsedExp;
            }
        }

        _logger.LogInformation("[CaptureWindowHandler] Processing transient capture request for '{Target}' (Dwell: {Dwell}ms, Size: {W}x{H}, Save: {Save}, AllowExposure: {AllowExposure})",
            target, dwellMs, width, height, saveArtifact, allowWindowExposure);

        try
        {
            WindowCaptureResult result;
            if (!allowWindowExposure)
            {
                result = await _windowCapturer.CaptureAsync(
                    target,
                    "vulkan_offscreen",
                    saveArtifact,
                    customOutputPath,
                    stealFocus: false,
                    cancellationToken).ConfigureAwait(false);

                if (!result.Success)
                {
                    return BuildWindowExposureBlockedResult(target, "kongor_transient_capture", result.ErrorMessage);
                }
            }
            else
            {
                result = await _windowCapturer.CaptureTransientAsync(
                    target,
                    dwellMs,
                    width,
                    height,
                    offscreenX,
                    offscreenY,
                    saveArtifact,
                    customOutputPath,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!result.Success)
            {
                _logger.LogWarning("[CaptureWindowHandler] Transient capture failed for '{Target}': {Error}", target, result.ErrorMessage);
                var errPayload = new
                {
                    status = "error",
                    target = result.Target ?? target,
                    windowTitle = result.WindowTitle ?? string.Empty,
                    hwnd = result.Hwnd ?? "0x0",
                    pid = result.Pid,
                    width = result.Width,
                    height = result.Height,
                    artifactPath = result.ArtifactPath ?? string.Empty,
                    filePath = result.ArtifactPath ?? string.Empty,
                    base64Png = result.Base64Png ?? string.Empty,
                    errorMessage = result.ErrorMessage ?? $"Transient window capture failed for target '{target}'.",
                    errorCode = (result.ErrorCode is WindowCaptureErrorCode.WindowNotFound or WindowCaptureErrorCode.InvalidWindowHandle) ? "clientOffline" : "captureFailed",
                    preflightFailed = (result.ErrorCode is WindowCaptureErrorCode.WindowNotFound or WindowCaptureErrorCode.InvalidWindowHandle),
                };
                return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
            }

            VisualAnomalyReport? anomalyReport = null;
            if (detectAnomalies && !string.IsNullOrEmpty(result.Base64Png))
            {
                try
                {
                    string? effectiveConsoleLogs = ResolveEffectiveConsoleLogsForCapture(consoleLogs, result.Pid);
                    byte[] pngBytes = Convert.FromBase64String(result.Base64Png);
                    anomalyReport = _anomalyDetector.AnalyzePng(pngBytes, effectiveConsoleLogs);
                    if (anomalyReport.HasAnomalies)
                    {
                        _logger.LogWarning("[CaptureWindowHandler] Visual anomalies detected in transient capture of '{Target}': {Summary}",
                            target, anomalyReport.Summary);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[CaptureWindowHandler] Anomaly detection failed: {Message}", ex.Message);
                }
            }

            bool isVulkan = result.CaptureMethodUsed == "vulkan_offscreen";
            var responsePayload = new
            {
                status = "success",
                target = result.Target,
                captureMethod = result.CaptureMethodUsed,
                zeroWindowExposure = isVulkan,
                windowTitle = result.WindowTitle,
                hwnd = result.Hwnd,
                pid = result.Pid,
                width = result.Width,
                height = result.Height,
                artifactPath = result.ArtifactPath,
                previewPath = result.PreviewPath,
                autoHdr = result.AutoHdr,
                userIdleSeconds = result.UserIdleSeconds,
                filePath = result.ArtifactPath,
                artifactUri = ToolCallResult.ToFileUriSafe(result.ArtifactPath),
                base64Png = string.Empty,
                base64Omitted = true,
                dwellMs = isVulkan ? 0 : dwellMs,
                restoredOffscreen = new { x = offscreenX, y = offscreenY },
                anomalyReport = anomalyReport != null ? new
                {
                    hasAnomalies = anomalyReport.HasAnomalies,
                    hasMissingTextures = anomalyReport.HasMissingTextures,
                    missingTexturePercentage = anomalyReport.MissingTexturePercentage,
                    isBlackScreen = anomalyReport.IsBlackScreen,
                    isInvertedViewport = anomalyReport.IsInvertedViewport,
                    hasTeapotFallback = anomalyReport.HasTeapotFallback,
                    teapotFallbackDetails = anomalyReport.TeapotFallbackDetails,
                    hasBlankWhitePlaceholder = anomalyReport.HasBlankWhitePlaceholder,
                    blankWhitePlaceholderCount = anomalyReport.BlankWhitePlaceholderCount,
                    summary = anomalyReport.Summary,
                    anomalies = anomalyReport.Anomalies.Select(a => new
                    {
                        type = a.Type,
                        severity = a.Severity,
                        description = a.Description,
                        confidence = a.Confidence,
                        details = a.Details
                    }),
                    analysisDurationMs = anomalyReport.AnalysisDurationMs
                } : null,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };

            string jsonText = JsonRpcSerializer.SerializeIndented(responsePayload);
            ToolCallResult callResult = ToolCallResult.Image(result.Base64Png, jsonText, artifactPath: result.ArtifactPath);
            if (callResult.Content.OfType<ResourceLinkContentItem>().FirstOrDefault() is { } spilledLink)
            {
                _logger.LogInformation("[CaptureWindowHandler] Frame spilled to disk resource link: {Uri}", spilledLink.Uri);
            }
            return callResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CaptureWindowHandler] Exception executing transient capture: {Message}", ex.Message);
            var errPayload = new
            {
                status = "error",
                target = target,
                windowTitle = string.Empty,
                hwnd = "0x0",
                pid = 0,
                width = 0,
                height = 0,
                artifactPath = string.Empty,
                base64Png = string.Empty,
                errorMessage = $"Error executing transient capture: {ex.Message}"
            };
            return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
        }
    }

    private string? ResolveEffectiveConsoleLogsForCapture(string? explicitConsoleLogs, int capturedPid)
    {
        if (!string.IsNullOrWhiteSpace(explicitConsoleLogs) || capturedPid <= 0)
        {
            return explicitConsoleLogs;
        }

        try
        {
            using System.Diagnostics.Process proc = System.Diagnostics.Process.GetProcessById(capturedPid);
            DateTime procStartUtc = proc.StartTime.ToUniversalTime();
            string root = Protocol.McpServer.FindProjectRoot();
            string candidateConsoleLog = Path.Combine(root, "games", "client", "userdataKONGOR", "logs", "console.log");
            if (!File.Exists(candidateConsoleLog))
            {
                return null;
            }

            DateTime lastWriteUtc = File.GetLastWriteTimeUtc(candidateConsoleLog);
            if (lastWriteUtc < procStartUtc.AddSeconds(-2))
            {
                return null;
            }

            using FileStream fs = new(candidateConsoleLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int maxTailBytes = 16 * 1024;
            if (fs.Length > maxTailBytes)
            {
                fs.Seek(-maxTailBytes, SeekOrigin.End);
            }

            using StreamReader reader = new(fs);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[CaptureWindowHandler] Skipped auto-tailing client console.log for PID {Pid}: {Message}", capturedPid, ex.Message);
            return null;
        }
    }
}

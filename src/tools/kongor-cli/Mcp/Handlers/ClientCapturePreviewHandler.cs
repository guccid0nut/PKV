// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_client_capture_preview' (alias: 'client_capture_preview').
/// Automates visual verification of hero preview models and avatars in the live game client.
/// Instructs the client over IPC to switch to a specific hero/alt preview stage, waits for
/// the 3D model and textures to render, and captures a PNG snapshot saved directly to the artifacts directory.
/// </summary>
public sealed class ClientCapturePreviewHandler
{
    private readonly IWindowCapturer _windowCapturer;
    private readonly IGameIpcBridge? _ipcBridge;
    private readonly string _repoRoot;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public ClientCapturePreviewHandler(
        IWindowCapturer? windowCapturer = null,
        IGameIpcBridge? ipcBridge = null,
        string? repoRoot = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _windowCapturer = windowCapturer ?? new WindowCapturer(timeProvider: _timeProvider);
        _ipcBridge = ipcBridge;
        _repoRoot = repoRoot ?? FindRepoRoot();
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string? hero = null;
        if ((arguments.TryGetProperty("hero", out JsonElement hEl) ||
             arguments.TryGetProperty("hero_key", out hEl)) &&
            !string.IsNullOrWhiteSpace(hEl.GetString()))
        {
            hero = hEl.GetString()!.Trim();
        }

        string alt = "default";
        if ((arguments.TryGetProperty("alt", out JsonElement aEl) ||
             arguments.TryGetProperty("avatar", out aEl)) &&
            !string.IsNullOrWhiteSpace(aEl.GetString()))
        {
            alt = aEl.GetString()!.Trim();
        }

        string target = "hon_x64";
        if (arguments.TryGetProperty("target", out JsonElement tEl) && !string.IsNullOrWhiteSpace(tEl.GetString()))
        {
            target = tEl.GetString()!.Trim();
        }

        int delayMs = 350;
        if ((arguments.TryGetProperty("delay_ms", out JsonElement dEl) ||
             arguments.TryGetProperty("delayMs", out dEl)) &&
            dEl.TryGetInt32(out int parsedDelay) && parsedDelay >= 0)
        {
            delayMs = Math.Clamp(parsedDelay, 0, 5000);
        }

        string? savePath = null;
        if ((arguments.TryGetProperty("save_path", out JsonElement spEl) ||
             arguments.TryGetProperty("savePath", out spEl)) &&
            !string.IsNullOrWhiteSpace(spEl.GetString()))
        {
            savePath = spEl.GetString()!.Trim();
        }

        // 1. If hero specified and IPC connected, command client to stage the preview model
        bool stageCommandSent = false;
        string? ipcResponse = null;
        if (!string.IsNullOrEmpty(hero) && _ipcBridge != null && _ipcBridge.IsConnected)
        {
            try
            {
                string stageScript = $@"
if SetHeroPreview then
    SetHeroPreview('{hero}', '{alt}')
elseif HeroSelect_SelectHero then
    HeroSelect_SelectHero('{hero}')
end
return 'PREVIEW_STAGED'
";
                ipcResponse = await _ipcBridge.ExecuteCommandAsync(stageScript.Trim(), 3000, cancellationToken).ConfigureAwait(false);
                stageCommandSent = true;
                _logger?.Invoke($"[ClientCapturePreviewHandler] Staged hero '{hero}' (alt '{alt}') via IPC: {ipcResponse}");
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[ClientCapturePreviewHandler] Staging command warning: {ex.Message}");
            }
        }

        // 2. Wait for rendering if we sent a staging command or delay requested
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        // 3. Determine output path
        string outputPath = savePath ?? Path.Combine(
            _repoRoot, "artifacts", "previews",
            $"{(!string.IsNullOrEmpty(hero) ? hero : "preview")}_{alt}_{_timeProvider.GetUtcNow():yyyyMMdd_HHmmss}.png");

        string? outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        // 4. Capture Window
        WindowCaptureResult captureResult;
        try
        {
            captureResult = await _windowCapturer.CaptureAsync(
                target: target,
                captureMethod: "auto",
                saveArtifact: true,
                customOutputPath: outputPath,
                stealFocus: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.Invoke($"[ClientCapturePreviewHandler] Window capture failed: {ex.Message}");
            return ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                target,
                error = $"Failed to capture window '{target}': {ex.Message}",
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }

        sw.Stop();
        double elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);

        if (!captureResult.Success)
        {
            return ToolCallResult.Json(new
            {
                status = "capture_failed",
                success = false,
                target,
                error = captureResult.ErrorMessage ?? "Game client window could not be captured. Ensure hon_x64.exe is running.",
                stageCommandSent,
                executionTimeMs = elapsedMs,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }

        _logger?.Invoke($"[ClientCapturePreviewHandler] Captured preview ({captureResult.Width}x{captureResult.Height}) to '{captureResult.ArtifactPath}' in {elapsedMs}ms.");

        return ToolCallResult.Json(new
        {
            status = "success",
            success = true,
            hero,
            alt,
            target = captureResult.Target,
            windowTitle = captureResult.WindowTitle,
            width = captureResult.Width,
            height = captureResult.Height,
            artifactPath = captureResult.ArtifactPath,
            captureMethodUsed = captureResult.CaptureMethodUsed,
            stageCommandSent,
            executionTimeMs = elapsedMs,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private static string FindRepoRoot()
    {
        string? current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, "games")) &&
                Directory.Exists(Path.Combine(current, "src")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}

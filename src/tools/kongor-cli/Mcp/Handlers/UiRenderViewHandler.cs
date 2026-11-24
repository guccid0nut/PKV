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
/// MCP Tool Handler for 'kongor_ui_render_view'.
/// Renders arbitrary Avalonia XAML views/UserControls offscreen to pixel-perfect PNG snapshots
/// using Avalonia Headless and SkiaSharp.
/// </summary>
public sealed class UiRenderViewHandler
{
    private readonly IHeadlessUiRenderer _renderer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UiRenderViewHandler> _logger;

    public UiRenderViewHandler(
        IHeadlessUiRenderer? renderer = null,
        TimeProvider? timeProvider = null,
        ILogger<UiRenderViewHandler>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<UiRenderViewHandler>.Instance;
        _renderer = renderer ?? new HeadlessUiRenderer(timeProvider: _timeProvider);
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("view_name", out JsonElement viewNameElem) ||
            string.IsNullOrWhiteSpace(viewNameElem.GetString()))
        {
            var missingPayload = new
            {
                status = "error",
                view_name = string.Empty,
                errorMessage = "Missing required parameter 'view_name'."
            };
            return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(missingPayload));
        }

        string viewName = viewNameElem.GetString()!.Trim();

        int width = 1280;
        if (arguments.TryGetProperty("width", out JsonElement widthElem) && widthElem.TryGetInt32(out int w))
        {
            width = w;
        }

        int height = 720;
        if (arguments.TryGetProperty("height", out JsonElement heightElem) && heightElem.TryGetInt32(out int h))
        {
            height = h;
        }

        string theme = "dark";
        if (arguments.TryGetProperty("theme", out JsonElement themeElem) && !string.IsNullOrWhiteSpace(themeElem.GetString()))
        {
            theme = themeElem.GetString()!.Trim();
        }

        bool mockData = true;
        if (arguments.TryGetProperty("mock_data", out JsonElement mockElem))
        {
            mockData = mockElem.GetBoolean();
        }

        bool saveArtifact = true;
        if (arguments.TryGetProperty("save_artifact", out JsonElement saveElem))
        {
            saveArtifact = saveElem.GetBoolean();
        }

        string? customOutputPath = CaptureWindowHandler.ResolveCustomOutputPath(arguments, out _);
        if (customOutputPath == null && arguments.TryGetProperty("custom_output_path", out JsonElement pathElem) && pathElem.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pathElem.GetString()))
        {
            customOutputPath = pathElem.GetString()!.Trim();
        }

        _logger.LogInformation("[UiRenderViewHandler] Processing headless UI render for '{ViewName}' ({Width}x{Height}, Theme: {Theme}, MockData: {Mock}, SaveArtifact: {Save})",
            viewName, width, height, theme, mockData, saveArtifact);

        await (McpProgressContext.Current?.ReportProgressAsync(0.2, 1.0, $"Preparing headless view '{viewName}'...", cancellationToken) ?? Task.CompletedTask);

        try
        {
            await (McpProgressContext.Current?.ReportProgressAsync(0.6, 1.0, $"Rendering Avalonia layout ({width}x{height}, theme: {theme})...", cancellationToken) ?? Task.CompletedTask);

            UiRenderResult result = await _renderer.RenderViewAsync(
                viewName,
                width,
                height,
                theme,
                mockData,
                saveArtifact,
                customOutputPath,
                cancellationToken
            ).ConfigureAwait(false);

            if (!result.Success)
            {
                _logger.LogWarning("[UiRenderViewHandler] Headless rendering failed for '{ViewName}': {Error}", viewName, result.ErrorMessage);
                var errPayload = new
                {
                    status = "error",
                    view_name = result.ViewName ?? viewName,
                    resolvedType = result.ResolvedType ?? string.Empty,
                    width = result.Width,
                    height = result.Height,
                    theme = result.Theme ?? theme,
                    artifactPath = result.ArtifactPath ?? string.Empty,
                    base64Png = result.Base64Png ?? string.Empty,
                    errorMessage = result.ErrorMessage ?? $"Rendering failed for view '{viewName}'."
                };
                return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
            }

            await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, $"Render completed successfully for '{viewName}'.", cancellationToken) ?? Task.CompletedTask);

            var responsePayload = new
            {
                status = "success",
                view_name = result.ViewName,
                resolvedType = result.ResolvedType,
                width = result.Width,
                height = result.Height,
                theme = result.Theme,
                artifactPath = result.ArtifactPath,
                artifactUri = ToolCallResult.ToFileUriSafe(result.ArtifactPath),
                base64Png = string.Empty,
                base64Omitted = true,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };

            string jsonText = JsonRpcSerializer.SerializeIndented(responsePayload);
            ToolCallResult callResult = ToolCallResult.Image(result.Base64Png, jsonText, artifactPath: result.ArtifactPath);
            if (callResult.Content.OfType<ResourceLinkContentItem>().FirstOrDefault() is { } spilledLink)
            {
                _logger.LogInformation("[UiRenderViewHandler] Rendered view '{ViewName}' spilled to disk resource link: {Uri}", viewName, spilledLink.Uri);
            }
            return callResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UiRenderViewHandler] Exception executing headless UI render: {Message}", ex.Message);
            var errPayload = new
            {
                status = "error",
                view_name = viewName,
                resolvedType = string.Empty,
                width = width,
                height = height,
                theme = theme,
                artifactPath = string.Empty,
                base64Png = string.Empty,
                errorMessage = $"Error executing kongor_ui_render_view: {ex.Message}"
            };
            return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
        }
    }
}

// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_asset_inspect' and 'kongor_asset_inspector'.
/// Performs binary inspection of .tga and .dds assets, detecting dimensions, origin orientation,
/// compression, alpha channels, and blank/solid textures ($white, $black, $invis).
/// </summary>
public sealed class AssetInspectHandler
{
    private readonly IAssetInspectService _assetInspectService;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logAction;

    public AssetInspectHandler(
        IAssetInspectService? assetInspectService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logAction = null)
    {
        _assetInspectService = assetInspectService ?? new AssetInspectService(logAction);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logAction = logAction;
    }

    private void Log(string message)
    {
        _logAction?.Invoke($"[AssetInspectHandler] {message}");
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("path", out JsonElement pathElem) || string.IsNullOrWhiteSpace(pathElem.GetString()))
        {
            return ToolCallResult.Error("Missing required parameter 'path'. Please specify an asset path (e.g. 'ui/fe2/mainmenu/logo_fb.tga' or 'core/brushes/standard/brush1.dds').");
        }

        string assetPath = pathElem.GetString()!.Trim();
        Log($"Handling asset inspection for '{assetPath}'...");

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            AssetInspectionResult result = await _assetInspectService.InspectAssetAsync(assetPath, null, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            var response = new
            {
                success = result.Success,
                path = result.Path,
                resolvedPath = result.ResolvedPath,
                vfsOrigin = result.VfsOrigin,
                resolvedArchive = result.ResolvedArchive,
                format = result.Format,
                dimensions = result.Dimensions != null ? new
                {
                    width = result.Dimensions.Width,
                    height = result.Dimensions.Height
                } : null,
                bitsPerPixel = result.BitsPerPixel,
                alphaDepth = result.AlphaDepth,
                hasAlpha = result.HasAlpha,
                imageType = result.ImageType,
                descriptorByte = result.DescriptorByte,
                origin = result.Origin,
                orientationWarning = result.OrientationWarning,
                solidColor = new
                {
                    isSolid = result.SolidColor.IsSolid,
                    type = result.SolidColor.Type,
                    confidence = result.SolidColor.Confidence
                },
                mipMapCount = result.MipMapCount,
                fourCC = result.FourCC,
                fileSizeBytes = result.FileSizeBytes,
                fileSizeKb = Math.Round((double)result.FileSizeBytes / 1024.0, 2),
                error = result.Error,
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };

            return ToolCallResult.Json(response);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"Error inspecting asset '{assetPath}': {ex.Message}");
            return ToolCallResult.Json(new
            {
                success = false,
                path = assetPath,
                error = ex.Message,
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }
    }
}

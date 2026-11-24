// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// Resolved VFS candidate file in a mount layer.
/// </summary>
public sealed class VfsLayerMatch
{
    [JsonPropertyName("layer")]
    public string Layer { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("physicalPath")]
    public string PhysicalPath { get; set; } = string.Empty;

    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("fileSizeBytes")]
    public long? FileSizeBytes { get; set; }

    [JsonPropertyName("lastModifiedUtc")]
    public string? LastModifiedUtc { get; set; }
}

/// <summary>
/// MCP Tool Handler for 'kongor_vfs_locate' (alias: 'vfs_locate').
/// Fast K2 Virtual File System disk locator & Voidtools Everything NTFS indexer bridge.
/// Resolves virtual paths against the active mount hierarchy with dynamic cache invalidation.
/// </summary>
public sealed class VfsLocateHandler
{
    private static readonly string[] PriorityLayers =
    [
        "override",
        "KONGOR/extensions",
        "KONGOR/updates",
        "KONGOR/configuration",
        "../shared",
        "ClientPackageFiles",
        "game",
        "base"
    ];

    private readonly ConcurrentDictionary<string, (DateTime CachedAt, VfsLayerMatch? WinningMatch)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly string _workspaceRoot;

    public VfsLocateHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        string? workspaceRoot = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[VfsLocate] {msg}"));
        _workspaceRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
    }

    public Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string? virtualPath = null;
        if ((arguments.TryGetProperty("virtual_path", out JsonElement vpEl) ||
             arguments.TryGetProperty("virtualPath", out vpEl) ||
             arguments.TryGetProperty("path", out vpEl) ||
             arguments.TryGetProperty("file_path", out vpEl) ||
             arguments.TryGetProperty("filePath", out vpEl) ||
             arguments.TryGetProperty("file", out vpEl) ||
             arguments.TryGetProperty("query", out vpEl) ||
             arguments.TryGetProperty("filename", out vpEl)) &&
            !string.IsNullOrWhiteSpace(vpEl.GetString()))
        {
            virtualPath = vpEl.GetString()!.Trim();
        }

        bool refreshCache = false;
        if ((arguments.TryGetProperty("refresh_cache", out JsonElement rcEl) ||
             arguments.TryGetProperty("refreshCache", out rcEl)) &&
            rcEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            refreshCache = rcEl.GetBoolean();
        }

        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            return Task.FromResult(ToolCallResult.Error("Missing required parameter 'virtual_path' (or 'path' / 'file_path' / 'query' / 'filename')."));
        }

        if (refreshCache)
        {
            _cache.Clear();
            _logger?.Invoke("VFS resolution cache cleared on request.");
        }

        string normalizedVirtualPath = VfsResolveHandler.NormalizeVirtualPath(virtualPath);
        string fileName = Path.GetFileName(normalizedVirtualPath);

        _logger?.Invoke($"Locating VFS asset '{normalizedVirtualPath}' (fileName: '{fileName}', refreshCache: {refreshCache})");

        // Resolve against priority layers
        List<VfsLayerMatch> layerMatches = [];
        string clientDir = ResolveClientDirectory();

        for (int i = 0; i < PriorityLayers.Length; i++)
        {
            string layer = PriorityLayers[i];
            string candidatePath = Path.Combine(clientDir, layer.Replace('/', Path.DirectorySeparatorChar), normalizedVirtualPath.Replace('/', Path.DirectorySeparatorChar));

            // Also check workspace root relative
            if (!File.Exists(candidatePath))
            {
                string altPath = Path.Combine(_workspaceRoot, layer.Replace('/', Path.DirectorySeparatorChar), normalizedVirtualPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(altPath))
                {
                    candidatePath = altPath;
                }
            }

            // Also check direct workspace relative for flat paths
            if (!File.Exists(candidatePath))
            {
                string flatPath = Path.Combine(_workspaceRoot, normalizedVirtualPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(flatPath))
                {
                    candidatePath = flatPath;
                }
            }

            bool exists = File.Exists(candidatePath);
            var match = new VfsLayerMatch
            {
                Layer = layer,
                Priority = i + 1,
                PhysicalPath = Path.GetFullPath(candidatePath),
                Exists = exists
            };

            if (exists)
            {
                try
                {
                    var fi = new FileInfo(candidatePath);
                    match.FileSizeBytes = fi.Length;
                    match.LastModifiedUtc = fi.LastWriteTimeUtc.ToString("o");
                }
                catch
                {
                    // Ignore file metadata read errors
                }
            }
            else if (VfsResolveHandler.TryFindInLayerArchives(
                clientDir,
                layer,
                normalizedVirtualPath,
                out string matchedArchiveName,
                out string matchedPhysicalPath,
                out VfsArchiveEntryMeta matchedEntry))
            {
                match.Exists = true;
                match.Layer = $"{layer}/{matchedArchiveName}";
                match.PhysicalPath = matchedPhysicalPath;
                match.FileSizeBytes = matchedEntry.UncompressedSize;
                match.LastModifiedUtc = matchedEntry.LastWriteTime.UtcDateTime.ToString("o");
            }

            layerMatches.Add(match);
        }

        var winningMatch = layerMatches.FirstOrDefault(m => m.Exists);
        bool resolved = winningMatch != null;

        // Formulate downstream handoff:
        // 1. If not found on disk, recommend Everything search across Windows NTFS MFT
        // 2. If found on disk, recommend Everything or asset inspection
        var handoffs = new List<ToolHandoff>();

        string searchPattern = string.IsNullOrEmpty(fileName) ? normalizedVirtualPath : fileName;
        handoffs.Add(ToolHandoff.Create(
            server: "everything",
            tool: "everything_search",
            reason: resolved
                ? $"Locate related versions, loose textures or archives for '{searchPattern}' across local NTFS drives"
                : $"Virtual asset '{normalizedVirtualPath}' not found in standard layers; query Windows NTFS MFT index",
            arguments: new { query = searchPattern },
            path: winningMatch?.PhysicalPath));

        if (resolved && winningMatch != null)
        {
            string ext = Path.GetExtension(winningMatch.PhysicalPath).ToLowerInvariant();
            if (ext is ".dds" or ".tga" or ".png")
            {
                handoffs.Add(ToolHandoff.Create(
                    server: "texconv",
                    tool: "dds_inspect_texture",
                    reason: $"Inspect texture metadata, mipmaps and format for '{Path.GetFileName(winningMatch.PhysicalPath)}'",
                    arguments: new { image_path = winningMatch.PhysicalPath },
                    path: winningMatch.PhysicalPath));
            }
            else if (ext is ".lua")
            {
                handoffs.Add(ToolHandoff.Create(
                    server: "luals",
                    tool: "lua_check_diagnostics",
                    reason: $"Verify Lua syntax and EmmyLua annotations for '{Path.GetFileName(winningMatch.PhysicalPath)}'",
                    arguments: new { file_path = winningMatch.PhysicalPath },
                    path: winningMatch.PhysicalPath));
            }
        }

        sw.Stop();

        var resultData = new
        {
            virtualPath = normalizedVirtualPath,
            fileName = fileName,
            resolved = resolved,
            winningLayer = winningMatch?.Layer,
            physicalPath = winningMatch?.PhysicalPath,
            fileSizeBytes = winningMatch?.FileSizeBytes,
            lastModifiedUtc = winningMatch?.LastModifiedUtc,
            existsOnDisk = resolved,
            mountStackLayers = layerMatches,
            handoffCount = handoffs.Count
        };

        return Task.FromResult(ExecutionTruthEnvelope.CreateJsonResult(
            status: resolved ? "success" : "not_found",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: null,
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: resultData,
            isError: false,
            handoffs: handoffs));
    }

    private string ResolveClientDirectory()
    {
        string[] candidates =
        [
            Path.Combine(_workspaceRoot, "games", "client"),
            Path.Combine(_workspaceRoot, "game"),
            _workspaceRoot
        ];

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir))
            {
                return dir;
            }
        }

        return _workspaceRoot;
    }
}

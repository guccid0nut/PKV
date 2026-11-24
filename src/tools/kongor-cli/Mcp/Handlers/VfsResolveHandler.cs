// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Services.Native;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// Metadata for a single entry indexed from a K2 .s2z Zip Central Directory.
/// </summary>
public readonly record struct VfsArchiveEntryMeta(
    string EntryPath,
    long UncompressedSize,
    long CompressedSize,
    uint Crc32,
    DateTimeOffset LastWriteTime);

/// <summary>
/// Cached Central Directory index for a single .s2z archive on disk.
/// Automatically invalidated when FileLength or LastWriteTimeUtcTicks changes.
/// </summary>
internal sealed class CachedArchiveIndex
{
    public required string ArchiveFullPath { get; init; }
    public required string ArchiveFileName { get; init; }
    public required long FileLength { get; init; }
    public required long LastWriteTimeUtcTicks { get; init; }
    public required Dictionary<string, VfsArchiveEntryMeta> Entries { get; init; }
}

/// <summary>
/// MCP Tool Handler for 'kongor_vfs_resolve'.
/// K2 Virtual File System conflict debugger: inspects the active priority stack
/// (override -> KONGOR/extensions -> KONGOR/updates -> KONGOR/configuration -> ../shared -> ClientPackageFiles -> game -> base)
/// including loose files and .s2z archives (resources99.s2z..resources0.s2z, textures.s2z)
/// for a given asset path and returns which physical file or archive entry wins and which are shadowed.
/// </summary>
public sealed class VfsResolveHandler
{
    /// <summary>
    /// Exact K2 runtime evaluation order (reverse of ClientModMountBuilder.CanonicalMountTiers).
    /// Confirmed via CFileManager::SetModStack and CArchive::Open telemetry in k2_x64.dll.
    /// </summary>
    public static readonly string[] PriorityLayers =
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

    private static readonly string[] IntermediateLayersAboveGameArchives =
    [
        "KONGOR/extensions",
        "KONGOR/updates",
        "KONGOR/configuration",
        "../shared",
        "ClientPackageFiles"
    ];

    private static readonly ConcurrentDictionary<string, CachedArchiveIndex> ArchiveIndexCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex ResourcesNumberRegex =
        new(@"^resources(\d+)\.s2z$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;
    private readonly string? _clientDirOverride;

    public VfsResolveHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        string? clientDirOverride = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[VfsResolve] {msg}"));
        _clientDirOverride = clientDirOverride;
    }

    public Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "resolve";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        string? pattern = null;
        if ((arguments.TryGetProperty("pattern", out JsonElement patEl) ||
             arguments.TryGetProperty("query", out patEl) ||
             arguments.TryGetProperty("filter", out patEl)) &&
            !string.IsNullOrWhiteSpace(patEl.GetString()))
        {
            pattern = patEl.GetString()!.Trim();
        }

        string? assetPath = null;
        if ((arguments.TryGetProperty("virtual_path", out JsonElement apEl) ||
             arguments.TryGetProperty("virtualPath", out apEl) ||
             arguments.TryGetProperty("asset_path", out apEl) ||
             arguments.TryGetProperty("assetPath", out apEl) ||
             arguments.TryGetProperty("path", out apEl)) &&
            !string.IsNullOrWhiteSpace(apEl.GetString()))
        {
            assetPath = apEl.GetString()!.Trim();
        }

        bool onlyWinners = false;
        if ((arguments.TryGetProperty("only_winners", out JsonElement owEl) ||
             arguments.TryGetProperty("onlyWinners", out owEl)) &&
            owEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            onlyWinners = owEl.GetBoolean();
        }

        bool includeArchives = true;
        if ((arguments.TryGetProperty("include_archives", out JsonElement iaEl) ||
             arguments.TryGetProperty("includeArchives", out iaEl)) &&
            iaEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            includeArchives = iaEl.GetBoolean();
        }

        int maxResults = 100;
        if ((arguments.TryGetProperty("max_results", out JsonElement mrEl) ||
             arguments.TryGetProperty("maxResults", out mrEl)) &&
            mrEl.TryGetInt32(out int parsedMax) && parsedMax > 0)
        {
            maxResults = parsedMax;
        }

        string clientDir = !string.IsNullOrWhiteSpace(_clientDirOverride)
            ? _clientDirOverride
            : ResolveClientDirectory();

        // 1. Verify native zlib runtime and VFS decompression readiness
        if (action is "verify_zlib" or "verify-zlib" or "zlib_status" or "zlib-status" or "zlib")
        {
            _logger("Verifying native zlib runtime and VFS archive decompression readiness");
            var result = ExecuteVerifyZlib(clientDir);
            return Task.FromResult(ToolCallResult.Json(result));
        }

        // 2. Shadow-collision audit across override/ -> intermediate layers & .s2z archives
        if (action is "audit_shadows" or "audit-shadows" or "shadow_audit" or "shadow-audit" or "audit")
        {
            string effectiveFilter = pattern ?? assetPath ?? "*";
            _logger($"Running VFS shadow-collision audit on override/ (Filter: '{effectiveFilter}', MaxResults: {maxResults})");
            var auditResult = ExecuteAuditShadows(clientDir, effectiveFilter, maxResults);
            return Task.FromResult(ToolCallResult.Json(auditResult));
        }

        // 3. Find/search across VFS mount stack OR pattern provided without assetPath
        if (action is "find" or "search" || (!string.IsNullOrWhiteSpace(pattern) && string.IsNullOrWhiteSpace(assetPath)))
        {
            pattern = string.IsNullOrWhiteSpace(pattern) ? (assetPath ?? "*") : pattern;
            _logger($"Searching VFS mount stack for pattern: '{pattern}' (OnlyWinners: {onlyWinners}, IncludeArchives: {includeArchives})");

            var searchResult = ExecuteVfsFind(clientDir, pattern, onlyWinners, maxResults, includeArchives);
            return Task.FromResult(ToolCallResult.Json(searchResult));
        }

        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return Task.FromResult(ToolCallResult.Error(
                "Missing required parameter 'virtualPath' (or 'pattern' for search, or action='audit_shadows'). " +
                "Fix: Pass {\"virtualPath\": \"/ui/scripts/store2.lua\"} or {\"action\": \"audit_shadows\"}."));
        }

        assetPath = NormalizeVirtualPath(assetPath);
        _logger($"Resolving VFS asset path: '{assetPath}' (IncludeArchives: {includeArchives})");

        Stopwatch sw = Stopwatch.StartNew();
        List<object> candidates = [];
        string? winningLayer = null;
        string? winningFullPath = null;
        string? winningSourceType = null;

        foreach (string layer in PriorityLayers)
        {
            string layerDir = Path.GetFullPath(Path.Combine(clientDir, layer));
            if (!Directory.Exists(layerDir))
            {
                continue;
            }

            // A. Check loose file in this mount layer first
            string candidateFile = Path.Combine(layerDir, assetPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidateFile))
            {
                FileInfo fi = new(candidateFile);
                bool isWinner = winningLayer == null;
                var candidateInfo = new
                {
                    layer,
                    mountLayer = layer,
                    sourceType = "loose_file",
                    fullPath = candidateFile,
                    sizeBytes = fi.Length,
                    lastModified = fi.LastWriteTimeUtc.ToString("o"),
                    isWinner
                };

                candidates.Add(candidateInfo);

                if (isWinner)
                {
                    winningLayer = layer;
                    winningFullPath = candidateFile;
                    winningSourceType = "loose_file";
                }
            }

            // B. Check mounted .s2z archives in this layer in exact K2 registration precedence
            if (includeArchives)
            {
                foreach (string archivePath in GetOrderedArchivesForLayer(layerDir))
                {
                    CachedArchiveIndex? archiveIndex = GetOrBuildArchiveIndex(archivePath, _logger);
                    if (archiveIndex == null)
                    {
                        continue;
                    }

                    if (TryFindEntryInArchive(archiveIndex, assetPath, out VfsArchiveEntryMeta entryMeta))
                    {
                        string archiveLayerLabel = $"{layer}/{archiveIndex.ArchiveFileName}";
                        string combinedPath = $"{archiveIndex.ArchiveFullPath}::{entryMeta.EntryPath}";
                        bool isWinner = winningLayer == null;

                        var archiveCandidate = new
                        {
                            layer = archiveLayerLabel,
                            mountLayer = layer,
                            sourceType = "archive",
                            archivePath = archiveIndex.ArchiveFullPath,
                            entryPath = entryMeta.EntryPath,
                            fullPath = combinedPath,
                            sizeBytes = entryMeta.UncompressedSize,
                            compressedSizeBytes = entryMeta.CompressedSize,
                            crc32 = $"0x{entryMeta.Crc32:X8}",
                            lastModified = entryMeta.LastWriteTime.UtcDateTime.ToString("o"),
                            isWinner
                        };

                        candidates.Add(archiveCandidate);

                        if (isWinner)
                        {
                            winningLayer = archiveLayerLabel;
                            winningFullPath = combinedPath;
                            winningSourceType = "archive";
                        }
                    }
                }
            }
        }

        sw.Stop();
        bool found = winningLayer != null;
        var response = new
        {
            status = "success",
            action = "resolve",
            virtualPath = assetPath,
            assetPath,
            found,
            winningFile = found ? new
            {
                layer = winningLayer,
                sourceType = winningSourceType,
                fullPath = winningFullPath,
                resolutionReason = $"Layer '{winningLayer}' ({winningSourceType}) is highest priority in K2 search hierarchy."
            } : null,
            isOverridden = string.Equals(winningLayer, "override", StringComparison.OrdinalIgnoreCase) ||
                           (winningLayer != null && winningLayer.StartsWith("override/", StringComparison.OrdinalIgnoreCase)),
            totalCandidatesFound = candidates.Count,
            candidates,
            shadowedCandidates = candidates.Skip(1).ToList(),
            prioritySearchOrder = PriorityLayers,
            mountHierarchy = PriorityLayers,
            resolutionLatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        return Task.FromResult(ToolCallResult.Json(response));
    }

    /// <summary>
    /// Normalizes K2 virtual paths (stripping leading '/', '\', '~', ':', '@', '#' and redundant 'override/' prefixes).
    /// </summary>
    public static string NormalizeVirtualPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        string clean = rawPath.Trim().Replace('\\', '/');
        while (clean.Length > 0 && clean[0] is '/' or '~' or ':' or '@' or '#')
        {
            clean = clean[1..];
        }

        while (clean.Contains("//", StringComparison.Ordinal))
        {
            clean = clean.Replace("//", "/", StringComparison.Ordinal);
        }

        if (clean.StartsWith("games/client/override/", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["games/client/override/".Length..];
        }
        else if (clean.StartsWith("override/", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["override/".Length..];
        }
        else if (clean.StartsWith("games/client/ClientPackageFiles/", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["games/client/ClientPackageFiles/".Length..];
        }
        else if (clean.StartsWith("ClientPackageFiles/", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["ClientPackageFiles/".Length..];
        }

        return clean.TrimStart('/');
    }

    /// <summary>
    /// Returns all active .s2z archives in a layer directory sorted in K2's exact evaluation order:
    /// 1. resources99.s2z -> resources98.s2z -> ... -> resources0.s2z (descending numeric suffix)
    /// 2. Any custom non-textures .s2z archives (descending alphabetical)
    /// 3. textures.s2z
    /// </summary>
    public static IReadOnlyList<string> GetOrderedArchivesForLayer(string layerDir)
    {
        if (!Directory.Exists(layerDir))
        {
            return [];
        }

        List<(int Bucket, int ResourceNum, string Name, string FullPath)> discovered = [];
        try
        {
            foreach (string file in Directory.EnumerateFiles(layerDir, "*.s2z", SearchOption.TopDirectoryOnly))
            {
                if (!file.EndsWith(".s2z", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string fileName = Path.GetFileName(file);
                Match m = ResourcesNumberRegex.Match(fileName);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int resNum))
                {
                    discovered.Add((0, resNum, fileName, file));
                }
                else if (string.Equals(fileName, "textures.s2z", StringComparison.OrdinalIgnoreCase))
                {
                    discovered.Add((2, -1, fileName, file));
                }
                else
                {
                    discovered.Add((1, 0, fileName, file));
                }
            }
        }
        catch
        {
            return [];
        }

        return discovered
            .OrderBy(x => x.Bucket)
            .ThenByDescending(x => x.ResourceNum)
            .ThenByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.FullPath)
            .ToList();
    }

    internal static CachedArchiveIndex? GetOrBuildArchiveIndex(string archiveFullPath, Action<string>? logger = null)
    {
        try
        {
            FileInfo fi = new(archiveFullPath);
            if (!fi.Exists || fi.Length < 22)
            {
                return null;
            }

            long length = fi.Length;
            long ticks = fi.LastWriteTimeUtc.Ticks;

            if (ArchiveIndexCache.TryGetValue(fi.FullName, out CachedArchiveIndex? existing) &&
                existing.FileLength == length &&
                existing.LastWriteTimeUtcTicks == ticks)
            {
                return existing;
            }

            using var fs = new FileStream(
                fi.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

            var entries = new Dictionary<string, VfsArchiveEntryMeta>(zip.Entries.Count, StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                string normKey = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrEmpty(normKey))
                {
                    continue;
                }

                entries[normKey] = new VfsArchiveEntryMeta(
                    EntryPath: normKey,
                    UncompressedSize: entry.Length,
                    CompressedSize: entry.CompressedLength,
                    Crc32: entry.Crc32,
                    LastWriteTime: entry.LastWriteTime);
            }

            var built = new CachedArchiveIndex
            {
                ArchiveFullPath = fi.FullName,
                ArchiveFileName = fi.Name,
                FileLength = length,
                LastWriteTimeUtcTicks = ticks,
                Entries = entries
            };

            ArchiveIndexCache[fi.FullName] = built;
            return built;
        }
        catch (Exception ex)
        {
            logger?.Invoke($"Warning indexing archive '{archiveFullPath}': {ex.Message}");
            return null;
        }
    }

    internal static bool TryFindEntryInArchive(
        CachedArchiveIndex archiveIndex,
        string normalizedVirtualPath,
        out VfsArchiveEntryMeta entryMeta)
    {
        if (archiveIndex.Entries.TryGetValue(normalizedVirtualPath, out entryMeta))
        {
            return true;
        }

        // K2 textures.s2z stores mipmap LOD 0 textures under '00000000/<path>.dds'
        // and transparently resolves '.tga' / '.png' references to '.dds'.
        string ext = Path.GetExtension(normalizedVirtualPath);
        if (ext.Equals(".tga", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".dds", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(archiveIndex.ArchiveFileName, "textures.s2z", StringComparison.OrdinalIgnoreCase))
        {
            string ddsPath = Path.ChangeExtension(normalizedVirtualPath, ".dds").Replace('\\', '/');
            if (archiveIndex.Entries.TryGetValue($"00000000/{ddsPath}", out entryMeta))
            {
                return true;
            }

            if (archiveIndex.Entries.TryGetValue($"00000000/{normalizedVirtualPath}", out entryMeta))
            {
                return true;
            }

            if (!string.Equals(ddsPath, normalizedVirtualPath, StringComparison.OrdinalIgnoreCase) &&
                archiveIndex.Entries.TryGetValue(ddsPath, out entryMeta))
            {
                return true;
            }
        }

        entryMeta = default;
        return false;
    }

    /// <summary>
    /// Helper used by VfsLocateHandler and VfsResolveHandler to find the highest-priority .s2z archive match within a layer.
    /// </summary>
    public static bool TryFindInLayerArchives(
        string clientDir,
        string layer,
        string normalizedVirtualPath,
        out string matchedArchiveName,
        out string matchedPhysicalPath,
        out VfsArchiveEntryMeta matchedEntry)
    {
        string layerDir = Path.GetFullPath(Path.Combine(clientDir, layer.Replace('/', Path.DirectorySeparatorChar)));
        foreach (string archivePath in GetOrderedArchivesForLayer(layerDir))
        {
            CachedArchiveIndex? idx = GetOrBuildArchiveIndex(archivePath);
            if (idx != null && TryFindEntryInArchive(idx, normalizedVirtualPath, out matchedEntry))
            {
                matchedArchiveName = idx.ArchiveFileName;
                matchedPhysicalPath = $"{idx.ArchiveFullPath}::{matchedEntry.EntryPath}";
                return true;
            }
        }

        matchedArchiveName = string.Empty;
        matchedPhysicalPath = string.Empty;
        matchedEntry = default;
        return false;
    }

    private object ExecuteAuditShadows(string clientDir, string pattern, int maxResults)
    {
        Stopwatch sw = Stopwatch.StartNew();
        string overrideDir = Path.GetFullPath(Path.Combine(clientDir, "override"));
        if (!Directory.Exists(overrideDir))
        {
            return new
            {
                status = "error",
                action = "audit_shadows",
                message = $"Override directory not found at '{overrideDir}'."
            };
        }

        // Pre-warm all .s2z archive indexes in parallel for sub-second full-tree auditing
        List<string> allArchivePaths = [];
        foreach (string layer in PriorityLayers)
        {
            string layerDir = Path.GetFullPath(Path.Combine(clientDir, layer));
            allArchivePaths.AddRange(GetOrderedArchivesForLayer(layerDir));
        }

        Parallel.ForEach(allArchivePaths, archPath => GetOrBuildArchiveIndex(archPath, _logger));

        // Pre-resolve intermediate layer directories and archive indexes once outside the per-file loop
        List<(string LayerName, string LayerDir)> intermediateDirs = [];
        List<(string LayerLabel, CachedArchiveIndex Index)> intermediateArchiveIndexes = [];
        foreach (string interLayer in IntermediateLayersAboveGameArchives)
        {
            string interDir = Path.GetFullPath(Path.Combine(clientDir, interLayer));
            if (Directory.Exists(interDir))
            {
                intermediateDirs.Add((interLayer, interDir));
                foreach (string interArch in GetOrderedArchivesForLayer(interDir))
                {
                    CachedArchiveIndex? idx = GetOrBuildArchiveIndex(interArch);
                    if (idx != null)
                    {
                        intermediateArchiveIndexes.Add(($"{interLayer}/{idx.ArchiveFileName}", idx));
                    }
                }
            }
        }

        string gameDirFullPath = Path.GetFullPath(Path.Combine(clientDir, "game"));
        bool gameDirExists = Directory.Exists(gameDirFullPath);

        List<(string LayerLabel, CachedArchiveIndex Index)> gameAndBaseArchiveIndexes = [];
        foreach (string archLayer in new[] { "game", "base" })
        {
            string archLayerDir = Path.GetFullPath(Path.Combine(clientDir, archLayer));
            foreach (string archPath in GetOrderedArchivesForLayer(archLayerDir))
            {
                CachedArchiveIndex? idx = GetOrBuildArchiveIndex(archPath);
                if (idx != null)
                {
                    gameAndBaseArchiveIndexes.Add(($"{archLayer}/{idx.ArchiveFileName}", idx));
                }
            }
        }

        bool matchAll = string.IsNullOrWhiteSpace(pattern) || pattern == "*";
        Regex? filterRegex = null;
        if (!matchAll)
        {
            string regexPattern = "^" + Regex.Escape(pattern.Replace('\\', '/').TrimStart('/')).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            filterRegex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        int totalActiveOverrideFiles = 0;
        int skippedBackupFiles = 0;
        int safeNewNetAssetsCount = 0;
        int safeArchiveUpdateCount = 0;
        int intermediateLayerCollisionRiskCount = 0;
        int multiArchiveShadowCount = 0;

        Dictionary<string, int> categoryBreakdown = new(StringComparer.OrdinalIgnoreCase);
        List<object> collisionRisks = [];
        List<object> archiveUpdatesSample = [];
        List<object> newNetAssetsSample = [];

        foreach (string file in Directory.EnumerateFiles(overrideDir, "*", SearchOption.AllDirectories))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Contains(".bak", StringComparison.OrdinalIgnoreCase))
            {
                skippedBackupFiles++;
                continue;
            }

            string relPath = Path.GetRelativePath(overrideDir, file).Replace('\\', '/');
            if (!matchAll && filterRegex != null &&
                !filterRegex.IsMatch(relPath) &&
                !filterRegex.IsMatch(fileName) &&
                !relPath.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            totalActiveOverrideFiles++;
            string topCategory = relPath.Contains('/') ? relPath[..relPath.IndexOf('/')] : "(root)";
            categoryBreakdown[topCategory] = categoryBreakdown.GetValueOrDefault(topCategory) + 1;

            string relPathOs = relPath.Replace('/', Path.DirectorySeparatorChar);

            // 1. Check intermediate loose/mod layers that sit ABOVE game/*.s2z in K2's mod stack
            List<string> intermediateHits = [];
            foreach (var (interLayer, interDir) in intermediateDirs)
            {
                string interCandidate = Path.Combine(interDir, relPathOs);
                if (File.Exists(interCandidate))
                {
                    intermediateHits.Add(interLayer);
                }
            }

            foreach (var (interArchLabel, idx) in intermediateArchiveIndexes)
            {
                if (TryFindEntryInArchive(idx, relPath, out _))
                {
                    intermediateHits.Add(interArchLabel);
                }
            }

            // Also check loose files directly inside games/client/game/ (which shadow game/*.s2z)
            if (gameDirExists)
            {
                string looseGameCandidate = Path.Combine(gameDirFullPath, relPathOs);
                if (File.Exists(looseGameCandidate))
                {
                    intermediateHits.Add("game (loose)");
                }
            }

            // 2. Check game/*.s2z and base/*.s2z archives via in-memory index
            List<string> archiveHits = [];
            foreach (var (archLabel, idx) in gameAndBaseArchiveIndexes)
            {
                if (TryFindEntryInArchive(idx, relPath, out _))
                {
                    archiveHits.Add(archLabel);
                }
            }

            if (archiveHits.Count > 1)
            {
                multiArchiveShadowCount++;
            }

            if (intermediateHits.Count > 0)
            {
                intermediateLayerCollisionRiskCount++;
                if (collisionRisks.Count < maxResults)
                {
                    collisionRisks.Add(new
                    {
                        virtualPath = relPath,
                        classification = "CollisionRisk_IntermediateLayerShadowsGameS2z",
                        intermediateLayers = intermediateHits,
                        shadowedArchives = archiveHits,
                        highestShadowedArchive = archiveHits.FirstOrDefault(),
                        remediation = $"File exists in [{string.Join(", ", intermediateHits)}] which has higher K2 mount priority than game/*.s2z. " +
                                      "Do NOT evict from override/ into game/*.s2z unless the intermediate layer copy is also updated or removed."
                    });
                }
            }
            else if (archiveHits.Count > 0)
            {
                safeArchiveUpdateCount++;
                if (archiveUpdatesSample.Count < maxResults)
                {
                    archiveUpdatesSample.Add(new
                    {
                        virtualPath = relPath,
                        classification = "SafeToInject_UpdatesExistingArchiveEntry",
                        highestShadowedArchive = archiveHits[0],
                        allShadowedArchives = archiveHits,
                        recommendedTargetArchive = archiveHits[0]
                    });
                }
            }
            else
            {
                safeNewNetAssetsCount++;
                if (newNetAssetsSample.Count < Math.Min(maxResults, 25))
                {
                    newNetAssetsSample.Add(new
                    {
                        virtualPath = relPath,
                        classification = "SafeToInject_NewNetAsset",
                        recommendedTargetArchive = relPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                            ? "game/textures.s2z"
                            : "game/resources99.s2z"
                    });
                }
            }
        }

        sw.Stop();
        return new
        {
            status = "success",
            action = "audit_shadows",
            pattern,
            overrideDirectory = overrideDir,
            totalOverrideFilesAudited = totalActiveOverrideFiles,
            skippedBackupFilesCount = skippedBackupFiles,
            summary = new
            {
                safeNewNetAssetsCount,
                safeArchiveUpdateCount,
                intermediateLayerCollisionRiskCount,
                multiArchiveShadowCount,
                safeForDirectGameS2zInjectionCount = safeNewNetAssetsCount + safeArchiveUpdateCount
            },
            categoryBreakdown,
            collisionRisks,
            archiveUpdatesSample,
            newNetAssetsSample,
            prioritySearchOrder = PriorityLayers,
            auditDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };
    }

    private object ExecuteVfsFind(string clientDir, string pattern, bool onlyWinners, int maxResults, bool includeArchives = true)
    {
        string regexPattern = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        Regex matchRegex = new(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Map relative virtual path -> list of (layer, sourceType, fullPath, sizeBytes, lastModified) in priority order
        Dictionary<string, List<(string Layer, string SourceType, string FullPath, long SizeBytes, string LastModified)>> groupedMatches =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (string layer in PriorityLayers)
        {
            string layerDir = Path.GetFullPath(Path.Combine(clientDir, layer));
            if (!Directory.Exists(layerDir)) continue;

            try
            {
                var files = Directory.EnumerateFiles(layerDir, "*", SearchOption.AllDirectories);
                foreach (string file in files)
                {
                    if (file.EndsWith(".s2z", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(file).Contains(".bak", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string relative = Path.GetRelativePath(layerDir, file).Replace('\\', '/');
                    string fileName = Path.GetFileName(file);

                    if (matchRegex.IsMatch(relative) || matchRegex.IsMatch(fileName) ||
                        relative.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!groupedMatches.TryGetValue(relative, out var list))
                        {
                            list = [];
                            groupedMatches[relative] = list;
                        }
                        var fi = new FileInfo(file);
                        list.Add((layer, "loose_file", file, fi.Length, fi.LastWriteTimeUtc.ToString("o")));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"Warning reading layer '{layer}': {ex.Message}");
            }

            if (includeArchives)
            {
                foreach (string archivePath in GetOrderedArchivesForLayer(layerDir))
                {
                    CachedArchiveIndex? idx = GetOrBuildArchiveIndex(archivePath, _logger);
                    if (idx == null) continue;

                    string archiveLayerLabel = $"{layer}/{idx.ArchiveFileName}";
                    foreach (var kvp in idx.Entries)
                    {
                        string entryPath = kvp.Key;
                        string fileName = Path.GetFileName(entryPath);
                        if (matchRegex.IsMatch(entryPath) || matchRegex.IsMatch(fileName) ||
                            entryPath.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!groupedMatches.TryGetValue(entryPath, out var list))
                            {
                                list = [];
                                groupedMatches[entryPath] = list;
                            }
                            list.Add((
                                archiveLayerLabel,
                                "archive",
                                $"{idx.ArchiveFullPath}::{entryPath}",
                                kvp.Value.UncompressedSize,
                                kvp.Value.LastWriteTime.UtcDateTime.ToString("o")));
                        }
                    }
                }
            }
        }

        int totalPhysicalMatches = groupedMatches.Values.Sum(v => v.Count);
        List<object> results = [];

        foreach (var kvp in groupedMatches.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Take(maxResults))
        {
            string virtualPath = kvp.Key;
            var candidates = kvp.Value;
            var winner = candidates[0];

            var winningObj = new
            {
                layer = winner.Layer,
                sourceType = winner.SourceType,
                fullPath = winner.FullPath,
                sizeBytes = winner.SizeBytes,
                lastModified = winner.LastModified
            };

            var shadowedList = candidates.Skip(1).Select(c => new
            {
                layer = c.Layer,
                sourceType = c.SourceType,
                fullPath = c.FullPath,
                sizeBytes = c.SizeBytes,
                lastModified = c.LastModified,
                shadowedBy = winner.Layer
            }).ToList();

            if (onlyWinners)
            {
                results.Add(new
                {
                    virtualPath,
                    winningFile = winningObj,
                    shadowedCount = shadowedList.Count
                });
            }
            else
            {
                results.Add(new
                {
                    virtualPath,
                    winningFile = winningObj,
                    shadowedCount = shadowedList.Count,
                    shadowedFiles = shadowedList
                });
            }
        }

        return new
        {
            status = "success",
            action = "find",
            pattern,
            onlyWinners,
            includeArchives,
            totalUniqueVirtualFiles = groupedMatches.Count,
            totalPhysicalMatches,
            returnedResultsCount = results.Count,
            results,
            priorityLayers = PriorityLayers,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };
    }

    private object ExecuteVerifyZlib(string clientDir)
    {
        string? zlibPath = NativeZlibBridge.TryDiscoverZlibPath(clientDir) ?? NativeZlibBridge.TryDiscoverZlibPath();
        ZlibVerificationResult? verification = null;
        if (zlibPath != null && File.Exists(zlibPath))
        {
            verification = NativeZlibBridge.VerifyLibrary(zlibPath);
        }

        // Check primary VFS archives in game/ and base/
        List<object> archives = [];
        string gameDir = Path.Combine(clientDir, "game");
        if (Directory.Exists(gameDir))
        {
            foreach (string s2zFile in Directory.EnumerateFiles(gameDir, "*.s2z", SearchOption.TopDirectoryOnly))
            {
                FileInfo fi = new(s2zFile);
                bool validHeader = false;
                try
                {
                    using var stream = File.OpenRead(s2zFile);
                    byte[] header = new byte[4];
                    int read = stream.Read(header, 0, 4);
                    // Standard zip / s2z header magic is PK\x03\x04
                    validHeader = read == 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04;
                }
                catch
                {
                    // Stream read exception fallback
                }

                archives.Add(new
                {
                    fileName = fi.Name,
                    fullPath = fi.FullName,
                    sizeBytes = fi.Length,
                    validZipHeader = validHeader
                });
            }
        }

        bool isSecure = verification != null && verification.Success;
        return new
        {
            status = "success",
            action = "verify_zlib",
            zlibDiscovered = zlibPath != null,
            zlibPath,
            verification = verification != null ? new
            {
                success = verification.Success,
                version = verification.Version,
                sampleCrc32 = $"0x{verification.SampleCrc32:X8}",
                crc32Passed = verification.Crc32Passed,
                roundTripPassed = verification.RoundTripPassed,
                securityStatus = verification.SecurityStatus,
                errorMessage = verification.ErrorMessage
            } : null,
            isVfsDecompressionReady = isSecure && archives.Count > 0,
            discoveredArchivesCount = archives.Count,
            archives,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };
    }

    private static string ResolveClientDirectory()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            string c1 = Path.Combine(dir.FullName, "games", "client");
            if (Directory.Exists(c1)) return c1;

            string c2 = Path.Combine(dir.FullName, "games", "client", "windows-x64");
            if (Directory.Exists(c2)) return c2;

            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx"))) break;
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}

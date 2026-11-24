// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Kongor.Core.Diagnostics;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Record representing where an asset physically resides in the K2 VFS hierarchy.
/// </summary>
public sealed record VfsEntryLocation(string LayerName, string PhysicalSource, bool IsArchive);

/// <summary>
/// High-performance in-memory index of all assets across K2 loose directories and .s2z archives.
/// Builds an indexed virtual path map in &lt; 500ms and resolves queries with multi-layer priority,
/// 00000000/ texture hash prefixing, and .dds/.tga/.png extension fallbacks.
/// </summary>
public sealed class VfsIndex
{
    private static readonly string[] TextureExtensionFallbacks = [".dds", ".tga", ".png"];

    // Normalized virtual path (lowercase, forward slashes) -> winning VfsEntryLocation
    private readonly Dictionary<string, VfsEntryLocation> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _logger;

    public int TotalIndexedEntries => _entries.Count;
    public int SkippedBombEntriesCount { get; private set; }

    public VfsIndex(Action<string>? logger = null)
    {
        _logger = logger ?? (msg => Console.Error.WriteLine($"[VfsIndex] {msg}"));
    }

    public static ArchiveDecompressionBudgetEvaluation CheckArchiveEntryBudget(
        string entryName,
        long compressedBytes,
        long uncompressedBytes,
        Action<string>? logger = null)
    {
        return BoundedDecompressionPaginator.ValidateArchiveEntryDecompressionBudget(
            entryName,
            compressedBytes,
            uncompressedBytes,
            logger: logger);
    }

    /// <summary>
    /// Builds the unified VFS index across all disk mounts and S2Z archives.
    /// </summary>
    public void BuildIndex(string clientDirectory, string repoRoot)
    {
        Stopwatch sw = Stopwatch.StartNew();
        _entries.Clear();
        SkippedBombEntriesCount = 0;

        // 1. Define Mount Hierarchy in Descending Priority Order
        // Highest priority layers must be indexed FIRST so that duplicates are shadowed.
        List<(string LayerName, string Path, bool IsArchive)> priorityLayers = [];

        // Loose directory mounts
        priorityLayers.Add(("override", Path.Combine(clientDirectory, "override"), false));
        priorityLayers.Add(("game", Path.Combine(clientDirectory, "game"), false));
        priorityLayers.Add(("ClientPackageFiles", Path.Combine(clientDirectory, "ClientPackageFiles"), false));
        priorityLayers.Add(("KONGOR/updates", Path.Combine(clientDirectory, "KONGOR", "updates"), false));
        priorityLayers.Add(("KONGOR/extensions", Path.Combine(clientDirectory, "KONGOR", "extensions"), false));
        string sharedOverride = Path.Combine(repoRoot, "games", "shared", "override");
        if (Directory.Exists(sharedOverride)) priorityLayers.Add(("shared/override", sharedOverride, false));
        string sharedBase = Path.Combine(repoRoot, "games", "shared");
        if (Directory.Exists(sharedBase)) priorityLayers.Add(("shared", sharedBase, false));
        string heroesBase = Path.Combine(repoRoot, "heroes");
        if (Directory.Exists(heroesBase)) priorityLayers.Add(("heroes", heroesBase, false));

        // S2Z Archives in games/client/game (resources99 -> 0, hero_content, game_content, reborn, textures)
        string gameDir = Path.Combine(clientDirectory, "game");
        if (Directory.Exists(gameDir))
        {
            // Numbered resources in descending order (resources99 down to resources0)
            for (int i = 99; i >= 0; i--)
            {
                string resPath = Path.Combine(gameDir, $"resources{i}.s2z");
                if (File.Exists(resPath))
                {
                    priorityLayers.Add(($"resources{i}.s2z", resPath, true));
                }
            }

            // Other content archives
            string[] contentArchives = ["hero_content.s2z", "game_content.s2z", "reborn_assets.s2z", "textures.s2z"];
            foreach (string arc in contentArchives)
            {
                string arcPath = Path.Combine(gameDir, arc);
                if (File.Exists(arcPath))
                {
                    priorityLayers.Add((arc, arcPath, true));
                }
            }
        }

        // Shared S2Z archives if present
        string sharedDir = Path.Combine(repoRoot, "games", "shared");
        if (Directory.Exists(sharedDir))
        {
            foreach (string arc in Directory.EnumerateFiles(sharedDir, "*.s2z", SearchOption.TopDirectoryOnly))
            {
                priorityLayers.Add((Path.GetFileName(arc), arc, true));
            }
        }

        _logger($"Scanning {priorityLayers.Count} VFS mount layers across loose directories and S2Z archives...");

        int looseCount = 0;
        int archiveCount = 0;

        foreach ((string layerName, string layerPath, bool isArchive) in priorityLayers)
        {
            if (isArchive)
            {
                if (!File.Exists(layerPath)) continue;
                try
                {
                    using FileStream fs = new(layerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using ZipArchive zip = new(fs, ZipArchiveMode.Read);
                    int layerEntries = 0;

                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        string entryName = entry.FullName.Replace('\\', '/').TrimStart('/');
                        if (string.IsNullOrWhiteSpace(entryName) || entryName.EndsWith('/')) continue;

                        ArchiveDecompressionBudgetEvaluation budgetEval =
                            BoundedDecompressionPaginator.ValidateArchiveEntryDecompressionBudget(
                                $"{layerName}#{entryName}",
                                entry.CompressedLength,
                                entry.Length,
                                logger: _logger);
                        if (!budgetEval.IsWithinBudget)
                        {
                            SkippedBombEntriesCount++;
                            continue;
                        }

                        RegisterPath(entryName, layerName, $"{layerName}#{entryName}", true);
                        layerEntries++;
                    }

                    archiveCount += layerEntries;
                    _logger($"  [S2Z] Layer '{layerName}' indexed {layerEntries:N0} entries.");
                }
                catch (Exception ex)
                {
                    _logger($"  [WARN] Failed to index archive '{layerName}': {ex.Message}");
                }
            }
            else
            {
                if (!Directory.Exists(layerPath)) continue;
                try
                {
                    int layerEntries = 0;
                    foreach (string file in Directory.EnumerateFiles(layerPath, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(layerPath, file).Replace('\\', '/').TrimStart('/');
                        if (string.IsNullOrWhiteSpace(rel)) continue;

                        RegisterPath(rel, layerName, file, false);
                        layerEntries++;
                    }

                    looseCount += layerEntries;
                    _logger($"  [Disk] Layer '{layerName}' indexed {layerEntries:N0} loose files.");
                }
                catch (Exception ex)
                {
                    _logger($"  [WARN] Failed to index directory '{layerName}': {ex.Message}");
                }
            }
        }

        sw.Stop();
        _logger($"[VfsIndex] Built complete VFS index with {_entries.Count:N0} unique paths ({looseCount:N0} loose, {archiveCount:N0} archive entries) in {sw.ElapsedMilliseconds}ms.");
    }

    private void RegisterPath(string relativePath, string layerName, string physicalSource, bool isArchive)
    {
        string norm = NormalizeVirtualPath(relativePath);
        VfsEntryLocation location = new(layerName, physicalSource, isArchive);

        // Direct registration (first writer wins to preserve layer priority)
        _entries.TryAdd(norm, location);
    }

    /// <summary>
    /// Resolves an asset reference against the active VFS index.
    /// Handles relative directory fallback, extension permutations (.dds, .tga, .png), and 00000000/ prefix.
    /// </summary>
    public bool TryResolve(
        string rawQueryPath,
        string? declaringDirectory,
        [NotNullWhen(true)] out VfsEntryLocation? winningLocation,
        [NotNullWhen(true)] out string? matchedKey,
        List<string>? probedPaths = null,
        bool allowTextureArchiveTiers = false)
    {
        winningLocation = null;
        matchedKey = null;

        if (string.IsNullOrWhiteSpace(rawQueryPath)) return false;

        string clean = rawQueryPath.Trim().Replace('\\', '/');
        if (clean.Contains('{'))
        {
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\{[^=}]+=([^}]*)\}", "$1");
        }
        if (string.IsNullOrWhiteSpace(clean))
        {
            winningLocation = new VfsEntryLocation("TemplateEmpty", string.Empty, false);
            matchedKey = string.Empty;
            return true;
        }

        bool isAbsolute = clean.StartsWith('/');
        string norm = clean.TrimStart('/');

        List<string> candidateBases = [];

        // 1. If relative and declaring directory provided, probe declaringDir/norm first
        if (!isAbsolute && !string.IsNullOrWhiteSpace(declaringDirectory))
        {
            string cleanDeclDir = declaringDirectory.Trim().Replace('\\', '/').Trim('/');
            if (!string.IsNullOrEmpty(cleanDeclDir))
            {
                candidateBases.Add(NormalizeVirtualPath($"{cleanDeclDir}/{norm}"));
            }
        }

        // 2. Probe from VFS root
        candidateBases.Add(NormalizeVirtualPath(norm));

        // 3. Fallback for frames/ shorthand to ui/frames/
        if (norm.StartsWith("frames/", StringComparison.OrdinalIgnoreCase))
        {
            candidateBases.Add($"ui/{norm}");
        }

        // Also if declaringDir starts with "heroes/" or "ui/", probe deeper standard locations
        if (!isAbsolute && !string.IsNullOrWhiteSpace(declaringDirectory))
        {
            string cleanDeclDir = declaringDirectory.Trim().Replace('\\', '/').Trim('/');
            string fn = Path.GetFileName(norm);
            if (!string.IsNullOrEmpty(cleanDeclDir) && !candidateBases.Contains($"{cleanDeclDir}/{fn}"))
            {
                candidateBases.Add($"{cleanDeclDir}/{fn}");
            }
        }

        foreach (string candBase in candidateBases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string ext = Path.GetExtension(candBase).ToLowerInvariant();
            string noExt = string.IsNullOrEmpty(ext) ? candBase : candBase[..^ext.Length];

            // 1. Direct candidate check
            probedPaths?.Add(candBase);
            if (_entries.TryGetValue(candBase, out winningLocation))
            {
                matchedKey = candBase;
                return true;
            }

            // 2. Extension fallbacks (.dds, .tga, .png)
            if (ext is ".tga" or ".dds" or ".png" || string.IsNullOrEmpty(ext))
            {
                foreach (string fallbackExt in TextureExtensionFallbacks)
                {
                    string extCand = noExt + fallbackExt;
                    probedPaths?.Add(extCand);
                    if (_entries.TryGetValue(extCand, out winningLocation))
                    {
                        matchedKey = extCand;
                        return true;
                    }
                }
            }

            // 3. Animated texture sequence fallback (e.g. liaht_ -> liaht_0000.dds, sl_ -> sl_top_left.dds)
            if (noExt.EndsWith('_'))
            {
                string[] animSuffixes = ["0000", "000", "00", "0", "1", "top_left", "bot_right"];
                foreach (string sfx in animSuffixes)
                {
                    foreach (string fallbackExt in TextureExtensionFallbacks)
                    {
                        string animCand = noExt + sfx + fallbackExt;
                        probedPaths?.Add(animCand);
                        if (_entries.TryGetValue(animCand, out winningLocation))
                        {
                            matchedKey = animCand;
                            return true;
                        }
                    }
                }
            }

            // 4. TextureArchive quality tiers for 3D engine material samplers
            if (allowTextureArchiveTiers)
            {
                string[] tiers = ["00000000", "00000001", "00000002", "00000009", "00000003"];
                foreach (string tier in tiers)
                {
                    string tierCand = $"{tier}/{candBase}";
                    probedPaths?.Add(tierCand);
                    if (_entries.TryGetValue(tierCand, out winningLocation))
                    {
                        matchedKey = tierCand;
                        return true;
                    }

                    if (ext is ".tga" or ".dds" or ".png" || string.IsNullOrEmpty(ext))
                    {
                        string tierNoExt = $"{tier}/{noExt}";
                        foreach (string fallbackExt in TextureExtensionFallbacks)
                        {
                            string extCand = tierNoExt + fallbackExt;
                            probedPaths?.Add(extCand);
                            if (_entries.TryGetValue(extCand, out winningLocation))
                            {
                                matchedKey = extCand;
                                return true;
                            }
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether an entry exists directly in the VFS index.
    /// </summary>
    public bool ContainsPath(string path)
    {
        string norm = NormalizeVirtualPath(path);
        return _entries.ContainsKey(norm);
    }

    private static string NormalizeVirtualPath(string path)
    {
        string p = path.Trim().Replace('\\', '/').TrimStart('/');
        if (p.Contains('.'))
        {
            string[] parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
            List<string> stack = [];
            foreach (string part in parts)
            {
                if (part == ".") continue;
                if (part == "..")
                {
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                }
                else
                {
                    stack.Add(part);
                }
            }
            return string.Join('/', stack);
        }
        return p;
    }
}

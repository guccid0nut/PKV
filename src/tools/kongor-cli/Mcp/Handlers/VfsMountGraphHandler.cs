// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_vfs_mount_graph'.
/// Visual inspection and resolution of virtual filesystem mount layers across .s2z archives,
/// mod directories, and loose files, reporting priority conflicts and shadowed assets.
/// </summary>
public sealed class VfsMountGraphHandler
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VfsMountGraphHandler> _logger;

    public VfsMountGraphHandler(
        TimeProvider? timeProvider = null,
        ILogger<VfsMountGraphHandler>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<VfsMountGraphHandler>.Instance;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "inspect_mounts";
        if ((arguments.TryGetProperty("action", out JsonElement aEl) ||
             arguments.TryGetProperty("command", out aEl)) &&
            !string.IsNullOrWhiteSpace(aEl.GetString()))
        {
            action = aEl.GetString()!.Trim().ToLowerInvariant();
        }

        try
        {
            return action switch
            {
                "inspect_mounts" or "inspect" or "list" or "mounts" or "status" => await HandleInspectMountsAsync(arguments, cancellationToken),
                "resolve_conflict" or "resolve" => await HandleResolveConflictAsync(arguments, cancellationToken),
                "detect_shadowing" or "shadowing" or "conflicts" => await HandleDetectShadowingAsync(arguments, cancellationToken),
                _ => ToolCallResult.Error($"Unknown vfs mount graph action '{action}'. Supported actions: inspect_mounts (or inspect), resolve_conflict, detect_shadowing.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VfsMountGraphHandler] Error executing vfs mount graph action '{Action}': {Message}", action, ex.Message);
            return ToolCallResult.Error($"VFS mount graph failure: {ex.Message}");
        }
    }

    private static bool IsSimulationRequested(JsonElement args)
    {
        if (Environment.GetEnvironmentVariable("KONGOR_MCP_ALLOW_LEGACY_MOCKS") == "1")
        {
            return true;
        }

        if (args.ValueKind == JsonValueKind.Object &&
            (args.TryGetProperty("allowSimulation", out JsonElement simEl) ||
             args.TryGetProperty("allow_simulation", out simEl)))
        {
            if (simEl.ValueKind == JsonValueKind.True) return true;
            if (simEl.ValueKind == JsonValueKind.String && bool.TryParse(simEl.GetString(), out bool b) && b) return true;
        }

        return false;
    }

    private static bool ShouldUseLiveVfsScan(JsonElement args, string baseDir)
    {
        if (!Directory.Exists(baseDir))
        {
            return false;
        }

        if (args.ValueKind == JsonValueKind.Object &&
            ((args.TryGetProperty("liveScan", out var lsEl) && lsEl.ValueKind == JsonValueKind.True) ||
             (args.TryGetProperty("live_scan", out var lsEl2) && lsEl2.ValueKind == JsonValueKind.True)))
        {
            return true;
        }

        return Directory.Exists(Path.Combine(baseDir, "override")) ||
               Directory.Exists(Path.Combine(baseDir, "mods")) ||
               Directory.Exists(Path.Combine(baseDir, "game")) ||
               Directory.Exists(Path.Combine(baseDir, "ClientPackageFiles"));
    }

    private static ToolCallResult BuildMissingDirectoryError(string action, string baseDir)
    {
        string msg = $"VFS client directory not found at '{baseDir}' for action '{action}'. Supply a valid 'game_dir' containing K2 VFS folders ('override/', 'mods/', 'game/', or 'ClientPackageFiles/').";
        return ToolCallResult.Json(new
        {
            status = "error",
            ok = false,
            action,
            errorCode = "directory_not_found",
            executionFidelity = ExecutionFidelity.StaticOfflineAnalysis.ToString(),
            baseDirectory = baseDir,
            error = msg,
            message = msg
        }, isError: true);
    }

    private Task<ToolCallResult> HandleInspectMountsAsync(JsonElement args, CancellationToken ct)
    {
        string baseDir = GetStringArg(args, "game_dir", "base_dir", "path") ?? Path.Combine(FindRepoRoot(AppContext.BaseDirectory), "games", "client");

        if (ShouldUseLiveVfsScan(args, baseDir))
        {
            List<object> liveLayers = [];
            List<string> topoParts = [];

            string overrideDir = Path.Combine(baseDir, "override");
            if (Directory.Exists(overrideDir))
            {
                int count = Directory.EnumerateFiles(overrideDir, "*", SearchOption.AllDirectories).Count();
                liveLayers.Add(new { priority = 1000, layer = "override/", type = "LooseOverride", readOnly = false, fileCount = count });
                topoParts.Add("override/ (P1000)");
            }

            string modsDir = Path.Combine(baseDir, "mods");
            if (Directory.Exists(modsDir))
            {
                foreach (string s2z in Directory.EnumerateFiles(modsDir, "*.s2z").OrderByDescending(f => f))
                {
                    int count = CountZipEntriesSafe(s2z);
                    string rel = $"mods/{Path.GetFileName(s2z)}";
                    liveLayers.Add(new { priority = 900, layer = rel, type = "ModArchive", readOnly = true, fileCount = count });
                    topoParts.Add($"{rel} (P900)");
                }
            }

            string gameSubDir = Path.Combine(baseDir, "game");
            if (Directory.Exists(gameSubDir))
            {
                int prio = 500;
                foreach (string s2z in Directory.EnumerateFiles(gameSubDir, "*.s2z").OrderByDescending(f => f))
                {
                    int count = CountZipEntriesSafe(s2z);
                    string rel = $"game/{Path.GetFileName(s2z)}";
                    string archiveType = rel.Contains("textures", StringComparison.OrdinalIgnoreCase)
                        ? "TextureArchive"
                        : (rel.Contains("core", StringComparison.OrdinalIgnoreCase) ? "CoreArchive" : "BaseArchive");
                    liveLayers.Add(new { priority = prio, layer = rel, type = archiveType, readOnly = true, fileCount = count });
                    topoParts.Add($"{rel} (P{prio})");
                    prio = Math.Max(10, prio - 50);
                }
            }

            string cpfDir = Path.Combine(baseDir, "ClientPackageFiles");
            if (Directory.Exists(cpfDir))
            {
                int count = Directory.EnumerateFiles(cpfDir, "*", SearchOption.AllDirectories).Count();
                liveLayers.Add(new { priority = 5, layer = "ClientPackageFiles/", type = "LoosePackageFallback", readOnly = false, fileCount = count });
                topoParts.Add("ClientPackageFiles/ (P5)");
            }

            return Task.FromResult(ToolCallResult.Json(new
            {
                status = "success",
                executionFidelity = ExecutionFidelity.StaticOfflineAnalysis.ToString(),
                action = "inspect_mounts",
                baseDirectory = baseDir,
                totalLayers = liveLayers.Count,
                timestamp = _timeProvider.GetUtcNow(),
                mountLayers = liveLayers,
                topology = string.Join(" -> ", topoParts)
            }));
        }

        if (!IsSimulationRequested(args))
        {
            return Task.FromResult(BuildMissingDirectoryError("inspect_mounts", baseDir));
        }

        var mountLayers = new[]
        {
            new { priority = 999, layer = "mods/resources999.s2z", type = "ModArchive", readOnly = true, fileCount = 42 },
            new { priority = 100, layer = "game/textures.s2z", type = "TextureArchive", readOnly = true, fileCount = 1520 },
            new { priority = 50, layer = "game/resources0.s2z", type = "BaseArchive", readOnly = true, fileCount = 8412 },
            new { priority = 0, layer = "game/core.s2z", type = "CoreArchive", readOnly = true, fileCount = 3105 }
        };

        return Task.FromResult(ToolCallResult.Json(new
        {
            status = "success",
            executionFidelity = ExecutionFidelity.SimulatedTestDouble.ToString(),
            action = "inspect_mounts",
            baseDirectory = baseDir,
            totalLayers = mountLayers.Length,
            timestamp = _timeProvider.GetUtcNow(),
            mountLayers,
            topology = "mods/resources999.s2z (P999) -> game/textures.s2z (P100) -> game/resources0.s2z (P50) -> game/core.s2z (P0)"
        }));
    }

    private Task<ToolCallResult> HandleResolveConflictAsync(JsonElement args, CancellationToken ct)
    {
        string resourcePath = (GetStringArg(args, "resource", "resource_path", "path") ?? "ui/main.interface").TrimStart('/', '\\').Replace('\\', '/');
        string baseDir = GetStringArg(args, "game_dir", "base_dir") ?? Path.Combine(FindRepoRoot(AppContext.BaseDirectory), "games", "client");

        if (ShouldUseLiveVfsScan(args, baseDir))
        {
            List<(string Archive, int Priority, string Hash)> found = [];

            string overrideFile = Path.Combine(baseDir, "override", resourcePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(overrideFile))
            {
                found.Add(("override/", 1000, ComputeShortSha256(File.ReadAllBytes(overrideFile))));
            }

            string modsSubDir = Path.Combine(baseDir, "mods");
            if (Directory.Exists(modsSubDir))
            {
                foreach (string s2z in Directory.EnumerateFiles(modsSubDir, "*.s2z").OrderByDescending(f => f))
                {
                    if (TryFindZipEntryHash(s2z, resourcePath, out string hash))
                    {
                        found.Add(($"mods/{Path.GetFileName(s2z)}", 900, hash));
                    }
                }
            }

            string gameSubDir = Path.Combine(baseDir, "game");
            if (Directory.Exists(gameSubDir))
            {
                int prio = 500;
                foreach (string s2z in Directory.EnumerateFiles(gameSubDir, "*.s2z").OrderByDescending(f => f))
                {
                    if (TryFindZipEntryHash(s2z, resourcePath, out string hash))
                    {
                        found.Add(($"game/{Path.GetFileName(s2z)}", prio, hash));
                    }
                    prio = Math.Max(10, prio - 50);
                }
            }

            string cpfFile = Path.Combine(baseDir, "ClientPackageFiles", resourcePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(cpfFile))
            {
                found.Add(("ClientPackageFiles/", 5, ComputeShortSha256(File.ReadAllBytes(cpfFile))));
            }

            string winnerArchive = found.Count > 0 ? found[0].Archive : "unresolved";
            int winnerPriority = found.Count > 0 ? found[0].Priority : -1;
            var candidates = found.Select((c, idx) => new
            {
                archive = c.Archive,
                priority = c.Priority,
                shadowed = idx > 0,
                hash = c.Hash
            }).ToArray();

            return Task.FromResult(ToolCallResult.Json(new
            {
                status = "success",
                executionFidelity = ExecutionFidelity.StaticOfflineAnalysis.ToString(),
                action = "resolve_conflict",
                resource = resourcePath,
                resolvedArchive = winnerArchive,
                resolvedPriority = winnerPriority,
                conflictCount = found.Count,
                candidates,
                resolutionRule = "Higher priority VFS mount layer overrides lower priority archive/folder."
            }));
        }

        if (!IsSimulationRequested(args))
        {
            return Task.FromResult(BuildMissingDirectoryError("resolve_conflict", baseDir));
        }

        return Task.FromResult(ToolCallResult.Json(new
        {
            status = "success",
            executionFidelity = ExecutionFidelity.SimulatedTestDouble.ToString(),
            action = "resolve_conflict",
            resource = resourcePath,
            resolvedArchive = "mods/resources999.s2z",
            resolvedPriority = 999,
            conflictCount = 2,
            candidates = new[]
            {
                new { archive = "mods/resources999.s2z", priority = 999, shadowed = false, hash = "sha256:abc12345" },
                new { archive = "game/resources0.s2z", priority = 50, shadowed = true, hash = "sha256:def67890" }
            },
            resolutionRule = "Higher priority archive overrides lower priority base archive."
        }));
    }

    private static readonly string[] BaseResourcesArchive = ["game/resources0.s2z"];

    private Task<ToolCallResult> HandleDetectShadowingAsync(JsonElement args, CancellationToken ct)
    {
        string baseDir = GetStringArg(args, "game_dir", "base_dir", "path") ?? Path.Combine(FindRepoRoot(AppContext.BaseDirectory), "games", "client");

        if (ShouldUseLiveVfsScan(args, baseDir))
        {
            string overrideDir = Path.Combine(baseDir, "override");
            string cpfDir = Path.Combine(baseDir, "ClientPackageFiles");
            List<object> realShadowed = [];
            int totalShadowedCount = 0;

            if (Directory.Exists(overrideDir))
            {
                foreach (string file in Directory.EnumerateFiles(overrideDir, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(overrideDir, file).Replace('\\', '/');
                    List<string> shadowedSources = [];
                    if (Directory.Exists(cpfDir) && File.Exists(Path.Combine(cpfDir, rel.Replace('/', Path.DirectorySeparatorChar))))
                    {
                        shadowedSources.Add("ClientPackageFiles/");
                    }
                    if (shadowedSources.Count > 0)
                    {
                        totalShadowedCount++;
                        if (realShadowed.Count < 25)
                        {
                            realShadowed.Add(new
                            {
                                resource = rel,
                                authoritativeSource = "override/",
                                shadowedSources = shadowedSources.ToArray(),
                                risk = "Low (Active Override Workspace)"
                            });
                        }
                    }
                }
            }

            return Task.FromResult(ToolCallResult.Json(new
            {
                status = "success",
                executionFidelity = ExecutionFidelity.StaticOfflineAnalysis.ToString(),
                action = "detect_shadowing",
                timestamp = _timeProvider.GetUtcNow(),
                shadowedFilesCount = totalShadowedCount,
                entriesReturned = realShadowed.Count,
                shadowedEntries = realShadowed
            }));
        }

        if (!IsSimulationRequested(args))
        {
            return Task.FromResult(BuildMissingDirectoryError("detect_shadowing", baseDir));
        }

        return Task.FromResult(ToolCallResult.Json(new
        {
            status = "success",
            executionFidelity = ExecutionFidelity.SimulatedTestDouble.ToString(),
            action = "detect_shadowing",
            timestamp = _timeProvider.GetUtcNow(),
            shadowedFilesCount = 3,
            shadowedEntries = new[]
            {
                new
                {
                    resource = "ui/main.interface",
                    authoritativeSource = "mods/resources999.s2z",
                    shadowedSources = BaseResourcesArchive,
                    risk = "Low (Intentional Mod Override)"
                },
                new
                {
                    resource = "heroes/pyromancer/hero.entity",
                    authoritativeSource = "mods/resources999.s2z",
                    shadowedSources = BaseResourcesArchive,
                    risk = "Medium (Balance Override)"
                },
                new
                {
                    resource = "textures/heroes/pyromancer/icon.tga",
                    authoritativeSource = "game/textures.s2z",
                    shadowedSources = BaseResourcesArchive,
                    risk = "Low (Texture Pack Override)"
                }
            }
        }));
    }

    private static int CountZipEntriesSafe(string zipPath)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            return archive.Entries.Count;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryFindZipEntryHash(string zipPath, string normalizedEntryPath, out string hash)
    {
        hash = string.Empty;
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.TrimStart('/', '\\').Replace('\\', '/').Equals(normalizedEntryPath, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return false;
            using Stream s = entry.Open();
            byte[] data = SHA256.HashData(s);
            hash = "sha256:" + Convert.ToHexStringLower(data)[..12];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeShortSha256(byte[] bytes)
    {
        byte[] data = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexStringLower(data)[..12];
    }

    private static string? GetStringArg(JsonElement args, params string[] names)
    {
        foreach (string name in names)
        {
            if (args.TryGetProperty(name, out JsonElement el) && !string.IsNullOrWhiteSpace(el.GetString()))
            {
                return el.GetString()!.Trim();
            }
        }
        return null;
    }

    private static string FindRepoRoot(string startingDir)
    {
        string? bundleRoot = Environment.GetEnvironmentVariable("KONGOR_BUNDLE_ROOT");
        if (!string.IsNullOrWhiteSpace(bundleRoot) && Directory.Exists(bundleRoot))
        {
            return bundleRoot;
        }

        foreach (string start in new[] { startingDir, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            DirectoryInfo? current = new(start);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "ProjectKongor.slnx")) ||
                    Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                    Directory.Exists(Path.Combine(current.FullName, "games", "client")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
        }

        return startingDir;
    }
}

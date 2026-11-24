// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_vfs_clean_cache' (alias: 'vfs_clean_cache').
/// Purges stale in-memory and on-disk K2 engine VFS caches, temporary file locks,
/// and user document caches to ensure modified models, textures, and scripts load immediately.
/// </summary>
public sealed class VfsCleanCacheHandler
{
    private readonly string _repoRoot;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public VfsCleanCacheHandler(
        string? repoRoot = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _repoRoot = repoRoot ?? FindRepoRoot();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "clean";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        bool purgeUserCache = true;
        if (arguments.TryGetProperty("purge_user_cache", out JsonElement pucEl) || arguments.TryGetProperty("purgeUserCache", out pucEl))
        {
            purgeUserCache = pucEl.GetBoolean();
        }

        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            var cacheDirs = GetTargetCacheDirectories(purgeUserCache);

            if (action == "status")
            {
                return Task.FromResult(HandleStatus(cacheDirs, sw));
            }

            return Task.FromResult(HandleClean(cacheDirs, sw));
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.Invoke($"[VfsCleanCache] Error executing '{action}': {ex.Message}");
            return Task.FromResult(ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                action,
                error = ex.Message,
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true));
        }
    }

    private ToolCallResult HandleClean(List<string> cacheDirs, Stopwatch sw)
    {
        int deletedFiles = 0;
        long freedBytes = 0;
        List<string> clearedPaths = [];
        List<string> errors = [];

        foreach (string dir in cacheDirs)
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                foreach (string f in files)
                {
                    try
                    {
                        var info = new FileInfo(f);
                        long len = info.Length;
                        File.Delete(f);
                        freedBytes += len;
                        deletedFiles++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to delete file '{f}': {ex.Message}");
                    }
                }
                clearedPaths.Add(dir);
                _logger?.Invoke($"[VfsCleanCache] Purged cache directory: '{dir}'");
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to scan cache directory '{dir}': {ex.Message}");
            }
        }

        sw.Stop();
        return ToolCallResult.Json(new
        {
            status = errors.Count > 0 ? "partial" : "success",
            success = true,
            action = "clean",
            deletedFiles,
            freedBytes,
            freedMb = Math.Round((double)freedBytes / (1024 * 1024), 2),
            clearedDirectories = clearedPaths,
            errors,
            executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult HandleStatus(List<string> cacheDirs, Stopwatch sw)
    {
        int totalFiles = 0;
        long totalBytes = 0;
        var existingDirs = new List<object>();

        foreach (string dir in cacheDirs)
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                long dirBytes = 0;
                foreach (string f in files)
                {
                    try { dirBytes += new FileInfo(f).Length; } catch { }
                }

                totalFiles += files.Length;
                totalBytes += dirBytes;

                existingDirs.Add(new
                {
                    path = dir,
                    fileCount = files.Length,
                    sizeBytes = dirBytes,
                    sizeMb = Math.Round((double)dirBytes / (1024 * 1024), 2)
                });
            }
            catch { }
        }

        sw.Stop();
        return ToolCallResult.Json(new
        {
            status = "success",
            action = "status",
            totalFiles,
            totalBytes,
            totalMb = Math.Round((double)totalBytes / (1024 * 1024), 2),
            directories = existingDirs,
            executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private List<string> GetTargetCacheDirectories(bool includeUserCache)
    {
        var dirs = new List<string>
        {
            Path.Combine(_repoRoot, "games", "client", "game", "cache"),
            Path.Combine(_repoRoot, "games", "client", "override", "cache"),
            Path.Combine(_repoRoot, "games", "server", "game", "cache"),
            Path.Combine(_repoRoot, "games", "server", "override", "cache"),
            Path.Combine(_repoRoot, "userdata", "cache"),
            Path.Combine(_repoRoot, "userdataKONGOR", "cache")
        };

        if (includeUserCache)
        {
            string userDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(userDocs))
            {
                dirs.Add(Path.Combine(userDocs, "Heroes of Newerth", "game", "cache"));
                dirs.Add(Path.Combine(userDocs, "Heroes of Newerth x64", "game", "cache"));
            }
        }

        return dirs;
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

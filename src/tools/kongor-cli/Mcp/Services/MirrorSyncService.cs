// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// High-performance multi-directory mirror synchronization service.
/// Eliminates asset desynchronization across client, server, and shared VFS override directories.
/// Employs SHA-256 content hashing to avoid redundant I/O, incorporates FileSystemWatcher with debouncing,
/// and maintains real-time synchronization metrics.
/// </summary>
public sealed class MirrorSyncService : IMirrorSyncService
{
    private readonly string _repoRoot;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _pendingChangedFiles = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private ITimer? _debounceTimer;
    private bool _isWatching;
    private int _debounceMs = 200;
    private int _totalSyncPasses;
    private string? _lastSyncTime;
    private MirrorSyncResultDto? _lastSyncResult;
    private bool _isDisposed;

    private static readonly HashSet<string> ServerMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dds", ".tga", ".png", ".jpg", ".jpeg", ".bmp", ".psd", ".tif", ".tiff",
        ".wav", ".mp3", ".ogg", ".fsb", ".fev", ".bank", ".sfx", ".flac",
        ".bik", ".webm", ".mp4", ".avi"
    };

    public static bool IsServerMedia(string relPath)
    {
        string norm = relPath.Replace('\\', '/');
        if (!norm.StartsWith("games/server/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string ext = Path.GetExtension(norm);
        if (ServerMediaExtensions.Contains(ext)) return true;
        if (norm.Contains("/sounds/") || norm.Contains("/music/")) return true;
        return false;
    }

    public bool IsWatching
    {
        get { lock (_stateLock) return _isWatching; }
    }

    public int DebounceMs => _debounceMs;
    public int TotalSyncPasses => Volatile.Read(ref _totalSyncPasses);
    public string WatchedPath => Path.Combine(_repoRoot, "games", "client", "override");

    public MirrorSyncService(
        string? repoRoot = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        bool startWatching = false)
    {
        _repoRoot = repoRoot ?? FindRepoRoot();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Trace.WriteLine($"[MirrorSyncService] {msg}"));

        if (startWatching)
        {
            StartWatching(_debounceMs);
        }
    }

    public MirrorStatusDto GetStatus()
    {
        lock (_stateLock)
        {
            return new MirrorStatusDto
            {
                Status = _isWatching ? "active" : "idle",
                IsWatching = _isWatching,
                WatchedPath = WatchedPath,
                DebounceMs = _debounceMs,
                TotalSyncPasses = _totalSyncPasses,
                LastSyncTime = _lastSyncTime,
                LastSyncResult = _lastSyncResult
            };
        }
    }

    public async Task<MirrorSyncResultDto> SyncAsync(MirrorSyncOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new MirrorSyncOptions();
        _logger($"[SYNC START] Beginning mirror synchronization sweep (Target: {options.Target}, DryRun: {options.DryRun})...");
        var copyPairs = CollectCopyPairs(options.Target);
        return await ExecuteSyncPairsAsync(copyPairs, options.Force, options.DryRun, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MirrorSyncResultDto> SyncSpecificFilesAsync(IEnumerable<string> relativeFiles, CancellationToken cancellationToken = default)
    {
        var copyPairs = CollectCopyPairs("tier5", relativeFiles);
        return await ExecuteSyncPairsAsync(copyPairs, force: false, dryRun: false, cancellationToken).ConfigureAwait(false);
    }

    public void StartAutoSyncWatcher(int debounceMs = 150)
    {
        StartWatching(debounceMs);
    }

    public void StopAutoSyncWatcher()
    {
        StopWatching();
    }

    private async Task<MirrorSyncResultDto> ExecuteSyncPairsAsync(
        List<(string Source, string Destination)> copyPairs,
        bool force,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        Stopwatch sw = Stopwatch.StartNew();
        var result = new MirrorSyncResultDto
        {
            Timestamp = _timeProvider.GetUtcNow().ToString("o"),
            TotalFilesEvaluated = copyPairs.Count
        };

        try
        {
            foreach (var (srcRel, destRel) in copyPairs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string srcFull = Path.GetFullPath(Path.Combine(_repoRoot, srcRel));
                string destFull = Path.GetFullPath(Path.Combine(_repoRoot, destRel));

                if (!File.Exists(srcFull))
                {
                    string warn = $"[WARN] Source file not found: {srcRel}";
                    _logger(warn);
                    result.Errors.Add(warn);
                    continue;
                }

                bool needsWrite = force || !File.Exists(destFull);

                if (!needsWrite)
                {
                    var srcFi = new FileInfo(srcFull);
                    var destFi = new FileInfo(destFull);
                    if (srcFi.Length != destFi.Length)
                    {
                        needsWrite = true;
                    }
                    else
                    {
                        string srcHash = ComputeFileHash(srcFull);
                        string destHash = ComputeFileHash(destFull);
                        if (!srcHash.Equals(destHash, StringComparison.OrdinalIgnoreCase))
                        {
                            needsWrite = true;
                        }
                    }
                }

                if (needsWrite)
                {
                    if (IsServerMedia(destRel))
                    {
                        result.TotalFilesUnchanged++;
                        _logger($"[SYNC SKIP SERVER MEDIA] {destRel}");
                        continue;
                    }

                    if (!dryRun)
                    {
                        string? parentDir = Path.GetDirectoryName(destFull);
                        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                        {
                            Directory.CreateDirectory(parentDir);
                        }

                        CopyFileShared(srcFull, destFull);
                        long length = new FileInfo(destFull).Length;
                        result.TotalBytesWritten += length;
                    }

                    result.TotalFilesCopied++;
                    result.SyncedFiles.Add($"{srcRel} -> {destRel}");
                    _logger($"[SYNC OK] {(dryRun ? "[DRY-RUN] " : "")}Copied {srcRel} -> {destRel}");
                }
                else
                {
                    result.TotalFilesUnchanged++;
                }
            }

            sw.Stop();
            result.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            result.Status = result.Errors.Count > 0 ? "partial" : "success";
            result.Success = true;

            Interlocked.Increment(ref _totalSyncPasses);
            lock (_stateLock)
            {
                _lastSyncTime = result.Timestamp;
                _lastSyncResult = result;
            }

            _logger($"[SYNC COMPLETE] Evaluated {result.TotalFilesEvaluated} files: {result.TotalFilesCopied} copied ({result.TotalBytesWritten:N0} bytes), {result.TotalFilesUnchanged} unchanged in {result.ElapsedMs}ms.");
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            result.Status = "error";
            result.Success = false;
            result.Errors.Add(ex.Message);
            _logger($"[SYNC ERROR] Synchronization failed: {ex.Message}");
            return result;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public Task<MirrorDiffResultDto> DiffAsync(string target = "all", CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        var result = new MirrorDiffResultDto
        {
            Timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        var copyPairs = CollectCopyPairs(target);
        result.TotalFilesEvaluated = copyPairs.Count;

        foreach (var (srcRel, destRel) in copyPairs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string srcFull = Path.GetFullPath(Path.Combine(_repoRoot, srcRel));
            string destFull = Path.GetFullPath(Path.Combine(_repoRoot, destRel));

            if (!File.Exists(srcFull))
            {
                continue;
            }

            if (IsServerMedia(destRel))
            {
                continue;
            }

            if (!File.Exists(destFull))
            {
                result.DriftingFiles.Add(new MirrorDiffItemDto
                {
                    SourcePath = srcRel,
                    DestinationPath = destRel,
                    Reason = "missing_destination",
                    SourceHash = ComputeFileHash(srcFull),
                    DestinationHash = string.Empty
                });
            }
            else
            {
                var srcFi = new FileInfo(srcFull);
                var destFi = new FileInfo(destFull);
                if (srcFi.Length != destFi.Length)
                {
                    result.DriftingFiles.Add(new MirrorDiffItemDto
                    {
                        SourcePath = srcRel,
                        DestinationPath = destRel,
                        Reason = "content_mismatch",
                        SourceHash = ComputeFileHash(srcFull),
                        DestinationHash = ComputeFileHash(destFull)
                    });
                }
                else
                {
                    string srcHash = ComputeFileHash(srcFull);
                    string destHash = ComputeFileHash(destFull);

                    if (!srcHash.Equals(destHash, StringComparison.OrdinalIgnoreCase))
                    {
                        result.DriftingFiles.Add(new MirrorDiffItemDto
                        {
                            SourcePath = srcRel,
                            DestinationPath = destRel,
                            Reason = "content_mismatch",
                            SourceHash = srcHash,
                            DestinationHash = destHash
                        });
                    }
                }
            }
        }

        sw.Stop();
        result.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
        result.DriftCount = result.DriftingFiles.Count;
        result.HasDrift = result.DriftCount > 0;
        result.Status = result.HasDrift ? "drift_detected" : "synced";

        return Task.FromResult(result);
    }

    public void StartWatching(int debounceMs = 200)
    {
        lock (_stateLock)
        {
            if (_isWatching) return;
            _debounceMs = debounceMs;

            string targetDir = WatchedPath;
            if (!Directory.Exists(targetDir))
            {
                _logger($"[WARN] Watch directory does not exist: {targetDir}");
                return;
            }

            try
            {
                _watcher = new FileSystemWatcher(targetDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    InternalBufferSize = 65536
                };

                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileChanged;
                _watcher.Deleted += OnFileChanged;
                _watcher.Renamed += OnFileRenamed;
                _watcher.Error += OnWatcherError;

                _debounceTimer = _timeProvider.CreateTimer(OnDebounceElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _watcher.EnableRaisingEvents = true;
                _isWatching = true;
                _logger($"[WATCHER] Active mirror file supervisor attached to: '{targetDir}' (Debounce: {_debounceMs}ms)");
            }
            catch (Exception ex)
            {
                _logger($"[ERROR] Failed to start FileSystemWatcher on '{targetDir}': {ex.Message}");
            }
        }
    }

    public void StopWatching()
    {
        lock (_stateLock)
        {
            if (!_isWatching) return;

            try
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Dispose();
                    _watcher = null;
                }

                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }
            catch (Exception ex)
            {
                _logger($"[WATCHER ERROR] Failed to cleanly stop FileSystemWatcher: {ex.Message}");
            }

            _isWatching = false;
            _logger("[WATCHER] Mirror file supervisor stopped.");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        RecordPendingChange(e.FullPath);
        TriggerDebounce();
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        RecordPendingChange(e.OldFullPath);
        RecordPendingChange(e.FullPath);
        TriggerDebounce();
    }

    private void RecordPendingChange(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        string targetDir = WatchedPath;
        if (fullPath.StartsWith(targetDir, StringComparison.OrdinalIgnoreCase))
        {
            string rel = Path.GetRelativePath(targetDir, fullPath).Replace('\\', '/');
            _pendingChangedFiles.TryAdd(rel, 0);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger($"[ERROR] FileSystemWatcher internal fault: {e.GetException().Message}");
    }

    private void TriggerDebounce()
    {
        lock (_stateLock)
        {
            if (_isWatching && _debounceTimer != null)
            {
                _debounceTimer.Change(TimeSpan.FromMilliseconds(_debounceMs), Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                List<string> pending;
                lock (_stateLock)
                {
                    pending = [.. _pendingChangedFiles.Keys];
                    _pendingChangedFiles.Clear();
                }

                _logger($"[AUTO-SYNC] File change detected in override folder ({pending.Count} file(s)). Executing automatic mirror sync...");
                if (pending.Count > 0)
                {
                    await SyncSpecificFilesAsync(pending, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await SyncAsync(new MirrorSyncOptions { Target = "all", DryRun = false }).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger($"[AUTO-SYNC ERROR] Auto-sync pass failed: {ex.Message}");
            }
        });
    }

    private static readonly string[] Tier5TargetRoots =
    [
        "games/client/ClientPackageFiles",
        "games/client/game",
        "games/shared",
        "games/server/override",
        "games/server/game"
    ];

    private void AddTier5CounterpartPairs(List<(string Source, string Destination)> pairs, HashSet<string> seen, IEnumerable<string>? specificRelativeFiles = null)
    {
        string overrideRoot = Path.Combine(_repoRoot, "games", "client", "override");
        if (!Directory.Exists(overrideRoot)) return;

        if (specificRelativeFiles != null)
        {
            foreach (string relFile in specificRelativeFiles)
            {
                string relNorm = relFile.Replace('\\', '/').TrimStart('/');
                if (relNorm.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
                    relNorm.StartsWith("ui/_probes/", StringComparison.OrdinalIgnoreCase) ||
                    relNorm.Contains("/_probes/"))
                {
                    continue;
                }

                string srcRel = $"games/client/override/{relNorm}";
                string srcFull = Path.Combine(_repoRoot, srcRel);
                if (!File.Exists(srcFull)) continue;

                foreach (string targetRoot in Tier5TargetRoots)
                {
                    string destRel = $"{targetRoot}/{relNorm}";
                    if (IsServerMedia(destRel)) continue;

                    string destFull = Path.Combine(_repoRoot, destRel);
                    if (File.Exists(destFull))
                    {
                        string key = $"{srcRel}->{destRel}";
                        if (seen.Add(key))
                        {
                            pairs.Add((srcRel, destRel));
                        }
                    }
                }
            }
            return;
        }

        int prefixLen = overrideRoot.Length + 1;
        var dirInfo = new DirectoryInfo(overrideRoot);

        foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            string rel = file.FullName.Length >= prefixLen ? file.FullName.Substring(prefixLen) : file.Name;
            string relNorm = rel.Replace('\\', '/');

            if (relNorm.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
                relNorm.StartsWith("ui/_probes/", StringComparison.OrdinalIgnoreCase) ||
                relNorm.Contains("/_probes/"))
            {
                continue;
            }

            string srcRel = $"games/client/override/{relNorm}";

            foreach (string targetRoot in Tier5TargetRoots)
            {
                string destRel = $"{targetRoot}/{relNorm}";
                if (IsServerMedia(destRel)) continue;

                string destFull = Path.Combine(_repoRoot, destRel);
                if (File.Exists(destFull))
                {
                    string key = $"{srcRel}->{destRel}";
                    if (seen.Add(key))
                    {
                        pairs.Add((srcRel, destRel));
                    }
                }
            }
        }
    }

    private List<(string Source, string Destination)> CollectCopyPairs(string target, IEnumerable<string>? specificRelativeFiles = null)
    {
        var rawPairs = new List<(string Source, string Destination)>();
        string t = target.Trim().ToLowerInvariant();

        bool includeCore = t is "all" or "scripts" or "catalogs" or "tier5";
        bool includeHeroes = t is "all" or "heroes" or "hero_definitions" or "tier5";

        if (includeCore)
        {
            AddCoreFilePairs(rawPairs, t);
        }

        if (includeHeroes)
        {
            AddModularHeroPairs(rawPairs);
        }

        var uniquePairs = new List<(string Source, string Destination)>(rawPairs.Count + 100);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in rawPairs)
        {
            string key = $"{p.Source}->{p.Destination}";
            if (seen.Add(key))
            {
                uniquePairs.Add(p);
            }
        }

        if (t is "all" or "tier5" || specificRelativeFiles != null)
        {
            AddTier5CounterpartPairs(uniquePairs, seen, specificRelativeFiles);
        }

        return uniquePairs;
    }

    private void AddCoreFilePairs(List<(string Source, string Destination)> pairs, string target)
    {
        // 1. hero_definitions_cache.lua
        if (target is "all" or "scripts" or "heroes" or "hero_definitions")
        {
            AddMultiDest(pairs, "games/client/override/ui/scripts/newui/hero_definitions_cache.lua", [
                "games/client/ClientPackageFiles/ui/scripts/newui/hero_definitions_cache.lua",
                "games/server/override/ui/scripts/newui/hero_definitions_cache.lua",
                "games/client/game/ui/scripts/newui/hero_definitions_cache.lua",
                "games/server/game/ui/scripts/newui/hero_definitions_cache.lua"
            ]);
        }

        // 2. kongor_logger.lua
        if (target is "all" or "scripts")
        {
            AddMultiDest(pairs, "games/client/override/ui/scripts/newui/kongor_logger.lua", [
                "games/client/ClientPackageFiles/ui/scripts/newui/kongor_logger.lua",
                "games/server/override/ui/scripts/newui/kongor_logger.lua",
                "games/client/game/ui/scripts/newui/kongor_logger.lua",
                "games/server/game/ui/scripts/newui/kongor_logger.lua"
            ]);
        }

        // 3. safecall.lua
        if (target is "all" or "scripts")
        {
            AddMultiDest(pairs, "games/client/override/ui/scripts/newui/safecall.lua", [
                "games/client/ClientPackageFiles/ui/scripts/newui/safecall.lua",
                "games/server/override/ui/scripts/newui/safecall.lua",
                "games/client/game/ui/scripts/newui/safecall.lua",
                "games/server/game/ui/scripts/newui/safecall.lua"
            ]);
        }

        // 4. items_catalog.lua
        if (target is "all" or "catalogs" or "scripts")
        {
            AddMultiDest(pairs, "games/client/override/ui/scripts/newui/definitions/items/items_catalog.lua", [
                "games/client/ClientPackageFiles/ui/scripts/newui/definitions/items/items_catalog.lua",
                "games/server/override/ui/scripts/newui/definitions/items/items_catalog.lua",
                "games/client/game/ui/scripts/newui/definitions/items/items_catalog.lua",
                "games/server/game/ui/scripts/newui/definitions/items/items_catalog.lua"
            ]);
        }

        // 5. cosmetics_catalog.lua
        if (target is "all" or "catalogs" or "scripts")
        {
            AddMultiDest(pairs, "games/client/override/ui/scripts/newui/definitions/cosmetics/cosmetics_catalog.lua", [
                "games/client/ClientPackageFiles/ui/scripts/newui/definitions/cosmetics/cosmetics_catalog.lua",
                "games/server/override/ui/scripts/newui/definitions/cosmetics/cosmetics_catalog.lua",
                "games/client/game/ui/scripts/newui/definitions/cosmetics/cosmetics_catalog.lua",
                "games/server/game/ui/scripts/newui/definitions/cosmetics/cosmetics_catalog.lua"
            ]);
        }

        // 6. avatar_index.lua
        if (target is "all" or "catalogs" or "heroes" or "hero_definitions")
        {
            AddMultiDest(pairs, "games/client/override/ui/scripts/newui/definitions/heroes/avatar_index.lua", [
                "games/client/ClientPackageFiles/ui/scripts/newui/definitions/heroes/avatar_index.lua",
                "games/server/override/ui/scripts/newui/definitions/heroes/avatar_index.lua",
                "games/client/game/ui/scripts/newui/definitions/heroes/avatar_index.lua",
                "games/server/game/ui/scripts/newui/definitions/heroes/avatar_index.lua"
            ]);
        }

        // 7. store.lua
        if (target is "all" or "scripts")
        {
            AddMultiDest(pairs, "games/client/override/ui/scripts/store.lua", [
                "games/client/ClientPackageFiles/ui/scripts/store.lua",
                "games/server/override/ui/scripts/store.lua",
                "games/client/game/ui/scripts/store.lua"
            ]);
        }

        // 8. learnatorium.lua
        if (target is "all" or "scripts")
        {
            AddMultiDest(pairs, "games/client/override/ui/scripts/learnatorium.lua", [
                "games/client/ClientPackageFiles/ui/scripts/learnatorium.lua",
                "games/server/override/ui/scripts/learnatorium.lua",
                "games/client/game/ui/scripts/learnatorium.lua"
            ]);
        }

        // 9. global_main.lua
        if (target is "all" or "scripts")
        {
            AddMultiDest(pairs, "games/client/override/ui/scripts/global_main.lua", [
                "games/client/ClientPackageFiles/ui/scripts/global_main.lua",
                "games/server/override/ui/scripts/global_main.lua",
                "games/client/game/ui/scripts/global_main.lua",
                "games/server/game/ui/scripts/global_main.lua"
            ]);
        }

        // 10. sysbar_controller.lua
        if (target is "all" or "scripts")
        {
            AddMultiDest(pairs, "games/client/override/ui/fe2/scripts/sysbar_controller.lua", [
                "games/client/ClientPackageFiles/ui/fe2/scripts/sysbar_controller.lua",
                "games/server/override/ui/fe2/scripts/sysbar_controller.lua",
                "games/server/game/ui/fe2/scripts/sysbar_controller.lua"
            ]);
        }

        // 11. system_bar.lua
        if (target is "all" or "scripts")
        {
            AddMultiDest(pairs, "games/client/override/ui/fe2/scripts/system_bar.lua", [
                "games/client/ClientPackageFiles/ui/fe2/scripts/system_bar.lua",
                "games/server/override/ui/fe2/scripts/system_bar.lua"
            ]);
        }

        // 12. heroes_preview.xml
        if (target is "all" or "catalogs")
        {
            AddMultiDest(pairs, "games/client/override/heroes_preview.xml", [
                "games/client/game/heroes_preview.xml",
                "games/server/override/heroes_preview.xml",
                "games/server/game/heroes_preview.xml"
            ]);
        }

        // 13. artillery hero.entity
        if (target is "all" or "heroes" or "hero_definitions")
        {
            AddMultiDest(pairs, "games/client/override/heroes/artillery/hero.entity", [
                "games/client/game/heroes/artillery/hero.entity",
                "games/server/override/heroes/artillery/hero.entity",
                "games/server/game/heroes/artillery/hero.entity"
            ]);
        }

        // 14. tao.lua (Together as One companion script)
        if (target is "all" or "scripts")
        {
            AddMultiDest(pairs, "games/client/override/ui/scripts/tao.lua", [
                "games/client/game/ui/scripts/tao.lua",
                "games/server/override/ui/scripts/tao.lua"
            ]);
        }

        // 15. Wave 3A modularized main_v2 companion scripts & lib_json.lua
        if (target is "all" or "scripts")
        {
            string[] mainV2CompanionScripts = [
                "main_v2_p2p.lua",
                "main_v2_botmatch.lua",
                "main_v2_walkthrough.lua",
                "main_v2_audio_news.lua",
                "main_v2_scene.lua",
                "lib_json.lua"
            ];
            foreach (string scriptName in mainV2CompanionScripts)
            {
                AddMultiDest(pairs, $"games/client/override/ui/scripts/{scriptName}", [
                    $"games/client/ClientPackageFiles/ui/scripts/{scriptName}",
                    $"games/client/game/ui/scripts/{scriptName}",
                    $"games/shared/ui/scripts/{scriptName}"
                ]);
            }
        }
    }

    private void AddModularHeroPairs(List<(string Source, string Destination)> pairs)
    {
        string heroDefSrc = Path.Combine(_repoRoot, "games", "client", "override", "ui", "scripts", "newui", "definitions", "heroes");
        if (!Directory.Exists(heroDefSrc)) return;

        string[] heroDests = [
            "games/client/ClientPackageFiles/ui/scripts/newui/definitions/heroes",
            "games/server/override/ui/scripts/newui/definitions/heroes",
            "games/client/game/ui/scripts/newui/definitions/heroes",
            "games/server/game/ui/scripts/newui/definitions/heroes"
        ];

        var heroFiles = Directory.GetFiles(heroDefSrc, "*.lua", SearchOption.TopDirectoryOnly);
        foreach (string hf in heroFiles)
        {
            string fileName = Path.GetFileName(hf);
            string relSrc = Path.Combine("games", "client", "override", "ui", "scripts", "newui", "definitions", "heroes", fileName).Replace('\\', '/');

            foreach (string destDir in heroDests)
            {
                string relDest = Path.Combine(destDir, fileName).Replace('\\', '/');
                pairs.Add((relSrc, relDest));
            }
        }
    }

    private static void AddMultiDest(List<(string Source, string Destination)> pairs, string source, string[] destinations)
    {
        foreach (string dest in destinations)
        {
            pairs.Add((source.Replace('\\', '/'), dest.Replace('\\', '/')));
        }
    }

    private static void CopyFileShared(string srcFull, string destFull)
    {
        using var srcStream = new FileStream(srcFull, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var destStream = new FileStream(destFull, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        srcStream.CopyTo(destStream);
        destStream.Flush();
    }

    private static string ComputeFileHash(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MirrorSyncService] ComputeFileHash failed on '{path}': {ex.Message}");
            return string.Empty;
        }
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

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        StopWatching();
        _syncLock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
